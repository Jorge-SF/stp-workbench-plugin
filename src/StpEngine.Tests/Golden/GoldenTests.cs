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
        public static IEnumerable<object[]> Cases() => GoldenCaseRunner.AllCases().Select(c => new object[] { c.Fixture, c.Case });

        [Theory]
        [MemberData(nameof(Cases))]
        public void CaseMatchesGolden(string fixture, string caseName)
        {
            var (actual, canonical) = GoldenCaseRunner.Run(fixture, caseName);
            string expectedDir = Path.Combine(GoldenCaseRunner.GoldenDir, "expected");
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
