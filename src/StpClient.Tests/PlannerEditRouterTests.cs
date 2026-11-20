using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Timeline;
using Maptek.Vulcan.Stp.Timeline.Model;
using Maptek.Vulcan.Stp.Views.Planner;
using Xunit;

namespace Maptek.Vulcan.Stp.Client.Tests
{
    /// <summary>
    /// Each timeline request reaches the right PlannerSession command, refusals come back with the Spanish reason, and
    /// IsEditable follows the session's busy state. Requests are built from the blocks PlannerTimelineModel draws, the
    /// way TimelineView builds its TimelineEditEventArgs.
    /// </summary>
    public sealed class PlannerEditRouterTests : IDisposable
    {
        private readonly PlannerSession _session = DesignSnapshot.CreateSession();
        private readonly PlannerTimelineModel _model;
        private readonly List<ResourcePickRequest> _picks = new List<ResourcePickRequest>();
        private PickedResource _pickAnswer;
        private readonly PlannerEditRouter _router;

        public PlannerEditRouterTests()
        {
            _model = new PlannerTimelineModel(_session);
            _router = new PlannerEditRouter(_session, r => { _picks.Add(r); return _pickAnswer; });
        }

        public void Dispose()
        {
            _router.Dispose();
            _model.Dispose();
            _session.Dispose();
        }

        private TimelineRow Row(string key) => _model.Rows.Single(r => r.Key == key);

        private int[] Days(string key) => Row(key).Blocks.Select(b => b.Day).ToArray();

        private PlannerEditRequest Request(TimelineEditAction action, string key, int day, int delta = 0, TimelineMoveScope scope = TimelineMoveScope.Tail)
        {
            var block = Row(key).Blocks.Single(b => b.Day == day);
            return new PlannerEditRequest
            {
                Action = action, Scope = scope, ActivityKey = key, Tag = block.Tag, Date = _model.StartDate.AddDays(day), DayDelta = delta,
            };
        }

        [Fact]
        public void Drag_from_a_day_moves_the_tail_and_everything_follows_the_session()
        {
            Assert.Equal(new[] { 7, 8, 9, 10, 11 }, Days("REZ-1400-02"));
            int changed = 0;
            _model.Changed += (s, e) => changed++;

            var outcome = _router.Apply(Request(TimelineEditAction.Move, "REZ-1400-02", 9, 2));

            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(new[] { 7, 8, 11, 12, 13 }, Days("REZ-1400-02"));   // days 9.. land on 11..
            Assert.True(changed > 0);
            Assert.Equal(2, _session.Counters.Moved);
            Assert.Equal("REZ-1400-02", _session.SelectedActivityId);       // side panel shows what moved
            Assert.Equal("REZ-1400-02", _session.Selected.ActivityId);
            Assert.Equal(PlannerMessages.PendingChanges(_session.PendingChangeCount), _session.PendingChangesText);
            Assert.Equal(TimelineMoveKind.Direct, Row("REZ-1400-02").Move);
        }

        [Fact]
        public void Shift_drag_moves_the_whole_activity()
        {
            var outcome = _router.Apply(Request(TimelineEditAction.Move, "SRV-1450-01", 8, 1, TimelineMoveScope.Activity));
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(new[] { 8, 9, 10 }, Days("SRV-1450-01"));
        }

        [Fact]
        public void Engine_refusals_come_back_with_the_spanish_reason()
        {
            // REZ-1425-01 follows VOL-1425-01 (FS): one day earlier breaks the precedence.
            var outcome = _router.Apply(Request(TimelineEditAction.Move, "REZ-1425-01", 10, -1, TimelineMoveScope.Activity));

            Assert.False(outcome.Ok);
            Assert.Equal("El movimiento viola una precedencia entrante. Revise las actividades predecesoras.", outcome.Message);
            Assert.Equal(outcome.Message, _session.StatusMessage);
            Assert.True(_session.StatusIsError);
            Assert.Equal(new[] { 10, 11, 12, 13 }, Days("REZ-1425-01"));   // unchanged
        }

        [Fact]
        public void Locked_completed_work_is_refused()
        {
            var outcome = _router.Apply(Request(TimelineEditAction.Move, "REL-1400-01", 2, 1));
            Assert.False(outcome.Ok);
            Assert.Equal("El avance completado está protegido y no se puede mover.", outcome.Message);
        }

