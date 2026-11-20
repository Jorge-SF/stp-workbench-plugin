using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>
    /// Capacity preview while dragging: <see cref="CapacityExcesses"/> against timeline_logic.js <c>previewExcesses</c>
    /// (tools/golden/excesses), and <see cref="PlannerSession.PreviewMoveActivity"/> / <see cref="PlannerSession.PreviewMoveDailyTail"/>
    /// over chain_fs (Desarrollo: A100 6.67 / 10 / 8.33 m on 03-02..04, D400 10 / 10 m on 03-03..04).
    /// </summary>
    public class CapacityPreviewTests
    {
        private static string ExcessDir => Path.Combine(Golden.Root, "tools", "golden", "excesses");

        public static IEnumerable<object[]> GoldenCases() =>
            Golden.Load(Path.Combine(ExcessDir, "cases.json"))["cases"].Select(c => new object[] { (string)c["name"] });

        [Theory]
        [MemberData(nameof(GoldenCases))]
        public void Excesses_match_the_browser(string name)
        {
            var spec = (JObject)Golden.Load(Path.Combine(ExcessDir, "cases.json"))["cases"].Single(c => (string)c["name"] == name);
            var expected = (JObject)Golden.Load(Path.Combine(ExcessDir, "expected.json"))["cases"].Single(c => (string)c["name"] == name);
            var preferences = (JObject)spec["preferences"];
            var objectives = ((JObject)preferences["typeObjectives"] ?? new JObject()).Properties().ToDictionary(p => p.Name, p => (double)p.Value);
            var fronts = Fronts(preferences);

            var excesses = CapacityExcesses.Find(Rows(spec["view"]), objectives, fronts);
            Assert.Equal(Describe((JArray)expected["excesses"]), Describe(excesses));
            if (spec["baseline"] != null)
            {
                var added = CapacityExcesses.Added(CapacityExcesses.Find(Rows(spec["baseline"]), objectives, fronts), excesses);
                Assert.Equal(Describe((JArray)expected["added"]), Describe(added));
            }
        }

        [Fact]
        public void Exempt_segments_do_not_count_toward_the_type_amount()
        {
            PlannerRow Row(bool exempt) => new PlannerRow
            {
                ActivityId = "R1",
                Type = "Rezagado",
                Resource = "LHD-01",
                Segments = new[] { new DaySegment { Date = "2026-11-16", Status = SegmentStatus.Pending, Amount = 3000, WidthRatio = 1, IsCapacityExempt = exempt } },
            };
            var objectives = new Dictionary<string, double> { ["Rezagado"] = 2500 };
            Assert.Single(CapacityExcesses.Find(new[] { Row(false) }, objectives));
            Assert.Empty(CapacityExcesses.Find(new[] { Row(true) }, objectives));
            // ... but the day still uses a front of its equipment.
            Assert.Single(CapacityExcesses.Find(new[] { Row(true) }, objectives, new FrontLimits { Daily = 0 }));
        }

        [Fact]
        public void Fronts_count_the_day_equipment_assignment()
        {
            var rows = new[]
            {
                new PlannerRow { ActivityId = "A", Type = "T", Resource = "LHD-01", Segments = new[] { new DaySegment { Date = "2026-11-16", Status = SegmentStatus.Pending, WidthRatio = 1, Resource = "LHD-02" } } },
                new PlannerRow { ActivityId = "B", Type = "T", Resource = "LHD-02", Segments = new[] { new DaySegment { Date = "2026-11-16", Status = SegmentStatus.Pending, WidthRatio = 1, Resource = "LHD-02" } } },
            };
            var excess = Assert.Single(CapacityExcesses.Find(rows, new Dictionary<string, double>(), new FrontLimits { Daily = 1 }));
            Assert.Equal(CapacityExcessKind.Fronts, excess.Kind);
            Assert.Equal("LHD-02", excess.Label);
            Assert.Equal("Frentes excedidos para LHD-02.", excess.Message);
        }

        [Fact]
        public void Shift_drag_preview_shows_the_new_excess_without_moving_anything()
        {
            var session = ProjectionTests.Session();
            session.SetTypeObjective("Desarrollo", 19);   // over now: 03-03 (20)
            var before = session.State.Model;
            var raised = new List<string>();
            session.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            // A100 +1 day: 03-03 16.67, 03-04 20 (new), 03-05 8.33.
            var preview = session.PreviewMoveActivity("A100", 1);

            Assert.NotNull(preview);
            Assert.Same(preview, session.DragPreview);
            Assert.Contains(nameof(PlannerSession.DragPreview), raised);
            var excess = Assert.Single(preview.Excesses);
            Assert.Equal(CapacityExcessKind.Capacity, excess.Kind);
            Assert.Equal("Desarrollo", excess.Label);
            Assert.Equal("2026-03-04", excess.Date);
            Assert.Equal("Capacidad excedida para Desarrollo.", excess.Message);
            Assert.True(preview.HasWarning);
            Assert.Equal(new[] { "2026-03-04" }, preview.WarningDates);

            var cells = preview.Capacity.Single(r => r.Type == "Desarrollo").Cells.ToDictionary(c => c.Date);
            Assert.True(cells["2026-03-04"].IsPreviewExcess);
            Assert.True(cells["2026-03-04"].IsOver);
            Assert.Equal(20, cells["2026-03-04"].Value, 6);
            Assert.False(cells["2026-03-03"].IsOver);       // the candidate relieves 03-03
            Assert.False(cells["2026-03-03"].IsPreviewExcess);

            // Nothing was applied.
            Assert.Same(before, session.State.Model);
            Assert.False(session.HasPendingChanges);
            Assert.False(session.CanUndo);
            Assert.True(session.Capacity.Single(r => r.Type == "Desarrollo").Cells.Single(c => c.Date == "2026-03-03").IsOver);
        }

        [Fact]
        public void Tail_drag_preview_uses_the_daily_tail_move()
        {
            var session = ProjectionTests.Session();
            session.SetTypeObjective("Desarrollo", 19);
            string fragment = session.Rows.Single(r => r.ActivityId == "A100").Segments.Single(s => s.Date == "2026-03-03").FragmentId;

            // 03-03 and later move one day: A100 02 6.67, 04 10, 05 8.33 -> 03-04 = 20 (new).
            var preview = session.PreviewMoveDailyTail("A100", fragment, "2026-03-04");

            Assert.NotNull(preview);
            Assert.Equal("2026-03-04", Assert.Single(preview.Excesses).Date);
            Assert.False(session.HasPendingChanges);
        }

        [Fact]
        public void A_move_that_relieves_capacity_has_no_warning()
        {
            var session = ProjectionTests.Session();
            session.SetTypeObjective("Desarrollo", 15);   // over now: 03-03, 03-04
            var preview = session.PreviewMoveActivity("D400", 2);   // D400 to 03-05..06
            Assert.NotNull(preview);
            Assert.False(preview.HasWarning);
            Assert.Empty(preview.WarningDates);
            Assert.Equal(string.Empty, preview.WarningText);
            Assert.DoesNotContain(preview.Capacity.SelectMany(r => r.Cells), c => c.IsPreviewExcess);
        }

        [Fact]
        public void An_exempt_day_keeps_the_preview_clear()
        {
            var session = ProjectionTests.Session();
            session.SetTypeObjective("Desarrollo", 19);
            Assert.True(session.SetCapacityExemption("A100:2026-03-03", true).Ok);   // the day follows the move
            var preview = session.PreviewMoveActivity("A100", 1);
            Assert.NotNull(preview);
            Assert.False(preview.HasWarning);
        }

        [Fact]
        public void A_refused_or_zero_move_clears_the_preview()
        {
            var session = ProjectionTests.Session();
            session.SetTypeObjective("Desarrollo", 19);
            Assert.NotNull(session.PreviewMoveActivity("A100", 1));

            Assert.Null(session.PreviewMoveActivity("A100", 400));   // horizon-range
            Assert.Null(session.DragPreview);

            Assert.NotNull(session.PreviewMoveActivity("A100", 1));
            Assert.Null(session.PreviewMoveActivity("A100", 0));
            Assert.Null(session.DragPreview);

            string fragment = session.Rows.Single(r => r.ActivityId == "A100").Segments.First().FragmentId;
            Assert.Null(session.PreviewMoveDailyTail("A100", fragment, "2030-01-01"));   // outside the visible days
            Assert.Null(session.DragPreview);
        }

        [Fact]
        public void Drop_and_commit_clear_the_preview()
        {
            var session = ProjectionTests.Session();
            session.SetTypeObjective("Desarrollo", 19);
            Assert.NotNull(session.PreviewMoveActivity("A100", 1));
            session.ClearDragPreview();
            Assert.Null(session.DragPreview);

            Assert.NotNull(session.PreviewMoveActivity("A100", 1));
            Assert.True(session.MoveActivity("A100", 1).Ok);   // the drop itself
            Assert.Null(session.DragPreview);
            Assert.True(session.Capacity.Single(r => r.Type == "Desarrollo").Cells.Single(c => c.Date == "2026-03-04").IsOver);
        }

        [Fact]
        public void The_same_target_is_not_recomputed()
        {
            var session = ProjectionTests.Session();
            session.SetTypeObjective("Desarrollo", 19);
            var first = session.PreviewMoveActivity("A100", 1);
            Assert.Same(first, session.PreviewMoveActivity("A100", 1));
            Assert.NotSame(first, session.PreviewMoveActivity("A100", 2));
        }

        private static FrontLimits Fronts(JObject preferences)
        {
            // JS: resourceCapacities[label] ?? resourceFronts[label] ?? dailyFronts; no value at all = no check.
            var perResource = new Dictionary<string, double>();
            foreach (var p in ((JObject)preferences["resourceFronts"] ?? new JObject()).Properties()) perResource[p.Name] = (double)p.Value;
            foreach (var p in ((JObject)preferences["resourceCapacities"] ?? new JObject()).Properties()) perResource[p.Name] = (double)p.Value;
            double? daily = preferences["dailyFronts"] == null ? (double?)null : (double)preferences["dailyFronts"];
            return perResource.Count == 0 && daily == null ? null : new FrontLimits { PerResource = perResource, Daily = daily };
        }

        private static IReadOnlyList<PlannerRow> Rows(JToken view) => view["rows"].Select(row => new PlannerRow
        {
            ActivityId = (string)row["activityId"],
            Type = (string)row["activityType"],
            Resource = (string)row["resource"],
            Segments = row["days"].SelectMany(day => day["segments"].Select(segment => new DaySegment
            {
                Date = (string)day["date"],
                Status = (string)segment["status"] == "completed" ? SegmentStatus.Completed
                    : (string)segment["status"] == "direct" ? SegmentStatus.Direct
                    : (string)segment["status"] == "cascade" ? SegmentStatus.Cascade : SegmentStatus.Pending,
                Amount = segment["scheduledAmount"] == null ? (double?)null : (double)segment["scheduledAmount"],
                WidthRatio = (double)segment["widthRatio"],
            })).ToList(),
        }).ToList();

        private static List<string> Describe(JArray items) => items.Select(i =>
        {
            Assert.False((bool)i["blocking"]);
            return $"{i["kind"]}|{i["label"]}|{i["date"]}|{i["message"]}";
        }).ToList();

        private static List<string> Describe(IEnumerable<CapacityExcess> items) => items.Select(i =>
            $"{(i.Kind == CapacityExcessKind.Capacity ? "capacity" : "fronts")}|{i.Label}|{i.Date}|{i.Message}").ToList();
    }
}
