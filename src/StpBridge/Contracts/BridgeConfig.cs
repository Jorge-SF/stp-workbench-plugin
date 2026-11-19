using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Contracts
{
    /// <summary>
    /// The planner's bridge page config (<c>BridgeDescriptor.page_config()</c> + <c>precedenceConflictPolicy</c>):
    /// <c>{mode, endpoint, token, projectKey, bridgeId, message[, precedenceConflictPolicy]}</c>.
    /// Live configs are validated exactly like <c>refresh_client.js</c> <c>validBridgeConfig</c>; a static
    /// descriptor (<c>mode:"static"</c>, empty endpoint/token: the bridge failed to start) is accepted so the plugin
    /// can show its message, but it cannot build a client.
    /// </summary>
    public sealed class BridgeConfig
    {
        public const string BlockInherited = "block-inherited";
        public const string AllowInherited = "allow-inherited";

        private static readonly string[] Fields = { "mode", "endpoint", "token", "projectKey", "bridgeId", "message" };
        private static readonly string[] OptionalFields = { "precedenceConflictPolicy" };
        private static readonly Regex EndpointPattern = new Regex(@"^http://127\.0\.0\.1:(\d{1,5})$", RegexOptions.CultureInvariant);

        public BridgeConfig(string endpoint, string token, string projectKey, string bridgeId, string message, string precedenceConflictPolicy = null)
            : this("live", endpoint, token, projectKey, bridgeId, message, precedenceConflictPolicy)
        {
        }

        private BridgeConfig(string mode, string endpoint, string token, string projectKey, string bridgeId, string message, string policy)
        {
            Mode = mode;
            Endpoint = endpoint ?? "";
            Token = token ?? "";
            ProjectKey = projectKey ?? "";
            BridgeId = bridgeId ?? "";
            Message = message ?? "";
            PrecedenceConflictPolicy = policy;
            if (policy != null && policy != BlockInherited && policy != AllowInherited)
                throw new WireFormatException("unsupported precedence conflict policy");
            if (mode == "live")
            {
                var match = EndpointPattern.Match(Endpoint);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port < 1 || port > 65535)
                    throw new WireFormatException("bridge endpoint must be http://127.0.0.1:<port>");
                Port = port;
                if (Token.Length < 16 || Token.Any(c => c < 0x20 || c == 0x7f)) throw new WireFormatException("bridge token is invalid");
                if (!WireJson.IsLowerHex(ProjectKey, 64)) throw new WireFormatException("bridge projectKey is invalid");
                if (!WireJson.IsLowerHex(BridgeId, 32)) throw new WireFormatException("bridge bridgeId is invalid");
                if (Message.Length < 1 || Message.Length > 240 || Message.Any(c => c == '<' || c == '>' || c == '&' || c < 0x20 || c == 0x7f))
                    throw new WireFormatException("bridge message is invalid");
            }
            else if (mode != "static")
            {
                throw new WireFormatException("bridge mode must be live or static");
            }
        }

        /// <summary><c>live</c> or <c>static</c>.</summary>
        public string Mode { get; }

        public bool IsLive => Mode == "live";

        public string Endpoint { get; }

        /// <summary>Port parsed from <see cref="Endpoint"/> (0 for static).</summary>
        public int Port { get; }

        public string Token { get; }

        public string ProjectKey { get; }

        public string BridgeId { get; }

        /// <summary>User-facing (Spanish) bridge status message.</summary>
        public string Message { get; }

        /// <summary>As supplied, or null when absent.</summary>
        public string PrecedenceConflictPolicy { get; }

        /// <summary>Script 2's default when the field is absent is <c>block-inherited</c>.</summary>
        public string EffectivePrecedenceConflictPolicy => PrecedenceConflictPolicy ?? BlockInherited;

        public static BridgeConfig Parse(string json) => FromJson(WireJson.Parse(json));

        public static BridgeConfig FromJson(JToken token)
        {
            const string L = "bridge config";
            var raw = WireJson.ObjectWithFields(token, L, Fields, OptionalFields);
            string policy = null;
            if (raw["precedenceConflictPolicy"] != null) policy = WireJson.String(raw, "precedenceConflictPolicy", L);
            return new BridgeConfig(
                WireJson.String(raw, "mode", L),
                WireJson.String(raw, "endpoint", L),
                WireJson.String(raw, "token", L),
                WireJson.String(raw, "projectKey", L),
                WireJson.String(raw, "bridgeId", L),
                WireJson.String(raw, "message", L),
                policy);
        }

        public JObject ToJson()
        {
            var value = new JObject
            {
                ["mode"] = Mode,
                ["endpoint"] = Endpoint,
                ["token"] = Token,
                ["projectKey"] = ProjectKey,
                ["bridgeId"] = BridgeId,
                ["message"] = Message,
            };
            if (PrecedenceConflictPolicy != null) value["precedenceConflictPolicy"] = PrecedenceConflictPolicy;
            return value;
        }

        /// <summary>Never print the token.</summary>
        public override string ToString() => $"BridgeConfig({Mode}, {Endpoint}, bridge {BridgeId})";
    }

    /// <summary>Error body of every non-2xx bridge response: <c>{"error":…[, "diagnostic":{…}][, "recoveryPath":…]}</c>.</summary>
    public sealed class BridgeErrorPayload
    {
        private BridgeErrorPayload(string error, UnsafeApplyDiagnostic diagnostic, string recoveryPath)
        {
            Error = error;
            Diagnostic = diagnostic;
            RecoveryPath = recoveryPath;
        }

        /// <summary>Server error text, e.g. <c>forbidden</c>, <c>project stale</c>, <c>apply package unsafe</c>.</summary>
        public string Error { get; }

        public UnsafeApplyDiagnostic Diagnostic { get; }

        /// <summary>Set for "save indeterminate" 500s: where the atomic writer left the recovery copy.</summary>
        public string RecoveryPath { get; }

        public static BridgeErrorPayload FromJson(JToken token)
        {
            const string L = "error response";
            var raw = WireJson.ObjectWithFields(token, L, new[] { "error" }, new[] { "diagnostic", "recoveryPath" });
            var diagnostic = raw["diagnostic"] != null ? UnsafeApplyDiagnostic.FromJson(raw["diagnostic"]) : null;
            var recovery = raw["recoveryPath"] != null ? WireJson.String(raw, "recoveryPath", L) : null;
            return new BridgeErrorPayload(WireJson.String(raw, "error", L), diagnostic, recovery);
        }
    }

    /// <summary>
    /// <c>diagnostic</c> of a 422 <c>apply package unsafe</c>: the first difference's reason
    /// (<c>anclaje_nativo</c>, <c>unidad_logica</c>, <c>solapamiento</c>, <c>precedencia</c>,
    /// <c>fragmento_completado</c>, <c>movimiento_civil</c> or <c>integridad</c>) and up to 5 activity ids.
    /// </summary>
    public sealed class UnsafeApplyDiagnostic
    {
        public static readonly IReadOnlyCollection<string> KnownReasons = new[]
        {
            "anclaje_nativo", "unidad_logica", "solapamiento", "precedencia", "fragmento_completado", "movimiento_civil", "integridad",
        };

        private UnsafeApplyDiagnostic(string reason, IReadOnlyList<string> activities)
        {
            Reason = reason;
            Activities = activities;
        }

        public string Reason { get; }

        public IReadOnlyList<string> Activities { get; }

        public static UnsafeApplyDiagnostic FromJson(JToken token)
        {
            const string L = "diagnostic";
            var raw = WireJson.ExactObject(token, L, "reason", "activities");
            var activities = WireJson.List(raw, "activities", L);
            if (activities.Count > 5 || activities.Any(a => a.Type != JTokenType.String))
                throw new WireFormatException("diagnostic.activities must be at most 5 strings");
            return new UnsafeApplyDiagnostic(WireJson.Text(raw, "reason", L), activities.Select(a => (string)a).ToList().AsReadOnly());
        }
    }
}
