using System;
using System.Linq;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Timeline.Model;
using Maptek.Vulcan.Stp.Views.Planner;
using Xunit;

namespace Maptek.Vulcan.Stp.Client.Tests
{
    /// <summary>The timeline adapter over a real PlannerSession of the mockup's design data.</summary>
    public class PlannerTimelineModelTests
    {
        private static TimelineRow Row(PlannerTimelineModel model, string key) => model.Rows.Single(r => r.Key == key);

        [Fact]
        public void Design_session_is_the_mockup_state()
        {
            using var session = DesignSnapshot.CreateSession();
            Assert.Equal("2026-11-16", session.RangeStart);
            Assert.Equal(14, session.DurationDays);
            Assert.Equal(12, session.Counters.Activities);
            Assert.Equal(1, session.Counters.Moved);
            Assert.Equal(2, session.Counters.Cascaded);
            Assert.Equal(DesignSnapshot.SelectedActivity, session.Selected?.ActivityId);
            Assert.False(session.IsLive);
        }

        [Fact]
        public void Design_rows_show_their_names()
        {
            using var session = DesignSnapshot.CreateSession();
            Assert.Equal("Desarrollo rampa 1450 tramo 1", session.Snapshot.Activities.Single(a => a.Id == "DES-1450-01").Grid["Description"]);
            Assert.DoesNotContain(session.Rows, r => r.Name.StartsWith("Actividad ", StringComparison.Ordinal));
        }

