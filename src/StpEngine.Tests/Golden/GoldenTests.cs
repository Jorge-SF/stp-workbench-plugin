using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Engine.Tests.Golden
{
    /// <summary>
    /// Replays every case of tools/golden/fixtures through the C# engine and compares with tools/golden/expected:
    /// structural equality for the case document, byte equality for <c>*.apply.canonical.json</c>.
    /// </summary>
    public class GoldenTests
    {
        /// <summary>
        /// Cases where the C# engine deliberately differs from planner_logic.js, with the reason. The C# result is
        /// pinned in TestData/deviations/&lt;fixture&gt;.&lt;case&gt;.json instead of tools/golden/expected.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, string> Deviations = new Dictionary<string, string>
        {
            ["engine_edges.fraction_tail_roundtrip"] =
                "JS re-renders a fragment moved back to its original instant (.500 -> .5) and then treats it as moved; "
                + "the server rejects that package (movement_provenance_without_shift). C# keeps the original text, "
                + "which gives the same result JS gives for whole-second times (chain_fs.tail_roundtrip).",
        };

        public static IEnumerable<object[]> Cases() => GoldenCaseRunner.AllCases().Select(c => new object[] { c.Fixture, c.Case });

        internal static string DeviationDir => Path.Combine(GoldenCaseRunner.RepoRoot, "src", "StpEngine.Tests", "TestData", "deviations");

        [Theory]
        [MemberData(nameof(Cases))]
        public void CaseMatchesGolden(string fixture, string caseName)
        {
            var (actual, canonical) = GoldenCaseRunner.Run(fixture, caseName);
            bool deviation = Deviations.ContainsKey($"{fixture}.{caseName}");
            string expectedDir = deviation ? DeviationDir : Path.Combine(GoldenCaseRunner.GoldenDir, "expected");
            if (deviation && System.Environment.GetEnvironmentVariable("STP_WRITE_DEVIATIONS") == "1")
            {
                File.WriteAllText(Path.Combine(DeviationDir, $"{fixture}.{caseName}.json"), actual.ToString(Formatting.Indented) + "\n");
            }

            var expected = GoldenJson.Load(Path.Combine(expectedDir, $"{fixture}.{caseName}.json"));
            string difference = FirstDifference(expected, actual, "$");
            Assert.True(difference == null, $"{fixture}.{caseName}: {difference}");

            string canonicalPath = Path.Combine(expectedDir, $"{fixture}.{caseName}.apply.canonical.json");
            if (File.Exists(canonicalPath))
            {
                byte[] expectedBytes = File.ReadAllBytes(canonicalPath);
                Assert.NotNull(canonical);
                Assert.Equal(expectedBytes, new UTF8Encoding(false).GetBytes(canonical));
            }
            else
            {
                Assert.Null(canonical);
            }
        }

        [Fact]
        public void DeviationsStillDifferFromJs()
        {
            foreach (string name in Deviations.Keys)
            {
                var js = GoldenJson.Load(Path.Combine(GoldenCaseRunner.GoldenDir, "expected", name + ".json"));
                var pinned = GoldenJson.Load(Path.Combine(DeviationDir, name + ".json"));
                Assert.True(FirstDifference(js, pinned, "$") != null, $"{name}: JS now matches the C# result; drop the deviation");
            }
        }

        [Fact]
        public void EveryGoldenCaseIsReplayed()
        {
            var index = GoldenJson.Load(Path.Combine(GoldenCaseRunner.GoldenDir, "expected", "index.json"));
            var indexed = index["cases"].Select(c => $"{c["fixture"]}.{c["case"]}").OrderBy(s => s).ToList();
            var replayed = GoldenCaseRunner.AllCases().Select(c => $"{c.Fixture}.{c.Case}").OrderBy(s => s).ToList();
            Assert.Equal(indexed, replayed);
        }

        internal static string FirstDifference(JToken expected, JToken actual, string path)
        {
            if (expected.Type != actual.Type)
            {
                return $"{path}: expected {expected.Type} {expected.ToString(Formatting.None)}, got {actual.Type} {actual.ToString(Formatting.None)}";
            }

            switch (expected)
            {
                case JObject eo:
                    var ao = (JObject)actual;
                    foreach (var name in eo.Properties().Select(p => p.Name).Union(ao.Properties().Select(p => p.Name)))
                    {
                        if (!eo.ContainsKey(name) || !ao.ContainsKey(name))
                        {
                            return $"{path}.{name}: {(eo.ContainsKey(name) ? "missing" : "unexpected")} ({(eo[name] ?? ao[name]).ToString(Formatting.None)})";
                        }

                        string inner = FirstDifference(eo[name], ao[name], $"{path}.{name}");
                        if (inner != null)
                        {
                            return inner;
                        }
                    }

                    return null;
                case JArray ea:
                    var aa = (JArray)actual;
                    for (int i = 0; i < System.Math.Min(ea.Count, aa.Count); i++)
                    {
                        string inner = FirstDifference(ea[i], aa[i], $"{path}[{i}]");
                        if (inner != null)
                        {
                            return inner;
                        }
                    }

                    return ea.Count == aa.Count ? null : $"{path}: expected {ea.Count} items, got {aa.Count}";
                default:
                    return JToken.DeepEquals(expected, actual) ? null : $"{path}: expected {expected.ToString(Formatting.None)}, got {actual.ToString(Formatting.None)}";
            }
        }
    }
}
