using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Maptek.Vulcan.Stp.Timeline.Layout;
using Maptek.Vulcan.Stp.Timeline.Model;
using Xunit;

namespace Maptek.Vulcan.Stp.Timeline.Tests
{
    /// <summary>Capacity preview while dragging (DragPreviewChanged, warning ghosts) and green capacity-exempt blocks.</summary>
    public partial class TimelineEditingTests
    {
        private static readonly Color ExemptGreen = Color.FromRgb(0x68, 0xBD, 0x79);

        private static List<TimelineDragPreviewEventArgs> RecordPreview(TimelineView view, Func<TimelineDragPreviewEventArgs, IEnumerable<int>> warn = null)
        {
            var seen = new List<TimelineDragPreviewEventArgs>();
            view.DragPreviewChanged += (s, e) =>
            {
                seen.Add(e);
                if (warn == null || !e.IsActive) return;
                var days = warn(e).ToArray();
                if (days.Length > 0) e.SetWarnings(days, "Capacidad excedida para Rezagado.");
            };
            return seen;
        }

        /// <summary>The chart's pixel at a chart point (the control rendered off-screen at 96 dpi).</summary>
        private static Color Pixel(TimelineView view, Point p)
        {
            // Render the whole control (a child renders at its offset) and read the chart point there.
            var at = view.ChartPanel.TranslatePoint(p, view);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect((int)at.X, (int)at.Y, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }

        /// <summary>A point inside the block of model row <paramref name="row"/> on <paramref name="day"/>, clear of its label.</summary>
        private static Point BlockEdge(int row, int day) => new Point(day * Dw + 5, TimelineLayout.HeaderHeight + 24 + (row - 1) * 30 + TimelineEditMath.BlockInset + 3);

        private static bool Close(Color a, Color b, int tolerance = 6) =>
            Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

        [Fact]
        public void DragPreviewIsRaisedWhenTheTargetOrScopeChangesAndClearedOnDrop() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = RecordPreview(view);
            var chart = view.ChartPanel;
            // A12 (row 13): days 12..16.
            chart.Press(At(13, 13));
            chart.PointerMove(new Point(At(13, 13).X + 10, At(13, 13).Y), ModifierKeys.None);
            var start = Assert.Single(seen);
            Assert.True(start.IsActive);
            Assert.Equal("A12", start.ActivityKey);
            Assert.Equal(0, start.DayDelta);
            Assert.Equal("A12#1", start.Block.Value.Tag);

            chart.PointerMove(At(13, 15), ModifierKeys.None);
            chart.PointerMove(new Point(At(13, 15).X + 5, At(13, 15).Y), ModifierKeys.None);   // same day: nothing new
            Assert.Equal(2, seen.Count);
            Assert.Equal(2, seen[1].DayDelta);
            Assert.Equal(TimelineMoveScope.Tail, seen[1].Scope);
            Assert.Equal(new DateTime(2026, 12, 1), seen[1].TargetDate);

            chart.PointerMove(At(13, 15), ModifierKeys.Shift);   // Shift+drag: whole activity
            Assert.Equal(3, seen.Count);
            Assert.Equal(TimelineMoveScope.Activity, seen[2].Scope);
            Assert.Equal(2, seen[2].DayDelta);

            chart.Release(At(13, 15));
            Assert.Equal(4, seen.Count);
            Assert.False(seen[3].IsActive);
            Assert.Null(seen[3].Row);
            Assert.Empty(view.DragWarningDays);
        });

        [Fact]
        public void WarningDaysFromTheHandlerMarkTheGhostsRed() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            RecordPreview(view, e => e.DayDelta == 0 ? Array.Empty<int>() : new[] { 14 });
            Drag(view, At(13, 13), At(13, 14), release: false);   // A12 days 13..16 -> 14..17

            Assert.Equal(new[] { 14 }, view.DragWarningDays);
            Assert.Equal("Capacidad excedida para Rezagado.", view.DragWarningText);
            view.UpdateNow();
            var warn = Pixel(view, BlockEdge(13, 14));
            var plain = Pixel(view, BlockEdge(13, 15));
            Assert.True(warn.R > warn.B + 12 && warn.R > warn.G + 12, "warning ghost should be pale red (#ffe2df), was " + warn);
            Assert.True(plain.B > plain.R, "plain ghost should be blue, was " + plain);

