using System;
using System.Linq;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using Xunit;

namespace Maptek.Vulcan.Stp.Timeline.Tests
{
    public class TimelineLayoutTests
    {
        private const TimelineRowKind G = TimelineRowKind.Group, A = TimelineRowKind.Activity;

        // Group(24) A(30) A(30) Group(24) A(30): tops 0, 24, 54, 84, 108; extent 138.
        private static TimelineLayout Mixed() => new TimelineLayout(new[] { G, A, A, G, A });

        [Fact]
        public void RowTopsAreThePrefixSumOfMixedHeights()
        {
            var layout = Mixed();
            Assert.Equal(new[] { 0.0, 24, 54, 84, 108 }, Enumerable.Range(0, 5).Select(layout.RowTop));
            Assert.Equal(new[] { 24.0, 30, 30, 24, 30 }, Enumerable.Range(0, 5).Select(layout.RowHeight));
            Assert.Equal(138, layout.ExtentHeight);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(23.9, 0)]
        [InlineData(24, 1)]
        [InlineData(53.99, 1)]
        [InlineData(54, 2)]
        [InlineData(107, 3)]
        [InlineData(137.9, 4)]
        [InlineData(138, -1)]
        [InlineData(-0.1, -1)]
        public void RowAtFindsTheRowUnderY(double y, int expected) => Assert.Equal(expected, Mixed().RowAt(y));

        [Fact]
        public void RowAtOnEmptyLayoutIsMinusOne()
        {
            var layout = new TimelineLayout(new TimelineRowKind[0]);
            Assert.Equal(-1, layout.RowAt(0));
            Assert.True(layout.VisibleRows(0, 500).IsEmpty);
            Assert.Equal(0, layout.ExtentHeight);
        }

        [Fact]
        public void VisibleRowsIncludesPartlyVisibleRowsOnly()
        {
            var layout = Mixed();
            Assert.Equal((0, 2), Range(layout.VisibleRows(0, 54)));      // row 2 starts exactly at the bottom edge
            Assert.Equal((0, 3), Range(layout.VisibleRows(0, 54.5)));
            Assert.Equal((1, 4), Range(layout.VisibleRows(30, 60)));     // 30..90: rows 1, 2, 3
            Assert.Equal((4, 5), Range(layout.VisibleRows(120, 500)));   // past the end is clamped
            Assert.True(layout.VisibleRows(138, 100).IsEmpty);
            Assert.True(layout.VisibleRows(0, 0).IsEmpty);
            Assert.Equal((0, 1), Range(layout.VisibleRows(-50, 60)));    // negative offset: 0..10
        }

        [Fact]
        public void VirtualizationKeepsVisibleRowsBoundedFor2000Activities()
        {
            // 7 groups of ~286 activities.
            var kinds = Enumerable.Range(0, 2007).Select(i => i % 287 == 0 ? G : A).ToArray();
            var layout = new TimelineLayout(kinds);
            const double viewport = 700;
            int max = (int)Math.Ceiling(viewport / TimelineLayout.GroupRowHeight) + 1;
            for (double offset = 0; offset < layout.ExtentHeight; offset += 997)
            {
                var range = layout.VisibleRows(offset, viewport);
                Assert.InRange(range.Count, 1, max);
                Assert.True(layout.RowTop(range.First) <= offset);
                Assert.True(layout.RowTop(range.First) + layout.RowHeight(range.First) > offset);
                Assert.True(range.End == kinds.Length || layout.RowTop(range.End) >= offset + viewport);
            }
        }

        [Theory]
        [InlineData(44, 0, 0)]
        [InlineData(44, 5, 220)]
        [InlineData(44, 2.5, 110)]
        [InlineData(10, 89, 890)]
        public void DayToX(double dayWidth, double day, double x) =>
            Assert.Equal(x, new TimelineLayout(new[] { A }, dayWidth).DayToX(day), 6);

        [Theory]
        [InlineData(0, 0)]
        [InlineData(43.9, 0)]
        [InlineData(44, 1)]
        [InlineData(220, 5)]
        [InlineData(-0.1, -1)]
        public void XToDayFloors(double x, int day) => Assert.Equal(day, new TimelineLayout(new[] { A }).XToDay(x));

