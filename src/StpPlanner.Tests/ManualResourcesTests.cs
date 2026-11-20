using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>
    /// Manual resources against the browser (tools/golden/compat: v1_compat_logic.js normalizeManualResources and the
    /// template's add/rename/remove), plus the session: assignment, undo/redo, persistence, and the apply package.
    /// </summary>
    public class ManualResourcesTests
    {
        private const string Day2 = "A100:2026-03-03";

        private static string CompatExpected => Path.Combine(Golden.Root, "tools", "golden", "compat", "expected");

        public static IEnumerable<object[]> CompatCases() => Directory.GetFiles(CompatExpected, "*.json")
            .Select(Path.GetFileName).Where(f => f != "index.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => new object[] { f });

        public static IEnumerable<object[]> PackageCases() => Golden.StrictCases()
            .Where(c => File.Exists(Path.Combine(Golden.Expected, $"{c[0]}.{c[1]}.apply.canonical.json")));

        // ------------------------------------------------------------------ golden parity with the JS

        [Theory]
        [MemberData(nameof(CompatCases))]
        public void Replays_the_browser_manual_resource_rules(string file)
        {
            var expected = Golden.Load(Path.Combine(CompatExpected, file));
            var vgs = expected["vgsResources"].Select(t => (string)t).Select(n => n.Trim().Length == 0 ? ActivityCatalog.NoResource : n.Trim())
                .Distinct(StringComparer.Ordinal).ToList();
            var state = ResourceState.Empty;
            int index = 0;
            foreach (var entry in expected["steps"])
            {
                var step = entry["step"];
                string label = $"{file} step {index++} {step["op"]}";
                var options = ManualResources.Options(vgs, state.Manual);
                string NameOf(JToken id) => id?.Type == JTokenType.String ? options.FirstOrDefault(o => o.Id == (string)id)?.Name : null;
                bool refused = false;
                switch ((string)step["op"])
                {
                    case "load":
                        state = Load(step["preferences"], vgs);
                        break;
                    case "add":
                        state = state.WithManual(ManualResources.Add(state.Manual, vgs, out _));
                        break;
                    case "rename":
                    {
                        var current = state.Manual.FirstOrDefault(r => r.Id == (string)step["id"]);
                        var renamed = ManualResources.Rename(state.Manual, (string)step["id"], (string)step["name"], vgs);
                        refused = renamed == null;
                        if (!refused) state = state.WithManual(renamed).Renamed(current.Name, renamed.First(r => r.Id == current.Id).Name);
                        break;
                    }
                    case "remove":
                    {
                        var list = ManualResources.Remove(state.Manual, (string)step["id"], out var removed);
                        if (removed != null) state = state.WithManual(list).Without(removed.Name);
                        break;
                    }
                    case "assignActivity":
                        state = state.WithActivity((string)step["activityId"], NameOf(step["resourceId"]));
                        break;
                    case "assignFragment":
                        state = state.WithDay((string)step["logicalUnitId"], NameOf(step["resourceId"]));
                        break;
                    default:
                        throw new InvalidOperationException("unknown op " + step["op"]);
                }

                Assert.True(refused == ((string)entry["notice"]).Length > 0, label + ": refusal");
                if (refused) Assert.Equal((string)entry["notice"], PlannerMessages.ManualResourceNameInvalid);
                Assert.Equal(entry["manualResources"].Select(r => (string)r["id"] + "|" + (string)r["name"]).ToList(),
                    state.Manual.Select(r => r.Id + "|" + r.Name).ToList());
                var jsOptions = entry["options"].Select(o => new { Id = (string)o["id"], Name = (string)o["name"], Manual = (string)o["source"] == "manual" }).ToList();
                var csOptions = ManualResources.Options(vgs, state.Manual);
                Assert.Equal(jsOptions.Select(o => $"{o.Id}|{o.Name}|{o.Manual}"), csOptions.Select(o => $"{o.Id}|{o.Name}|{o.IsManual}"));
                // The browser keys assignments by resource id; the plugin by name. Same equipment either way.
                string JsName(string id) => jsOptions.First(o => o.Id == id).Name;
                Assert.Equal(Map(((JObject)entry["fragmentResourceOverrides"]).Properties().ToDictionary(p => p.Name, p => JsName((string)p.Value))), Map(state.Days));
                Assert.Equal(Map(((JObject)entry["activityResourceOverrides"]).Properties().ToDictionary(p => p.Name, p => JsName((string)p.Value))), Map(state.Activities));
            }
        }

        // The stored browser preferences, translated to the plugin's form (names, not ids), through LocalOverrides.Load.
        private static ResourceState Load(JToken preferences, IReadOnlyCollection<string> vgs)
        {
            var manual = ManualResources.Normalize(preferences["manualResources"], vgs);
            JObject Names(JToken map)
            {
                var result = new JObject();
                if (!(map is JObject source)) return result;
                foreach (var pair in source.Properties())
                {
                    if (pair.Value.Type != JTokenType.String) { result[pair.Name] = pair.Value.DeepClone(); continue; }
                    string id = (string)pair.Value;
                    string name = id.StartsWith(ManualResources.VgsPrefix, StringComparison.Ordinal) ? id.Substring(ManualResources.VgsPrefix.Length)
                        : manual.FirstOrDefault(r => r.Id == id)?.Name ?? id;
                    result[pair.Name] = name;
                }

                return result;
            }

            var stored = new JObject
            {
                ["manualResources"] = preferences["manualResources"]?.DeepClone(),
                ["fragmentResourceOverrides"] = Names(preferences["fragmentResourceOverrides"]),
                ["activityResourceOverrides"] = Names(preferences["activityResourceOverrides"]),
            };
            var storage = new MemoryHorizonStorage();
            storage.SetItem(LocalOverrides.StoragePrefix + "p", stored.ToString());
            return LocalOverrides.Load(storage, "p", vgs).ResourceState;
        }

        private static string Map(IEnumerable<KeyValuePair<string, string>> map) =>
            string.Join(";", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

        [Fact]
        public void Js_trim_matches_ecmascript_not_dotnet()
        {
            Assert.Equal("a", ManualResources.JsTrim(((char)0xFEFF).ToString() + " a" + (char)0x3000));
            Assert.Equal(((char)0x85).ToString() + "a", ManualResources.JsTrim(((char)0x85).ToString() + "a"));   // NEL is not JS whitespace
            Assert.Equal(string.Empty, ManualResources.JsTrim(null));
        }

        // ------------------------------------------------------------------ session

        private static PlannerSession NewSession(MemoryHorizonStorage storage = null) =>
            new PlannerSession(ProjectionTests.ChainWithRates(), Golden.StaticConfig(), null, storage ?? new MemoryHorizonStorage(), new PlannerOptions { TypeColumns = new[] { "Type" } });

        private static DaySegment Segment(PlannerSession session, string unit) =>
            session.Rows.SelectMany(r => r.Segments).First(s => s.LogicalUnitId == unit);

        [Fact]
        public void Added_resources_are_offered_for_assignment_after_the_vgs_ones()
        {
            var session = NewSession();
            var vgs = session.ResourceOptions.ToList();
            var outcome = session.AddManualResource();

            Assert.True(outcome.Ok);
            Assert.Equal(PlannerMessages.ManualResourceAdded, outcome.Message);
            Assert.Equal(new ManualResource("manual:1", "Equipo manual 1"), session.LastAddedManualResource);
            Assert.Equal(vgs.Concat(new[] { "Equipo manual 1" }), session.ResourceOptions);
            Assert.True(session.ResourceDescriptors.Last().IsManual);
            Assert.False(session.HasPendingChanges);   // nothing for VGS

            Assert.True(session.AssignResource(Day2, "Equipo manual 1").Ok);
            Assert.Equal("Equipo manual 1", Segment(session, Day2).Resource);
            Assert.True(Segment(session, Day2).HasResourceOverride);
        }

        [Fact]
        public void Rename_keeps_assignments_and_refuses_blank_or_repeated_names()
        {
            var session = NewSession();
            session.AddManualResource();
            session.AssignResource(Day2, "Equipo manual 1");

            Assert.True(session.RenameManualResource("manual:1", "  Scoop LH-05 ").Ok);
            Assert.Equal("Scoop LH-05", session.ManualResources.Single().Name);
            Assert.Equal("Scoop LH-05", Segment(session, Day2).Resource);
            Assert.Contains("Scoop LH-05", session.ResourceOptions);
            Assert.DoesNotContain("Equipo manual 1", session.ResourceOptions);

            foreach (string bad in new[] { "", "   ", "jumbo 01", "EMPERNADOR 01", new string('x', 161) })
            {
                var refused = session.RenameManualResource("manual:1", bad);
                Assert.False(refused.Ok);
                Assert.Equal(PlannerMessages.ManualResourceNameInvalid, refused.Message);
            }

            Assert.False(session.RenameManualResource("manual:9", "Otro").Ok);
            Assert.Equal("Scoop LH-05", session.ManualResources.Single().Name);
        }

        [Fact]
        public void Remove_sends_its_days_and_activities_back_to_their_own_equipment()
        {
            var session = NewSession();
            session.AddManualResource("A100");
            Assert.Equal("Equipo manual 1", session.ActivityResource("A100"));
            Assert.Equal("Equipo manual 1", Segment(session, Day2).Resource);
            session.AssignResource("A100:2026-03-02", "Empernador 01");

            Assert.True(session.RemoveManualResource("manual:1").Ok);
            Assert.Empty(session.ManualResources);
            Assert.Equal("Jumbo 01", session.ActivityResource("A100"));
            Assert.Equal("Jumbo 01", Segment(session, Day2).Resource);
            Assert.False(Segment(session, Day2).HasResourceOverride);
            Assert.Equal("Empernador 01", Segment(session, "A100:2026-03-02").Resource);   // other equipment stays
            Assert.False(session.RemoveManualResource("manual:1").Ok);
        }

        [Fact]
        public void Activity_equipment_is_the_default_of_its_days_and_a_day_assignment_wins()
        {
            var session = NewSession();
            Assert.True(session.AssignActivityResource("A100", "Empernador 01").Ok);
            Assert.Equal("Empernador 01", session.Selected?.Resource ?? session.ActivityResource("A100"));
            Assert.All(session.Rows.First(r => r.ActivityId == "A100").Segments.Where(s => s.Status != SegmentStatus.Completed),
                s => Assert.Equal("Empernador 01", s.Resource));

            session.AddManualResource();
            session.AssignResource(Day2, "Equipo manual 1");
            Assert.Equal("Equipo manual 1", Segment(session, Day2).Resource);
            Assert.True(session.AssignResource(Day2, "Empernador 01").Ok);   // = the activity's own: clears the day
            Assert.Null(session.AssignedResource(Day2));

            session.SelectedActivityId = "A100";
            Assert.Equal("Empernador 01", session.Selected.Resource);
            Assert.Equal("Jumbo 01", session.Selected.VgsResource);
            Assert.True(session.AssignActivityResource("A100", "Jumbo 01").Ok);   // its VGS resource clears it
            Assert.Equal("Jumbo 01", session.Selected.Resource);
            Assert.Equal(PlannerMessages.UnknownResource, session.AssignActivityResource("A100", "Nada").Message);
            Assert.Equal(PlannerMessages.UnknownActivity, session.AssignActivityResource("ZZZ", "Jumbo 01").Message);
        }

        [Fact]
        public void Undo_and_redo_cover_every_equipment_edit()
        {
            var storage = new MemoryHorizonStorage();
            var session = NewSession(storage);
            session.AddManualResource();                                   // 1
            session.AssignResource(Day2, "Equipo manual 1");               // 2
            var fragment = Segment(session, Day2).FragmentId;
            Assert.True(session.MoveDailyTail("A100", fragment, "2026-03-10").Ok);   // 3 (plan)
            session.RenameManualResource("manual:1", "Camión 3");          // 4
            session.RemoveManualResource("manual:1");                      // 5
            Assert.Equal("Quitar recurso manual Camión 3", session.UndoDescription);

            Assert.True(session.Undo().Ok);   // 5
            Assert.Equal("Camión 3", session.ManualResources.Single().Name);
            Assert.Equal("Camión 3", Segment(session, Day2).Resource);
            Assert.Equal("2026-03-10", Segment(session, Day2).Date);       // the plan move is untouched
            session.Undo();                   // 4
            Assert.Equal("Equipo manual 1", Segment(session, Day2).Resource);
            session.Undo();                   // 3: the move goes, the equipment stays
            Assert.Equal("2026-03-03", Segment(session, Day2).Date);
            Assert.Equal("Equipo manual 1", Segment(session, Day2).Resource);
            session.Undo();                   // 2
            Assert.Null(session.AssignedResource(Day2));
            session.Undo();                   // 1
            Assert.Empty(session.ManualResources);
            Assert.DoesNotContain("Equipo manual 1", session.ResourceOptions);
            Assert.False(session.CanUndo);
            Assert.Empty(NewSession(storage).ManualResources);   // undo is saved too

            while (session.CanRedo) Assert.True(session.Redo().Ok);
            Assert.Empty(session.ManualResources);
            Assert.Null(session.AssignedResource(Day2));
            Assert.Equal("2026-03-10", Segment(session, Day2).Date);
            session.Undo();
            Assert.Equal("Camión 3", NewSession(storage).ManualResources.Single().Name);
        }

        [Fact]
        public void A_refused_or_unchanged_edit_is_not_an_undo_step()
        {
            var session = NewSession();
            session.RenameManualResource("manual:1", "x");
            session.AssignResource(Day2, null);
            session.AssignActivityResource("A100", "Jumbo 01");
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Saved_per_project_and_normalized_against_vgs_on_load()
        {
            var storage = new MemoryHorizonStorage();
            var session = NewSession(storage);
            session.AddManualResource("A100");
            session.AddManualResource();
            session.RenameManualResource("manual:2", "Scoop LH-05");
            session.AssignResource(Day2, "Scoop LH-05");

            var reopened = NewSession(storage);
            Assert.Equal(new[] { "manual:1|Equipo manual 1", "manual:2|Scoop LH-05" }, reopened.ManualResources.Select(r => r.Id + "|" + r.Name));
            Assert.Equal("Scoop LH-05", reopened.AssignedResource(Day2));
            Assert.Equal("Equipo manual 1", reopened.ActivityResource("A100"));
            Assert.False(reopened.CanUndo);

            // A stored manual name that VGS now uses is dropped (JS normalizeManualResources); so is an assignment to
            // equipment that no longer exists.
            string key = LocalOverrides.StoragePrefix + ProjectionTests.ChainWithRates().ProjectKey;
            var stored = JObject.Parse(storage.GetItem(key));
            stored["manualResources"][0]["name"] = "JUMBO 01";
            stored["activityResourceOverrides"]["A100"] = "JUMBO 01";
            stored["fragmentResourceOverrides"]["A100:2026-03-02"] = "Pala retirada";
            storage.SetItem(key, stored.ToString());
            var normalized = NewSession(storage);
            Assert.Equal(new[] { "manual:2" }, normalized.ManualResources.Select(r => r.Id));
            Assert.Equal("Jumbo 01", normalized.ActivityResource("A100"));
            Assert.Null(normalized.AssignedResource("A100:2026-03-02"));
            Assert.Equal("Scoop LH-05", normalized.AssignedResource(Day2));
        }

        [Fact]
        public void Storage_written_before_manual_resources_still_loads()
        {
            var storage = new MemoryHorizonStorage();
            string key = LocalOverrides.StoragePrefix + ProjectionTests.ChainWithRates().ProjectKey;
            storage.SetItem(key, "{\"fragmentResourceOverrides\":{\"A100:2026-03-03\":\"Empernador 01\"},\"fragmentCapacityExemptions\":{\"A100:2026-03-03\":true}}");
            var session = NewSession(storage);
            Assert.Equal("Empernador 01", session.AssignedResource(Day2));
            Assert.True(session.IsCapacityExempt(Day2));
            Assert.Empty(session.ManualResources);
        }

        /// <summary>Manual resources and assignments are local: the apply package stays Python's canonical bytes.</summary>
        [Theory]
        [MemberData(nameof(PackageCases))]
        public void Equipment_edits_never_change_the_apply_package(string fixture, string caseName)
        {
            var snapshot = Golden.Snapshot(fixture);
            var testCase = Golden.Case(fixture, caseName);
            var session = new PlannerSession(snapshot, Golden.StaticConfig(), null, Golden.Storage(testCase, snapshot.ProjectKey), null, Golden.Horizon(testCase["initialHorizon"]));
            string activity = snapshot.Activities.First().Id;
            session.AddManualResource(activity);
            foreach (var step in Golden.Load(Path.Combine(Golden.Expected, $"{fixture}.{caseName}.json"))["steps"].Select(s => s["step"]))
            {
                GoldenSessionTests.Run(session, step);
                session.AddManualResource();
            }

            session.RenameManualResource("manual:1", "Equipo renombrado");
            session.AssignActivityResource(activity, session.ManualResources.Last().Name);
            string canonical = Golden.PythonCanonical[$"{fixture}.{caseName}.apply.canonical.json"];
            Assert.Equal(canonical, Encoding.UTF8.GetString(session.BuildApplyPackage().ToCanonicalBytes()));
            session.Undo();
            session.Undo();
            Assert.Equal(canonical, Encoding.UTF8.GetString(session.BuildApplyPackage().ToCanonicalBytes()));
        }
    }
}
