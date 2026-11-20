using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using T = Maptek.Vulcan.Stp.Timeline.Rendering.TimelineTheme;

namespace Maptek.Vulcan.Stp.Timeline.Rendering
{
    /// <summary>"Avance real" drawing and input of the chart (template renderTimelineRow / beginActualProgressDrag …).</summary>
    internal sealed partial class TimelineChartPanel
    {
        private ProgressDragState _progressDrag;

        internal bool IsDraggingProgress => _progressDrag != null;

        internal int ProgressDragTarget => _progressDrag?.TargetDay ?? -1;

        internal TimelineProgressHit ProgressHit(Point p)
        {
            var layout = _owner.Layout;
            var model = _owner.Model;
            if (layout == null || model == null || p.Y < TimelineLayout.HeaderHeight) return TimelineProgressHit.None;
            return TimelineEditMath.ProgressHitTest(layout, _owner.Rows, p.X + _owner.OffsetX, p.Y - TimelineLayout.HeaderHeight + _owner.OffsetY, model.DayCount);
        }

        // ------------------------------------------------------------------ drawing

        private Rect ProgressRect(int row, int day, bool valueOnly)
        {
            var g = TimelineEditMath.ProgressBoxGeometry(_owner.Layout.DayWidth);
            double x = DayScreenX(day), top = RowScreenTop(row) + g.Top;
            return valueOnly ? new Rect(x + g.ValueX, top, g.ValueWidth, g.Height) : new Rect(x + g.GripX, top, g.GripWidth + g.ValueWidth, g.Height);
        }

        private void DrawProgressBoxes(DrawingContext dc, int row, IndexRange days)
        {
            var boxes = (_owner.Rows[row] as ITimelineProgressRow)?.ProgressBoxes;
            if (boxes == null || boxes.Count == 0) return;
            var layout = _owner.Layout;
            var g = TimelineEditMath.ProgressBoxGeometry(layout.DayWidth);
            var text = _owner.Text;

            // Continuity arrows: from each box's value to the next box's grip (template renderActualProgressLinks).
            var lines = new StreamGeometry();
            var arrows = new StreamGeometry();
            using (var gl = lines.Open())
            using (var ga = arrows.Open())
            {
                for (int k = 1; k < boxes.Count; k++)
                {
                    if (boxes[k].Day < days.First - 1 && boxes[k - 1].Day < days.First - 1) continue;
                    if (boxes[k - 1].Day >= days.End + 1) break;
                    var from = ProgressRect(row, boxes[k - 1].Day, true);
                    var to = ProgressRect(row, boxes[k].Day, false);
                    double y = from.Bottom + 1, x1 = from.Right, x2 = to.Left - 3;
                    if (x2 - x1 < 4) continue;
                    gl.BeginFigure(new Point(x1, y), false, false);
                    gl.LineTo(new Point(x2 - 4, y), true, false);
                    ga.BeginFigure(new Point(x2, y), true, true);
                    ga.LineTo(new Point(x2 - 5, y - 2.5), false, false);
                    ga.LineTo(new Point(x2 - 5, y + 2.5), false, false);
                }
            }

            lines.Freeze();
            arrows.Freeze();
            dc.DrawGeometry(null, T.ProgressContinuity, lines);
            dc.DrawGeometry(T.ProgressContinuityBrush, null, arrows);

            for (int k = 0; k < boxes.Count; k++)
            {
                var box = boxes[k];
                if (box.Day < days.First) continue;
                if (box.Day >= days.End) break;
                var value = ProgressRect(row, box.Day, true);
                if (g.GripWidth > 0)
                {
                    var grip = new Rect(value.X - g.GripWidth, value.Y, g.GripWidth, value.Height);
                    dc.DrawRectangle(T.ProgressBack, T.ProgressBorder, grip);
                    var arrow = text.Get("↕", T.Bold, T.ChartFontSize - 1, T.ProgressGrip);
                    dc.DrawText(arrow, new Point(grip.X + (grip.Width - arrow.Width) / 2, grip.Y + (grip.Height - arrow.Height) / 2));
                }

                dc.DrawRectangle(T.ProgressBack, T.ProgressBorder, value);
                if (box.Text.Length > 0)
                {
                    bool positive = box.Value.HasValue && box.Value.Value > 0;
                    var ft = text.Get(box.Text, positive ? T.Bold : T.Regular, T.ChartFontSize - 1, T.ProgressText);
                    if (ft.Width <= value.Width - 2) dc.DrawText(ft, new Point(value.X + (value.Width - ft.Width) / 2, value.Y + (value.Height - ft.Height) / 2));
                    else
                    {
                        var dots = text.Get("…", T.Bold, T.ChartFontSize - 1, T.ProgressText);
                        dc.DrawText(dots, new Point(value.X + (value.Width - dots.Width) / 2, value.Y + (value.Height - dots.Height) / 2));
                    }
                }
            }
        }

