using System;
using System.Globalization;
using System.Linq;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Timeline;
using Maptek.Vulcan.Stp.Timeline.Model;
using Maptek.Vulcan.Stp.Views.Planner;
using Xunit;

namespace Maptek.Vulcan.Stp.Client.Tests
{
    /// <summary>"Avance real" requests reach the session, and the timeline model draws the session's boxes.</summary>
    public sealed class ActualProgressRouterTests : IDisposable
    {
        private readonly PlannerSession _session = DesignSnapshot.CreateSession();
        private readonly PlannerTimelineModel _model;
        private readonly ActualProgressRouter _router;

        public ActualProgressRouterTests()
        {
            _model = new PlannerTimelineModel(_session);
            _router = new ActualProgressRouter(_session);
        }

        public void Dispose()
        {
            _model.Dispose();
            _session.Dispose();
        }

        private static DateTime Date(string day) => DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        private (PlannerRow Row, DaySegment Segment) Pending() =>
            _session.Rows.SelectMany(r => r.Segments.Where(s => s.Status != SegmentStatus.Completed).Select(s => (r, s))).First();

        [Fact]
        public void The_mode_adds_boxes_to_the_timeline_rows()
        {
            var (row, segment) = Pending();
            Assert.All(_model.Rows, r => Assert.Empty(r.ProgressBoxes));
            _session.ShowActualProgress = true;
            var timelineRow = _model.Rows.First(r => r.Key == row.ActivityId);
            var box = Assert.Single(timelineRow.ProgressBoxes, b => b.Day == segment.DayIndex);
            Assert.Null(box.Value);
            Assert.IsType<ActualProgressBox>(box.Tag);
        }

        [Fact]
        public void Typed_values_moves_and_menus_reach_the_session()
        {
            var (row, segment) = Pending();
            _session.ShowActualProgress = true;
            var day = Date(segment.Date);

            var outcome = _router.Apply(TimelineProgressAction.Edit, row.ActivityId, day, day, 6);
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(6, _session.ActualProgressValue(row.ActivityId, segment.Date));
            Assert.Equal("6", _model.Rows.First(r => r.Key == row.ActivityId).ProgressBoxes.First(b => b.Day == segment.DayIndex).Text);

            var free = _session.Days.Select(d => d.Date).Last(d => row.Segments.All(s => s.Date != d));
            var preview = _router.Preview(row.ActivityId, day, Date(free));
            Assert.NotEmpty(preview);
            Assert.Equal(segment.DayIndex, preview[0].Origin);
            Assert.True(_router.Apply(TimelineProgressAction.Move, row.ActivityId, day, Date(free), null).Ok);
            Assert.Equal(6, _session.ActualProgressValue(row.ActivityId, _session.Days[preview[0].Target].Date));

            // The planned origin's box moved away (hidden): "Eliminar" is refused there, "Agregar" brings it back.
            Assert.False(_session.HasActualProgressBox(row.ActivityId, segment.Date));
            Assert.False(_router.Apply(TimelineProgressAction.Remove, row.ActivityId, day, day, null).Ok);
            Assert.True(_session.CanAddActualProgress(row.ActivityId, segment.Date));
            Assert.True(_router.Apply(TimelineProgressAction.Add, row.ActivityId, day, day, null).Ok);
            Assert.True(_session.HasActualProgressBox(row.ActivityId, segment.Date));
            Assert.True(_router.Apply(TimelineProgressAction.Remove, row.ActivityId, day, day, null).Ok);
            Assert.False(_session.HasActualProgressBox(row.ActivityId, segment.Date));
        }

        [Fact]
        public void Refusals_come_back_with_the_reason()
        {
            var (row, segment) = Pending();
            var outcome = _router.Apply(TimelineProgressAction.Edit, row.ActivityId, Date(segment.Date), Date(segment.Date), 1);
            Assert.False(outcome.Ok);
            Assert.Equal(ActualProgressMessages.ModeOff, outcome.Message);
        }

        [Fact]
        public void Charts_follow_the_summary()
        {
            var charts = ProgressCharts.Build(_session.ActualProgressSummary, false);
            Assert.NotEmpty(charts);
            Assert.All(charts, c => Assert.Equal("Programado", c.DailyLegend[0].Text));
            Assert.All(charts, c => Assert.False(c.HasRingsText));
            _session.ShowActualProgress = true;
            charts = ProgressCharts.Build(_session.ActualProgressSummary, true);
            Assert.All(charts, c => Assert.StartsWith("Plan: ", c.TotalsText));
            Assert.All(charts, c => Assert.Contains("Real", c.TotalsText));
            Assert.Equal(400, ProgressCharts.DailyAxis(300).Maximum);
            Assert.Equal(new double[] { 100, 200, 300, 400 }, ProgressCharts.DailyAxis(300).Ticks);
            Assert.Equal("16/nov", ProgressCharts.DateLabel("2026-11-16"));
        }
    }
}
