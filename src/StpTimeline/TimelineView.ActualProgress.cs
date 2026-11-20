using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// "Avance real" mode (template <c>actualProgressMode</c>): taller activity rows with a value box under each day
    /// that has one (<see cref="ITimelineProgressRow"/>), continuity arrows between the boxes of a row, a text box to
    /// type the value, a grip to drag a box (and the boxes after it) to another day, and the "Agregar / Eliminar avance
    /// real" menu entries. As for the other edits, the control only raises requests.
    /// </summary>
    public partial class TimelineView
    {
        /// <summary>Identifies <see cref="ShowActualProgress"/>.</summary>
        public static readonly DependencyProperty ShowActualProgressProperty = DependencyProperty.Register(
            nameof(ShowActualProgress), typeof(bool), typeof(TimelineView),
            new FrameworkPropertyMetadata(false, (d, e) => ((TimelineView)d).OnShowActualProgressChanged()));

        /// <summary>Identifies <see cref="ActualProgressRequested"/>.</summary>
        public static readonly RoutedEvent ActualProgressRequestedEvent = EventManager.RegisterRoutedEvent(
            nameof(ActualProgressRequested), RoutingStrategy.Bubble, typeof(EventHandler<TimelineProgressEventArgs>), typeof(TimelineView));

        /// <summary>Identifies <see cref="ActualProgressQuery"/>.</summary>
        public static readonly RoutedEvent ActualProgressQueryEvent = EventManager.RegisterRoutedEvent(
            nameof(ActualProgressQuery), RoutingStrategy.Bubble, typeof(EventHandler<TimelineProgressQueryEventArgs>), typeof(TimelineView));

        internal const string InvalidProgressReason = "Ingrese un número mayor o igual a 0.";

        private Canvas _progressLayer;
        private TextBox _progressEditor;
        private string _progressEditKey;
        private int _progressEditDay = -1;
        private bool _progressCommitting;

        /// <summary>Gets or sets a value indicating whether the "Avance real" boxes are shown (and rows are taller).</summary>
        public bool ShowActualProgress
        {
            get => (bool)GetValue(ShowActualProgressProperty);
            set => SetValue(ShowActualProgressProperty, value);
        }

        /// <summary>An "Avance real" edit: a typed value, a box drag, "Eliminar" or "Agregar avance real".</summary>
        public event EventHandler<TimelineProgressEventArgs> ActualProgressRequested
        {
            add => AddHandler(ActualProgressRequestedEvent, value);
            remove => RemoveHandler(ActualProgressRequestedEvent, value);
        }

        /// <summary>The control asks whether a box can be added on a day, or what a box drag would move.</summary>
        public event EventHandler<TimelineProgressQueryEventArgs> ActualProgressQuery
        {
            add => AddHandler(ActualProgressQueryEvent, value);
            remove => RemoveHandler(ActualProgressQueryEvent, value);
        }

        /// <summary>Gets a value indicating whether a value box is being typed in.</summary>
        public bool IsEditingActualProgress => _progressEditor != null && _progressEditor.Visibility == Visibility.Visible;

        /// <summary>The activity row height in the current mode.</summary>
        internal double ActivityRowHeight => ShowActualProgress ? TimelineLayout.ProgressRowHeight : TimelineLayout.ActivityRowHeight;

        /// <summary>Opens the value box of an activity's day for typing (keyboard, tests). Returns false when there is none.</summary>
        /// <param name="key">The row key.</param>
        /// <param name="day">The day index.</param>
        /// <returns>Opened.</returns>
        public bool BeginActualProgressEdit(string key, int day)
        {
            if (key == null || !_rowByKey.TryGetValue(key, out int row)) return false;
            int box = TimelineEditMath.ProgressBoxOnDay((_rows[row] as ITimelineProgressRow)?.ProgressBoxes, day);
            return box >= 0 && BeginProgressEdit(row, box);
        }

        /// <summary>Commits the text being typed (as Enter would).</summary>
        public void CommitActualProgressEdit() => CommitProgressEdit(false);

        internal void InitActualProgress(Grid root, int row, int column)
        {
            _progressLayer = new Canvas { ClipToBounds = true, Margin = new Thickness(0, TimelineLayout.HeaderHeight, 0, 0) };
            Grid.SetRow(_progressLayer, row);
            Grid.SetColumn(_progressLayer, column);
            root.Children.Add(_progressLayer);
        }

        private void OnShowActualProgressChanged()
        {
            CancelProgressEdit();
            Rebuild();
        }

        internal TimelineProgressBox? ProgressBox(int row, int box)
        {
            var boxes = row >= 0 && row < _rows.Count ? (_rows[row] as ITimelineProgressRow)?.ProgressBoxes : null;
            return boxes != null && box >= 0 && box < boxes.Count ? boxes[box] : (TimelineProgressBox?)null;
        }

        private DateTime DayDate(int day) => (Model?.StartDate.Date ?? DateTime.MinValue).AddDays(day);

        /// <summary>Raises <see cref="ActualProgressRequested"/>; a refusal flashes the day and shows the reason.</summary>
        internal TimelineProgressEventArgs RequestProgress(TimelineProgressAction action, int row, int day, int targetDay, double? value)
        {
            if (!IsEditable || !ShowActualProgress || row < 0 || row >= _rows.Count) return null;
            var args = new TimelineProgressEventArgs(this, action, _rows[row], day, DayDate(day), targetDay, DayDate(targetDay), value);
            RaiseEvent(args);
            if (args.RejectReason != null) ShowBlocked(row, new[] { action == TimelineProgressAction.Move ? targetDay : day }, args.RejectReason);
            return args;
        }

        internal bool QueryCanAddProgress(int row, int day)
        {
            if (!IsEditable || !ShowActualProgress || row < 0 || row >= _rows.Count) return false;
            var args = new TimelineProgressQueryEventArgs(this, false, _rows[row], day, DayDate(day), day, DayDate(day));
            RaiseEvent(args);
            return args.CanAdd;
        }

        internal IReadOnlyList<(int Origin, int Target)> QueryProgressPreview(int row, int originDay, int targetDay)
        {
            if (row < 0 || row >= _rows.Count) return Array.Empty<(int, int)>();
            var args = new TimelineProgressQueryEventArgs(this, true, _rows[row], originDay, DayDate(originDay), targetDay, DayDate(targetDay));
            RaiseEvent(args);
            return args.Moves;
        }

        /// <summary>
        /// Right-click in a progress row: "Eliminar avance real" on a box, "Agregar avance real" on a day the host
        /// allows. Returns the entry shown (tests), or null.
        /// </summary>
        internal string OpenProgressMenu(TimelineProgressHit hit, UIElement panel, Point at)
        {
            if (!IsEditable || !ShowActualProgress || hit.Row < 0 || hit.Day < 0) return null;
            string header;
            TimelineProgressAction action;
            if (hit.Box >= 0)
            {
                header = "Eliminar avance real";
                action = TimelineProgressAction.Remove;
            }
            else if (QueryCanAddProgress(hit.Row, hit.Day))
            {
                header = "Agregar avance real";
                action = TimelineProgressAction.Add;
            }
            else
            {
                return null;
            }

            HideHover();
            HideReason();
            if (!HasWindow) return header;
            string key = _rows[hit.Row].Key;
            int day = hit.Day;
            var menu = new ContextMenu { PlacementTarget = panel, Placement = PlacementMode.Relative, HorizontalOffset = at.X, VerticalOffset = at.Y };
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (key != null && _rowByKey.TryGetValue(key, out int row)) RequestProgress(action, row, day, day, null);
            }));
            menu.Items.Add(item);
            _menu = menu;
            menu.IsOpen = true;
            return header;
        }

        // ------------------------------------------------------------------ value editor

        internal bool BeginProgressEdit(int row, int box)
        {
            if (!IsEditable || !ShowActualProgress || _progressLayer == null) return false;
            var b = ProgressBox(row, box);
            if (b == null) return false;
            CommitProgressEdit(false);
            if (_progressEditor == null)
            {
                _progressEditor = new TextBox
                {
                    FontSize = TimelineTheme.ChartFontSize,
                    Padding = new Thickness(1, 0, 1, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = TimelineTheme.ProgressFocus,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                _progressEditor.PreviewKeyDown += OnProgressEditorKey;
                _progressEditor.LostKeyboardFocus += (s, e) => CommitProgressEdit(false);
                _progressLayer.Children.Add(_progressEditor);
            }

            _progressEditKey = _rows[row].Key;
            _progressEditDay = b.Value.Day;
            _progressEditor.Text = b.Value.Text;
            _progressEditor.Tag = b.Value.Text;
            _progressEditor.Visibility = Visibility.Visible;
            AutomationLabel(_progressEditor, $"Avance real de {_progressEditKey} el {DayDate(_progressEditDay):dd/MM}");
            PositionProgressEditor();
            _progressEditor.Focus();
            _progressEditor.SelectAll();
            return true;
        }

        private static void AutomationLabel(DependencyObject element, string text) => System.Windows.Automation.AutomationProperties.SetName(element, text);

        /// <summary>Keeps the text box over its box after scrolling or zooming; closes it when the box went away.</summary>
        internal void PositionProgressEditor()
        {
            if (!IsEditingActualProgress) return;
            if (Layout == null || _progressEditKey == null || !_rowByKey.TryGetValue(_progressEditKey, out int row)
                || TimelineEditMath.ProgressBoxOnDay((_rows[row] as ITimelineProgressRow)?.ProgressBoxes, _progressEditDay) < 0 || !ShowActualProgress)
            {
                CancelProgressEdit();
                return;
            }

            var g = TimelineEditMath.ProgressBoxGeometry(Layout.DayWidth);
            double x = Layout.DayToX(_progressEditDay) - OffsetX + g.ValueX;
            double y = Layout.RowTop(row) - OffsetY + g.Top;
            Canvas.SetLeft(_progressEditor, x - 1);
            Canvas.SetTop(_progressEditor, y - 1);
            _progressEditor.Width = Math.Max(24, g.ValueWidth + 2);
            _progressEditor.Height = g.Height + 2;
        }

        private void OnProgressEditorKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    CommitProgressEdit(false);
                    Focus();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    CancelProgressEdit();
                    Focus();
                    e.Handled = true;
                    break;
                case Key.Tab:
                    CommitProgressEdit(true, (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                    e.Handled = true;
                    break;
            }
        }

        private void CommitProgressEdit(bool moveNext, int direction = 1)
        {
            if (!IsEditingActualProgress || _progressCommitting) return;
            _progressCommitting = true;
            try
            {
                string key = _progressEditKey;
                int day = _progressEditDay;
                string text = _progressEditor.Text;
                bool changed = !string.Equals((text ?? string.Empty).Trim(), ((string)_progressEditor.Tag ?? string.Empty).Trim(), StringComparison.Ordinal);
                HideProgressEditor();
                if (key == null || !_rowByKey.TryGetValue(key, out int row)) return;
                if (changed)
                {
                    if (!TimelineEditMath.TryParseProgressValue(text, out double? value)) ShowBlocked(row, new[] { day }, InvalidProgressReason);
                    else RequestProgress(TimelineProgressAction.Edit, row, day, day, value);
                }

                if (moveNext && key != null && _rowByKey.TryGetValue(key, out row))
                {
                    // The model was rebuilt by the host: find the next box again.
                    var boxes = (_rows[row] as ITimelineProgressRow)?.ProgressBoxes;
                    int index = TimelineEditMath.ProgressBoxOnDay(boxes, day);
                    if (index >= 0 && index + direction >= 0 && index + direction < boxes.Count)
                    {
                        int next = index + direction;
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => BeginActualProgressEdit(key, boxes[next].Day)));
                    }
                    else
                    {
                        Focus();
                    }
                }
            }
            finally
            {
                _progressCommitting = false;
            }
        }

        internal void CancelProgressEdit()
        {
            if (!IsEditingActualProgress) return;
            HideProgressEditor();
        }

        private void HideProgressEditor()
        {
            _progressEditor.Visibility = Visibility.Collapsed;
            _progressEditKey = null;
            _progressEditDay = -1;
        }
    }
}
