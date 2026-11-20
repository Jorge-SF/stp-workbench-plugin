using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Maptek.Vulcan.Stp.Timeline;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>
    /// The planner screen. Set DataContext to an <see cref="IPlannerViewModel"/>. The code here forwards the
    /// timeline's edit requests to <see cref="IPlannerViewModel.Editor"/>, and keeps the bottom panel's row in step with
    /// <see cref="IPlannerViewModel.ShowCapacityPanel"/> (a GridSplitter writes a fixed row height, so hiding the panel
    /// must zero the row and showing it restores the height the user left).
    /// </summary>
    public partial class PlannerContent : UserControl
    {
        private IPlannerViewModel _vm;
        private GridLength _bottomHeight = new GridLength(190);

        public PlannerContent()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => Attach(e.NewValue as IPlannerViewModel);
            Timeline.MoveRequested += OnEditRequested;
            Timeline.DeferRequested += OnEditRequested;
            Timeline.StaggerRequested += OnEditRequested;
            Timeline.AssignResourceRequested += OnEditRequested;
            Timeline.CapacityExemptionRequested += OnEditRequested;
            Timeline.UndoRequested += OnEditRequested;
            Timeline.RedoRequested += OnEditRequested;
            Timeline.BlockMenuOpening += OnBlockMenuOpening;
            Timeline.ActualProgressRequested += (s, e) => _vm?.ActualProgress?.Apply(e);
            Timeline.ActualProgressQuery += (s, e) => _vm?.ActualProgress?.Answer(e);
            Timeline.DragPreviewChanged += OnDragPreviewChanged;
        }

        /// <summary>
        /// While dragging: the capacity preview of the move (ghosts on days that would exceed capacity turn red, the
        /// capacity panel shows the candidate's cells). The drag's end (drop, Esc) clears it.
        /// </summary>
        private void OnDragPreviewChanged(object sender, TimelineDragPreviewEventArgs e)
        {
            var editor = _vm?.Editor;
            if (editor == null) return;
            var warnings = editor.PreviewDrag(PlannerEditRequest.From(e), e.IsActive);
            e.SetWarnings(warnings.Days, warnings.Text);
            e.Handled = true;
        }

        /// <summary>
        /// The timeline only asks; the view model's Editor applies the request to the plan. A refusal on an activity
        /// flashes its blocks red with the Spanish reason; the status bar shows every outcome.
        /// </summary>
        private void OnEditRequested(object sender, TimelineEditEventArgs e)
        {
            var editor = _vm?.Editor;
            if (editor == null) return;
            var outcome = editor.Apply(PlannerEditRequest.From(e));
            if (outcome != null && !outcome.Ok && e.Row != null) e.Reject(outcome.Message);
            e.Handled = true;
        }

        private void OnBlockMenuOpening(object sender, TimelineMenuEventArgs e) =>
            _vm?.Editor?.PrepareMenu(e.Row?.Key, e.Block.Tag, e.Items);

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
