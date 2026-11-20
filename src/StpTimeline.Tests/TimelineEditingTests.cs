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
    /// <summary>Pure drag / hit-test math.</summary>
    public class TimelineEditMathTests
    {
        private static List<TimelineBlock> Blocks(int first, int count, int completed = 0) =>
            Enumerable.Range(0, count).Select(k => new TimelineBlock(first + k, "1", k < completed)).ToList();

        [Theory]
        [InlineData(0, 0)]
        [InlineData(43.9, 0)]
        [InlineData(44, 1)]        // a day boundary belongs to the next day
        [InlineData(131.99, 2)]
        [InlineData(132, 3)]
        [InlineData(-5, 0)]        // left of the horizon: first day
        [InlineData(44 * 14 + 30, 13)] // right of the horizon: last day
        public void TargetDaySnapsDownToWholeDaysAndClampsToTheHorizon(double x, int expected) =>
            Assert.Equal(expected, TimelineEditMath.TargetDay(x, 44, 14));

        [Fact]
        public void DayDeltaIsPointerDayMinusPressedDay()
        {
            Assert.Equal(3, TimelineEditMath.DayDelta(5, 8 * 44 + 1, 44, 14));
            Assert.Equal(-5, TimelineEditMath.DayDelta(5, 10, 44, 14));
            Assert.Equal(0, TimelineEditMath.DayDelta(5, 5 * 44 + 43, 44, 14));
            Assert.Equal(8, TimelineEditMath.DayDelta(5, 10_000, 44, 14)); // clamped to day 13
            Assert.Equal(2, TimelineEditMath.DayDelta(5, 7 * 12.5 + 0.1, 12.5, 14)); // other zoom
        }

        [Fact]
        public void MovedBlocksTakeTheTailOrEveryPendingBlockButNeverCompletedOnes()
        {
            var blocks = Blocks(2, 6, completed: 2); // days 2..7, 2 and 3 completed
            Assert.Equal(new[] { 3, 4, 5 }, TimelineEditMath.MovedBlocks(blocks, 3, TimelineMoveScope.Tail));
            Assert.Equal(new[] { 2, 3, 4, 5 }, TimelineEditMath.MovedBlocks(blocks, 1, TimelineMoveScope.Activity));
            Assert.Equal(new[] { 2, 3, 4, 5 }, TimelineEditMath.MovedBlocks(blocks, -1, TimelineMoveScope.Activity));
        }

        [Fact]
        public void LockedCompletedWorkIsRejected()
        {
            var row = new TimelineRow { Kind = TimelineRowKind.Activity, Key = "A" };
            row.Blocks.AddRange(Blocks(0, 5, completed: 2));
            Assert.False(TimelineEditMath.CanMove(row, 0, TimelineMoveScope.Tail, out string reason));
            Assert.Equal(TimelineEditMath.CompletedWorkReason, reason);
            Assert.False(TimelineEditMath.CanMove(row, 1, TimelineMoveScope.Activity, out reason)); // pressed on completed
            Assert.Equal(TimelineEditMath.CompletedWorkReason, reason);
            Assert.True(TimelineEditMath.CanMove(row, 2, TimelineMoveScope.Tail, out reason));
            Assert.Null(reason);
            Assert.True(TimelineEditMath.CanMove(row, -1, TimelineMoveScope.Activity, out _)); // keyboard, no focused block
            Assert.False(TimelineEditMath.CanMove(row, -1, TimelineMoveScope.Tail, out _));

            row.IsLocked = true;
            Assert.False(TimelineEditMath.CanMove(row, 3, TimelineMoveScope.Tail, out reason));
            Assert.Equal(TimelineEditMath.CompletedWorkReason, reason);

            var done = new TimelineRow { Kind = TimelineRowKind.Activity };
            done.Blocks.AddRange(Blocks(0, 3, completed: 3));
            Assert.False(TimelineEditMath.CanMove(done, -1, TimelineMoveScope.Activity, out reason));
            Assert.Equal(TimelineEditMath.NoPendingWorkReason, reason);
            Assert.False(TimelineEditMath.CanMove(TimelineRow.Group("g", "G", Colors.Red, 1), -1, TimelineMoveScope.Activity, out _));
        }

        [Fact]
        public void HitTestFindsRowAndBlockInContentCoordinates()
        {
            var group = TimelineRow.Group("g", "G", Colors.Red, 1);
            var a = new TimelineRow { Kind = TimelineRowKind.Activity, Key = "A" };
            a.Blocks.AddRange(Blocks(3, 2, completed: 1)); // days 3 (completed), 4
            var rows = new List<ITimelineRow> { group, a };
            var layout = new TimelineLayout(rows.Select(r => r.Kind).ToList(), 44);
            double y = 24 + 15;
            Assert.Equal(new TimelineHit(1, 1), TimelineEditMath.HitTest(layout, rows, 4 * 44 + 1, y, true));
            Assert.Equal(new TimelineHit(1, 0), TimelineEditMath.HitTest(layout, rows, 3 * 44, y, true));
            Assert.Equal(new TimelineHit(1, -1), TimelineEditMath.HitTest(layout, rows, 3 * 44, y, false)); // hidden completed
            Assert.Equal(new TimelineHit(1, -1), TimelineEditMath.HitTest(layout, rows, 5 * 44 + 1, y, true)); // empty day
            Assert.Equal(new TimelineHit(1, -1), TimelineEditMath.HitTest(layout, rows, 4 * 44 + 1, 24 + 2, true)); // above the block band
            Assert.Equal(new TimelineHit(0, -1), TimelineEditMath.HitTest(layout, rows, 4 * 44 + 1, 10, true)); // group row
            Assert.Equal(TimelineHit.None, TimelineEditMath.HitTest(layout, rows, 0, 24 + 30 + 1, true)); // below the rows
        }

        [Fact]
        public void KeyboardBlockNavigation()
        {
            var blocks = new List<TimelineBlock> { new TimelineBlock(2, null), new TimelineBlock(3, null), new TimelineBlock(7, null) };
            Assert.Equal(0, TimelineEditMath.NextBlockIndex(blocks, -1, 1));
            Assert.Equal(2, TimelineEditMath.NextBlockIndex(blocks, -1, -1));
            Assert.Equal(1, TimelineEditMath.NextBlockIndex(blocks, 2, 1));
            Assert.Equal(2, TimelineEditMath.NextBlockIndex(blocks, 3, 1));
            Assert.Equal(-1, TimelineEditMath.NextBlockIndex(blocks, 7, 1));
            Assert.Equal(1, TimelineEditMath.NextBlockIndex(blocks, 7, -1));
            Assert.Equal(-1, TimelineEditMath.NextBlockIndex(blocks, 2, -1));
            Assert.Equal(1, TimelineEditMath.NextBlockIndex(blocks, 5, -1)); // from a day without a block

            Assert.Equal(0, TimelineEditMath.NearestBlockIndex(blocks, 0));
            Assert.Equal(1, TimelineEditMath.NearestBlockIndex(blocks, 4));
            Assert.Equal(1, TimelineEditMath.NearestBlockIndex(blocks, 5)); // tie goes to the earlier block
            Assert.Equal(2, TimelineEditMath.NearestBlockIndex(blocks, 6));
            Assert.Equal(2, TimelineEditMath.NearestBlockIndex(blocks, 40));
            Assert.Equal(-1, TimelineEditMath.NearestBlockIndex(new List<TimelineBlock>(), 4));
            Assert.Equal(2, TimelineEditMath.BlockOnDay(blocks, 7));
            Assert.Equal(-1, TimelineEditMath.BlockOnDay(blocks, 6));
        }

        [Fact]
        public void DeltaText()
        {
            Assert.Equal("+3 días", TimelineEditMath.DeltaText(3));
            Assert.Equal("−1 día", TimelineEditMath.DeltaText(-1));
            Assert.Equal("0 días", TimelineEditMath.DeltaText(0));
        }
    }

    /// <summary>Editing on an off-screen control: the input handlers are driven directly.</summary>
    public partial class TimelineEditingTests
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

        /// <summary>Group header, then activity i (model row i + 1) with 5 blocks from day i % (days - 5); days 0..4 completed.</summary>
        private static TimelineModel Model(int activities, int days)
        {
            var model = new TimelineModel { StartDate = new DateTime(2026, 11, 16), DayCount = days };
            model.Rows.Add(TimelineRow.Group("g", "Rezagado", Colors.Orange, activities));
            for (int i = 0; i < activities; i++)
            {
                var row = new TimelineRow { Kind = TimelineRowKind.Activity, Key = "A" + i, Id = "A" + i, Name = "Act " + i };
                int start = i % (days - 5);
                for (int k = 0; k < 5; k++) row.Blocks.Add(new TimelineBlock(start + k, "100", start + k < 5, tag: "A" + i + "#" + k));
                model.Rows.Add(row);
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

        /// <summary>Chart point in the middle of model row <paramref name="row"/> (activities only), day <paramref name="day"/>.</summary>
        private static Point At(int row, double day) => new Point(day * Dw + 20, TimelineLayout.HeaderHeight + 24 + (row - 1) * 30 + 15);

        private static List<TimelineEditEventArgs> Record(TimelineView view)
        {
            var seen = new List<TimelineEditEventArgs>();
            TimelineEditEventHandler add = (s, e) => seen.Add(e);
            view.MoveRequested += add;
            view.DeferRequested += add;
            view.StaggerRequested += add;
            view.AssignResourceRequested += add;
            view.CapacityExemptionRequested += add;
            view.UndoRequested += add;
            view.RedoRequested += add;
            return seen;
        }

        private static void Drag(TimelineView view, Point from, Point to, ModifierKeys mods = ModifierKeys.None, bool release = true)
        {
            var chart = view.ChartPanel;
            chart.Press(from);
            chart.PointerMove(new Point(from.X + 10, from.Y), mods); // past the drag threshold
            chart.PointerMove(to, mods);
            view.UpdateNow();
            if (release) chart.Release(to);
        }

        [Fact]
        public void DraggingABlockRequestsATailMoveSnappedToDays() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = Record(view);
            var started = 0;
            view.BlockDragStarted += (s, e) => started++;
            // A12 (row 13): days 12..16. Press day 13, drop in day 16 (anywhere inside it).
            Drag(view, At(13, 13), new Point(16 * Dw + 40, At(13, 13).Y + 40), release: false);
            Assert.True(view.IsDragging);
            Assert.Equal(3, view.ChartPanel.DragDelta);
            Assert.Equal(new[] { 1, 2, 3, 4 }, view.ChartPanel.DragMovedBlocks);
            view.ChartPanel.Release(new Point(16 * Dw + 40, 0));

            Assert.False(view.IsDragging);
            Assert.Equal(1, started);
            var e = Assert.Single(seen);
            Assert.Equal(TimelineView.MoveRequestedEvent, e.RoutedEvent);
            Assert.Equal(TimelineEditAction.Move, e.Action);
            Assert.Equal(TimelineEditOrigin.Drag, e.Origin);
            Assert.Equal(TimelineMoveScope.Tail, e.Scope);
            Assert.Equal("A12", e.ActivityKey);
            Assert.Equal(1, e.BlockIndex);
            Assert.Equal("A12#1", e.Block.Value.Tag);
            Assert.Equal(3, e.DayDelta);
            Assert.Equal(new DateTime(2026, 11, 29), e.Date);
            Assert.Equal(new DateTime(2026, 12, 2), e.TargetDate);
        });

        [Fact]
        public void ShiftDragMovesTheWholeActivityAndShiftCanToggleMidDrag() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = Record(view);
            Drag(view, At(13, 14), At(13, 12), ModifierKeys.Shift, release: false);
            Assert.Equal(TimelineMoveScope.Activity, view.ChartPanel.DragScope);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, view.ChartPanel.DragMovedBlocks);
            view.ChartPanel.PointerMove(At(13, 12), ModifierKeys.None);
            Assert.Equal(TimelineMoveScope.Tail, view.ChartPanel.DragScope);
            view.ChartPanel.PointerMove(At(13, 11), ModifierKeys.Shift);
            view.ChartPanel.Release(At(13, 11));
            var e = Assert.Single(seen);
            Assert.Equal(TimelineMoveScope.Activity, e.Scope);
            Assert.Equal(-3, e.DayDelta);
        });

        [Fact]
        public void DroppingOnTheSameDayOrPressingEscapeRaisesNothing() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = Record(view);
            Drag(view, At(13, 13), new Point(13 * Dw + 40, At(13, 13).Y));
            Assert.Empty(seen);

            Drag(view, At(13, 13), At(13, 18), release: false);
            Assert.True(view.IsDragging);
            Assert.True(view.HandleKey(Key.Escape, ModifierKeys.None));
            Assert.False(view.IsDragging);
            view.ChartPanel.Release(At(13, 18));
            Assert.Empty(seen);
        });

        [Fact]
        public void LockedCompletedBlocksCannotBeDragged() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = Record(view);
            // A2 (row 3): days 2..6, days 2, 3, 4 completed.
            Drag(view, At(3, 3), At(3, 8), release: false);
            Assert.False(view.IsDragging);
            Assert.Equal(Cursors.No, view.ChartPanel.Cursor);
            view.ChartPanel.Release(At(3, 8));
            Assert.Empty(seen);
            Assert.Equal(TimelineEditMath.CompletedWorkReason, view.LastBlockedReason);
            var flash = Assert.Single(view.Flashes);
            Assert.Equal(3, flash.Row);
            Assert.Equal(new[] { 3 }, flash.Days);

            // The pending part of the same activity can be dragged; only pending blocks travel.
            Drag(view, At(3, 5), At(3, 7), release: false);
            Assert.Equal(new[] { 3, 4 }, view.ChartPanel.DragMovedBlocks);
            view.ChartPanel.Release(At(3, 7));
            Assert.Single(seen);

            // Not editable (host busy): nothing moves and nothing is flagged as blocked.
            view.IsEditable = false;
            Drag(view, At(13, 13), At(13, 15));
            Assert.Single(seen);
        });

        [Fact]
        public void HoverCursorIsNotAllowedOverCompletedWork() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            view.ChartPanel.PointerMove(At(3, 3), ModifierKeys.None);
            Assert.Equal(Cursors.No, view.ChartPanel.Cursor);
            view.ChartPanel.PointerMove(At(3, 5), ModifierKeys.None);
            Assert.Equal(Cursors.Hand, view.ChartPanel.Cursor);
            view.ChartPanel.PointerMove(At(3, 12), ModifierKeys.None);
            Assert.Null(view.ChartPanel.Cursor);
        });

        [Fact]
        public void HostRejectionFlashesTheMovedBlocksWithTheReason() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            view.MoveRequested += (s, e) => e.Reject("El movimiento viola una precedencia entrante.");
            Drag(view, At(13, 14), At(13, 16));
            Assert.Equal("El movimiento viola una precedencia entrante.", view.LastBlockedReason);
            var flash = Assert.Single(view.Flashes);
            Assert.Equal(new[] { 14, 15, 16 }, flash.Days);

            // Async rejection by key.
            view.ShowBlocked("A10", -1, "otra razón");
            Assert.Equal(2, view.Flashes.Count);
            Assert.Equal(new[] { 10, 11, 12, 13, 14 }, view.Flashes[1].Days);
            view.UpdateNow(); // the overlay draws the flashes without throwing
        });

        [Fact]
        public void EditCommandRunsForEveryRequestUnlessHandled() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var command = new RecordingCommand();
            view.EditCommand = command;
            Drag(view, At(13, 13), At(13, 15));
            view.HandleKey(Key.Z, ModifierKeys.Control);
            Assert.Equal(new[] { TimelineEditAction.Move, TimelineEditAction.Undo }, command.Seen.Select(e => e.Action));

            view.UndoRequested += (s, e) => e.Handled = true;
            view.HandleKey(Key.Z, ModifierKeys.Control);
            Assert.Equal(2, command.Seen.Count);
        });

        [Fact]
        public void KeyboardMovesTheSelectionAndRequestsDayMoves() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = Record(view);
            view.ChartPanel.Press(At(13, 13));
            view.ChartPanel.Release(At(13, 13));
            Assert.Equal("A12", view.SelectedKey);
            Assert.Equal(13, view.FocusedDay);

            Assert.True(view.HandleKey(Key.Right, ModifierKeys.None));
            Assert.Equal(14, view.FocusedDay);
            view.HandleKey(Key.Left, ModifierKeys.None);
            view.HandleKey(Key.Left, ModifierKeys.None);
            view.HandleKey(Key.Left, ModifierKeys.None); // stays on the first block
            Assert.Equal(12, view.FocusedDay);

            view.HandleKey(Key.Down, ModifierKeys.None); // A13: days 13..17, nearest to 12 is 13
            Assert.Equal("A13", view.SelectedKey);
            Assert.Equal(13, view.FocusedDay);
            view.HandleKey(Key.Up, ModifierKeys.None);
            Assert.Equal("A12", view.SelectedKey);
            Assert.Equal(13, view.FocusedDay);

            view.HandleKey(Key.Right, ModifierKeys.Control);
            var tail = Assert.Single(seen);
            Assert.Equal(TimelineEditOrigin.Keyboard, tail.Origin);
            Assert.Equal(TimelineMoveScope.Tail, tail.Scope);
            Assert.Equal(1, tail.DayDelta);
            Assert.Equal(1, tail.BlockIndex);
            Assert.Equal(14, view.FocusedDay); // the focus follows the moved day (the model is unchanged here)

            view.HandleKey(Key.Left, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal(TimelineMoveScope.Activity, seen[1].Scope);
            Assert.Equal(-1, seen[1].DayDelta);

            view.HandleKey(Key.Z, ModifierKeys.Control);
            view.HandleKey(Key.Y, ModifierKeys.Control);
            view.HandleKey(Key.Z, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal(new[] { TimelineEditAction.Undo, TimelineEditAction.Redo, TimelineEditAction.Redo }, seen.Skip(2).Select(e => e.Action));
            Assert.Null(seen[2].Row);
        });

        [Fact]
        public void KeyboardMovesAreRejectedLocallyForLockedWorkAndTheHorizonEdge() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = Record(view);
            view.FocusBlock("A2", 3); // completed
            view.HandleKey(Key.Right, ModifierKeys.Control);
            Assert.Empty(seen);
            Assert.Equal(TimelineEditMath.CompletedWorkReason, view.LastBlockedReason);

            view.FocusBlock("A5", 5); // days 5..9, first day of the horizon is 0: -1 is fine
            view.HandleKey(Key.Left, ModifierKeys.Control);
            Assert.Single(seen);

            var model = Model(20, 30);
            model.Rows[1].Blocks.Clear();
            model.Rows[1].Blocks.Add(new TimelineBlock(0, "1"));
            view.Model = model;
            view.FocusBlock("A0", 0);
            view.HandleKey(Key.Left, ModifierKeys.Control);
            Assert.Single(seen);
            Assert.Equal(TimelineView.HorizonRangeReason, view.LastBlockedReason);

            view.IsEditable = false;
            view.FocusBlock("A5", 6);
            view.HandleKey(Key.Right, ModifierKeys.Control);
            Assert.Single(seen);
        });

        [Fact]
        public void ContextMenuHasTheReferenceEntries() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var items = view.OpenBlockMenu(13, 1, view.ChartPanel, null);
            Assert.Equal(new[]
            {
                "Mover desde este día: −1 día", "Mover desde este día: +1 día",
                "Mover actividad: −1 día", "Mover actividad: +1 día",
                "Asignar equipo", "Mandar al siguiente periodo", "Dividir y desfasar",
            }, items.Select(i => i.Header));
            Assert.True(items.All(i => i.IsEnabled));

            // Completed blocks get no menu, like the browser planner.
            Assert.Null(view.OpenBlockMenu(3, 0, view.ChartPanel, null));

            // The host can show the capacity exemption entry and relabel it.
            view.BlockMenuOpening += (s, e) =>
            {
                var exemption = e.Find(TimelineEditAction.ToggleCapacityExemption);
                exemption.IsVisible = true;
                exemption.Header = "Quitar exención";
            };
            items = view.OpenBlockMenu(13, 1, view.ChartPanel, null);
            Assert.Contains("Quitar exención", items.Select(i => i.Header));
        });

        [Fact]
        public void HorizonEdgesDisableTheMatchingMoveEntries() => Sta(() =>
        {
            var model = Model(20, 30);
            model.Rows[1].Blocks.Clear();
            model.Rows[1].Blocks.Add(new TimelineBlock(0, "1"));
            model.Rows[1].Blocks.Add(new TimelineBlock(29, "1"));
            var view = Arranged(model);
            var first = view.DefaultMenuItems(1, 0);
            Assert.False(first[0].IsEnabled); // tail -1 at day 0
            Assert.True(first[1].IsEnabled);
            Assert.False(first[2].IsEnabled); // activity -1
            Assert.False(first[3].IsEnabled); // activity +1: last day is 29
            Assert.Equal(TimelineView.HorizonRangeReason, first[0].DisabledReason);
        });

        [Fact]
        public void MenuEntriesRaiseTheirRequests() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = Record(view);
            var items = view.DefaultMenuItems(13, 1);
            view.RunMenuItem("A12", 13, items[1], view.ChartPanel);      // tail +1
            view.RunMenuItem("A12", 13, items[2], view.ChartPanel);      // activity -1
            view.RunMenuItem("A12", 13, items[4], view.ChartPanel);      // Asignar equipo
            view.RunMenuItem("A12", 13, items[5], view.ChartPanel);      // Exentar Límite
            view.RunMenuItem("A12", 13, items[6], view.ChartPanel);      // Mandar al siguiente periodo
            Assert.Equal(new[] { TimelineEditAction.Move, TimelineEditAction.Move, TimelineEditAction.AssignResource, TimelineEditAction.ToggleCapacityExemption, TimelineEditAction.DeferToNextPeriod },
                seen.Select(e => e.Action));
            Assert.Equal(TimelineView.DeferRequestedEvent, seen[4].RoutedEvent);
            Assert.True(seen.All(e => e.Origin == TimelineEditOrigin.ContextMenu && e.ActivityKey == "A12" && e.BlockIndex == 1));
            Assert.Equal((TimelineMoveScope.Tail, 1), (seen[0].Scope, seen[0].DayDelta));
            Assert.Equal((TimelineMoveScope.Activity, -1), (seen[1].Scope, seen[1].DayDelta));

            // A menu item on a day that no longer has a block (model changed meanwhile) does nothing.
            view.RunMenuItem("A12", 25, items[6], view.ChartPanel);
            Assert.Equal(5, seen.Count);
        });

        [Fact]
        public void StaggerValidatesLikeTheBrowserAndRaisesTheRequest() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = Record(view);
            Assert.Null(view.SubmitStagger("A12", 13, "0", "1", out string error));
            Assert.Equal(TimelineView.StaggerInputReason, error);
            Assert.Null(view.SubmitStagger("A12", 13, "2", "1.5", out error));
            Assert.Equal(TimelineView.StaggerInputReason, error);
            Assert.Empty(seen);

            var e = view.SubmitStagger("A12", 13, " 3 ", "2", out error);
            Assert.Null(error);
            Assert.Same(e, Assert.Single(seen));
            Assert.Equal(TimelineView.StaggerRequestedEvent, e.RoutedEvent);
            Assert.Equal(TimelineEditAction.SplitAndStagger, e.Action);
            Assert.Equal((3, 2), (e.Divisions, e.GapDays));
            Assert.Equal("A12#1", e.Block.Value.Tag);
        });

        [Fact]
        public void TooltipShowsActivityDateQuantityAndState()
        {
            var row = new TimelineRow { Kind = TimelineRowKind.Activity, Id = "REZ-1425-01", Name = "Rezagado TJ-1425-01", Move = TimelineMoveKind.Cascade };
            var date = new DateTime(2026, 11, 26);
            Assert.Equal("REZ-1425-01 · Rezagado TJ-1425-01\njue 26/11/2026\nCantidad: 1.7k\nDesplazada por cascada; se puede mover.",
                TimelineView.BlockToolTipText(row, new TimelineBlock(10, "1.7k"), date));
            Assert.Equal("REZ-1425-01 · Rezagado TJ-1425-01\njue 26/11/2026\n1,700 t · 07:00 — 19:00\nAvance completado: bloqueado, no se puede mover.",
                TimelineView.BlockToolTipText(row, new TimelineBlock(10, "1.7k", true, null, "1,700 t · 07:00 — 19:00"), date));
        }

        [Fact]
        public void DragFramesWith2000RowsRedrawOnlyTheOverlay() => Sta(() =>
        {
            var view = Arranged(Model(2000, 90));
            view.SetVerticalOffset(30 * 1000);
            view.UpdateNow();
            int row = view.Layout.RowAt(view.OffsetY + 200);
            var blocks = view.Rows[row].Blocks;
            int day = blocks.First(b => !b.IsCompleted).Day;
            var chart = view.ChartPanel;
            double y = TimelineLayout.HeaderHeight + view.Layout.RowTop(row) - view.OffsetY + 15;
            var p = new Point(day * Dw + 20 - view.OffsetX, y);
            chart.Press(p);
            chart.PointerMove(new Point(p.X + 10, y), ModifierKeys.None);
            Assert.True(view.IsDragging);
            var times = new List<double>();
            for (int i = 0; i < 200; i++)
            {
                chart.PointerMove(new Point(p.X + (i % 12) * Dw, y), ModifierKeys.None);
                view.UpdateNow();
                times.Add(view.LastRenderMilliseconds);
            }
            chart.CancelDrag();
            times.Sort();
            // An overlay frame draws one activity; generous bound so a loaded CI agent can't flake it.
            Assert.True(times[times.Count / 2] < 8, "median drag frame " + times[times.Count / 2] + " ms");
        });

        private sealed class RecordingCommand : ICommand
        {
            public readonly List<TimelineEditEventArgs> Seen = new List<TimelineEditEventArgs>();
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => parameter is TimelineEditEventArgs;
            public void Execute(object parameter) => Seen.Add((TimelineEditEventArgs)parameter);
        }
    }
}
