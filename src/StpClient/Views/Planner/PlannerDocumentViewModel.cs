// Workbench view model of the planner document. It only carries the IPlannerViewModel the screen binds to,
// so StpPlanner can hand in the real one without touching the view.

using System;
using System.Windows;
using Maptek.Workbench.Client;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    class PlannerDocumentViewModel : WorkbenchViewModelBase
    {
        public PlannerDocumentViewModel(IPlannerViewModel planner)
        {
            Planner = planner ?? throw new ArgumentNullException(nameof(planner));
        }

        /// <summary>What PlannerContent binds to.</summary>
        public IPlannerViewModel Planner { get; }

        protected override IWorkbenchView CreateView(Window parentWindow) => new PlannerView(this, parentWindow);
    }
}