            // Back over the origin: no preview, no warnings.
            view.ChartPanel.PointerMove(At(13, 13), ModifierKeys.None);
            Assert.Empty(view.DragWarningDays);
            Assert.Null(view.DragWarningText);
            view.ChartPanel.CancelDrag();
        });

        [Fact]
        public void EscapeAndRefusedMovesClearThePreview() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = RecordPreview(view, e => new[] { 18 });
            Drag(view, At(13, 13), At(13, 18), release: false);
            Assert.NotEmpty(view.DragWarningDays);
            Assert.True(view.HandleKey(Key.Escape, ModifierKeys.None));
            Assert.False(seen.Last().IsActive);
            Assert.Empty(view.DragWarningDays);

            // Refused drop: the preview is cleared before the move is asked for, so the rejection sees no preview.
            bool clearedBeforeMove = false;
            view.MoveRequested += (s, e) =>
            {
                clearedBeforeMove = !seen.Last().IsActive && view.DragWarningDays.Count == 0;
                e.Reject("El movimiento viola una precedencia entrante.");
            };
            Drag(view, At(13, 13), At(13, 16));
            Assert.True(clearedBeforeMove);
            Assert.False(seen.Last().IsActive);
            Assert.Equal("El movimiento viola una precedencia entrante.", view.LastBlockedReason);
        });

        [Fact]
        public void HostsCanSetWarningsAfterTheEvent() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            view.SetDragWarnings(new[] { 3 });   // no drag: ignored
            Assert.Empty(view.DragWarningDays);
            Drag(view, At(13, 13), At(13, 15), release: false);
            view.SetDragWarnings(new[] { 15, 16 }, "Frentes excedidos para LHD-01.");
            Assert.Equal(new[] { 15, 16 }, view.DragWarningDays.OrderBy(d => d));
            Assert.Equal("Frentes excedidos para LHD-01.", view.DragWarningText);
            view.UpdateNow();   // badge with the warning text draws without throwing
            view.ChartPanel.Release(At(13, 15));
            Assert.Empty(view.DragWarningDays);
        });

        [Fact]
        public void LockedOrNotEditableDragsRaiseNoPreview() => Sta(() =>
        {
            var view = Arranged(Model(20, 30));
            var seen = RecordPreview(view);
            Drag(view, At(3, 3), At(3, 8));   // completed block
            Assert.Empty(seen);
            view.IsEditable = false;
            Drag(view, At(13, 13), At(13, 15));
            Assert.Empty(seen);
        });

        [Fact]
        public void CapacityExemptBlocksAreDrawnGreenAndRemovingTheExemptionRestoresTheTypeColour() => Sta(() =>
        {
            var model = new TimelineModel { StartDate = new DateTime(2026, 11, 16), DayCount = 30 };
            model.Rows.Add(TimelineRow.Group("g", "Rezagado", Colors.Orange, 1));
            var row = new TimelineRow { Kind = TimelineRowKind.Activity, Key = "A0", Id = "A0", Name = "Act 0", Color = Color.FromRgb(0x2B, 0x6C, 0xB0) };
            row.Blocks.Add(new TimelineBlock(2, "100", tag: "d2"));
            row.Blocks.Add(new TimelineBlock(3, "100", tag: "d3", isCapacityExempt: true));
            row.Blocks.Add(new TimelineBlock(4, "100", isCompleted: true, tag: "d4", isCapacityExempt: true));   // completed wins
            model.Rows.Add(row);
            var view = Arranged(model);

            Assert.True(Close(Pixel(view, BlockEdge(1, 2)), row.Color), "type colour block: " + Pixel(view, BlockEdge(1, 2)));
            Assert.True(Close(Pixel(view, BlockEdge(1, 3)), ExemptGreen), "exempt block: " + Pixel(view, BlockEdge(1, 3)));
            Assert.False(Close(Pixel(view, BlockEdge(1, 4)), ExemptGreen, 20), "completed block: " + Pixel(view, BlockEdge(1, 4)));

            // "Quitar exención": the host rebuilds the block without the flag.
            row.Blocks[1] = new TimelineBlock(3, "100", tag: "d3");
            model.NotifyChanged();
            view.UpdateNow();
            Assert.True(Close(Pixel(view, BlockEdge(1, 3)), row.Color), "after removing the exemption: " + Pixel(view, BlockEdge(1, 3)));
        });
    }
}
