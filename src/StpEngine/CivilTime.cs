using System.Globalization;
using System.Text.RegularExpressions;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>The components of a naive civil date-time (no zone), as parsed by <see cref="CivilTime.Parse"/>.</summary>
    public readonly struct CivilParts
    {
        internal CivilParts(int year, int month, int day, int hour, int minute, int second, int micros, bool hasTime)
        {
            Year = year;
            Month = month;
            Day = day;
            Hour = hour;
            Minute = minute;
            Second = second;
            Micros = micros;
            HasTime = hasTime;
        }

        /// <summary>Gets the year, 1-9999.</summary>
        public int Year { get; }

        /// <summary>Gets the month, 1-12.</summary>
        public int Month { get; }

        /// <summary>Gets the day of month.</summary>
        public int Day { get; }

        /// <summary>Gets the hour, 0-23.</summary>
        public int Hour { get; }

        /// <summary>Gets the minute, 0-59.</summary>
        public int Minute { get; }

        /// <summary>Gets the second, 0-59.</summary>
        public int Second { get; }

        /// <summary>Gets the microseconds, 0-999999.</summary>
        public int Micros { get; }

        /// <summary>Gets a value indicating whether the text had a <c>T</c> time part.</summary>
        public bool HasTime { get; }
    }

    /// <summary>
    /// Naive civil time: ISO-like text <c>YYYY-MM-DD[THH:MM[:SS[.ffffff]]]</c>, microsecond scalars, whole-day arithmetic.
    /// </summary>
    /// <remarks>Port of planner_logic.js L3-64. All arithmetic is proleptic Gregorian with no time zone.</remarks>
    public static class CivilTime
    {
        /// <summary>Microseconds per civil day. JS: <c>DAY_US</c>, L3.</summary>
        public const long DayMicroseconds = 86_400_000_000L;

        // JS: CIVIL, L4. [0-9] instead of \d (JS \d is ASCII-only) and \z instead of $ (JS $ is end-of-input).
        private static readonly Regex CivilPattern = new Regex(
            @"^([0-9]{4})-([0-9]{2})-([0-9]{2})(?:T([0-9]{2}):([0-9]{2})(?::([0-9]{2})(?:\.([0-9]{1,6}))?)?)?\z",
            RegexOptions.CultureInvariant);

        // Any shift larger than this lands outside years 0001-9999, so it is rejected before the day arithmetic.
        private const long MaxDayShift = 10_000_000L;

        /// <summary>Parses civil text. JS: <c>civilParts</c>, L12-21.</summary>
        /// <param name="value">The civil text.</param>
        /// <returns>The parsed components.</returns>
        /// <exception cref="EngineException"><c>fecha civil inválida[: value]</c>.</exception>
        public static CivilParts Parse(string value)
        {
            if (value == null)
            {
                throw EngineException.Fail("fecha civil inválida");
            }

            Match match = CivilPattern.Match(value);
            if (!match.Success)
            {
                throw EngineException.Fail($"fecha civil inválida: {value}");
            }

            int year = Int(match.Groups[1].Value), month = Int(match.Groups[2].Value), day = Int(match.Groups[3].Value);
            int hour = Int(Group(match, 4, "00")), minute = Int(Group(match, 5, "00")), second = Int(Group(match, 6, "00"));
            string fraction = Group(match, 7, string.Empty);
            if (year < 1 || year > 9999 || month < 1 || month > 12 || day < 1 || day > DaysInMonth(year, month)
                || hour > 23 || minute > 59 || second > 59)
            {
                throw EngineException.Fail($"fecha civil inválida: {value}");
            }

            int micros = Int((fraction + "000000").Substring(0, 6));
            return new CivilParts(year, month, day, hour, minute, second, micros, match.Groups[4].Success);
        }

        /// <summary>Days in a Gregorian month. JS: <c>daysInMonth</c>, L23.</summary>
        /// <param name="year">The year.</param>
        /// <param name="month">The month, 1-12.</param>
        /// <returns>28-31.</returns>
        public static int DaysInMonth(int year, int month)
        {
            if (month == 2)
            {
                return year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28;
            }

            return month == 4 || month == 6 || month == 9 || month == 11 ? 30 : 31;
        }

        /// <summary>Day number counted from 0000-03-01. JS: <c>dayNumber</c>, L24-30.</summary>
        /// <param name="year">The year.</param>
        /// <param name="month">The month.</param>
        /// <param name="day">The day of month.</param>
        /// <returns>The day number.</returns>
        public static long DayNumber(long year, long month, long day)
        {
            long adjustedYear = year - (month <= 2 ? 1 : 0);
            long era = FloorDiv(adjustedYear, 400);
            long yoe = adjustedYear - (era * 400);
            long mp = month + (month > 2 ? -3 : 9);
            return (era * 146097) + ((yoe * 365) + FloorDiv(yoe, 4) - FloorDiv(yoe, 100) + FloorDiv((153 * mp) + 2, 5) + day - 1);
        }

        /// <summary>Inverse of <see cref="DayNumber"/>. JS: <c>dateFromDayNumber</c>, L31-37.</summary>
        /// <param name="number">The day number.</param>
        /// <returns>(year, month, day); the year may fall outside 1-9999.</returns>
        public static (long Year, long Month, long Day) DateFromDayNumber(long number)
        {
            long era = FloorDiv(number, 146097), doe = number - (era * 146097);
            long yoe = FloorDiv(doe - FloorDiv(doe, 1460) + FloorDiv(doe, 36524) - FloorDiv(doe, 146096), 365);
            long y = yoe + (era * 400), doy = doe - ((365 * yoe) + FloorDiv(yoe, 4) - FloorDiv(yoe, 100));
            long mp = FloorDiv((5 * doy) + 2, 153), d = doy - FloorDiv((153 * mp) + 2, 5) + 1;
            return (y + (mp >= 10 ? 1 : 0), mp + (mp < 10 ? 3 : -9), d);
        }

        /// <summary>Microseconds since 0000-03-01T00:00. JS: <c>civilScalar</c>, L38 (BigInt there; it fits a long).</summary>
        /// <param name="value">The civil text.</param>
        /// <returns>The scalar.</returns>
        public static long Scalar(string value)
        {
            CivilParts part = Parse(value);
            return (DayNumber(part.Year, part.Month, part.Day) * DayMicroseconds)
                + ((((part.Hour * 3600L) + (part.Minute * 60L) + part.Second) * 1_000_000L) + part.Micros);
        }

        /// <summary>The <c>YYYY-MM-DD</c> part of civil text. JS: <c>civilDate</c>, L39-42.</summary>
        /// <param name="value">The civil text.</param>
        /// <returns>The date text.</returns>
        public static string Date(string value)
        {
            CivilParts part = Parse(value);
            return DateText(part.Year, part.Month, part.Day);
        }

        /// <summary>
        /// Adds whole days and re-renders in the input's shape (date-only stays date-only; a zero fraction is dropped,
        /// otherwise trailing zeros are trimmed). JS: <c>addCivilDays</c>, L43-51.
        /// </summary>
        /// <param name="value">The civil text.</param>
        /// <param name="days">Days to add (may be negative).</param>
        /// <returns>The shifted civil text.</returns>
        /// <exception cref="EngineException"><c>CIVIL_RANGE</c> when the result leaves years 0001-9999.</exception>
        public static string AddDays(string value, long days)
        {
            CivilParts part = Parse(value);
            if (days > MaxDayShift || days < -MaxDayShift)
            {
                throw CivilRangeError();
            }

            var date = DateFromDayNumber(DayNumber(part.Year, part.Month, part.Day) + days);
            if (date.Year < 1 || date.Year > 9999)
            {
                throw CivilRangeError();
            }

            string dateText = DateText(date.Year, date.Month, date.Day);
            if (!part.HasTime)
            {
                return dateText;
            }

            string fraction = part.Micros != 0 ? "." + Pad(part.Micros, 6).TrimEnd('0') : string.Empty;
            return $"{dateText}T{Pad(part.Hour)}:{Pad(part.Minute)}:{Pad(part.Second)}{fraction}";
        }

        /// <summary>Requires an exact midnight and renders it as <c>YYYY-MM-DDT00:00:00</c>. JS: <c>midnightText</c>, L52-56.</summary>
        /// <param name="value">The civil text.</param>
        /// <returns>The normalized midnight.</returns>
        /// <exception cref="EngineException"><c>horizonte civil inválido</c>.</exception>
        public static string MidnightText(string value)
        {
            CivilParts part = Parse(value);
            if (!part.HasTime || part.Hour != 0 || part.Minute != 0 || part.Second != 0 || part.Micros != 0)
            {
                throw EngineException.Fail("horizonte civil inválido");
            }

            return DateText(part.Year, part.Month, part.Day) + "T00:00:00";
        }

        /// <summary>Whole civil days between two dates. JS: <c>horizonDayCount</c>, L61-64.</summary>
        /// <param name="start">The first date.</param>
        /// <param name="finish">The last date.</param>
        /// <returns><paramref name="finish"/> day number minus <paramref name="start"/> day number.</returns>
        public static long DayCount(string start, string finish)
        {
            CivilParts first = Parse(start), last = Parse(finish);
            return DayNumber(last.Year, last.Month, last.Day) - DayNumber(first.Year, first.Month, first.Day);
        }

        internal static string DateText(long year, long month, long day) => $"{Pad(year, 4)}-{Pad(month)}-{Pad(day)}";

        internal static string Pad(long number, int width = 2) => number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

        internal static long FloorDiv(long a, long b)
        {
            long q = a / b;
            return (a % b != 0) && ((a < 0) != (b < 0)) ? q - 1 : q;
        }

        private static EngineException CivilRangeError() =>
            EngineException.Fail("fecha civil fuera del rango compatible (años 0001 a 9999)", EngineException.CivilRange);

        private static int Int(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

        private static string Group(Match match, int index, string fallback) =>
            match.Groups[index].Success ? match.Groups[index].Value : fallback;
    }
}
