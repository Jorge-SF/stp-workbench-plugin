using System;
using System.Collections.Generic;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline.Layout
{
    /// <summary>Row and block under a point: row index (-1 none) and block index in that row (-1 none).</summary>
    public readonly struct TimelineHit
    {
        public static readonly TimelineHit None = new TimelineHit(-1, -1);

        public TimelineHit(int row, int block)
        {
            Row = row;
            Block = block;
        }

        public int Row { get; }
        public int Block { get; }
        public override string ToString() => $"row {Row}, block {Block}";
    }

    /// <summary>
    /// Pure math behind the editing interactions (no WPF input or drawing): hit testing, pixel to day delta, what a
    /// move carries along, the completed-work lock, and keyboard block navigation. Unit-tested directly.
    /// </summary>
    public static class TimelineEditMath
    {
        /// <summary>Spanish reason shown when a move touches locked completed work (planner outcome
        /// <c>completed-work</c>, reference/web/timeline_logic.js <c>humanPlannerOutcome</c>).</summary>
        public const string CompletedWorkReason = "El avance completado está protegido y no se puede mover.";

        /// <summary>Shown when an activity has no pending work to move.</summary>
        public const string NoPendingWorkReason = "La actividad no tiene trabajo pendiente.";

        /// <summary>Block rows are inset by this much from the row top and bottom (mockup: y + 6, height RH - 12).</summary>
        public const double BlockInset = 6;

        /// <summary>
        /// Row and block under a content point (content = below the header, scroll offsets already added).
        /// The block band is a little more forgiving than the drawn block (2 px). Hidden completed blocks do not hit.
        /// </summary>
        public static TimelineHit HitTest(TimelineLayout layout, IReadOnlyList<ITimelineRow> rows, double contentX, double contentY, bool showCompleted)
        {
            if (layout == null || rows == null) return TimelineHit.None;
            int row = layout.RowAt(contentY);
            if (row < 0 || row >= rows.Count) return TimelineHit.None;
            var r = rows[row];
            int blockIndex = -1;
            if (r != null && r.Kind == TimelineRowKind.Activity && r.Blocks != null && r.Blocks.Count > 0)
            {
                double inRow = contentY - layout.RowTop(row);
                double inset = BlockInset - 2;
                if (inRow >= inset && inRow <= layout.RowHeight(row) - inset)
                {
                    int day = layout.XToDay(contentX);
                    int k = FirstBlockAtOrAfter(r.Blocks, day);
                    if (k < r.Blocks.Count && r.Blocks[k].Day == day && (showCompleted || !r.Blocks[k].IsCompleted))
                        blockIndex = k;
                }
            }
            return new TimelineHit(row, blockIndex);
        }

        /// <summary>Index of the first block with Day &gt;= day (blocks are sorted by day).</summary>
        public static int FirstBlockAtOrAfter(IReadOnlyList<TimelineBlock> blocks, int day)
        {
            int lo = 0, hi = blocks.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (blocks[mid].Day < day) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Day column under the pointer, snapped down to whole days and clamped to the horizon [0, dayCount - 1]
        /// (the browser's <c>pointerDate</c>: dropping past either end targets the first or last day).
        /// </summary>
        public static int TargetDay(double contentX, double dayWidth, int dayCount)
        {
            if (dayCount <= 0 || double.IsNaN(contentX) || dayWidth <= 0) return 0;
            double day = Math.Floor(contentX / dayWidth);
            if (day < 0) return 0;
            return day > dayCount - 1 ? dayCount - 1 : (int)day;
        }

        /// <summary>Whole days between the pressed block's day and the day under the pointer (0 = no move).</summary>
        public static int DayDelta(int originDay, double contentX, double dayWidth, int dayCount) =>
            TargetDay(contentX, dayWidth, dayCount) - originDay;

        /// <summary>
        /// Indices of the blocks a move carries: <see cref="TimelineMoveScope.Tail"/> = the pressed block and every
        /// later pending block ("mover desde este día"); <see cref="TimelineMoveScope.Activity"/> = every pending block.
        /// Completed blocks never move.
        /// </summary>
        public static List<int> MovedBlocks(IReadOnlyList<TimelineBlock> blocks, int blockIndex, TimelineMoveScope scope)
        {
            var moved = new List<int>();
            if (blocks == null) return moved;
            int first = scope == TimelineMoveScope.Tail ? Math.Max(0, blockIndex) : 0;
            for (int k = first; k < blocks.Count; k++)
                if (!blocks[k].IsCompleted) moved.Add(k);
            return moved;
        }

        /// <summary>
        /// Whether a drag / keyboard move may start from this block. Locked completed work is rejected: the pressed
        /// block itself must be pending (a completed block can't be dragged), a fully completed (locked) activity
        /// can't move, and there must be pending work to carry.
        /// </summary>
        public static bool CanMove(ITimelineRow row, int blockIndex, TimelineMoveScope scope, out string reason)
        {
            reason = null;
            if (row == null || row.Kind != TimelineRowKind.Activity || row.Blocks == null || row.Blocks.Count == 0)
            {
                reason = NoPendingWorkReason;
                return false;
            }
            if (blockIndex >= row.Blocks.Count) { reason = NoPendingWorkReason; return false; }
            if (row.IsLocked || (blockIndex >= 0 && row.Blocks[blockIndex].IsCompleted))
            {
                reason = CompletedWorkReason;
                return false;
            }
            if (blockIndex < 0 && scope == TimelineMoveScope.Tail) { reason = NoPendingWorkReason; return false; }
            if (MovedBlocks(row.Blocks, blockIndex, scope).Count == 0)
            {
                reason = scope == TimelineMoveScope.Tail ? CompletedWorkReason : NoPendingWorkReason;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Keyboard Left/Right: the next block after (step &gt; 0) or before (step &lt; 0) <paramref name="fromDay"/>.
        /// <paramref name="fromDay"/> &lt; 0 means "nothing focused": Right gives the first block, Left the last.
        /// Returns -1 when there is none (the focus stays put).
        /// </summary>
        public static int NextBlockIndex(IReadOnlyList<TimelineBlock> blocks, int fromDay, int step)
        {
            if (blocks == null || blocks.Count == 0) return -1;
            if (fromDay < 0) return step > 0 ? 0 : blocks.Count - 1;
            if (step > 0)
            {
                int k = FirstBlockAtOrAfter(blocks, fromDay + 1);
                return k < blocks.Count ? k : -1;
            }
            return FirstBlockAtOrAfter(blocks, fromDay) - 1;
        }

        /// <summary>Keyboard Up/Down: the block of the new row nearest to the focused day (ties go to the earlier one).</summary>
        public static int NearestBlockIndex(IReadOnlyList<TimelineBlock> blocks, int day)
        {
            if (blocks == null || blocks.Count == 0) return -1;
            int k = FirstBlockAtOrAfter(blocks, day);
            if (k >= blocks.Count) return blocks.Count - 1;
            if (k == 0 || blocks[k].Day == day) return k;
            return day - blocks[k - 1].Day <= blocks[k].Day - day ? k - 1 : k;
        }

        /// <summary>Index of the block on <paramref name="day"/>, or -1.</summary>
        public static int BlockOnDay(IReadOnlyList<TimelineBlock> blocks, int day)
        {
            if (blocks == null || day < 0) return -1;
            int k = FirstBlockAtOrAfter(blocks, day);
            return k < blocks.Count && blocks[k].Day == day ? k : -1;
        }

        /// <summary>"+3 días", "−1 día", "0 días" (the offset shown while dragging).</summary>
        public static string DeltaText(int delta)
        {
            string sign = delta > 0 ? "+" : delta < 0 ? "−" : string.Empty;
            int n = Math.Abs(delta);
            return sign + n + (n == 1 ? " día" : " días");
        }
    }
}