        [Fact]
        public void Defer_sends_the_rest_after_the_horizon()
        {
            var outcome = _router.Apply(Request(TimelineEditAction.DeferToNextPeriod, "REZ-1400-02", 10));
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(new[] { 7, 8, 9 }, Days("REZ-1400-02"));
        }

        [Fact]
        public void Split_and_stagger_inserts_gaps_after_the_day()
        {
            var request = Request(TimelineEditAction.SplitAndStagger, "REL-1400-01", 6);
            request.Divisions = 1;
            request.GapDays = 2;
            var outcome = _router.Apply(request);
            Assert.True(outcome.Ok, outcome.Message);
            var pending = Row("REL-1400-01").Blocks.Where(b => !b.IsCompleted).Select(b => b.Day).ToArray();
            Assert.Contains(5, pending);
            Assert.Contains(6, pending);
            Assert.DoesNotContain(7, pending);
        }

        [Fact]
        public void Undo_and_redo_use_the_session_history()
        {
            Assert.True(_router.Apply(Request(TimelineEditAction.Move, "SRV-1450-01", 8, 1, TimelineMoveScope.Activity)).Ok);
            Assert.True(_router.Apply(new PlannerEditRequest { Action = TimelineEditAction.Undo }).Ok);
            Assert.Equal(new[] { 7, 8, 9 }, Days("SRV-1450-01"));
            Assert.True(_router.Apply(new PlannerEditRequest { Action = TimelineEditAction.Redo }).Ok);
            Assert.Equal(new[] { 8, 9, 10 }, Days("SRV-1450-01"));
            Assert.False(_router.Apply(new PlannerEditRequest { Action = TimelineEditAction.Redo }).Ok);
        }

        [Fact]
        public void Exemption_toggles_and_the_menu_follows()
        {
            var items = DefaultMenu();
            _router.PrepareMenu("REZ-1425-01", Row("REZ-1425-01").Blocks[0].Tag, items);
            var exemption = items.Single(i => i.Action == TimelineEditAction.ToggleCapacityExemption);
            Assert.True(exemption.IsVisible);
            Assert.Equal(PlannerEditRouter.ExemptHeader, exemption.Header);

            Assert.True(_router.Apply(Request(TimelineEditAction.ToggleCapacityExemption, "REZ-1425-01", 10)).Ok);
            var segment = (DaySegment)Row("REZ-1425-01").Blocks[0].Tag;
            Assert.True(segment.IsCapacityExempt);
            Assert.Contains("exento", Row("REZ-1425-01").Blocks[0].ToolTip);
            Assert.False(_session.Capacity.Single(r => r.Type == "Rezagado").Cells[10].IsOver);   // 2.9k, 1.7k exempt

            items = DefaultMenu();
            _router.PrepareMenu("REZ-1425-01", Row("REZ-1425-01").Blocks[0].Tag, items);
            Assert.Equal(PlannerEditRouter.RemoveExemptionHeader, items.Single(i => i.Action == TimelineEditAction.ToggleCapacityExemption).Header);

            Assert.True(_router.Apply(Request(TimelineEditAction.ToggleCapacityExemption, "REZ-1425-01", 10)).Ok);
            Assert.False(((DaySegment)Row("REZ-1425-01").Blocks[0].Tag).IsCapacityExempt);
        }

        [Fact]
        public void Exempt_days_are_green_blocks_until_the_exemption_is_removed()
        {
            Assert.False(Row("REZ-1425-01").Blocks.Single(b => b.Day == 10).IsCapacityExempt);
            Assert.True(_router.Apply(Request(TimelineEditAction.ToggleCapacityExemption, "REZ-1425-01", 10)).Ok);
            Assert.True(Row("REZ-1425-01").Blocks.Single(b => b.Day == 10).IsCapacityExempt);
            Assert.All(Row("REZ-1425-01").Blocks.Where(b => b.Day != 10), b => Assert.False(b.IsCapacityExempt));
            Assert.True(_router.Apply(Request(TimelineEditAction.ToggleCapacityExemption, "REZ-1425-01", 10)).Ok);
            Assert.False(Row("REZ-1425-01").Blocks.Single(b => b.Day == 10).IsCapacityExempt);
        }

