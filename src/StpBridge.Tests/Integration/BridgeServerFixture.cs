using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Tests.Integration
{
    /// <summary>
    /// Runs <c>Integration/stp_test_server.py</c>: the real <c>stp_live_refresh_server_v2</c> from <c>python/</c> on an
    /// ephemeral 127.0.0.1 port over a synthetic <c>.vgantt</c> in a temp folder. Started lazily, once per test class;
    /// does nothing when Python is unavailable (the tests are skipped by <see cref="PythonFactAttribute"/>).
    /// </summary>
    public sealed class BridgeServerFixture : IDisposable
    {
        private readonly Lazy<Started> started;
        private readonly StringBuilder stderr = new StringBuilder();

        public BridgeServerFixture() => started = new Lazy<Started>(Start);

        public sealed class Started
        {
            public Process Process;
            public string WorkDirectory;
            public BridgeConfig Config;
            public JObject Info;
        }

        public BridgeConfig Config => started.Value.Config;

        public JObject Info => started.Value.Info;

        public string WorkDirectory => started.Value.WorkDirectory;

        public string ProjectDirectory => Path.GetDirectoryName((string)Info["projectPath"]);

        public StpBridgeClient CreateClient(string token = null, TimeSpan? timeout = null)
        {
            var config = Config;
            if (token != null) config = new BridgeConfig(config.Endpoint, token, config.ProjectKey, config.BridgeId, config.Message, config.PrecedenceConflictPolicy);
            return new StpBridgeClient(config, timeout ?? TimeSpan.FromSeconds(30));
        }

        private Started Start()
        {
            var work = Path.Combine(Path.GetTempPath(), "stp-bridge-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var script = Path.Combine(TestPaths.RepoRoot, "src", "StpBridge.Tests", "Integration", "stp_test_server.py");
            var pythonRoot = Path.Combine(TestPaths.RepoRoot, "python");
            var startInfo = new ProcessStartInfo(PythonEnvironment.FileName, $"{PythonEnvironment.ArgumentPrefix}-I \"{script}\" --python-root \"{pythonRoot}\" --workdir \"{work}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = work,
            };
            startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("python did not start");
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            var line = Task.Run(() => process.StandardOutput.ReadLine());
            if (!line.Wait(TimeSpan.FromSeconds(60)) || string.IsNullOrEmpty(line.Result))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                process.WaitForExit(5000);
                string errors;
                lock (stderr) errors = stderr.ToString();
                throw new InvalidOperationException("the test bridge did not start:\n" + errors);
            }
            var info = (JObject)WireJson.Parse(line.Result);
            var config = new BridgeConfig(
                "http://127.0.0.1:" + (long)info["port"],
                (string)info["token"],
                (string)info["projectKey"],
                Guid.NewGuid().ToString("N"),
                "Actualización en vivo disponible.");
            return new Started { Process = process, WorkDirectory = work, Config = config, Info = info };
        }

        public void Dispose()
        {
            if (!started.IsValueCreated) return;
            var value = started.Value;
            try
            {
                value.Process.StandardInput.Close(); // the helper shuts the server down when stdin closes
                if (!value.Process.WaitForExit(10000)) value.Process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            value.Process.Dispose();
            try
            {
                Directory.Delete(value.WorkDirectory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
