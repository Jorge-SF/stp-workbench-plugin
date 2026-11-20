// Workbench view model of the planner document. It only carries the IPlannerViewModel the screen binds to
// (PlannerScreenViewModel over the user's PlannerSession) and releases it when the document closes.

using System;
using System.Windows;
using Maptek.Workbench.Client;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    class PlannerDocumentViewModel : WorkbenchViewModelBase
    {
        private readonly IDisposable[] _owned;

        /// <param name="planner">What the screen binds to.</param>
        /// <param name="owned">Released when the document closes (the screen model, the session and its bridge client).</param>
        public PlannerDocumentViewModel(IPlannerViewModel planner, params IDisposable[] owned)
        {
            Planner = planner ?? throw new ArgumentNullException(nameof(planner));
            _owned = owned ?? Array.Empty<IDisposable>();
        }

        /// <summary>What PlannerContent binds to.</summary>
        public IPlannerViewModel Planner { get; }

        /// <summary>Disposes what the document owns; safe to call twice.</summary>
        public void Release()
        {
            foreach (var item in _owned)
            {
                try
                {
                    item?.Dispose();
                }
                catch (Exception)
                {
                    // closing the document must not fail on a bridge client that is already gone
                }
            }
        }

        protected override IWorkbenchView CreateView(Window parentWindow) => new PlannerView(this, parentWindow);
    }
}
