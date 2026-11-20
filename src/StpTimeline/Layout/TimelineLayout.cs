using System;
using System.Collections.Generic;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline.Layout
{
    /// <summary>Half-open index range [First, End).</summary>
    public readonly struct IndexRange
    {
        public IndexRange(int first, int end)
        {
            First = first;
            End = Math.Max(first, end);
        }

        public int First { get; }
        public int End { get; }
        public int Count => End - First;
        public bool IsEmpty => End <= First;
        public bool Contains(int i) => i >= First && i < End;
        public override string ToString() => $"[{First}, {End})";
    }

    /// <summary>
    /// Pure layout math of the timeline: row tops (rows have two heights, so a prefix sum), row virtualization
    /// (binary search), and day &lt;-&gt; x. All "content" coordinates start at 0 below the header and to the
    /// right of the grid; the control subtracts the scroll offsets. No WPF types, so it is unit-tested directly.
    /// </summary>
    public sealed class TimelineLayout
    {
        // Mockup sizes (planner_layout.html: RH, GH, HH, CW).
        public const double ActivityRowHeight = 30;
        public const double GroupRowHeight = 24;
        public const double HeaderHeight = 40;
        public const double DefaultDayWidth = 44;
        public const double MinDayWidth = 8;
        public const double MaxDayWidth = 240;

        private readonly double[] _tops; // _tops[i] = top of row i; _tops[n] = extent height

        public TimelineLayout(IReadOnlyList<TimelineRowKind> kinds, double dayWidth = DefaultDayWidth)
        {
            if (kinds == null) throw new ArgumentNullException(nameof(kinds));
            _tops = new double[kinds.Count + 1];
            for (int i = 0; i < kinds.Count; i++)
                _tops[i + 1] = _tops[i] + HeightOf(kinds[i]);
            DayWidth = CoerceDayWidth(dayWidth);
        }

        public static double HeightOf(TimelineRowKind kind) =>
            kind == TimelineRowKind.Group ? GroupRowHeight : ActivityRowHeight;

        public static double CoerceDayWidth(double width) =>
            double.IsNaN(width) ? DefaultDayWidth : Math.Max(MinDayWidth, Math.Min(MaxDayWidth, width));

        public double DayWidth { get; set; }

        public int RowCount => _tops.Length - 1;

        /// <summary>Total height of all rows (header excluded).</summary>
        public double ExtentHeight => _tops[RowCount];

        public double RowTop(int row) => _tops[row];

        public double RowHeight(int row) => _tops[row + 1] - _tops[row];

        /// <summary>Row under a content y, or -1 outside the rows.</summary>
        public int RowAt(double y)
        {
            if (RowCount == 0 || y < 0 || y >= ExtentHeight || double.IsNaN(y)) return -1;
            // Largest i with _tops[i] <= y.
            int lo = 0, hi = RowCount - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_tops[mid] <= y) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Rows that intersect the viewport [offsetY, offsetY + height).</summary>
        public IndexRange VisibleRows(double offsetY, double viewportHeight)
        {
            if (RowCount == 0 || viewportHeight <= 0) return new IndexRange(0, 0);
            double top = Math.Max(0, offsetY), bottom = offsetY + viewportHeight;
            if (bottom <= 0 || top >= ExtentHeight) return new IndexRange(0, 0);
            int first = RowAt(top);
            int last = bottom >= ExtentHeight ? RowCount - 1 : RowAt(bottom);
            // A row that starts exactly at the bottom edge is not visible.
            if (last > first && _tops[last] >= bottom) last--;
            return new IndexRange(first, last + 1);
        }

        public double ExtentWidth(int dayCount) => Math.Max(0, dayCount) * DayWidth;

        /// <summary>Content x of the left edge of a (possibly fractional) day.</summary>
        public double DayToX(double day) => day * DayWidth;

        /// <summary>Day column under a content x (floor; may be negative or past the range).</summary>
        public int XToDay(double x) => (int)Math.Floor(x / DayWidth);

        /// <summary>Day columns that intersect the viewport [offsetX, offsetX + width), clamped to [0, dayCount).</summary>
        public IndexRange VisibleDays(double offsetX, double viewportWidth, int dayCount)
        {
            if (dayCount <= 0 || viewportWidth <= 0) return new IndexRange(0, 0);
            int first = Math.Max(0, XToDay(Math.Max(0, offsetX)));
            int end = Math.Min(dayCount, (int)Math.Ceiling((offsetX + viewportWidth) / DayWidth));
            return new IndexRange(Math.Min(first, dayCount), end);
        }

        /// <summary>Fractional day index of an instant, from the date of <paramref name="start"/>.</summary>
        public static double DateToDay(DateTime start, DateTime instant) => (instant - start.Date).TotalDays;

        /// <summary>
        /// New horizontal offset after a zoom from <paramref name="oldDayWidth"/> to <paramref name="newDayWidth"/>
        /// that keeps the instant under <paramref name="anchorX"/> (viewport x) where it is.
        /// </summary>
        public static double ZoomOffset(double oldOffset, double anchorX, double oldDayWidth, double newDayWidth)
        {
            double day = (oldOffset + anchorX) / oldDayWidth;
            return day * newDayWidth - anchorX;
        }

        /// <summary>Clamp a scroll offset to [0, max(0, extent - viewport)].</summary>
        public static double ClampOffset(double offset, double extent, double viewport)
        {
            double max = Math.Max(0, extent - viewport);
            if (double.IsNaN(offset) || offset < 0) return 0;
            return offset > max ? max : offset;
        }

        /// <summary>Offset that brings row into view with the least scrolling (unchanged if already visible).</summary>
        public double ScrollIntoView(int row, double offsetY, double viewportHeight)
        {
            if (row < 0 || row >= RowCount) return offsetY;
            double top = _tops[row], bottom = _tops[row + 1];
            if (top < offsetY) return top;
            if (bottom > offsetY + viewportHeight) return Math.Min(top, bottom - viewportHeight);
            return offsetY;
        }
    }
}
