// Applies the timeline control's edit requests (StpTimeline: drag, keyboard, context menu) to the PlannerSession.
// The control only expresses intent; this class is the one place that turns a request into a session command, and
// a refusal into the Spanish reason the control flashes. Everything visible (timeline, Resumen, capacity, selected
// activity, "cambios sin aplicar") updates from the session's own change notifications, never from here.
//
// No Workbench types and no Resources: src/StpClient.Tests compiles this file directly.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Timeline;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>One edit request, copied from <see cref="TimelineEditEventArgs"/> (whose constructor is internal to the control).</summary>
    public sealed class PlannerEditRequest
    {
        public TimelineEditAction Action { get; set; }
        public TimelineMoveScope Scope { get; set; }
        public string ActivityKey { get; set; }

        /// <summary>The pressed block's tag: the <see cref="DaySegment"/> PlannerTimelineModel put there.</summary>
        public object Tag { get; set; }

        /// <summary>The pressed block's date.</summary>
        public DateTime? Date { get; set; }

        public int DayDelta { get; set; }
        public int Divisions { get; set; }
        public int GapDays { get; set; }

        /// <summary>Tail moves: where the pressed day lands.</summary>
        public DateTime? TargetDate => Date?.AddDays(DayDelta);

        public static PlannerEditRequest From(TimelineEditEventArgs e) => new PlannerEditRequest
        {
            Action = e.Action,
            Scope = e.Scope,
            ActivityKey = e.ActivityKey,
            Tag = e.Block?.Tag,
            Date = e.Date,
            DayDelta = e.DayDelta,
            Divisions = e.Divisions,
            GapDays = e.GapDays,
        };
    }

    /// <summary>What "Asignar equipo" asks the user.</summary>
    public sealed class ResourcePickRequest
    {
        public string ActivityLabel { get; set; }
        public string DateText { get; set; }

        /// <summary>The activity's own equipment (the "Predeterminado de actividad" entry).</summary>
        public string ActivityResource { get; set; }

        /// <summary>The day's local assignment, or null.</summary>
        public string Current { get; set; }

        public IReadOnlyList<string> Options { get; set; }
    }

    public sealed class PlannerEditRouter : ObservableObject, IDisposable
    {
        /// <summary>Header of the menu entry when the day is already exempt (the browser's label).</summary>
        public const string RemoveExemptionHeader = "Quitar exención";

        public const string ExemptHeader = "Exentar Límite";

        /// <summary>Asked when a menu or key acts on no day (should not happen with the control's own requests).</summary>
        public const string NoDayMessage = "Seleccione un día pendiente de la actividad.";

        private readonly PlannerSession _session;
        private readonly Func<ResourcePickRequest, PickedResource> _pickResource;
        private bool _isEditable;

        /// <param name="session">The plan.</param>
        /// <param name="pickResource">Shows the "Asignar equipo" picker; null result = cancelled. Null: the entry is hidden.</param>
        public PlannerEditRouter(PlannerSession session, Func<ResourcePickRequest, PickedResource> pickResource)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _pickResource = pickResource;
            _isEditable = !session.IsBusy;
            session.PropertyChanged += OnSessionChanged;
        }

        /// <summary>
        /// The control's IsEditable: off while the session is busy (refresh, apply). Edits stay on without a live
        /// bridge: they are local, and only Actualizar / Aplicar a VGS need VGS (design doc §4.4).
        /// </summary>
        public bool IsEditable { get => _isEditable; private set => Set(ref _isEditable, value); }

        public void Dispose() => _session.PropertyChanged -= OnSessionChanged;

        /// <summary>
        /// Applies a request. Returns the session's outcome, or null when nothing was asked of it (picker cancelled).
        /// A failed outcome's <see cref="PlannerOutcome.Message"/> is the reason the control shows.
        /// </summary>
        public PlannerOutcome Apply(PlannerEditRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!IsEditable && request.Action != TimelineEditAction.Undo && request.Action != TimelineEditAction.Redo)
                return Refused(PlannerMessages.Busy);

            var segment = request.Tag as DaySegment;
            switch (request.Action)
            {
                case TimelineEditAction.Undo:
                    return _session.Undo();
                case TimelineEditAction.Redo:
                    return _session.Redo();
                case TimelineEditAction.Move when request.Scope == TimelineMoveScope.Activity:
                    return Select(request, _session.MoveActivity(request.ActivityKey, request.DayDelta));
                case TimelineEditAction.Move:
                    if (segment == null || request.TargetDate == null) return Refused(NoDayMessage);
                    return Select(request, _session.MoveDailyTail(request.ActivityKey, segment.FragmentId, Day(request.TargetDate.Value)));
                case TimelineEditAction.DeferToNextPeriod:
                    if (segment == null) return Refused(NoDayMessage);
                    return Select(request, _session.DeferDailyTail(request.ActivityKey, segment.FragmentId));
                case TimelineEditAction.SplitAndStagger:
                    if (segment == null) return Refused(NoDayMessage);
                    return Select(request, _session.SplitAndStagger(request.ActivityKey, segment.FragmentId, request.Divisions, request.GapDays));
                case TimelineEditAction.ToggleCapacityExemption:
                    if (segment == null) return Refused(NoDayMessage);
                    return _session.SetCapacityExemption(segment.LogicalUnitId, !_session.IsCapacityExempt(segment.LogicalUnitId));
                case TimelineEditAction.AssignResource:
                    if (segment == null) return Refused(NoDayMessage);
                    if (_pickResource == null) return null;
                    var picked = _pickResource(new ResourcePickRequest
                    {
                        ActivityLabel = _session.ActivityLabel(request.ActivityKey),
                        DateText = request.Date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? segment.Date,
                        ActivityResource = _session.ActivityResource(request.ActivityKey),
                        Current = _session.AssignedResource(segment.LogicalUnitId),
                        Options = _session.ResourceOptions,
                    });
                    return picked == null ? null : _session.AssignResource(segment.LogicalUnitId, picked.Resource);
                default:
                    return Refused("Acción no disponible.");
            }
        }

        /// <summary>
        /// BlockMenuOpening: shows "Exentar Límite" / "Quitar exención" for days of activities with a VGS type (the
        /// browser's rule), and hides "Asignar equipo" without a picker.
        /// </summary>
        public void PrepareMenu(string activityKey, object tag, IList<TimelineMenuItem> items)
        {
            if (items == null) return;
            var segment = tag as DaySegment;
            foreach (var item in items)
            {
                if (item.Action == TimelineEditAction.ToggleCapacityExemption)
                {
                    bool show = segment != null && segment.Status != SegmentStatus.Completed && _session.IsCapacityExemptible(activityKey);
                    item.IsVisible = show;
                    if (show) item.Header = _session.IsCapacityExempt(segment.LogicalUnitId) ? RemoveExemptionHeader : ExemptHeader;
                }
                else if (item.Action == TimelineEditAction.AssignResource)
                {
                    item.IsVisible = _pickResource != null;
                }

                if (!IsEditable)
                {
                    item.IsEnabled = false;
                    item.DisabledReason = PlannerMessages.Busy;
                }
            }
        }

        private PlannerOutcome Select(PlannerEditRequest request, PlannerOutcome outcome)
        {
            // The edited activity becomes the selected one, so the side panel shows what just moved.
            if (outcome.Ok && !string.IsNullOrEmpty(request.ActivityKey) && _session.SelectedActivityId != request.ActivityKey)
                _session.SelectedActivityId = request.ActivityKey;
            return outcome;
        }

        private static PlannerOutcome Refused(string message) => new PlannerOutcome { Ok = false, Message = message, Code = "refused", Detail = string.Empty };

        private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private void OnSessionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PlannerSession.IsBusy)) IsEditable = !_session.IsBusy;
        }
    }

    /// <summary>The picker's answer: <see cref="Resource"/> null = back to the activity's own equipment.</summary>
    public sealed class PickedResource
    {
        public PickedResource(string resource)
        {
            Resource = resource;
        }

        public string Resource { get; }
    }
}
