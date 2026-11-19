using System.Collections.Generic;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>
    /// A <c>vgs-stp-apply/1</c> package. Property names map to the snake_case wire keys
    /// (<c>project_fingerprint</c>, ...). JS: <c>buildVgsApplyPackage</c>, L1061-1076.
    /// </summary>
    public sealed class ApplyPackage
    {
        /// <summary>Gets the schema, <c>vgs-stp-apply/1</c>.</summary>
        public string Schema { get; init; }

        /// <summary>Gets <c>project_fingerprint</c>.</summary>
        public string ProjectFingerprint { get; init; }

        /// <summary>Gets <c>precedence_fingerprint</c>.</summary>
        public string PrecedenceFingerprint { get; init; }

        /// <summary>Gets <c>horizon_start</c>.</summary>
        public string HorizonStart { get; init; }

        /// <summary>Gets <c>horizon_finish</c>.</summary>
        public string HorizonFinish { get; init; }

        /// <summary>Gets <c>activities</c>, sorted by id (ordinal).</summary>
        public IReadOnlyList<ApplyActivity> Activities { get; init; }
    }

    /// <summary>An activity of an apply package: every fragment of an affected activity.</summary>
    public sealed class ApplyActivity
    {
        /// <summary>Gets <c>activity_id</c>.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets <c>fragments</c>, ordered by original start then id.</summary>
        public IReadOnlyList<ApplyFragment> Fragments { get; init; }
    }

    /// <summary>A fragment of an apply package. JS: <c>serializeAllAffectedActivitiesByFragment</c>, L1035-1059.</summary>
    public sealed class ApplyFragment
    {
        /// <summary>Gets <c>id</c>.</summary>
        public string Id { get; init; }

        /// <summary>Gets <c>native_bar_xml_key</c>.</summary>
        public string NativeBarXmlKey { get; init; }

        /// <summary>Gets <c>native_bar_key</c>.</summary>
        public string NativeBarKey { get; init; }

        /// <summary>Gets <c>logical_unit_id</c> (<c>activity:date</c>).</summary>
        public string LogicalUnitId { get; init; }

        /// <summary>Gets <c>logical_unit_date</c>.</summary>
        public string LogicalUnitDate { get; init; }

        /// <summary>Gets <c>provenance</c>: <c>unchanged</c>, <c>direct</c> or <c>cascade</c>.</summary>
        public string Provenance { get; init; }

        /// <summary>Gets <c>start</c>.</summary>
        public string Start { get; init; }

        /// <summary>Gets <c>finish</c>.</summary>
        public string Finish { get; init; }

        /// <summary>Gets <c>original_start</c>.</summary>
        public string OriginalStart { get; init; }

        /// <summary>Gets <c>original_finish</c>.</summary>
        public string OriginalFinish { get; init; }

        /// <summary>Gets <c>quantity_percentage</c>.</summary>
        public string QuantityPercentage { get; init; }

        /// <summary>Gets <c>locked_completed</c>.</summary>
        public bool LockedCompleted { get; init; }
    }

    /// <summary>A <c>vgs-stp-splits/2</c> scenario. JS: <c>buildScenario</c>, L1004-1033.</summary>
    public sealed class ScenarioPackage
    {
        /// <summary>Gets the schema, <c>vgs-stp-splits/2</c>.</summary>
        public string Schema { get; init; }

        /// <summary>Gets <c>project_fingerprint</c>.</summary>
        public string ProjectFingerprint { get; init; }

        /// <summary>Gets <c>precedence_fingerprint</c>.</summary>
        public string PrecedenceFingerprint { get; init; }

        /// <summary>Gets <c>horizon_start</c>.</summary>
        public string HorizonStart { get; init; }

        /// <summary>Gets <c>horizon_finish</c>.</summary>
        public string HorizonFinish { get; init; }

        /// <summary>Gets <c>activities</c> (changed activities only).</summary>
        public IReadOnlyList<ScenarioActivity> Activities { get; init; }
    }

    /// <summary>An activity of a scenario.</summary>
    public sealed class ScenarioActivity
    {
        /// <summary>Gets <c>activity_id</c>.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets <c>provenance</c>: <c>direct</c> or <c>cascade</c>.</summary>
        public string Provenance { get; init; }

        /// <summary>Gets <c>fragments</c>.</summary>
        public IReadOnlyList<ScenarioFragment> Fragments { get; init; }
    }

    /// <summary>A fragment of a scenario.</summary>
    public sealed class ScenarioFragment
    {
        /// <summary>Gets <c>id</c>.</summary>
        public string Id { get; init; }

        /// <summary>Gets <c>native_bar_xml_key</c>.</summary>
        public string NativeBarXmlKey { get; init; }

        /// <summary>Gets <c>native_bar_key</c>.</summary>
        public string NativeBarKey { get; init; }

        /// <summary>Gets <c>start</c>.</summary>
        public string Start { get; init; }

        /// <summary>Gets <c>finish</c>.</summary>
        public string Finish { get; init; }

        /// <summary>Gets <c>original_start</c>.</summary>
        public string OriginalStart { get; init; }

        /// <summary>Gets <c>original_finish</c>.</summary>
        public string OriginalFinish { get; init; }

        /// <summary>Gets <c>quantity_percentage</c>.</summary>
        public string QuantityPercentage { get; init; }

        /// <summary>Gets <c>locked_completed</c>.</summary>
        public bool LockedCompleted { get; init; }
    }
}
