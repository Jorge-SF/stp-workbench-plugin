using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using Maptek.Vulcan.Stp.Timeline.Rendering;

namespace Maptek.Vulcan.Stp.Timeline
{
    /// <summary>
    /// Planner timeline (Gantt) as in docs/mockups/planner_layout.html: a fixed left grid and a day chart that share
    /// one vertical offset, with horizontal scroll and zoom (day width) on the chart.
    /// <para>
    /// Rendering: WPF DrawingVisual layers, redrawn for the visible rows x visible days only, so the cost of a frame
    /// depends on the viewport, not on the model (2,000 activities x 90 days draws ~40 rows). Redraws are coalesced
    /// into one per dispatcher Render pass.
    /// </para>
    /// Input: mouse wheel scrolls, Shift+wheel scrolls horizontally, Ctrl+wheel zooms around the pointer,
    /// Up/Down move the selection. Feed it an <see cref="ITimelineModel"/> through <see cref="Model"/>.
    /// </summary>
    public class TimelineView : UserControl
    {
        private readonly TimelineGridPanel _grid;
        private readonly TimelineChartPanel _chart;
        private readonly ScrollBar _vbar, _hbar;
        private readonly ColumnDefinition _gridColumn;
        private readonly Dictionary<string, int> _rowByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        private IReadOnlyList<ITimelineRow> _rows = Array.Empty<ITimelineRow>();
        private int[] _firstDay = Array.Empty<int>(), _lastDay = Array.Empty<int>();
        private TimelineLayers _dirty;
        private bool _redrawQueued, _syncingBars;
        private double _zoomAnchor = double.NaN;

        public TimelineView()
        {
            Focusable = true;
            FocusVisualStyle = null;
            Background = TimelineTheme.Panel;
            SnapsToDevicePixels = true;

            _grid = new TimelineGridPanel(this);
            _chart = new TimelineChartPanel(this);
            _vbar = new ScrollBar { Orientation = Orientation.Vertical, Margin = new Thickness(0, TimelineLayout.HeaderHeight, 0, 0), SmallChange = TimelineLayout.ActivityRowHeight };
            _hbar = new ScrollBar { Orientation = Orientation.Horizontal };
            _vbar.ValueChanged += (s, e) => { if (!_syncingBars) SetVerticalOffset(e.NewValue); };
            _hbar.ValueChanged += (s, e) => { if (!_syncingBars) SetHorizontalOffset(e.NewValue); };

            var root = new Grid();
            _gridColumn = new ColumnDefinition { Width = new GridLength(GridWidth) };
            root.ColumnDefinitions.Add(_gridColumn);
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Add(root, _grid, 0, 0);
            var divider = new Border { Background = TimelineTheme.Divider.Brush };
            Add(root, divider, 0, 1);
            Grid.SetRowSpan(divider, 2);
            Add(root, _chart, 0, 2);
            Add(root, _vbar, 0, 3);
            Add(root, _hbar, 1, 2);
            Add(root, new Border { Background = TimelineTheme.HeaderBack }, 1, 0);
            Content = root;

            Loaded += (s, e) =>
            {
                Text.Reset(VisualTreeHelper.GetDpi(this).PixelsPerDip);
                RequestRedraw(TimelineLayers.All);
            };
        }

        private static void Add(Grid root, UIElement child, int row, int column)
        {
            Grid.SetRow(child, row);
            Grid.SetColumn(child, column);
            root.Children.Add(child);
        }

        #region Dependency properties

        public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
            nameof(Model), typeof(ITimelineModel), typeof(TimelineView),
            new PropertyMetadata(null, (d, e) => ((TimelineView)d).OnModelChanged((ITimelineModel)e.OldValue, (ITimelineModel)e.NewValue)));

        /// <summary>What to draw. The control listens to <see cref="ITimelineModel.Changed"/>.</summary>
        public ITimelineModel Model
        {
            get => (ITimelineModel)GetValue(ModelProperty);
            set => SetValue(ModelProperty, value);
        }

        public static readonly DependencyProperty DayWidthProperty = DependencyProperty.Register(
            nameof(DayWidth), typeof(double), typeof(TimelineView),
            new FrameworkPropertyMetadata(TimelineLayout.DefaultDayWidth, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((TimelineView)d).OnDayWidthChanged((double)e.OldValue, (double)e.NewValue),
                (d, v) => TimelineLayout.CoerceDayWidth((double)v)));

        /// <summary>Zoom: width of one day column in device-independent pixels (8..240, default 44).</summary>
        public double DayWidth
        {
            get => (double)GetValue(DayWidthProperty);
            set => SetValue(DayWidthProperty, value);
        }

