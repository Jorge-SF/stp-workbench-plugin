using System;
using System.IO;
using System.Linq;
using Maptek.Vulcan.Stp.Engine.Tests.Golden;
using Xunit;

namespace Maptek.Vulcan.Stp.Engine.Tests
{
    /// <summary><see cref="JsCompare.LocaleCompare"/> against <c>localeCompare</c> results recorded under Node (TestData/make_locale_compare.mjs).</summary>
    public class JsCompareTests
    {
        private static readonly Newtonsoft.Json.Linq.JToken Data =
            GoldenJson.Load(Path.Combine(GoldenCaseRunner.RepoRoot, "src", "StpEngine.Tests", "TestData", "locale_compare.json"));

        [Fact]
        public void PairwiseSignsMatchNode()
        {
            var mismatches = Data["pairs"]
                .Select(p => (A: (string)p[0], B: (string)p[1], Expected: (int)p[2]))
                .Where(p => JsCompare.LocaleCompare(p.A, p.B) != p.Expected)
                .Select(p => $"'{p.A}' vs '{p.B}': node {p.Expected}, C# {JsCompare.LocaleCompare(p.A, p.B)}")
                .ToList();
            Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Take(20)));
        }

        [Fact]
        public void SortOrderMatchesNode()
        {
            var expected = Data["sorted"].Select(t => (string)t).ToList();
            var actual = expected.OrderBy(s => s, StringComparer.Ordinal).OrderBy(s => s, JsCompare.Locale).ToList();
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void OrdinalIsUtf16CodeUnitOrder()
        {
            Assert.True(JsCompare.Ordinal("B", "a") < 0);
            Assert.True(JsCompare.Ordinal("a", "B") > 0);
            Assert.True(JsCompare.LocaleCompare("a", "B") < 0);
        }
    }
}
