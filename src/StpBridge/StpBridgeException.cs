using System;
using Maptek.Vulcan.Stp.Bridge.Contracts;

namespace Maptek.Vulcan.Stp.Bridge
{
    /// <summary>What went wrong talking to the bridge, mapped from the server's status + <c>error</c> text.</summary>
    public enum StpBridgeErrorKind
    {
        /// <summary>Connection refused/reset, DNS, socket errors: the bridge is not running.</summary>
        Unreachable,

        /// <summary>The per-call timeout elapsed.</summary>
        Timeout,

        /// <summary>403 <c>forbidden</c>: wrong token or a disallowed Origin.</summary>
        Forbidden,

        /// <summary>409 <c>project stale</c> / <c>project mismatch</c> / <c>project or snapshot stale</c> /
        /// <c>scenario stale or unsafe</c> / <c>snapshot unavailable</c>: VGS changed since the snapshot; refresh.</summary>
        Stale,

        /// <summary>200 with <c>snapshotComplete:false</c> from <c>/snapshot</c>, or 409 <c>project unavailable</c>.</summary>
        SnapshotUnavailable,

        /// <summary>422 <c>invalid apply package</c> / <c>invalid scenario</c> / <c>invalid Excel export</c>: the body failed parsing.</summary>
        InvalidRequest,

        /// <summary>422 <c>apply package unsafe</c>: parsed but failed the safety validation; see <see cref="StpBridgeException.Diagnostic"/>.</summary>
        ApplyUnsafe,

        /// <summary>413 <c>payload too large</c> (20 MB).</summary>
        PayloadTooLarge,

        /// <summary>503 <c>server busy</c> (16 concurrent requests).</summary>
        Busy,

        /// <summary>500 <c>… save indeterminate</c>: the write may or may not have committed; see <see cref="StpBridgeException.RecoveryPath"/>.</summary>
        SaveIndeterminate,

        /// <summary>Other 5xx (<c>… save failed</c>, <c>request failed</c>, xlsxwriter missing).</summary>
        ServerError,

        /// <summary>400/404/405/415 or any response that breaks the wire contract (including a receipt whose digest
        /// does not match the package we sent).</summary>
        ProtocolViolation,
    }

    public sealed class StpBridgeException : Exception
    {
        public StpBridgeException(StpBridgeErrorKind kind, string message, int? statusCode = null, string serverError = null,
            UnsafeApplyDiagnostic diagnostic = null, string recoveryPath = null, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
            StatusCode = statusCode;
            ServerError = serverError;
            Diagnostic = diagnostic;
            RecoveryPath = recoveryPath;
        }

        public StpBridgeErrorKind Kind { get; }

        /// <summary>HTTP status, when a response was received.</summary>
        public int? StatusCode { get; }

        /// <summary>The server's <c>error</c> text, when present.</summary>
        public string ServerError { get; }

        public UnsafeApplyDiagnostic Diagnostic { get; }

        public string RecoveryPath { get; }
    }
}
