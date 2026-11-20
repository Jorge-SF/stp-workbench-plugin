using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using ActualProgress = Maptek.Vulcan.Stp.Engine.ActualProgress;
using ActualProgressState = Maptek.Vulcan.Stp.Engine.ActualProgressState;
using Maptek.Vulcan.Stp.Planner.Export;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Planner.Tests
{
    /// <summary>The Exportar group: Excel request, PDF report and STP suelto.</summary>
    public class ExportTests : IDisposable
    {
        private static readonly PlannerOptions TypeOptions = new PlannerOptions { TypeColumns = new[] { "Type" } };

        private readonly string folder = Path.Combine(Path.GetTempPath(), "stp-export-tests-" + Guid.NewGuid().ToString("N"));

        public ExportTests()
        {
            Directory.CreateDirectory(folder);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }

        private static (PlannerSession Session, ExportBridge Bridge) Live(SnapshotV2 snapshot = null)
        {
            snapshot = snapshot ?? ProjectionTests.ChainWithRates();
            var bridge = new ExportBridge();
            return (new PlannerSession(snapshot, Golden.LiveConfig(snapshot), bridge, new MemoryHorizonStorage(), TypeOptions), bridge);
        }

        // ------------------------------------------------------------------ export view and Excel request

        [Fact]
        public void ExportFreezesTheVisibleView()
        {
            var session = ProjectionTests.Session();
            var export = session.CreateExport();
            Assert.Equal(ExportMessages.DefaultTitle, export.Title);
            Assert.Equal(session.Days.Select(d => d.Date), export.Days.Select(d => d.Date));
            Assert.Equal(session.Rows.Select(r => r.ActivityId), export.Rows.Select(r => r.ActivityId));
            Assert.Equal(session.Capacity.Select(c => c.Type), export.Capacity.Select(c => c.Type));
            Assert.All(export.Rows, r => Assert.Equal(export.Days.Count, r.Cells.Count));
            Assert.Equal(export.Days[0].Label + " — " + export.Days[export.Days.Count - 1].Label, export.RangeLabel);

            // Each day's planned value is the sum of its pending segments (buildExcelExportPayload plannedFor).
            foreach (var row in session.Rows)
            {
                var exported = export.Rows.Single(r => r.ActivityId == row.ActivityId);
                for (int i = 0; i < export.Days.Count; i++)
                {
                    var pending = row.Segments.Where(s => s.Date == export.Days[i].Date && s.Status != SegmentStatus.Completed).ToList();
                    double? expected = pending.Count == 0 ? (double?)null : pending.Sum(s => s.Amount ?? 0);
                    Assert.Equal(expected, exported.Cells[i].Planned);
                }
            }

            Assert.Contains(export.Rows, r => r.Cells.Any(c => c.Planned > 0));
            session.SearchText = "A100";
            Assert.Equal(new[] { "A100" }, session.CreateExport().Rows.Select(r => r.ActivityId));
        }

        [Fact]
        public void ExcelRequestMatchesTheBridgeContract()
        {
            var session = ProjectionTests.Session();
            var export = session.CreateExport("Plan semanal");
            var json = export.ToExcelRequest().ToJson();
            Assert.Equal(ExcelExportRequest.SchemaId, (string)json["schema"]);
            Assert.Equal(session.Snapshot.ProjectKey, (string)json["projectKey"]);
            Assert.Equal("Plan semanal", (string)json["title"]);
            Assert.False((bool)json["actualProgressMode"]);
            Assert.Equal(export.Days.Count, ((JArray)json["days"]).Count);
            var activity = (JObject)json["activities"].First(a => (string)a["id"] == "A100");
            Assert.Equal(new[] { "id", "name", "type", "resource", "planned", "actual", "color" }, activity.Properties().Select(p => p.Name));
            Assert.Matches("^#[0-9A-Fa-f]{6}$", (string)activity["color"]);
            Assert.All(activity["actual"], v => Assert.Equal(JTokenType.Null, v.Type));
            var aggregate = json["aggregates"].First();
            StringAssert.StartsWith("Capacidad · ", (string)aggregate["label"]);
            Assert.Equal(export.Days.Count, ((JArray)aggregate["values"]).Count);
            Assert.Equal(export.Days.Count, ((JArray)aggregate["states"]).Count);
            Assert.Equal(session.ActualProgressSummary.Types.Select(t => t.Type), json["chartTypes"].Select(t => (string)t["type"]));
            Assert.DoesNotContain(json["aggregates"], a => ((string)a["label"]).StartsWith("Avance real", StringComparison.Ordinal));
            Assert.Empty((JArray)json["chartImages"]);
            Assert.NotEmpty((JArray)json["legend"]);
        }

        [Fact]
        public void ExcelNumberFollowsTheTemplate()
        {
            Assert.Equal("12", PlanExport.Number(12));
            Assert.Equal("12.5", PlanExport.Number(12.5));
            Assert.Equal("12.35", PlanExport.Number(12.345));
            Assert.Equal("—", PlanExport.Number(double.NaN));
        }

        [Fact]
        public void ExcelRefusesMoreThan31Days()
        {
            var session = ProjectionTests.Session();
            Assert.True(session.SetDuration(40).Ok);
            var export = session.CreateExport();
            Assert.Equal(40, export.Days.Count);
            Assert.Equal(ExportMessages.ExcelTooManyDays, export.ExcelBlockedReason());
            Assert.Throws<InvalidOperationException>(() => export.ToExcelRequest());
        }

        [Fact]
        public async Task ExcelOfflineExplainsWhy()
        {
            var session = ProjectionTests.Session();
            Assert.False(session.CanExportExcel);
            Assert.Equal(ExportMessages.ExcelNeedsLive, session.ExportExcelBlockedReason);
            var outcome = await PlannerExports.ExcelAsync(session, Path.Combine(folder, "x.xlsx"));
            Assert.False(outcome.Ok);
            Assert.Equal(ExportMessages.ExcelNeedsLive, outcome.Message);
            Assert.False(File.Exists(Path.Combine(folder, "x.xlsx")));
        }

        [Fact]
        public async Task ExcelPostsTheViewAndWritesTheWorkbook()
        {
            var (session, bridge) = Live();
            Assert.True(session.CanExportExcel, session.ExportExcelBlockedReason);
            string path = Path.Combine(folder, "STP.xlsx");
            var outcome = await PlannerExports.ExcelAsync(session, path);
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(string.Format(ExportMessages.ExcelSaved, path), outcome.Message);
            Assert.Equal(ExportBridge.Workbook, File.ReadAllBytes(path));
            Assert.Equal(session.Rows.Count, bridge.Requests.Single().Activities.Count);
            Assert.False(session.IsBusy);
            Assert.Empty(Directory.GetFiles(folder, "*.stp-tmp"));
        }

        [Fact]
        public async Task ExcelBridgeErrorsAreSpanish()
        {
            var (session, bridge) = Live();
            bridge.Error = new StpBridgeException(StpBridgeErrorKind.ServerError, "boom", 500);
            string path = Path.Combine(folder, "STP.xlsx");
            var outcome = await PlannerExports.ExcelAsync(session, path);
            Assert.False(outcome.Ok);
            StringAssert.StartsWith(ExportMessages.ExcelFailed, outcome.Message);
            Assert.Contains("xlsxwriter", outcome.Message);
            Assert.False(File.Exists(path));
            Assert.False(session.IsBusy);
        }

        // ------------------------------------------------------------------ PDF

        [Fact]
        public void PdfIsAValidDocument()
        {
            var session = ProjectionTests.Session();
            session.MoveActivity("A100", 1);
            string path = Path.Combine(folder, "STP.pdf");
            var outcome = PlannerExports.Pdf(session, path, generatedAt: new DateTime(2026, 3, 2, 9, 30, 0));
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(string.Format(ExportMessages.PdfSaved, path), outcome.Message);
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.GetEncoding(1252).GetString(bytes);
            StringAssert.StartsWith("%PDF-1.4", text);
            Assert.EndsWith("%%EOF\n", text);
            AssertXrefPointsAtObjects(bytes, text);

            // 28 days: landscape A4, one page.
            Assert.Contains("/MediaBox [0 0 841.89 595.28]", text);
            Assert.Equal(1, Regex.Matches(text, "/Type /Page ").Count);
            Assert.Contains("(Actividad)", text);
            Assert.Contains("(A100 \xB7 ", text);              // "·" in WinAnsi
            Assert.Contains("(Capacidad \xB7 ", text);
            Assert.Contains("(" + PdfPlanReport.FooterText + ")", text);
            Assert.Contains("(P\xE1gina 1 de 1)", text);
            Assert.Contains("(Generado 02/03/2026 09:30)", text);
        }

        [Fact]
        public void PdfPortraitForShortRangesAndBlocksOf31Days()
        {
            Assert.False(PdfPlanReport.IsLandscape(14));
            Assert.True(PdfPlanReport.IsLandscape(15));

            var session = ProjectionTests.Session();
            Assert.True(session.SetDuration(40).Ok);
            var text = Encoding.GetEncoding(1252).GetString(PdfPlanReport.Render(session.CreateExport(), DateTime.Now));
            Assert.Equal(2, Regex.Matches(text, "/Type /Page ").Count);     // 31 + 9 days
            Assert.Contains("(P\xE1gina 2 de 2)", text);
        }

        [Fact]
        public void PdfPaginatesManyRows()
        {
            var export = SyntheticExport(rows: 120, days: 10);
            var bytes = PdfPlanReport.Render(export, DateTime.Now);
            var text = Encoding.GetEncoding(1252).GetString(bytes);
            Assert.Contains("/MediaBox [0 0 595.28 841.89]", text);
            int pages = Regex.Matches(text, "/Type /Page ").Count;
            Assert.True(pages >= 2, pages.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(pages, Regex.Matches(text, @"\(Actividad\)").Count);   // header repeated
            Assert.Contains("(R119 \xB7 ", text);
            AssertXrefPointsAtObjects(bytes, text);
        }

        [Fact]
        public void PdfWithoutRowsIsRefused()
        {
            var session = ProjectionTests.Session();
            session.SearchText = "no-such-activity";
            var outcome = PlannerExports.Pdf(session, Path.Combine(folder, "x.pdf"));
            Assert.False(outcome.Ok);
            Assert.Equal(ExportMessages.NoRows, outcome.Message);
        }

        [Fact]
        public void PdfTextIsEscapedAndFitted()
        {
            Assert.Equal(@"(a\(b\)c\\d)", PdfWriter.Literal(@"a(b)c\d"));
            Assert.Equal("(Barrenaci\xF3n ?)", PdfWriter.Literal("Barrenación \u4E2D"));
            Assert.Equal("(a b)", PdfWriter.Literal("a\nb"));
            string fitted = PdfWriter.Fit("Desarrollo rampa nivel 1200 frente norte", 60, 7);
            Assert.EndsWith("…", fitted);
            Assert.True(PdfWriter.MeasureText(fitted, 7) <= 60);
            Assert.Equal("corto", PdfWriter.Fit("corto", 60, 7));
            Assert.Equal(PdfColor.Black.R, PdfColor.Parse("#F5F5F5", PdfColor.White).Foreground().R);
            Assert.Equal(PdfColor.White.R, PdfColor.Parse("#17345B", PdfColor.Black).Foreground().R);
        }

        // ------------------------------------------------------------------ STP suelto

        [Fact]
        public void StandaloneEmbedsThePlanWithAStaticBridge()
        {
            var session = ProjectionTests.Session();
            Assert.True(session.MoveActivity("A100", 1).Ok);
            string path = Path.Combine(folder, StandalonePlanner.DefaultFileName);
            var outcome = PlannerExports.Standalone(session, WebFolder, path);
            Assert.True(outcome.Ok, outcome.Message);
            Assert.Equal(string.Format(ExportMessages.StandaloneSaved, path), outcome.Message);
            string html = File.ReadAllText(path, Encoding.UTF8);

            Assert.DoesNotContain("__VGS_STP_V2_", html);
            Assert.DoesNotContain("const standaloneBootstrap = null;", html);
            Assert.Single(Regex.Matches(html, @"const standaloneBootstrap = \{").Cast<Match>());

            var bootstrap = InlineJson(html, "const standaloneBootstrap = ");
            var bridge = InlineJson(html, "const bridgeConfig = ");
            Assert.Equal("static", (string)bridge["mode"]);
            Assert.Equal(string.Empty, (string)bridge["token"]);
            Assert.Equal(string.Empty, (string)bridge["endpoint"]);
            Assert.Equal(session.Snapshot.ProjectKey, (string)bridge["projectKey"]);
            Assert.Equal(session.PrecedencePolicy, (string)bridge["precedenceConflictPolicy"]);
            Assert.Equal(session.Snapshot.ProjectKey, (string)bootstrap["snapshot"]["projectKey"]);
            Assert.Equal(session.Horizon.HorizonStart, (string)bootstrap["plannerState"]["horizon"]["horizonStart"]);
            Assert.True(JToken.DeepEquals(ActualProgressStore.ToJson(ActualProgressState.Empty), bootstrap["localState"]));

            // The moved plan travels: fragment positions and provenance as the engine has them.
            var fragments = (JArray)bootstrap["plannerState"]["model"]["fragments"];
            Assert.Equal(session.State.Model.Fragments.Count, fragments.Count);
            foreach (var f in session.State.Model.Fragments)
            {
                var j = fragments.Single(x => (string)x["id"] == f.Id);
                Assert.Equal(f.Start, (string)j["start"]);
                Assert.Equal(f.Finish, (string)j["finish"]);
                Assert.Equal(f.OriginalStart, (string)j["originalStart"]);
                Assert.Equal(f.Provenance, (string)j["provenance"]);
                Assert.Equal(f.ActivityId, (string)j["activityId"]);
            }

            Assert.Contains(fragments, x => (string)x["provenance"] == "direct");

            // Modules are inlined and still parse as scripts (</script> escaped).
            Assert.Contains("export function buildPlannerModel", html);
            Assert.Equal(1, Regex.Matches(html, "<script id=\"planner-runtime\"").Count);
        }

        [Fact]
        public void StandaloneEscapesInlineJsonAndScriptEnds()
        {
            var resources = new StandaloneResources
            {
                Template = "<script>/*STP_V2_STANDALONE_BOOTSTRAP_BEGIN*/\nconst standaloneBootstrap = null;\nconst snapshot = __VGS_STP_V2_SNAPSHOT__;\n"
                    + "const bridgeConfig = __VGS_STP_V2_BRIDGE__;\n/*STP_V2_STANDALONE_BOOTSTRAP_END*/</script><script>__VGS_STP_V2_LOGIC__</script>",
                ModuleSources = new Dictionary<string, string> { ["__VGS_STP_V2_LOGIC__"] = "if (a < b) x = '</SCRIPT>';" },
            };
            string html = StandalonePlanner.Render(resources, new JObject { ["name"] = "</script><b>&" }, new JObject { ["mode"] = "static" });
            Assert.Contains("\\u003c/script\\u003e\\u003cb\\u003e\\u0026", html);
            Assert.Contains("if (a < b) x = '<\\/script>';", html);
            Assert.Equal(2, Regex.Matches(html, "</script>").Count);
        }

        [Fact]
        public void StandaloneRejectsBrokenTemplates()
        {
            var modules = new Dictionary<string, string> { ["__VGS_STP_V2_LOGIC__"] = "x" };
            Assert.Throws<InvalidDataException>(() => StandalonePlanner.Render(
                new StandaloneResources { Template = "__VGS_STP_V2_SNAPSHOT__ __VGS_STP_V2_BRIDGE__ __VGS_STP_V2_LOGIC__", ModuleSources = modules },
                new JObject(), new JObject()));    // no bootstrap block
            Assert.Throws<InvalidDataException>(() => StandalonePlanner.Render(
                new StandaloneResources { Template = "/*STP_V2_STANDALONE_BOOTSTRAP_BEGIN*/__VGS_STP_V2_SNAPSHOT__ __VGS_STP_V2_BRIDGE__/*STP_V2_STANDALONE_BOOTSTRAP_END*/ __VGS_STP_V2_LOGIC__ __VGS_STP_V2_NEW__", ModuleSources = modules },
                new JObject(), new JObject()));    // unknown placeholder
        }

        [Fact]
        public void StandaloneWithoutResourcesSaysWhereItLooked()
        {
            var session = ProjectionTests.Session();
            string path = Path.Combine(folder, "x.html");
            var outcome = PlannerExports.Standalone(session, folder, path);
            Assert.False(outcome.Ok);
            Assert.Equal(string.Format(ExportMessages.StandaloneNoTemplate, folder), outcome.Message);
            Assert.False(File.Exists(path));
        }

        // ------------------------------------------------------------------ "Avance real"

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

        // A100: 7.5 on its first planned day, 2 on an added box of a free day.
        private static (string Planned, string Free) EnterProgress(PlannerSession session)
        {
            session.ShowActualProgress = true;
            var row = session.Rows.First(r => r.ActivityId == "A100");
            string planned = row.Segments.First(s => s.Status != SegmentStatus.Completed).Date;
            string free = session.Days.Select(d => d.Date).Last(d => row.Segments.All(s => s.Date != d));
            Assert.True(session.SetActualProgress("A100", planned, 7.5).Ok);
            Assert.True(session.AddActualProgress("A100", free).Ok);
            Assert.True(session.SetActualProgress("A100", free, 2).Ok);
            return (planned, free);
        }

        [Fact]
        public void ExcelCarriesActualProgress()
        {
            var (session, _) = Live();
            var (planned, free) = EnterProgress(session);
            var export = session.CreateExport();
            Assert.True(export.ShowActualProgress);
            var json = export.ToExcelRequest(new[] { new ExcelExportChartImage("Desarrollo", Png) }).ToJson();
            Assert.True((bool)json["actualProgressMode"]);

            // activities[].actual: the entered value of each day, null elsewhere
            var dates = export.Days.Select(d => d.Date).ToList();
            var actual = (JArray)json["activities"].First(a => (string)a["id"] == "A100")["actual"];
            Assert.Equal(7.5, (double)actual[dates.IndexOf(planned)]);
            Assert.Equal(2, (double)actual[dates.IndexOf(free)]);
            Assert.Equal(dates.Count - 2, actual.Count(v => v.Type == JTokenType.Null));
            Assert.All(json["activities"].Where(a => (string)a["id"] != "A100"), a => Assert.All(a["actual"], v => Assert.Equal(JTokenType.Null, v.Type)));

            // "Avance real · tipo" after the capacity rows: real/plan, under-progress / met-progress
            var aggregates = json["aggregates"].ToList();
            int firstActual = aggregates.FindIndex(a => ((string)a["label"]).StartsWith("Avance real · ", StringComparison.Ordinal));
            Assert.Equal(export.Capacity.Count, firstActual);
            Assert.Equal(session.ActualProgressRows.Select(r => r.Label), aggregates.Skip(firstActual).Select(a => (string)a["label"]));
            string type = session.Rows.First(r => r.ActivityId == "A100").Type;
            var mine = aggregates.Single(a => (string)a["label"] == "Avance real · " + type);
            var day = session.ActualProgressSummary.Types.Single(t => t.Type == type).Day(planned);
            Assert.Equal(PlanExport.Number(day.Actual.Value) + "/" + PlanExport.Number(day.Scheduled), (string)mine["values"][dates.IndexOf(planned)]);
            Assert.Equal(day.Actual < day.Scheduled ? "under-progress" : "met-progress", (string)mine["states"][dates.IndexOf(planned)]);
            var tab = session.ActualProgressRows.Single(r => r.Type == type).Cells;
            for (int i = 0; i < dates.Count; i++)
            {
                string state = tab[i].IsUnder ? "under-progress" : tab[i].IsMet ? "met-progress" : string.Empty;
                Assert.Equal(state, (string)mine["states"][i]);
                if (tab[i].IsEmpty) Assert.Equal("—", (string)mine["values"][i]);
            }

            // chartTypes: planned per day and actual per equipment, as the charts have them
            var chart = json["chartTypes"].Single(c => (string)c["type"] == type);
            var progress = session.ActualProgressSummary.Types.Single(t => t.Type == type);
            Assert.Equal(dates.Select(d => progress.Day(d)?.Scheduled ?? 0), chart["planned"].Select(v => (double)v));
            var byResource = (JObject)chart["actualByResource"];
            Assert.Equal(progress.ActualByResource.Select(p => p.Key), byResource.Properties().Select(p => p.Name));
            Assert.Equal(9.5, byResource.Properties().SelectMany(p => p.Value).Where(v => v.Type != JTokenType.Null).Sum(v => (double)v));
            Assert.Equal(9.5, ((JObject)chart["actualTotalsByResource"]).Properties().Sum(p => (double)p.Value));
            Assert.Equal(progress.PlannedByResource.Sum(p => p.Value), ((JObject)chart["plannedByResource"]).Properties().Sum(p => (double)p.Value));

            var image = (JObject)json["chartImages"].Single();
            Assert.Equal("Desarrollo", (string)image["title"]);
            StringAssert.StartsWith("data:image/png;base64,", (string)image["pngDataUrl"]);
        }

        [Fact]
        public void ExcelWithTheModeOffKeepsValuesButNoRows()
        {
            var (session, _) = Live();
            var (planned, _) = EnterProgress(session);
            session.ShowActualProgress = false;
            var export = session.CreateExport();
            Assert.Empty(export.ActualProgress);
            var json = export.ToExcelRequest().ToJson();
            Assert.False((bool)json["actualProgressMode"]);
            int at = export.Days.Select(d => d.Date).ToList().IndexOf(planned);
            Assert.Equal(7.5, (double)json["activities"].First(a => (string)a["id"] == "A100")["actual"][at]);   // template: read whatever the mode
            Assert.DoesNotContain(json["aggregates"], a => ((string)a["label"]).StartsWith("Avance real", StringComparison.Ordinal));
            Assert.All(json["chartTypes"], c => Assert.Empty((JObject)c["actualByResource"]));
        }

        [Fact]
        public async Task ExcelSendsTheChartImages()
        {
            var (session, bridge) = Live();
            EnterProgress(session);
            var images = Enumerable.Range(0, 40).Select(i => new ExcelExportChartImage("T" + i.ToString(CultureInfo.InvariantCulture), Png)).ToList();
            var outcome = await PlannerExports.ExcelAsync(session, Path.Combine(folder, "STP.xlsx"), chartImages: images);
            Assert.True(outcome.Ok, outcome.Message);
            var request = bridge.Requests.Single();
            Assert.True(request.ActualProgressMode);
            Assert.Equal(ExcelExportRequest.MaxChartImages, request.ChartImages.Count);   // the bridge takes 32
            Assert.Equal("T0", request.ChartImages[0].Title);
        }

        [Fact]
        public void PdfDrawsActualProgressWithTheModeOn()
        {
            var session = ProjectionTests.Session();
            Assert.DoesNotContain("Avance real", Encoding.GetEncoding(1252).GetString(PdfPlanReport.Render(session.CreateExport(), DateTime.Now)));

            EnterProgress(session);
            var export = session.CreateExport();
            var bytes = PdfPlanReport.Render(export, DateTime.Now);
            var text = Encoding.GetEncoding(1252).GetString(bytes);
            AssertXrefPointsAtObjects(bytes, text);
            foreach (var row in session.ActualProgressRows) Assert.Contains("(Avance real \xB7 " + row.Type + ")", text);
            Assert.Contains("(7.5)", text);                                   // value boxes
            Assert.Contains("(Real: 9.5 / Plan: ", text);                     // period total of A100's type
            Assert.Contains("(Avance real)", text);                           // legend
            string type = session.Rows.First(r => r.ActivityId == "A100").Type;
            var cell = session.ActualProgressRows.Single(r => r.Type == type).Cells.First(c => !c.IsEmpty);
            Assert.Contains("(" + cell.Text + ")", text);

            // Off again: the boxes and rows go, the plan stays.
            session.ShowActualProgress = false;
            text = Encoding.GetEncoding(1252).GetString(PdfPlanReport.Render(session.CreateExport(), DateTime.Now));
            Assert.DoesNotContain("Avance real", text);
            Assert.DoesNotContain("(7.5)", text);
        }

        [Fact]
        public void PdfWithProgressStillPaginates()
        {
            var session = ProjectionTests.Session();
            EnterProgress(session);
            Assert.True(session.SetDuration(40).Ok);
            var bytes = PdfPlanReport.Render(session.CreateExport(), DateTime.Now);
            var text = Encoding.GetEncoding(1252).GetString(bytes);
            Assert.Equal(2, Regex.Matches(text, "/Type /Page ").Count);
            Assert.Equal(2 * session.ActualProgressRows.Count, Regex.Matches(text, "\\(Avance real \xB7 ").Count);   // each block has the rows
            AssertXrefPointsAtObjects(bytes, text);
        }

        [Fact]
        public void StandaloneCarriesTheActualProgress()
        {
            var session = ProjectionTests.Session();
            var (planned, free) = EnterProgress(session);
            string path = Path.Combine(folder, StandalonePlanner.DefaultFileName);
            Assert.True(PlannerExports.Standalone(session, WebFolder, path).Ok);
            var local = (JObject)InlineJson(File.ReadAllText(path, Encoding.UTF8), "const standaloneBootstrap = ")["localState"];
            Assert.True(JToken.DeepEquals(ActualProgressStore.ToJson(session.ActualProgressState), local), local.ToString());
            Assert.Equal(7.5, (double)local["actualProgressByDay"][ActualProgress.Key("A100", planned)]);
            Assert.Equal(2, (double)local["actualProgressByDay"][ActualProgress.Key("A100", free)]);
            Assert.True((bool)local["actualProgressSlotsByDay"][ActualProgress.Key("A100", free)]);

            // what the page reads back is the same progress
            Assert.True(ActualProgressStore.FromJson(local).SameContent(session.ActualProgressState));
        }

        // ------------------------------------------------------------------ file writing

        [Fact]
        public void WriteReplacesAndCancels()
        {
            string path = Path.Combine(folder, "out.bin");
            File.WriteAllText(path, "old");
            Assert.True(PlannerExports.Write(path, new byte[] { 1, 2 }, "ok {0}", "bad").Ok);
            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
            Assert.Equal("cancelled", PlannerExports.Write(" ", new byte[0], "ok {0}", "bad").Code);
            var missing = PlannerExports.Write(Path.Combine(folder, "no-such-dir", "x.bin"), new byte[] { 1 }, "ok {0}", "bad");
            Assert.False(missing.Ok);
            StringAssert.StartsWith("bad", missing.Message);
            Assert.Empty(Directory.GetFiles(folder, "*.stp-tmp", SearchOption.AllDirectories));
        }

        // ------------------------------------------------------------------ helpers

        private static string WebFolder => Path.Combine(Golden.Root, "reference", "web");

        private static JObject InlineJson(string html, string prefix)
        {
            int start = html.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
            int end = html.IndexOf(";\n", start, StringComparison.Ordinal);
            return (JObject)WireJson.Parse(Encoding.UTF8.GetBytes(html.Substring(start, end - start)));
        }

        private static void AssertXrefPointsAtObjects(byte[] bytes, string text)
        {
            int startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF").Groups[1].Value, CultureInfo.InvariantCulture);
            StringAssert.StartsWith("xref\n", text.Substring(startxref));
            var entries = Regex.Matches(text.Substring(startxref), @"(\d{10}) 00000 n ").Cast<Match>().ToList();
            Assert.NotEmpty(entries);
            for (int i = 0; i < entries.Count; i++)
            {
                int offset = int.Parse(entries[i].Groups[1].Value, CultureInfo.InvariantCulture);
                StringAssert.StartsWith((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj", text.Substring(offset));
            }

            foreach (Match stream in Regex.Matches(text, @"<< /Length (\d+) >>\nstream\n"))
            {
                int length = int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.Equal("\nendstream", text.Substring(stream.Index + stream.Length + length, 10));
            }

            Assert.Equal(bytes.Length, text.Length);
        }

        private static PlanExport SyntheticExport(int rows, int days)
        {
            // A long plan from chain_fs repeated: enough rows to need several pages.
            var raw = Golden.RawSnapshot("chain_fs");
            var template = (JObject)raw["activities"][0];
            var activities = new JArray();
            var fragmentIds = new HashSet<string>();
            for (int i = 0; i < rows; i++)
            {
                var copy = (JObject)template.DeepClone();
                string id = "R" + i.ToString(CultureInfo.InvariantCulture);
                string old = (string)copy["id"];
                copy["id"] = id;
                foreach (var cell in ((JObject)copy["grid"]).Properties().Where(p => (string)p.Value == old).ToList()) cell.Value = id;
                foreach (JObject f in copy["fragments"]) f["id"] = id + ":" + (string)f["id"];
                activities.Add(copy);
            }

            raw["activities"] = activities;
            raw["precedences"] = new JArray();
            var session = new PlannerSession(SnapshotV2.FromJson(raw), Golden.StaticConfig(), null, new MemoryHorizonStorage(), TypeOptions);
            Assert.True(session.SetDuration(days).Ok);
            return session.CreateExport();
        }

        private static class StringAssert
        {
            public static void StartsWith(string expected, string actual) => Assert.True(actual != null && actual.StartsWith(expected, StringComparison.Ordinal), $"'{Short(actual)}' does not start with '{expected}'");

            private static string Short(string s) => s == null ? "null" : s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        private sealed class ExportBridge : IPlannerBridge, IExcelExportBridge
        {
            public static readonly byte[] Workbook = { (byte)'P', (byte)'K', 3, 4, 42 };

            public List<ExcelExportRequest> Requests { get; } = new List<ExcelExportRequest>();

            public StpBridgeException Error { get; set; }

            public Task<SnapshotV2> GetSnapshotAsync(SnapshotV2 current, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<ApplyPackageSaved> SubmitApplyPackageAsync(ApplyPackage package, CancellationToken cancellationToken) => throw new NotSupportedException();

            public async Task<byte[]> ExportXlsxAsync(ExcelExportRequest request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                if (Error != null) throw Error;
                request.Validate();
                Requests.Add(request);
                return Workbook;
            }
        }
    }
}
