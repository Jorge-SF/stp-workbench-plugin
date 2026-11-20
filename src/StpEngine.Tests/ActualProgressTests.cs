using System.Collections.Generic;
using System.Linq;
using Maptek.Vulcan.Stp.Engine.Internal;
using Xunit;

namespace Maptek.Vulcan.Stp.Engine.Tests
{
    /// <summary>Unit checks of <see cref="ActualProgress"/> beyond the golden cases.</summary>
    public class ActualProgressTests
    {
        private static readonly string[] Week = { "2026-11-16", "2026-11-17", "2026-11-18", "2026-11-19", "2026-11-20" };

        [Fact]
        public void KeyIsJsonStringifyOfTheTrimmedIdAndDate()
        {
            Assert.Equal("[\"A-1\",\"2026-11-16\"]", ActualProgress.Key("  A-1 ", "2026-11-16"));
            Assert.Equal("[\"a\\\"b\\\\c\\u0001\\nz\",\"2026-11-16\"]", ActualProgress.Key("a\"b\\c\u0001\nz", "2026-11-16"));
            Assert.Equal("[\"\\ud800x\",\"2026-11-16\"]", ActualProgress.Key("\ud800x", "2026-11-16"));
            Assert.Equal(string.Empty, ActualProgress.Key("A", "2026-11-16\n"));
            Assert.Equal(string.Empty, ActualProgress.Key("A", "2026-13-01"));
            Assert.Equal(string.Empty, ActualProgress.Key(new string('x', 241), "2026-11-16"));
        }

        [Fact]
        public void ObjectMapEnumeratesLikeJs()
        {
            var map = new JsObjectMap<int> { ["b"] = 1, ["10"] = 2, ["a"] = 3, ["2"] = 4, ["01"] = 5 };
            map["b"] = 6;
            Assert.Equal(new[] { "2", "10", "b", "a", "01" }, map.Keys);
            map.Remove("b");
            map["b"] = 7;
            Assert.Equal(new[] { "2", "10", "a", "01", "b" }, map.Keys);
            Assert.False(JsObjectMap<int>.IsArrayIndex("4294967295"));
            Assert.True(JsObjectMap<int>.IsArrayIndex("4294967294"));
        }

        [Fact]
        public void EditsAreImmutable()
        {
            var empty = ActualProgressState.Empty;
            var one = ActualProgress.SetValue(empty, "A", "2026-11-16", 3);
            Assert.False(empty.HasAny);
            Assert.True(one.HasAny);
            Assert.Equal(3, ActualProgress.Value(one, "A", "2026-11-16"));
            Assert.Null(ActualProgress.Value(empty, "A", "2026-11-16"));
            Assert.True(ActualProgress.SetHiddenSlot(empty, "A", "2026-11-16", true).HiddenSlots.Count == 1);
            Assert.False(ActualProgress.SetHiddenSlot(empty, "A", "2026-11-16", true).HasAny);
        }

        [Fact]
        public void SameContentIgnoresOrder()
        {
            var left = ActualProgressState.Create(new[] { Pair("x", 1), Pair("y", 2) }, slots: new[] { "s" });
            var right = ActualProgressState.Create(new[] { Pair("y", 2), Pair("x", 1) }, slots: new[] { "s" });
            Assert.True(left.SameContent(right));
            Assert.False(left.SameContent(ActualProgressState.Create(new[] { Pair("x", 1), Pair("y", 3) }, slots: new[] { "s" })));
            Assert.False(left.SameContent(ActualProgressState.Create(new[] { Pair("x", 1), Pair("y", 2) })));
        }

        [Fact]
        public void DragForwardCarriesTheLaterBoxesAndHidesPlannedOrigins()
        {
            var row = new ProgressRow
            {
                ActivityId = "A",
                Days = Week.Select((d, i) => new ProgressDay { Date = d, Segments = i < 3 ? new[] { new ProgressSegment { Amount = 5 } } : new ProgressSegment[0] }).ToList(),
            };
            var state = ActualProgress.SetValue(ActualProgressState.Empty, "A", Week[1], 4);
            Assert.True(ActualProgress.TryApplySlotCascade(state, row, Week[1], Week[3], out var moved));
            Assert.Null(ActualProgress.Value(moved, "A", Week[1]));
            Assert.Equal(4, ActualProgress.Value(moved, "A", Week[3]));
            Assert.True(ActualProgress.HasHiddenSlot(moved, "A", Week[1]));
            Assert.True(ActualProgress.HasSlot(moved, "A", Week[3]));      // unplanned target: explicit box
            Assert.Equal(new[] { Week[0], Week[3], Week[4] }, ActualProgress.VisibleSlotDates(moved, row));   // 18 -> 20 too
            Assert.False(ActualProgress.TryApplySlotCascade(state, row, Week[1], "2026-12-01", out var same));
            Assert.Same(state, same);
        }

        [Fact]
        public void AddDaysPadsTheYearLikeJs()
        {
            Assert.Equal("2026-12-01", ActualProgress.AddDays("2026-11-30", 1));
            Assert.Equal("10000-01-01", ActualProgress.AddDays("9999-12-31", 1));
            Assert.Equal("0000-12-31", ActualProgress.AddDays("0001-01-01", -1));
            Assert.Equal(string.Empty, ActualProgress.AddDays("bad", 1));
        }

        private static KeyValuePair<string, double> Pair(string key, double value) => new KeyValuePair<string, double>(key, value);
    }
}
