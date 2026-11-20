using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline.Preview
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var options = Options.Parse(args);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new PreviewWindow(options);
            return app.Run(window);
        }
    }

    internal sealed class Options
    {
        public string Data = "mockup";
        public int Rows = 2000, Days = 90;
        public string Screenshot;
        public double Width = 1280, Height = 720;
        public int ScrollRow = -1;
        public double DayWidth = double.NaN;
        public string Bench;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i + 1 < args.Length; i += 2)
            {
                string v = args[i + 1];
                switch (args[i])
                {
                    case "--data": o.Data = v; break;
                    case "--rows": o.Rows = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--days": o.Days = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--screenshot": o.Screenshot = Path.GetFullPath(v); break;
                    case "--width": o.Width = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--height": o.Height = double.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--scroll-row": o.ScrollRow = int.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--bench": o.Bench = Path.GetFullPath(v); break;
                    case "--day-width": o.DayWidth = double.Parse(v, CultureInfo.InvariantCulture); break;
                }
            }
            return o;
        }
    }

    internal sealed class PreviewWindow : Window
    {
        private readonly Options _options;
        private readonly TimelineView _timeline = new TimelineView();
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(12, 3, 12, 3), Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0x48, 0x52)) };
        private readonly ComboBox _data = new ComboBox { Width = 170, Margin = new Thickness(4, 0, 12, 0) };
        private string _lastEvent = "—";
        private readonly System.Collections.Generic.List<double> _startupFrames = new System.Collections.Generic.List<double>();

        public PreviewWindow(Options options)
        {
            _options = options;
            Title = "STP UG – vista previa de la línea de tiempo";
            Width = options.Width;
            Height = options.Height;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF3));

            _data.Items.Add("Maqueta (12 actividades)");
            _data.Items.Add($"Carga ({options.Rows:N0} × {options.Days} días)");
            _data.SelectedIndex = options.Data == "stress" ? 1 : 0;
            _data.SelectionChanged += (s, e) => LoadData();

            var zoom = new Slider { Minimum = 8, Maximum = 120, Width = 160, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
            zoom.SetBinding(RangeBase.ValueProperty, new Binding(nameof(TimelineView.DayWidth)) { Source = _timeline, Mode = BindingMode.TwoWay });
            var links = Check("Precedencias", TimelineView.ShowLinksProperty);
            var done = Check("Avance completado", TimelineView.ShowCompletedProperty);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
            bar.Children.Add(new TextBlock { Text = "Datos", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(_data);
            bar.Children.Add(new TextBlock { Text = "Ancho de día", VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(zoom);
            bar.Children.Add(links);
            bar.Children.Add(done);
            bar.Children.Add(new TextBlock { Text = "Rueda: desplazar · Mayús+rueda: horizontal · Ctrl+rueda: zoom · ↑/↓: selección", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });

            var root = new DockPanel();
            var top = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xD9, 0xE0)), BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
            DockPanel.SetDock(top, Dock.Top);
            var status = new Border { Background = new SolidColorBrush(Color.FromRgb(0xDF, 0xE3, 0xE8)), Child = _status };
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(status);
            root.Children.Add(_timeline);
            Content = root;

            _timeline.BlockClick += (s, e) => { _lastEvent = $"clic {e.Row.Id} {e.Date:dd/MM} ({e.Block.Tag})"; UpdateStatus(); };
            _timeline.BlockDragStarted += (s, e) => { _lastEvent = $"arrastre iniciado {e.Row.Id} {e.Date:dd/MM}"; UpdateStatus(); };
            _timeline.SelectedRowChanged += (s, e) => UpdateStatus();
            _timeline.Rendered += (s, e) => { if (_startupFrames.Count < 5) _startupFrames.Add(_timeline.LastRenderMilliseconds); UpdateStatus(); };

            LoadData();
            if (!double.IsNaN(options.DayWidth)) _timeline.DayWidth = options.DayWidth;
            Loaded += OnLoaded;
        }

        private CheckBox Check(string label, DependencyProperty property)
        {
            var box = new CheckBox { Content = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(property.Name) { Source = _timeline, Mode = BindingMode.TwoWay });
            return box;
        }

        private void LoadData()
        {
            string selected;
            TimelineModel model = _data.SelectedIndex == 1
                ? FakeData.Stress(_options.Rows, _options.Days, out selected)
                : FakeData.Mockup(out selected);
            _timeline.Model = model;
            _timeline.SelectedKey = selected;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var model = _timeline.Model;
            int rows = model?.Rows.Count ?? 0;
            _status.Text = $"{rows:N0} filas · {model?.Links.Count ?? 0:N0} precedencias · seleccionada {_timeline.SelectedKey ?? "—"} · " +
                           $"último evento: {_lastEvent} · dibujo {_timeline.LastRenderMilliseconds:0.0} ms " +
                           $"({_timeline.DrawnRowCount} filas, {_timeline.DrawnBlockCount} bloques, {_timeline.DrawnLinkCount} líneas visibles)";
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_options.ScrollRow >= 0 && _timeline.Model != null && _options.ScrollRow < _timeline.Model.Rows.Count)
            {
                var key = _timeline.Model.Rows[_options.ScrollRow].Key;
                _timeline.SelectedKey = key;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => _timeline.ScrollIntoView(key)));
            }
            if (_options.Bench != null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RunBench));
                return;
            }
            if (_options.Screenshot == null) return;
            // Let layout and the coalesced redraw run, then capture and exit.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Capture))));
        }

        /// <summary>Times the control's redraw (our drawing code, not WPF composition) for scrolls and zooms.</summary>
        private void RunBench()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"rows={_timeline.Model.Rows.Count} links={_timeline.Model.Links.Count} days={_timeline.Model.DayCount} " +
                              $"viewport={_timeline.ActualWidth:0}x{_timeline.ActualHeight:0}");
            report.AppendLine("startup frames (cold: JIT, font loading, empty text cache): " +
                              string.Join(", ", _startupFrames.ConvertAll(t => t.ToString("0.0", CultureInfo.InvariantCulture) + " ms")));
            Measure("vertical scroll (300 x 6 rows)", report, i => _timeline.SetVerticalOffset(i * 30.0 * 6));
            Measure("horizontal scroll (300 x 1/3 day)", report, i => _timeline.SetHorizontalOffset(i * _timeline.DayWidth / 3));
            Measure("zoom (300 steps 12..120 px/day)", report, i => _timeline.ZoomAt(12 + (i % 100) * 1.08, 200));
            _timeline.DayWidth = 10; // all 90 days on screen: worst case for blocks
            Measure("vertical scroll, all 90 days visible (300 x 6 rows)", report, i => _timeline.SetVerticalOffset(i * 30.0 * 6));
            Measure("selection change (300)", report, i => _timeline.SelectedKey = _timeline.Model.Rows[(i * 7) % _timeline.Model.Rows.Count].Key);
            File.WriteAllText(_options.Bench, report.ToString());
            Close();
        }

        private void Measure(string name, System.Text.StringBuilder report, Action<int> step)
        {
            var times = new System.Collections.Generic.List<double>();
            for (int i = 0; i < 300; i++)
            {
                step(i);
                _timeline.UpdateNow();
                times.Add(_timeline.LastRenderMilliseconds);
            }
            times.Sort();
            report.AppendLine($"{name}: median {times[times.Count / 2]:0.00} ms, p95 {times[(int)(times.Count * 0.95)]:0.00} ms, max {times[times.Count - 1]:0.00} ms " +
                              $"(last frame: {_timeline.DrawnRowCount} rows, {_timeline.DrawnBlockCount} blocks, {_timeline.DrawnLinkCount} links)");
        }

        private void Capture()
        {
            var element = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(_options.Screenshot));
            using (var file = File.Create(_options.Screenshot)) encoder.Save(file);
            Close();
        }
    }
}
