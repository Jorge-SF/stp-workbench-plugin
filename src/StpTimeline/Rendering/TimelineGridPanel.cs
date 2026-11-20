using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using T = Maptek.Vulcan.Stp.Timeline.Rendering.TimelineTheme;

namespace Maptek.Vulcan.Stp.Timeline.Rendering
{
    /// <summary>Fixed left grid: ID · Actividad · Recurso · Plan, group header rows. Only visible rows are drawn.</summary>
    internal sealed class TimelineGridPanel : LayeredPanel
    {
        // Mockup: grid-template-columns 112px 1fr 120px 74px, 8 px padding.
        internal const double IdWidth = 112, ResourceWidth = 120, PlanWidth = 74, Pad = 8;

        private readonly TimelineView _owner;

        public TimelineGridPanel(TimelineView owner) : base(1)
        {
            _owner = owner;
            Cursor = Cursors.Arrow;
        }

        internal int LastDrawnRows { get; private set; }

        public void Redraw()
        {
            double w = ActualWidth, h = ActualHeight;
            var text = _owner.Text;
            using (var dc = Layers[0].RenderOpen())
            {
                dc.DrawRectangle(T.Panel, null, new Rect(0, 0, w, h));
                double hh = TimelineLayout.HeaderHeight;
                double nameWidth = Math.Max(20, w - IdWidth - ResourceWidth - PlanWidth);
                double resX = IdWidth + nameWidth, planX = resX + ResourceWidth;

                LastDrawnRows = 0;
                var layout = _owner.Layout;
                if (layout != null && h > hh)
                {
                    dc.PushClip(new RectangleGeometry(new Rect(0, hh, w, h - hh)));
                    var range = layout.VisibleRows(_owner.OffsetY, h - hh);
                    var rows = _owner.Rows;
                    for (int i = range.First; i < range.End; i++)
                    {
                        var row = rows[i];
                        double top = hh + layout.RowTop(i) - _owner.OffsetY, rh = layout.RowHeight(i);
                        if (row.Kind == TimelineRowKind.Group)
                        {
                            dc.DrawRectangle(T.GroupBack, null, new Rect(0, top, w, rh));
                            dc.DrawRoundedRectangle(T.ForColor(row.Color), null, new Rect(Pad, top + (rh - 10) / 2, 10, 10), 2, 2);
                            var title = text.Get(row.Name, T.Bold, T.GridFontSize, T.Text, Math.Max(1, w - Pad * 2 - 16 - 40));
                            dc.DrawText(title, new Point(Pad + 16, top + (rh - title.Height) / 2));
                            var count = text.Get("(" + row.GroupCount + ")", T.Regular, T.GridFontSize, T.Muted);
                            dc.DrawText(count, new Point(Pad + 16 + title.WidthIncludingTrailingWhitespace + 4, top + (rh - count.Height) / 2));
                        }
                        else
                        {
                            if (i == _owner.SelectedRowIndex) dc.DrawRectangle(T.Selected, null, new Rect(0, top, w, rh));
                            double lockW = row.IsLocked ? 14 : 0;
                            var id = text.Get(row.Id, T.Bold, T.GridFontSize, T.Text, IdWidth - Pad * 2 - lockW);
                            Cell(dc, id, Pad, top, rh);
                            if (row.IsLocked)
                            {
                                var glyph = text.Get(T.LockGlyph, T.Icons, 10, T.Muted);
                                dc.DrawText(glyph, new Point(Pad + id.WidthIncludingTrailingWhitespace + 4, top + (rh - glyph.Height) / 2));
                            }
                            Cell(dc, text.Get(row.Name, T.Regular, T.GridFontSize, T.Text, nameWidth - Pad * 2), IdWidth + Pad, top, rh);
                            Cell(dc, text.Get(row.Resource, T.Regular, T.GridFontSize, T.Muted, ResourceWidth - Pad * 2), resX + Pad, top, rh);
                            var plan = text.Get(row.Plan, T.Regular, T.GridFontSize, T.Text);
                            double planLeft = Math.Max(planX + Pad, w - Pad - plan.WidthIncludingTrailingWhitespace);
                            Cell(dc, plan, planLeft, top, rh);
                        }
                        double line = Snap(top + rh - 1);
                        dc.DrawLine(T.LineSoft, new Point(0, line), new Point(w, line));
                        LastDrawnRows++;
                    }
                    dc.Pop();
                }

                dc.DrawRectangle(T.HeaderBack, null, new Rect(0, 0, w, hh));
                Header(dc, "ID", Pad);
                Header(dc, "Actividad", IdWidth + Pad);
                Header(dc, "Recurso", resX + Pad);
                var planHeader = text.Get("Plan", T.Bold, T.GridFontSize, T.Muted);
                dc.DrawText(planHeader, new Point(w - Pad - planHeader.Width, (hh - planHeader.Height) / 2));
                dc.DrawLine(T.Line, new Point(0, Snap(hh - 1)), new Point(w, Snap(hh - 1)));
            }
        }

        private void Header(DrawingContext dc, string label, double x)
        {
            var ft = _owner.Text.Get(label, T.Bold, T.GridFontSize, T.Muted);
            dc.DrawText(ft, new Point(x, (TimelineLayout.HeaderHeight - ft.Height) / 2));
        }

        private static void Cell(DrawingContext dc, FormattedText ft, double x, double top, double rowHeight) =>
            dc.DrawText(ft, new Point(x, top + (rowHeight - ft.Height) / 2));

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _owner.OnViewportChanged();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var p = e.GetPosition(this);
            if (p.Y < TimelineLayout.HeaderHeight || _owner.Layout == null) return;
            int row = _owner.Layout.RowAt(p.Y - TimelineLayout.HeaderHeight + _owner.OffsetY);
            _owner.Focus();
            _owner.SelectRowFromInput(row, p, this);
            e.Handled = true;
        }
    }
}
