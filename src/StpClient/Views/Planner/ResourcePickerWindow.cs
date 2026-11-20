// "Asignar equipo" (timeline block menu): picks the equipment of one day, like the browser's resource popover
// ("Predeterminado de actividad" first, then the project's VGS resources). Built in code: a small modal window.

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Res = Maptek.Vulcan.Stp.Resources.Resources;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    internal sealed class ResourcePickerWindow : Window
    {
        private readonly ComboBox _choice = new ComboBox { MinWidth = 280, Margin = new Thickness(0, 6, 0, 0) };

        private ResourcePickerWindow(ResourcePickRequest request)
        {
            Title = Res.Planner_PickResourceTitle;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock { Text = request.ActivityLabel + " · " + request.DateText, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock { Text = Res.Planner_PickResourceNote, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Margin = new Thickness(0, 4, 0, 0) });

            string defaultText = string.Format(Res.Planner_PickResourceDefault, request.ActivityResource ?? "—");
            _choice.Items.Add(defaultText);
            foreach (var option in request.Options ?? Enumerable.Empty<string>()) _choice.Items.Add(option);
            _choice.SelectedItem = request.Current != null && _choice.Items.Contains(request.Current) ? (object)request.Current : defaultText;
            root.Children.Add(_choice);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = Res.Planner_PickResourceOk, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = Res.Settings_Cancel, IsCancel = true, MinWidth = 80 };
            ok.Click += (s, e) => DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;
        }

        /// <summary>Shows the picker; null when cancelled, else the equipment (null resource = the activity's own).</summary>
        public static PickedResource Show(Window owner, ResourcePickRequest request)
        {
            var window = new ResourcePickerWindow(request);
            if (owner != null) window.Owner = owner;
            if (window.ShowDialog() != true) return null;
            return new PickedResource(window._choice.SelectedIndex <= 0 ? null : window._choice.SelectedItem as string);
        }
    }
}
