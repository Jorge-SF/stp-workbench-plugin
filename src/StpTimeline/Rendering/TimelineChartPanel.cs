using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using T = Maptek.Vulcan.Stp.Timeline.Rendering.TimelineTheme;

namespace Maptek.Vulcan.Stp.Timeline.Rendering
{
    /// <summary>
    /// The chart: day header, weekend shading, row bands, precedence links, day blocks and the "completado hasta"
    /// line. Five DrawingVisual layers (back, links, blocks, editing overlay, front) so a selection change, the links
    /// toggle or a drag frame only redraws its own layer. Every layer draws only the visible rows x visible days.
    /// Editing input (drag, right-click) lives here too; it only expresses intent through the owner's events.
    /// </summary>
    internal sealed class TimelineChartPanel : LayeredPanel
    {
        internal const int BackLayer = 0, LinkLayer = 1, BlockLayer = 2, OverlayLayer = 3, FrontLayer = 4;
        private const double BlockInset = TimelineEditMath.BlockInset; // mockup: y + 6, height RH - 12

        private readonly TimelineView _owner;
        private Point _pressPoint;
        private TimelineHit _pressHit;
        private bool _pressed, _dragStarted;

        public TimelineChartPanel(TimelineView owner) : base(5)
        {
            _owner = owner;
        }

        internal int LastDrawnBlocks { get; private set; }
        internal int LastDrawnLinks { get; private set; }

        private double BodyHeight => Math.Max(0, ActualHeight - TimelineLayout.HeaderHeight);

        private Rect BodyRect => new Rect(0, TimelineLayout.HeaderHeight, ActualWidth, BodyHeight);

        public void Redraw(TimelineLayers layers)
        {
            if ((layers & TimelineLayers.ChartBack) != 0) DrawBack();
            if ((layers & TimelineLayers.Links) != 0) DrawLinks();
            if ((layers & TimelineLayers.Blocks) != 0) DrawBlocks();
            if ((layers & TimelineLayers.Overlay) != 0) DrawOverlay();
            if ((layers & TimelineLayers.Front) != 0) DrawFront();
        }

        private double RowScreenTop(int row) => TimelineLayout.HeaderHeight + _owner.Layout.RowTop(row) - _owner.OffsetY;

        private double DayScreenX(double day) => _owner.Layout.DayToX(day) - _owner.OffsetX;

        private void DrawBack()
        {
            double w = ActualWidth, h = ActualHeight, hh = TimelineLayout.HeaderHeight;
            using (var dc = Layers[BackLayer].RenderOpen())
            {
                dc.DrawRectangle(T.Panel, null, new Rect(0, 0, w, h)); // also makes the whole panel hit-testable
                var layout = _owner.Layout;
                var model = _owner.Model;
                if (layout == null || model == null || h <= hh) return;
                dc.PushClip(new RectangleGeometry(BodyRect));

                double dw = layout.DayWidth;
                var days = layout.VisibleDays(_owner.OffsetX, w, model.DayCount);
                DateTime start = model.StartDate.Date;
                for (int d = days.First; d < days.End; d++)
                    if (TimelineLabels.IsWeekend(start.AddDays(d)))
                        dc.DrawRectangle(T.Weekend, null, new Rect(DayScreenX(d), hh, dw, h - hh));

                var dayLines = new StreamGeometry();
                using (var g = dayLines.Open())
                {
                    for (int d = days.First; d <= days.End && d <= model.DayCount; d++)
                    {
                        double x = Snap(DayScreenX(d));
                        g.BeginFigure(new Point(x, hh), false, false);
                        g.LineTo(new Point(x, h), true, false);
                    }
                }
                dayLines.Freeze();
                dc.DrawGeometry(null, T.LineSoft, dayLines);

                double right = Math.Min(w, DayScreenX(model.DayCount));
                var rows = layout.VisibleRows(_owner.OffsetY, BodyHeight);
                var separators = new StreamGeometry();
                using (var g = separators.Open())
                {
                    for (int i = rows.First; i < rows.End; i++)
                    {
                        double top = RowScreenTop(i), rh = layout.RowHeight(i);
                        if (_owner.Rows[i].Kind == TimelineRowKind.Group)
                            dc.DrawRectangle(T.GroupBack, null, new Rect(0, top, right, rh));
                        else if (i == _owner.SelectedRowIndex)
                            dc.DrawRectangle(T.Selected, null, new Rect(0, top, right, rh));
                        double y = Snap(top + rh - 1);
                        g.BeginFigure(new Point(0, y), false, false);
                        g.LineTo(new Point(right, y), true, false);
                    }
                }
                separators.Freeze();
                dc.DrawGeometry(null, T.LineSoft, separators);
                dc.Pop();
            }
        }

