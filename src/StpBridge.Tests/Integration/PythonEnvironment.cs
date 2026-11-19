using System;
using System.Diagnostics;
using Xunit;

namespace Maptek.Vulcan.Stp.Bridge.Tests.Integration
{
    /// <summary>
    /// Finds a Python 3.8+ for the integration tests: <c>STP_TEST_PYTHON</c> if set, else <c>python</c>, then <c>py -3</c>
    /// on PATH. Set <c>STP_TEST_PYTHON=skip</c> to force-skip.
    /// </summary>
    internal static class PythonEnvironment
    {
        private static readonly Lazy<(string File, string Prefix)?> Found = new Lazy<(string, string)?>(Find);

        public static bool Available => Found.Value.HasValue;

        public static string FileName => Found.Value?.File;

        /// <summary>Arguments to put before the script (e.g. <c>-3</c> for the py launcher).</summary>
        public static string ArgumentPrefix => Found.Value?.Prefix ?? "";

        public static string SkipReason =>
            "Python 3.8+ not found (set STP_TEST_PYTHON to a python.exe to run the bridge integration tests)";

        public static bool HasModule(string module) => Available && Works(FileName, ArgumentPrefix, $"import {module}");

        private static (string, string)? Find()
        {
            var configured = Environment.GetEnvironmentVariable("STP_TEST_PYTHON");
            if (string.Equals(configured, "skip", StringComparison.OrdinalIgnoreCase)) return null;
            var candidates = string.IsNullOrEmpty(configured)
                ? new[] { ("python", ""), ("py", "-3 ") }
                : new[] { (configured, "") };
            foreach (var (file, prefix) in candidates)
            {
                if (Works(file, prefix, "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)")) return (file, prefix);
            }
            return null;
        }

        private static bool Works(string file, string prefix, string code)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo(file, prefix + "-I -c \"" + code + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }))
                {
                    if (process == null) return false;
                    if (!process.WaitForExit(20000))
                    {
                        process.Kill();
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>A <see cref="FactAttribute"/> that skips cleanly when no suitable Python is installed.</summary>
    public sealed class PythonFactAttribute : FactAttribute
    {
        private string skip;

        /// <summary>Also skip unless this Python module imports (e.g. <c>xlsxwriter</c> for <c>/export-xlsx</c>).</summary>
        public string RequiresModule { get; set; }

        public override string Skip
        {
            get
            {
                if (skip != null) return skip;
                if (!PythonEnvironment.Available) return PythonEnvironment.SkipReason;
                if (RequiresModule != null && !PythonEnvironment.HasModule(RequiresModule)) return $"Python module '{RequiresModule}' is not installed";
                return null;
            }
            set => skip = value;
        }
    }
}