        [Fact]
        public void Columns_and_completed_line_follow_the_range()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);
            Assert.Equal(new DateTime(2026, 11, 16), model.StartDate);
            Assert.Equal(14, model.DayCount);
            Assert.Equal(DesignSnapshot.ExportedAt, model.CompletedThrough);
        }

        [Fact]
        public void Rows_are_group_headers_then_the_session_rows_in_display_order()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);

            var groups = model.Rows.Where(r => r.Kind == TimelineRowKind.Group).ToList();
            Assert.Equal(session.Groups.Select(g => g.Label), groups.Select(g => g.Name));
            Assert.Equal(session.Groups.Select(g => g.Count), groups.Select(g => g.GroupCount));
            Assert.All(groups, g => Assert.StartsWith(PlannerTimelineModel.GroupKeyPrefix, g.Key));
            Assert.Equal(TimelineRowKind.Group, model.Rows[0].Kind);

            var activities = model.Rows.Where(r => r.Kind == TimelineRowKind.Activity).Select(r => r.Key);
            Assert.Equal(session.Rows.Select(r => r.ActivityId), activities);
        }

        [Fact]
        public void Ungrouped_has_no_header_rows()
        {
            using var session = DesignSnapshot.CreateSession();
            session.Grouping = PlannerGrouping.None;
            using var model = new PlannerTimelineModel(session);
            Assert.All(model.Rows, r => Assert.Equal(TimelineRowKind.Activity, r.Kind));
            Assert.Equal(session.Rows.Count, model.Rows.Count);
        }

        [Fact]
        public void Activity_rows_carry_the_grid_columns_and_type_colour()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);
            var row = Row(model, "REZ-1425-01");
            var source = session.Rows.Single(r => r.ActivityId == "REZ-1425-01");
            Assert.Equal("REZ-1425-01", row.Id);
            Assert.Equal("Rezagado TJ-1425-01", row.Name);
            Assert.Equal("Scoop LH-07", row.Resource);
            Assert.Equal(source.PlanText, row.Plan);
            Assert.Equal(Color.FromRgb(0xD0, 0x7A, 0x2C), row.Color);   // mockup --t-rez
            var header = model.Rows.Single(r => r.Key == PlannerTimelineModel.GroupKeyPrefix + "Rezagado");
            Assert.Equal(row.Color, header.Color);
        }

        [Fact]
        public void One_block_per_visible_day_with_the_day_quantity()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);

            var bar = Row(model, "BAR-1425-01");   // moved +1: days 5..8
            Assert.Equal(new[] { 5, 6, 7, 8 }, bar.Blocks.Select(b => b.Day));
            Assert.All(bar.Blocks, b => Assert.Equal("300", b.Label));

            var rez = Row(model, "REZ-1425-01");   // cascade: days 10..13
            Assert.Equal(new[] { 10, 11, 12, 13 }, rez.Blocks.Select(b => b.Day).Take(4));
            Assert.All(rez.Blocks, b => Assert.Equal("1.7k", b.Label));

            foreach (var row in model.Rows.Where(r => r.Kind == TimelineRowKind.Activity))
            {
                var days = row.Blocks.Select(b => b.Day).ToList();
                Assert.Equal(days.OrderBy(d => d), days);
                Assert.Equal(days.Distinct().Count(), days.Count);
                Assert.All(row.Blocks, b => Assert.IsType<DaySegment>(b.Tag));
            }
        }

        [Fact]
        public void Completed_work_is_hatched_and_fully_completed_rows_are_locked()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);

            var done = Row(model, "DES-1450-01");
            Assert.True(done.IsLocked);
            Assert.All(done.Blocks, b => Assert.True(b.IsCompleted));

            var partial = Row(model, "REL-1400-01");
            Assert.False(partial.IsLocked);
            Assert.Equal(new[] { true, true, true, true, true, false, false, false, false, false }, partial.Blocks.Select(b => b.IsCompleted));
        }

        [Fact]
        public void Completed_work_can_be_hidden()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);
            session.ShowCompleted = false;
            Assert.DoesNotContain(model.Rows, r => r.Blocks.Any(b => b.IsCompleted));
            Assert.Equal(new[] { 5, 6, 7, 8, 9 }, Row(model, "REL-1400-01").Blocks.Select(b => b.Day));
        }

        [Fact]
        public void Move_outline_follows_direct_and_cascade()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);
            Assert.Equal(TimelineMoveKind.Direct, Row(model, "BAR-1425-01").Move);
            Assert.Equal(TimelineMoveKind.Cascade, Row(model, "VOL-1425-01").Move);
            Assert.Equal(TimelineMoveKind.Cascade, Row(model, "REZ-1425-01").Move);
            Assert.Equal(TimelineMoveKind.None, Row(model, "BAR-1400-02").Move);
        }

        [Fact]
        public void Links_join_shown_rows_only()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);
            Assert.Equal(8, model.Links.Count);
            Assert.Contains(model.Links, l => l.FromKey == "VOL-1425-01" && l.ToKey == "REZ-1425-01");

            session.SetTypeVisible("Voladura", false);
            Assert.DoesNotContain(model.Links, l => l.FromKey.StartsWith("VOL") || l.ToKey.StartsWith("VOL"));
            Assert.Equal(4, model.Links.Count);
        }

        [Fact]
        public void Session_changes_rebuild_once_and_raise_Changed()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);
            int changed = 0;
            model.Changed += (s, e) => changed++;
            int before = model.BuildCount;

            Assert.True(session.Undo().Ok);   // the one direct move
            Assert.Equal(before + 1, model.BuildCount);
            Assert.Equal(1, changed);
            Assert.All(model.Rows, r => Assert.Equal(TimelineMoveKind.None, r.Move));
            Assert.Equal(new[] { 4, 5, 6, 7 }, Row(model, "BAR-1425-01").Blocks.Select(b => b.Day));

            session.SelectedActivityId = "BAR-1400-02";
            Assert.Equal(2, changed);
        }

        [Fact]
        public void Dispose_stops_listening()
        {
            using var session = DesignSnapshot.CreateSession();
            var model = new PlannerTimelineModel(session);
            model.Dispose();
            int builds = model.BuildCount;
            session.ToggleType("Relleno");
            Assert.Equal(builds, model.BuildCount);
        }

        [Fact]
        public void Search_filters_the_timeline_rows()
        {
            using var session = DesignSnapshot.CreateSession();
            using var model = new PlannerTimelineModel(session);
            session.SearchText = "1425";
            var keys = model.Rows.Where(r => r.Kind == TimelineRowKind.Activity).Select(r => r.Key).ToList();
            Assert.NotEmpty(keys);
            Assert.All(keys, k => Assert.Contains("1425", k));
        }

        [Theory]
        [InlineData("#3B78C4", 0x3B, 0x78, 0xC4)]
        [InlineData("#000000", 0, 0, 0)]
        public void Hex_colours_parse(string hex, byte r, byte g, byte b) =>
            Assert.Equal(Color.FromRgb(r, g, b), PlannerTimelineModel.ToColor(hex));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("red")]
        [InlineData("#12345")]
        [InlineData("#GGGGGG")]
        public void Bad_colours_use_the_fallback(string hex) =>
            Assert.Equal(PlannerTimelineModel.ToColor(TypePalette.Fallback), PlannerTimelineModel.ToColor(hex));
    }
}
