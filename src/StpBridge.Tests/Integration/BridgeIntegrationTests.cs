using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Maptek.Vulcan.Stp.Bridge.Tests.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Bridge.Tests.Integration
{
    /// <summary>
    /// End to end against the real Python bridge. The synthetic project has A100 (2026-03-02 08:00 → 03-04 08:00)
    /// --FinishStart--> B200 (03-04 08:00 → 03-05 08:00).
    /// </summary>
    [Trait("Category", "Integration")]
    public class BridgeIntegrationTests : IClassFixture<BridgeServerFixture>
    {
        private readonly BridgeServerFixture server;

        public BridgeIntegrationTests(BridgeServerFixture server) => this.server = server;

        private async Task<SnapshotV2> Snapshot()
        {
            using (var client = server.CreateClient()) return await client.GetSnapshotAsync();
        }

        /// <summary>Every fragment of every activity; <paramref name="moves"/> shifts whole activities (direct provenance).</summary>
        private static ApplyPackage Package(SnapshotV2 snapshot, Func<string, TimeSpan> moves, string projectFingerprint = null)
        {
            return new ApplyPackage(
                projectFingerprint ?? snapshot.ProjectFingerprint,
                snapshot.PrecedenceFingerprint,
                new DateTime(2026, 3, 1),
                new DateTime(2026, 3, 29),
                snapshot.Activities.Select(activity =>
                {
                    var shift = moves(activity.Id);
                    return new ApplyActivity(activity.Id, activity.Fragments.Select(f => new ApplyFragment(
                        f.Id, f.NativeBarXmlKey, f.NativeBarKey, activity.Id + ":" + PyDateTime.DateIso(f.LogicalUnitDate), f.LogicalUnitDate,
                        shift == TimeSpan.Zero ? ApplyProvenance.Unchanged : ApplyProvenance.Direct,
                        f.Start + shift, f.Finish + shift, f.OriginalStart, f.OriginalFinish, f.QuantityPercentage, f.LockedCompleted)));
                }));
        }

        [PythonFact]
        public async Task HealthIsOk()
        {
            using (var client = server.CreateClient()) await client.CheckHealthAsync();
        }

        [PythonFact]
        public async Task WrongTokenIsForbidden()
        {
            using (var client = server.CreateClient(token: "not-the-token-0123456789"))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.CheckHealthAsync());
                Assert.Equal(StpBridgeErrorKind.Forbidden, error.Kind);
                Assert.Equal(403, error.StatusCode);
            }
        }

        [PythonFact]
        public async Task SnapshotParsesStrictlyAndMatchesTheProject()
        {
            var snapshot = await Snapshot();
            Assert.Equal((string)server.Info["projectKey"], snapshot.ProjectKey);
            Assert.Equal((string)server.Info["projectFingerprint"], snapshot.ProjectFingerprint);
            Assert.Equal((string)server.Info["precedenceFingerprint"], snapshot.PrecedenceFingerprint);
            Assert.Equal((string)server.Info["projectPath"], snapshot.ProjectPath);
            Assert.Equal("Synthetic Ñandú.vgantt", snapshot.ProjectName);
            Assert.Equal(new DateTime(2026, 3, 1, 9, 30, 0), snapshot.ExportedAt);
            Assert.Equal(new[] { "A100", "B200" }, snapshot.Activities.Select(a => a.Id));
            Assert.Equal("Rampa 𝄞 sur", snapshot.Activities[1].Grid["Nombre"]);
            Assert.Equal(3, snapshot.Activities[0].Fragments.Count); // 03-02 08:00 | 03-03 | 03-04 00:00-08:00
            var link = snapshot.Precedences.Single();
            Assert.Equal(("A100", "B200", "FinishStart", 0L), (link.PredecessorId, link.SuccessorId, link.Type, link.LagMicroseconds));

            // The snapshot file the server reads has the same native evidence.
            var file = SnapshotV2.FromJson(WireJson.Parse(File.ReadAllBytes((string)server.Info["snapshotPath"])));
            Assert.Equal(CanonicalJson.ToBytes(file.ToJson()), CanonicalJson.ToBytes(snapshot.ToJson()));
        }

        [PythonFact]
        public async Task AggravatedPrecedenceIsRejectedAsUnsafe()
        {
            var snapshot = await Snapshot();
            using (var client = server.CreateClient())
            {
                var package = Package(snapshot, id => id == "A100" ? TimeSpan.FromDays(1) : TimeSpan.Zero);
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.SubmitApplyPackageAsync(package));
                Assert.Equal(StpBridgeErrorKind.ApplyUnsafe, error.Kind);
                Assert.Equal(422, error.StatusCode);
                Assert.Equal("precedencia", error.Diagnostic.Reason);
                Assert.Contains("B200", error.Diagnostic.Activities);
            }
        }

        [PythonFact]
        public async Task NonCivilShiftIsRejectedAsUnsafe()
        {
            var snapshot = await Snapshot();
            using (var client = server.CreateClient())
            {
                var package = Package(snapshot, id => id == "B200" ? TimeSpan.FromHours(12) : TimeSpan.Zero);
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.SubmitApplyPackageAsync(package));
                Assert.Equal(StpBridgeErrorKind.ApplyUnsafe, error.Kind);
                Assert.Equal("movimiento_civil", error.Diagnostic.Reason);
                Assert.Equal(new[] { "B200" }, error.Diagnostic.Activities);
            }
        }

        [PythonFact]
        public async Task ForeignFingerprintIsStale()
        {
            var snapshot = await Snapshot();
            using (var client = server.CreateClient())
            {
                var package = Package(snapshot, id => TimeSpan.Zero, projectFingerprint: new string('0', 64));
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.SubmitApplyPackageAsync(package));
                Assert.Equal(StpBridgeErrorKind.Stale, error.Kind);
                Assert.Equal("project stale", error.ServerError);
            }
        }

        [PythonFact]
        public async Task RawWireRulesTheClientReliesOn()
        {
            // Malformed package -> 422 "invalid apply package"; "; charset=utf-8" -> 415; an Origin -> 403.
            using (var http = new HttpClient(StpBridgeClient.CreateDefaultHandler()))
            {
                async Task<(int, string)> Post(string body, string contentType, string origin = null)
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, server.Config.Endpoint + "/apply-package");
                    request.Headers.TryAddWithoutValidation(BridgeInfo.TokenHeader, server.Config.Token);
                    request.Headers.ExpectContinue = false;
                    if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
                    request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                    using (var response = await http.SendAsync(request))
                        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
                }

                Assert.Equal((422, "{\"error\":\"invalid apply package\"}"), await Post("{\"schema\":\"vgs-stp-apply/1\"}", "application/json"));
                Assert.Equal(415, (await Post("{}", "application/json; charset=utf-8")).Item1);
                Assert.Equal((403, "{\"error\":\"forbidden\"}"), await Post("{}", "application/json", origin: "http://evil.example"));
            }
        }

        [PythonFact]
        public async Task AcceptedPackageReceiptMatchesTheLocalCanonicalHash()
        {
            var snapshot = await Snapshot();
            var package = Package(snapshot, id => id == "B200" ? TimeSpan.FromDays(1) : TimeSpan.Zero);
            ApplyPackageSaved receipt;
            using (var client = server.CreateClient())
            {
                receipt = await client.SubmitApplyPackageAsync(package);
            }
            Assert.Equal(package.ComputeSha256(), receipt.PackageSha256);
            Assert.Equal(snapshot.ProjectFingerprint, receipt.ProjectFingerprint);

            // What the server committed: byte-identical package + digest-bound ready receipt.
            var outputDirectory = Path.Combine(server.ProjectDirectory, "Respaldos_STPUG-VGS_v2");
            Assert.Equal(package.ToCanonicalBytes(), File.ReadAllBytes(Path.Combine(outputDirectory, "stp_apply_pending_v2.json")));
            var ready = ApplyReadyReceipt.FromJson(WireJson.Parse(File.ReadAllBytes(Path.Combine(outputDirectory, "stp_apply_pending_v2.ready.json"))));
            Assert.Equal(("stp_apply_pending_v2.json", receipt.PackageSha256, receipt.ProjectFingerprint), (ready.PackageFile, ready.PackageSha256, ready.ProjectFingerprint));
        }

        [PythonFact(RequiresModule = "xlsxwriter")]
        public async Task ExportsAWorkbook()
        {
            var request = ExcelExportContractTests.Request();
            request.ProjectKey = server.Config.ProjectKey;
            request.ChartImages.Clear();
            using (var client = server.CreateClient())
            {
                var bytes = await client.ExportXlsxAsync(request);
                Assert.True(bytes.Length > 1000);
            }
        }
    }
}
