using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Maptek.Vulcan.Stp.Engine;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>
    /// "Avance real": what was really done per activity and day, entered by hand under the timeline (template
    /// <c>actualProgressMode</c>). Local only: saved per project, never sent to VGS or put in the apply package.
    /// Every edit is a plan step, so Deshacer/Rehacer cover it.
    /// </summary>
    public sealed partial class PlannerSession
    {
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<ActualProgressBox>> NoBoxes = new Dictionary<string, IReadOnlyList<ActualProgressBox>>();

        private bool showActualProgress;
        private bool actualProgressModeSet;
        private ActualProgressState savedProgress;
        private Dictionary<string, ProgressRow> progressRows = new Dictionary<string, ProgressRow>(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, IReadOnlyList<ActualProgressBox>> progressBoxes = NoBoxes;
        private IReadOnlyList<ActualProgressRow> progressTypeRows = Array.Empty<ActualProgressRow>();
        private ActualProgressSummary progressSummary = new ActualProgressSummary { Dates = Array.Empty<string>(), Types = Array.Empty<TypeProgress>() };

        /// <summary>
        /// Gets or sets a value indicating whether the "Avance real" mode is on (value boxes under the timeline days,
        /// actual bars in the charts). It starts on when the project already has values or added boxes.
        /// </summary>
        public bool ShowActualProgress
        {
            get => showActualProgress;
            set
            {
                if (busy || !Set(ref showActualProgress, value)) return;
                RefreshView();
            }
        }

        /// <summary>Gets the local actual progress (the state of the current plan step).</summary>
        public ActualProgressState ActualProgressState => history.Current.Progress;

        /// <summary>Gets a value indicating whether any actual progress was entered.</summary>
        public bool HasActualProgress => history.Current.Progress.HasAny;

        /// <summary>Gets the value boxes per shown activity (empty while the mode is off), in day order.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<ActualProgressBox>> ActualProgressBoxes => progressBoxes;

        /// <summary>Gets the "Avance real" tab: one row per type of the shown rows (empty while the mode is off).</summary>
        public IReadOnlyList<ActualProgressRow> ActualProgressRows => progressTypeRows;

        /// <summary>Gets planned vs actual per type of the shown rows (the charts; actual values count only with the mode on).</summary>
        public ActualProgressSummary ActualProgressSummary => progressSummary;

        /// <summary>The value of a day, or <c>null</c>.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day <c>YYYY-MM-DD</c>.</param>
        /// <returns>The value.</returns>
        public double? ActualProgressValue(string activityId, string date) => ActualProgress.Value(ActualProgressState, activityId, date);

        /// <summary>Whether "Agregar avance real" is offered on a shown day (no box yet, and no plan unless its box was removed).</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <returns>Can add.</returns>
        public bool CanAddActualProgress(string activityId, string date) =>
            showActualProgress && !busy && TryRow(activityId, out var row) && ActualProgress.CanAddSlot(ActualProgressState, row, date);

        /// <summary>Whether a shown day has a value box.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <returns>Has a box.</returns>
        public bool HasActualProgressBox(string activityId, string date) =>
            showActualProgress && TryRow(activityId, out var row) && ActualProgress.ShowsBox(ActualProgressState, row, date);

        /// <summary>
        /// Types a value into a day's box (template value input). <c>null</c> (an empty box) clears the value but keeps
        /// the box.
        /// </summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <param name="value">The value (&gt;= 0), or <c>null</c>.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome SetActualProgress(string activityId, string date, double? value)
        {
            if (!CanEditProgress(out var refused)) return refused;
            if (!HasActualProgressBox(activityId, date)) return Report(PlannerOutcome.Failure(ActualProgressMessages.NoBox, "invalid"));
            if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0))
            {
                return Report(PlannerOutcome.Failure(ActualProgressMessages.InvalidValue, "invalid"));
            }

            var next = ActualProgress.EditValue(ActualProgressState, activityId, date, value);
            return ChangeProgress(next, $"Avance real de {activityId} el {DayLabel(date)}", ActualProgressMessages.ValueSaved);
        }

        /// <summary>"Agregar avance real": an empty box on a shown day (template <c>openActualProgressSlotMenu</c>).</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome AddActualProgress(string activityId, string date)
        {
            if (!CanEditProgress(out var refused)) return refused;
            if (!TryRow(activityId, out var row) || !ActualProgress.CanAddSlot(ActualProgressState, row, date))
            {
                return Report(PlannerOutcome.Failure(ActualProgressMessages.CannotAdd, "invalid"));
            }

            var next = ActualProgress.RestoreSlot(ActualProgressState, activityId, date, ActualProgress.IsScheduled(row, date));
            return ChangeProgress(next, $"Agregar avance real a {activityId} el {DayLabel(date)}", ActualProgressMessages.Added);
        }

        /// <summary>"Eliminar avance real": removes a day's value and box (template <c>openActualProgressRemovalMenu</c>).</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome RemoveActualProgress(string activityId, string date)
        {
            if (!CanEditProgress(out var refused)) return refused;
            if (!TryRow(activityId, out var row) || !ActualProgress.ShowsBox(ActualProgressState, row, date))
            {
                return Report(PlannerOutcome.Failure(ActualProgressMessages.NoBox, "invalid"));
            }

            var next = ActualProgress.ClearSlot(ActualProgressState, activityId, date, ActualProgress.IsScheduled(row, date));
            return ChangeProgress(next, $"Eliminar avance real de {activityId} el {DayLabel(date)}", ActualProgressMessages.Removed);
        }

        /// <summary>
        /// What a box drag would move (template <c>actualProgressSlotCascadePreview</c>): forward, every later box of the
        /// row; backward, the dragged box only.
        /// </summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="originDate">The dragged day.</param>
        /// <param name="targetDate">The day under the pointer.</param>
        /// <returns>The moves (empty when nothing would move).</returns>
        public IReadOnlyList<ActualProgressMove> PreviewActualProgressMove(string activityId, string originDate, string targetDate)
        {
            if (!showActualProgress || busy || !TryRow(activityId, out var row)) return Array.Empty<ActualProgressMove>();
            var slots = ActualProgress.VisibleSlotDates(ActualProgressState, row);
            if (!slots.Contains(originDate)) return Array.Empty<ActualProgressMove>();
            return ActualProgress.SlotCascadeMoves(ActualProgressState, activityId, originDate, targetDate, ActualProgress.VisibleDates(row), slots);
        }

        /// <summary>Drops a dragged box on another shown day (template <c>finishActualProgressDrag</c>).</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="originDate">The dragged day.</param>
        /// <param name="targetDate">The day it was dropped on.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome MoveActualProgress(string activityId, string originDate, string targetDate)
        {
            if (!CanEditProgress(out var refused)) return refused;
            if (!TryRow(activityId, out var row) || !ActualProgress.VisibleSlotDates(ActualProgressState, row).Contains(originDate))
            {
                return Report(PlannerOutcome.Failure(ActualProgressMessages.NoBox, "invalid"));
            }

            if (!ActualProgress.TryApplySlotCascade(ActualProgressState, row, originDate, targetDate, out var next))
            {
                return Report(PlannerOutcome.Failure(ActualProgressMessages.NothingToMove, "invalid"));
            }

            return ChangeProgress(next, $"Desplazar avance real de {activityId} del {DayLabel(originDate)} al {DayLabel(targetDate)}", ActualProgressMessages.Moved);
        }

        // ------------------------------------------------------------------ internals

        private ActualProgressState LoadActualProgress(string projectKey)
        {
            var progress = ActualProgressStore.Load(storage, projectKey);
            savedProgress = progress;
            if (!actualProgressModeSet)
            {
                // template: the mode starts on when the local state has values or explicit boxes; a refresh keeps it
                showActualProgress = progress.HasAny;
                actualProgressModeSet = true;
            }

            return progress;
        }

        // template renderPlanner: legacy per-fragment values move to their visible activity day. Not a plan step.
        private void MigrateActualProgress()
        {
            var segments = view.Rows.SelectMany(r => (r.Segments ?? Array.Empty<DaySegment>())
                .Select(s => new ProgressSegmentRef { FragmentId = s.FragmentId, ActivityId = r.ActivityId, Date = s.Date })).ToList();
            var current = history.Current.Progress;
            var migrated = ActualProgress.Migrate(current, segments);
            if (migrated.SameContent(current)) return;
            history = new PlanHistory(history.Current.WithProgress(migrated, history.Current.Description), options.UndoLimit);
            SaveActualProgress(migrated);
            RefreshView();
        }

        private bool CanEditProgress(out PlannerOutcome refused)
        {
            refused = busy ? Report(PlannerOutcome.Failure(PlannerMessages.Busy))
                : !showActualProgress ? Report(PlannerOutcome.Failure(ActualProgressMessages.ModeOff, "mode"))
                : null;
            return refused == null;
        }

        private PlannerOutcome ChangeProgress(ActualProgressState next, string description, string message)
        {
            if (next.SameContent(ActualProgressState)) return Report(PlannerOutcome.Success(message));
            history.Push(history.Current.WithProgress(next, description));
            string error = SaveActualProgress(next);
            RefreshView();
            return error == null ? Report(PlannerOutcome.Success(message))
                : Report(PlannerOutcome.Failure(ActualProgressMessages.NotSaved + error, "storage", error));
        }

        /// <summary>Stores the progress when it changed; returns the error text, or <c>null</c>.</summary>
        private string SaveActualProgress(ActualProgressState progress)
        {
            if (progress == null || (savedProgress != null && progress.SameContent(savedProgress))) return null;
            try
            {
                ActualProgressStore.Save(storage, Snapshot.ProjectKey, progress);
                savedProgress = progress;
                return null;
            }
            catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException)
            {
                return error.Message;
            }
        }

        private bool TryRow(string activityId, out ProgressRow row)
        {
            row = null;
            return activityId != null && progressRows.TryGetValue(activityId, out row);
        }

        private static string DayLabel(string date) =>
            DateTime.TryParseExact(date ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day.ToString("dd/MM", CultureInfo.InvariantCulture) : date;

        // Called by RefreshView after the rows are built.
        private void BuildActualProgressView()
        {
            var days = view.Days;
            var progress = history.Current.Progress;
            var rows = new Dictionary<string, ProgressRow>(StringComparer.Ordinal);
            var ordered = new List<ProgressRow>();
            foreach (var row in view.Rows)
            {
                var byDay = (row.Segments ?? Array.Empty<DaySegment>()).GroupBy(s => s.DayIndex).ToDictionary(g => g.Key, g => g.ToList());
                var progressRow = new ProgressRow
                {
                    ActivityId = row.ActivityId,
                    Type = row.Type,
                    Resources = catalog.TryGetValue(row.ActivityId, out var info) && !string.IsNullOrEmpty(info.Resource) ? new[] { info.Resource } : Array.Empty<string>(),
                    Days = days.Select(d => new ProgressDay
                    {
                        Date = d.Date,
                        Segments = byDay.TryGetValue(d.Index, out var list)
                            ? list.Select(s => new ProgressSegment { Completed = s.Status == SegmentStatus.Completed, Amount = s.Amount ?? s.WidthRatio, Resource = s.Resource }).ToList()
                            : (IReadOnlyList<ProgressSegment>)Array.Empty<ProgressSegment>(),
                    }).ToList(),
                };
                if (!rows.ContainsKey(row.ActivityId)) rows[row.ActivityId] = progressRow;
                ordered.Add(progressRow);
            }

            progressRows = rows;
            progressSummary = ActualProgress.BuildByType(new ProgressView { Dates = days.Select(d => d.Date).ToList(), Rows = ordered }, showActualProgress ? progress : ActualProgressState.Empty);
            if (!showActualProgress)
            {
                progressBoxes = NoBoxes;
                progressTypeRows = Array.Empty<ActualProgressRow>();
                return;
            }

            var boxes = new Dictionary<string, IReadOnlyList<ActualProgressBox>>(StringComparer.Ordinal);
            foreach (var row in ordered)
            {
                var list = new List<ActualProgressBox>();
                for (int i = 0; i < row.Days.Count; i++)
                {
                    string date = row.Days[i].Date;
                    if (!ActualProgress.ShowsBox(progress, row, date)) continue;
                    double? value = ActualProgress.Value(progress, row.ActivityId, date);
                    list.Add(new ActualProgressBox
                    {
                        ActivityId = row.ActivityId,
                        Date = date,
                        DayIndex = i,
                        Value = value,
                        Text = value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty,
                        IsScheduled = row.Days[i].Segments.Count > 0,
                    });
                }

                boxes[row.ActivityId] = list;
            }

            progressBoxes = boxes;
            var dates = days.Select(d => d.Date).ToList();
            progressTypeRows = progressSummary.Types.Select(t => new ActualProgressRow
            {
                Type = t.Type,
                Color = TypeColor(t.Type),
                Cells = days.Select(d => Cell(t, d)).ToList(),
                TotalText = TotalText(ActualProgress.TypePeriodTotals(t, dates)),
            }).ToList();
        }

        private static ActualProgressCell Cell(TypeProgress type, HorizonDay day)
        {
            var value = type.Day(day.Date);
            double scheduled = value?.Scheduled ?? 0;
            double? actual = value?.Actual;
            string s = ActualProgressMessages.CompactNumber(scheduled);
            return new ActualProgressCell
            {
                Date = day.Date,
                IsWeekend = day.IsWeekend,
                Text = actual.HasValue ? ActualProgressMessages.CompactNumber(actual) + "/" + s : "—",
                ToolTip = actual.HasValue ? $"{type.Type}: {ActualProgressMessages.CompactNumber(actual)} real de {s} programado" : $"Sin avance real de {type.Type} para esta fecha",
                State = !actual.HasValue ? ActualProgressLevel.None : actual.Value < scheduled ? ActualProgressLevel.Under : ActualProgressLevel.Met,
            };
        }

        // template formatActualProgressPeriodTotal
        private static string TotalText(ProgressTotals totals) =>
            $"Real: {ActualProgressMessages.CompactNumber(totals.Actual)} / Plan: {ActualProgressMessages.CompactNumber(totals.Scheduled)}";
    }
}