        [Fact]
        public void Drag_preview_marks_the_days_the_move_would_take_over_capacity()
        {
            // Rezagado goal 2,500 t: REZ-1400-02 (1,220 t/day, days 7..11) dragged +3 lands on REZ-1425-01 (1,700 t/day).
            // Days 10 and 11 are over already; 12 and 13 are new (on 14 REZ-1425-01 has only a partial day).
            Assert.Equal(new[] { 7, 8, 9, 10, 11 }, Days("REZ-1400-02"));
            var before = _session.State.Model;
            var request = Request(TimelineEditAction.Move, "REZ-1400-02", 7, 3);
            var warned = _router.PreviewDrag(request, true);
            Assert.Equal(new[] { 12, 13 }, warned.Days);

            var preview = _session.DragPreview;
            Assert.NotNull(preview);
            Assert.Equal("Capacidad excedida para Rezagado.", warned.Text);
            var expected = _session.Days.Where(d => preview.WarningDates.Contains(d.Date)).Select(d => d.Index).ToArray();
            Assert.Equal(expected, warned.Days);
            Assert.Contains(preview.Capacity.SelectMany(r => r.Cells), c => c.IsPreviewExcess);
            Assert.Same(before, _session.State.Model);   // only a preview
            Assert.Equal(new[] { 7, 8, 9, 10, 11 }, Days("REZ-1400-02"));

            // Drop / Esc: the drag ends and the preview goes.
            Assert.Same(PlannerDragWarnings.None, _router.PreviewDrag(request, false));
            Assert.Null(_session.DragPreview);

            // A move the engine refuses (out of the horizon) has no preview.
            Assert.Empty(_router.PreviewDrag(Request(TimelineEditAction.Move, "REZ-1400-02", 7, 400, TimelineMoveScope.Activity), true).Days);
            Assert.Null(_session.DragPreview);
        }

        [Fact]
        public void Assign_resource_asks_the_picker_and_applies_its_answer()
        {
            _pickAnswer = new PickedResource("Scoop LH-05");
            var outcome = _router.Apply(Request(TimelineEditAction.AssignResource, "REZ-1425-01", 11));

            Assert.True(outcome.Ok, outcome.Message);
            var pick = Assert.Single(_picks);
            Assert.Equal("Scoop LH-07", pick.ActivityResource);
            Assert.Null(pick.Current);
            Assert.Contains("Scoop LH-05", pick.Options);
            var day = (DaySegment)Row("REZ-1425-01").Blocks.Single(b => b.Day == 11).Tag;
            Assert.Equal("Scoop LH-05", day.Resource);
            Assert.True(day.HasResourceOverride);

            _pickAnswer = null;   // cancelled: nothing asked of the session
            Assert.Null(_router.Apply(Request(TimelineEditAction.AssignResource, "REZ-1425-01", 11)));
            Assert.Equal("Scoop LH-05", _session.AssignedResource(day.LogicalUnitId));
        }

        [Fact]
        public void Assign_resource_offers_manual_resources_and_undo_takes_it_back()
        {
            Assert.True(_session.AddManualResource().Ok);
            Assert.True(_session.RenameManualResource("manual:1", "Scoop LH-09").Ok);
            _pickAnswer = new PickedResource("Scoop LH-09");
            Assert.True(_router.Apply(Request(TimelineEditAction.AssignResource, "REZ-1425-01", 11)).Ok);

            Assert.Equal("Scoop LH-09", _picks.Single().Options.Last());
            var day = (DaySegment)Row("REZ-1425-01").Blocks.Single(b => b.Day == 11).Tag;
            Assert.Equal("Scoop LH-09", day.Resource);

            Assert.True(_router.Apply(new PlannerEditRequest { Action = TimelineEditAction.Undo }).Ok);   // Ctrl+Z
            Assert.Null(_session.AssignedResource(day.LogicalUnitId));
        }

        [Fact]
        public void Screen_models_route_picks_and_renames_to_the_session()
        {
            var picked = new List<ResourceChoice>();
            var vgs = new ResourceChoice("Scoop LH-07", "Scoop LH-07 (VGS)");
            var other = new ResourceChoice("Scoop LH-05", "Scoop LH-05");
            var selected = new SelectedActivity(picked.Add) { ResourceOptions = new[] { vgs, other } };
            selected.ShowResource(vgs);
            selected.Resource = null;   // the ComboBox clearing its selection does nothing
            selected.Resource = vgs;    // nor does the same entry
            selected.Resource = other;
            Assert.Equal(new[] { other }, picked);
            Assert.Same(vgs, selected.Resource);   // shown until the session answers (the panel is rebuilt)

            var renames = new List<string>();
            var manual = new ResourceItem(new ResourceDescriptor { Id = "manual:1", Name = "Equipo manual 1", IsManual = true }, (i, n) => renames.Add(i.Id + "=" + n), null);
            var fromVgs = new ResourceItem(new ResourceDescriptor { Id = "vgs:Jumbo 01", Name = "Jumbo 01" }, (i, n) => renames.Add(i.Id + "=" + n), null);
            manual.Name = "Camión 3";
            fromVgs.Name = "Otro";
            Assert.Equal(new[] { "manual:1=Camión 3" }, renames);
            Assert.Equal("Jumbo 01", fromVgs.Name);
        }

