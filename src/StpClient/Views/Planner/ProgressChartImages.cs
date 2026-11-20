// Excel "Gráficas" sheet: the "Gráficos" tab cards as PNG (template captureProgressChartImages / chartCardPngDataUrl):
// every "Cumplimiento diario" card first, then every "Gráficas generales" card, each with its title, chart and legend
// on white. Drawn off screen with the same chart elements as the tab.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Maptek.Vulcan.Stp.Bridge.Contracts;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>Renders the progress chart cards for the Excel export.</summary>
    public static class ProgressChartImages
    {
        private const double Scale = 2;   // 192 dpi, sharp in Excel at the exporter's 0.8 scale

        private static readonly Brush Title = Frozen(Color.FromRgb(0x17, 0x34, 0x5B));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x4C, 0x5D, 0x70));

        /// <summary>One PNG per card (at most <see cref="ExcelExportRequest.MaxChartImages"/>). Call on the UI thread.</summary>
        /// <param name="charts">The chart models (<see cref="ProgressCharts.Build"/>).</param>
        /// <returns>The images.</returns>
        public static IReadOnlyList<ExcelExportChartImage> Render(IReadOnlyList<ProgressChartModel> charts)
        {
            if (charts == null || charts.Count == 0) return Array.Empty<ExcelExportChartImage>();
            var images = new List<ExcelExportChartImage>();
            foreach (var chart in charts) Add(images, chart.Type, DailyCard(chart));
            foreach (var chart in charts) Add(images, chart.Type, TotalCard(chart));
            return images.Take(ExcelExportRequest.MaxChartImages).ToList();
        }

        private static void Add(List<ExcelExportChartImage> images, string title, FrameworkElement card)
        {
            byte[] png = ToPng(card);
            if (png != null && png.Length <= ExcelExportRequest.MaxPngBytes) images.Add(new ExcelExportChartImage(title ?? string.Empty, png));
        }

        private static FrameworkElement DailyCard(ProgressChartModel chart)
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(chart.Type));
            panel.Children.Add(new DailyProgressChart { Chart = chart });
            panel.Children.Add(Legend(chart.DailyLegend, 410, Orientation.Horizontal));
            return Card(panel);
        }

        private static FrameworkElement TotalCard(ProgressChartModel chart)
        {
            var text = new StackPanel { Margin = new Thickness(6, 4, 0, 0), Width = 160 };
            text.Children.Add(new TextBlock { Text = chart.TotalsText, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Title });
            if (chart.HasRingsText) text.Children.Add(new TextBlock { Text = chart.RingsText, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(Legend(chart.TotalLegend, 160, Orientation.Vertical));
            var row = new DockPanel();
            var ring = new TotalProgressChart { Chart = chart };
            DockPanel.SetDock(ring, Dock.Left);
            row.Children.Add(ring);
            row.Children.Add(text);
            var panel = new StackPanel();
            panel.Children.Add(Heading(chart.Type));
            panel.Children.Add(row);
            return Card(panel);
        }

        private static TextBlock Heading(string text) =>
            new TextBlock { Text = text ?? string.Empty, FontWeight = FontWeights.Bold, FontSize = 11, Foreground = Title, Margin = new Thickness(0, 0, 0, 2) };

        private static FrameworkElement Legend(IEnumerable<ProgressLegendItem> items, double maxWidth, Orientation orientation)
        {
            Panel panel = orientation == Orientation.Horizontal ? new WrapPanel { MaxWidth = maxWidth } : (Panel)new StackPanel { MaxWidth = maxWidth };
            foreach (var item in items ?? Enumerable.Empty<ProgressLegendItem>())
            {
                var entry = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 2) };
                entry.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = item.Swatch, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                entry.Children.Add(new TextBlock { Text = item.Text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth - 14 });
                panel.Children.Add(entry);
            }

            return panel;
        }

        private static FrameworkElement Card(UIElement content) =>
            new Border { Background = Brushes.White, Padding = new Thickness(6, 4, 6, 4), Child = content };

        private static byte[] ToPng(FrameworkElement card)
        {
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = card.DesiredSize;
            if (!(size.Width > 0) || !(size.Height > 0)) return null;
            card.Arrange(new Rect(size));
            card.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(card);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
