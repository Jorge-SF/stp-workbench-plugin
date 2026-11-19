using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Engine.Tests.Golden
{
    /// <summary>C# twin of tools/golden/run.mjs <c>runCase</c>: replays one case and returns the same result document.</summary>
    internal static class GoldenCaseRunner
    {
        public static string RepoRoot { get; } = FindRepoRoot();

        public static string GoldenDir => Path.Combine(RepoRoot, "tools", "golden");

        public static IEnumerable<(string Fixture, string Case)> AllCases()
        {
            foreach (string file in Directory.GetFiles(Path.Combine(GoldenDir, "fixtures"), "*.cases.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string fixture = Path.GetFileName(file).Replace(".cases.json", string.Empty);
                foreach (var testCase in GoldenJson.Load(file)["cases"])
                {
                    yield return (fixture, (string)testCase["name"]);
                }
            }
        }

        public static (JObject Result, string Canonical) Run(string fixture, string caseName)
        {
            string snapshotPath = Path.Combine(GoldenDir, "fixtures", fixture + ".snapshot.json");
            var snapshot = GoldenJson.Load(snapshotPath);
            var testCase = GoldenJson.Load(Path.Combine(GoldenDir, "fixtures", fixture + ".cases.json"))["cases"].First(c => (string)c["name"] == caseName);
            string projectKey = (string)snapshot["projectKey"];

            var seeded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in ((JObject)testCase["storage"] ?? new JObject()).Properties())
            {
                seeded[property.Name.Replace("{projectKey}", projectKey)] = property.Value.Type == JTokenType.String
                    ? (string)property.Value
                    : property.Value.ToString(Formatting.None);
            }

            var storage = new MemoryStorage(seeded);
            var result = new JObject { ["case"] = caseName, ["description"] = (string)testCase["description"] ?? string.Empty, ["steps"] = new JArray() };
            CivilHorizonController planner;
            try
            {
                var model = PlannerLogic.ValidatePlannerModel(PlannerLogic.BuildPlannerModel(GoldenJson.ToSnapshot(snapshot)));
                planner = new CivilHorizonController(() => model, projectKey, storage, GoldenJson.ToHorizon(testCase["initialHorizon"]));
            }
            catch (EngineException error)
            {
                result["setupError"] = GoldenJson.Of(error);
                return (result, null);
            }

            result["initial"] = GoldenJson.Summary(planner.State());
            foreach (var step in testCase["steps"] ?? new JArray())
            {
                JToken outcome;
                try
                {
                    outcome = RunStep(planner, step, snapshot);
                }
                catch (EngineException error)
                {
                    outcome = GoldenJson.Of(error);
                }

                ((JArray)result["steps"]).Add(new JObject { ["step"] = step.DeepClone(), ["outcome"] = outcome });
            }

            var state = planner.State();
            result["final"] = GoldenJson.Summary(state);
            result["fragments"] = GoldenJson.FragmentRows(state.Model);
            result["storage"] = storage.Dump();
            string canonical = null;
            try
            {
                var package = GoldenJson.Of(PlannerLogic.BuildVgsApplyPackage(state.Model, Source(snapshot), state.Horizon));
                canonical = CanonicalJson.Write(package);
                result["applyPackage"] = new JObject { ["sha256"] = CanonicalJson.Sha256Hex(canonical), ["package"] = package };
            }
            catch (EngineException error)
            {
                result["applyPackage"] = GoldenJson.Of(error);
            }

            return (result, canonical);
        }

        private static ProjectSource Source(JToken snapshot) => new ProjectSource
        {
            ProjectFingerprint = (string)snapshot["projectFingerprint"],
            PrecedenceFingerprint = (string)snapshot["precedenceFingerprint"],
        };

        private static JToken RunStep(CivilHorizonController planner, JToken step, JToken snapshot)
        {
            string op = (string)step["op"];
            switch (op)
            {
                case "moveActivity": return GoldenJson.Of(planner.MoveActivity((string)step["activityId"], (long)step["days"]));
                case "moveDailyTail": return GoldenJson.Of(planner.MoveDailyTail((string)step["activityId"], (string)step["fragmentId"], (string)step["targetDate"]));
                case "deferDailyTail": return GoldenJson.Of(planner.DeferDailyTailToNextPeriod((string)step["activityId"], (string)step["fragmentId"]));
                case "staggerDailyTail": return GoldenJson.Of(planner.StaggerDailyTail((string)step["activityId"], (string)step["fragmentId"], (long)step["divisions"], (long)step["gapDays"]));
                case "activityEligibility": return GoldenJson.Of(planner.ActivityEligibility((string)step["activityId"]));
                case "fragmentEligibility": return GoldenJson.Of(planner.FragmentEligibility((string)step["fragmentId"]));
                case "fragmentState":
                    return new JValue(planner.FragmentState(planner.State().Model.Fragments.First(f => f.Id == (string)step["fragmentId"])));
                case "editStart": return GoldenJson.Of(planner.EditStart((string)step["value"]));
                case "editFinish": return GoldenJson.Of(planner.EditFinish((string)step["value"]));
                case "editDuration": return GoldenJson.Of(planner.EditDuration((long)step["value"]));
                case "savePreference": return GoldenJson.Of(planner.SavePreference());
                case "reset": return GoldenJson.Of(planner.Reset());
                case "assessPrecedenceConflicts": return GoldenJson.Of(PlannerLogic.AssessPrecedenceConflicts(planner.State().Model, (string)step["policy"]));
                case "moveFragment":
                    return new JObject { ["fragments"] = GoldenJson.FragmentRows(PlannerLogic.MoveFragment(planner.State().Model, (string)step["fragmentId"], (string)step["newStart"])) };
                case "buildScenario":
                    return GoldenJson.Of(PlannerLogic.BuildScenario(planner.State().Model, Source(snapshot), planner.State().Horizon));
                default: throw new InvalidOperationException($"unknown op {op}");
            }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "StpWorkbench.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new DirectoryNotFoundException("StpWorkbench.sln not found above the test output folder");
        }

        /// <summary>run.mjs <c>memoryStorage</c>.</summary>
        private sealed class MemoryStorage : IHorizonStorage
        {
            private readonly Dictionary<string, string> data;

            public MemoryStorage(Dictionary<string, string> initial)
            {
                data = new Dictionary<string, string>(initial, StringComparer.Ordinal);
            }

            public string GetItem(string key) => data.TryGetValue(key, out string value) ? value : null;

            public void SetItem(string key, string value) => data[key] = value;

            public JObject Dump()
            {
                var result = new JObject();
                foreach (var pair in data.OrderBy(p => p.Key, JsCompare.Locale))
                {
                    result[pair.Key] = pair.Value;
                }

                return result;
            }
        }
    }
}
