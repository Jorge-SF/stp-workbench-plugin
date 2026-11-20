// The STP entry scripts (python/scripts) and where to find them.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Maptek.Vulcan.Stp.Bridge
{
    /// <summary>One STP entry script run from the STP UG menu.</summary>
    internal sealed class StpScript
    {
        public static readonly StpScript Prepare = new StpScript("prepare", "1. VGS_PrepareProjectForSTP.py", true);
        /// <summary>Script 2p (loads script 2): writes plugin_session.json for the planner document.</summary>
        public static readonly StpScript PluginPlanner = new StpScript("planner_plugin", "2p. VGS_ShortTermPlanner_Plugin.py", false);
        public static readonly StpScript Apply = new StpScript("apply", "3. VGS_ApplySTPToVGS.py", true);
        public static readonly StpScript Verify = new StpScript("verify", "4. VGS_VerifySTPSave.py", false);

        public static readonly IReadOnlyList<StpScript> All = new[] { Prepare, PluginPlanner, Apply, Verify };

        private StpScript(string id, string fileName, bool writesVgs)
        {
            Id = id;
            FileName = fileName;
            WritesVgs = writesVgs;
        }

        /// <summary>Short id, used for log file names.</summary>
        public string Id { get; }

        public string FileName { get; }

        /// <summary>True for scripts that change the VGS project (1, 3): never stopped once started.</summary>
        public bool WritesVgs { get; }
    }

    /// <summary>Finds the folder that holds the four entry scripts.</summary>
    internal static class StpScriptLocator
    {
        /// <summary>The plugin DLL's folder.</summary>
        public static string PluginDirectory =>
            Path.GetDirectoryName(typeof(StpScriptLocator).Assembly.Location) ?? "";

        /// <summary>
        /// The scripts folder in use: the setting when it holds the scripts, else <c>&lt;plugin&gt;\Python\scripts</c>
        /// (the installed layout), else the first <c>python\scripts</c> found walking up from the plugin folder (a
        /// build: <c>Deploy\Client</c> → the repository). Null when none holds script 1.
        /// </summary>
        public static string Resolve(string configured)
        {
            return Candidates(configured).FirstOrDefault(HasScripts);
        }

        /// <summary>The default folder (the setting left empty), or null.</summary>
        public static string ResolveDefault() => Resolve(null);

        public static bool HasScripts(string folder)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, StpScript.Prepare.FileName));
            }
            catch (ArgumentException)
            {
                return false;   // illegal characters in a typed path
            }
        }

        /// <summary>The python root for STP_CORE_PATH (the folder holding stp_core), or null.</summary>
        public static string PythonRoot(string scriptsFolder)
        {
            string parent = Path.GetDirectoryName(scriptsFolder.TrimEnd('\\', '/'));
            return parent != null && Directory.Exists(Path.Combine(parent, "stp_core")) ? parent : null;
        }

        private static IEnumerable<string> Candidates(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) yield return configured.Trim();
            string dir = PluginDirectory;
            if (string.IsNullOrEmpty(dir)) yield break;
            yield return Path.Combine(dir, "Python", "scripts");
            var d = new DirectoryInfo(dir);
            for (int i = 0; i < 5 && d != null; i++, d = d.Parent)
            {
                yield return Path.Combine(d.FullName, "python", "scripts");
            }
        }
    }
}
