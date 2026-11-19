using System;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>
    /// A rule violation raised by the planner engine.
    /// </summary>
    /// <remarks>
    /// JS: <c>fail(message, code)</c>, planner_logic.js L10. <see cref="Exception.Message"/> carries the exact Spanish
    /// text of the JS error and <see cref="Code"/> the optional <c>error.code</c> (empty when JS sets none).
    /// </remarks>
    [Serializable]
    public sealed class EngineException : Exception
    {
        /// <summary><c>error.code</c> for a civil date outside years 0001-9999.</summary>
        public const string CivilRange = "CIVIL_RANGE";

        /// <summary><c>error.code</c> for a move that leaves the visible horizon.</summary>
        public const string HorizonRange = "HORIZON_RANGE";

        /// <summary><c>error.code</c> for a malformed <c>vgs-stp-snapshot/2</c>.</summary>
        public const string SnapshotInvalid = "SNAPSHOT_INVALID";

        /// <summary>Initializes a new instance of the <see cref="EngineException"/> class.</summary>
        /// <param name="message">The Spanish error text, identical to the JS message.</param>
        /// <param name="code">The JS <c>error.code</c>, or empty.</param>
        public EngineException(string message, string code = "")
            : base(message)
        {
            Code = code ?? string.Empty;
        }

        /// <summary>Gets the JS <c>error.code</c>: <c>CIVIL_RANGE</c>, <c>HORIZON_RANGE</c>, <c>SNAPSHOT_INVALID</c> or empty.</summary>
        public string Code { get; }

        internal static EngineException Fail(string message, string code = "") => new EngineException(message, code);
    }
}
