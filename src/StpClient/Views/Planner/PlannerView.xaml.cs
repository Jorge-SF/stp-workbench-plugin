using System;
using System.Windows;
using Maptek.Workbench.Client;
using Res = Maptek.Vulcan.Stp.Resources.Resources;

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

        /// <summary>
        /// Changes not applied to VGS would be lost: ask first. On the view's UI thread (during shut-down Workbench asks
        /// from a worker thread; the question and the session belong to the UI thread).
        /// </summary>
        public override bool CanViewClose()
        {
            if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => CanViewClose());
            if (!(ViewModel.Planner is PlannerScreenViewModel screen) || !screen.Session.HasPendingChanges) return true;
            string text = string.Format(Res.Planner_CloseWithChanges, screen.Session.PendingChangeCount);
            var owner = Window.GetWindow(this);
            var answer = owner != null
                ? MessageBox.Show(owner, text, Res.Msg_Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(text, Res.Msg_Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
        }

        /// <summary>
        /// The document is gone (not on CanViewClose: Workbench may still abort a close during shut-down): release the
        /// session and its bridge client. The base method runs on the UI thread, so this one says so (the build's
        /// calling-thread checker, Pit Design docs\Workbench_Plugin_Guide.md gotcha 8).
        /// </summary>
        [Maptek.Libraries.BindingSupport.RequiredCallingThread("UI")]
        protected override void NotifyAfterClose()
        {
            base.NotifyAfterClose();
            ViewModel.Release();
        }
    }
}
