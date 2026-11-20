using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Maptek.Vulcan.Stp.Timeline.Layout;
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

        /// <summary>Screenshot demo: drag, drag-activity, blocked, menu, tooltip, stagger (with --screenshot; captures the screen).</summary>
        public string Demo;

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
                    case "--demo": o.Demo = v; break;
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
        private FakePlanner _planner;
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
            bar.Children.Add(new TextBlock
            {
                Text = "Arrastrar: mover desde este día · Mayús+arrastrar: actividad · Esc: cancelar · flechas: selección · " +
                       "Ctrl+←/→: ±1 día (Ctrl+Mayús: actividad) · Ctrl+Z / Ctrl+Y · clic derecho: menú",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
            });

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
            // Fake host: every request goes to the fake planner, which applies it or rejects it (red flash).
            TimelineEditEventHandler edit = (s, e) =>
            {
                _planner.Handle(e);
                _lastEvent = Describe(e) + " → " + _planner.LastOutcome;
                UpdateStatus();
            };
            _timeline.MoveRequested += edit;
            _timeline.DeferRequested += edit;
            _timeline.StaggerRequested += edit;
            _timeline.AssignResourceRequested += edit;
            _timeline.CapacityExemptionRequested += edit;
            _timeline.UndoRequested += edit;
            _timeline.RedoRequested += edit;
            _timeline.BlockMenuOpening += (s, e) => _planner.OnMenuOpening(e);
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
            _planner = new FakePlanner(model);
            _timeline.Model = model;
            _timeline.SelectedKey = selected;
            UpdateStatus();
        }

        private static string Describe(TimelineEditEventArgs e)
        {
            switch (e.Action)
            {
                case TimelineEditAction.Move:
                    string what = e.Scope == TimelineMoveScope.Tail ? "desde " + e.Date?.ToString("dd/MM", CultureInfo.InvariantCulture) : "actividad";
                    return $"Mover {what} {e.ActivityKey} {TimelineEditMath.DeltaText(e.DayDelta)}";
                case TimelineEditAction.SplitAndStagger:
                    return $"Dividir y desfasar {e.ActivityKey} ({e.Divisions} × {e.GapDays} d)";
                case TimelineEditAction.Undo:
                    return "Deshacer";
                case TimelineEditAction.Redo:
                    return "Rehacer";
                default:
                    return $"{e.Action} {e.ActivityKey} {e.Date:dd/MM}";
            }
        }

        private void UpdateStatus()
        {
            var model = _timeline.Model;
            int rows = model?.Rows.Count ?? 0;
            _status.Text = $"{rows:N0} filas · {model?.Links.Count ?? 0:N0} precedencias · seleccionada {_timeline.SelectedKey ?? "—"} · " +
                           $"último: {_lastEvent} · dibujo {_timeline.LastRenderMilliseconds:0.0} ms " +
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
            if (_options.Demo != null)
            {
                _timeline.IsHitTestVisible = false; // the real pointer must not disturb the staged state
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(StageDemo))));
                return;
            }
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
            // Drag preview: press a pending block of a visible row, then move the pointer across 12 days back and forth.
            _timeline.DayWidth = 44;
            _timeline.SetVerticalOffset(30.0 * 1000);
            _timeline.UpdateNow();
            var chart = _timeline.ChartPanel;
            int row = _timeline.Layout.RowAt(_timeline.OffsetY + 200);
            var pending = _timeline.Model.Rows[row].Blocks.FirstOrDefault(b => !b.IsCompleted);
            double y = TimelineLayout.HeaderHeight + _timeline.Layout.RowTop(row) - _timeline.OffsetY + 15;
            double x = pending.Day * _timeline.DayWidth + 20 - _timeline.OffsetX;
            _timeline.IsHitTestVisible = false;
            chart.Press(new Point(x, y));
            chart.PointerMove(new Point(x + 10, y), System.Windows.Input.ModifierKeys.None);
            Measure("drag preview frame (300 pointer moves over 12 days)", report, i => chart.PointerMove(new Point(x + (i % 12) * _timeline.DayWidth, y), System.Windows.Input.ModifierKeys.None));
            chart.CancelDrag();
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

        /// <summary>Put the control in an editing state for a screenshot, then capture the screen area of the window.</summary>
        private void StageDemo()
        {
            var chart = _timeline.ChartPanel;
            var model = (TimelineModel)_timeline.Model;
            int Row(string key) => model.Rows.FindIndex(r => r.Key == key);
            Point At(int row, int day) => new Point(day * _timeline.DayWidth + _timeline.DayWidth / 2 - _timeline.OffsetX,
                TimelineLayout.HeaderHeight + _timeline.Layout.RowTop(row) - _timeline.OffsetY + 15);
            int rez = Row("REZ-1425-01");
            switch (_options.Demo)
            {
                case "drag":
                    chart.Press(At(rez, 11));
                    chart.PointerMove(At(rez, 11) + new Vector(8, 0), ModifierKeys.None);
                    chart.PointerMove(At(rez, 13), ModifierKeys.None);
                    break;
                case "drag-activity":
                    chart.Press(At(rez, 11));
                    chart.PointerMove(At(rez, 11) + new Vector(8, 0), ModifierKeys.Shift);
                    chart.PointerMove(At(rez, 12), ModifierKeys.Shift);
                    break;
                case "blocked":
                    _timeline.Focus();
                    _timeline.FocusBlock("REZ-1425-01", 10);
                    _timeline.HandleKey(Key.Left, ModifierKeys.Control | ModifierKeys.Shift); // VOL-1425-01 ends on day 9
                    break;
                case "menu":
                    _timeline.Focus();
                    _timeline.OpenBlockMenu(rez, 0, chart, At(rez, 10) + new Vector(4, 6));
                    break;
                case "tooltip":
                    _timeline.ShowHoverNow(rez, 1, At(rez, 11));
                    break;
                case "stagger":
                    _timeline.OpenStagger("REZ-1425-01", 11, chart);
                    break;
            }
            UpdateStatus();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_options.Demo == "blocked" ? 60 : 500) };
            timer.Tick += (s, e) => { timer.Stop(); Capture(); };
            timer.Start();
        }

        /// <summary>Render the window, plus any open popup of the timeline (menu, popover, tooltip) at its place.</summary>
        private void Capture()
        {
            var element = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                foreach (var (popup, offset) in _timeline.OpenPopups())
                {
                    var at = _timeline.ChartPanel.TranslatePoint(offset, element);
                    var size = new Size(popup.ActualWidth, popup.ActualHeight);
                    if (size.Width <= 0 || size.Height <= 0) continue;
                    double x = Math.Max(0, Math.Min(element.ActualWidth - size.Width, at.X));
                    double y = Math.Max(0, Math.Min(element.ActualHeight - size.Height, at.Y));
                    dc.DrawRectangle(new VisualBrush(popup) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                        null, new Rect(new Point(x, y), size));
                }
            }
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(_options.Screenshot));
            using (var file = File.Create(_options.Screenshot)) encoder.Save(file);
            Close();
        }
    }
}
