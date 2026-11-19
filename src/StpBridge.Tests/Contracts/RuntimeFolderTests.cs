using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Maptek.Vulcan.Stp.Bridge.Tests.Contracts
{
    /// <summary>The per-user runtime folder (KI-2): the same rule as stp_core.runtime.default_runtime_root.</summary>
    public class RuntimeFolderTests
    {
        private static string From(Dictionary<string, string> environment) =>
            PluginSession.RuntimeFolderFrom(name => environment.TryGetValue(name, out var value) ? value : null);

        [Fact]
        public void DefaultIsUnderLocalAppData()
        {
            var folder = From(new Dictionary<string, string> { ["LOCALAPPDATA"] = @"C:\Users\someone\AppData\Local" });
            Assert.Equal(@"C:\Users\someone\AppData\Local\Maptek\StpUG\runtime", folder);
        }

        [Fact]
        public void EnvironmentOverrideWins()
        {
            var folder = From(new Dictionary<string, string>
            {
                ["STP_RUNTIME_ROOT"] = @"D:\elsewhere",
                ["LOCALAPPDATA"] = @"C:\x",
            });
            Assert.Equal(@"D:\elsewhere", folder);
        }

        [Fact]
        public void DefaultPathIsTheSessionFileInTheFolder()
        {
            Assert.Equal(PluginSession.FileName, Path.GetFileName(PluginSession.DefaultPath));
            Assert.Equal(PluginSession.DefaultRuntimeFolder, Path.GetDirectoryName(PluginSession.DefaultPath));
        }

        [Fact]
        public void PythonUsesTheSameRule()
        {
            // Guard against the two sides drifting apart: the Python rule must name the same folders.
            var runtime = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "python", "stp_core", "runtime.py"));
            Assert.Contains("\"STP_RUNTIME_ROOT\"", runtime);
            Assert.Contains("Path(local) / \"Maptek\" / \"StpUG\" / \"runtime\"", runtime);
        }
    }
}
