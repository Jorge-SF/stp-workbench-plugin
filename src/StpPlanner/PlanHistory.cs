using System;
using System.Collections.Generic;
using System.Linq;
using Maptek.Vulcan.Stp.Engine;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>One plan command, replayable on a fresh controller (the engine is pure, so replay is exact).</summary>
    public sealed class PlanOperation
    {
        private readonly Func<CivilHorizonController, OperationResult> run;

        private PlanOperation(string description, Func<CivilHorizonController, OperationResult> run)
        {
            Description = description;
            this.run = run;
        }

        /// <summary>Gets the description shown for undo/redo (Spanish).</summary>
        public string Description { get; }

        /// <summary>Whole-activity day move.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="days">Day shift.</param>
        /// <returns>The operation.</returns>
        public static PlanOperation MoveActivity(string activityId, long days) =>
            new PlanOperation($"Mover {activityId} {PlannerProjection.ShiftText(days)}", c => c.MoveActivity(activityId, days));

        /// <summary>Daily-tail move to a visible day.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day.</param>
        /// <param name="targetDate">Target day.</param>
        /// <returns>The operation.</returns>
        public static PlanOperation MoveDailyTail(string activityId, string fragmentId, string targetDate) =>
            new PlanOperation($"Mover cola de {activityId} al {targetDate}", c => c.MoveDailyTail(activityId, fragmentId, targetDate));

        /// <summary>Daily-tail move to the next period.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day.</param>
        /// <returns>The operation.</returns>
        public static PlanOperation DeferDailyTail(string activityId, string fragmentId) =>
            new PlanOperation($"Pasar cola de {activityId} al siguiente periodo", c => c.DeferDailyTailToNextPeriod(activityId, fragmentId));

        /// <summary>Split-and-stagger.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day.</param>
        /// <param name="divisions">Gaps.</param>
        /// <param name="gapDays">Days per gap.</param>
        /// <returns>The operation.</returns>
        public static PlanOperation Stagger(string activityId, string fragmentId, long divisions, long gapDays) =>
            new PlanOperation($"Dividir y escalonar {activityId}", c => c.StaggerDailyTail(activityId, fragmentId, divisions, gapDays));

        /// <summary>Horizon start edit.</summary>
        /// <param name="day">New first day.</param>
        /// <returns>The operation.</returns>
        public static PlanOperation EditStart(string day) => new PlanOperation($"Cambiar inicio del rango a {day}", c => c.EditStart(day));

        /// <summary>Horizon last-day edit.</summary>
        /// <param name="day">New last day.</param>
        /// <returns>The operation.</returns>
        public static PlanOperation EditFinish(string day) => new PlanOperation($"Cambiar fin del rango a {day}", c => c.EditFinish(day));

        /// <summary>Horizon duration edit.</summary>
        /// <param name="days">New duration.</param>
        /// <returns>The operation.</returns>
        public static PlanOperation EditDuration(long days) => new PlanOperation($"Cambiar duración del rango a {days} días", c => c.EditDuration(days));

        internal OperationResult Run(CivilHorizonController controller) => run(controller);
    }

    /// <summary>
    /// A plan state: the horizon it starts from plus the accepted operations since then, and, at that point, the
    /// equipment settings (manual resources and assignments) and the local actual progress ("Avance real"), so
    /// undo/redo covers all of them.
    /// </summary>
    internal sealed class PlanState
    {
        public PlanState(CivilHorizon seed, IReadOnlyList<PlanOperation> operations, string description, ResourceState resources = null, ActualProgressState progress = null)
        {
            Seed = seed;
            Operations = operations;
            Description = description;
            Resources = resources ?? ResourceState.Empty;
            Progress = progress ?? ActualProgressState.Empty;
        }

        public CivilHorizon Seed { get; }

        public IReadOnlyList<PlanOperation> Operations { get; }

        /// <summary>What produced this state (for "Deshacer …").</summary>
        public string Description { get; }

        /// <summary>Gets the equipment settings.</summary>
        public ResourceState Resources { get; }

        /// <summary>Gets the local actual progress.</summary>
        public ActualProgressState Progress { get; }

        public PlanState With(PlanOperation operation) => new PlanState(Seed, Operations.Concat(new[] { operation }).ToList(), operation.Description, Resources, Progress);

        /// <summary>The same plan with other equipment settings (no replay needed).</summary>
        public PlanState With(ResourceState resources, string description) => new PlanState(Seed, Operations, description, resources, Progress);

        /// <summary>The same plan with another actual progress (an "Avance real" edit).</summary>
        public PlanState WithProgress(ActualProgressState progress, string description) => new PlanState(Seed, Operations, description, Resources, progress);
    }

    /// <summary>Undo/redo stacks of <see cref="PlanState"/>.</summary>
    internal sealed class PlanHistory
    {
        private readonly List<PlanState> past = new List<PlanState>();
        private readonly List<PlanState> future = new List<PlanState>();
        private readonly int limit;

        public PlanHistory(PlanState initial, int limit)
        {
            Current = initial;
            this.limit = Math.Max(1, limit);
        }

        public PlanState Current { get; private set; }

        public bool CanUndo => past.Count > 0;

        public bool CanRedo => future.Count > 0;

        public string UndoDescription => CanUndo ? Current.Description : string.Empty;

        public string RedoDescription => CanRedo ? future[future.Count - 1].Description : string.Empty;

        public void Push(PlanState next)
        {
            past.Add(Current);
            if (past.Count > limit) past.RemoveAt(0);
            future.Clear();
            Current = next;
        }

        public PlanState Undo()
        {
            future.Add(Current);
            Current = past[past.Count - 1];
            past.RemoveAt(past.Count - 1);
            return Current;
        }

        public PlanState Redo()
        {
            past.Add(Current);
            Current = future[future.Count - 1];
            future.RemoveAt(future.Count - 1);
            return Current;
        }
    }
}
