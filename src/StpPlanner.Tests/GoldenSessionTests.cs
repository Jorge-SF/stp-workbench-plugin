using System.IO;
using System.Linq;
using System.Text;
using Maptek.Vulcan.Stp.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>
    /// Replays every golden case of the strict fixtures through <see cref="PlannerSession"/> (wire snapshot →
    /// <see cref="SnapshotMapper"/> → engine) and compares with tools/golden/expected: step outcomes, final state,
    /// fragments, stored preferences, and the apply package byte-equal to Python's normalized canonical JSON.
    /// </summary>
    public class GoldenSessionTests
    {
        public static System.Collections.Generic.IEnumerable<object[]> Cases() => Golden.StrictCases();

        [Theory]
        [MemberData(nameof(Cases))]
        public void SessionReplaysGoldenCase(string fixture, string caseName)
        {
            var expected = Golden.Load(Path.Combine(Golden.Expected, $"{fixture}.{caseName}.json"));
            var snapshot = Golden.Snapshot(fixture);
            var testCase = Golden.Case(fixture, caseName);
            var storage = Golden.Storage(testCase, snapshot.ProjectKey);

            if (expected["setupError"] != null)
            {
                var error = Assert.Throws<EngineException>(() => new PlannerSession(snapshot, Golden.StaticConfig(), null, storage, null, Golden.Horizon(testCase["initialHorizon"])));
                Golden.AssertSame(expected["setupError"], new JObject { ["error"] = error.Message, ["code"] = error.Code }, "setupError");
                return;
            }

            var session = new PlannerSession(snapshot, Golden.StaticConfig(), null, storage, null, Golden.Horizon(testCase["initialHorizon"]));
            Golden.AssertSame(expected["initial"], Golden.Summary(session.State), "initial");

            var steps = (JArray)expected["steps"];
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i]["step"];
                var actual = Run(session, step);
                if (actual != null) Golden.AssertSame(steps[i]["outcome"], actual, $"step {i} {step["op"]}");
            }

            Golden.AssertSame(expected["final"], Golden.Summary(session.State), "final");
            Golden.AssertSame(expected["fragments"], Golden.FragmentRows(session.State.Model), "fragments");
            Golden.AssertSame(expected["storage"], Golden.Storage(storage), "storage");

            string canonicalFile = $"{fixture}.{caseName}.apply.canonical.json";
            if (File.Exists(Path.Combine(Golden.Expected, canonicalFile)))
            {
                var package = session.BuildApplyPackage();
                Assert.Equal(Golden.PythonCanonical[canonicalFile], Encoding.UTF8.GetString(package.ToCanonicalBytes()));
            }
            else
            {
                var error = Assert.Throws<EngineException>(() => session.BuildApplyPackage());
                Assert.Equal((string)expected["applyPackage"]["error"], error.Message);
            }
        }

        /// <summary>Undo all the way back gives the initial plan; redo all the way gives the final plan again.</summary>
        [Theory]
        [MemberData(nameof(Cases))]
        public void UndoRedoReplaysTheGoldenCase(string fixture, string caseName)
        {
            var expected = Golden.Load(Path.Combine(Golden.Expected, $"{fixture}.{caseName}.json"));
            if (expected["setupError"] != null) return;
            var snapshot = Golden.Snapshot(fixture);
            var testCase = Golden.Case(fixture, caseName);
            var session = new PlannerSession(snapshot, Golden.StaticConfig(), null, Golden.Storage(testCase, snapshot.ProjectKey), null, Golden.Horizon(testCase["initialHorizon"]));
            var initialHorizon = Golden.Of(session.Horizon);
            var initialFragments = Golden.FragmentRows(session.State.Model);
            foreach (var step in expected["steps"].Select(s => s["step"])) Run(session, step);
            var finalHorizon = Golden.Of(session.Horizon);
            var finalFragments = Golden.FragmentRows(session.State.Model);

            int undone = 0;
            while (session.CanUndo)
            {
                Assert.True(session.Undo().Ok);
                undone++;
            }

            Golden.AssertSame(initialHorizon, Golden.Of(session.Horizon), "horizon after undo");
            Golden.AssertSame(initialFragments, Golden.FragmentRows(session.State.Model), "fragments after undo");
            Assert.False(session.Undo().Ok);

            for (int i = 0; i < undone; i++) Assert.True(session.Redo().Ok);
            Assert.False(session.CanRedo);
            Golden.AssertSame(finalHorizon, Golden.Of(session.Horizon), "horizon after redo");
            Golden.AssertSame(finalFragments, Golden.FragmentRows(session.State.Model), "fragments after redo");
        }

        [Fact]
        public void EveryStrictCaseIsReplayed()
        {
            var index = Golden.Load(Path.Combine(Golden.Expected, "index.json"));
            var strict = index["cases"].Where(c => !Golden.EngineOnlyFixtures.Contains((string)c["fixture"]))
                .Select(c => $"{c["fixture"]}.{c["case"]}").OrderBy(s => s).ToList();
            Assert.Equal(strict, Cases().Select(c => $"{c[0]}.{c[1]}").OrderBy(s => s).ToList());
            Assert.NotEmpty(strict);
        }

        /// <summary>Runs one golden step through the session; <c>null</c> for steps that are not session commands.</summary>
        internal static JToken Run(PlannerSession session, JToken step)
        {
            try
            {
                switch ((string)step["op"])
                {
                    case "moveActivity": return Of(session.MoveActivity((string)step["activityId"], (long)step["days"]));
                    case "moveDailyTail": return Of(session.MoveDailyTail((string)step["activityId"], (string)step["fragmentId"], (string)step["targetDate"]));
                    case "deferDailyTail": return Of(session.DeferDailyTail((string)step["activityId"], (string)step["fragmentId"]));
                    case "staggerDailyTail": return Of(session.SplitAndStagger((string)step["activityId"], (string)step["fragmentId"], (long)step["divisions"], (long)step["gapDays"]));
                    case "editStart": return Of(session.SetRangeStart((string)step["value"]));
                    case "editFinish": return Of(session.SetRangeFinish((string)step["value"]));
                    case "editDuration": return Of(session.SetDuration((long)step["value"]));
                    case "savePreference": return Of(session.SaveRangePreference());
                    case "reset": return Of(session.Discard());
                    case "activityEligibility":
                        var a = session.ActivityEligibility((string)step["activityId"]);
                        return new JObject { ["showControls"] = a.ShowControls, ["backwardEnabled"] = a.BackwardEnabled, ["forwardEnabled"] = a.ForwardEnabled, ["guidance"] = a.Guidance };
                    case "fragmentEligibility":
                        var f = session.FragmentEligibility((string)step["fragmentId"]);
                        return new JObject { ["eligible"] = f.Eligible, ["reason"] = f.Reason, ["guidance"] = f.Guidance };
                    default:
                        return null; // pure queries (moveFragment, buildScenario, fragmentState, assess...): no session state change
                }
            }
            catch (EngineException error)
            {
                return new JObject { ["error"] = error.Message, ["code"] = error.Code };
            }
        }

        private static JToken Of(PlannerOutcome outcome)
        {
            Assert.False(string.IsNullOrEmpty(outcome.Message));
            return new JObject { ["ok"] = outcome.Ok, ["message"] = outcome.Detail, ["code"] = outcome.Code };
        }
    }
}
