using System.Collections.Generic;
using System.Linq;
using System.Text;
using Maptek.Vulcan.Stp.Engine.Internal;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>
    /// The exported functions of reference/web/planner_logic.js, as pure functions over immutable models.
    /// Inputs are never changed; each call returns a new model. Errors are <see cref="EngineException"/> with the
    /// exact JS message and code.
    /// </summary>
    public static class PlannerLogic
    {
        /// <summary>Default horizon length in days.</summary>
        public const int DefaultHorizonDays = 28;

        /// <summary>Earliest pending start (midnight) plus <paramref name="durationDays"/>. JS: <c>deriveCivilHorizon</c>, L84-94.</summary>
        /// <param name="model">The model.</param>
        /// <param name="durationDays">1-366.</param>
        /// <returns>The horizon.</returns>
        public static CivilHorizon DeriveCivilHorizon(PlannerModel model, long durationDays = DefaultHorizonDays) =>
            Rules.DeriveCivilHorizon(WModel.From(model), durationDays);

        /// <summary>Midnight-to-midnight, 1-366 days, overlapping pending work. JS: <c>validateCivilHorizon</c>, L96-114.</summary>
        /// <param name="model">The model.</param>
        /// <param name="horizon">The candidate horizon.</param>
        /// <returns>The normalized horizon with its duration.</returns>
        public static CivilHorizon ValidateCivilHorizon(PlannerModel model, CivilHorizon horizon) =>
            Rules.ValidateCivilHorizon(WModel.From(model), horizon);

        /// <summary>JS: <c>loadHorizonPreference</c>, L116-133.</summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        /// <returns>The preference.</returns>
        public static HorizonPreference LoadHorizonPreference(IHorizonStorage storage, string projectKey) =>
            HorizonPreferences.Load(storage, projectKey);

        /// <summary>JS: <c>saveHorizonPreference</c>, L135-144.</summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        /// <param name="horizon">The horizon to persist.</param>
        /// <returns>The save result.</returns>
        public static HorizonPreferenceSaveResult SaveHorizonPreference(IHorizonStorage storage, string projectKey, CivilHorizon horizon)
        {
            if (horizon == null || !horizon.DurationDays.HasValue)
            {
                throw EngineException.Fail("preferencia de horizonte inválida");
            }

            return HorizonPreferences.Save(storage, projectKey, horizon.HorizonStart, horizon.HorizonFinish, horizon.DurationDays.Value);
        }

        /// <summary>JS: <c>escapeHtml</c>, L156-158.</summary>
        /// <param name="value">The text.</param>
        /// <returns>The escaped text.</returns>
        public static string EscapeHtml(string value)
        {
            var builder = new StringBuilder();
            foreach (char c in value ?? "undefined")
            {
                switch (c)
                {
                    case '&': builder.Append("&amp;"); break;
                    case '<': builder.Append("&lt;"); break;
                    case '>': builder.Append("&gt;"); break;
                    case '"': builder.Append("&quot;"); break;
                    case '\'': builder.Append("&#39;"); break;
                    default: builder.Append(c); break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Flattens a snapshot into the planner model; each fragment gets its activity id, empty provenance and its
        /// native anchors. JS: <c>buildPlannerModel</c>, L160-184.
        /// </summary>
        /// <param name="snapshot">The snapshot.</param>
        /// <returns>The (not yet validated) model.</returns>
        /// <exception cref="EngineException"><c>instantánea V2 inválida</c> (<c>SNAPSHOT_INVALID</c>).</exception>
        public static PlannerModel BuildPlannerModel(PlannerSnapshot snapshot)
        {
            if (snapshot?.Activities == null || snapshot.Precedences == null
                || snapshot.Activities.Any(a => a == null || a.NativeBars == null || a.Fragments == null || a.NativeAnchors == null))
            {
                throw EngineException.Fail("instantánea V2 inválida", EngineException.SnapshotInvalid);
            }

            return new PlannerModel
            {
                Activities = snapshot.Activities.Select(a => new PlannerActivity { Id = a.Id, NativeBars = a.NativeBars.ToArray() }).ToArray(),
                Precedences = snapshot.Precedences.ToArray(),
                Fragments = snapshot.Activities.SelectMany(activity => activity.Fragments.Select(fragment =>
                {
                    var before = activity.NativeAnchors.FirstOrDefault(anchor => anchor.RightBarKey == fragment.NativeBarKey && anchor.RightAt == fragment.Start);
                    var after = activity.NativeAnchors.FirstOrDefault(anchor => anchor.LeftBarKey == fragment.NativeBarKey && anchor.At == fragment.Finish);
                    var w = WFragment.From(fragment);
                    w.ActivityId = activity.Id;
                    w.Provenance = string.Empty;
                    w.AnchorBefore = before != null ? before.RightAt : string.Empty;
                    w.AnchorAfter = after != null ? after.At : string.Empty;
                    return w.ToPublic();
                })).ToArray(),
            };
        }

        /// <summary>Strict browser preflight; returns the normalized copy. JS: <c>validatePlannerModel</c>, L186-188.</summary>
        /// <param name="model">The model.</param>
        /// <returns>The validated model.</returns>
        public static PlannerModel ValidatePlannerModel(PlannerModel model) => Rules.Preflight(WModel.From(model), true).Clone().ToPublic();

        /// <summary>Links already violated in the native schedule. JS: <c>preexistingPrecedenceConflicts</c>, L190-198.</summary>
        /// <param name="model">The model.</param>
        /// <returns>The inherited conflicts.</returns>
        public static IReadOnlyList<PrecedenceConflict> PreexistingPrecedenceConflicts(PlannerModel model) =>
            Rules.PreexistingPrecedenceConflicts(WModel.From(model));

        /// <summary>
        /// <c>block-inherited</c> (default) blocks operations when inherited violations exist; <c>allow-inherited</c>
        /// (exactly) does not. JS: <c>assessPrecedenceConflicts</c>, L200-206.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <param name="policy">The policy name.</param>
        /// <returns>The assessment.</returns>
        public static PrecedenceConflictAssessment AssessPrecedenceConflicts(PlannerModel model, string policy = PrecedenceConflictPolicy.BlockInherited)
        {
            var inherited = Rules.PreexistingPrecedenceConflicts(WModel.From(model));
            return new PrecedenceConflictAssessment
            {
                Inherited = inherited,
                BlocksOperations = inherited.Count > 0 && policy != PrecedenceConflictPolicy.AllowInherited,
            };
        }

        /// <summary>JS: <c>cascadeForward</c>, L842-868.</summary>
        /// <param name="model">The model.</param>
        /// <param name="directActivityIds">Activities moved directly.</param>
        /// <returns>The cascaded model.</returns>
        public static PlannerModel CascadeForward(PlannerModel model, IEnumerable<string> directActivityIds = null) =>
            Rules.CascadeForward(WModel.From(model), directActivityIds ?? Enumerable.Empty<string>()).ToPublic();

        /// <summary>JS: <c>moveFragment</c>, L870-883.</summary>
        /// <param name="model">The model.</param>
        /// <param name="fragmentId">The fragment to move (it and the later fragments of its bar move).</param>
        /// <param name="newStart">The new start, a whole number of days away.</param>
        /// <returns>The moved model (cascaded when moved later).</returns>
        public static PlannerModel MoveFragment(PlannerModel model, string fragmentId, string newStart) =>
            Rules.MoveFragment(WModel.From(model), fragmentId, newStart).ToPublic();

        /// <summary>JS: <c>moveDailyTail</c>, L885-905.</summary>
        /// <param name="model">The model.</param>
        /// <param name="request">The request.</param>
        /// <returns>The moved model.</returns>
        public static PlannerModel MoveDailyTail(PlannerModel model, DailyTailMoveRequest request) =>
            Rules.MoveDailyTail(WModel.From(model), request).ToPublic();

        /// <summary>JS: <c>staggerDailyTail</c>, L907-936.</summary>
        /// <param name="model">The model.</param>
        /// <param name="request">The request.</param>
        /// <param name="horizon">Optional horizon every target date must fall in.</param>
        /// <returns>The staggered model.</returns>
        public static PlannerModel StaggerDailyTail(PlannerModel model, DailyStaggerRequest request, CivilHorizon horizon = null) =>
            Rules.StaggerDailyTail(WModel.From(model), request, horizon).ToPublic();

        /// <summary>JS: <c>moveActivity</c>, L938-967.</summary>
        /// <param name="model">The model.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="deltaDays">Non-zero day shift.</param>
        /// <param name="horizon">The visible horizon.</param>
        /// <returns>The moved model.</returns>
        public static PlannerModel MoveActivity(PlannerModel model, string activityId, long deltaDays, CivilHorizon horizon) =>
            Rules.MoveActivity(WModel.From(model), activityId, deltaDays, horizon).ToPublic();

        /// <summary>JS: <c>buildScenario</c>, L1004-1033.</summary>
        /// <param name="model">The model.</param>
        /// <param name="source">Project identity.</param>
        /// <param name="horizon">The horizon.</param>
        /// <returns>The <c>vgs-stp-splits/2</c> scenario.</returns>
        public static ScenarioPackage BuildScenario(PlannerModel model, ProjectSource source, CivilHorizon horizon) =>
            Rules.BuildScenario(WModel.From(model), source, horizon);

        /// <summary>JS: <c>buildVgsApplyPackage</c>, L1061-1076.</summary>
        /// <param name="model">The model.</param>
        /// <param name="source">Project identity.</param>
        /// <param name="horizon">The horizon.</param>
        /// <returns>The <c>vgs-stp-apply/1</c> package.</returns>
        public static ApplyPackage BuildVgsApplyPackage(PlannerModel model, ProjectSource source, CivilHorizon horizon) =>
            Rules.BuildVgsApplyPackage(WModel.From(model), source, horizon);
    }
}
