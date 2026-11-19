using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge
{
    /// <summary>
    /// Async client for the STP V2 loopback bridge (<c>stp_live_refresh_server_v2.py</c>).
    /// Sends <c>X-STP-V2-Token</c> on every call and never an <c>Origin</c> header; POST bodies are canonical JSON
    /// with <c>Content-Type: application/json</c> exactly (no charset — the server answers 415 otherwise), a
    /// <c>Content-Length</c>, and no <c>Expect: 100-continue</c>. Proxies are bypassed.
    /// Errors surface as <see cref="StpBridgeException"/>; caller cancellation as <see cref="OperationCanceledException"/>.
    /// </summary>
    public sealed class StpBridgeClient : IDisposable
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>The server reads at most 20 MB bodies.</summary>
        public const int MaxBodyBytes = 20 * 1024 * 1024;

        private const int MaxResponseBytes = 64 * 1024 * 1024;
        private readonly HttpClient http;
        private readonly bool disposeHttp;

        public StpBridgeClient(BridgeConfig config, TimeSpan? timeout = null)
            : this(config, CreateDefaultHandler(), true, timeout)
        {
        }

        /// <summary>For tests and custom pipelines. <paramref name="handler"/> is disposed with the client when <paramref name="disposeHandler"/>.</summary>
        public StpBridgeClient(BridgeConfig config, HttpMessageHandler handler, bool disposeHandler, TimeSpan? timeout = null)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            if (!config.IsLive) throw new ArgumentException("the bridge is not live (static mode): " + config.Message, nameof(config));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Timeout = timeout ?? DefaultTimeout;
            if (Timeout <= TimeSpan.Zero && Timeout != System.Threading.Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
            http = new HttpClient(handler, disposeHandler)
            {
                BaseAddress = new Uri(config.Endpoint + "/"),
                Timeout = System.Threading.Timeout.InfiniteTimeSpan, // enforced per call so timeouts are distinguishable from cancellation
                MaxResponseContentBufferSize = MaxResponseBytes,
            };
            disposeHttp = true;
        }

        public BridgeConfig Config { get; }

        /// <summary>Per-call timeout.</summary>
        public TimeSpan Timeout { get; }

        public static HttpClientHandler CreateDefaultHandler() => new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        /// <summary><c>GET /health</c> → <c>{"status":"ok"}</c>. Throws on anything else.</summary>
        public async Task CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            var json = await SendJsonAsync(HttpMethod.Get, "health", null, 200, cancellationToken).ConfigureAwait(false);
            var raw = WireJson.ExactObject(json, "health response", "status");
            if (WireJson.String(raw, "status", "health response") != "ok")
                throw Protocol("unexpected health status", 200);
        }

        /// <summary>
        /// <c>GET /snapshot</c>: the freshly rebuilt snapshot. Throws <see cref="StpBridgeErrorKind.SnapshotUnavailable"/>
        /// when the server answers <c>snapshotComplete:false</c>. When <paramref name="current"/> is given, the result must
        /// name the same project (<c>projectPath</c>, <c>projectKey</c>), as <c>validRefreshedSnapshot</c> requires.
        /// </summary>
        public async Task<SnapshotV2> GetSnapshotAsync(SnapshotV2 current = null, CancellationToken cancellationToken = default)
        {
            var json = await SendJsonAsync(HttpMethod.Get, "snapshot", null, 200, cancellationToken).ConfigureAwait(false);
            SnapshotRefresh refresh;
            try
            {
                refresh = SnapshotRefresh.FromJson(json);
            }
            catch (WireFormatException error)
            {
                throw new StpBridgeException(StpBridgeErrorKind.ProtocolViolation, "invalid snapshot response: " + error.Message, 200, inner: error);
            }
            if (!refresh.SnapshotComplete)
                throw new StpBridgeException(StpBridgeErrorKind.SnapshotUnavailable, "the bridge could not rebuild the snapshot", 200);
            var snapshot = refresh.Snapshot;
            if (snapshot.ProjectKey != Config.ProjectKey
                || (current != null && (snapshot.ProjectPath != current.ProjectPath || snapshot.ProjectKey != current.ProjectKey)))
                throw new StpBridgeException(StpBridgeErrorKind.ProtocolViolation, "the refreshed snapshot names a different project", 200);
            return snapshot;
        }

        /// <summary>
        /// <c>POST /apply-package</c>. On 201 the receipt is checked against the package we sent: schema,
        /// <c>project_fingerprint</c>, and <c>package_sha256</c> == SHA-256 of our canonical bytes.
        /// </summary>
        public async Task<ApplyPackageSaved> SubmitApplyPackageAsync(ApplyPackage package, CancellationToken cancellationToken = default)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            var body = package.ToCanonicalBytes();
            var json = await SendJsonAsync(HttpMethod.Post, "apply-package", body, 201, cancellationToken).ConfigureAwait(false);
            ApplyPackageSaved receipt;
            try
            {
                receipt = ApplyPackageSaved.FromJson(json);
            }
            catch (WireFormatException error)
            {
                throw new StpBridgeException(StpBridgeErrorKind.ProtocolViolation, "invalid apply receipt: " + error.Message, 201, inner: error);
            }
            var expected = CanonicalJson.Sha256Hex(body);
            if (receipt.PackageSha256 != expected || receipt.ProjectFingerprint != package.ProjectFingerprint)
                throw new StpBridgeException(StpBridgeErrorKind.ProtocolViolation, "the apply receipt does not match the package that was sent", 201);
            return receipt;
        }

        /// <summary><c>POST /scenario</c> (legacy <c>vgs-stp-splits/2</c>) → <c>{"status":"saved"}</c>.</summary>
        public async Task SaveScenarioAsync(ScenarioV2 scenario, CancellationToken cancellationToken = default)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            var json = await SendJsonAsync(HttpMethod.Post, "scenario", CanonicalJson.ToBytes(scenario.ToJson()), 201, cancellationToken).ConfigureAwait(false);
            var raw = WireJson.ExactObject(json, "scenario response", "status");
            if (WireJson.String(raw, "status", "scenario response") != "saved") throw Protocol("unexpected scenario status", 201);
        }

        /// <summary><c>POST /export-xlsx</c> → the workbook bytes (<c>STP.xlsx</c>).</summary>
        public async Task<byte[]> ExportXlsxAsync(ExcelExportRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.ProjectKey != Config.ProjectKey) throw new ArgumentException("the export projectKey must be the bridge's projectKey", nameof(request));
            var body = CanonicalJson.ToBytes(request.ToJson());
            using (var response = await SendAsync(HttpMethod.Post, "export-xlsx", body, cancellationToken).ConfigureAwait(false))
            {
                var bytes = await ReadBodyAsync(response).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status != 200) throw MapError(status, bytes);
                var type = response.Content.Headers.ContentType?.MediaType;
                if (type != "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" || bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
                    throw Protocol("the export response is not an xlsx workbook", status);
                return bytes;
            }
        }

        private async Task<JToken> SendJsonAsync(HttpMethod method, string path, byte[] body, int successStatus, CancellationToken cancellationToken)
        {
            using (var response = await SendAsync(method, path, body, cancellationToken).ConfigureAwait(false))
            {
                var bytes = await ReadBodyAsync(response).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status != successStatus) throw MapError(status, bytes);
                if (response.Content.Headers.ContentType?.MediaType != "application/json") throw Protocol("response is not JSON", status);
                try
                {
                    return WireJson.Parse(bytes);
                }
                catch (WireFormatException error)
                {
                    throw new StpBridgeException(StpBridgeErrorKind.ProtocolViolation, "response is not valid JSON", status, inner: error);
                }
            }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] body, CancellationToken cancellationToken)
        {
            if (body != null && body.Length > MaxBodyBytes)
                throw new StpBridgeException(StpBridgeErrorKind.PayloadTooLarge, "request body exceeds the bridge's 20 MB limit");
            cancellationToken.ThrowIfCancellationRequested();
            using (var timeout = new CancellationTokenSource())
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
            {
                if (Timeout != System.Threading.Timeout.InfiniteTimeSpan) timeout.CancelAfter(Timeout);
                var request = new HttpRequestMessage(method, path);
                request.Headers.TryAddWithoutValidation(BridgeInfo.TokenHeader, Config.Token);
                request.Headers.ExpectContinue = false;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body != null)
                {
                    var content = new ByteArrayContent(body);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    content.Headers.ContentLength = body.Length;
                    request.Content = content;
                }
                try
                {
                    var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                    // Buffer the body under the same deadline.
                    try
                    {
                        await response.Content.LoadIntoBufferAsync(MaxResponseBytes).WithCancellation(linked.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        response.Dispose();
                        throw;
                    }
                    return response;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException error) when (timeout.IsCancellationRequested)
                {
                    throw new StpBridgeException(StpBridgeErrorKind.Timeout, $"the bridge did not answer {path} within {Timeout.TotalSeconds:0.#} s", inner: error);
                }
                catch (HttpRequestException error)
                {
                    throw new StpBridgeException(StpBridgeErrorKind.Unreachable, "the bridge is not reachable: " + (error.InnerException?.Message ?? error.Message), inner: error);
                }
                finally
                {
                    request.Dispose();
                }
            }
        }

        private static Task<byte[]> ReadBodyAsync(HttpResponseMessage response) => response.Content.ReadAsByteArrayAsync();

        internal static StpBridgeException MapError(int status, byte[] body)
        {
            BridgeErrorPayload payload = null;
            try
            {
                payload = BridgeErrorPayload.FromJson(WireJson.Parse(body));
            }
            catch (WireFormatException)
            {
            }
            var error = payload?.Error;
            var message = error != null ? $"bridge answered {status} '{error}'" : $"bridge answered {status}";
            StpBridgeErrorKind kind;
            switch (status)
            {
                case 403: kind = StpBridgeErrorKind.Forbidden; break;
                case 409: kind = error == "project unavailable" || error == "snapshot unavailable" ? StpBridgeErrorKind.SnapshotUnavailable : StpBridgeErrorKind.Stale; break;
                case 413: kind = StpBridgeErrorKind.PayloadTooLarge; break;
                case 422: kind = error == "apply package unsafe" ? StpBridgeErrorKind.ApplyUnsafe : StpBridgeErrorKind.InvalidRequest; break;
                case 503: kind = StpBridgeErrorKind.Busy; break;
                default:
                    if (status >= 500) kind = error != null && error.EndsWith("indeterminate", StringComparison.Ordinal) ? StpBridgeErrorKind.SaveIndeterminate : StpBridgeErrorKind.ServerError;
                    else kind = StpBridgeErrorKind.ProtocolViolation;
                    break;
            }
            if (payload == null && kind != StpBridgeErrorKind.ProtocolViolation)
                kind = status >= 500 ? StpBridgeErrorKind.ServerError : StpBridgeErrorKind.ProtocolViolation;
            return new StpBridgeException(kind, message, status, error, payload?.Diagnostic, payload?.RecoveryPath);
        }

        private static StpBridgeException Protocol(string message, int status) => new StpBridgeException(StpBridgeErrorKind.ProtocolViolation, message, status);

        public void Dispose()
        {
            if (disposeHttp) http.Dispose();
        }
    }

    internal static class TaskExtensions
    {
        public static async Task WithCancellation(this Task task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => tcs.TrySetResult(true)))
            {
                if (task != await Task.WhenAny(task, tcs.Task).ConfigureAwait(false)) throw new OperationCanceledException(cancellationToken);
            }
            await task.ConfigureAwait(false);
        }
    }
}
