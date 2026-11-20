using System;
using System.Globalization;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Engine = Maptek.Vulcan.Stp.Engine;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>
    /// Wire DTOs (StpBridge) to engine models (StpEngine) and back.
    /// </summary>
    /// <remarks>
    /// The engine compares civil times as text (<c>.5</c> and <c>.500</c> are different values to it, as in
    /// planner_logic.js), so every time is rendered with <see cref="PyDateTime.SnapshotIso"/>: exactly the text Python
    /// wrote in <c>snapshot_v2.json</c> (the strict reader rejects any other spelling, so the round trip is exact).
    /// Decimals use <see cref="PyDecimal.ToString"/> (<c>str(Decimal)</c>), lags the integer microsecond text.
    /// </remarks>
    public static class SnapshotMapper
    {
        /// <summary>The engine view of a strict <c>vgs-stp-snapshot/2</c>.</summary>
        /// <param name="snapshot">The parsed snapshot.</param>
        /// <returns>The engine snapshot, ready for <see cref="Engine.PlannerLogic.BuildPlannerModel"/>.</returns>
        public static Engine.PlannerSnapshot ToEngine(SnapshotV2 snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            return new Engine.PlannerSnapshot
            {
                Activities = snapshot.Activities.Select(ToEngine).ToArray(),
                Precedences = snapshot.Precedences.Select(ToEngine).ToArray(),
            };
        }

        /// <summary>Snapshot to validated planner model (<c>validatePlannerModel(buildPlannerModel(snapshot))</c>).</summary>
        /// <param name="snapshot">The parsed snapshot.</param>
        /// <returns>The validated model.</returns>
        /// <exception cref="Engine.EngineException">The snapshot breaks a planner rule.</exception>
        public static Engine.PlannerModel ToModel(SnapshotV2 snapshot) =>
            Engine.PlannerLogic.ValidatePlannerModel(Engine.PlannerLogic.BuildPlannerModel(ToEngine(snapshot)));

        /// <summary>The project identity of a snapshot, for apply packages.</summary>
        /// <param name="snapshot">The snapshot.</param>
        /// <returns>The fingerprints.</returns>
        public static Engine.ProjectSource Source(SnapshotV2 snapshot) => new Engine.ProjectSource
        {
            ProjectFingerprint = snapshot.ProjectFingerprint,
            PrecedenceFingerprint = snapshot.PrecedenceFingerprint,
        };

        /// <summary>Civil time text as the snapshot spells it.</summary>
        /// <param name="value">The time.</param>
        /// <returns>The text.</returns>
        public static string Time(DateTime value) => PyDateTime.SnapshotIso(value);

        private static Engine.SnapshotActivity ToEngine(SnapshotActivity activity) => new Engine.SnapshotActivity
        {
            Id = activity.Id,
            NativeBars = activity.NativeBars.Select(bar => new Engine.NativeBar
            {
                Key = bar.Key,
                XmlKey = bar.XmlKey,
                Start = Time(bar.Start),
                Finish = Time(bar.Finish),
                QuantityPercentage = bar.QuantityPercentage.ToString(),
                PercentComplete = bar.PercentComplete.ToString(),
                CompleteWorkedMinutes = bar.CompleteWorkedMinutes.ToString(),
            }).ToArray(),
            Fragments = activity.Fragments.Select(fragment => new Engine.PlannerFragment
            {
                Id = fragment.Id,
                ActivityId = activity.Id,
                NativeBarXmlKey = fragment.NativeBarXmlKey,
                NativeBarKey = fragment.NativeBarKey,
                Start = Time(fragment.Start),
                Finish = Time(fragment.Finish),
                OriginalStart = Time(fragment.OriginalStart),
                OriginalFinish = Time(fragment.OriginalFinish),
                LogicalUnitDate = PyDateTime.DateIso(fragment.LogicalUnitDate),
                DisplaySuppressed = fragment.DisplaySuppressed,
                QuantityPercentage = fragment.QuantityPercentage.ToString(),
                LockedCompleted = fragment.LockedCompleted,
            }).ToArray(),
            NativeAnchors = activity.NativeAnchors.Select(anchor => new Engine.NativeAnchor
            {
                LeftBarXmlKey = anchor.LeftBarXmlKey,
                LeftBarKey = anchor.LeftBarKey,
                RightBarXmlKey = anchor.RightBarXmlKey,
                RightBarKey = anchor.RightBarKey,
                At = Time(anchor.At),
                RightAt = Time(anchor.RightAt),
            }).ToArray(),
        };

        private static Engine.PrecedenceLink ToEngine(SnapshotPrecedence link) => new Engine.PrecedenceLink
        {
            Key = link.Key,
            PredecessorId = link.PredecessorId,
            SuccessorId = link.SuccessorId,
            PredecessorBar = link.PredecessorBar,
            SuccessorBar = link.SuccessorBar,
            Type = link.Type,
            LagMicroseconds = link.LagMicroseconds.ToString(CultureInfo.InvariantCulture),
            GroupKey = link.GroupKey,
        };

        /// <summary>
        /// Engine package (civil text) to the typed wire package. Its <see cref="ApplyPackage.ToCanonicalBytes"/> are the
        /// bytes the bridge stores and hashes (Python <c>isoformat()</c>, see docs/known_issues.md KI-1).
        /// </summary>
        /// <param name="package">The engine package.</param>
        /// <returns>The wire package.</returns>
        /// <exception cref="WireFormatException">A field is not valid wire data.</exception>
        public static ApplyPackage ToWire(Engine.ApplyPackage package)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (package.Schema != ApplyPackage.SchemaId) throw new WireFormatException("apply package schema mismatch");
            return new ApplyPackage(
                package.ProjectFingerprint,
                package.PrecedenceFingerprint,
                PyDateTime.ParseDateTime(package.HorizonStart, true, "package.horizon_start"),
                PyDateTime.ParseDateTime(package.HorizonFinish, true, "package.horizon_finish"),
                package.Activities.Select(activity => new ApplyActivity(
                    activity.ActivityId,
                    activity.Fragments.Select(fragment => new ApplyFragment(
                        fragment.Id,
                        fragment.NativeBarXmlKey,
                        fragment.NativeBarKey,
                        fragment.LogicalUnitId,
                        PyDateTime.ParseDate(fragment.LogicalUnitDate, "fragment.logical_unit_date"),
                        Provenance(fragment.Provenance),
                        PyDateTime.ParseDateTime(fragment.Start, true, "fragment.start"),
                        PyDateTime.ParseDateTime(fragment.Finish, true, "fragment.finish"),
                        PyDateTime.ParseDateTime(fragment.OriginalStart, true, "fragment.original_start"),
                        PyDateTime.ParseDateTime(fragment.OriginalFinish, true, "fragment.original_finish"),
                        PyDecimal.Parse(fragment.QuantityPercentage),
                        fragment.LockedCompleted)))));
        }

        private static ApplyProvenance Provenance(string text)
        {
            switch (text)
            {
                case "unchanged": return ApplyProvenance.Unchanged;
                case "direct": return ApplyProvenance.Direct;
                case "cascade": return ApplyProvenance.Cascade;
                default: throw new WireFormatException("fragment.provenance must be unchanged, direct or cascade");
            }
        }
    }
}
