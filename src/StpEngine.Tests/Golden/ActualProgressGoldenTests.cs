using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Engine.Tests.Golden
{
    /// <summary>
    /// Replays tools/golden/fixtures/actual_progress.steps.json through <see cref="ActualProgress"/> and compares every
    /// step (outcome and state, in JS order) with tools/golden/expected_actual_progress (tools/golden/actual_progress.mjs).
    /// </summary>
    public class ActualProgressGoldenTests
    {
        private static string Fixture => Path.Combine(GoldenCaseRunner.GoldenDir, "fixtures", "actual_progress.steps.json");

        private static string ExpectedDir => Path.Combine(GoldenCaseRunner.GoldenDir, "expected_actual_progress");

        public static IEnumerable<object[]> Cases() => Load(Fixture)["cases"].Select(c => new object[] { (string)c["name"] });

        [Theory]
        [MemberData(nameof(Cases))]
        public void CaseMatchesGolden(string caseName)
        {
            var testCase = Load(Fixture)["cases"].First(c => (string)c["name"] == caseName);
            var expected = Load(Path.Combine(ExpectedDir, caseName + ".json"));
            var state = StateIn(testCase["state"]);
            Same(expected["initial"], StateOut(state), "$.initial");
            var steps = (JArray)testCase["steps"];
            Assert.Equal(((JArray)expected["steps"]).Count, steps.Count);
            for (int i = 0; i < steps.Count; i++)
            {
                var outcome = Run(ref state, steps[i]);
                string path = $"$.steps[{i}]({steps[i]["op"]})";
                Same(expected["steps"][i]["outcome"], outcome, path + ".outcome");
                Same(expected["steps"][i]["state"], StateOut(state), path + ".state");
            }
        }

        [Fact]
        public void EveryGoldenCaseIsReplayed()
        {
            var index = Load(Path.Combine(ExpectedDir, "index.json"));
            Assert.Equal(index["cases"].Select(c => (string)c).ToList(), Cases().Select(c => (string)c[0]).ToList());
        }

        private static JToken Run(ref ActualProgressState state, JToken step)
        {
            string op = (string)step["op"];
            string id = (string)step["activityId"];
            string date = (string)step["date"];
            string origin = (string)step["originDate"];
            string target = (string)step["targetDate"];
            var dates = Strings(step["dates"]);
            var row = step["row"] != null ? RowIn(step["row"]) : null;
            switch (op)
            {
                case "value": return Number(ActualProgress.Value(state, id, date));
                case "hasSlot": return ActualProgress.HasSlot(state, id, date);
                case "hasHidden": return ActualProgress.HasHiddenSlot(state, id, date);
                case "hasVisibleSlotInDays": return ActualProgress.HasVisibleSlotInDays(state, id, dates);
                case "visibleSlotActivityIds": return new JArray(ActualProgress.VisibleSlotActivityIds(state, dates));
                case "cascadeMoves": return MovesOut(ActualProgress.CascadeMoves(state, id, origin, target, dates));
                case "slotCascadeMoves": return MovesOut(ActualProgress.SlotCascadeMoves(state, id, origin, target, dates, Strings(step["slotDates"])));
                case "byType": return ByTypeOut(ActualProgress.BuildByType(ViewIn(step["view"]), state));
                case "activityPeriodTotals": return TotalsOut(ActualProgress.ActivityPeriodTotals(row, state, (bool?)step["show"] == true));
                case "typePeriodTotals":
                    return new JArray(ActualProgress.BuildByType(ViewIn(step["view"]), state).Types
                        .Select(t => new JArray(t.Type, TotalsOut(ActualProgress.TypePeriodTotals(t, dates)))));
                case "hasAny": return state.HasAny;
                case "visibleSlotDates": return new JArray(ActualProgress.VisibleSlotDates(state, row));
                case "showsControl": return ActualProgress.ShowsBox(state, row, date);
                case "canAddSlot": return ActualProgress.CanAddSlot(state, row, date);
                case "setValue": state = ActualProgress.SetValue(state, id, date, Num(step["value"])); return JValue.CreateNull();
                case "setFragmentValue": state = ActualProgress.SetFragmentValue(state, (string)step["fragmentId"], Num(step["value"])); return JValue.CreateNull();
                case "editValue":
                    state = ActualProgress.EditValue(state, id, date, step["value"].Type == JTokenType.Null ? (double?)null : Num(step["value"]));
                    return JValue.CreateNull();
                case "setSlot": state = ActualProgress.SetSlot(state, id, date, (bool)step["present"]); return JValue.CreateNull();
                case "setHidden": state = ActualProgress.SetHiddenSlot(state, id, date, (bool)step["hidden"]); return JValue.CreateNull();
                case "clearSlot": state = ActualProgress.ClearSlot(state, id, date, (bool)step["scheduled"]); return JValue.CreateNull();
                case "restoreSlot": state = ActualProgress.RestoreSlot(state, id, date, (bool)step["scheduled"]); return JValue.CreateNull();
                case "move": state = ActualProgress.Move(state, id, origin, target); return JValue.CreateNull();
                case "moveCascade": state = ActualProgress.MoveCascade(state, id, origin, target, dates); return JValue.CreateNull();
                case "moveSlotCascade": state = ActualProgress.MoveSlotCascade(state, id, origin, target, dates, Strings(step["slotDates"])); return JValue.CreateNull();
                case "migrate":
                    state = ActualProgress.Migrate(state, step["segments"].Select(s => new ProgressSegmentRef
                    {
                        FragmentId = (string)s["fragmentId"],
                        ActivityId = (string)s["activityId"],
                        Date = (string)s["date"],
                    }).ToList());
                    return JValue.CreateNull();
                case "applySlotCascade":
                {
                    bool applied = ActualProgress.TryApplySlotCascade(state, row, origin, target, out var next);
                    state = next;
                    return applied;
                }

                case "applyCascade":
                {
                    bool applied = ActualProgress.TryApplyCascade(state, row, origin, target, out var next);
                    state = next;
                    return applied;
                }

                default: throw new InvalidOperationException("unknown op " + op);
            }
        }

        // ------------------------------------------------------------------ JSON in

        private static JToken Load(string path)
        {
            using (var reader = new JsonTextReader(new StreamReader(path)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
            {
                return JToken.ReadFrom(reader);
            }
        }

        private static double Num(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                switch ((string)token)
                {
                    case "NaN": return double.NaN;
                    case "Infinity": return double.PositiveInfinity;
                    case "-Infinity": return double.NegativeInfinity;
                }
            }

            return (double)token;
        }

        private static List<string> Strings(JToken token) => token?.Select(t => (string)t).ToList();

        private static ActualProgressState StateIn(JToken raw)
        {
            IEnumerable<KeyValuePair<string, double>> Values(string name) =>
                (raw?[name] ?? new JArray()).Select(p => new KeyValuePair<string, double>((string)p[0], Num(p[1])));
            IEnumerable<string> Keys(string name) => (raw?[name] ?? new JArray()).Where(p => p[1].Type == JTokenType.Boolean && (bool)p[1]).Select(p => (string)p[0]);
            return ActualProgressState.Create(Values("byDay"), Values("byFragment"), Keys("slots"), Keys("hidden"));
        }

        private static ProgressRow RowIn(JToken row) => new ProgressRow
        {
            ActivityId = (string)row["activityId"],
            Type = (string)row["type"],
            Resources = Strings(row["resources"]) ?? new List<string>(),
            Days = row["days"].Select(d => new ProgressDay
            {
                Date = (string)d["date"],
                Segments = d["segments"].Select(s => new ProgressSegment
                {
                    Completed = (string)s["status"] == "completed",
                    Amount = Num(s["amount"]),
                    Resource = (string)s["resource"],
                }).ToList(),
            }).ToList(),
        };

        private static ProgressView ViewIn(JToken view) => new ProgressView { Dates = Strings(view["dates"]), Rows = view["rows"].Select(RowIn).ToList() };

        // ------------------------------------------------------------------ JSON out (the harness's shapes)

        private static JToken Number(double? value) => value.HasValue ? new JValue(value.Value) : JValue.CreateNull();

        private static JObject StateOut(ActualProgressState state) => new JObject
        {
            ["byDay"] = Pairs(state.ByDay),
            ["byFragment"] = Pairs(state.ByFragment),
            ["slots"] = new JArray(state.Slots.Select(k => new JArray(k, true))),
            ["hidden"] = new JArray(state.HiddenSlots.Select(k => new JArray(k, true))),
        };

        private static JArray Pairs(IEnumerable<KeyValuePair<string, double>> pairs) => new JArray(pairs.Select(p => new JArray(p.Key, p.Value)));

        private static JArray MovesOut(IEnumerable<ActualProgressMove> moves) => new JArray(moves.Select(m =>
        {
            var move = new JObject { ["originDate"] = m.OriginDate, ["targetDate"] = m.TargetDate };
            if (m.Value.HasValue) move["value"] = m.Value.Value;   // JSON.stringify drops an undefined value
            return move;
        }));

        private static JObject TotalsOut(ProgressTotals totals) => new JObject { ["scheduled"] = totals.Scheduled, ["actual"] = Number(totals.Actual) };

        private static JObject ByTypeOut(ActualProgressSummary summary) => new JObject
        {
            ["dates"] = new JArray(summary.Dates),
            ["byType"] = new JArray(summary.Types.Select(t => new JObject
            {
                ["type"] = t.Type,
                ["days"] = new JArray(t.Days.Select(d => new JArray(d.Date, new JObject { ["scheduled"] = d.Scheduled, ["actual"] = Number(d.Actual) }))),
                ["plannedByResource"] = Pairs(t.PlannedByResource),
                ["actualByResource"] = Pairs(t.ActualByResource),
                ["plannedByResourceByDate"] = new JArray(t.PlannedByResourceByDate.Select(p => new JArray(p.Key, Pairs(p.Value)))),
                ["actualByResourceByDate"] = new JArray(t.ActualByResourceByDate.Select(p => new JArray(p.Key, Pairs(p.Value)))),
            })),
        };

        // ------------------------------------------------------------------ compare (numbers as doubles, arrays in order)

        private static void Same(JToken expected, JToken actual, string path)
        {
            string difference = Difference(expected, actual, path);
            Assert.True(difference == null, difference);
        }

        private static string Difference(JToken expected, JToken actual, string path)
        {
            bool expectedNumber = expected.Type == JTokenType.Integer || expected.Type == JTokenType.Float;
            bool actualNumber = actual.Type == JTokenType.Integer || actual.Type == JTokenType.Float;
            if (expectedNumber || actualNumber)
            {
                return expectedNumber && actualNumber && (double)expected == (double)actual ? null
                    : $"{path}: expected {expected.ToString(Formatting.None)}, got {actual.ToString(Formatting.None)}";
            }

            if (expected.Type != actual.Type) return $"{path}: expected {expected.ToString(Formatting.None)}, got {actual.ToString(Formatting.None)}";
            switch (expected)
            {
                case JArray ea:
                    var aa = (JArray)actual;
                    if (ea.Count != aa.Count) return $"{path}: expected {ea.Count} items {ea.ToString(Formatting.None)}, got {aa.Count} {aa.ToString(Formatting.None)}";
                    for (int i = 0; i < ea.Count; i++)
                    {
                        string inner = Difference(ea[i], aa[i], $"{path}[{i}]");
                        if (inner != null) return inner;
                    }

                    return null;
                case JObject eo:
                    var ao = (JObject)actual;
                    var names = eo.Properties().Select(p => p.Name).ToList();
                    if (!names.SequenceEqual(ao.Properties().Select(p => p.Name).Where(names.Contains)) || names.Count != ao.Count)
                    {
                        return $"{path}: expected keys {string.Join(",", names)}, got {string.Join(",", ao.Properties().Select(p => p.Name))}";
                    }

                    foreach (string name in names)
                    {
                        string inner = Difference(eo[name], ao[name], $"{path}.{name}");
                        if (inner != null) return inner;
                    }

                    return null;
                default:
                    return JToken.DeepEquals(expected, actual) ? null
                        : $"{path}: expected {expected.ToString(Formatting.None)}, got {actual.ToString(Formatting.None)}";
            }
        }
    }
}
