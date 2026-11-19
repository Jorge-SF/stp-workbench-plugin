using System;
using System.Globalization;

namespace Maptek.Vulcan.Stp.Bridge.Json
{
    /// <summary>
    /// Naive civil datetimes and dates as Python writes and reads them on the wire.
    /// Values are <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/> and whole-microsecond ticks.
    /// </summary>
    public static class PyDateTime
    {
        /// <summary>
        /// Parse a naive ISO datetime accepted by Python 3.11 <c>datetime.fromisoformat</c>, restricted to the
        /// extended forms STP uses: <c>YYYY-MM-DD</c> (only when <paramref name="allowDateOnly"/>), or
        /// <c>YYYY-MM-DDTHH[:MM[:SS[.f{1,6}]]]</c>. Offsets / <c>Z</c> are rejected (the validators require naive).
        /// Python's basic forms (<c>20260301T0930</c>) and non-<c>T</c> separators are rejected; STP never emits them.
        /// </summary>
        public static bool TryParseDateTime(string text, bool allowDateOnly, out DateTime value)
        {
            value = default;
            if (text == null) return false;
            if (!TryDate(text, out var date)) return false;
            if (text.Length == 10)
            {
                if (!allowDateOnly) return false;
                value = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                return true;
            }
            if (text[10] != 'T') return false;
            var rest = text.Substring(11);
            int hour, minute = 0, second = 0, micro = 0;
            if (rest.Length < 2 || !TwoDigits(rest, 0, out hour)) return false;
            var i = 2;
            if (i < rest.Length)
            {
                if (rest[i] != ':' || !TwoDigits(rest, i + 1, out minute)) return false;
                i += 3;
                if (i < rest.Length)
                {
                    if (rest[i] != ':' || !TwoDigits(rest, i + 1, out second)) return false;
                    i += 3;
                    if (i < rest.Length)
                    {
                        if (rest[i] != '.' && rest[i] != ',') return false;
                        var fraction = rest.Substring(i + 1);
                        if (fraction.Length < 1 || fraction.Length > 6) return false;
                        foreach (var c in fraction) if (c < '0' || c > '9') return false;
                        micro = int.Parse(fraction.PadRight(6, '0'), CultureInfo.InvariantCulture);
                        i = rest.Length;
                    }
                }
            }
            if (hour > 23 || minute > 59 || second > 59) return false;
            value = DateTime.SpecifyKind(date.AddHours(hour).AddMinutes(minute).AddSeconds(second).AddTicks(micro * 10L), DateTimeKind.Unspecified);
            return true;
        }

        public static DateTime ParseDateTime(string text, bool allowDateOnly, string label)
        {
            if (!TryParseDateTime(text, allowDateOnly, out var value)) throw new WireFormatException(label + " must be a naive ISO datetime");
            return value;
        }

        /// <summary>Python <c>date.fromisoformat</c> for the <c>YYYY-MM-DD</c> form.</summary>
        public static DateTime ParseDate(string text, string label)
        {
            if (text == null || text.Length != 10 || !TryDate(text, out var date)) throw new WireFormatException(label + " must be an ISO date");
            return date;
        }

        private static bool TryDate(string text, out DateTime date)
        {
            date = default;
            if (text.Length < 10 || text[4] != '-' || text[7] != '-') return false;
            for (var i = 0; i < 10; i++)
            {
                if (i == 4 || i == 7) continue;
                if (text[i] < '0' || text[i] > '9') return false;
            }
            var year = int.Parse(text.Substring(0, 4), CultureInfo.InvariantCulture);
            var month = int.Parse(text.Substring(5, 2), CultureInfo.InvariantCulture);
            var day = int.Parse(text.Substring(8, 2), CultureInfo.InvariantCulture);
            if (year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
            date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
            return true;
        }

        private static bool TwoDigits(string text, int index, out int value)
        {
            value = 0;
            if (index + 2 > text.Length) return false;
            var a = text[index];
            var b = text[index + 1];
            if (a < '0' || a > '9' || b < '0' || b > '9') return false;
            value = (a - '0') * 10 + (b - '0');
            return true;
        }

        /// <summary>Python <c>datetime.isoformat()</c>: seconds always, <c>.ffffff</c> only when microseconds are non-zero.</summary>
        public static string IsoFormat(DateTime value)
        {
            RequireMicroseconds(value);
            var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            var micro = Microsecond(value);
            return micro == 0 ? text : text + "." + micro.ToString("000000", CultureInfo.InvariantCulture);
        }

        /// <summary>The snapshot's <c>_iso</c>: like isoformat but trailing fractional zeros are removed.</summary>
        public static string SnapshotIso(DateTime value)
        {
            var text = IsoFormat(value);
            return Microsecond(value) == 0 ? text : text.TrimEnd('0');
        }

        /// <summary>Python <c>date.isoformat()</c>.</summary>
        public static string DateIso(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static int Microsecond(DateTime value) => (int)(value.Ticks % TimeSpan.TicksPerSecond / 10);

        private static void RequireMicroseconds(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) throw new WireFormatException("STP datetimes are naive civil times, not UTC");
            if (value.Ticks % 10 != 0) throw new WireFormatException("STP datetimes have microsecond precision");
        }
    }
}