        private void DrawLinks()
        {
            LastDrawnLinks = 0;
            using (var dc = Layers[LinkLayer].RenderOpen())
            {
                var layout = _owner.Layout;
                var model = _owner.Model;
                if (layout == null || model == null || !_owner.ShowLinks || model.Links == null) return;
                double viewTop = TimelineLayout.HeaderHeight, viewBottom = ActualHeight, viewRight = ActualWidth;
                if (viewBottom <= viewTop) return;

                var lines = new StreamGeometry();
                var arrows = new StreamGeometry();
                using (var gl = lines.Open())
                using (var ga = arrows.Open())
                {
                    foreach (var link in model.Links)
                    {
                        if (link == null || !_owner.TryGetSpan(link.FromKey, out int ra, out _, out int lastA)) continue;
                        if (!_owner.TryGetSpan(link.ToKey, out int rb, out int firstB, out _)) continue;
                        double y1 = RowScreenTop(ra) + layout.RowHeight(ra) / 2;
                        double y2 = RowScreenTop(rb) + layout.RowHeight(rb) / 2;
                        if (Math.Max(y1, y2) < viewTop - 6 || Math.Min(y1, y2) > viewBottom + 6) continue;
                        double x1 = DayScreenX(lastA + 1) - 2, x2 = DayScreenX(firstB) + 2;
                        double mx = Math.Max(x1 + 6, x2 - 8);
                        if (Math.Max(mx, x2) < -6 || Math.Min(x1, x2 - 6) > viewRight + 6) continue;

                        gl.BeginFigure(new Point(x1, y1), false, false);
                        gl.LineTo(new Point(mx, y1), true, true);
                        gl.LineTo(new Point(mx, y2), true, true);
                        gl.LineTo(new Point(x2 - 5, y2), true, true);
                        ga.BeginFigure(new Point(x2 + 1, y2), true, true);
                        ga.LineTo(new Point(x2 - 5, y2 - 3), false, false);
                        ga.LineTo(new Point(x2 - 5, y2 + 3), false, false);
                        LastDrawnLinks++;
                    }
                }
                lines.Freeze();
                arrows.Freeze();
                dc.PushClip(new RectangleGeometry(BodyRect));
                dc.DrawGeometry(null, T.Link, lines);
                dc.DrawGeometry(T.LinkBrush, null, arrows);
                dc.Pop();
            }
        }

