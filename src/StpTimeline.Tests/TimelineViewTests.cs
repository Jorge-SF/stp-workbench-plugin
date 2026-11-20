using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using Xunit;

namespace Maptek.Vulcan.Stp.Timeline.Tests
{
    /// <summary>Off-screen control: measure/arrange at a fixed size and draw synchronously with UpdateNow.</summary>
    public class TimelineViewTests
    {
        private const double Width = 1200, Height = 700, GridWidth = 440;

        private static void Sta(Action body)
        {
            ExceptionDispatchInfo error = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            error?.Throw();
        }

        /// <summary>One group header, then <paramref name="activities"/> rows of 5-day blocks; row i starts on day i % (days - 5).</summary>
        private static TimelineModel Model(int activities, int days)
        {
            var model = new TimelineModel { StartDate = new DateTime(2026, 11, 16), DayCount = days, CompletedThrough = new DateTime(2026, 11, 21) };
            model.Rows.Add(TimelineRow.Group("g", "Rezagado", Colors.Orange, activities));
            for (int i = 0; i < activities; i++)
            {
                var row = new TimelineRow { Kind = TimelineRowKind.Activity, Key = "A" + i, Id = "A" + i, Name = "Act " + i, Resource = "R", Plan = "1 t" };
                int start = i % (days - 5);
                for (int k = 0; k < 5; k++) row.Blocks.Add(new TimelineBlock(start + k, "100", start + k < 5, tag: i * 10 + k));
                model.Rows.Add(row);
                if (i > 0) model.Links.Add(new TimelineLink("A" + (i - 1), "A" + i));
            }
            return model;
        }

        private static TimelineView Arranged(ITimelineModel model)
        {
            var view = new TimelineView { Model = model, GridWidth = GridWidth };
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));
            view.UpdateLayout();
            view.UpdateNow();
            return view;
        }

        [Fact]
        public void DrawsOnlyTheVisibleRowsAndDaysOf2000x90() => Sta(() =>
        {
            var view = Arranged(Model(2000, 90));
            double bodyHeight = view.ChartPanel.ActualHeight - TimelineLayout.HeaderHeight;
            int maxRows = (int)Math.Ceiling(bodyHeight / TimelineLayout.GroupRowHeight) + 1;
            int maxDays = (int)Math.Ceiling(view.ChartPanel.ActualWidth / view.DayWidth) + 1;

            foreach (double offset in new[] { 0.0, 15_000, 59_000 })
            {
                view.SetVerticalOffset(offset);
                view.UpdateNow();
                Assert.InRange(view.DrawnRowCount, 1, maxRows);
                Assert.InRange(view.DrawnBlockCount, 0, maxRows * maxDays);
                Assert.InRange(view.DrawnLinkCount, 0, maxRows + 1);
            }
            Assert.True(view.OffsetY > 50_000, "scrolled near the end");
        });

        [Fact]
        public void ScrollOffsetsAreClampedToTheExtent() => Sta(() =>
        {
            var view = Arranged(Model(2000, 90));
            view.SetVerticalOffset(1e9);
            double bodyHeight = view.ChartPanel.ActualHeight - TimelineLayout.HeaderHeight;
            Assert.Equal(TimelineLayout.GroupRowHeight + 2000 * TimelineLayout.ActivityRowHeight - bodyHeight, view.OffsetY, 6);
            view.SetHorizontalOffset(-10);
            Assert.Equal(0, view.OffsetX);
            view.SetHorizontalOffset(1e9);
            Assert.Equal(90 * view.DayWidth - view.ChartPanel.ActualWidth, view.OffsetX, 6);
        });

        [Fact]
        public void ZoomKeepsTheDayUnderTheAnchor() => Sta(() =>
        {
            var view = Arranged(Model(50, 90));
            view.SetHorizontalOffset(440);                          // day 10 at the left edge (44 px/day)
            view.ZoomAt(88, 220);                                   // anchor on day 15
            Assert.Equal(88, view.DayWidth);
            Assert.Equal(15, (view.OffsetX + 220) / view.DayWidth, 6);
        });

        [Fact]
        public void HitTestFindsTheBlockUnderThePoint() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var chart = view.ChartPanel;
            // Row 3 = activity A2 (blocks on days 2..6): content y = 24 + 2 * 30, centre + 15.
            double y = TimelineLayout.HeaderHeight + 24 + 2 * 30 + 15;
            var hit = chart.HitTest(new Point(3 * 44 + 20, y));
            Assert.Equal(3, hit.Row);
            Assert.Equal(1, hit.Block);                             // day 3 is A2's second block
            Assert.Equal(-1, chart.HitTest(new Point(10 * 44 + 20, y)).Block); // no block on day 10
            Assert.Equal(-1, chart.HitTest(new Point(100, 10)).Row);            // header
            Assert.Equal(0, chart.HitTest(new Point(100, TimelineLayout.HeaderHeight + 5)).Row); // group row
        });

        [Fact]
        public void HiddenCompletedBlocksAreNotHit() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            double y = TimelineLayout.HeaderHeight + 24 + 15;       // A0: days 0..4, all completed
            Assert.Equal(0, view.ChartPanel.HitTest(new Point(20, y)).Block);
            view.ShowCompleted = false;
            Assert.Equal(-1, view.ChartPanel.HitTest(new Point(20, y)).Block);
        });

        [Fact]
        public void BlockEventsCarryRowBlockAndDate() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = new List<TimelineBlockEventArgs>();
            view.BlockClick += (s, e) => seen.Add(e);
            view.BlockDragStarted += (s, e) => seen.Add(e);
            var hit = view.ChartPanel.HitTest(new Point(3 * 44 + 20, TimelineLayout.HeaderHeight + 24 + 2 * 30 + 15));
            view.RaiseBlockEvent(TimelineView.BlockClickEvent, hit, new Point(1, 1), view.ChartPanel);
            view.RaiseBlockEvent(TimelineView.BlockDragStartedEvent, hit, new Point(1, 1), view.ChartPanel);
            Assert.Equal(2, seen.Count);
            Assert.Equal(TimelineView.BlockClickEvent, seen[0].RoutedEvent);
            Assert.Equal(TimelineView.BlockDragStartedEvent, seen[1].RoutedEvent);
            Assert.Equal("A2", seen[0].Row.Key);
            Assert.Equal(new DateTime(2026, 11, 19), seen[0].Date);
            Assert.Equal(21, seen[0].Block.Tag);
        });

        [Fact]
        public void SelectionFollowsTheKeyAndSurvivesModelChanges() => Sta(() =>
        {
            var model = Model(20, 30);
            var view = Arranged(model);
            int changes = 0;
            view.SelectedRowChanged += (s, e) => changes++;
            view.SelectedKey = "A5";
            Assert.Equal(6, view.SelectedRowIndex);
            model.Rows.RemoveAt(1);                                 // A0 gone: A5 moves up one row
            model.NotifyChanged();
            Assert.Equal(5, view.SelectedRowIndex);
            view.SelectedKey = "g";                                 // group rows are not selectable
            Assert.Equal(-1, view.SelectedRowIndex);
            Assert.Equal(2, changes);
        });

        [Fact]
        public void ScrollIntoViewBringsADistantRowOnScreen() => Sta(() =>
        {
            var view = Arranged(Model(2000, 90));
            view.ScrollIntoView("A1500");
            var layout = view.Layout;
            var visible = layout.VisibleRows(view.OffsetY, view.ChartPanel.ActualHeight - TimelineLayout.HeaderHeight);
            Assert.True(visible.Contains(1501));
        });
    }
}