        [Fact]
        public void Without_a_picker_assign_is_hidden()
        {
            using var router = new PlannerEditRouter(_session, null);
            var items = DefaultMenu();
            router.PrepareMenu("REZ-1425-01", Row("REZ-1425-01").Blocks[0].Tag, items);
            Assert.False(items.Single(i => i.Action == TimelineEditAction.AssignResource).IsVisible);
            Assert.Null(router.Apply(Request(TimelineEditAction.AssignResource, "REZ-1425-01", 10)));
        }

        [Fact]
        public void Requests_without_a_day_are_refused()
        {
            var outcome = _router.Apply(new PlannerEditRequest { Action = TimelineEditAction.DeferToNextPeriod, ActivityKey = "REZ-1425-01" });
            Assert.False(outcome.Ok);
            Assert.Equal(PlannerEditRouter.NoDayMessage, outcome.Message);
        }

        [Fact]
        public async Task Editing_is_off_while_the_session_is_busy()
        {
            var bridge = new BlockingBridge();
            var config = new BridgeConfig("http://127.0.0.1:50000", new string('t', 32), DesignSnapshot.ProjectKey, new string('b', 32), "En vivo.");
            using var session = DesignSnapshot.CreateSession(config, bridge);
            using var model = new PlannerTimelineModel(session);
            using var router = new PlannerEditRouter(session, null);
            var raised = new List<bool>();
            router.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(PlannerEditRouter.IsEditable)) raised.Add(router.IsEditable); };
            Assert.True(router.IsEditable);

            var apply = session.ApplyToVgsAsync();   // waits on the bridge: busy
            Assert.True(session.IsBusy);
            Assert.False(router.IsEditable);

            var tag = model.Rows.Single(r => r.Key == "SRV-1450-01").Blocks[0].Tag;
            var refused = router.Apply(new PlannerEditRequest
            {
                Action = TimelineEditAction.Move, Scope = TimelineMoveScope.Activity, ActivityKey = "SRV-1450-01", Tag = tag, DayDelta = 1,
            });
            Assert.False(refused.Ok);
            Assert.Equal(PlannerMessages.Busy, refused.Message);

            var items = DefaultMenu();
            router.PrepareMenu("SRV-1450-01", tag, items);
            Assert.All(items, i => Assert.False(i.IsEnabled));

            bridge.Release();
            await apply;
            Assert.True(router.IsEditable);
            Assert.Equal(new[] { false, true }, raised);
        }

        [Fact]
        public void Edits_stay_on_without_a_live_bridge()
        {
            Assert.False(_session.IsLive);
            Assert.True(_router.IsEditable);
        }

        private static List<TimelineMenuItem> DefaultMenu() => new List<TimelineMenuItem>
        {
            new TimelineMenuItem(TimelineEditAction.Move, "Mover desde este día: −1 día", TimelineMoveScope.Tail, -1),
            new TimelineMenuItem(TimelineEditAction.AssignResource, "Asignar equipo"),
            new TimelineMenuItem(TimelineEditAction.ToggleCapacityExemption, "Exentar Límite") { IsVisible = false },
            new TimelineMenuItem(TimelineEditAction.DeferToNextPeriod, "Mandar al siguiente periodo"),
            new TimelineMenuItem(TimelineEditAction.SplitAndStagger, "Dividir y desfasar"),
        };

        /// <summary>A bridge whose apply waits until released, then reports a cancellation.</summary>
        private sealed class BlockingBridge : IPlannerBridge
        {
            private readonly TaskCompletionSource<ApplyPackageSaved> _apply = new TaskCompletionSource<ApplyPackageSaved>();

            public void Release() => _apply.SetCanceled();

            public Task<SnapshotV2> GetSnapshotAsync(SnapshotV2 current, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<ApplyPackageSaved> SubmitApplyPackageAsync(ApplyPackage package, CancellationToken cancellationToken) => _apply.Task;
        }
    }
}