        public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
            nameof(SelectedKey), typeof(string), typeof(TimelineView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((TimelineView)d).OnSelectedKeyChanged()));

        /// <summary>Key of the selected activity row (highlighted in grid and chart), or null.</summary>
        public string SelectedKey
        {
            get => (string)GetValue(SelectedKeyProperty);
            set => SetValue(SelectedKeyProperty, value);
        }

        public static readonly DependencyProperty ShowLinksProperty = DependencyProperty.Register(
            nameof(ShowLinks), typeof(bool), typeof(TimelineView),
            new PropertyMetadata(true, (d, e) => ((TimelineView)d).RequestRedraw(TimelineLayers.Links)));

        /// <summary>"Precedencias" toggle.</summary>
        public bool ShowLinks
        {
            get => (bool)GetValue(ShowLinksProperty);
            set => SetValue(ShowLinksProperty, value);
        }

        public static readonly DependencyProperty ShowCompletedProperty = DependencyProperty.Register(
            nameof(ShowCompleted), typeof(bool), typeof(TimelineView),
            new PropertyMetadata(true, (d, e) => ((TimelineView)d).RequestRedraw(TimelineLayers.Blocks | TimelineLayers.Front)));

        /// <summary>"Avance completado" toggle: hatched completed blocks and the "completado hasta" line.</summary>
        public bool ShowCompleted
        {
            get => (bool)GetValue(ShowCompletedProperty);
            set => SetValue(ShowCompletedProperty, value);
        }

        public static readonly DependencyProperty GridWidthProperty = DependencyProperty.Register(
            nameof(GridWidth), typeof(double), typeof(TimelineView),
            new PropertyMetadata(440.0, (d, e) => ((TimelineView)d)._gridColumn.Width = new GridLength((double)e.NewValue)));

        /// <summary>Width of the left grid (mockup: 440).</summary>
        public double GridWidth
        {
            get => (double)GetValue(GridWidthProperty);
            set => SetValue(GridWidthProperty, value);
        }

        #endregion

        #region Events

        public static readonly RoutedEvent BlockClickEvent = EventManager.RegisterRoutedEvent(
            nameof(BlockClick), RoutingStrategy.Bubble, typeof(TimelineBlockEventHandler), typeof(TimelineView));

        public static readonly RoutedEvent BlockDragStartedEvent = EventManager.RegisterRoutedEvent(
            nameof(BlockDragStarted), RoutingStrategy.Bubble, typeof(TimelineBlockEventHandler), typeof(TimelineView));

        /// <summary>A block was pressed and released without moving.</summary>
        public event TimelineBlockEventHandler BlockClick
        {
            add => AddHandler(BlockClickEvent, value);
            remove => RemoveHandler(BlockClickEvent, value);
        }

        /// <summary>The mouse moved past the system drag threshold with a block pressed. Mouse capture stays on the
        /// chart until the button is released.</summary>
        public event TimelineBlockEventHandler BlockDragStarted
        {
            add => AddHandler(BlockDragStartedEvent, value);
            remove => RemoveHandler(BlockDragStartedEvent, value);
        }

        /// <summary><see cref="SelectedKey"/> changed (by input or by code).</summary>
        public event EventHandler SelectedRowChanged;

        /// <summary>A redraw finished (see <see cref="LastRenderMilliseconds"/>).</summary>
        public event EventHandler Rendered;

        #endregion

        #region State read by the panels

        internal TextCache Text { get; } = new TextCache();
        internal TimelineChartPanel ChartPanel => _chart;
        internal TimelineLayout Layout { get; private set; }
        internal IReadOnlyList<ITimelineRow> Rows => _rows;

        /// <summary>Index of the selected row in the model, or -1.</summary>
        public int SelectedRowIndex { get; private set; } = -1;

        /// <summary>Vertical scroll, shared by grid and chart (content pixels below the header).</summary>
        public double OffsetY { get; private set; }

        /// <summary>Horizontal scroll of the chart.</summary>
        public double OffsetX { get; private set; }

        public double LastRenderMilliseconds { get; private set; }
        public int DrawnRowCount => _grid.LastDrawnRows;
        public int DrawnBlockCount => _chart.LastDrawnBlocks;
        public int DrawnLinkCount => _chart.LastDrawnLinks;

        internal bool TryGetSpan(string key, out int row, out int firstDay, out int lastDay)
        {
            firstDay = lastDay = 0;
            if (key == null || !_rowByKey.TryGetValue(key, out row) || _firstDay[row] > _lastDay[row]) { row = -1; return false; }
            firstDay = _firstDay[row];
            lastDay = _lastDay[row];
            return true;
        }

        private double ViewportWidth => Math.Max(0, _chart.ActualWidth);
        private double ViewportHeight => Math.Max(0, _chart.ActualHeight - TimelineLayout.HeaderHeight);
        private double ExtentWidth => Layout == null || Model == null ? 0 : Layout.ExtentWidth(Model.DayCount);
        private double ExtentHeight => Layout?.ExtentHeight ?? 0;

        #endregion

        #region Model

        private void OnModelChanged(ITimelineModel oldModel, ITimelineModel newModel)
        {
            if (oldModel != null) oldModel.Changed -= OnModelContentChanged;
            if (newModel != null) newModel.Changed += OnModelContentChanged;
            OffsetX = OffsetY = 0;
            Rebuild();
        }

        private void OnModelContentChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Rebuild)); return; }
            Rebuild();
        }

        /// <summary>Re-read the rows (keys, day spans, row tops). O(rows + blocks).</summary>
        private void Rebuild()
        {
            var model = Model;
            _rows = model?.Rows ?? Array.Empty<ITimelineRow>();
            _rowByKey.Clear();
            int n = _rows.Count;
            var kinds = new TimelineRowKind[n];
            _firstDay = new int[n];
            _lastDay = new int[n];
            for (int i = 0; i < n; i++)
            {
                var row = _rows[i];
                kinds[i] = row.Kind;
                if (row.Key != null && !_rowByKey.ContainsKey(row.Key)) _rowByKey[row.Key] = i;
                var blocks = row.Blocks;
                if (row.Kind == TimelineRowKind.Activity && blocks != null && blocks.Count > 0)
                {
                    _firstDay[i] = blocks[0].Day;
                    _lastDay[i] = blocks[blocks.Count - 1].Day;
                }
                else
                {
                    _firstDay[i] = 1;
                    _lastDay[i] = 0; // empty span
                }
            }
            Layout = model == null ? null : new TimelineLayout(kinds, DayWidth);
            SelectedRowIndex = LookUpSelected();
            ClampOffsets();
            RequestRedraw(TimelineLayers.All);
        }

        private int LookUpSelected()
        {
            string key = SelectedKey;
            return key != null && _rowByKey.TryGetValue(key, out int i) && _rows[i].Kind == TimelineRowKind.Activity ? i : -1;
        }

        #endregion

        #region Scroll, zoom, selection

        public void SetVerticalOffset(double offset)
        {
            double clamped = TimelineLayout.ClampOffset(offset, ExtentHeight, ViewportHeight);
            if (clamped == OffsetY) return;
            OffsetY = clamped;
            SyncScrollBars();
            RequestRedraw(TimelineLayers.All & ~TimelineLayers.Front);
        }

        public void SetHorizontalOffset(double offset)
        {
            double clamped = TimelineLayout.ClampOffset(offset, ExtentWidth, ViewportWidth);
            if (clamped == OffsetX) return;
            OffsetX = clamped;
            SyncScrollBars();
            RequestRedraw(TimelineLayers.All & ~TimelineLayers.Grid);
        }

        /// <summary>Zoom to <paramref name="dayWidth"/>, keeping the instant under viewport x <paramref name="anchorX"/> in place.</summary>
        public void ZoomAt(double dayWidth, double anchorX)
        {
            _zoomAnchor = anchorX;
            DayWidth = dayWidth;
            _zoomAnchor = double.NaN;
        }

        private void OnDayWidthChanged(double oldWidth, double newWidth)
        {
            if (Layout == null) return;
            double anchor = double.IsNaN(_zoomAnchor) ? ViewportWidth / 2 : _zoomAnchor;
            Layout.DayWidth = newWidth;
            OffsetX = TimelineLayout.ClampOffset(TimelineLayout.ZoomOffset(OffsetX, anchor, oldWidth, newWidth), ExtentWidth, ViewportWidth);
            SyncScrollBars();
            RequestRedraw(TimelineLayers.All & ~TimelineLayers.Grid);
        }

        /// <summary>Scroll vertically so the row with this key is visible.</summary>
        public void ScrollIntoView(string key)
        {
            if (Layout == null || key == null || !_rowByKey.TryGetValue(key, out int row)) return;
            SetVerticalOffset(Layout.ScrollIntoView(row, OffsetY, ViewportHeight));
        }

        private void OnSelectedKeyChanged()
        {
            SelectedRowIndex = LookUpSelected();
            RequestRedraw(TimelineLayers.Selection);
            SelectedRowChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void SelectRowFromInput(int row, Point position, IInputElement panel)
        {
            if (row < 0 || row >= _rows.Count || _rows[row].Kind != TimelineRowKind.Activity) return;
            SelectedKey = _rows[row].Key;
        }

        internal void RaiseBlockEvent(RoutedEvent routedEvent, TimelineHit hit, Point panelPoint, UIElement panel)
        {
            if (hit.Row < 0 || hit.Block < 0 || hit.Row >= _rows.Count) return;
            var row = _rows[hit.Row];
            var block = row.Blocks[hit.Block];
            var date = (Model?.StartDate.Date ?? DateTime.MinValue).AddDays(block.Day);
            var position = panel.TranslatePoint(panelPoint, this);
            RaiseEvent(new TimelineBlockEventArgs(routedEvent, this, row, hit.Row, block, hit.Block, date, position));
        }

        internal void OnViewportChanged()
        {
            ClampOffsets();
            RequestRedraw(TimelineLayers.All);
        }

        private void ClampOffsets()
        {
            OffsetX = TimelineLayout.ClampOffset(OffsetX, ExtentWidth, ViewportWidth);
            OffsetY = TimelineLayout.ClampOffset(OffsetY, ExtentHeight, ViewportHeight);
            SyncScrollBars();
        }

        private void SyncScrollBars()
        {
            _syncingBars = true;
            try
            {
                Sync(_vbar, ExtentHeight, ViewportHeight, OffsetY);
                Sync(_hbar, ExtentWidth, ViewportWidth, OffsetX);
                _hbar.SmallChange = DayWidth;
            }
            finally
            {
                _syncingBars = false;
            }
        }

        private static void Sync(ScrollBar bar, double extent, double viewport, double offset)
        {
            double max = Math.Max(0, extent - viewport);
            bar.Minimum = 0;
            bar.Maximum = max;
            bar.ViewportSize = Math.Max(1, viewport);
            bar.LargeChange = Math.Max(1, viewport * 0.9);
            bar.Value = Math.Min(offset, max);
            bar.IsEnabled = max > 0;
        }

        #endregion

        #region Input

        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            base.OnPreviewMouseWheel(e);
            double steps = e.Delta / 120.0;
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) != 0)
            {
                double x = e.GetPosition(_chart).X;
                double anchor = x >= 0 && x <= ViewportWidth ? x : 0;
                ZoomAt(DayWidth * Math.Pow(1.2, steps), anchor);
            }
            else if ((mods & ModifierKeys.Shift) != 0)
            {
                SetHorizontalOffset(OffsetX - steps * DayWidth * 2);
            }
            else
            {
                SetVerticalOffset(OffsetY - steps * 3 * TimelineLayout.ActivityRowHeight);
            }
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || (e.Key != Key.Up && e.Key != Key.Down)) return;
            int step = e.Key == Key.Down ? 1 : -1;
            int i = SelectedRowIndex < 0 ? (step > 0 ? -1 : _rows.Count) : SelectedRowIndex;
            for (i += step; i >= 0 && i < _rows.Count; i += step)
            {
                if (_rows[i].Kind != TimelineRowKind.Activity) continue;
                SelectedKey = _rows[i].Key;
                ScrollIntoView(_rows[i].Key);
                break;
            }
            e.Handled = true;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Text.Reset(newDpi.PixelsPerDip);
            RequestRedraw(TimelineLayers.All);
        }

        #endregion

        #region Redraw

        internal void RequestRedraw(TimelineLayers layers)
        {
            _dirty |= layers;
            if (_redrawQueued) return;
            _redrawQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderNow));
        }

        /// <summary>Redraw pending changes now instead of at the next Render pass (tests, benchmarks, printing).</summary>
        public void UpdateNow() => RenderNow();

        /// <summary>Redraw the dirty layers now (normally called once per Render pass).</summary>
        internal void RenderNow()
        {
            _redrawQueued = false;
            var dirty = _dirty;
            _dirty = TimelineLayers.None;
            if (dirty == TimelineLayers.None) return;
            var watch = Stopwatch.StartNew();
            if ((dirty & TimelineLayers.Grid) != 0) _grid.Redraw();
            _chart.Redraw(dirty);
            LastRenderMilliseconds = watch.Elapsed.TotalMilliseconds;
            Rendered?.Invoke(this, EventArgs.Empty);
        }

        #endregion
    }
}
