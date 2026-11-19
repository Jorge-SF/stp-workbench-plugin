// STP UG > Configuración y acerca de...: the scripts folder setting, plus version and diagnostics.
// Built in code (no XAML): a small modal WPF window.

using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Maptek.Vulcan.Stp.Bridge;
using Microsoft.Win32;
using Res = Maptek.Vulcan.Stp.Resources.Resources;

namespace Maptek.Vulcan.Stp.Views
{
    internal sealed class SettingsWindow : Window
    {
        private readonly TextBox _folder = new TextBox { MinWidth = 420, Margin = new Thickness(0, 0, 6, 0) };
        private readonly TextBlock _inUse = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _scripts = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontFamily = new System.Windows.Media.FontFamily("Consolas") };

        private SettingsWindow(bool envisageAvailable)
        {
            Title = Res.Settings_Title;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(14) };

            root.Children.Add(new TextBlock { Text = Res.Settings_ScriptsFolder, Margin = new Thickness(0, 0, 0, 4) });
            var row = new DockPanel();
            var browse = new Button { Content = Res.Settings_Browse, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
            var reset = new Button { Content = Res.Settings_Default, Padding = new Thickness(10, 2, 10, 2) };
            DockPanel.SetDock(reset, Dock.Right);
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(reset);
            row.Children.Add(browse);
            row.Children.Add(_folder);
            root.Children.Add(row);
            root.Children.Add(_inUse);
            root.Children.Add(new TextBlock { Text = Res.Settings_Scripts, Margin = new Thickness(0, 6, 0, 0) });
            root.Children.Add(_scripts);

            root.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
            string running = EnvisageRunner.Running;
            var about = new[]
            {
                "STP UG · " + string.Format(Res.About_Version, AssemblyVersionInfo.VersionLabel + " (" + AssemblyVersionInfo.Version + ")"),
                string.Format(Res.About_Envisage, envisageAvailable ? Res.About_Available : Res.About_NotAvailable),
                running != null ? string.Format(Res.About_Running, running) : null,
                string.Format(Res.About_Logs, EnvisageRunner.RunFolder),
                string.Format(Res.About_Settings, StpSettings.FilePath),
            };
            foreach (var line in about.Where(l => l != null))
            {
                root.Children.Add(new TextBox { Text = line, IsReadOnly = true, BorderThickness = new Thickness(0), Background = null });
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var save = new Button { Content = Res.Settings_Save, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = Res.Settings_Cancel, IsCancel = true, MinWidth = 80 };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;

            _folder.Text = StpSettings.Load().ScriptsFolder;
            _folder.TextChanged += (s, e) => Refresh();
            browse.Click += (s, e) => Browse();
            reset.Click += (s, e) => _folder.Text = "";
            save.Click += (s, e) => Save();
            Refresh();
        }

        public static void ShowDialog(Window owner, bool envisageAvailable)
        {
            var w = new SettingsWindow(envisageAvailable);
            if (owner != null && owner.IsLoaded) w.Owner = owner;
            else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.ShowDialog();
        }

        private void Refresh()
        {
            string folder = StpScriptLocator.Resolve(_folder.Text);
            _inUse.Text = string.Format(Res.Settings_InUse, folder ?? Res.Settings_NotFound);
            _scripts.Text = string.Join(Environment.NewLine, StpScript.All.Select(s =>
                (folder != null && File.Exists(Path.Combine(folder, s.FileName)) ? "[OK] " : "[--] ") + s.FileName));
        }

        private void Browse()
        {
            var dlg = new OpenFileDialog
            {
                Title = Res.Settings_PickScript,
                Filter = "Python (*.py)|*.py",
                CheckFileExists = true,
            };
            string start = StpScriptLocator.Resolve(_folder.Text);
            if (start != null) dlg.InitialDirectory = start;
            if (dlg.ShowDialog(this) == true) _folder.Text = Path.GetDirectoryName(dlg.FileName);
        }

        private void Save()
        {
            string typed = _folder.Text.Trim();
            if (typed.Length > 0 && !StpScriptLocator.HasScripts(typed))
            {
                MessageBox.Show(this, string.Format(Res.Settings_Invalid, StpScript.Prepare.FileName), Res.Msg_Title,
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                new StpSettings { ScriptsFolder = typed }.Save();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, string.Format(Res.Settings_SaveFailed, ex.Message), Res.Msg_Title,
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
