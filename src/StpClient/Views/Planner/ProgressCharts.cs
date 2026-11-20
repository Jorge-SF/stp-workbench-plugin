// "Gráficos" tab: the browser planner's progress charts (short_term_planner_template_v2.html
// renderDailyProgressChart / renderTotalProgressChart), drawn with WPF in the same coordinates as the template's SVG
// (410 x 172 and 220 x 156 view boxes). Planned bars and rings always; actual bars and the outer ring with
// "Avance real" on.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Engine;
using Maptek.Vulcan.Stp.Planner;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>One legend entry: a swatch and a text.</summary>
    public sealed class ProgressLegendItem
    {
        /// <summary>Gets the swatch.</summary>
        public Brush Swatch { get; set; }

        /// <summary>Gets the text.</summary>
        public string Text { get; set; }
    }

    /// <summary>The charts of one type.</summary>
    public sealed class ProgressChartModel
    {
        /// <summary>Gets the type.</summary>
        public string Type { get; set; }

        /// <summary>Gets the dates of the daily chart.</summary>
        public IReadOnlyList<string> Dates { get; set; }

        /// <summary>Gets planned vs actual of the type.</summary>
        public TypeProgress Progress { get; set; }

        /// <summary>Gets a value indicating whether actual values are drawn.</summary>
        public bool ShowActual { get; set; }

        /// <summary>Gets the colour per equipment.</summary>
        public IReadOnlyDictionary<string, Color> ResourceColors { get; set; }

        /// <summary>Gets the daily chart legend.</summary>
        public IReadOnlyList<ProgressLegendItem> DailyLegend { get; set; }

        /// <summary>Gets the total chart legend (per equipment).</summary>
        public IReadOnlyList<ProgressLegendItem> TotalLegend { get; set; }

        /// <summary>Gets <c>Plan: x · Real: y</c>.</summary>
        public string TotalsText { get; set; }

        /// <summary>Gets <c>Interior: plan · Exterior: real</c>, or empty.</summary>
        public string RingsText { get; set; }

        /// <summary>Gets a value indicating whether <see cref="RingsText"/> is shown.</summary>
        public bool HasRingsText => !string.IsNullOrEmpty(RingsText);

        /// <summary>The colour of an equipment (template fallback <c>#8497ab</c>).</summary>
        /// <param name="resource">The equipment.</param>
        /// <returns>The colour.</returns>
        public Color ColorOf(string resource) => resource != null && ResourceColors != null && ResourceColors.TryGetValue(resource, out var c) ? c : ProgressCharts.Fallback;
    }

    /// <summary>Builds the chart models from <see cref="PlannerSession.ActualProgressSummary"/>.</summary>
    public static class ProgressCharts
    {
        /// <summary>Template <c>MAPTEK_GREEN</c>: planned bars without actual progress.</summary>
        public static readonly Color Green = Color.FromRgb(0x4B, 0x9E, 0x32);

        /// <summary>Planned bars with actual progress.</summary>
        public static readonly Color Planned = Color.FromRgb(0xD8, 0xDD, 0xE2);

        /// <summary>Equipment without a colour.</summary>
        public static readonly Color Fallback = Color.FromRgb(0x84, 0x97, 0xAB);

        private static readonly CompareInfo Es = CultureInfo.GetCultureInfo("es-MX").CompareInfo;

        /// <summary>One model per type of the summary.</summary>
        /// <param name="summary">The summary.</param>
        /// <param name="showActual">Whether the mode is on.</param>
        /// <returns>The models.</returns>
        public static IReadOnlyList<ProgressChartModel> Build(ActualProgressSummary summary, bool showActual)
        {
            if (summary == null || summary.Types.Count == 0) return Array.Empty<ProgressChartModel>();
            var resources = summary.Types.SelectMany(t => t.PlannedByResource.Concat(t.ActualByResource).Select(p => p.Key)).Distinct(StringComparer.Ordinal).ToList();
            var palette = TypePalette.Assign(resources);
            var colors = resources.ToDictionary(r => r, r => PlannerTimelineModel.ToColor(PlannerProjection.ColorOf(palette, r)), StringComparer.Ordinal);
            int Compare(string a, string b) => Es.Compare(a, b, CompareOptions.None);
            Brush Swatch(Color c)
            {
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                return brush;
            }

            return summary.Types.Select(t =>
            {
                var daily = new List<ProgressLegendItem> { new ProgressLegendItem { Swatch = Swatch(showActual ? Planned : Green), Text = "Programado" } };
                if (showActual)
                {
                    var used = t.ActualByResourceByDate.SelectMany(p => p.Value.Select(v => v.Key)).Distinct(StringComparer.Ordinal).ToList();
                    used.Sort(Compare);
                    daily.AddRange(used.Select(r => new ProgressLegendItem { Swatch = Swatch(colors[r]), Text = r }));
                }

                var planned = t.PlannedByResource.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var actual = t.ActualByResource.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var all = planned.Keys.Concat(actual.Keys).Distinct(StringComparer.Ordinal).ToList();
                all.Sort(Compare);
                string N(double v) => ActualProgressMessages.CompactNumber(v);
                return new ProgressChartModel
                {
                    Type = t.Type,
                    Dates = summary.Dates,
                    Progress = t,
                    ShowActual = showActual,
                    ResourceColors = colors,
                    DailyLegend = daily,
                    TotalLegend = all.Select(r =>
                    {
                        double p = planned.TryGetValue(r, out double pv) ? pv : 0, a = actual.TryGetValue(r, out double av) ? av : 0;
                        return new ProgressLegendItem { Swatch = Swatch(colors[r]), Text = showActual ? $"{r}: Plan {N(p)} · Real {N(a)}" : $"{r}: Plan {N(p)}" };
                    }).ToList(),
                    TotalsText = showActual ? $"Plan: {N(planned.Values.Sum())} · Real: {N(actual.Values.Sum())}" : $"Plan: {N(planned.Values.Sum())}",
                    RingsText = showActual ? "Interior: plan · Exterior: real" : string.Empty,
                };
            }).ToList();
        }

        /// <summary>Template <c>dailyChartAxis</c>: four round ticks.</summary>
        /// <param name="maximum">The largest value.</param>
        /// <returns>The axis maximum and ticks.</returns>
        public static (double Maximum, double[] Ticks) DailyAxis(double maximum)
        {
            double adjusted = maximum >= 100 ? maximum * 1.2 : maximum;
            double raw = Math.Max(1, adjusted / 4);
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double normalized = raw / magnitude;
            double multiplier = new[] { 1, 2, 2.5, 3, 5, 10 }.FirstOrDefault(v => v >= normalized);
            if (multiplier == 0) multiplier = 10;
            double step = multiplier * magnitude;
            return (step * 4, new[] { step, step * 2, step * 3, step * 4 });
        }

        /// <summary>Template <c>progressDateLabel</c>: <c>16/nov</c>.</summary>
        /// <param name="date">The day.</param>
        /// <returns>The label.</returns>
        public static string DateLabel(string date)
        {
            string[] months = { "ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic" };
            return date != null && date.Length == 10 && int.TryParse(date.Substring(5, 2), out int m) && m >= 1 && m <= 12
                ? date.Substring(8, 2) + "/" + months[m - 1] : string.Empty;
        }
    }

    /// <summary>"Cumplimiento diario" chart of one type (template <c>renderDailyProgressChart</c>).</summary>
    public sealed class DailyProgressChart : FrameworkElement
    {
        /// <summary>Identifies <see cref="Chart"/>.</summary>
        public static readonly DependencyProperty ChartProperty = DependencyProperty.Register(nameof(Chart), typeof(ProgressChartModel), typeof(DailyProgressChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private const double W = 410, H = 150, Left = 48, Right = W - 6, Top = 12, Baseline = 112;
        private static readonly Typeface Face = new Typeface("Segoe UI");
        private static readonly Brush Axis = Frozen(Color.FromRgb(0x84, 0x97, 0xAB));
        private static readonly Pen AxisPen = FrozenPen(Axis, 1, null);
        private static readonly Pen GridPen = FrozenPen(Frozen(Color.FromRgb(0xD9, 0xE2, 0xEC)), 1, new DashStyle(new double[] { 2, 2 }, 0));
        private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x4C, 0x5D, 0x70));

        /// <summary>Initializes a new instance of the <see cref="DailyProgressChart"/> class.</summary>
        public DailyProgressChart()
        {
            Width = W;
            Height = H;
        }

        /// <summary>Gets or sets the chart.</summary>
        public ProgressChartModel Chart
        {
            get => (ProgressChartModel)GetValue(ChartProperty);
            set => SetValue(ChartProperty, value);
        }

        /// <inheritdoc/>
        protected override void OnRender(DrawingContext dc)
        {
            var chart = Chart;
            if (chart?.Progress == null) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var dates = chart.Dates ?? Array.Empty<string>();
            double raw = 0;
            foreach (string d in dates)
            {
                var day = chart.Progress.Day(d);
                raw = Math.Max(raw, Math.Max(day?.Scheduled ?? 0, day?.Actual ?? 0));
            }

            var axis = ProgressCharts.DailyAxis(raw);
            double height = Baseline - Top;
            foreach (double tick in axis.Ticks)
            {
                double y = Baseline - tick / axis.Maximum * height;
                dc.DrawLine(GridPen, new Point(Left, y), new Point(Right, y));
                var label = Text(ActualProgressMessages.CompactNumber(tick), 7, LabelBrush, dpi);
                dc.DrawText(label, new Point(Left - 4 - label.Width, y - label.Height / 2));
            }

            dc.DrawLine(AxisPen, new Point(Left, Top), new Point(Left, Baseline));
            dc.DrawLine(AxisPen, new Point(Left, Baseline), new Point(Right, Baseline));
            double step = (Right - Left) / Math.Max(1, dates.Count);
            double lane = Math.Max(5, Math.Min(18, step * .62));
            var planned = Frozen(chart.ShowActual ? ProgressCharts.Planned : ProgressCharts.Green);
            for (int i = 0; i < dates.Count; i++)
            {
                var day = chart.Progress.Day(dates[i]);
                double x = Left + i * step + (step - lane) / 2;
                double scheduled = day?.Scheduled ?? 0;
                double scheduledHeight = scheduled / axis.Maximum * height;
                double scheduledWidth = chart.ShowActual ? Math.Max(2, lane * .47) : lane;
                if (scheduledHeight > 0) dc.DrawRectangle(planned, null, new Rect(x, Baseline - scheduledHeight, scheduledWidth, scheduledHeight));
                if (chart.ShowActual && day?.Actual is double actual)
                {
                    // template appendResourceBar: the bar is value / maximum * 96 tall, split by equipment
                    var values = chart.Progress.ActualByResourceOn(dates[i]);
                    double total = values.Sum(v => v.Value);
                    if (total > 0 && actual > 0 && axis.Maximum > 0)
                    {
                        double rendered = actual / axis.Maximum * 96, offset = 0;
                        double bx = x + scheduledWidth + Math.Max(.5, lane * .04), bw = Math.Max(2, lane * .47);
                        foreach (var v in values)
                        {
                            double h = rendered * v.Value / total;
                            if (!(h > 0)) continue;
                            dc.DrawRectangle(Frozen(chart.ColorOf(v.Key)), null, new Rect(bx, Baseline - offset - h, bw, h));
                            offset += h;
                        }
                    }
                }

                var date = Text(ProgressCharts.DateLabel(dates[i]), Math.Max(5.5, Math.Min(8, step * .72)), LabelBrush, dpi);
                double cx = x + lane / 2;
                dc.PushTransform(new RotateTransform(-90, cx, 136));
                dc.DrawText(date, new Point(cx - date.Width, 136 - date.Height / 2));
                dc.Pop();
            }
        }

        internal static FormattedText Text(string text, double size, Brush brush, double dpi) =>
            new FormattedText(text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, dpi);

        internal static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Brush brush, double thickness, DashStyle dash)
        {
            var pen = new Pen(brush, thickness);
            if (dash != null) pen.DashStyle = dash;
            pen.Freeze();
            return pen;
        }
    }

    /// <summary>"Gráficas generales" ring chart of one type (template <c>renderTotalProgressChart</c>): inner ring plan, outer ring real, per equipment.</summary>
    public sealed class TotalProgressChart : FrameworkElement
    {
        /// <summary>Identifies <see cref="Chart"/>.</summary>
        public static readonly DependencyProperty ChartProperty = DependencyProperty.Register(nameof(Chart), typeof(ProgressChartModel), typeof(TotalProgressChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly Brush TitleBrush = DailyProgressChart.Frozen(Color.FromRgb(0x17, 0x34, 0x5B));
        private static readonly Point Centre = new Point(74, 72);

        /// <summary>Initializes a new instance of the <see cref="TotalProgressChart"/> class.</summary>
        public TotalProgressChart()
        {
            Width = 150;
            Height = 146;
        }

        /// <summary>Gets or sets the chart.</summary>
        public ProgressChartModel Chart
        {
            get => (ProgressChartModel)GetValue(ChartProperty);
            set => SetValue(ChartProperty, value);
        }

        /// <inheritdoc/>
        protected override void OnRender(DrawingContext dc)
        {
            var chart = Chart;
            if (chart?.Progress == null) return;
            Ring(dc, chart, chart.Progress.PlannedByResource, 40);
            if (chart.ShowActual) Ring(dc, chart, chart.Progress.ActualByResource, 56);
            var title = DailyProgressChart.Text(chart.Type, 10, TitleBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            title.MaxTextWidth = 70;
            title.Trimming = TextTrimming.CharacterEllipsis;
            title.MaxLineCount = 1;
            dc.DrawText(title, new Point(Centre.X - Math.Min(70, title.Width) / 2, Centre.Y - title.Height / 2));
        }

        // template appendProgressRing: arcs from 12 o'clock, clockwise, one per equipment
        private static void Ring(DrawingContext dc, ProgressChartModel chart, IReadOnlyList<KeyValuePair<string, double>> values, double radius)
        {
            double total = values.Sum(v => v.Value);
            if (!(total > 0)) return;
            double start = 0;
            foreach (var v in values)
            {
                double sweep = v.Value / total * 360;
                if (sweep > 0)
                {
                    var pen = new Pen(DailyProgressChart.Frozen(chart.ColorOf(v.Key)), 13);
                    pen.Freeze();
                    if (sweep >= 359.999) dc.DrawEllipse(null, pen, Centre, radius, radius);
                    else dc.DrawGeometry(null, pen, Arc(radius, start, sweep));
                }

                start += sweep;
            }
        }

        private static Geometry Arc(double radius, double startDegrees, double sweepDegrees)
        {
            Point At(double degrees)
            {
                double a = (degrees - 90) * Math.PI / 180;
                return new Point(Centre.X + radius * Math.Cos(a), Centre.Y + radius * Math.Sin(a));
            }

            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(At(startDegrees), false, false);
                g.ArcTo(At(startDegrees + sweepDegrees), new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
            }

            geometry.Freeze();
            return geometry;
        }
    }
}
