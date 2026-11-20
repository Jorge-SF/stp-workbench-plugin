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
            Assert.Empty((JArray)json["chartTypes"]);
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
            Assert.Equal(new JObject(), bootstrap["localState"]);

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