        private void DrawBlocks()
        {
            LastDrawnBlocks = 0;
            using (var dc = Layers[BlockLayer].RenderOpen())
            {
                var layout = _owner.Layout;
                var model = _owner.Model;
                if (layout == null || model == null || BodyHeight <= 0) return;
                dc.PushClip(new RectangleGeometry(BodyRect));

                double dw = layout.DayWidth;
                var days = layout.VisibleDays(_owner.OffsetX, ActualWidth, model.DayCount);
                var rows = layout.VisibleRows(_owner.OffsetY, BodyHeight);
                bool labels = dw >= 20;
                bool showCompleted = _owner.ShowCompleted;
                var text = _owner.Text;
                for (int i = rows.First; i < rows.End; i++)
                {
                    var row = _owner.Rows[i];
                    var blocks = row.Blocks;
                    if (row.Kind != TimelineRowKind.Activity || blocks == null || blocks.Count == 0) continue;
                    double top = RowScreenTop(i), rh = layout.RowHeight(i);
                    double bh = Math.Max(2, rh - BlockInset * 2);
                    var fill = T.ForColor(row.Color);
                    Pen movePen = row.Move == TimelineMoveKind.Direct ? T.DirectMove
                        : row.Move == TimelineMoveKind.Cascade ? T.CascadeMove : null;
                    for (int k = TimelineEditMath.FirstBlockAtOrAfter(blocks, days.First); k < blocks.Count; k++)
                    {
                        var b = blocks[k];
                        if (b.Day >= days.End) break;
                        if (b.IsCompleted && !showCompleted) continue;
                        var rect = new Rect(DayScreenX(b.Day) + 1, top + BlockInset, Math.Max(1, dw - 2), bh);
                        dc.DrawRoundedRectangle(b.IsCompleted ? T.Hatch : b.IsCapacityExempt ? T.Exempt : fill, b.IsCompleted ? null : movePen, rect, 2, 2);
                        if (labels && !string.IsNullOrEmpty(b.Label))
                        {
                            var ft = text.Get(b.Label, T.Bold, T.ChartFontSize, b.IsCompleted ? T.DoneText : T.BlockText);
                            if (ft.Width <= rect.Width - 2)
                                dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
                        }
                        LastDrawnBlocks++;
                    }
                }
                dc.Pop();
            }
        }

        private void DrawFront()
        {
            double w = ActualWidth, hh = TimelineLayout.HeaderHeight;
            using (var dc = Layers[FrontLayer].RenderOpen())
            {
                var layout = _owner.Layout;
                var model = _owner.Model;
                dc.DrawRectangle(T.HeaderBack, null, new Rect(0, 0, w, hh));
                if (layout != null && model != null)
                {
                    var text = _owner.Text;
                    double dw = layout.DayWidth;
                    DateTime start = model.StartDate.Date;
                    var days = layout.VisibleDays(_owner.OffsetX, w, model.DayCount);
                    // Wide days: "lun" over "16/11" (mockup). Medium: "lun" over "16". Narrow (< 20 px): one
                    // "lun" / "16/11" label per week, on Mondays, left-aligned and allowed to span the week.
                    bool wide = dw >= 34, medium = dw >= 20;
                    for (int d = days.First; d < days.End; d++)
                    {
                        DateTime date = start.AddDays(d);
                        bool we = TimelineLabels.IsWeekend(date);
                        double x = DayScreenX(d);
                        if (medium)
                        {
                            var top = text.Get(TimelineLabels.Weekday(date), T.Regular, T.ChartFontSize, T.Muted);
                            dc.DrawText(top, new Point(x + (dw - top.Width) / 2, 6));
                            string label = wide ? date.ToString("dd/MM", CultureInfo.InvariantCulture) : date.Day.ToString(CultureInfo.InvariantCulture);
                            var bottom = text.Get(label, T.Bold, T.ChartFontSize, we ? T.Muted : T.Text);
                            dc.DrawText(bottom, new Point(x + (dw - bottom.Width) / 2, 21));
                        }
                        else if (date.DayOfWeek == DayOfWeek.Monday)
                        {
                            var top = text.Get(TimelineLabels.Weekday(date), T.Regular, T.ChartFontSize, T.Muted);
                            dc.DrawText(top, new Point(x + 3, 6));
                            var bottom = text.Get(date.ToString("dd/MM", CultureInfo.InvariantCulture), T.Bold, T.ChartFontSize, T.Text);
                            dc.DrawText(bottom, new Point(x + 3, 21));
                        }
                        if (medium || date.DayOfWeek == DayOfWeek.Monday)
                        {
                            double lx = Snap(x);
                            dc.DrawLine(medium ? T.LineSoft : T.Line, new Point(lx, 0), new Point(lx, hh));
                        }
                    }
                    if (model.DayCount > 0)
                    {
                        double end = Snap(DayScreenX(model.DayCount));
                        if (end <= w) dc.DrawLine(T.LineSoft, new Point(end, 0), new Point(end, hh));
                    }

                    if (_owner.ShowCompleted && model.CompletedThrough is DateTime done && BodyHeight > 0)
                    {
                        double x = Snap(DayScreenX(TimelineLayout.DateToDay(start, done)));
                        if (x >= 0 && x <= w)
                        {
                            dc.PushClip(new RectangleGeometry(BodyRect));
                            dc.DrawLine(T.CompletedLine, new Point(x, hh), new Point(x, ActualHeight));
                            var caption = text.Get("completado hasta " + done.ToString("dd/MM", CultureInfo.InvariantCulture),
                                T.Regular, T.ChartFontSize, T.CompletedLineText);
                            dc.DrawText(caption, new Point(x + 4, hh + 1));
                            dc.Pop();
                        }
                    }
                }
                dc.DrawLine(T.Line, new Point(0, Snap(hh - 1)), new Point(w, Snap(hh - 1)));
            }
        }

