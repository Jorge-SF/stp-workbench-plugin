using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using Xunit;

namespace Maptek.Vulcan.Stp.Timeline.Tests
{
    /// <summary>"Avance real" in the timeline: taller rows, boxes, typing, grip drag and menus.</summary>
    public class TimelineProgressTests
    {
        private const double Width = 1200, Height = 700, GridWidth = 440, Dw = 44;

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

        /// <summary>Activity A0 with blocks on days 1..3 (day 1 completed) and boxes on days 1, 2 (value 4) and 5.</summary>
        private static TimelineModel Model()
        {
            var model = new TimelineModel { StartDate = new DateTime(2026, 11, 16), DayCount = 14 };
            var row = new TimelineRow { Kind = TimelineRowKind.Activity, Key = "A0", Id = "A0", Name = "Act" };
            for (int d = 1; d <= 3; d++) row.Blocks.Add(new TimelineBlock(d, "10", d == 1));
            row.ProgressBoxes.Add(new TimelineProgressBox(1, null, string.Empty));
            row.ProgressBoxes.Add(new TimelineProgressBox(2, 4, "4"));
            row.ProgressBoxes.Add(new TimelineProgressBox(5, null, string.Empty));
            model.Rows.Add(row);
            model.Rows.Add(new TimelineRow { Kind = TimelineRowKind.Activity, Key = "B1", Id = "B1", Name = "Other" });
            return model;
        }

