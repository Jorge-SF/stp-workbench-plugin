using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Bridge.Tests.Contracts
{
    public class ApplyPackageContractTests
    {
        public static IEnumerable<object[]> PythonPackageVectors()
            => ((JArray)TestPaths.PythonVectors["applyPackages"]).Select(v => new object[] { (string)v["name"] });

        /// <summary>Same accept/reject decision and the same normalized canonical bytes as load_apply_package → apply_package_to_json_value.</summary>
        [Theory]
        [MemberData(nameof(PythonPackageVectors))]
        public void MatchesPythonLoadApplyPackage(string name)
        {
            var vector = ((JArray)TestPaths.PythonVectors["applyPackages"]).Single(v => (string)v["name"] == name);
            var input = (string)vector["input"];
            var expected = (string)vector["canonical"];
            if (expected == null)
            {
                Assert.Throws<WireFormatException>(() => ApplyPackage.Parse(input));
                return;
            }
            var package = ApplyPackage.Parse(input);
            Assert.Equal(expected, Encoding.UTF8.GetString(package.ToCanonicalBytes()));
            Assert.Equal((string)vector["sha256"], package.ComputeSha256());
        }

        [Fact]
        public void BuildsTheSamePackageInCode()
        {
            var fragment = new ApplyFragment(
                "A:x:1:0", "x", "1", "A:2026-03-02", new DateTime(2026, 3, 2), ApplyProvenance.Direct,
                new DateTime(2026, 3, 3, 8, 0, 0), new DateTime(2026, 3, 4), new DateTime(2026, 3, 2, 8, 0, 0), new DateTime(2026, 3, 3),
                PyDecimal.Parse("4E+1"), false);
            var package = new ApplyPackage(new string('a', 64), new string('b', 64), new DateTime(2026, 3, 1), new DateTime(2026, 3, 29), new[] { new ApplyActivity("A", new[] { fragment }) });
            Assert.Equal(
                "{\"activities\":[{\"activity_id\":\"A\",\"fragments\":[{\"finish\":\"2026-03-04T00:00:00\",\"id\":\"A:x:1:0\",\"locked_completed\":false,"
                + "\"logical_unit_date\":\"2026-03-02\",\"logical_unit_id\":\"A:2026-03-02\",\"native_bar_key\":\"1\",\"native_bar_xml_key\":\"x\","
                + "\"original_finish\":\"2026-03-03T00:00:00\",\"original_start\":\"2026-03-02T08:00:00\",\"provenance\":\"direct\","
                + "\"quantity_percentage\":\"40\",\"start\":\"2026-03-03T08:00:00\"}]}],"
                + "\"horizon_finish\":\"2026-03-29T00:00:00\",\"horizon_start\":\"2026-03-01T00:00:00\",\"precedence_fingerprint\":\"" + new string('b', 64)
                + "\",\"project_fingerprint\":\"" + new string('a', 64) + "\",\"schema\":\"vgs-stp-apply/1\"}",
                Encoding.UTF8.GetString(package.ToCanonicalBytes()));
        }

        [Fact]
        public void RejectsSubMicrosecondTimes()
        {
            var fragment = new ApplyFragment("f", "x", "1", "A:2026-03-02", new DateTime(2026, 3, 2), ApplyProvenance.Unchanged,
                new DateTime(2026, 3, 2).AddTicks(1), new DateTime(2026, 3, 3), new DateTime(2026, 3, 2), new DateTime(2026, 3, 3), PyDecimal.Parse("1"), false);
            var package = new ApplyPackage("p", "q", new DateTime(2026, 3, 1), new DateTime(2026, 3, 2), new[] { new ApplyActivity("A", new[] { fragment }) });
            Assert.Throws<WireFormatException>(() => package.ToCanonicalBytes());
        }
    }

    public class ScenarioContractTests
    {
        private const string Valid = "{\"schema\":\"vgs-stp-splits/2\",\"project_fingerprint\":\"p\",\"precedence_fingerprint\":\"q\",\"horizon_start\":\"2026-03-01T00:00:00\",\"horizon_finish\":\"2026-03-29T00:00\","
            + "\"activities\":[{\"activity_id\":\"A\",\"provenance\":\"direct\",\"fragments\":[{\"id\":\"f1\",\"native_bar_xml_key\":\"x\",\"native_bar_key\":\"1\",\"start\":\"2026-03-03T08:00:00\",\"finish\":\"2026-03-04T00:00:00\","
            + "\"original_start\":\"2026-03-02T08:00:00\",\"original_finish\":\"2026-03-03T00:00:00\",\"quantity_percentage\":\"4E+1\",\"locked_completed\":false}]}]}";

        [Fact]
        public void ParsesAndSerializesLikeScenarioV2ToJsonValue()
        {
            var scenario = ScenarioV2.Parse(Valid);
            var json = scenario.ToJson();
            Assert.Equal("2026-03-29T00:00:00", (string)json["horizon_finish"]);
            Assert.Equal("4E+1", (string)json["activities"][0]["fragments"][0]["quantity_percentage"]); // str(Decimal), not 'f'
        }

        [Theory]
        [InlineData("\"horizon_start\":\"2026-03-01T00:00:00\"", "\"horizon_start\":\"2026-03-01\"")] // _required_datetime needs a 'T'
        [InlineData("\"provenance\":\"direct\"", "\"provenance\":\"unchanged\"")]
        [InlineData("\"activities\":[{", "\"activities\":[],\"x\":[{")]
        [InlineData("\"horizon_finish\":\"2026-03-29T00:00\"", "\"horizon_finish\":\"2027-03-29T00:00\"")] // > 366 days
        [InlineData("\"start\":\"2026-03-03T08:00:00\"", "\"start\":\"2026-03-05T08:00:00\"")] // start >= finish
        public void RejectsWhatPythonRejects(string from, string to)
        {
            Assert.Throws<WireFormatException>(() => ScenarioV2.Parse(Valid.Replace(from, to)));
        }
    }

    public class SnapshotContractTests
    {
        /// <summary>
        /// Hand-written fixtures that deliberately are NOT Python wire output: they exercise the JS/C# engine's tolerance
        /// (HH:MM times, millisecond fractions, a non-schema <c>lagSeconds</c>, precedence type aliases). Python's
        /// <c>canonicalize_snapshot</c> rejects them, so the strict reader must too.
        /// </summary>
        internal static readonly string[] EngineToleranceFixtures = { "engine_edges.snapshot.json" };

        public static IEnumerable<object[]> Fixtures()
            => Directory.GetFiles(TestPaths.GoldenFixtures, "*.snapshot.json").Select(Path.GetFileName)
                .Where(f => !EngineToleranceFixtures.Contains(f)).Select(f => new object[] { f });

        [Fact]
        public void EveryFixtureIsStrictWireDataUnlessDeclaredEngineOnly()
        {
            var all = Directory.GetFiles(TestPaths.GoldenFixtures, "*.snapshot.json").Select(Path.GetFileName).ToList();
            Assert.Subset(new HashSet<string>(all), new HashSet<string>(EngineToleranceFixtures));
            foreach (var file in all)
            {
                var raw = WireJson.Parse(File.ReadAllBytes(Path.Combine(TestPaths.GoldenFixtures, file)));
                if (EngineToleranceFixtures.Contains(file))
                    Assert.Throws<WireFormatException>(() => SnapshotV2.FromJson(raw)); // if it becomes canonical, drop it from the list
                else
                    SnapshotV2.FromJson(raw);
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void GoldenSnapshotsRoundTripExactly(string fileName)
        {
            var raw = WireJson.Parse(File.ReadAllBytes(Path.Combine(TestPaths.GoldenFixtures, fileName)));
            var snapshot = SnapshotV2.FromJson(raw);
            Assert.NotEmpty(snapshot.Activities);
            Assert.Equal(CanonicalJson.ToBytes(raw), CanonicalJson.ToBytes(snapshot.ToJson()));
        }

        private static JObject Fixture() => (JObject)WireJson.Parse(File.ReadAllBytes(Path.Combine(TestPaths.GoldenFixtures, "native_splits.snapshot.json")));

        [Fact]
        public void ExposesTypedFields()
        {
            var snapshot = SnapshotV2.FromJson(Fixture());
            var activity = snapshot.Activities[0];
            Assert.Equal(activity.Id, activity.Grid.First(c => SnapshotActivity.IdColumns.Contains(c.Key)).Value);
            Assert.All(activity.Fragments, f => Assert.True(f.Start < f.Finish));
            Assert.Equal(DateTimeKind.Unspecified, snapshot.ExportedAt.Kind);
        }

        [Fact]
        public void RejectsUnknownMissingAndRetypedFields()
        {
            var extra = Fixture();
            extra["snapshotComplete"] = true;
            Assert.Throws<WireFormatException>(() => SnapshotV2.FromJson(extra));

            var missing = Fixture();
            ((JObject)missing["activities"][0]["fragments"][0]).Remove("lockedCompleted");
            Assert.Throws<WireFormatException>(() => SnapshotV2.FromJson(missing));

            var retyped = Fixture();
            retyped["activities"][0]["fragments"][0]["displaySuppressed"] = "false";
            Assert.Throws<WireFormatException>(() => SnapshotV2.FromJson(retyped));

            var numeric = Fixture();
            numeric["activities"][0]["nativeBars"][0]["quantityPercentage"] = 40;
            Assert.Throws<WireFormatException>(() => SnapshotV2.FromJson(numeric));
        }

        [Fact]
        public void RejectsNonCanonicalSpellingsLikeCanonicalizeSnapshot()
        {
            var time = Fixture();
            var start = (string)time["activities"][0]["nativeBars"][0]["start"];
            time["activities"][0]["nativeBars"][0]["start"] = start.Substring(0, 16); // drops ":SS"
            Assert.Throws<WireFormatException>(() => SnapshotV2.FromJson(time));

            var dec = Fixture();
            dec["activities"][0]["overallPercentComplete"] = "0.0E+1";
            Assert.Throws<WireFormatException>(() => SnapshotV2.FromJson(dec));
        }

        [Fact]
        public void RefreshResponseShapes()
        {
            var complete = Fixture();
            complete["snapshotComplete"] = true;
            complete["applyAllowed"] = false;
            Assert.True(SnapshotRefresh.FromJson(complete).SnapshotComplete);

            Assert.False(SnapshotRefresh.FromJson(WireJson.Parse("{\"snapshotComplete\":false,\"applyAllowed\":false}")).SnapshotComplete);

            var allowed = (JObject)complete.DeepClone();
            allowed["applyAllowed"] = true;
            Assert.Throws<WireFormatException>(() => SnapshotRefresh.FromJson(allowed));
            Assert.Throws<WireFormatException>(() => SnapshotRefresh.FromJson(Fixture()));
            Assert.Throws<WireFormatException>(() => SnapshotRefresh.FromJson(WireJson.Parse("{\"snapshotComplete\":false,\"applyAllowed\":true}")));
        }
    }

    public class ReceiptContractTests
    {
        private static readonly string Hash = new string('c', 64);

        [Fact]
        public void ApplyReceiptAndReadyFile()
        {
            var saved = ApplyPackageSaved.FromJson(WireJson.Parse($"{{\"status\":\"saved\",\"schema\":\"vgs-stp-apply/1\",\"package_sha256\":\"{Hash}\",\"project_fingerprint\":\"p\"}}"));
            Assert.Equal(Hash, saved.PackageSha256);
            Assert.Throws<WireFormatException>(() => ApplyPackageSaved.FromJson(WireJson.Parse($"{{\"status\":\"saved\",\"schema\":\"vgs-stp-apply/1\",\"package_sha256\":\"{Hash.ToUpperInvariant()}\",\"project_fingerprint\":\"p\"}}")));

            var ready = ApplyReadyReceipt.Parse($"{{\"package_file\":\"stp_apply_pending_v2.json\",\"package_sha256\":\"{Hash}\",\"project_fingerprint\":\"p\",\"schema\":\"vgs-stp-apply-ready/1\"}}");
            Assert.Equal("stp_apply_pending_v2.json", ready.PackageFile);
            Assert.Throws<WireFormatException>(() => ApplyReadyReceipt.Parse($"{{\"package_file\":\"f\",\"package_sha256\":\"{Hash}\",\"project_fingerprint\":\"p\",\"schema\":\"vgs-stp-apply-ready/2\"}}"));
        }

        private static string Pending(string cost = "{\"by_activity\":{\"A\":2,\"B\":1},\"move_count\":2,\"split_count\":1,\"total\":3}", string ids = "[\"A\",\"B\"]")
            => "{\"authorized_ids\":" + ids + $",\"backup_path\":\"C:\\\\p\\\\Back up STP\\\\x_STP_V2_20260301_093000.vgantt\",\"backup_sha256\":\"{Hash}\",\"native_operation_cost\":" + cost
               + $",\"package_sha256\":\"{Hash}\",\"precedence_fingerprint\":\"q\",\"project_fingerprint\":\"p\",\"project_name\":\"x.vgantt\",\"schema\":\"vgs-stp-pending-save/1\"}}";

        [Fact]
        public void PendingSaveRoundTripsCanonically()
        {
            var text = Pending();
            var receipt = PendingSaveReceipt.Parse(text);
            Assert.Equal(3, receipt.NativeOperationCost.Total);
            Assert.Equal(text, CanonicalJson.ToText(receipt.ToJson()));
        }

        [Theory]
        [InlineData("{\"by_activity\":{\"A\":2},\"move_count\":2,\"split_count\":1,\"total\":3}", "[\"A\"]")] // does not add up
        [InlineData("{\"by_activity\":{\"A\":3},\"move_count\":2,\"split_count\":1,\"total\":4}", "[\"A\"]")] // total != split+move
        [InlineData("{\"by_activity\":{\"A\":true},\"move_count\":0,\"split_count\":1,\"total\":1}", "[\"A\"]")] // bool is not an int here
        [InlineData("{\"by_activity\":{\"A\":3},\"move_count\":2,\"split_count\":1,\"total\":3}", "[]")] // no ids
        [InlineData("{\"by_activity\":{\"A\":3},\"move_count\":2,\"split_count\":1,\"total\":3}", "[\"A\",\"A\"]")] // duplicate ids
        public void PendingSaveRejects(string cost, string ids)
        {
            Assert.Throws<WireFormatException>(() => PendingSaveReceipt.Parse(Pending(cost, ids)));
        }
    }

    public class BridgeConfigContractTests
    {
        internal static readonly string Key = new string('a', 64);
        internal static readonly string Id = new string('b', 32);

        internal static string Live(string extra = "") =>
            $"{{\"mode\":\"live\",\"endpoint\":\"http://127.0.0.1:50123\",\"token\":\"0123456789abcdefXYZ\",\"projectKey\":\"{Key}\",\"bridgeId\":\"{Id}\",\"message\":\"Actualización en vivo disponible.\"{extra}}}";

        [Fact]
        public void ParsesLiveConfig()
        {
            var config = BridgeConfig.Parse(Live(",\"precedenceConflictPolicy\":\"allow-inherited\""));
            Assert.True(config.IsLive);
            Assert.Equal(50123, config.Port);
            Assert.Equal("allow-inherited", config.EffectivePrecedenceConflictPolicy);
            Assert.Equal(BridgeConfig.BlockInherited, BridgeConfig.Parse(Live()).EffectivePrecedenceConflictPolicy);
            Assert.DoesNotContain("0123456789abcdef", config.ToString());
        }

        [Fact]
        public void ParsesStaticDescriptor()
        {
            var config = BridgeConfig.Parse($"{{\"mode\":\"static\",\"endpoint\":\"\",\"token\":\"\",\"projectKey\":\"{Key}\",\"bridgeId\":\"\",\"message\":\"No se pudo iniciar la actualización en vivo; se usará el modo estático.\"}}");
            Assert.False(config.IsLive);
            Assert.Throws<ArgumentException>(() => new StpBridgeClient(config));
        }

        [Theory]
        [InlineData("http://127.0.0.1:50123", "http://localhost:50123")]
        [InlineData("http://127.0.0.1:50123", "http://127.0.0.1:70000")]
        [InlineData("http://127.0.0.1:50123", "https://127.0.0.1:50123")]
        [InlineData("http://127.0.0.1:50123", "http://127.0.0.1:50123/")]
        [InlineData("0123456789abcdefXYZ", "short")]
        [InlineData("\"live\"", "\"remote\"")]
        [InlineData("disponible.", "<b>disponible</b>")]
        [InlineData("disponible.\"", "disponible.\",\"extra\":1")]
        public void RejectsInvalidLiveConfig(string from, string to)
        {
            Assert.Throws<WireFormatException>(() => BridgeConfig.Parse(Live().Replace(from, to)));
        }

        [Fact]
        public void RejectsUnknownPolicy()
        {
            Assert.Throws<WireFormatException>(() => BridgeConfig.Parse(Live(",\"precedenceConflictPolicy\":\"ignore\"")));
        }
    }

    public class PluginSessionTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "stp-bridge-tests-" + Guid.NewGuid().ToString("N"));

        public PluginSessionTests() => Directory.CreateDirectory(directory);

        public void Dispose() => Directory.Delete(directory, true);

        private string WriteSession(string snapshotPath, string config)
        {
            var path = Path.Combine(directory, PluginSession.FileName);
            var session = new JObject { ["snapshot_path"] = snapshotPath, ["bridge_config"] = WireJson.Parse(config) };
            File.WriteAllBytes(path, CanonicalJson.ToBytes(session));
            return path;
        }

        [Fact]
        public void ReadsSessionAndSnapshot()
        {
            var fixture = (JObject)WireJson.Parse(File.ReadAllBytes(Path.Combine(TestPaths.GoldenFixtures, "chain_fs.snapshot.json")));
            var snapshotPath = Path.Combine(directory, "snapshot_v2.json");
            File.WriteAllBytes(snapshotPath, CanonicalJson.ToBytes(fixture));
            WriteSession(snapshotPath, BridgeConfigContractTests.Live().Replace(BridgeConfigContractTests.Key, (string)fixture["projectKey"]));

            var session = PluginSession.Read(directory);
            Assert.Equal(snapshotPath, session.SnapshotPath);
            Assert.Equal((string)fixture["projectKey"], session.LoadSnapshot().ProjectKey);
        }

        [Fact]
        public void RejectsSnapshotOfAnotherProject()
        {
            var snapshotPath = Path.Combine(directory, "snapshot_v2.json");
            File.Copy(Path.Combine(TestPaths.GoldenFixtures, "chain_fs.snapshot.json"), snapshotPath);
            var session = PluginSession.Read(WriteSession(snapshotPath, BridgeConfigContractTests.Live()));
            Assert.Throws<WireFormatException>(() => session.LoadSnapshot());
        }

        [Fact]
        public void RejectsBadShapes()
        {
            Assert.Throws<WireFormatException>(() => PluginSession.Read(WriteSession("snapshot_v2.json", BridgeConfigContractTests.Live())));
            var path = Path.Combine(directory, PluginSession.FileName);
            File.WriteAllText(path, "{\"snapshot_path\":\"C:\\\\x.json\",\"bridge_config\":{},\"extra\":1}");
            Assert.Throws<WireFormatException>(() => PluginSession.Read(path));
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' });
            Assert.Throws<WireFormatException>(() => PluginSession.Read(path));
        }
    }

    public class ExcelExportContractTests
    {
        internal static ExcelExportRequest Request()
        {
            var request = new ExcelExportRequest { ProjectKey = BridgeConfigContractTests.Key, Title = "Plan", RangeLabel = "1–2 mar" };
            request.Days.Add(new ExcelExportDay("2026-03-01", "dom 1"));
            request.Days.Add(new ExcelExportDay("2026-03-02", "lun 2"));
            request.Activities.Add(new ExcelExportActivity { Id = "A100", Name = "Galería", Type = "Desarrollo", Resource = "J1", Planned = new List<double?> { 1.5, null }, Actual = new List<double?> { null, null }, Color = "#68BD79" });
            request.ChartTypes.Add(new ExcelExportChartType { Type = "Desarrollo", Planned = new List<double?> { 1.5, 0 }, ActualByResource = new Dictionary<string, IList<double?>> { ["J1"] = new List<double?> { 0, 0 } } });
            request.ChartImages.Add(new ExcelExportChartImage("Gráfica", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }));
            return request;
        }

        [Fact]
        public void SerializesTheExactRootFields()
        {
            var json = Request().ToJson();
            Assert.Equal(
                new[] { "activities", "actualProgressMode", "aggregates", "chartImages", "chartTypes", "days", "legend", "projectKey", "rangeLabel", "schema", "title" },
                json.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.StartsWith("data:image/png;base64,iVBORw0KGgo", (string)json["chartImages"][0]["pngDataUrl"]);
            Assert.Contains("[1.5,null]", CanonicalJson.ToText(json));
        }

        [Fact]
        public void ValidatesLikeThePythonExporter()
        {
            var shortSeries = Request();
            shortSeries.Activities[0].Planned = new List<double?> { 1 };
            Assert.Throws<WireFormatException>(() => shortSeries.Validate());

            var badDate = Request();
            badDate.Days.Add(new ExcelExportDay("2026-3-3", "x"));
            Assert.Throws<WireFormatException>(() => badDate.Validate());

            var notPng = Request();
            notPng.ChartImages.Add(new ExcelExportChartImage("x", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.Throws<WireFormatException>(() => notPng.Validate());

            var tooManyDays = Request();
            for (var i = 0; i < 30; i++) tooManyDays.Days.Add(new ExcelExportDay("2026-03-03", "x"));
            Assert.Throws<WireFormatException>(() => tooManyDays.Validate());

            var nan = Request();
            nan.ChartTypes[0].Planned = new List<double?> { double.NaN, 0 };
            Assert.Throws<WireFormatException>(() => nan.Validate());
        }
    }
}
