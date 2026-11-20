using System.Windows;
using Maptek.Workbench.Client;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    partial class PlannerView : WorkbenchViewBase
    {
        public PlannerView(PlannerDocumentViewModel viewModel, Window parentWindow)
        {
            DataContext = ViewModel = viewModel;
            InitializeComponent();
        }

        public PlannerDocumentViewModel ViewModel { get; }
    }
}
