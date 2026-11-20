// Applies the timeline's "Avance real" requests (typed value, grip drag, Agregar / Eliminar avance real) to the
// PlannerSession, and answers its questions (can a box be added here, what would this drag move). Like
// PlannerEditRouter, the control only expresses intent and the session refreshes everything shown.
//
// No Workbench types and no Resources: src/StpClient.Tests compiles this file directly.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Timeline;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>Routes <see cref="TimelineView.ActualProgressRequested"/> / <see cref="TimelineView.ActualProgressQuery"/> to <see cref="PlannerSession"/>.</summary>
    public sealed class ActualProgressRouter
    {
        private readonly PlannerSession _session;

        /// <summary>Initializes a new instance of the <see cref="ActualProgressRouter"/> class.</summary>
        /// <param name="session">The session.</param>
        public ActualProgressRouter(PlannerSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>Applies one request.</summary>
        /// <param name="action">What the user did.</param>
        /// <param name="activity">The activity (row key).</param>
        /// <param name="date">The box's day.</param>
        /// <param name="targetDate">The drop day (move).</param>
        /// <param name="value">The typed value (edit), <c>null</c> = emptied.</param>
        /// <returns>The outcome (show its message when not ok).</returns>
        public PlannerOutcome Apply(TimelineProgressAction action, string activity, DateTime date, DateTime targetDate, double? value)
        {
            string day = Day(date);
            switch (action)
            {
                case TimelineProgressAction.Edit: return _session.SetActualProgress(activity, day, value);
                case TimelineProgressAction.Move: return _session.MoveActualProgress(activity, day, Day(targetDate));
                case TimelineProgressAction.Remove: return _session.RemoveActualProgress(activity, day);
                case TimelineProgressAction.Add: return _session.AddActualProgress(activity, day);
                default: return null;
            }
        }

        /// <summary>Applies a request from the timeline; a refusal goes back to it (red flash + reason).</summary>
        /// <param name="e">The request.</param>
        public void Apply(TimelineProgressEventArgs e)
        {
            if (e == null) return;
            var outcome = Apply(e.Action, e.ActivityKey, e.Date, e.TargetDate, e.Value);
            if (outcome != null && !outcome.Ok) e.Reject(outcome.Message);
            e.Handled = true;
        }

        /// <summary>Answers a question of the timeline.</summary>
        /// <param name="e">The question.</param>
        public void Answer(TimelineProgressQueryEventArgs e)
        {
            if (e == null) return;
            if (e.IsPreview) e.SetMoves(Preview(e.ActivityKey, e.Date, e.TargetDate));
            else e.CanAdd = _session.CanAddActualProgress(e.ActivityKey, Day(e.Date));
            e.Handled = true;
        }

        /// <summary>The boxes a drag would move, as day indices of <see cref="PlannerSession.Days"/> (-1 past the range).</summary>
        /// <param name="activity">The activity.</param>
        /// <param name="origin">The dragged day.</param>
        /// <param name="target">The day under the pointer.</param>
        /// <returns>Origin and target day indices.</returns>
        public IReadOnlyList<(int Origin, int Target)> Preview(string activity, DateTime origin, DateTime target)
        {
            var index = _session.Days.ToDictionary(d => d.Date, d => d.Index, StringComparer.Ordinal);
            int Of(string date) => date != null && index.TryGetValue(date, out int i) ? i : -1;
            return _session.PreviewActualProgressMove(activity, Day(origin), Day(target))
                .Select(m => (Of(m.OriginDate), Of(m.TargetDate))).ToList();
        }

        private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
