using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using T = Maptek.Vulcan.Stp.Timeline.Rendering.TimelineTheme;

namespace Maptek.Vulcan.Stp.Timeline
{
    /// <summary>
    /// Editing interactions (reference/web: the browser planner's drag, keyboard and segment context menu). The
    /// control never changes the model: it raises a request event (and runs <see cref="EditCommand"/>), the host
    /// asks the engine, then refreshes the model or rejects the request so the blocks flash red with the reason.
    /// </summary>
    public partial class TimelineView
    {
        /// <summary>planner outcome <c>horizon-range</c> (timeline_logic.js <c>humanPlannerOutcome</c>).</summary>
        public const string HorizonRangeReason = "El movimiento saldría del horizonte visible. Extienda el rango o reduzca el desplazamiento.";

        /// <summary>Stagger validation message of the browser planner.</summary>
        public const string StaggerInputReason = "Divisiones y días de desfase deben ser enteros mayores que cero.";

        private int _focusDay = -1;

        #region Properties and events

        public static readonly DependencyProperty IsEditableProperty = DependencyProperty.Register(
            nameof(IsEditable), typeof(bool), typeof(TimelineView),
            new PropertyMetadata(true, (d, e) => ((TimelineView)d).OnIsEditableChanged()));

        /// <summary>False while the host is busy or the plan can't be edited: no drags, edit menus or move keys
        /// (the browser's <c>busy()</c> / <c>interactionBlocked</c>). Selection, scrolling and tooltips still work.</summary>
        public bool IsEditable
        {
            get => (bool)GetValue(IsEditableProperty);
            set => SetValue(IsEditableProperty, value);
        }

        public static readonly DependencyProperty EditCommandProperty = DependencyProperty.Register(
            nameof(EditCommand), typeof(ICommand), typeof(TimelineView), new PropertyMetadata(null));

        /// <summary>
        /// Optional MVVM hook: run with the <see cref="TimelineEditEventArgs"/> of every request (move, defer, stagger,
        /// assign, exemption, undo, redo) after the routed event, unless a handler set Handled. The command may call
        /// <see cref="TimelineEditEventArgs.Reject"/>.
        /// </summary>
        public ICommand EditCommand
        {
            get => (ICommand)GetValue(EditCommandProperty);
            set => SetValue(EditCommandProperty, value);
        }

        public static readonly RoutedEvent MoveRequestedEvent = Register(nameof(MoveRequested));
        public static readonly RoutedEvent DeferRequestedEvent = Register(nameof(DeferRequested));
        public static readonly RoutedEvent StaggerRequestedEvent = Register(nameof(StaggerRequested));
        public static readonly RoutedEvent AssignResourceRequestedEvent = Register(nameof(AssignResourceRequested));
        public static readonly RoutedEvent CapacityExemptionRequestedEvent = Register(nameof(CapacityExemptionRequested));
        public static readonly RoutedEvent UndoRequestedEvent = Register(nameof(UndoRequested));
        public static readonly RoutedEvent RedoRequestedEvent = Register(nameof(RedoRequested));

        public static readonly RoutedEvent DragPreviewChangedEvent = EventManager.RegisterRoutedEvent(
            nameof(DragPreviewChanged), RoutingStrategy.Bubble, typeof(TimelineDragPreviewEventHandler), typeof(TimelineView));

        public static readonly RoutedEvent BlockMenuOpeningEvent = EventManager.RegisterRoutedEvent(
            nameof(BlockMenuOpening), RoutingStrategy.Bubble, typeof(TimelineMenuEventHandler), typeof(TimelineView));

        private static RoutedEvent Register(string name) =>
            EventManager.RegisterRoutedEvent(name, RoutingStrategy.Bubble, typeof(TimelineEditEventHandler), typeof(TimelineView));

        /// <summary>
        /// Move by <see cref="TimelineEditEventArgs.DayDelta"/> whole days. <see cref="TimelineMoveScope.Tail"/>:
        /// from <see cref="TimelineEditEventArgs.Block"/> onward to <see cref="TimelineEditEventArgs.TargetDate"/>
        /// (planner <c>MoveDailyTail</c>); <see cref="TimelineMoveScope.Activity"/>: the whole activity
        /// (<c>MoveActivity</c>). Raised on drag release, Ctrl(+Shift)+Left/Right and the "Mover…" menu items.
        /// </summary>
        public event TimelineEditEventHandler MoveRequested { add => AddHandler(MoveRequestedEvent, value); remove => RemoveHandler(MoveRequestedEvent, value); }

        /// <summary>"Mandar al siguiente periodo" from the block (planner <c>DeferDailyTail</c>).</summary>
        public event TimelineEditEventHandler DeferRequested { add => AddHandler(DeferRequestedEvent, value); remove => RemoveHandler(DeferRequestedEvent, value); }

        /// <summary>"Dividir y desfasar" from the block with Divisions and GapDays (planner <c>SplitAndStagger</c>).</summary>
        public event TimelineEditEventHandler StaggerRequested { add => AddHandler(StaggerRequestedEvent, value); remove => RemoveHandler(StaggerRequestedEvent, value); }