        [Fact]
        public void DayAndXRoundTrip()
        {
            var layout = new TimelineLayout(new[] { A }, 37.5);
            for (int d = -3; d < 120; d++) Assert.Equal(d, layout.XToDay(layout.DayToX(d) + 0.01));
        }

        [Fact]
        public void VisibleDaysClampsToTheRange()
        {
            var layout = new TimelineLayout(new[] { A }); // 44 px
            Assert.Equal((0, 14), Range(layout.VisibleDays(0, 2000, 14)));
            Assert.Equal((2, 7), Range(layout.VisibleDays(100, 200, 90)));    // 100..300 -> days 2..6
            Assert.Equal((2, 6), Range(layout.VisibleDays(88, 176, 90)));     // exact edges: 88..264 -> days 2..5
            Assert.True(layout.VisibleDays(0, 0, 90).IsEmpty);
            Assert.True(layout.VisibleDays(0, 500, 0).IsEmpty);
            Assert.True(layout.VisibleDays(5000, 500, 90).IsEmpty);
        }

        [Fact]
        public void ExtentWidthIsDaysTimesWidth()
        {
            var layout = new TimelineLayout(new[] { A }, 12);
            Assert.Equal(1080, layout.ExtentWidth(90));
            Assert.Equal(0, layout.ExtentWidth(-1));
        }

        [Fact]
        public void DateToDayIsFractionalFromTheStartDate()
        {
            var start = new DateTime(2026, 11, 16, 7, 30, 0); // time of day ignored
            Assert.Equal(5, TimelineLayout.DateToDay(start, new DateTime(2026, 11, 21)));
            Assert.Equal(5.5, TimelineLayout.DateToDay(start, new DateTime(2026, 11, 21, 12, 0, 0)));
            Assert.Equal(-1, TimelineLayout.DateToDay(start, new DateTime(2026, 11, 15)));
        }

        [Theory]
        [InlineData(0, 200, 44, 88)]
        [InlineData(500, 0, 44, 20)]
        [InlineData(1234, 321, 30, 60)]
        public void ZoomKeepsTheAnchorDayUnderThePointer(double offset, double anchor, double oldWidth, double newWidth)
        {
            double dayBefore = (offset + anchor) / oldWidth;
            double newOffset = TimelineLayout.ZoomOffset(offset, anchor, oldWidth, newWidth);
            Assert.Equal(dayBefore, (newOffset + anchor) / newWidth, 9);
        }

        [Theory]
        [InlineData(-5, 1000, 300, 0)]
        [InlineData(100, 1000, 300, 100)]
        [InlineData(900, 1000, 300, 700)]
        [InlineData(50, 200, 300, 0)]     // content smaller than viewport
        [InlineData(double.NaN, 1000, 300, 0)]
        public void ClampOffset(double offset, double extent, double viewport, double expected) =>
            Assert.Equal(expected, TimelineLayout.ClampOffset(offset, extent, viewport));

        [Fact]
        public void ScrollIntoViewMovesTheLeast()
        {
            var layout = Mixed();
            Assert.Equal(10, layout.ScrollIntoView(1, 10, 60));   // 24..54 inside 10..70: unchanged
            Assert.Equal(24, layout.ScrollIntoView(1, 40, 60));   // above: align top
            Assert.Equal(78, layout.ScrollIntoView(4, 0, 60));    // below: align bottom (138 - 60)
            Assert.Equal(7, layout.ScrollIntoView(99, 7, 60));    // unknown row: unchanged
        }

        [Theory]
        [InlineData(238, "238")]
        [InlineData(1700, "1.7k")]
        [InlineData(8500, "8.5k")]
        [InlineData(1000, "1.0k")]
        [InlineData(12.5, "12.5")]
        [InlineData(7, "7")]
        [InlineData(0, "0")]
        public void CompactQuantityMatchesTheMockup(double value, string label) =>
            Assert.Equal(label, TimelineLabels.CompactQuantity(value));

        [Fact]
        public void DayWidthIsCoerced()
        {
            Assert.Equal(TimelineLayout.MinDayWidth, TimelineLayout.CoerceDayWidth(1));
            Assert.Equal(TimelineLayout.MaxDayWidth, TimelineLayout.CoerceDayWidth(1e6));
            Assert.Equal(TimelineLayout.DefaultDayWidth, TimelineLayout.CoerceDayWidth(double.NaN));
        }

        private static (int, int) Range(IndexRange r) => (r.First, r.End);
    }
}
