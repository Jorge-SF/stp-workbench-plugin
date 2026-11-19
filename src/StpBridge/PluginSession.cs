using System;
using System.IO;
using System.Text;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge
{
    /// <summary>
    /// <c>&lt;runtime&gt;/plugin_session.json</c>, written by script 2 in plugin mode instead of rendering the HTML:
    /// <c>{"snapshot_path": "&lt;absolute path of snapshot_v2.json&gt;", "bridge_config": {…page config…}}</c>
    /// (design doc §5). Exact keys; <c>bridge_config</c> is a <see cref="BridgeConfig"/>.
    /// </summary>
    public sealed class PluginSession
    {
        public const string FileName = "plugin_session.json";

        /// <summary>Same cap as the server's request bodies.</summary>
        public const long MaxFileBytes = 20 * 1024 * 1024;

        /// <summary>Environment variable that moves script 2's runtime folder (also honoured by script 2).</summary>
        public const string RuntimeRootVariable = "STP_RUNTIME_ROOT";

        /// <summary>
        /// Script 2's per-user runtime folder, where it writes <see cref="FileName"/>: <c>STP_RUNTIME_ROOT</c> if set,
        /// else <c>%LOCALAPPDATA%\Maptek\StpUG\runtime</c>. Same rule as <c>stp_core.runtime.default_runtime_root</c>
        /// and script 2 (docs/known_issues.md, KI-2): keep the three in step.
        /// </summary>
        public static string DefaultRuntimeFolder => RuntimeFolderFrom(Environment.GetEnvironmentVariable);

        /// <summary>The <see cref="DefaultRuntimeFolder"/> rule over a given environment (for tests).</summary>
        public static string RuntimeFolderFrom(Func<string, string> environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            var configured = environment(RuntimeRootVariable);
            if (!string.IsNullOrEmpty(configured)) return configured;
            var local = environment("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local))
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            return Path.Combine(local, "Maptek", "StpUG", "runtime");
        }

        /// <summary>The default session file: <see cref="DefaultRuntimeFolder"/>\<see cref="FileName"/>.</summary>
        public static string DefaultPath => Path.Combine(DefaultRuntimeFolder, FileName);

        public PluginSession(string snapshotPath, BridgeConfig bridgeConfig)
        {
            if (string.IsNullOrEmpty(snapshotPath) || !Path.IsPathRooted(snapshotPath)
                || !string.Equals(Path.GetExtension(snapshotPath), ".json", StringComparison.OrdinalIgnoreCase))
                throw new WireFormatException("plugin session snapshot_path must be an absolute .json path");
            SnapshotPath = snapshotPath;
            BridgeConfig = bridgeConfig ?? throw new ArgumentNullException(nameof(bridgeConfig));
        }

        public string SnapshotPath { get; }

        public BridgeConfig BridgeConfig { get; }

        public static PluginSession FromJson(JToken token)
        {
            var raw = WireJson.ExactObject(token, "plugin session", "snapshot_path", "bridge_config");
            return new PluginSession(WireJson.Text(raw, "snapshot_path", "plugin session"), BridgeConfig.FromJson(raw["bridge_config"]));
        }

        /// <summary>Read <paramref name="path"/> (a <c>plugin_session.json</c> file, or the runtime folder containing it).</summary>
        public static PluginSession Read(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var file = Directory.Exists(path) ? Path.Combine(path, FileName) : path;
            return FromJson(WireJson.Parse(ReadCapped(file)));
        }

        /// <summary>
        /// Load and validate the session's snapshot. Like <c>validCurrentSnapshot</c> in <c>refresh_client.js</c>,
        /// a live session's snapshot must carry the bridge's <c>projectKey</c>.
        /// </summary>
        public SnapshotV2 LoadSnapshot()
        {
            var snapshot = SnapshotV2.FromJson(WireJson.Parse(ReadCapped(SnapshotPath)));
            if (BridgeConfig.IsLive && snapshot.ProjectKey != BridgeConfig.ProjectKey)
                throw new WireFormatException("snapshot projectKey does not match the bridge config");
            return snapshot;
        }

        internal static byte[] ReadCapped(string file)
        {
            var info = new FileInfo(file);
            if (!info.Exists) throw new FileNotFoundException("STP file not found", file);
            if (info.Length > MaxFileBytes) throw new WireFormatException(Path.GetFileName(file) + " is too large");
            return File.ReadAllBytes(file);
        }
    }
}
