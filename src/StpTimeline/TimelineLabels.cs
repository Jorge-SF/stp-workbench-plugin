using System;
using System.Globalization;

namespace Maptek.Vulcan.Stp.Timeline
{
    /// <summary>Label helpers for view-models that feed the timeline.</summary>
    public static class TimelineLabels
    {
        /// <summary>Spanish weekday abbreviations, Monday first (mockup header).</summary>
        internal static readonly string[] WeekdayNames = { "lun", "mar", "mié", "jue", "vie", "sáb", "dom" };

        /// <summary>
        /// Block label as in the mockup: values of 1000 or more as "1.7k" (one decimal), smaller ones as an integer
        /// (or one decimal when not whole). Invariant culture: the block is too small for grouping separators.
        /// </summary>
        public static string CompactQuantity(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "";
            if (Math.Abs(value) >= 1000) return (value / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            double rounded = Math.Round(value, 1);
            return rounded == Math.Round(rounded)
                ? Math.Round(rounded).ToString("0", CultureInfo.InvariantCulture)
                : rounded.ToString("0.0", CultureInfo.InvariantCulture);
        }

        internal static string Weekday(DateTime date) => WeekdayNames[((int)date.DayOfWeek + 6) % 7];

        internal static bool IsWeekend(DateTime date) =>
            date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
    }
}
