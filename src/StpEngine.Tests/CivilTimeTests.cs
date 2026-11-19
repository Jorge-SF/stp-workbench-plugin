using System;
using System.Globalization;
using Xunit;

namespace Maptek.Vulcan.Stp.Engine.Tests
{
    /// <summary>Calendar arithmetic of <see cref="CivilTime"/> cross-checked against <see cref="DateTime"/> over the whole civil range.</summary>
    public class CivilTimeTests
    {
        [Fact]
        public void DayNumbersMatchDateTimeAcrossTheCivilRange()
        {
            long origin = CivilTime.DayNumber(1, 1, 1);
            for (var date = new DateTime(1, 1, 1); date.Year < 9999; date = date.AddDays(97))
            {
                long number = CivilTime.DayNumber(date.Year, date.Month, date.Day);
                Assert.Equal((long)(date - DateTime.MinValue).TotalDays, number - origin);
                Assert.Equal((date.Year, date.Month, date.Day), ((int, int, int))ToInts(CivilTime.DateFromDayNumber(number)));
            }
        }

        [Theory]
        [InlineData("2026-03-02T06:30:00.250", 1, "2026-03-03T06:30:00.25")]
        [InlineData("2026-03-02T06:30:00.000001", -1, "2026-03-01T06:30:00.000001")]
        [InlineData("2026-03-02T06:30:00.000", 1, "2026-03-03T06:30:00")]
        [InlineData("2026-03-02T06:30", 1, "2026-03-03T06:30:00")]
        [InlineData("2024-02-28", 1, "2024-02-29")]
        [InlineData("2023-02-28", 1, "2023-03-01")]
        [InlineData("2000-03-01", -1, "2000-02-29")]
        [InlineData("1900-03-01", -1, "1900-02-28")]
        public void AddDaysRendersLikeJs(string value, long days, string expected) => Assert.Equal(expected, CivilTime.AddDays(value, days));

        [Theory]
        [InlineData("9999-12-31T00:00:00", 1)]
        [InlineData("0001-01-01", -1)]
        [InlineData("2026-01-01", long.MaxValue)]
        public void AddDaysOutsideTheRangeIsCivilRange(string value, long days)
        {
            var error = Assert.Throws<EngineException>(() => CivilTime.AddDays(value, days));
            Assert.Equal(EngineException.CivilRange, error.Code);
            Assert.Equal("fecha civil fuera del rango compatible (años 0001 a 9999)", error.Message);
        }

        [Theory]
        [InlineData("2026-02-29")]
        [InlineData("2026-13-01")]
        [InlineData("0000-01-01")]
        [InlineData("2026-01-01T24:00:00")]
        [InlineData("2026-01-01T00:00:00.1234567")]
        [InlineData("2026-01-01\n")]
        [InlineData("2026-01-01T00:00:00Z")]
        [InlineData("٢٠٢٦-01-01")]
        public void InvalidCivilTextIsRejected(string value)
        {
            var error = Assert.Throws<EngineException>(() => CivilTime.Parse(value));
            Assert.Equal("fecha civil inválida: " + value, error.Message);
        }

        [Fact]
        public void ScalarIsMicrosecondsSinceDayZero()
        {
            long day = CivilTime.DayNumber(2026, 3, 2);
            Assert.Equal((day * CivilTime.DayMicroseconds) + (((6 * 3600) + (30 * 60)) * 1_000_000L) + 250_000, CivilTime.Scalar("2026-03-02T06:30:00.25"));
            Assert.Equal(CivilTime.Scalar("2026-03-02T06:30"), CivilTime.Scalar("2026-03-02T06:30:00.000"));
            Assert.Equal("2026-03-02T00:00:00", CivilTime.MidnightText("2026-03-02T00:00"));
            Assert.Throws<EngineException>(() => CivilTime.MidnightText("2026-03-02"));
            Assert.Equal(366L, CivilTime.DayCount("2024-01-01T00:00:00", "2025-01-01T00:00:00"));
            Assert.Equal("2026-03-02", CivilTime.Date("2026-03-02T23:59:59.999999"));
            Assert.Equal("0042", CivilTime.Pad(42, 4).ToString(CultureInfo.InvariantCulture));
        }

        private static (int, int, int) ToInts((long Year, long Month, long Day) date) => ((int)date.Year, (int)date.Month, (int)date.Day);
    }
}
