// PDF: the template's print report (renderPrintReport) drawn with PdfWriter. A4, landscape from 15 visible days
// (printOrientationForDayCount), 8 mm margins, blocks of at most 31 days (partitionPrintDays). Each block has the
// heading (title, project, range), the "Actividad" + day header, one row per visible activity (a bar coloured by type
// with the day's scheduled amount; completed work grey; moved work outlined orange), the "Capacidad · tipo" rows
// and the type legend. Rows that do not fit continue on the next page under a repeated header.
// Not yet: the actual-progress rows and chart pages (renderPrintProgressChartPages); they need the "Avance real"
// mode, which the plugin does not have yet.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Maptek.Vulcan.Stp.Planner.Export
{
    /// <summary>Renders a <see cref="PlanExport"/> as a PDF.</summary>
    public static class PdfPlanReport
    {
        /// <summary>Template <c>partitionPrintDays</c> maximum.</summary>
        public const int DaysPerBlock = 31;

        /// <summary>Footer text of every page (template <c>print-fixed-footer</c>).</summary>
        public const string FooterText = "Maptek Mexico & Centro America";

        private const double A4Short = 595.28;
        private const double A4Long = 841.89;
        private const double Margin = 22.68;           // 8 mm
        private const double FooterHeight = 14;
        private const double HeaderRowHeight = 18;
        private const double RowHeight = 13;

        private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-MX");
        private static readonly PdfColor Ink = new PdfColor(0x17, 0x34, 0x5B);
        private static readonly PdfColor Muted = new PdfColor(0x4C, 0x5D, 0x70);
        private static readonly PdfColor Grid = new PdfColor(0xC9, 0xD3, 0xDD);
        private static readonly PdfColor HeaderFill = new PdfColor(0xDB, 0xE8, 0xF2);
        private static readonly PdfColor WeekendFill = new PdfColor(0xF3, 0xF5, 0xF8);
        private static readonly PdfColor AggregateFill = new PdfColor(0xED, 0xF4, 0xF8);
        private static readonly PdfColor OverFill = new PdfColor(0xF6, 0xD3, 0xD0);
        private static readonly PdfColor OverInk = new PdfColor(0xB0, 0x2A, 0x22);
        private static readonly PdfColor Completed = new PdfColor(0x9A, 0xA5, 0xB1);
        private static readonly PdfColor Moved = new PdfColor(0xE0, 0x7B, 0x00);

        /// <summary>Template <c>printOrientationForDayCount</c>.</summary>
        public static bool IsLandscape(int dayCount) => dayCount >= 15;

        /// <summary>Renders the report.</summary>
        /// <param name="export">The frozen view.</param>
        /// <param name="generatedAt">Time printed in the footer (local).</param>
        /// <returns>The PDF bytes.</returns>
        /// <exception cref="InvalidOperationException">No visible days or activities.</exception>
        public static byte[] Render(PlanExport export, DateTime generatedAt)
        {
            if (export == null) throw new ArgumentNullException(nameof(export));
            if (export.Days.Count == 0) throw new InvalidOperationException(ExportMessages.NoDays);
            if (export.Rows.Count == 0) throw new InvalidOperationException(ExportMessages.NoRows);

            bool landscape = IsLandscape(export.Days.Count);
            double width = landscape ? A4Long : A4Short;
            double height = landscape ? A4Short : A4Long;
            var pdf = new PdfWriter { Title = export.Title };

            for (int start = 0; start < export.Days.Count; start += DaysPerBlock)
            {
                int count = Math.Min(DaysPerBlock, export.Days.Count - start);
                new Block(pdf, export, start, count, width, height, landscape).Draw();
            }

            string stamp = "Generado " + generatedAt.ToString("dd/MM/yyyy HH:mm", Es);
            for (int i = 0; i < pdf.PageCount; i++)
            {
                pdf.SelectPage(i);
                double baseline = height - Margin + 2;
                pdf.Line(Margin, baseline - 9, width - Margin, baseline - 9, Grid);
                pdf.Text(Margin, baseline, FooterText, 7, Muted, true);
                pdf.TextCentered(width / 2, baseline, stamp, 7, Muted);
                pdf.TextRight(width - Margin, baseline, string.Format(Es, "Página {0} de {1}", i + 1, pdf.PageCount), 7, Muted);
            }

            return pdf.ToBytes();
        }

        private sealed class Block
        {
            private readonly PdfWriter pdf;
            private readonly PlanExport export;
            private readonly int first;
            private readonly int count;
            private readonly double width;
            private readonly double height;
            private readonly double activityWidth;
            private readonly double dayWidth;
            private readonly double dayFont;
            private double y;

            public Block(PdfWriter pdf, PlanExport export, int first, int count, double width, double height, bool landscape)
            {
                this.pdf = pdf;
                this.export = export;
                this.first = first;
                this.count = count;
                this.width = width;
                this.height = height;
                double usable = width - 2 * Margin;
                activityWidth = Math.Min(landscape ? 190 : 170, usable * 0.4);
                dayWidth = (usable - activityWidth) / count;
                dayFont = Math.Max(4.5, Math.Min(7, dayWidth / 3.4));
            }

            private double Bottom => height - Margin - FooterHeight;

            private IEnumerable<int> DayIndexes => Enumerable.Range(first, count);

            public void Draw()
            {
                NewPage(true);
                foreach (var row in export.Rows)
                {
                    EnsureRoom(RowHeight);
                    DrawActivity(row);
                }

                foreach (var capacity in export.Capacity)
                {
                    EnsureRoom(RowHeight);
                    DrawCapacity(capacity);
                }

                DrawLegend();
            }

            private void NewPage(bool firstOfBlock)
            {
                pdf.AddPage(width, height);
                y = Margin;
                var lastDay = export.Days[first + count - 1];
                string range = export.Days[first].Label + " — " + lastDay.Label;
                pdf.Text(Margin, y + 13, PdfWriter.Fit(export.Title, width - 2 * Margin - 160, 14, true), 14, Ink, true);
                pdf.TextRight(width - Margin, y + 12, range, 10, Ink, true);
                y += 17;
                string subtitle = string.IsNullOrEmpty(export.ProjectName) ? string.Empty : export.ProjectName;
                string meta = "Instantánea " + export.SnapshotTime.ToString("dd/MM/yyyy HH:mm", Es) + (firstOfBlock ? string.Empty : " · continuación");
                pdf.Text(Margin, y + 9, PdfWriter.Fit(subtitle, width - 2 * Margin - 180, 9), 9, Muted);
                pdf.TextRight(width - Margin, y + 9, meta, 7.5, Muted);
                y += 16;
                DrawHeaderRow();
            }

            private void EnsureRoom(double needed)
            {
                if (y + needed > Bottom) NewPage(false);
            }

            private double DayX(int index) => Margin + activityWidth + (index - first) * dayWidth;

            private void DrawHeaderRow()
            {
                pdf.FillRect(Margin, y, width - 2 * Margin, HeaderRowHeight, HeaderFill);
                pdf.Text(Margin + 3, y + 12, "Actividad", 8, Ink, true);
                foreach (int i in DayIndexes)
                {
                    var day = export.Days[i];
                    double x = DayX(i);
                    double centre = x + dayWidth / 2;
                    pdf.TextCentered(centre, y + 8, PdfWriter.Fit(day.Weekday ?? string.Empty, dayWidth - 1, dayFont - 0.5), dayFont - 0.5, Muted);
                    pdf.TextCentered(centre, y + 15, PdfWriter.Fit(day.Label, dayWidth - 1, dayFont, true), dayFont, Ink, true);
                    pdf.Line(x, y, x, y + HeaderRowHeight, Grid);
                }

                pdf.StrokeRect(Margin, y, width - 2 * Margin, HeaderRowHeight, Grid);
                y += HeaderRowHeight;
            }

            private void DrawWeekends(double top, double rowHeight)
            {
                foreach (int i in DayIndexes)
                {
                    if (export.Days[i].IsWeekend) pdf.FillRect(DayX(i), top, dayWidth, rowHeight, WeekendFill);
                }
            }

            private void DrawRowGrid(double top, double rowHeight)
            {
                foreach (int i in DayIndexes) pdf.Line(DayX(i), top, DayX(i), top + rowHeight, Grid);
                pdf.Line(Margin, top + rowHeight, width - Margin, top + rowHeight, Grid);
                pdf.Line(Margin, top, Margin, top + rowHeight, Grid);
                pdf.Line(width - Margin, top, width - Margin, top + rowHeight, Grid);
            }

            private void DrawActivity(PlanExportRow row)
            {
                DrawWeekends(y, RowHeight);
                double total = Enumerable.Range(first, count).Sum(i => row.Cells[i].Planned ?? 0);
                string totalText = total > 0 ? PlannerProjection.CompactQuantity(total) + (string.IsNullOrEmpty(row.Unit) ? string.Empty : " " + row.Unit) : string.Empty;
                double totalWidth = PdfWriter.MeasureText(totalText, 6);
                string label = row.ActivityId + " · " + row.Name;
                pdf.Text(Margin + 3, y + 9, PdfWriter.Fit(label, activityWidth - totalWidth - 9, 7), 7, PdfColor.Black);
                pdf.TextRight(Margin + activityWidth - 3, y + 9, totalText, 6, Muted);

                var typeColor = PdfColor.Parse(row.Color, Completed);
                foreach (int i in DayIndexes)
                {
                    var cell = row.Cells[i];
                    if (!cell.HasSegment) continue;
                    double x = DayX(i) + 1;
                    double w = dayWidth - 2;
                    double top = y + 2;
                    double h = RowHeight - 4;
                    var fill = cell.Status == SegmentStatus.Completed ? Completed : typeColor;
                    pdf.FillRect(x, top, w, h, fill);
                    if (cell.Status == SegmentStatus.Direct || cell.Status == SegmentStatus.Cascade)
                        pdf.StrokeRect(x, top, w, h, Moved, cell.Status == SegmentStatus.Direct ? 1.2 : 0.7);
                    if (cell.Shown.HasValue)
                    {
                        string text = PdfWriter.Fit(PlannerProjection.CompactQuantity(cell.Shown.Value), w - 1, Math.Min(6.5, dayFont));
                        pdf.TextCentered(x + w / 2, top + h - 2.2, text, Math.Min(6.5, dayFont), fill.Foreground());
                    }
                }

                DrawRowGrid(y, RowHeight);
                y += RowHeight;
            }

            private void DrawCapacity(PlanExportCapacity capacity)
            {
                pdf.FillRect(Margin, y, width - 2 * Margin, RowHeight, AggregateFill);
                pdf.FillRect(Margin + 2, y + 3, 6, RowHeight - 6, PdfColor.Parse(capacity.Color, Completed));
                pdf.Text(Margin + 11, y + 9, PdfWriter.Fit(capacity.Label, activityWidth - 14, 7, true), 7, Ink, true);
                foreach (int i in DayIndexes)
                {
                    var (_, over) = capacity.Cells[i];
                    if (over) pdf.FillRect(DayX(i), y, dayWidth, RowHeight, OverFill);
                    string text = PdfWriter.Fit(capacity.CellText(i), dayWidth - 1, Math.Min(6, dayFont), over);
                    pdf.TextCentered(DayX(i) + dayWidth / 2, y + 9, text, Math.Min(6, dayFont), over ? OverInk : Ink, over);
                }

                DrawRowGrid(y, RowHeight);
                y += RowHeight;
            }

            private void DrawLegend()
            {
                if (export.Legend.Count == 0) return;
                y += 6;
                EnsureRoom(12);
                double x = Margin;
                pdf.Text(x, y + 8, "Leyenda:", 7, Ink, true);
                x += PdfWriter.MeasureText("Leyenda:", 7, true) + 6;
                DrawLegendItem(ref x, "Completado", Completed, false);
                DrawLegendItem(ref x, "Movido", PdfColor.White, true);
                foreach (var (label, color) in export.Legend) DrawLegendItem(ref x, label, PdfColor.Parse(color, Completed), false);
                y += 12;
            }

            private void DrawLegendItem(ref double x, string label, PdfColor color, bool moved)
            {
                double itemWidth = 13 + PdfWriter.MeasureText(label, 7) + 10;
                if (x + itemWidth > width - Margin)
                {
                    y += 12;
                    EnsureRoom(12);
                    x = Margin;
                }

                pdf.FillRect(x, y + 2, 10, 7, color);
                pdf.StrokeRect(x, y + 2, 10, 7, moved ? Moved : Ink, moved ? 1.2 : 0.4);
                pdf.Text(x + 13, y + 8, label, 7, Muted);
                x += itemWidth;
            }
        }
    }
}
