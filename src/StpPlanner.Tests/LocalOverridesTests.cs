using System.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>"Asignar equipo" and "Exentar Límite" over chain_fs (A100 Desarrollo, Jumbo 01, 10 m/día, 2026-03-02..04).</summary>
    public class LocalOverridesTests
    {
        private const string Day2 = "A100:2026-03-03";

        private static DaySegment Segment(PlannerSession session, string unit) =>
            session.Rows.SelectMany(r => r.Segments).First(s => s.LogicalUnitId == unit);

        [Fact]
        public void Segments_carry_their_logical_unit_and_the_activity_equipment()
        {
            var session = ProjectionTests.Session();
            var segment = Segment(session, Day2);
            Assert.Equal("Jumbo 01", segment.Resource);
            Assert.False(segment.HasResourceOverride);
            Assert.False(segment.IsCapacityExempt);
            Assert.Equal(Day2, session.LogicalUnitOf(segment.FragmentId));
            Assert.True(session.Rows.First(r => r.ActivityId == "A100").CapacityExemptible);
            Assert.Contains("Empernador 01", session.ResourceOptions);
        }

        [Fact]
        public void Assigning_equipment_changes_the_day_and_the_row_not_the_plan()
        {
            var storage = new MemoryHorizonStorage();
            var session = new PlannerSession(ProjectionTests.ChainWithRates(), Golden.StaticConfig(), null, storage, new PlannerOptions { TypeColumns = new[] { "Type" } });
            var outcome = session.AssignResource(Day2, "Empernador 01");

            Assert.True(outcome.Ok);
            Assert.Equal(PlannerMessages.ResourceAssigned, outcome.Message);
            Assert.Equal("Empernador 01", Segment(session, Day2).Resource);
            Assert.True(Segment(session, Day2).HasResourceOverride);
            Assert.Equal("Jumbo 01, Empernador 01", session.Rows.First(r => r.ActivityId == "A100").Resource);
            Assert.False(session.HasPendingChanges);   // local view setting, nothing for VGS
            Assert.True(session.CanUndo);              // but an undo step (manual resources made equipment undoable)
            Assert.Equal("Asignar Empernador 01 a " + Day2, session.UndoDescription);

            // Saved per project: a new session over the same storage sees it.
            var reopened = new PlannerSession(ProjectionTests.ChainWithRates(), Golden.StaticConfig(), null, storage, new PlannerOptions { TypeColumns = new[] { "Type" } });
            Assert.Equal("Empernador 01", reopened.AssignedResource(Day2));

            Assert.Equal(PlannerMessages.ResourceCleared, session.AssignResource(Day2, null).Message);
            Assert.False(Segment(session, Day2).HasResourceOverride);
        }

        [Fact]
        public void The_activity_own_equipment_clears_the_assignment()
        {
            var session = ProjectionTests.Session();
            session.AssignResource(Day2, "Empernador 01");
            session.AssignResource(Day2, "Jumbo 01");
            Assert.Null(session.AssignedResource(Day2));
        }

        [Fact]
        public void Unknown_equipment_or_day_is_refused()
        {
            var session = ProjectionTests.Session();
            var unknown = session.AssignResource(Day2, "Pala inexistente");
            Assert.False(unknown.Ok);
            Assert.Equal(PlannerMessages.UnknownResource, unknown.Message);
            Assert.True(session.StatusIsError);
            Assert.False(session.AssignResource("A100:2030-01-01", "Jumbo 01").Ok);
            Assert.False(session.SetCapacityExemption("nope", true).Ok);
        }

        [Fact]
        public void An_exempt_day_does_not_count_against_the_objective()
        {
            var session = ProjectionTests.Session();
            session.SetTypeObjective("Desarrollo", 15);   // A100 + D400: 20 m on 2026-03-03
            var cell = session.Capacity.Single(r => r.Type == "Desarrollo").Cells.Single(c => c.Date == "2026-03-03");
            Assert.True(cell.IsOver);

            var outcome = session.SetCapacityExemption(Day2, true);
            Assert.True(outcome.Ok);
            Assert.True(Segment(session, Day2).IsCapacityExempt);
            Assert.True(session.IsCapacityExempt(Day2));
            cell = session.Capacity.Single(r => r.Type == "Desarrollo").Cells.Single(c => c.Date == "2026-03-03");
            Assert.False(cell.IsOver);
            Assert.Equal(20, cell.Value, 6);          // still shown as scheduled
            Assert.Equal(10, cell.CapacityValue, 6);  // A100 no longer counts against the objective

            Assert.Equal(PlannerMessages.ExemptionCleared, session.SetCapacityExemption(Day2, false).Message);
            Assert.False(session.IsCapacityExempt(Day2));
        }

        [Fact]
        public void Activities_without_a_type_cannot_be_exempted()
        {
            var session = ProjectionTests.Session(options: new PlannerOptions());   // browser default: "Activity Type" only
            Assert.False(session.IsCapacityExemptible("A100"));
            var outcome = session.SetCapacityExemption(Day2, true);
            Assert.False(outcome.Ok);
            Assert.Equal(PlannerMessages.NotExemptible, outcome.Message);
        }

        [Fact]
        public void Overrides_follow_the_day_when_it_moves()
        {
            var session = ProjectionTests.Session();
            session.AssignResource(Day2, "Empernador 01");
            var fragment = Segment(session, Day2).FragmentId;
            Assert.True(session.MoveDailyTail("A100", fragment, "2026-03-10").Ok);
            var moved = Segment(session, Day2);
            Assert.Equal("Empernador 01", moved.Resource);
            Assert.Equal("2026-03-10", moved.Date);
        }

        [Fact]
        public void Damaged_storage_reads_as_no_overrides()
        {
            var storage = new MemoryHorizonStorage();
            var snapshot = ProjectionTests.ChainWithRates();
            storage.SetItem(LocalOverrides.StoragePrefix + snapshot.ProjectKey, "{not json");
            var session = new PlannerSession(snapshot, Golden.StaticConfig(), null, storage, new PlannerOptions { TypeColumns = new[] { "Type" } });
            Assert.Null(session.AssignedResource(Day2));
            Assert.True(session.AssignResource(Day2, "Empernador 01").Ok);
        }
    }
}
