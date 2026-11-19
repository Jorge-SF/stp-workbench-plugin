using System;
using System.Collections.Generic;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>A native VGS bar of an activity (snapshot <c>activities[].nativeBars[]</c>). Only <see cref="Key"/> drives rules.</summary>
    public sealed class NativeBar
    {
        /// <summary>Gets the bar key (unique per activity).</summary>
        public string Key { get; init; }

        /// <summary>Gets the XML key of the bar.</summary>
        public string XmlKey { get; init; }

        /// <summary>Gets the native start.</summary>
        public string Start { get; init; }

        /// <summary>Gets the native finish.</summary>
        public string Finish { get; init; }

        /// <summary>Gets the quantity percentage text.</summary>
        public string QuantityPercentage { get; init; }

        /// <summary>Gets the percent-complete text.</summary>
        public string PercentComplete { get; init; }

        /// <summary>Gets the completed worked minutes text.</summary>
        public string CompleteWorkedMinutes { get; init; }
    }

    /// <summary>An activity of the planner model: <c>{ id, nativeBars }</c> (JS <c>buildPlannerModel</c>, L165-168).</summary>
    public sealed class PlannerActivity
    {
        /// <summary>Gets the activity id.</summary>
        public string Id { get; init; }

        /// <summary>Gets the native bar catalogue, or <c>null</c> when absent (allowed only outside the strict browser preflight).</summary>
        public IReadOnlyList<NativeBar> NativeBars { get; init; }
    }

    /// <summary>
    /// A civil-day fragment of a native bar. <see cref="Start"/>/<see cref="Finish"/> are the planned position,
    /// the <c>Original*</c> pair the native VGS position. JS: fragment objects of <c>buildPlannerModel</c>, L170-181.
    /// </summary>
    public sealed class PlannerFragment
    {
        /// <summary>Gets the fragment id (unique in the model).</summary>
        public string Id { get; init; }

        /// <summary>Gets the owning activity id.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets the native bar key.</summary>
        public string NativeBarKey { get; init; }

        /// <summary>Gets the native bar XML key.</summary>
        public string NativeBarXmlKey { get; init; }

        /// <summary>Gets the planned start.</summary>
        public string Start { get; init; }

        /// <summary>Gets the planned finish.</summary>
        public string Finish { get; init; }

        /// <summary>Gets the native start.</summary>
        public string OriginalStart { get; init; }

        /// <summary>Gets the native finish.</summary>
        public string OriginalFinish { get; init; }

        /// <summary>Gets the logical-unit date (<c>YYYY-MM-DD</c>), or <c>null</c>/empty to group by the original start date.</summary>
        public string LogicalUnitDate { get; init; }

        /// <summary>Gets the display-suppressed flag, or <c>null</c> when absent.</summary>
        public bool? DisplaySuppressed { get; init; }

        /// <summary>Gets the quantity percentage text.</summary>
        public string QuantityPercentage { get; init; }

        /// <summary>Gets a value indicating whether this fragment is completed work (never moves).</summary>
        public bool LockedCompleted { get; init; }

        /// <summary>Gets the provenance: empty, <c>direct</c> or <c>cascade</c>.</summary>
        public string Provenance { get; init; } = string.Empty;

        /// <summary>Gets the native anchor before this fragment (civil text) or empty.</summary>
        public string AnchorBefore { get; init; } = string.Empty;

        /// <summary>Gets the native anchor after this fragment (civil text) or empty.</summary>
        public string AnchorAfter { get; init; } = string.Empty;

        /// <summary>Gets the optional native split order.</summary>
        public int? SplitOrder { get; init; }
    }

    /// <summary>
    /// A precedence link. Lag is supplied as <see cref="LagMicroseconds"/> (integer text) and/or <see cref="LagSeconds"/>;
    /// after validation only <see cref="LagMicroseconds"/> remains. JS: <c>normalizeLag</c>, L419-437.
    /// </summary>
    public sealed class PrecedenceLink
    {
        /// <summary>Gets the link key.</summary>
        public string Key { get; init; }

        /// <summary>Gets the predecessor activity id.</summary>
        public string PredecessorId { get; init; }

        /// <summary>Gets the successor activity id.</summary>
        public string SuccessorId { get; init; }

        /// <summary>Gets the predecessor native bar key.</summary>
        public string PredecessorBar { get; init; }

        /// <summary>Gets the successor native bar key.</summary>
        public string SuccessorBar { get; init; }

        /// <summary>Gets the type: FinishStart/StartStart/FinishFinish/StartFinish (or FS/SS/FF/SF, any case, '-', '_' or spaces).</summary>
        public string Type { get; init; }

        /// <summary>Gets the lag in microseconds as signed integer text, or <c>null</c>.</summary>
        public string LagMicroseconds { get; init; }

        /// <summary>Gets the lag in whole seconds (alias input), or <c>null</c>.</summary>
        public long? LagSeconds { get; init; }

        /// <summary>Gets the group key (carried, not used by rules).</summary>
        public string GroupKey { get; init; }
    }

    /// <summary>The planner model: activities, precedences and civil-day fragments.</summary>
    public sealed class PlannerModel
    {
        /// <summary>Gets the activities.</summary>
        public IReadOnlyList<PlannerActivity> Activities { get; init; } = Array.Empty<PlannerActivity>();

        /// <summary>Gets the precedence links.</summary>
        public IReadOnlyList<PrecedenceLink> Precedences { get; init; } = Array.Empty<PrecedenceLink>();

        /// <summary>Gets the fragments.</summary>
        public IReadOnlyList<PlannerFragment> Fragments { get; init; } = Array.Empty<PlannerFragment>();
    }

    /// <summary>A native anchor between two bars (snapshot <c>activities[].nativeAnchors[]</c>).</summary>
    public sealed class NativeAnchor
    {
        /// <summary>Gets the left bar XML key.</summary>
        public string LeftBarXmlKey { get; init; }

        /// <summary>Gets the left bar key.</summary>
        public string LeftBarKey { get; init; }

        /// <summary>Gets the right bar XML key.</summary>
        public string RightBarXmlKey { get; init; }

        /// <summary>Gets the right bar key.</summary>
        public string RightBarKey { get; init; }

        /// <summary>Gets the anchor time at the left bar's finish.</summary>
        public string At { get; init; }

        /// <summary>Gets the anchor time at the right bar's start.</summary>
        public string RightAt { get; init; }
    }

    /// <summary>A snapshot activity: the parts of <c>vgs-stp-snapshot/2</c> that <see cref="PlannerLogic.BuildPlannerModel"/> reads.</summary>
    public sealed class SnapshotActivity
    {
        /// <summary>Gets the activity id.</summary>
        public string Id { get; init; }

        /// <summary>Gets the native bars.</summary>
        public IReadOnlyList<NativeBar> NativeBars { get; init; }

        /// <summary>Gets the fragments (their <c>ActivityId</c>, <c>Provenance</c> and anchors are recomputed).</summary>
        public IReadOnlyList<PlannerFragment> Fragments { get; init; }

        /// <summary>Gets the native anchors.</summary>
        public IReadOnlyList<NativeAnchor> NativeAnchors { get; init; }
    }

    /// <summary>The engine view of a <c>vgs-stp-snapshot/2</c> document.</summary>
    public sealed class PlannerSnapshot
    {
        /// <summary>Gets the activities.</summary>
        public IReadOnlyList<SnapshotActivity> Activities { get; init; }

        /// <summary>Gets the precedences.</summary>
        public IReadOnlyList<PrecedenceLink> Precedences { get; init; }
    }

    /// <summary>
    /// A civil horizon: midnight to midnight, finish exclusive. <see cref="DurationDays"/> is optional on input
    /// (and ignored: it is recomputed) and always set on output. JS: <c>validateCivilHorizon</c>, L96-114.
    /// </summary>
    public sealed class CivilHorizon
    {
        /// <summary>Gets the inclusive start (<c>YYYY-MM-DDT00:00:00</c>).</summary>
        public string HorizonStart { get; init; }

        /// <summary>Gets the exclusive finish (<c>YYYY-MM-DDT00:00:00</c>).</summary>
        public string HorizonFinish { get; init; }

        /// <summary>Gets the length in days (1-366).</summary>
        public int? DurationDays { get; init; }
    }

    /// <summary>Identity of the project a package is built for.</summary>
    public sealed class ProjectSource
    {
        /// <summary>Gets the project fingerprint.</summary>
        public string ProjectFingerprint { get; init; }

        /// <summary>Gets the precedence fingerprint.</summary>
        public string PrecedenceFingerprint { get; init; }
    }

    /// <summary>A daily-tail move request. JS: <c>validateDailyTailMoveRequest</c>, L611-621.</summary>
    public sealed class DailyTailMoveRequest
    {
        /// <summary>Gets the activity id.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets a fragment id inside the logical unit to move.</summary>
        public string FragmentId { get; init; }

        /// <summary>Gets the target date <c>YYYY-MM-DD</c>.</summary>
        public string TargetDate { get; init; }
    }

    /// <summary>A split-and-stagger request. JS: <c>validateDailyStaggerRequest</c>, L622-632.</summary>
    public sealed class DailyStaggerRequest
    {
        /// <summary>Gets the activity id.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets a fragment id inside the logical unit after which gaps are inserted.</summary>
        public string FragmentId { get; init; }

        /// <summary>Gets the number of divisions (&gt;= 1).</summary>
        public long Divisions { get; init; }

        /// <summary>Gets the gap in days (&gt;= 1).</summary>
        public long GapDays { get; init; }
    }

    /// <summary>One inherited precedence violation. JS: <c>preexistingPrecedenceConflicts</c>, L190-198.</summary>
    public sealed class PrecedenceConflict
    {
        /// <summary>Gets the link key, or empty.</summary>
        public string Key { get; init; }

        /// <summary>Gets the predecessor id.</summary>
        public string PredecessorId { get; init; }

        /// <summary>Gets the successor id.</summary>
        public string SuccessorId { get; init; }
    }

    /// <summary>Result of <see cref="PlannerLogic.AssessPrecedenceConflicts"/>. JS L200-206.</summary>
    public sealed class PrecedenceConflictAssessment
    {
        /// <summary>Gets the inherited violations of the native schedule.</summary>
        public IReadOnlyList<PrecedenceConflict> Inherited { get; init; }

        /// <summary>Gets a value indicating whether the policy blocks operations.</summary>
        public bool BlocksOperations { get; init; }
    }

    /// <summary>Precedence conflict policy names (bridge config <c>precedenceConflictPolicy</c>).</summary>
    public static class PrecedenceConflictPolicy
    {
        /// <summary>Inherited violations block the package (default).</summary>
        public const string BlockInherited = "block-inherited";

        /// <summary>Inherited violations are tolerated.</summary>
        public const string AllowInherited = "allow-inherited";
    }
}