        #region Editing overlay

        /// <summary>
        /// Overlay layer: the focused block's ring, the drag ghost (moved blocks faded in place, dashed ghosts at the
        /// target, offset badge) and the red "blocked" flashes. Only one activity's blocks, so a drag frame costs the
        /// same with 20 or 2,000 rows.
        /// </summary>
        private void DrawOverlay()
        {
            using (var dc = Layers[OverlayLayer].RenderOpen())
            {
                var layout = _owner.Layout;
                var model = _owner.Model;
                if (layout == null || model == null || BodyHeight <= 0) return;
                dc.PushClip(new RectangleGeometry(BodyRect));
                var rows = layout.VisibleRows(_owner.OffsetY, BodyHeight);

                int sel = _owner.SelectedRowIndex, focus = _owner.FocusedBlockIndex;
                if (_drag == null && sel >= 0 && focus >= 0 && rows.Contains(sel) && _owner.IsKeyboardFocusWithin)
                {
                    var b = _owner.Rows[sel].Blocks[focus];
                    var r = BlockRect(sel, b.Day);
                    r.Inflate(1.5, 1.5);
                    dc.DrawRoundedRectangle(null, T.FocusPen, r, 3, 3);
                }

                int dayCount = model.DayCount;
                bool InHorizon(int day) => day >= 0 && day < dayCount; // like the browser, nothing past the horizon
                foreach (var flash in _owner.Flashes)
                {
                    if (!rows.Contains(flash.Row)) continue;
                    double a = flash.Alpha;
                    if (a <= 0) continue;
                    var fill = new SolidColorBrush(Color.FromArgb((byte)(200 * a), T.BlockedColor.R, T.BlockedColor.G, T.BlockedColor.B));
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, a * 1.5)), T.BlockedColor.R, T.BlockedColor.G, T.BlockedColor.B)), 2);
                    foreach (int day in flash.Days)
                        if (InHorizon(day)) dc.DrawRoundedRectangle(fill, pen, BlockRect(flash.Row, day), 2, 2);
                }

                if (_drag != null && rows.Contains(_drag.Row) && _drag.Row < _owner.Rows.Count)
                {
                    var blocks = _owner.Rows[_drag.Row].Blocks;
                    foreach (int k in _drag.Moved)
                        if (k < blocks.Count) dc.DrawRoundedRectangle(T.DragOrigin, null, BlockRect(_drag.Row, blocks[k].Day), 2, 2);
                    if (_drag.Delta != 0)
                    {
                        foreach (int k in _drag.Moved)
                        {
                            if (k >= blocks.Count) continue;
                            int day = blocks[k].Day + _drag.Delta;
                            if (!InHorizon(day)) continue;
                            bool warn = _drag.WarningDays.Contains(day);   // previewDailyTail: previewWarning
                            dc.DrawRoundedRectangle(warn ? T.GhostWarnFill : T.GhostFill, warn ? T.GhostWarnPen : T.GhostPen, BlockRect(_drag.Row, day), 2, 2);
                        }
                    }
                    DrawBadge(dc, _drag);
                }
                dc.Pop();
            }
        }

        private Rect BlockRect(int row, int day)
        {
            var layout = _owner.Layout;
            double top = RowScreenTop(row), rh = layout.RowHeight(row);
            return new Rect(DayScreenX(day) + 1, top + TimelineEditMath.BlockInset, Math.Max(1, layout.DayWidth - 2), Math.Max(2, rh - TimelineEditMath.BlockInset * 2));
        }

        /// <summary>"+3 días · jue 19/11" above the ghost of the pressed block (below the row near the header).</summary>
        private void DrawBadge(DrawingContext dc, DragState drag)
        {
            var model = _owner.Model;
            var layout = _owner.Layout;
            DateTime target = model.StartDate.Date.AddDays(drag.OriginDay + drag.Delta);
            string text = TimelineEditMath.DeltaText(drag.Delta) + " · " + TimelineLabels.Weekday(target) + " " + target.ToString("dd/MM", CultureInfo.InvariantCulture)
                          + (drag.Scope == TimelineMoveScope.Activity ? " · actividad completa" : string.Empty)
                          + (drag.Delta != 0 && drag.WarningText != null ? " · " + drag.WarningText : string.Empty);
            bool warn = drag.Delta != 0 && (drag.WarningText != null || drag.WarningDays.Count > 0);
            var ft = _owner.Text.Get(text, T.Bold, T.ChartFontSize, T.BadgeText);
            double w = ft.Width + 12, h = ft.Height + 4;
            double top = RowScreenTop(drag.Row);
            double y = top - h - 1;
            if (y < TimelineLayout.HeaderHeight + 1) y = top + layout.RowHeight(drag.Row) + 1;
            double x = DayScreenX(drag.OriginDay + drag.Delta);
            x = Math.Max(2, Math.Min(ActualWidth - w - 2, x));
            dc.DrawRoundedRectangle(warn ? T.BadgeWarnBack : T.BadgeBack, null, new Rect(x, y, w, h), 3, 3);
            dc.DrawText(ft, new Point(x + 6, y + 2));
        }

        #endregion

        #region Input

        private sealed class DragState
        {
            public int Row, Block, OriginDay, Delta;
            public TimelineMoveScope Scope;
            public System.Collections.Generic.List<int> Moved;
            public System.Collections.Generic.HashSet<int> WarningDays = new System.Collections.Generic.HashSet<int>();
            public string WarningText;
        }

        private DragState _drag;
        private bool _pressBlocked;
        private DispatcherTimer _autoScroll;

        /// <summary>A drag preview is showing.</summary>
        internal bool IsDragging => _drag != null;

        /// <summary>Current drag offset in days (0 when not dragging).</summary>
        internal int DragDelta => _drag?.Delta ?? 0;

        internal TimelineMoveScope DragScope => _drag?.Scope ?? TimelineMoveScope.Tail;

        /// <summary>Blocks the current drag carries.</summary>
        internal System.Collections.Generic.IReadOnlyList<int> DragMovedBlocks => _drag?.Moved ?? new System.Collections.Generic.List<int>();

        /// <summary>Days the capacity preview marks (empty when not dragging).</summary>
        internal System.Collections.Generic.IReadOnlyCollection<int> DragWarningDays =>
            _drag != null ? (System.Collections.Generic.IReadOnlyCollection<int>)_drag.WarningDays : Array.Empty<int>();

        internal string DragWarningText => _drag?.WarningText;

        internal void SetDragWarnings(System.Collections.Generic.IEnumerable<int> days, string text)
        {
            if (_drag == null) return;
            _drag.WarningDays = new System.Collections.Generic.HashSet<int>(days ?? Array.Empty<int>());
            _drag.WarningText = string.IsNullOrEmpty(text) ? null : text;
            _owner.RequestRedraw(TimelineLayers.Overlay);
        }

        /// <summary>Ask the host for the capacity preview of the current target (sync handlers set the warnings).</summary>
        private void PublishPreview()
        {
            var drag = _drag;
            drag.WarningDays.Clear();
            drag.WarningText = null;
            var args = _owner.RaiseDragPreview(true, drag.Row, drag.Block, drag.Scope, drag.Delta);
            if (_drag != drag) return;   // a handler ended the drag
            if (args.WarningDays.Count > 0 || args.WarningText != null) SetDragWarnings(args.WarningDays, args.WarningText);
        }

        /// <summary>Row and block under a point of this panel.</summary>
        internal TimelineHit HitTest(Point p)
        {
            var layout = _owner.Layout;
            if (layout == null || p.Y < TimelineLayout.HeaderHeight) return TimelineHit.None;
            return TimelineEditMath.HitTest(layout, _owner.Rows, p.X + _owner.OffsetX, p.Y - TimelineLayout.HeaderHeight + _owner.OffsetY, _owner.ShowCompleted);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _owner.OnViewportChanged();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Press(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointerMove(e.GetPosition(this), Keyboard.Modifiers);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Release(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_pressed) _owner.HideHover();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            if (_pressed) { e.Handled = true; return; }
            var p = e.GetPosition(this);
            var hit = HitTest(p);
            _owner.Focus();
            if (hit.Row >= 0) _owner.SelectRowFromInput(hit.Row, hit.Block);
            e.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            if (_pressed) { e.Handled = true; return; }
            var p = e.GetPosition(this);
            var hit = HitTest(p);
            if (hit.Block >= 0) _owner.OpenBlockMenu(hit.Row, hit.Block, this, p);
            e.Handled = true;
        }

        /// <summary>Left button down (also driven directly by tests).</summary>
        internal void Press(Point p)
        {
            _owner.Focus();
            _owner.HideHover();
            _owner.HideReason();
            var hit = HitTest(p);
            if (hit.Row >= 0) _owner.SelectRowFromInput(hit.Row, hit.Block);
            if (hit.Block >= 0)
            {
                _pressed = true;
                _dragStarted = false;
                _pressBlocked = false;
                _pressHit = hit;
                _pressPoint = p;
                CaptureMouse();
            }
        }

        /// <summary>Mouse move: drag threshold, drag update, or hover (cursor and tooltip).</summary>
        internal void PointerMove(Point p, ModifierKeys mods)
        {
            if (_pressed && !_dragStarted)
            {
                if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _dragStarted = true;
                _owner.RaiseBlockEvent(TimelineView.BlockDragStartedEvent, _pressHit, _pressPoint, this);
                var scope = Scope(mods);
                var row = _pressHit.Row < _owner.Rows.Count ? _owner.Rows[_pressHit.Row] : null;
                if (!_owner.IsEditable || !TimelineEditMath.CanMove(row, _pressHit.Block, scope, out _))
                {
                    _pressBlocked = _owner.IsEditable;
                    Cursor = _owner.IsEditable ? Cursors.No : null;
                    return;
                }
                _drag = new DragState
                {
                    Row = _pressHit.Row,
                    Block = _pressHit.Block,
                    OriginDay = row.Blocks[_pressHit.Block].Day,
                    Scope = scope,
                    Moved = TimelineEditMath.MovedBlocks(row.Blocks, _pressHit.Block, scope),
                };
                Cursor = Cursors.SizeWE;
                StartAutoScroll();
                PublishPreview();
            }
            if (_drag != null)
            {
                UpdateDrag(p, mods);
                return;
            }
            if (_pressed) return;
            var hit = HitTest(p);
            Cursor = HoverCursor(hit);
            _owner.UpdateHover(hit, this, p);
        }

        private static TimelineMoveScope Scope(ModifierKeys mods) =>
            (mods & ModifierKeys.Shift) != 0 ? TimelineMoveScope.Activity : TimelineMoveScope.Tail;

        private Cursor HoverCursor(TimelineHit hit)
        {
            if (hit.Block < 0) return null;
            if (!_owner.IsEditable) return Cursors.Hand;
            var row = _owner.Rows[hit.Row];
            return row.IsLocked || row.Blocks[hit.Block].IsCompleted ? Cursors.No : Cursors.Hand;
        }

        private void UpdateDrag(Point p, ModifierKeys mods)
        {
            var model = _owner.Model;
            var layout = _owner.Layout;
            if (model == null || layout == null) { CancelDrag(); return; }
            var scope = Scope(mods);
            bool changed = false;
            if (scope != _drag.Scope)
            {
                _drag.Scope = scope;
                _drag.Moved = TimelineEditMath.MovedBlocks(_owner.Rows[_drag.Row].Blocks, _drag.Block, scope);
                changed = true;
            }
            int delta = TimelineEditMath.DayDelta(_drag.OriginDay, p.X + _owner.OffsetX, layout.DayWidth, model.DayCount);
            if (delta != _drag.Delta) { _drag.Delta = delta; changed = true; }
            if (!changed) return;
            PublishPreview();
            _owner.RequestRedraw(TimelineLayers.Overlay);
        }

        /// <summary>Left button up: raise the move (drag), the click (no drag), or flash a locked block.</summary>
        internal void Release(Point p)
        {
            var drag = _drag;
            bool click = _pressed && !_dragStarted, blocked = _pressBlocked;
            var hit = _pressHit;
            EndPress();
            if (click) _owner.RaiseBlockEvent(TimelineView.BlockClickEvent, hit, _pressPoint, this);
            else if (blocked) _owner.ShowBlocked(hit.Row, new[] { _owner.Rows[hit.Row].Blocks[hit.Block].Day }, TimelineEditMath.CompletedWorkReason);
            else if (drag != null && drag.Delta != 0)
                _owner.RequestMove(drag.Row, drag.Block, drag.Scope, drag.Delta, TimelineEditOrigin.Drag, drag.Moved);
        }

        /// <summary>Escape or lost capture: drop the drag without raising anything.</summary>
        internal void CancelDrag()
        {
            if (!_pressed && _drag == null) return;
            EndPress();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (_pressed || _drag != null) EndPress();
        }

        private void EndPress()
        {
            bool hadDrag = _drag != null;
            _pressed = false;
            _dragStarted = false;
            _pressBlocked = false;
            _drag = null;
            _autoScroll?.Stop();
            Cursor = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (hadDrag)
            {
                _owner.RaiseDragPreview(false, -1, -1, TimelineMoveScope.Tail, 0);   // drop, Esc, lost capture: clear the preview
                _owner.RequestRedraw(TimelineLayers.Overlay);
            }
        }

        /// <summary>While dragging near the left or right edge, scroll the days (faster closer to the edge).</summary>
        private void StartAutoScroll()
        {
            if (_autoScroll == null)
            {
                _autoScroll = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
                _autoScroll.Tick += (s, e) =>
                {
                    if (_drag == null || !IsMouseCaptured) { _autoScroll.Stop(); return; }
                    var p = Mouse.GetPosition(this);
                    const double edge = 32;
                    double step = 0;
                    if (p.X < edge) step = -(edge - Math.Max(-edge, p.X)) / edge;
                    else if (p.X > ActualWidth - edge) step = (Math.Min(ActualWidth + edge, p.X) - (ActualWidth - edge)) / edge;
                    if (step == 0) return;
                    _owner.SetHorizontalOffset(_owner.OffsetX + step * Math.Max(6, _owner.DayWidth / 3));
                    UpdateDrag(p, Keyboard.Modifiers);
                };
            }
            _autoScroll.Start();
        }

        /// <summary>Shift pressed or released mid-drag switches between tail and whole activity.</summary>
        internal void RefreshDragScope()
        {
            if (_drag != null) UpdateDrag(Mouse.GetPosition(this), Keyboard.Modifiers);
        }

        #endregion
    }

    [Flags]
    internal enum TimelineLayers
    {
        None = 0,
        ChartBack = 1,
        Links = 2,
        Blocks = 4,
        Front = 8,
        Grid = 16,
        Overlay = 32,
        Selection = ChartBack | Grid | Overlay,
        All = ChartBack | Links | Blocks | Front | Grid | Overlay,
    }
}
