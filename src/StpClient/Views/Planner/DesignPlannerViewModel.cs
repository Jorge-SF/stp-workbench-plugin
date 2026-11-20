// Design-time and preview data only: the real PlannerScreenViewModel over a PlannerSession of the mockup's invented
// project (DesignSnapshot, no client data). tools/Preview-Planner.ps1 creates it with the parameterless constructor.
// The plugin never opens it: "Abrir planificador" always shows the user's project.

namespace Maptek.Vulcan.Stp.Views.Planner
{
    public sealed class DesignPlannerViewModel : PlannerScreenViewModel
    {
        public DesignPlannerViewModel()
            : base(DesignSnapshot.CreateSession(), null)
        {
        }
    }
}