        /// <summary>"Asignar equipo" on the block: the host shows its resource picker.</summary>
        public event TimelineEditEventHandler AssignResourceRequested { add => AddHandler(AssignResourceRequestedEvent, value); remove => RemoveHandler(AssignResourceRequestedEvent, value); }

        /// <summary>"Exentar Límite" / "Quitar exención" (only when the host makes the item visible in <see cref="BlockMenuOpening"/>).</summary>
        public event TimelineEditEventHandler CapacityExemptionRequested { add => AddHandler(CapacityExemptionRequestedEvent, value); remove => RemoveHandler(CapacityExemptionRequestedEvent, value); }

        /// <summary>Ctrl+Z.</summary>
        public event TimelineEditEventHandler UndoRequested { add => AddHandler(UndoRequestedEvent, value); remove => RemoveHandler(UndoRequestedEvent, value); }

        /// <summary>Ctrl+Y or Ctrl+Shift+Z.</summary>
        public event TimelineEditEventHandler RedoRequested { add => AddHandler(RedoRequestedEvent, value); remove => RemoveHandler(RedoRequestedEvent, value); }

        /// <summary>
        /// While dragging (plain drag and Shift+drag): raised when the target day or the scope changes, and once with
        /// <see cref="TimelineDragPreviewEventArgs.IsActive"/> false when the drag ends (drop, Esc, lost capture). The
        /// host previews the move and calls <see cref="TimelineDragPreviewEventArgs.SetWarnings"/> (or later
        /// <see cref="SetDragWarnings"/>) with the days that would exceed capacity.
        /// </summary>
        public event TimelineDragPreviewEventHandler DragPreviewChanged { add => AddHandler(DragPreviewChangedEvent, value); remove => RemoveHandler(DragPreviewChangedEvent, value); }

        /// <summary>The block context menu is about to open: hide, disable or relabel entries, or set Handled to suppress it.</summary>
        public event TimelineMenuEventHandler BlockMenuOpening { add => AddHandler(BlockMenuOpeningEvent, value); remove => RemoveHandler(BlockMenuOpeningEvent, value); }

        /// <summary>Day of the keyboard-focused block in the selected row, or -1. Left/Right move it.</summary>
        public int FocusedDay => FocusedBlockIndex >= 0 ? _focusDay : -1;

        /// <summary>Index of the keyboard-focused block in the selected row's blocks, or -1.</summary>
        public int FocusedBlockIndex =>
            SelectedRowIndex >= 0 ? TimelineEditMath.BlockOnDay(_rows[SelectedRowIndex].Blocks, _focusDay) : -1;

        /// <summary>Select the activity and focus its block on <paramref name="day"/> (-1: no block).</summary>
        public void FocusBlock(string key, int day)
        {
            SelectedKey = key;
            _focusDay = day;
            RequestRedraw(TimelineLayers.Overlay);
        }

        /// <summary>A drag preview is showing.</summary>
        public bool IsDragging => _chart.IsDragging;

        /// <summary>Days marked by the current drag's capacity preview (empty when not dragging).</summary>
        public IReadOnlyCollection<int> DragWarningDays => _chart.DragWarningDays;

        /// <summary>Badge text of the current drag's capacity preview, or null.</summary>
        public string DragWarningText => _chart.DragWarningText;

        /// <summary>
        /// Marks the days where the current drag would exceed capacity (for hosts that compute the preview after the
        /// <see cref="DragPreviewChanged"/> handler returned). Ignored when no drag is showing.
        /// </summary>
        public void SetDragWarnings(IEnumerable<int> days, string text = null) => _chart.SetDragWarnings(days, text);

        /// <summary>Raise <see cref="DragPreviewChanged"/> and return the args (warnings set by handlers).</summary>
        internal TimelineDragPreviewEventArgs RaiseDragPreview(bool active, int row, int block, TimelineMoveScope scope, int delta)
        {
            var r = active && row >= 0 && row < _rows.Count ? _rows[row] : null;
            DateTime? date = r != null && block >= 0 && block < r.Blocks.Count ? (Model?.StartDate.Date ?? DateTime.MinValue).AddDays(r.Blocks[block].Day) : (DateTime?)null;
            var args = new TimelineDragPreviewEventArgs(DragPreviewChangedEvent, this, r != null, r, r == null ? -1 : row, r == null ? -1 : block, date, scope, r == null ? 0 : delta);
            RaiseEvent(args);
            return args;
        }

        /// <summary>Last reason shown by blocked feedback (for hosts and tests), or null.</summary>
        public string LastBlockedReason { get; private set; }

        private void OnIsEditableChanged()
        {
            if (!IsEditable) { _chart.CancelDrag(); CloseStagger(); }
        }

        #endregion

        #region Requests

