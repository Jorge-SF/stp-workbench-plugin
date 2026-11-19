using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Engine.Tests.Golden
{
    /// <summary>Guards for the golden harness itself and for the engine's dependency rule.</summary>
    public class HarnessSelfTests
    {
        [Fact]
        public void ComparatorDetectsTamperedExpectation()
        {
            var (actual, _) = GoldenCaseRunner.Run("chain_fs", "shift_forward_cascade");
            var tampered = (JObject)actual.DeepClone();
            tampered["fragments"][3]["start"] = "2026-03-05T20:00:01";
            Assert.NotNull(GoldenTests.FirstDifference(tampered, actual, "$"));
            var reordered = (JObject)actual.DeepClone();
            var rows = (JArray)reordered["fragments"];
            var first = rows[0];
            rows.RemoveAt(0);
            rows.Add(first);
            Assert.NotNull(GoldenTests.FirstDifference(reordered, actual, "$"));
            Assert.Null(GoldenTests.FirstDifference(actual.DeepClone(), actual, "$"));
        }

        [Fact]
        public void CanonicalWriterMatchesGoldenBytesForEveryPackage()
        {
            string expectedDir = Path.Combine(GoldenCaseRunner.GoldenDir, "expected");
            foreach (string path in Directory.GetFiles(expectedDir, "*.json").Where(p => !p.EndsWith(".canonical.json") && !p.EndsWith("index.json")))
            {
                var document = GoldenJson.Load(path);
                var package = document["applyPackage"]?["package"];
                if (package == null)
                {
                    continue;
                }

                string canonical = CanonicalJson.Write(package);
                Assert.Equal(File.ReadAllText(path.Replace(".json", ".apply.canonical.json")), canonical);
                Assert.Equal((string)document["applyPackage"]["sha256"], CanonicalJson.Sha256Hex(canonical));
            }
        }

        [Fact]
        public void EngineHasNoWorkbenchWpfOrJsonReferences()
        {
            var references = typeof(PlannerLogic).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
            Assert.DoesNotContain(references, name => name.StartsWith("Maptek") || name.Contains("Newtonsoft") || name.Contains("Json")
                || name == "PresentationCore" || name == "PresentationFramework" || name == "WindowsBase" || name.StartsWith("System.Xaml")
                || name.StartsWith("System.Windows"));
            Assert.All(references, name => Assert.Contains(name, new[] { "mscorlib", "System", "System.Core", "System.Numerics" }));
        }
    }
}
