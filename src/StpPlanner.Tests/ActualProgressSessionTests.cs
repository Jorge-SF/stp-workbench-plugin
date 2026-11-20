using System.Linq;
using Maptek.Vulcan.Stp.Engine;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>"Avance real" in <see cref="PlannerSession"/>: mode, edits, undo/redo, storage, rows kept by a box, tab rows.</summary>
    public class ActualProgressSessionTests
    {
        private const string A = "A100";

        private static PlannerSession Open(MemoryHorizonStorage storage = null) =>
            new PlannerSession(ProjectionTests.ChainWithRates(), Golden.StaticConfig(), null, storage ?? new MemoryHorizonStorage(), new PlannerOptions { TypeColumns = new[] { "Type" } });

        private static string PlannedDay(PlannerSession session, string activity = A) =>
            session.Rows.First(r => r.ActivityId == activity).Segments.First(s => s.Status != SegmentStatus.Completed).Date;

        private static string FreeDay(PlannerSession session, string activity = A)
        {
            var planned = session.Rows.First(r => r.ActivityId == activity).Segments.Select(s => s.Date).ToList();
            return session.Days.Select(d => d.Date).Last(d => !planned.Contains(d));
        }

        [Fact]
        public void Mode_starts_off_and_edits_need_it()
        {
            var session = Open();
            Assert.False(session.ShowActualProgress);
            Assert.Empty(session.ActualProgressBoxes);
            var refused = session.SetActualProgress(A, PlannedDay(session), 5);
            Assert.False(refused.Ok);
            Assert.Equal(ActualProgressMessages.ModeOff, refused.Message);
        }

        [Fact]
        public void Planned_days_get_a_box_and_values_are_undoable()
        {
            var session = Open();
            session.ShowActualProgress = true;
            string day = PlannedDay(session);
            var box = session.ActualProgressBoxes[A].First(b => b.Date == day);
            Assert.True(box.IsScheduled);
            Assert.Null(box.Value);

            var outcome = session.SetActualProgress(A, day, 7.5);
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(ActualProgressMessages.ValueSaved, outcome.Message);
            Assert.Equal(7.5, session.ActualProgressValue(A, day));
            Assert.Equal("7.5", session.ActualProgressBoxes[A].First(b => b.Date == day).Text);
            Assert.False(session.HasPendingChanges);   // nothing for VGS
            Assert.True(session.CanUndo);
            Assert.StartsWith("Avance real de A100", session.UndoDescription);

            session.Undo();
            Assert.Null(session.ActualProgressValue(A, day));
            session.Redo();
            Assert.Equal(7.5, session.ActualProgressValue(A, day));

            Assert.False(session.SetActualProgress(A, day, -1).Ok);
            Assert.True(session.SetActualProgress(A, day, null).Ok);
            Assert.Null(session.ActualProgressValue(A, day));
            Assert.Contains(session.ActualProgressBoxes[A], b => b.Date == day);   // the box stays
        }

        [Fact]
        public void Undo_of_a_plan_move_keeps_later_progress_consistent()
        {
            var session = Open();
            session.ShowActualProgress = true;
            string day = PlannedDay(session);
            session.SetActualProgress(A, day, 3);
            var move = session.MoveActivity("D400", 1);
            Assert.True(move.Ok, move.Message);
            Assert.Equal(3, session.ActualProgressValue(A, day));   // a plan move carries the progress along
            session.Undo();   // undoes the move only
            Assert.Equal(3, session.ActualProgressValue(A, day));
            Assert.False(session.HasPendingChanges);
            session.Undo();
            Assert.Null(session.ActualProgressValue(A, day));
        }

        [Fact]
        public void Add_remove_and_storage()
        {
            var storage = new MemoryHorizonStorage();
            var session = Open(storage);
            session.ShowActualProgress = true;
            string free = FreeDay(session);
            string planned = PlannedDay(session);
            Assert.True(session.CanAddActualProgress(A, free));
            Assert.False(session.CanAddActualProgress(A, planned));
            Assert.Equal(ActualProgressMessages.Added, session.AddActualProgress(A, free).Message);
            Assert.Contains(session.ActualProgressBoxes[A], b => b.Date == free && !b.IsScheduled);
            session.SetActualProgress(A, free, 2);

            Assert.Equal(ActualProgressMessages.Removed, session.RemoveActualProgress(A, planned).Message);
            Assert.DoesNotContain(session.ActualProgressBoxes[A], b => b.Date == planned);
            Assert.True(session.CanAddActualProgress(A, planned));   // a planned day whose box was removed

            // Saved per project; the mode starts on when there is something.
            var reopened = Open(storage);
            Assert.True(reopened.ShowActualProgress);
            Assert.Equal(2, reopened.ActualProgressValue(A, free));
            Assert.DoesNotContain(reopened.ActualProgressBoxes[A], b => b.Date == planned);
        }

        [Fact]
        public void Drag_moves_the_box_and_its_cascade()
        {
            var session = Open();
            session.ShowActualProgress = true;
            string planned = PlannedDay(session);
            string free = FreeDay(session);
            session.SetActualProgress(A, planned, 4);
            var preview = session.PreviewActualProgressMove(A, planned, free);
            Assert.NotEmpty(preview);
            Assert.Equal(planned, preview[0].OriginDate);
            var outcome = session.MoveActualProgress(A, planned, free);
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(ActualProgressMessages.Moved, outcome.Message);
            Assert.Null(session.ActualProgressValue(A, planned));
            Assert.Equal(4, session.ActualProgressValue(A, preview[0].TargetDate));
            Assert.False(session.MoveActualProgress(A, "2000-01-01", free).Ok);
            session.Undo();
            Assert.Equal(4, session.ActualProgressValue(A, planned));
        }

        [Fact]
        public void Tab_rows_compare_actual_with_plan()
        {
            var session = Open();
            session.ShowActualProgress = true;
            string day = PlannedDay(session);
            var type = session.Rows.First(r => r.ActivityId == A).Type;
            session.SetActualProgress(A, day, 1);
            var row = session.ActualProgressRows.First(r => r.Type == type);
            Assert.Equal("Avance real · " + type, row.Label);
            var cell = row.Cells.First(c => c.Date == day);
            Assert.True(cell.IsUnder);
            Assert.StartsWith("1/", cell.Text);
            Assert.StartsWith("Real: 1 / Plan: ", row.TotalText);
            Assert.Contains(session.ActualProgressSummary.Types, t => t.Type == type && t.Day(day).Actual == 1);

            session.ShowActualProgress = false;
            Assert.Empty(session.ActualProgressRows);
            Assert.Null(session.ActualProgressSummary.Types.First(t => t.Type == type).Day(day).Actual);
        }

        [Fact]
        public void A_box_keeps_an_activity_on_the_timeline()
        {
            var storage = new MemoryHorizonStorage();
            var probe = Open(storage);
            var shown = probe.Rows.Select(r => r.ActivityId).ToList();
            var all = new[] { "A100", "B200", "C300", "D400" };
            string hidden = all.FirstOrDefault(id => !shown.Contains(id));
            if (hidden == null) return;   // every fixture activity is already in range
            var state = ActualProgress.SetSlot(ActualProgressState.Empty, hidden, probe.Days[0].Date, true);
            ActualProgressStore.Save(storage, probe.Snapshot.ProjectKey, state);
            var session = Open(storage);
            Assert.Contains(session.Rows, r => r.ActivityId == hidden);
        }

        [Fact]
        public void Store_round_trips_the_standalone_shape()
        {
            var state = ActualProgress.SetValue(ActualProgressState.Empty, "A", "2026-03-03", 2);
            state = ActualProgress.SetSlot(state, "A", "2026-03-04", true);
            state = ActualProgress.SetHiddenSlot(state, "A", "2026-03-05", true);
            var json = ActualProgressStore.ToJson(state);
            Assert.Equal(2.0, (double)json["actualProgressByDay"]["[\"A\",\"2026-03-03\"]"]);
            Assert.True(ActualProgressStore.FromJson(json).SameContent(state));
            Assert.Same(ActualProgressState.Empty, ActualProgressStore.Load(new MemoryHorizonStorage(), "x"));
        }

        [Fact]
        public void CompactNumber_matches_js()
        {
            Assert.Equal("3", ActualProgressMessages.CompactNumber(3));
            Assert.Equal("2.5", ActualProgressMessages.CompactNumber(2.5));
            Assert.Equal("2.3", ActualProgressMessages.CompactNumber(2.345));
            Assert.Equal("—", ActualProgressMessages.CompactNumber(null));
        }
    }
}