        /// <summary>Raise a move; on rejection flash the blocks it carried.</summary>
        internal TimelineEditEventArgs RequestMove(int row, int block, TimelineMoveScope scope, int delta, TimelineEditOrigin origin, IList<int> moved = null)
        {
            if (row < 0 || row >= _rows.Count || delta == 0) return null;
            var r = _rows[row];
            var args = Raise(MoveRequestedEvent, TimelineEditAction.Move, origin, row, block, scope, delta);
            if (args.RejectReason != null)
                ShowBlocked(row, (moved ?? TimelineEditMath.MovedBlocks(r.Blocks, block, scope)).Select(k => r.Blocks[k].Day), args.RejectReason);
            return args;
        }

        internal TimelineEditEventArgs RequestEdit(RoutedEvent routedEvent, TimelineEditAction action, TimelineEditOrigin origin, int row, int block, int divisions = 0, int gapDays = 0)
        {
            var args = Raise(routedEvent, action, origin, row, block, TimelineMoveScope.Tail, 0, divisions, gapDays);
            if (args.RejectReason != null)
            {
                if (row >= 0 && block >= 0 && row < _rows.Count)
                    ShowBlocked(row, TimelineEditMath.MovedBlocks(_rows[row].Blocks, block, TimelineMoveScope.Tail).Select(k => _rows[row].Blocks[k].Day), args.RejectReason);
                else
                    ShowBlocked(-1, Array.Empty<int>(), args.RejectReason);
            }
            return args;
        }

        private TimelineEditEventArgs Raise(RoutedEvent routedEvent, TimelineEditAction action, TimelineEditOrigin origin, int row, int block,
            TimelineMoveScope scope, int delta, int divisions = 0, int gapDays = 0)
        {
            var r = row >= 0 && row < _rows.Count ? _rows[row] : null;
            DateTime? date = r != null && block >= 0 && block < r.Blocks.Count ? (Model?.StartDate.Date ?? DateTime.MinValue).AddDays(r.Blocks[block].Day) : (DateTime?)null;
            var args = new TimelineEditEventArgs(routedEvent, this, action, origin, r, r == null ? -1 : row, r == null ? -1 : block, date, scope, delta, divisions, gapDays);
            RaiseEvent(args);
            var command = EditCommand;
            if (!args.Handled && command != null && command.CanExecute(args)) command.Execute(args);
            return args;
        }

        /// <summary>Ask the host to undo (Ctrl+Z).</summary>
        public TimelineEditEventArgs RequestUndo() => RequestEdit(UndoRequestedEvent, TimelineEditAction.Undo, TimelineEditOrigin.Keyboard, -1, -1);

        /// <summary>Ask the host to redo (Ctrl+Y).</summary>
        public TimelineEditEventArgs RequestRedo() => RequestEdit(RedoRequestedEvent, TimelineEditAction.Redo, TimelineEditOrigin.Keyboard, -1, -1);

        #endregion