        private static TimelineView Arranged(ITimelineModel model, bool mode = true)
        {
            var view = new TimelineView { Model = model, GridWidth = GridWidth, ShowActualProgress = mode, IsEditable = true };
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));
            view.UpdateLayout();
            view.UpdateNow();
            return view;
        }

        /// <summary>A chart point on the grip (or value) of a box of row <paramref name="row"/>, day <paramref name="day"/>.</summary>
        private static Point Box(int row, int day, bool grip) =>
            new Point(day * Dw + (grip ? 5 : 30), TimelineLayout.HeaderHeight + row * TimelineLayout.ProgressRowHeight + TimelineLayout.ProgressBoxTop + 8);

        private static List<TimelineProgressEventArgs> Record(TimelineView view)
        {
            var seen = new List<TimelineProgressEventArgs>();
            view.ActualProgressRequested += (s, e) => seen.Add(e);
            return seen;
        }

        [Fact]
        public void ParsingAcceptsBlankCommaAndRejectsNegatives()
        {
            Assert.True(TimelineEditMath.TryParseProgressValue("  ", out var v) && v == null);
            Assert.True(TimelineEditMath.TryParseProgressValue("2,5", out v) && v == 2.5);
            Assert.True(TimelineEditMath.TryParseProgressValue("1.25", out v) && v == 1.25);
            Assert.False(TimelineEditMath.TryParseProgressValue("-1", out _));
            Assert.False(TimelineEditMath.TryParseProgressValue("abc", out _));
            Assert.False(TimelineEditMath.TryParseProgressValue("Infinity", out _));
        }

        [Fact]
        public void ProgressRowsAreTallerAndBlocksKeepTheirBand()
        {
            var layout = new TimelineLayout(new[] { TimelineRowKind.Group, TimelineRowKind.Activity }, Dw, TimelineLayout.ProgressRowHeight);
            Assert.Equal(TimelineLayout.GroupRowHeight, layout.RowHeight(0));
            Assert.Equal(TimelineLayout.ProgressRowHeight, layout.RowHeight(1));
            Assert.Equal(TimelineLayout.ActivityRowHeight, layout.BlockBandHeight(1));
            Assert.Equal(TimelineLayout.GroupRowHeight, layout.BlockBandHeight(0));
        }

        [Fact]
        public void HitTestFindsGripValueAndEmptyDays()
        {
            var model = Model();
            var layout = new TimelineLayout(model.Rows.Select(r => r.Kind).ToList(), Dw, TimelineLayout.ProgressRowHeight);
            double y = TimelineLayout.ProgressBoxTop + 8;
            var grip = TimelineEditMath.ProgressHitTest(layout, model.Rows, 2 * Dw + 5, y, 14);
            Assert.Equal((0, 1, 2, TimelineProgressPart.Grip), (grip.Row, grip.Box, grip.Day, grip.Part));
            var value = TimelineEditMath.ProgressHitTest(layout, model.Rows, 2 * Dw + 30, y, 14);
            Assert.Equal(TimelineProgressPart.Value, value.Part);
            var empty = TimelineEditMath.ProgressHitTest(layout, model.Rows, 4 * Dw + 30, y, 14);
            Assert.Equal((0, -1, 4), (empty.Row, empty.Box, empty.Day));
            var block = TimelineEditMath.ProgressHitTest(layout, model.Rows, 2 * Dw + 30, 12, 14);
            Assert.Equal(-1, block.Box);   // the block band is not the box
            Assert.Equal(1, TimelineEditMath.HitTest(layout, model.Rows, 2 * Dw + 30, 12, true).Block);
            Assert.Equal(-1, TimelineEditMath.HitTest(layout, model.Rows, 2 * Dw + 30, y, true).Block);
            Assert.Equal(0, TimelineEditMath.ProgressGripWidth(20));
        }

        [Fact]
        public void TypingInABoxRequestsAnEdit() => Sta(() =>
        {
            var view = Arranged(Model());
            var seen = Record(view);
            view.ChartPanel.Press(Box(0, 2, grip: false));
            Assert.True(view.IsEditingActualProgress);
            Assert.Equal("A0", view.SelectedKey);
            view.CommitActualProgressEdit();   // unchanged text: no request
            Assert.Empty(seen);

            Assert.True(view.BeginActualProgressEdit("A0", 2));
            SetEditorText(view, "7,5");
            view.CommitActualProgressEdit();
            var e = Assert.Single(seen);
            Assert.Equal(TimelineProgressAction.Edit, e.Action);
            Assert.Equal("A0", e.ActivityKey);
            Assert.Equal(2, e.Day);
            Assert.Equal(new DateTime(2026, 11, 18), e.Date);
            Assert.Equal(7.5, e.Value);
            Assert.False(view.IsEditingActualProgress);

            Assert.True(view.BeginActualProgressEdit("A0", 5));
            SetEditorText(view, "-3");
            view.CommitActualProgressEdit();
            Assert.Single(seen);   // refused locally
            Assert.Equal(TimelineView.InvalidProgressReason, view.LastBlockedReason);

            Assert.True(view.BeginActualProgressEdit("A0", 2));
            SetEditorText(view, string.Empty);
            view.CommitActualProgressEdit();
            Assert.Null(seen.Last().Value);   // emptied
            Assert.False(view.BeginActualProgressEdit("A0", 3));   // no box on day 3
        });

        [Fact]
        public void TheModeOffShowsNoBoxesAndKeepsNormalRows() => Sta(() =>
        {
            var view = Arranged(Model(), mode: false);
            Assert.Equal(TimelineLayout.ActivityRowHeight, view.Layout.RowHeight(0));
            view.ChartPanel.Press(new Point(2 * Dw + 30, TimelineLayout.HeaderHeight + 26));
            Assert.False(view.IsEditingActualProgress);
            view.ChartPanel.Release(new Point(2 * Dw + 30, TimelineLayout.HeaderHeight + 26));
            Assert.False(view.BeginActualProgressEdit("A0", 2));
        });

        [Fact]
        public void DraggingTheGripPreviewsAndRequestsAMove() => Sta(() =>
        {
            var view = Arranged(Model());
            var seen = Record(view);
            var asked = new List<TimelineProgressQueryEventArgs>();
            view.ActualProgressQuery += (s, e) =>
            {
                asked.Add(e);
                if (e.IsPreview) e.SetMoves(new[] { (e.Day, e.TargetDay), (5, 5 + e.TargetDay - e.Day) });
            };
            var chart = view.ChartPanel;
            chart.Press(Box(0, 2, grip: true));
            Assert.True(chart.IsDraggingProgress);
            chart.PointerMove(Box(0, 4, grip: false), ModifierKeys.None);
            view.UpdateNow();
            Assert.Equal(4, chart.ProgressDragTarget);
            var preview = Assert.Single(asked);
            Assert.True(preview.IsPreview);
            Assert.Equal((2, 4), (preview.Day, preview.TargetDay));
            chart.PointerMove(new Point(4 * Dw + 30, TimelineLayout.HeaderHeight + TimelineLayout.ProgressRowHeight + 30), ModifierKeys.None);
            Assert.Equal(-1, chart.ProgressDragTarget);   // another row is not a target
            chart.PointerMove(Box(0, 6, grip: false), ModifierKeys.None);
            chart.Release(Box(0, 6, grip: false));
            Assert.False(chart.IsDraggingProgress);
            var move = Assert.Single(seen);
            Assert.Equal(TimelineProgressAction.Move, move.Action);
            Assert.Equal((2, 6), (move.Day, move.TargetDay));
            Assert.Equal(new DateTime(2026, 11, 22), move.TargetDate);

            chart.Press(Box(0, 2, grip: true));
            chart.Release(Box(0, 2, grip: true));   // dropped where it started
            Assert.Single(seen);
        });

        [Fact]
        public void RejectionFlashesTheDay() => Sta(() =>
        {
            var view = Arranged(Model());
            view.ActualProgressRequested += (s, e) => e.Reject("No.");
            Assert.True(view.BeginActualProgressEdit("A0", 2));
            SetEditorText(view, "3");
            view.CommitActualProgressEdit();
            Assert.Equal("No.", view.LastBlockedReason);
        });

        [Fact]
        public void MenusOfferRemoveOnABoxAndAddWhenTheHostAllows() => Sta(() =>
        {
            var view = Arranged(Model());
            bool allow = false;
            view.ActualProgressQuery += (s, e) => { if (!e.IsPreview) e.CanAdd = allow && e.Day == 4; };
            var chart = view.ChartPanel;
            Assert.Equal("Eliminar avance real", view.OpenProgressMenu(chart.ProgressHit(Box(0, 2, grip: false)), chart, new Point()));
            Assert.Null(view.OpenProgressMenu(chart.ProgressHit(Box(0, 4, grip: false)), chart, new Point()));
            allow = true;
            Assert.Equal("Agregar avance real", view.OpenProgressMenu(chart.ProgressHit(Box(0, 4, grip: false)), chart, new Point()));
            Assert.Null(view.OpenProgressMenu(chart.ProgressHit(Box(0, 7, grip: false)), chart, new Point()));
            view.IsEditable = false;
            Assert.Null(view.OpenProgressMenu(chart.ProgressHit(Box(0, 2, grip: false)), chart, new Point()));
        });

        private static void SetEditorText(TimelineView view, string text)
        {
            var editor = (System.Windows.Controls.TextBox)typeof(TimelineView)
                .GetField("_progressEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(view);
            editor.Text = text;
        }
    }
}