        private void DrawProgressDrag(DrawingContext dc, ProgressDragState drag)
        {
            int dayCount = _owner.Model?.DayCount ?? 0;
            foreach (int day in drag.Origins.Count > 0 ? drag.Origins : (IReadOnlyList<int>)new[] { drag.OriginDay })
            {
                if (day >= 0 && day < dayCount) dc.DrawRectangle(T.DragOrigin, null, ProgressRect(drag.Row, day, false));
            }

            foreach (int day in drag.Targets)
            {
                if (day < 0 || day >= dayCount) continue;
                var r = ProgressRect(drag.Row, day, false);
                dc.DrawRectangle(T.GhostFill, T.GhostPen, r);
            }
        }

        // ------------------------------------------------------------------ input

        /// <summary>Left press in "Avance real" mode: the grip starts a drag, the value opens the text box.</summary>
        private bool PressProgress(Point p)
        {
            var hit = ProgressHit(p);
            if (hit.Box < 0) return false;
            _owner.SelectRowFromInput(hit.Row, -1);
            if (!_owner.IsEditable) return true;
            if (hit.Part == TimelineProgressPart.Value)
            {
                _owner.BeginProgressEdit(hit.Row, hit.Box);
                return true;
            }

            var box = _owner.ProgressBox(hit.Row, hit.Box);
            if (box == null) return true;
            _owner.CancelProgressEdit();
            _progressDrag = new ProgressDragState { Row = hit.Row, OriginDay = box.Value.Day, TargetDay = box.Value.Day };
            CaptureMouse();
            Cursor = Cursors.SizeWE;
            _owner.RequestRedraw(TimelineLayers.Overlay);
            return true;
        }

        private void UpdateProgressDrag(Point p)
        {
            var drag = _progressDrag;
            if (!_owner.IsEditable || _owner.Layout == null) { EndProgressDrag(); return; }
            // template updateActualProgressDrag: only a day of the same row's track is a target
            var hit = ProgressHit(p);
            int target = hit.Row == drag.Row ? hit.Day : -1;
            if (target == drag.TargetDay) return;
            drag.TargetDay = target;
            var moves = target >= 0 && target != drag.OriginDay ? _owner.QueryProgressPreview(drag.Row, drag.OriginDay, target) : Array.Empty<(int Origin, int Target)>();
            drag.Origins = moves.Select(m => m.Origin).ToList();
            drag.Targets = moves.Select(m => m.Target).ToList();
            _owner.RequestRedraw(TimelineLayers.Overlay);
        }

        private void FinishProgressDrag(Point p)
        {
            UpdateProgressDrag(p);
            var drag = _progressDrag;
            EndProgressDrag();
            if (drag == null || drag.TargetDay < 0 || drag.TargetDay == drag.OriginDay) return;
            _owner.RequestProgress(TimelineProgressAction.Move, drag.Row, drag.OriginDay, drag.TargetDay, null);
        }

        private void EndProgressDrag()
        {
            if (_progressDrag == null) return;
            _progressDrag = null;
            Cursor = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
            _owner.RequestRedraw(TimelineLayers.Overlay);
        }

        private sealed class ProgressDragState
        {
            public int Row, OriginDay, TargetDay;
            public IReadOnlyList<int> Origins = Array.Empty<int>();
            public IReadOnlyList<int> Targets = Array.Empty<int>();
        }
    }
}