        #region Keyboard

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.OriginalSource is TextBox) return;   // typing in an "Avance real" box
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftShift || key == Key.RightShift) { _chart.RefreshDragScope(); return; }
            if (HandleKey(key, Keyboard.Modifiers)) e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift) _chart.RefreshDragScope();
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            RequestRedraw(TimelineLayers.Overlay);
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            RequestRedraw(TimelineLayers.Overlay);
        }

        /// <summary>
        /// Up/Down: previous / next activity (the focus jumps to its nearest block). Left/Right: previous / next block.
        /// Ctrl+Left/Right: move the focused block's tail (or the activity, without a focused block) one day;
        /// Ctrl+Shift+Left/Right: the whole activity. Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z): undo / redo. Escape cancels a
        /// drag. Shift+F10 or the menu key opens the focused block's context menu.
        /// </summary>
        internal bool HandleKey(Key key, ModifierKeys mods)
        {
            bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
            if (key == Key.Escape)
            {
                if (_chart.IsDragging) { _chart.CancelDrag(); return true; }
                if (_staggerPopup != null && _staggerPopup.IsOpen) { CloseStagger(); return true; }
                _chart.CancelDrag();
                return false;
            }
            if (_chart.IsDragging) return true; // no other keys while dragging
            if (ctrl && key == Key.Z && !shift) { RequestUndo(); return true; }
            if (ctrl && (key == Key.Y || (key == Key.Z && shift))) { RequestRedo(); return true; }
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                    MoveSelection(key == Key.Down ? 1 : -1);
                    return true;
                case Key.Left:
                case Key.Right:
                    int step = key == Key.Right ? 1 : -1;
                    if (ctrl) KeyboardMove(step, shift);
                    else MoveFocus(step);
                    return true;
                case Key.Apps:
                case Key.F10 when shift:
                    if (SelectedRowIndex >= 0 && FocusedBlockIndex >= 0) OpenBlockMenu(SelectedRowIndex, FocusedBlockIndex, _chart, null);
                    return true;
            }
            return false;
        }

        private void MoveSelection(int step)
        {
            int i = SelectedRowIndex < 0 ? (step > 0 ? -1 : _rows.Count) : SelectedRowIndex;
            for (i += step; i >= 0 && i < _rows.Count; i += step)
            {
                if (_rows[i].Kind != TimelineRowKind.Activity) continue;
                if (_focusDay >= 0)
                {
                    int k = TimelineEditMath.NearestBlockIndex(_rows[i].Blocks, _focusDay);
                    if (k >= 0) _focusDay = _rows[i].Blocks[k].Day;
                }
                SelectedKey = _rows[i].Key;
                ScrollIntoView(_rows[i].Key);
                if (_focusDay >= 0) ScrollDayIntoView(_focusDay);
                break;
            }
        }

        private void MoveFocus(int step)
        {
            if (SelectedRowIndex < 0) return;
            var blocks = _rows[SelectedRowIndex].Blocks;
            int from = FocusedBlockIndex >= 0 ? _focusDay : -1;
            if (from < 0 && _focusDay >= 0)
            {
                // Focus day without a block (e.g. after a refresh): start from the nearest block.
                int near = TimelineEditMath.NearestBlockIndex(blocks, _focusDay);
                if (near >= 0) { _focusDay = blocks[near].Day; ScrollDayIntoView(_focusDay); RequestRedraw(TimelineLayers.Overlay); }
                return;
            }
            int k = TimelineEditMath.NextBlockIndex(blocks, from, step);
            if (k < 0) return;
            _focusDay = blocks[k].Day;
            ScrollDayIntoView(_focusDay);
            RequestRedraw(TimelineLayers.Overlay);
        }

        /// <summary>Ctrl+Left/Right (one day). Rejected locally, with blocked feedback, for locked completed work and
        /// for moves that leave the horizon; everything else goes to the host.</summary>
        internal TimelineEditEventArgs KeyboardMove(int delta, bool wholeActivity)
        {
            if (!IsEditable || SelectedRowIndex < 0) return null;
            int row = SelectedRowIndex;
            var r = _rows[row];
            int block = FocusedBlockIndex;
            var scope = wholeActivity || block < 0 ? TimelineMoveScope.Activity : TimelineMoveScope.Tail;
            if (!TimelineEditMath.CanMove(r, block, scope, out string reason))
            {
                var days = block >= 0 ? new[] { r.Blocks[block].Day } : r.Blocks.Select(b => b.Day).ToArray();
                ShowBlocked(row, days, reason);
                return null;
            }
            var moved = TimelineEditMath.MovedBlocks(r.Blocks, block, scope);
            int dayCount = Model?.DayCount ?? 0;
            int origin = scope == TimelineMoveScope.Tail ? r.Blocks[block].Day : r.Blocks[moved[0]].Day;
            int last = r.Blocks[moved[moved.Count - 1]].Day;
            if ((delta < 0 && origin + delta < 0) || (delta > 0 && (scope == TimelineMoveScope.Tail ? origin : last) + delta > dayCount - 1))
            {
                ShowBlocked(row, moved.Select(k => r.Blocks[k].Day), HorizonRangeReason);
                return null;
            }
            var args = RequestMove(row, block, scope, delta, TimelineEditOrigin.Keyboard, moved);
            // The focus follows its block, so repeated Ctrl+Right keeps moving the same day.
            if (args != null && args.RejectReason == null && block >= 0) { _focusDay += delta; ScrollDayIntoView(_focusDay); RequestRedraw(TimelineLayers.Overlay); }
            return args;
        }

        private void ScrollDayIntoView(int day)
        {
            if (Layout == null || day < 0) return;
            double x = Layout.DayToX(day), dw = Layout.DayWidth;
            if (x < OffsetX) SetHorizontalOffset(x);
            else if (x + dw > OffsetX + ViewportWidth) SetHorizontalOffset(x + dw - ViewportWidth);
        }

        #endregion

        #region Blocked feedback

        internal sealed class Flash
        {
            public string Key;
            public int Row;
            public int[] Days;
            public long StartTicks;
            public double Alpha;
        }

        private const double FlashMilliseconds = 1400;
        private readonly List<Flash> _flashes = new List<Flash>();
        private DispatcherTimer _flashTimer;
        private Popup _reasonPopup;
        private TextBlock _reasonText;
        private DispatcherTimer _reasonTimer;

        internal IReadOnlyList<Flash> Flashes => _flashes;

        /// <summary>
        /// Blocked feedback for a rejection that arrives later (async engine call): flash the activity's block on
        /// <paramref name="day"/> red (day &lt; 0: all its pending blocks) and show <paramref name="reason"/> next to it.
        /// </summary>
        public void ShowBlocked(string key, int day, string reason)
        {
            if (key == null || !_rowByKey.TryGetValue(key, out int row)) { ShowBlocked(-1, Array.Empty<int>(), reason); return; }
            var blocks = _rows[row].Blocks;
            IEnumerable<int> days = day >= 0 ? new[] { day } : TimelineEditMath.MovedBlocks(blocks, 0, TimelineMoveScope.Activity).Select(k => blocks[k].Day);
            ShowBlocked(row, days, reason);
        }

        /// <summary>Flash these days of the activity red and show the reason.</summary>
        public void ShowBlocked(string key, IEnumerable<int> days, string reason)
        {
            int row = key != null && _rowByKey.TryGetValue(key, out int r) ? r : -1;
            ShowBlocked(row, days ?? Array.Empty<int>(), reason);
        }

        internal void ShowBlocked(int row, IEnumerable<int> days, string reason)
        {
            LastBlockedReason = string.IsNullOrEmpty(reason) ? "No se pudo completar la operación solicitada." : reason;
            int[] list = days.ToArray();
            if (row >= 0 && row < _rows.Count && list.Length > 0)
            {
                _flashes.RemoveAll(f => f.Row == row);
                _flashes.Add(new Flash { Key = _rows[row].Key, Row = row, Days = list, StartTicks = Stopwatch.GetTimestamp(), Alpha = 1 });
                if (_flashTimer == null)
                {
                    _flashTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
                    _flashTimer.Tick += (s, e) => AnimateFlashes();
                }
                _flashTimer.Start();
                RequestRedraw(TimelineLayers.Overlay);
            }
            ShowReason(row, list.Length > 0 ? list[0] : -1, LastBlockedReason);
        }

        private void AnimateFlashes()
        {
            long now = Stopwatch.GetTimestamp();
            for (int i = _flashes.Count - 1; i >= 0; i--)
            {
                var f = _flashes[i];
                double t = (now - f.StartTicks) * 1000.0 / Stopwatch.Frequency / FlashMilliseconds;
                if (t >= 1) { _flashes.RemoveAt(i); continue; }
                f.Alpha = (1 - t) * (0.55 + 0.45 * Math.Cos(t * 4 * Math.PI)); // two pulses, fading
            }
            if (_flashes.Count == 0) _flashTimer.Stop();
            RequestRedraw(TimelineLayers.Overlay);
        }

        private void RemapFlashes()
        {
            for (int i = _flashes.Count - 1; i >= 0; i--)
            {
                if (_flashes[i].Key != null && _rowByKey.TryGetValue(_flashes[i].Key, out int row)) _flashes[i].Row = row;
                else _flashes.RemoveAt(i);
            }
        }

        private bool HasWindow => PresentationSource.FromVisual(this) != null;

        private void ShowReason(int row, int day, string reason)
        {
            if (!HasWindow) return;
            if (_reasonPopup == null)
            {
                _reasonText = new TextBlock { Foreground = T.BlockedText, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, FontSize = 12 };
                var title = new TextBlock { Text = "Movimiento bloqueado", FontWeight = FontWeights.SemiBold, Foreground = T.BlockedText, FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };
                var stack = new StackPanel();
                stack.Children.Add(title);
                stack.Children.Add(_reasonText);
                _reasonPopup = new Popup
                {
                    AllowsTransparency = true,
                    Placement = PlacementMode.Relative,
                    PlacementTarget = _chart,
                    StaysOpen = true,
                    Child = new Border
                    {
                        Background = T.BlockedBack,
                        BorderBrush = new SolidColorBrush(T.BlockedColor),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(8, 5, 8, 6),
                        Child = stack,
                    },
                };
                _reasonTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(4) };
                _reasonTimer.Tick += (s, e) => HideReason();
            }
            _reasonText.Text = reason;
            Point at = new Point(8, TimelineLayout.HeaderHeight + 8);
            if (row >= 0 && Layout != null && day >= 0)
            {
                double x = Layout.DayToX(day) - OffsetX, y = TimelineLayout.HeaderHeight + Layout.RowTop(row) - OffsetY + Layout.RowHeight(row);
                if (x >= -Layout.DayWidth && x <= ViewportWidth && y >= TimelineLayout.HeaderHeight && y <= _chart.ActualHeight)
                    at = new Point(Math.Max(0, Math.Min(Math.Max(0, ViewportWidth - 300), x)), y + 2);
            }
            HideHover();
            _reasonPopup.HorizontalOffset = at.X;
            _reasonPopup.VerticalOffset = at.Y;
            _reasonPopup.IsOpen = false;
            _reasonPopup.IsOpen = true;
            _reasonTimer.Stop();
            _reasonTimer.Start();
        }

        internal void HideReason()
        {
            _reasonTimer?.Stop();
            if (_reasonPopup != null) _reasonPopup.IsOpen = false;
        }

        #endregion

        #region Hover tooltip

        private Popup _hoverPopup;
        private TextBlock _hoverText;
        private DispatcherTimer _hoverDelay;
        private int _hoverRow = -1, _hoverBlock = -1;
        private Point _hoverPoint;

        /// <summary>Hover text of a block: activity, date, quantity (or the host's <see cref="TimelineBlock.ToolTip"/>) and state.</summary>
        public static string BlockToolTipText(ITimelineRow row, TimelineBlock block, DateTime date)
        {
            var lines = new List<string>
            {
                string.IsNullOrEmpty(row.Id) ? row.Name : string.IsNullOrEmpty(row.Name) ? row.Id : row.Id + " · " + row.Name,
                TimelineLabels.Weekday(date) + " " + date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrEmpty(block.ToolTip)) lines.Add(block.ToolTip);
            else if (!string.IsNullOrEmpty(block.Label)) lines.Add("Cantidad: " + block.Label);
            if (block.IsCompleted || row.IsLocked) lines.Add("Avance completado: bloqueado, no se puede mover.");
            else if (row.Move == TimelineMoveKind.Cascade) lines.Add("Desplazada por cascada; se puede mover.");
            else if (row.Move == TimelineMoveKind.Direct) lines.Add("Movida directamente.");
            return string.Join("\n", lines);
        }

        internal void UpdateHover(TimelineHit hit, UIElement panel, Point p)
        {
            if (hit.Block < 0 || hit.Row < 0 || hit.Row >= _rows.Count || !HasWindow) { HideHover(); return; }
            _hoverPoint = p;
            if (_hoverPopup != null && _hoverPopup.IsOpen)
            {
                if (hit.Row != _hoverRow || hit.Block != _hoverBlock) SetHoverText(hit);
                _hoverPopup.HorizontalOffset = p.X + 14;
                _hoverPopup.VerticalOffset = p.Y + 18;
                return;
            }
            _hoverRow = hit.Row;
            _hoverBlock = hit.Block;
            if (_hoverDelay == null)
            {
                _hoverDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(450) };
                _hoverDelay.Tick += (s, e) => OpenHover(false);
            }
            _hoverDelay.Stop();
            _hoverDelay.Start();
        }

        /// <summary>Show the hover tooltip of a block now, at a chart point (preview screenshots).</summary>
        internal void ShowHoverNow(int row, int block, Point p)
        {
            if (!HasWindow || row < 0 || row >= _rows.Count || block < 0 || block >= _rows[row].Blocks.Count) return;
            _hoverRow = row;
            _hoverBlock = block;
            _hoverPoint = p;
            OpenHover(true);
        }

        private void OpenHover(bool force)
        {
            _hoverDelay?.Stop();
            if (_hoverRow < 0 || _hoverRow >= _rows.Count || _chart.IsDragging || (!force && !_chart.IsMouseOver)) return;
            if (_hoverPopup == null)
            {
                _hoverText = new TextBlock { Foreground = T.Text, FontSize = 12, LineHeight = 17 };
                _hoverPopup = new Popup
                {
                    AllowsTransparency = true,
                    Placement = PlacementMode.Relative,
                    PlacementTarget = _chart,
                    IsHitTestVisible = false,
                    Child = new Border
                    {
                        Background = T.TipBack,
                        BorderBrush = T.TipBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(8, 5, 8, 6),
                        Child = _hoverText,
                    },
                };
            }
            SetHoverText(new TimelineHit(_hoverRow, _hoverBlock));
            _hoverPopup.HorizontalOffset = _hoverPoint.X + 14;
            _hoverPopup.VerticalOffset = _hoverPoint.Y + 18;
            _hoverPopup.IsOpen = true;
        }

        private void SetHoverText(TimelineHit hit)
        {
            _hoverRow = hit.Row;
            _hoverBlock = hit.Block;
            var row = _rows[hit.Row];
            var block = row.Blocks[hit.Block];
            _hoverText.Text = BlockToolTipText(row, block, (Model?.StartDate.Date ?? DateTime.MinValue).AddDays(block.Day));
        }

        internal void HideHover()
        {
            _hoverDelay?.Stop();
            _hoverRow = _hoverBlock = -1;
            if (_hoverPopup != null) _hoverPopup.IsOpen = false;
        }

        #endregion

        #region Context menu

        /// <summary>The block menu entries before <see cref="BlockMenuOpening"/>, with enabled states for this block.</summary>
        internal List<TimelineMenuItem> DefaultMenuItems(int row, int block)
        {
            var r = _rows[row];
            int dayCount = Model?.DayCount ?? 0;
            int day = r.Blocks[block].Day;
            var pending = TimelineEditMath.MovedBlocks(r.Blocks, 0, TimelineMoveScope.Activity);
            int first = pending.Count > 0 ? r.Blocks[pending[0]].Day : day;
            int last = pending.Count > 0 ? r.Blocks[pending[pending.Count - 1]].Day : day;
            var items = new List<TimelineMenuItem>
            {
                new TimelineMenuItem(TimelineEditAction.Move, "Mover desde este día: −1 día", TimelineMoveScope.Tail, -1, "Ctrl+←") { IsEnabled = day > 0 },
                new TimelineMenuItem(TimelineEditAction.Move, "Mover desde este día: +1 día", TimelineMoveScope.Tail, 1, "Ctrl+→") { IsEnabled = day < dayCount - 1 },
                new TimelineMenuItem(TimelineEditAction.Move, "Mover actividad: −1 día", TimelineMoveScope.Activity, -1, "Ctrl+Mayús+←") { IsEnabled = first > 0 },
                new TimelineMenuItem(TimelineEditAction.Move, "Mover actividad: +1 día", TimelineMoveScope.Activity, 1, "Ctrl+Mayús+→") { IsEnabled = last < dayCount - 1 },
                new TimelineMenuItem(TimelineEditAction.AssignResource, "Asignar equipo") { StartsGroup = true },
                new TimelineMenuItem(TimelineEditAction.ToggleCapacityExemption, "Exentar Límite") { IsVisible = false },
                new TimelineMenuItem(TimelineEditAction.DeferToNextPeriod, "Mandar al siguiente periodo"),
                new TimelineMenuItem(TimelineEditAction.SplitAndStagger, "Dividir y desfasar"),
            };
            foreach (var item in items)
                if (!item.IsEnabled) item.DisabledReason = HorizonRangeReason;
            return items;
        }

        /// <summary>
        /// Right-click (or Shift+F10) on a pending block. Like the browser planner, completed blocks and a busy host
        /// get no menu. Returns the entries shown (tests), or null.
        /// </summary>
        internal IList<TimelineMenuItem> OpenBlockMenu(int row, int block, UIElement panel, Point? at)
        {
            if (!IsEditable || row < 0 || row >= _rows.Count || block < 0 || block >= _rows[row].Blocks.Count) return null;
            var r = _rows[row];
            if (r.IsLocked || r.Blocks[block].IsCompleted) return null;
            HideHover();
            HideReason();
            _focusDay = r.Blocks[block].Day;
            RequestRedraw(TimelineLayers.Overlay);
            var date = (Model?.StartDate.Date ?? DateTime.MinValue).AddDays(r.Blocks[block].Day);
            var args = new TimelineMenuEventArgs(BlockMenuOpeningEvent, this, r, row, block, date, DefaultMenuItems(row, block));
            RaiseEvent(args);
            if (args.Handled) return null;
            var visible = args.Items.Where(i => i.IsVisible).ToList();
            if (visible.Count == 0 || !HasWindow) return visible;

            string key = r.Key;
            int day = r.Blocks[block].Day;
            var menu = new ContextMenu { PlacementTarget = panel };
            _menu = menu;
            if (at is Point p)
            {
                menu.Placement = PlacementMode.Relative;
                menu.HorizontalOffset = p.X;
                menu.VerticalOffset = p.Y;
            }
            else if (Layout != null)
            {
                menu.Placement = PlacementMode.Relative;
                menu.HorizontalOffset = Math.Max(0, Layout.DayToX(day) - OffsetX);
                menu.VerticalOffset = TimelineLayout.HeaderHeight + Layout.RowTop(row) - OffsetY + Layout.RowHeight(row);
            }
            foreach (var item in visible)
            {
                if (item.StartsGroup && menu.Items.Count > 0) menu.Items.Add(new Separator());
                var mi = new MenuItem { Header = item.Header, InputGestureText = item.InputGestureText, IsEnabled = item.IsEnabled };
                if (!item.IsEnabled && !string.IsNullOrEmpty(item.DisabledReason))
                {
                    mi.ToolTip = item.DisabledReason;
                    ToolTipService.SetShowOnDisabled(mi, true);
                }
                var chosen = item;
                mi.Click += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RunMenuItem(key, day, chosen, panel)));
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
            return visible;
        }

        /// <summary>Run a menu entry against the activity / day it was opened on (looked up again: the model may have changed).</summary>
        internal void RunMenuItem(string key, int day, TimelineMenuItem item, UIElement panel)
        {
            if (!IsEditable || key == null || !_rowByKey.TryGetValue(key, out int row)) return;
            int block = TimelineEditMath.BlockOnDay(_rows[row].Blocks, day);
            if (block < 0) return;
            switch (item.Action)
            {
                case TimelineEditAction.Move:
                    var r = _rows[row];
                    if (!TimelineEditMath.CanMove(r, block, item.Scope, out string reason)) { ShowBlocked(row, new[] { day }, reason); return; }
                    var args = RequestMove(row, block, item.Scope, item.DayDelta, TimelineEditOrigin.ContextMenu);
                    if (args != null && args.RejectReason == null) _focusDay = day + item.DayDelta;
                    break;
                case TimelineEditAction.DeferToNextPeriod:
                    RequestEdit(DeferRequestedEvent, item.Action, TimelineEditOrigin.ContextMenu, row, block);
                    break;
                case TimelineEditAction.AssignResource:
                    RequestEdit(AssignResourceRequestedEvent, item.Action, TimelineEditOrigin.ContextMenu, row, block);
                    break;
                case TimelineEditAction.ToggleCapacityExemption:
                    RequestEdit(CapacityExemptionRequestedEvent, item.Action, TimelineEditOrigin.ContextMenu, row, block);
                    break;
                case TimelineEditAction.SplitAndStagger:
                    OpenStagger(key, day, panel);
                    break;
                case TimelineEditAction.Undo:
                    RequestUndo();
                    break;
                case TimelineEditAction.Redo:
                    RequestRedo();
                    break;
            }
        }

        #endregion

        #region Split and stagger popover

        private ContextMenu _menu;

        /// <summary>Open popups (menu, popover, tooltips) with their offset relative to the chart, so the preview can
        /// composite them into a screenshot without capturing the screen.</summary>
        internal IEnumerable<(FrameworkElement Element, Point Offset)> OpenPopups()
        {
            foreach (var popup in new[] { _hoverPopup, _reasonPopup, _staggerPopup })
                if (popup != null && popup.IsOpen && popup.Child is FrameworkElement child)
                    yield return (child, new Point(popup.HorizontalOffset, popup.VerticalOffset));
            if (_menu != null && _menu.IsOpen)
                yield return (_menu, new Point(_menu.HorizontalOffset, _menu.VerticalOffset));
        }

        private Popup _staggerPopup;
        private TextBox _divisionsBox, _gapBox;
        private TextBlock _staggerError;
        private string _staggerKey;
        private int _staggerDay;

        /// <summary>The browser's stagger popover: "N.º de divisiones", "Días de desfase" (both 1), Cancelar / Aplicar.</summary>
        internal void OpenStagger(string key, int day, UIElement panel)
        {
            if (!HasWindow || !_rowByKey.TryGetValue(key, out int row)) return;
            _staggerKey = key;
            _staggerDay = day;
            if (_staggerPopup == null)
            {
                _divisionsBox = new TextBox { Text = "1", Width = 64, Margin = new Thickness(0, 2, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
                _gapBox = new TextBox { Text = "1", Width = 64, Margin = new Thickness(0, 2, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
                AutomationLabel(_divisionsBox, "N.º de divisiones");
                AutomationLabel(_gapBox, "Días de desfase");
                _staggerError = new TextBlock { Foreground = T.BlockedText, TextWrapping = TextWrapping.Wrap, MaxWidth = 220, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
                var cancel = new Button { Content = "Cancelar", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
                var apply = new Button { Content = "Aplicar", Padding = new Thickness(10, 2, 10, 2) };
                cancel.Click += (s, e) => CloseStagger();
                apply.Click += (s, e) => ApplyStagger();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                buttons.Children.Add(cancel);
                buttons.Children.Add(apply);
                var form = new StackPanel();
                form.Children.Add(new TextBlock { Text = "Dividir y desfasar", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
                form.Children.Add(new TextBlock { Text = "N.º de divisiones" });
                form.Children.Add(_divisionsBox);
                form.Children.Add(new TextBlock { Text = "Días de desfase" });
                form.Children.Add(_gapBox);
                form.Children.Add(_staggerError);
                form.Children.Add(buttons);
                var border = new Border
                {
                    Background = T.TipBack,
                    BorderBrush = T.TipBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(10, 8, 10, 10),
                    Child = form,
                };
                border.PreviewKeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter) { ApplyStagger(); e.Handled = true; }
                    else if (e.Key == Key.Escape) { CloseStagger(); e.Handled = true; }
                };
                _staggerPopup = new Popup { AllowsTransparency = true, Placement = PlacementMode.Relative, PlacementTarget = _chart, StaysOpen = false, Child = border };
                _staggerPopup.Opened += (s, e) => { _divisionsBox.Focus(); _divisionsBox.SelectAll(); };
            }
            _divisionsBox.Text = "1";
            _gapBox.Text = "1";
            _staggerError.Visibility = Visibility.Collapsed;
            _staggerPopup.HorizontalOffset = Layout == null ? 0 : Math.Max(0, Layout.DayToX(day) - OffsetX);
            _staggerPopup.VerticalOffset = Layout == null ? 0 : TimelineLayout.HeaderHeight + Layout.RowTop(row) - OffsetY + Layout.RowHeight(row);
            _staggerPopup.IsOpen = true;
        }

        private static void AutomationLabel(UIElement element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);

        private void ApplyStagger()
        {
            var args = SubmitStagger(_staggerKey, _staggerDay, _divisionsBox.Text, _gapBox.Text, out string error);
            if (error != null)
            {
                _staggerError.Text = error;
                _staggerError.Visibility = Visibility.Visible;
                return;
            }
            CloseStagger();
            if (args == null) return;
        }

        /// <summary>Validate the popover's values like the browser (integers &gt;= 1) and raise <see cref="StaggerRequested"/>.</summary>
        internal TimelineEditEventArgs SubmitStagger(string key, int day, string divisionsText, string gapText, out string error)
        {
            error = null;
            if (!int.TryParse((divisionsText ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int divisions) || divisions < 1 ||
                !int.TryParse((gapText ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int gap) || gap < 1)
            {
                error = StaggerInputReason;
                return null;
            }
            if (!IsEditable || key == null || !_rowByKey.TryGetValue(key, out int row)) return null;
            int block = TimelineEditMath.BlockOnDay(_rows[row].Blocks, day);
            if (block < 0) return null;
            return RequestEdit(StaggerRequestedEvent, TimelineEditAction.SplitAndStagger, TimelineEditOrigin.ContextMenu, row, block, divisions, gap);
        }

        private void CloseStagger()
        {
            if (_staggerPopup != null) _staggerPopup.IsOpen = false;
        }

        #endregion
    }
}
