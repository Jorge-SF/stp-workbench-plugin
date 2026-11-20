using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Maptek.Vulcan.Stp.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>Paths and loaders for tools/golden and the Python vectors.</summary>
    internal static class Golden
    {
        /// <summary>Hand-written fixtures the strict wire reader rejects (see StpBridge.Tests SnapshotContractTests).</summary>
        public static readonly string[] EngineOnlyFixtures = { "engine_edges" };

        private static readonly Lazy<string> RootLazy = new Lazy<string>(() =>
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "StpWorkbench.sln"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("StpWorkbench.sln not found above " + AppContext.BaseDirectory);
        });

        private static readonly Lazy<Dictionary<string, string>> PythonCanonicalLazy = new Lazy<Dictionary<string, string>>(() =>
        {
            var vectors = (JObject)WireJson.Parse(File.ReadAllBytes(Path.Combine(RootLazy.Value, "src", "StpBridge.Tests", "TestData", "python_vectors.json")));
            return ((JArray)vectors["goldenNormalized"]).ToDictionary(v => (string)v["file"], v => (string)v["canonical"], StringComparer.Ordinal);
        });

        public static string Root => RootLazy.Value;

        public static string Fixtures => Path.Combine(Root, "tools", "golden", "fixtures");

        public static string Expected => Path.Combine(Root, "tools", "golden", "expected");

        /// <summary>Python's normalized canonical text of each golden apply package (what the bridge stores and hashes).</summary>
        public static IReadOnlyDictionary<string, string> PythonCanonical => PythonCanonicalLazy.Value;

        public static IEnumerable<string> StrictFixtureNames() => Directory.GetFiles(Fixtures, "*.snapshot.json")
            .Select(f => Path.GetFileName(f).Replace(".snapshot.json", string.Empty))
            .Where(f => !EngineOnlyFixtures.Contains(f)).OrderBy(f => f, StringComparer.Ordinal);

        public static IEnumerable<object[]> StrictFixtures() => StrictFixtureNames().Select(f => new object[] { f });

        public static IEnumerable<object[]> StrictCases() => StrictFixtureNames()
            .SelectMany(f => Load(Path.Combine(Fixtures, f + ".cases.json"))["cases"].Select(c => new object[] { f, (string)c["name"] }));

        public static JToken Load(string path)
        {
            using (var reader = new JsonTextReader(new StreamReader(path)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
            {
                return JToken.ReadFrom(reader);
            }
        }

        public static JObject RawSnapshot(string fixture) => (JObject)WireJson.Parse(File.ReadAllBytes(Path.Combine(Fixtures, fixture + ".snapshot.json")));

        public static SnapshotV2 Snapshot(string fixture) => SnapshotV2.FromJson(RawSnapshot(fixture));

        public static JToken Case(string fixture, string name) =>
            Load(Path.Combine(Fixtures, fixture + ".cases.json"))["cases"].First(c => (string)c["name"] == name);

        public static BridgeConfig StaticConfig() => BridgeConfig.FromJson(new JObject
        {
            ["mode"] = "static",
            ["endpoint"] = string.Empty,
            ["token"] = string.Empty,
            ["projectKey"] = string.Empty,
            ["bridgeId"] = string.Empty,
            ["message"] = "Sin puente.",
        });

        public static BridgeConfig LiveConfig(SnapshotV2 snapshot, string policy = null) => new BridgeConfig(
            "http://127.0.0.1:8765", "0123456789abcdef0123456789abcdef", snapshot.ProjectKey, new string('a', 32), "Actualización en vivo disponible.", policy);

        /// <summary>The case's seeded storage (run.mjs / GoldenCaseRunner rule).</summary>
        public static MemoryHorizonStorage Storage(JToken testCase, string projectKey)
        {
            var storage = new MemoryHorizonStorage();
            foreach (var property in ((JObject)testCase["storage"] ?? new JObject()).Properties())
            {
                storage.SetItem(property.Name.Replace("{projectKey}", projectKey), property.Value.Type == JTokenType.String ? (string)property.Value : property.Value.ToString(Formatting.None));
            }

            return storage;
        }

        public static CivilHorizon Horizon(JToken json) => json == null || json.Type == JTokenType.Null ? null : new CivilHorizon
        {
            HorizonStart = (string)json["horizonStart"],
            HorizonFinish = (string)json["horizonFinish"],
            DurationDays = (int?)json["durationDays"],
        };

        public static JToken Of(CivilHorizon h) => h == null ? (JToken)JValue.CreateNull() : new JObject
        {
            ["horizonStart"] = h.HorizonStart,
            ["horizonFinish"] = h.HorizonFinish,
            ["durationDays"] = h.DurationDays,
        };

        public static JToken Summary(ControllerState s) => new JObject
        {
            ["horizon"] = Of(s.Horizon),
            ["displayHorizon"] = s.DisplayHorizon == null ? (JToken)JValue.CreateNull() : new JObject
            {
                ["start"] = s.DisplayHorizon.Start,
                ["finish"] = s.DisplayHorizon.Finish,
                ["durationDays"] = s.DisplayHorizon.DurationDays,
            },
            ["hasPending"] = s.HasPending,
            ["hasLocalMovements"] = s.HasLocalMovements,
            ["canDownload"] = s.CanDownload,
            ["status"] = s.Status,
        };

        public static JToken FragmentRows(PlannerModel model) => new JArray(model.Fragments
            .OrderBy(f => f, Comparer<PlannerFragment>.Create((a, b) => a.ActivityId == b.ActivityId
                ? JsCompare.LocaleCompare(a.Id, b.Id)
                : JsCompare.LocaleCompare(a.ActivityId, b.ActivityId)))
            .Select(f => new JObject
            {
                ["id"] = f.Id,
                ["start"] = f.Start,
                ["finish"] = f.Finish,
                ["provenance"] = f.Provenance ?? string.Empty,
                ["lockedCompleted"] = f.LockedCompleted,
            }));

        public static JObject Storage(MemoryHorizonStorage storage)
        {
            var result = new JObject();
            foreach (var pair in storage.Items.OrderBy(p => p.Key, JsCompare.Locale)) result[pair.Key] = pair.Value;
            return result;
        }

        public static void AssertSame(JToken expected, JToken actual, string label)
        {
            if (!JToken.DeepEquals(expected, actual))
            {
                throw new Xunit.Sdk.XunitException($"{label}:\nexpected {expected?.ToString(Formatting.None)}\nactual   {actual?.ToString(Formatting.None)}");
            }
        }
    }
}
