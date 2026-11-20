using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>Rows, filters, counters, capacity and details over chain_fs (A100 →FS B200 →FS C300; D400 unlinked).</summary>
    public class ProjectionTests
    {
        private static readonly PlannerOptions TypeFromTypeColumn = new PlannerOptions { TypeColumns = new[] { "Type" } };

        /// <summary>chain_fs plus Rate/Unit/Nivel columns (presentation only: the engine ignores the grid).</summary>
        internal static SnapshotV2 ChainWithRates()
        {
            var raw = Golden.RawSnapshot("chain_fs");
            var extra = new Dictionary<string, (string Rate, string Unit, string Level)>
            {
                ["A100"] = ("10", "m", "N1"),
                ["B200"] = ("24", "pernos", "N1"),
                ["C300"] = ("6", "m3", "N1"),
                ["D400"] = ("10", "m", "N2"),
            };
            foreach (JObject activity in raw["activities"])
            {
                var (rate, unit, level) = extra[(string)activity["id"]];
                var grid = (JObject)activity["grid"];
                grid["Rate"] = rate;
                grid["Unit"] = unit;
                grid["Nivel"] = level;
            }

            return SnapshotV2.FromJson(raw);
        }

        internal static PlannerSession Session(SnapshotV2 snapshot = null, PlannerOptions options = null) =>
            new PlannerSession(snapshot ?? ChainWithRates(), Golden.StaticConfig(), null, new MemoryHorizonStorage(), options ?? TypeFromTypeColumn);

        [Fact]
        public void DefaultColumnsFollowTheBrowser()
        {
            var session = Session(Golden.Snapshot("chain_fs"), new PlannerOptions());
            var row = session.Rows.First(r => r.ActivityId == "A100");
            Assert.Equal("Desarrollo rampa N1", row.Name);
            Assert.Equal(ActivityCatalog.NoType, row.Type); // the browser reads only "Activity Type"
            Assert.Equal("Jumbo 01", row.Resource);
            Assert.Equal(ActivityCatalog.NoLevel, row.Level);
            Assert.Null(row.PlanQuantity);
            Assert.Equal(string.Empty, row.PlanText);
        }

        [Fact]
        public void HorizonDaysAndRange()
        {
            var session = Session();
            Assert.Equal("2026-03-02", session.RangeStart);
            Assert.Equal("2026-03-29", session.RangeFinish);
            Assert.Equal(28, session.DurationDays);
            Assert.Equal(28, session.Days.Count);
            Assert.Equal(("lun", "02/03", false), (session.Days[0].Weekday, session.Days[0].Label, session.Days[0].IsWeekend));
            Assert.True(session.Days[5].IsWeekend);
            Assert.Equal("sáb", session.Days[5].Weekday);
            Assert.True(session.CanEditRange);
        }

        [Fact]
        public void GroupsChipsAndCounters()
        {
            var session = Session();
            Assert.Equal(new[] { "Desarrollo", "Fortificación", "Shotcrete" }, session.TypeChips.Select(c => c.Type));
            Assert.Equal(new[] { 2, 1, 1 }, session.TypeChips.Select(c => c.Count));
            Assert.Equal(new[] { "Desarrollo", "Fortificación", "Shotcrete" }, session.Groups.Select(g => g.Label));
            Assert.Equal(new[] { "A100", "D400" }, session.Groups[0].Rows.Select(r => r.ActivityId));
            Assert.Equal((4, 4, 0, 0, false), (session.Counters.Activities, session.Counters.WithPending, session.Counters.Moved, session.Counters.Cascaded, session.Counters.HasChanges));

            session.Grouping = PlannerGrouping.Level;
            Assert.Equal(new[] { "N1", "N2" }, session.Groups.Select(g => g.Key));
            session.Grouping = PlannerGrouping.Resource;
            Assert.Equal(new[] { "Empernador 01", "Jumbo 01", "Jumbo 02", "Robot 01" }, session.Groups.Select(g => g.Key));
            session.Grouping = PlannerGrouping.None;
            Assert.Single(session.Groups);
            Assert.Equal(new[] { "A100", "D400", "B200", "C300" }, session.Rows.Select(r => r.ActivityId)); // by first visible start
        }

        [Fact]
        public void SortAndSearch()
        {
            var session = Session();
            session.Grouping = PlannerGrouping.None;
            session.Sort = PlannerSort.Id;
            session.SortDescending = true;
            Assert.Equal(new[] { "D400", "C300", "B200", "A100" }, session.Rows.Select(r => r.ActivityId));
            session.Sort = PlannerSort.Attribute;
            session.SortAttribute = "Rate";
            session.SortDescending = false;
            Assert.Equal(new[] { "C300", "A100", "D400", "B200" }, session.Rows.Select(r => r.ActivityId)); // 6, 10, 10, 24 (numeric, then snapshot order)
            session.Sort = PlannerSort.Source;
            Assert.Equal(new[] { "A100", "B200", "C300", "D400" }, session.Rows.Select(r => r.ActivityId));

            session.SearchText = "xc-2";
            Assert.Equal(new[] { "D400" }, session.Rows.Select(r => r.ActivityId));
            session.SearchText = "FORTIFICACION"; // case and accent insensitive
            Assert.Equal(new[] { "B200" }, session.Rows.Select(r => r.ActivityId));
            session.SearchText = "jumbo";
            Assert.Equal(new[] { "A100", "D400" }, session.Rows.Select(r => r.ActivityId));
            Assert.Equal(4, session.Counters.Activities); // counters ignore the search, like the browser metrics
        }

        [Fact]
        public void ChipsHideTypesFromRowsAndCapacity()
        {
            var session = Session();
            session.ToggleType("Shotcrete");
            Assert.False(session.TypeChips.Single(c => c.Type == "Shotcrete").IsVisible);
            Assert.DoesNotContain(session.Rows, r => r.Type == "Shotcrete");
            Assert.DoesNotContain(session.Capacity, c => c.Type == "Shotcrete");
            Assert.Equal(3, session.TypeChips.Count);
            session.ShowAllTypes();
            Assert.Contains(session.Rows, r => r.Type == "Shotcrete");
        }

        [Fact]
        public void SegmentsQuantitiesAndCapacity()
        {
            var session = Session();
            var a100 = session.Rows.Single(r => r.ActivityId == "A100");
            Assert.Equal(new[] { "2026-03-02", "2026-03-03", "2026-03-04" }, a100.Segments.Select(s => s.Date));
            Assert.Equal(new[] { 0, 1, 2 }, a100.Segments.Select(s => s.DayIndex));
            Assert.Equal(8.0 / 24, a100.Segments[0].LeftRatio, 9);
            Assert.Equal(16.0 / 24, a100.Segments[0].WidthRatio, 9);
            Assert.All(a100.Segments, s => Assert.Equal(SegmentStatus.Pending, s.Status));
            Assert.All(a100.Segments, s => Assert.True(s.CanMoveTail));
            Assert.Equal(25.0, a100.PlanQuantity.Value, 9); // 10/day × (16 + 24 + 20) h
            Assert.Equal("25 m", a100.PlanText);

            session.SetTypeObjective("Desarrollo", 15);
            var desarrollo = session.Capacity.Single(c => c.Type == "Desarrollo");
            Assert.Equal(15, desarrollo.Objective);
            Assert.Equal(28, desarrollo.Cells.Count);
            Assert.Equal(10.0 * 16 / 24, desarrollo.Cells[0].Value, 9);
            Assert.False(desarrollo.Cells[0].IsOver);
            Assert.Equal(20.0, desarrollo.Cells[1].Value, 9); // A100 + D400
            Assert.True(desarrollo.Cells[1].IsOver);
            Assert.True(desarrollo.Cells[2].IsOver);
            Assert.True(desarrollo.Cells[3].IsEmpty);
            Assert.DoesNotContain(session.Capacity.Single(c => c.Type == "Shotcrete").Cells, c => c.IsOver); // no objective

            // A100 25 m + D400 20 m; B200 48 pernos; C300 6/day × 36 h.
            Assert.Equal("Plan total 45 m · 9 m3 · 48 pernos", session.PlanTotalText);
        }

        [Fact]
        public void MoveUpdatesCountersDetailsAndPendingChanges()
        {
            var session = Session();
            Assert.Equal("Sin cambios pendientes", session.PendingChangesText);
            var outcome = session.MoveActivity("A100", 1);
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal("Movimiento validado.", session.StatusMessage);
            Assert.Equal((1, 2, true), (session.Counters.Moved, session.Counters.Cascaded, session.Counters.HasChanges));
            Assert.Equal(3, session.PendingChangeCount);
            Assert.Equal("3 cambios sin aplicar a VGS", session.PendingChangesText);
            Assert.False(session.CanEditRange);
            Assert.Equal("El rango no se puede cambiar mientras hay movimientos locales.", session.RangeLockedReason);
            Assert.Equal(SegmentStatus.Direct, session.Rows.Single(r => r.ActivityId == "A100").Status);
            Assert.Equal(SegmentStatus.Cascade, session.Rows.Single(r => r.ActivityId == "C300").Status);

            session.SelectedActivityId = "B200";
            var details = session.Selected;
            Assert.Equal("Fortificación rampa N1", details.Name);
            Assert.Equal("2026-03-05T20:00:00", details.Start);
            Assert.Equal("2026-03-04T20:00:00", details.OriginalStart);
            Assert.Equal((1L, "+1 día"), (details.ShiftDays, details.ShiftText));
            Assert.Equal("Movida por cascada desde A100", details.StatusText);
            Assert.Equal(new[] { "A100 FS" }, details.Predecessors.Select(p => p.Text));
            Assert.Equal(new[] { "C300 FS" }, details.Successors.Select(p => p.Text));
            Assert.True(details.Predecessors[0].IsMoved);
            Assert.Equal("48 pernos · 24 pernos/día", details.QuantityText); // 24/day × 48 h
            Assert.True(session.Rows.Single(r => r.ActivityId == "B200").IsSelected);

            session.SelectedActivityId = "A100";
            Assert.Equal("Movida directamente (+1 día)", session.Selected.StatusText);
            Assert.True(session.Selected.CanShowMoveControls);

            session.SelectedActivityId = "D400";
            Assert.Equal("Sin cambios", session.Selected.StatusText);
            Assert.Empty(session.Selected.Predecessors);
        }

        [Fact]
        public void RejectedMoveExplainsWhy()
        {
            var session = Session();
            var outcome = session.MoveActivity("B200", -1);
            Assert.False(outcome.Ok);
            Assert.Equal("incoming-precedence", outcome.Code);
            Assert.Equal("El movimiento viola una precedencia entrante. Revise las actividades predecesoras.", outcome.Message);
            Assert.True(session.StatusIsError);
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void UndoRedoAndDiscard()
        {
            var session = Session();
            Assert.False(session.CanUndo);
            Assert.False(session.CanDiscard);
            session.MoveActivity("A100", 1);
            session.MoveActivity("D400", 2);
            Assert.Equal("Mover D400 +2 días", session.UndoDescription);
            Assert.True(session.Undo().Ok);
            Assert.Equal(3, session.PendingChangeCount);
            Assert.Equal("Mover D400 +2 días", session.RedoDescription);
            Assert.True(session.Redo().Ok);
            Assert.Equal(4, session.PendingChangeCount);

            Assert.True(session.Discard().Ok);
            Assert.Equal(0, session.PendingChangeCount);
            Assert.False(session.CanRedo);
            Assert.True(session.Undo().Ok); // discard is undoable
            Assert.Equal(4, session.PendingChangeCount);

            session.Undo();
            session.Undo();
            Assert.False(session.HasPendingChanges);
            Assert.True(session.SetDuration(14).Ok); // range edits are history steps too
            Assert.Equal(14, session.Days.Count);
            session.Undo();
            Assert.Equal(28, session.Days.Count);
        }

        [Fact]
        public void TailMoveAndStaggerAreCommands()
        {
            var session = Session();
            var fragment = session.Rows.Single(r => r.ActivityId == "D400").Segments[1].FragmentId;
            var tail = session.MoveDailyTail("D400", fragment, "2026-03-10");
            Assert.True(tail.Ok, tail.Message);
            session.SelectedActivityId = "D400";
            Assert.Equal("Movida directamente (cola diaria)", session.Selected.StatusText);
            Assert.True(session.Undo().Ok);

            var split = session.SplitAndStagger("D400", session.Rows.Single(r => r.ActivityId == "D400").Segments[0].FragmentId, 1, 2);
            Assert.True(split.Ok, split.Message);
            Assert.Equal("Se insertaron 1 desfases de 2 días.", split.Message);

            var defer = session.DeferDailyTail("A100", session.Rows.Single(r => r.ActivityId == "A100").Segments[2].FragmentId);
            Assert.True(defer.Ok, defer.Message);
        }

        [Fact]
        public void ShowCompletedHidesLockedWork()
        {
            var session = Session(Golden.Snapshot("completed_lock"));
            int all = session.Rows.Sum(r => r.Segments.Count);
            Assert.Contains(session.Rows.SelectMany(r => r.Segments), s => s.Status == SegmentStatus.Completed);
            session.ShowCompleted = false;
            Assert.DoesNotContain(session.Rows.SelectMany(r => r.Segments), s => s.Status == SegmentStatus.Completed);
            Assert.True(session.Rows.Sum(r => r.Segments.Count) < all);
        }

        [Fact]
        public void ChangesRaisePropertyChanged()
        {
            var session = Session();
            var raised = new List<string>();
            ((INotifyPropertyChanged)session).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            session.MoveActivity("A100", 1);
            Assert.Contains(nameof(PlannerSession.Counters), raised);
            Assert.Contains(nameof(PlannerSession.Rows), raised);
            Assert.Contains(nameof(PlannerSession.CanUndo), raised);
            Assert.Contains(nameof(PlannerSession.PendingChangesText), raised);
            Assert.Contains(nameof(PlannerSession.StatusMessage), raised);
            raised.Clear();
            session.SearchText = "A";
            Assert.Contains(nameof(PlannerSession.SearchText), raised);
            Assert.Contains(nameof(PlannerSession.Groups), raised);
        }

        [Fact]
        public void LagAndTypeTexts()
        {
            Assert.Equal("FS", PlannerProjection.TypeAbbreviation("FinishStart"));
            Assert.Equal("SF", PlannerProjection.TypeAbbreviation("start-finish"));
            Assert.Equal(string.Empty, PlannerProjection.LagText(0));
            Assert.Equal("+1 d", PlannerProjection.LagText(86_400_000_000));
            Assert.Equal("-2 h", PlannerProjection.LagText(-7_200_000_000));
            Assert.Equal("+30 min", PlannerProjection.LagText(1_800_000_000));
        }
    }
}
