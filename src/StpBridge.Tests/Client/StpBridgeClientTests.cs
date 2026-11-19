using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Maptek.Vulcan.Stp.Bridge.Tests.Contracts;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Maptek.Vulcan.Stp.Bridge.Tests.Client
{
    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, byte[], CancellationToken, Task<HttpResponseMessage>> respond;

        public FakeHandler(Func<HttpRequestMessage, byte[], CancellationToken, Task<HttpResponseMessage>> respond) => this.respond = respond;

        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = new List<(HttpRequestMessage, byte[])>();

        public static HttpResponseMessage Json(int status, string body)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Requests.Add((request, body));
            return await respond(request, body, cancellationToken).ConfigureAwait(false);
        }
    }

    public class StpBridgeClientTests
    {
        private static readonly BridgeConfig Config = BridgeConfig.Parse(BridgeConfigContractTests.Live());

        private static StpBridgeClient Client(FakeHandler handler, TimeSpan? timeout = null) => new StpBridgeClient(Config, handler, true, timeout);

        private static FakeHandler Always(int status, string body) => new FakeHandler((r, b, c) => Task.FromResult(FakeHandler.Json(status, body)));

        private static ApplyPackage Package()
        {
            var path = Directory.GetFiles(TestPaths.GoldenExpected, "*.apply.canonical.json").OrderBy(f => f, StringComparer.Ordinal).First();
            return ApplyPackage.FromJson(WireJson.Parse(File.ReadAllBytes(path)));
        }

        [Fact]
        public async Task SendsTokenAndNoOriginOnGet()
        {
            var handler = Always(200, "{\"status\":\"ok\"}");
            using (var client = Client(handler))
            {
                await client.CheckHealthAsync();
            }
            var (request, body) = handler.Requests.Single();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://127.0.0.1:50123/health", request.RequestUri.ToString());
            Assert.Equal(new[] { "0123456789abcdefXYZ" }, request.Headers.GetValues("X-STP-V2-Token"));
            Assert.False(request.Headers.Contains("Origin"));
            Assert.Null(body);
        }

        [Fact]
        public async Task PostsCanonicalBodyWithExactContentType()
        {
            var package = Package();
            var digest = package.ComputeSha256();
            var handler = Always(201, $"{{\"status\":\"saved\",\"schema\":\"vgs-stp-apply/1\",\"package_sha256\":\"{digest}\",\"project_fingerprint\":\"{package.ProjectFingerprint}\"}}");
            using (var client = Client(handler))
            {
                var receipt = await client.SubmitApplyPackageAsync(package);
                Assert.Equal(digest, receipt.PackageSha256);
            }
            var (request, body) = handler.Requests.Single();
            Assert.Equal("http://127.0.0.1:50123/apply-package", request.RequestUri.ToString());
            Assert.Equal("application/json", request.Content.Headers.GetValues("Content-Type").Single()); // the server answers 415 to "; charset=utf-8"
            Assert.Equal(body.Length, request.Content.Headers.ContentLength);
            Assert.Equal(false, request.Headers.ExpectContinue);
            Assert.False(request.Headers.Contains("Origin"));
            Assert.Equal(package.ToCanonicalBytes(), body);
        }

        [Fact]
        public async Task ReceiptWithAnotherDigestIsAProtocolViolation()
        {
            var package = Package();
            var handler = Always(201, $"{{\"status\":\"saved\",\"schema\":\"vgs-stp-apply/1\",\"package_sha256\":\"{new string('0', 64)}\",\"project_fingerprint\":\"{package.ProjectFingerprint}\"}}");
            using (var client = Client(handler))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.SubmitApplyPackageAsync(package));
                Assert.Equal(StpBridgeErrorKind.ProtocolViolation, error.Kind);
            }
        }

        [Theory]
        [InlineData(403, "{\"error\":\"forbidden\"}", StpBridgeErrorKind.Forbidden)]
        [InlineData(409, "{\"error\":\"project stale\"}", StpBridgeErrorKind.Stale)]
        [InlineData(409, "{\"error\":\"project unavailable\"}", StpBridgeErrorKind.SnapshotUnavailable)]
        [InlineData(422, "{\"error\":\"invalid apply package\"}", StpBridgeErrorKind.InvalidRequest)]
        [InlineData(413, "{\"error\":\"payload too large\"}", StpBridgeErrorKind.PayloadTooLarge)]
        [InlineData(503, "{\"error\":\"server busy\"}", StpBridgeErrorKind.Busy)]
        [InlineData(500, "{\"error\":\"apply package save failed\"}", StpBridgeErrorKind.ServerError)]
        [InlineData(415, "{\"error\":\"unsupported media type\"}", StpBridgeErrorKind.ProtocolViolation)]
        [InlineData(404, "{\"error\":\"not found\"}", StpBridgeErrorKind.ProtocolViolation)]
        [InlineData(409, "<html>proxy</html>", StpBridgeErrorKind.ProtocolViolation)]
        [InlineData(502, "Bad Gateway", StpBridgeErrorKind.ServerError)]
        public async Task MapsServerErrors(int status, string body, StpBridgeErrorKind kind)
        {
            using (var client = Client(Always(status, body)))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.SubmitApplyPackageAsync(Package()));
                Assert.Equal(kind, error.Kind);
                Assert.Equal(status, error.StatusCode);
            }
        }

        [Fact]
        public async Task UnsafePackageCarriesTheDiagnostic()
        {
            using (var client = Client(Always(422, "{\"error\":\"apply package unsafe\",\"diagnostic\":{\"reason\":\"precedencia\",\"activities\":[\"B200\"]}}")))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.SubmitApplyPackageAsync(Package()));
                Assert.Equal(StpBridgeErrorKind.ApplyUnsafe, error.Kind);
                Assert.Equal("apply package unsafe", error.ServerError);
                Assert.Equal("precedencia", error.Diagnostic.Reason);
                Assert.Equal(new[] { "B200" }, error.Diagnostic.Activities);
            }
        }

        [Fact]
        public async Task IndeterminateSaveCarriesTheRecoveryPath()
        {
            using (var client = Client(Always(500, "{\"error\":\"apply package save indeterminate\",\"recoveryPath\":\"C:\\\\p\\\\x.recovery\"}")))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.SubmitApplyPackageAsync(Package()));
                Assert.Equal(StpBridgeErrorKind.SaveIndeterminate, error.Kind);
                Assert.Equal("C:\\p\\x.recovery", error.RecoveryPath);
            }
        }

        [Fact]
        public async Task IncompleteSnapshotIsSnapshotUnavailable()
        {
            using (var client = Client(Always(200, "{\"snapshotComplete\":false,\"applyAllowed\":false}")))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.GetSnapshotAsync());
                Assert.Equal(StpBridgeErrorKind.SnapshotUnavailable, error.Kind);
            }
        }

        [Fact]
        public async Task SnapshotOfAnotherProjectIsRejected()
        {
            var fixture = (JObject)WireJson.Parse(File.ReadAllBytes(Path.Combine(TestPaths.GoldenFixtures, "chain_fs.snapshot.json")));
            fixture["snapshotComplete"] = true;
            fixture["applyAllowed"] = false;
            using (var client = Client(Always(200, CanonicalJson.ToText(fixture))))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.GetSnapshotAsync());
                Assert.Equal(StpBridgeErrorKind.ProtocolViolation, error.Kind);
            }
        }

        [Fact]
        public async Task TimeoutIsDistinctFromCancellation()
        {
            var slow = new FakeHandler(async (r, b, c) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), c).ConfigureAwait(false);
                return FakeHandler.Json(200, "{\"status\":\"ok\"}");
            });
            using (var client = Client(slow, TimeSpan.FromMilliseconds(100)))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.CheckHealthAsync());
                Assert.Equal(StpBridgeErrorKind.Timeout, error.Kind);
            }
            using (var client = Client(slow, TimeSpan.FromSeconds(20)))
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckHealthAsync(cancel.Token));
            }
        }

        [Fact]
        public async Task UnreachableBridge()
        {
            var refused = new FakeHandler((r, b, c) => throw new HttpRequestException("refused", new WebException("connection refused")));
            using (var client = Client(refused))
            {
                var error = await Assert.ThrowsAsync<StpBridgeException>(() => client.CheckHealthAsync());
                Assert.Equal(StpBridgeErrorKind.Unreachable, error.Kind);
            }
        }

        [Fact]
        public async Task ExportReturnsWorkbookBytes()
        {
            var handler = new FakeHandler((r, b, c) =>
            {
                var content = new ByteArrayContent(new byte[] { (byte)'P', (byte)'K', 3, 4, 0 });
                content.Headers.TryAddWithoutValidation("Content-Type", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });
            using (var client = Client(handler))
            {
                var bytes = await client.ExportXlsxAsync(ExcelExportContractTests.Request());
                Assert.Equal((byte)'P', bytes[0]);
            }
            var body = (JObject)WireJson.Parse(handler.Requests.Single().Body);
            Assert.Equal("vgs-stp-excel-export/1", (string)body["schema"]);
            Assert.Equal("http://127.0.0.1:50123/export-xlsx", handler.Requests.Single().Request.RequestUri.ToString());
        }

        [Fact]
        public async Task ExportForAnotherProjectIsRefusedLocally()
        {
            var request = ExcelExportContractTests.Request();
            request.ProjectKey = new string('f', 64);
            using (var client = Client(Always(200, "{}")))
            {
                await Assert.ThrowsAsync<ArgumentException>(() => client.ExportXlsxAsync(request));
            }
        }
    }
}
