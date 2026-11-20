using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>
    /// The planner screen. Set DataContext to an <see cref="IPlannerViewModel"/>. The only code here keeps the
    /// bottom panel's row in step with <see cref="IPlannerViewModel.ShowCapacityPanel"/> (a GridSplitter writes a
    /// fixed row height, so hiding the panel must zero the row and showing it restores the height the user left).
    /// </summary>
    public partial class PlannerContent : UserControl
    {
        private IPlannerViewModel _vm;
        private GridLength _bottomHeight = new GridLength(190);

        public PlannerContent()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => Attach(e.NewValue as IPlannerViewModel);
        }

        private void Attach(IPlannerViewModel vm)
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = vm;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
            UpdateBottomPanel();
        }

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(IPlannerViewModel.ShowCapacityPanel))
                UpdateBottomPanel();
        }

        private void UpdateBottomPanel()
        {
            bool show = _vm == null || _vm.ShowCapacityPanel;
            bool shown = BottomTabs.Visibility == Visibility.Visible;
            if (show == shown) return;
            if (show)
            {
                BottomRow.Height = _bottomHeight;
                SplitterRow.Height = new GridLength(6);
                BottomTabs.Visibility = BottomSplitter.Visibility = Visibility.Visible;
            }
            else
            {
                _bottomHeight = BottomRow.Height;
                BottomRow.Height = SplitterRow.Height = new GridLength(0);
                BottomTabs.Visibility = BottomSplitter.Visibility = Visibility.Collapsed;
            }
        }
    }
}
