using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using T = Maptek.Vulcan.Stp.Timeline.Rendering.TimelineTheme;

namespace Maptek.Vulcan.Stp.Timeline.Rendering
{
    /// <summary>
    /// The chart: day header, weekend shading, row bands, precedence links, day blocks and the "completado hasta"
    /// line. Four DrawingVisual layers (back, links, blocks, front) so a selection change or the links toggle only
    /// redraws its own layer. Every layer draws only the visible rows x visible days.
    /// </summary>
    internal sealed class TimelineChartPanel : LayeredPanel
    {
        internal const int BackLayer = 0, LinkLayer = 1, BlockLayer = 2, FrontLayer = 3;
        private const double BlockInset = 6; // mockup: y + 6, height RH - 12

        private readonly TimelineView _owner;
        private Point _pressPoint;
        private TimelineHit _pressHit;
        private bool _pressed, _dragStarted;

        public TimelineChartPanel(TimelineView owner) : base(4)
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
                    for (int k = FirstBlockAtOrAfter(blocks, days.First); k < blocks.Count; k++)
                    {
                        var b = blocks[k];
                        if (b.Day >= days.End) break;
                        if (b.IsCompleted && !showCompleted) continue;
                        var rect = new Rect(DayScreenX(b.Day) + 1, top + BlockInset, Math.Max(1, dw - 2), bh);
                        dc.DrawRoundedRectangle(b.IsCompleted ? T.Hatch : fill, b.IsCompleted ? null : movePen, rect, 2, 2);
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

        /// <summary>Index of the first block with Day &gt;= day (blocks are sorted by day).</summary>
        internal static int FirstBlockAtOrAfter(System.Collections.Generic.IReadOnlyList<TimelineBlock> blocks, int day)
        {
            int lo = 0, hi = blocks.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (blocks[mid].Day < day) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>Row and block under a point of this panel.</summary>
        internal TimelineHit HitTest(Point p)
        {
            var layout = _owner.Layout;
            if (layout == null || p.Y < TimelineLayout.HeaderHeight) return TimelineHit.None;
            double y = p.Y - TimelineLayout.HeaderHeight + _owner.OffsetY;
            int row = layout.RowAt(y);
            if (row < 0) return TimelineHit.None;
            var r = _owner.Rows[row];
            int blockIndex = -1;
            if (r.Kind == TimelineRowKind.Activity && r.Blocks != null && r.Blocks.Count > 0)
            {
                double inRow = y - layout.RowTop(row);
                double inset = BlockInset - 2; // a little forgiving
                if (inRow >= inset && inRow <= layout.RowHeight(row) - inset)
                {
                    int day = layout.XToDay(p.X + _owner.OffsetX);
                    int k = FirstBlockAtOrAfter(r.Blocks, day);
                    if (k < r.Blocks.Count && r.Blocks[k].Day == day && (_owner.ShowCompleted || !r.Blocks[k].IsCompleted))
                        blockIndex = k;
                }
            }
            return new TimelineHit(row, blockIndex);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _owner.OnViewportChanged();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _owner.Focus();
            var p = e.GetPosition(this);
            var hit = HitTest(p);
            if (hit.Row >= 0) _owner.SelectRowFromInput(hit.Row, p, this);
            if (hit.Block >= 0)
            {
                _pressed = true;
                _dragStarted = false;
                _pressHit = hit;
                _pressPoint = p;
                CaptureMouse();
            }
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            if (_pressed && !_dragStarted)
            {
                if (Math.Abs(p.X - _pressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(p.Y - _pressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance)
                {
                    _dragStarted = true;
                    _owner.RaiseBlockEvent(TimelineView.BlockDragStartedEvent, _pressHit, _pressPoint, this);
                }
                return;
            }
            if (!_pressed) Cursor = HitTest(p).Block >= 0 ? Cursors.Hand : null;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (_pressed && !_dragStarted) _owner.RaiseBlockEvent(TimelineView.BlockClickEvent, _pressHit, _pressPoint, this);
            EndPress();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _pressed = false;
            _dragStarted = false;
        }

        private void EndPress()
        {
            _pressed = false;
            _dragStarted = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
    }

    /// <summary>Result of a chart hit test: row index (-1 none) and block index in that row (-1 none).</summary>
    internal readonly struct TimelineHit
    {
        public static readonly TimelineHit None = new TimelineHit(-1, -1);

        public TimelineHit(int row, int block)
        {
            Row = row;
            Block = block;
        }

        public int Row { get; }
        public int Block { get; }
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
        Selection = ChartBack | Grid,
        All = ChartBack | Links | Blocks | Front | Grid,
    }
}
