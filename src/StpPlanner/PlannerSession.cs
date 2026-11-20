using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Maptek.Vulcan.Stp.Engine;
using WireApplyPackage = Maptek.Vulcan.Stp.Bridge.Contracts.ApplyPackage;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>
    /// The planner screen's view model (docs/mockups/planner_layout.html): one snapshot, its engine state
    /// (<see cref="CivilHorizonController"/>), filters, undo/redo, the apply package and the live bridge.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: call it from the UI thread. The async commands do not use <c>ConfigureAwait(false)</c>, so their
    /// continuations (which change bound state) run back on the caller's context. Every change raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/> for the affected properties.
    /// </remarks>
    public sealed partial class PlannerSession : ObservableObject, IDisposable
    {
        private static readonly string[] ViewProperties =
        {
            nameof(State), nameof(Horizon), nameof(RangeStart), nameof(RangeFinish), nameof(DurationDays), nameof(CanEditRange), nameof(RangeLockedReason),
            nameof(View), nameof(Days), nameof(TypeChips), nameof(Groups), nameof(Rows), nameof(Counters), nameof(Capacity), nameof(Selected),
            nameof(PlanTotals), nameof(PlanTotalText), nameof(PendingChangeCount), nameof(PendingChangesText), nameof(HasPendingChanges),
            nameof(CanUndo), nameof(CanRedo), nameof(UndoDescription), nameof(RedoDescription), nameof(CanDiscard),
            nameof(CanApply), nameof(ApplyBlockedReason), nameof(IsPackageReady), nameof(CanRefresh), nameof(IsBusy),
            nameof(IsSnapshotOutOfDate), nameof(ConflictAssessment), nameof(ConflictNotice), nameof(Snapshot), nameof(SnapshotText),
            nameof(ProjectName), nameof(HasPending),
        };

        private readonly IHorizonStorage storage;
        private readonly IPlannerBridge bridge;
        private readonly PlannerOptions options;
        private readonly Dictionary<string, double> objectives;
        private readonly HashSet<string> hiddenTypes = new HashSet<string>(StringComparer.Ordinal);

        private PlannerModel baseModel;
        private IReadOnlyDictionary<string, ActivityInfo> catalog;
        private IReadOnlyDictionary<string, string> typeColors = new Dictionary<string, string>();
        private LocalOverrides overrides = new LocalOverrides();
        private CivilHorizonController controller;
        private PlanHistory history;
        private CivilHorizon appliedSeed;
        private IReadOnlyList<PlanOperation> appliedOperations;
        private PlannerView view = new PlannerView();
        private string readyDigest;
        private bool busy;
        private bool outOfDate;
        private bool readyInvalidated;
        private string statusMessage = string.Empty;
        private bool statusIsError;
        private PlannerGrouping grouping = PlannerGrouping.Type;
        private PlannerSort sort = PlannerSort.Start;
        private string sortAttribute = string.Empty;
        private bool descending;
        private string search = string.Empty;
        private bool showCompleted = true;
        private bool showPrecedences = true;
        private string selectedActivityId;

        /// <summary>Initializes a new instance of the <see cref="PlannerSession"/> class.</summary>
        /// <param name="snapshot">The snapshot.</param>
        /// <param name="bridgeConfig">The bridge config (live or static).</param>
        /// <param name="bridge">The bridge, or <c>null</c> when not live.</param>
        /// <param name="storage">Horizon preference storage.</param>
        /// <param name="options">Options, or defaults.</param>
        /// <param name="initialHorizon">Optional seed horizon (overrides the stored preference).</param>
        /// <exception cref="EngineException">The snapshot breaks a planner rule (show its message).</exception>
        public PlannerSession(SnapshotV2 snapshot, BridgeConfig bridgeConfig, IPlannerBridge bridge, IHorizonStorage storage, PlannerOptions options = null, CivilHorizon initialHorizon = null)
        {
            BridgeConfig = bridgeConfig ?? throw new ArgumentNullException(nameof(bridgeConfig));
            this.bridge = bridgeConfig.IsLive ? bridge : null;
            this.storage = storage ?? new MemoryHorizonStorage();
            this.options = options ?? new PlannerOptions();
            objectives = new Dictionary<string, double>(this.options.TypeObjectives.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
            Load(snapshot ?? throw new ArgumentNullException(nameof(snapshot)), initialHorizon);
            string notice = ConflictNotice;
            SetStatus(notice.Length > 0 ? notice : (controller.State().Status ?? string.Empty), ConflictAssessment.BlocksOperations);
        }

        /// <summary>Opens the session script 2 wrote in plugin mode.</summary>
        /// <param name="plugin">The plugin session.</param>
        /// <param name="storage">Horizon preference storage.</param>
        /// <param name="options">Options.</param>
        /// <returns>The session (owns its bridge client; dispose it).</returns>
        public static PlannerSession Open(PluginSession plugin, IHorizonStorage storage, PlannerOptions options = null)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            var snapshot = plugin.LoadSnapshot();
            var gateway = plugin.BridgeConfig.IsLive ? new StpBridgeGateway(new StpBridgeClient(plugin.BridgeConfig)) : null;
            try
            {
                return new PlannerSession(snapshot, plugin.BridgeConfig, gateway, storage, options);
            }
            catch
            {
                gateway?.Dispose();
                throw;
            }
        }

        // ------------------------------------------------------------------ identity and status bar

        /// <summary>Gets the bridge config.</summary>
        public BridgeConfig BridgeConfig { get; }

        /// <summary>Gets a value indicating whether the bridge is live ("En vivo").</summary>
        public bool IsLive => BridgeConfig.IsLive && bridge != null;

        /// <summary>Gets the connection chip text.</summary>
        public string ConnectionText => IsLive ? "En vivo" : "Sin conexión";

        /// <summary>Gets the current snapshot.</summary>
        public SnapshotV2 Snapshot { get; private set; }

        /// <summary>Gets the project name.</summary>
        public string ProjectName => Snapshot.ProjectName;

        /// <summary>Gets the status bar snapshot text, e.g. <c>Instantánea 21/11/2026 08:00</c>.</summary>
        public string SnapshotText => "Instantánea " + Snapshot.ExportedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        /// <summary>Gets the effective precedence conflict policy.</summary>
        public string PrecedencePolicy => BridgeConfig.EffectivePrecedenceConflictPolicy;

        /// <summary>Gets the status bar policy text.</summary>
        public string PolicyText => PlannerMessages.PolicyLabel(PrecedencePolicy);

        /// <summary>Gets the inherited precedence conflicts of the current snapshot.</summary>
        public PrecedenceConflictAssessment ConflictAssessment { get; private set; }

        /// <summary>Gets the conflict notice, or empty.</summary>
        public string ConflictNotice => PlannerMessages.ConflictNotice(ConflictAssessment);

        /// <summary>Gets the last user message.</summary>
        public string StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }

        /// <summary>Gets a value indicating whether <see cref="StatusMessage"/> is an error.</summary>
        public bool StatusIsError { get => statusIsError; private set => Set(ref statusIsError, value); }

        /// <summary>Gets a value indicating whether a bridge call is running.</summary>
        public bool IsBusy => busy;

        /// <summary>Gets a value indicating whether VGS changed since this snapshot (a 409 was seen).</summary>
        public bool IsSnapshotOutOfDate => outOfDate;

        // ------------------------------------------------------------------ engine state and horizon

        /// <summary>Gets the engine state.</summary>
        public ControllerState State { get; private set; }

        /// <summary>Gets a value indicating whether any work is pending.</summary>
        public bool HasPending => State.HasPending;

        /// <summary>Gets the horizon, or <c>null</c> when nothing is pending.</summary>
        public CivilHorizon Horizon => State.Horizon;

        /// <summary>Gets the first visible day <c>YYYY-MM-DD</c>, or empty.</summary>
        public string RangeStart => State.DisplayHorizon?.Start ?? string.Empty;

        /// <summary>Gets the last visible day <c>YYYY-MM-DD</c> (inclusive), or empty.</summary>
        public string RangeFinish => State.DisplayHorizon?.Finish ?? string.Empty;

        /// <summary>Gets the horizon length in days, or 0.</summary>
        public int DurationDays => State.DisplayHorizon?.DurationDays ?? 0;

        /// <summary>Gets a value indicating whether the range can be edited (no local moves).</summary>
        public bool CanEditRange => !busy && !State.HorizonControlsDisabled && !State.HorizonLocked;

        /// <summary>Gets why the range cannot be edited, or empty.</summary>
        public string RangeLockedReason => State.HorizonControlsDisabled ? "No hay trabajo pendiente para planificar."
            : State.HorizonLocked ? "El rango no se puede cambiar mientras hay movimientos locales." : string.Empty;

        // ------------------------------------------------------------------ filters

        /// <summary>Gets or sets the grouping.</summary>
        public PlannerGrouping Grouping { get => grouping; set { if (Set(ref grouping, value)) RefreshView(); } }

        /// <summary>Gets or sets the sort.</summary>
        public PlannerSort Sort { get => sort; set { if (Set(ref sort, value)) RefreshView(); } }

        /// <summary>Gets or sets the grid column for <see cref="PlannerSort.Attribute"/>.</summary>
        public string SortAttribute { get => sortAttribute; set { if (Set(ref sortAttribute, value ?? string.Empty)) RefreshView(); } }

        /// <summary>Gets or sets a value indicating whether the sort is descending.</summary>
        public bool SortDescending { get => descending; set { if (Set(ref descending, value)) RefreshView(); } }

        /// <summary>Gets or sets the search text.</summary>
        public string SearchText { get => search; set { if (Set(ref search, value ?? string.Empty)) RefreshView(); } }

        /// <summary>Gets or sets a value indicating whether completed work is shown.</summary>
        public bool ShowCompleted { get => showCompleted; set { if (Set(ref showCompleted, value)) RefreshView(); } }

        /// <summary>Gets or sets a value indicating whether precedence lines are drawn (view toggle only).</summary>
        public bool ShowPrecedences { get => showPrecedences; set => Set(ref showPrecedences, value); }

        /// <summary>Gets the grid columns usable for <see cref="PlannerSort.Attribute"/> (first-seen order).</summary>
        public IReadOnlyList<string> Attributes { get; private set; }

        /// <summary>Gets or sets the selected activity id.</summary>
        public string SelectedActivityId
        {
            get => selectedActivityId;
            set { if (Set(ref selectedActivityId, string.IsNullOrEmpty(value) ? null : value)) RefreshView(); }
        }

        /// <summary>Shows or hides one type (chip click).</summary>
        /// <param name="type">The type.</param>
        public void ToggleType(string type) => SetTypeVisible(type, hiddenTypes.Contains(type));

        /// <summary>Shows or hides one type.</summary>
        /// <param name="type">The type.</param>
        /// <param name="visible">Whether rows of the type are shown.</param>
        public void SetTypeVisible(string type, bool visible)
        {
            if (type == null) return;
            if (visible ? hiddenTypes.Remove(type) : hiddenTypes.Add(type)) RefreshView();
        }

        /// <summary>Shows every type.</summary>
        public void ShowAllTypes()
        {
            if (hiddenTypes.Count == 0) return;
            hiddenTypes.Clear();
            RefreshView();
        }

        /// <summary>Gets the colour (<c>#RRGGBB</c>) of every type of the snapshot (<see cref="TypePalette"/>).</summary>
        public IReadOnlyDictionary<string, string> TypeColors => typeColors;

        /// <summary>The colour of one type.</summary>
        /// <param name="type">The type.</param>
        /// <returns><c>#RRGGBB</c>.</returns>
        public string TypeColor(string type) => PlannerProjection.ColorOf(typeColors, type);

        /// <summary>Gets the daily objective per type.</summary>
        public IReadOnlyDictionary<string, double> TypeObjectives => objectives;

        /// <summary>Sets the daily objective of a type (0 removes it).</summary>
        /// <param name="type">The type.</param>
        /// <param name="objective">The objective.</param>
        public void SetTypeObjective(string type, double objective)
        {
            if (type == null) return;
            if (objective > 0 && !double.IsInfinity(objective)) objectives[type] = objective;
            else objectives.Remove(type);
            OnPropertyChanged(nameof(TypeObjectives));
            RefreshView();
        }

        // ------------------------------------------------------------------ projection

        /// <summary>Gets the whole projection.</summary>
        public PlannerView View => view;

        /// <summary>Gets the visible days.</summary>
        public IReadOnlyList<HorizonDay> Days => view.Days;

        /// <summary>Gets the type chips.</summary>
        public IReadOnlyList<TypeChip> TypeChips => view.TypeChips;

        /// <summary>Gets the row groups.</summary>
        public IReadOnlyList<PlannerRowGroup> Groups => view.Groups;

        /// <summary>Gets the rows in display order.</summary>
        public IReadOnlyList<PlannerRow> Rows => view.Rows;

        /// <summary>Gets the counters.</summary>
        public PlannerCounters Counters => view.Counters;

        /// <summary>Gets the capacity rows.</summary>
        public IReadOnlyList<CapacityRow> Capacity => view.Capacity;

        /// <summary>Gets the selected activity's details, or <c>null</c>.</summary>
        public ActivityDetails Selected => view.Selected;

        /// <summary>Gets the plan totals.</summary>
        public IReadOnlyList<PlanTotal> PlanTotals => view.PlanTotals;

        /// <summary>Gets the status bar plan text, e.g. <c>Plan total 26,150 t · 1,434 m</c>, or empty.</summary>
        public string PlanTotalText => view.PlanTotals.Count == 0 ? string.Empty
            : "Plan total " + string.Join(" · ", view.PlanTotals.Select(t => PlannerProjection.QuantityText(t.Quantity, t.Unit)));

        /// <summary>Gets the number of activities with local movements.</summary>
        public int PendingChangeCount => view.ChangedActivities;

        /// <summary>Gets the status bar text ("5 cambios sin aplicar a VGS").</summary>
        public string PendingChangesText => PlannerMessages.PendingChanges(view.ChangedActivities);

        /// <summary>Gets a value indicating whether there are local movements.</summary>
        public bool HasPendingChanges => State.HasLocalMovements;

        /// <summary>Tail-move eligibility of a fragment (throws for unknown ids, like the engine).</summary>
        /// <param name="fragmentId">The fragment.</param>
        /// <returns>The eligibility.</returns>
        public FragmentEligibility FragmentEligibility(string fragmentId) => controller.FragmentEligibility(fragmentId);

        /// <summary>±1 day eligibility of an activity.</summary>
        /// <param name="activityId">The activity.</param>
        /// <returns>The eligibility.</returns>
        public ActivityEligibility ActivityEligibility(string activityId) => controller.ActivityEligibility(activityId);

        /// <summary>The display name of an activity (<c>id name</c>), for messages.</summary>
        /// <param name="activityId">The activity.</param>
        /// <returns>The label.</returns>
        public string ActivityLabel(string activityId) =>
            catalog.TryGetValue(activityId ?? string.Empty, out var info) && !info.Name.StartsWith("Actividad ", StringComparison.Ordinal)
                ? $"{activityId} {info.Name}" : activityId;

        // ------------------------------------------------------------------ plan commands

        /// <summary>Moves a whole activity by whole days.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="days">Non-zero shift.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome MoveActivity(string activityId, long days) => Execute(PlanOperation.MoveActivity(activityId, days));

        /// <summary>Moves the selected activity by whole days.</summary>
        /// <param name="days">Non-zero shift.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome MoveSelected(long days) => selectedActivityId == null
            ? Report(PlannerOutcome.Failure("Seleccione una actividad.", OperationResult.Codes.InvalidMove))
            : MoveActivity(selectedActivityId, days);

        /// <summary>Moves a day of an activity and the rest of its tail to a visible day.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day.</param>
        /// <param name="targetDate">Target day <c>YYYY-MM-DD</c>.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome MoveDailyTail(string activityId, string fragmentId, string targetDate) => Execute(PlanOperation.MoveDailyTail(activityId, fragmentId, targetDate));

        /// <summary>Moves a day and the rest of the tail to the first day after the horizon.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome DeferDailyTail(string activityId, string fragmentId) => Execute(PlanOperation.DeferDailyTail(activityId, fragmentId));

        /// <summary>Splits after a day and staggers the rest: <paramref name="divisions"/> gaps of <paramref name="gapDays"/> days.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="fragmentId">A fragment of the day after which gaps are inserted.</param>
        /// <param name="divisions">Number of gaps.</param>
        /// <param name="gapDays">Days per gap.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome SplitAndStagger(string activityId, string fragmentId, long divisions, long gapDays) => Execute(PlanOperation.Stagger(activityId, fragmentId, divisions, gapDays));

        /// <summary>Sets the first visible day (keeps the duration).</summary>
        /// <param name="day"><c>YYYY-MM-DD</c>.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome SetRangeStart(string day) => Execute(PlanOperation.EditStart(day));

        /// <summary>Sets the last visible day.</summary>
        /// <param name="day"><c>YYYY-MM-DD</c>.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome SetRangeFinish(string day) => Execute(PlanOperation.EditFinish(day));

        /// <summary>Sets the horizon length.</summary>
        /// <param name="days">1-366.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome SetDuration(long days) => Execute(PlanOperation.EditDuration(days));

        /// <summary>Saves the current horizon as this project's preference.</summary>
        /// <returns>The outcome.</returns>
        public PlannerOutcome SaveRangePreference()
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            return Report(FromEngine(controller.SavePreference()));
        }

        /// <summary>Gets a value indicating whether there is something to undo.</summary>
        public bool CanUndo => !busy && history.CanUndo;

        /// <summary>Gets a value indicating whether there is something to redo.</summary>
        public bool CanRedo => !busy && history.CanRedo;

        /// <summary>Gets what undo would revert.</summary>
        public string UndoDescription => history.UndoDescription;

        /// <summary>Gets what redo would repeat.</summary>
        public string RedoDescription => history.RedoDescription;

        /// <summary>Gets a value indicating whether there are local movements to discard.</summary>
        public bool CanDiscard => !busy && State.HasLocalMovements;

        /// <summary>Reverts the last accepted command.</summary>
        /// <returns>The outcome.</returns>
        public PlannerOutcome Undo()
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            if (!history.CanUndo) return Report(PlannerOutcome.Failure(PlannerMessages.NothingToUndo));
            var saved = Rebuild(history.Undo());
            return Report(saved.Ok ? PlannerOutcome.Success(PlannerMessages.Undone) : saved);
        }

        /// <summary>Repeats the last undone command.</summary>
        /// <returns>The outcome.</returns>
        public PlannerOutcome Redo()
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            if (!history.CanRedo) return Report(PlannerOutcome.Failure(PlannerMessages.NothingToRedo));
            var saved = Rebuild(history.Redo());
            return Report(saved.Ok ? PlannerOutcome.Success(PlannerMessages.Redone) : saved);
        }

        /// <summary>Back to the snapshot ("Descartar", JS <c>reset</c>); undoable.</summary>
        /// <returns>The outcome.</returns>
        public PlannerOutcome Discard()
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            var result = controller.Reset();
            history.Push(new PlanState(controller.State().Horizon, Array.Empty<PlanOperation>(), "Descartar cambios", history.Current.Resources));
            appliedSeed = history.Current.Seed;
            appliedOperations = history.Current.Operations;
            RefreshAll();
            return Report(FromEngine(result));
        }

        // ------------------------------------------------------------------ local day settings (menu)

        /// <summary>
        /// Gets the equipment the "Asignar equipo" picker and the Recurso selector offer: the project's VGS resources,
        /// sorted, then the manual resources (JS <c>combinedResourceOptions</c>).
        /// </summary>
        public IReadOnlyList<string> ResourceOptions { get; private set; } = Array.Empty<string>();

        /// <summary>Gets the same list with ids and kind (Configuración → Frentes y recursos).</summary>
        public IReadOnlyList<ResourceDescriptor> ResourceDescriptors { get; private set; } = Array.Empty<ResourceDescriptor>();

        /// <summary>Gets the manual resources ("+ Recurso manual").</summary>
        public IReadOnlyList<ManualResource> ManualResources => overrides.ManualResources;

        /// <summary>Gets the resource the last <see cref="AddManualResource"/> created, or <c>null</c>.</summary>
        public ManualResource LastAddedManualResource { get; private set; }

        /// <summary>The logical unit of a fragment (the key of its local equipment and exemption), or <c>null</c>.</summary>
        /// <param name="fragmentId">The fragment.</param>
        /// <returns><c>activityId:logicalUnitDate</c>.</returns>
        public string LogicalUnitOf(string fragmentId)
        {
            var fragment = State.Model.Fragments.FirstOrDefault(f => f.Id == fragmentId);
            return fragment == null ? null : LocalOverrides.LogicalUnitId(fragment.ActivityId, fragment.LogicalUnitDate);
        }

        /// <summary>
        /// The activity's own equipment (the default of its days): the one assigned to the whole activity, else its VGS
        /// resource; <c>null</c> for an unknown activity.
        /// </summary>
        /// <param name="activityId">The activity.</param>
        /// <returns>The equipment.</returns>
        public string ActivityResource(string activityId) =>
            activityId != null && catalog.TryGetValue(activityId, out var info) ? overrides.ActivityResourceOf(activityId) ?? info.Resource : null;

        /// <summary>The activity's VGS resource, or <c>null</c> for an unknown activity.</summary>
        /// <param name="activityId">The activity.</param>
        /// <returns>The equipment.</returns>
        public string VgsResource(string activityId) => activityId != null && catalog.TryGetValue(activityId, out var info) ? info.Resource : null;

        /// <summary>The equipment assigned locally to a logical unit, or <c>null</c> (the activity's own).</summary>
        /// <param name="logicalUnitId">The logical unit.</param>
        /// <returns>The equipment.</returns>
        public string AssignedResource(string logicalUnitId) => overrides.ResourceOf(logicalUnitId);

        /// <summary>Whether a logical unit is exempt from the capacity limit.</summary>
        /// <param name="logicalUnitId">The logical unit.</param>
        /// <returns>Exempt.</returns>
        public bool IsCapacityExempt(string logicalUnitId) => overrides.IsExempt(logicalUnitId);

        /// <summary>Whether an activity's days can be exempted (it has a VGS type: template <c>capacityExemptible</c>).</summary>
        /// <param name="activityId">The activity.</param>
        /// <returns>Exemptible.</returns>
        public bool IsCapacityExemptible(string activityId) => activityId != null && catalog.TryGetValue(activityId, out var info) && info.HasType;

        /// <summary>
        /// "Asignar equipo": the equipment of one pending day (template <c>setFragmentResourceOverride</c>). Local only,
        /// saved per project, never sent to VGS; undoable.
        /// </summary>
        /// <param name="logicalUnitId">The day (<see cref="DaySegment.LogicalUnitId"/>).</param>
        /// <param name="resource">One of <see cref="ResourceOptions"/>, or <c>null</c>/empty for the activity's own.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome AssignResource(string logicalUnitId, string resource)
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            if (!IsPendingUnit(logicalUnitId, out var activity)) return Report(PlannerOutcome.Failure(PlannerMessages.DayNotEditable, "invalid"));
            if (!string.IsNullOrEmpty(resource) && !ResourceOptions.Contains(resource)) return Report(PlannerOutcome.Failure(PlannerMessages.UnknownResource, "invalid"));
            if (resource == ActivityResource(activity)) resource = null;   // the activity's own = no override
            bool cleared = string.IsNullOrEmpty(resource);
            return ChangeResources(overrides.ResourceState.WithDay(logicalUnitId, resource),
                cleared ? $"Quitar el equipo asignado a {logicalUnitId}" : $"Asignar {resource} a {logicalUnitId}",
                cleared ? PlannerMessages.ResourceCleared : PlannerMessages.ResourceAssigned);
        }

        /// <summary>
        /// The Recurso selector of "Actividad seleccionada": the equipment of the whole activity (template
        /// <c>setActivityResourceOverride</c>); a day's own assignment still wins. Local only; undoable.
        /// </summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="resource">One of <see cref="ResourceOptions"/>; <c>null</c>/empty or its VGS resource clears it.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome AssignActivityResource(string activityId, string resource)
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            if (activityId == null || !catalog.TryGetValue(activityId, out var info)) return Report(PlannerOutcome.Failure(PlannerMessages.UnknownActivity, "invalid"));
            if (!string.IsNullOrEmpty(resource) && !ResourceOptions.Contains(resource)) return Report(PlannerOutcome.Failure(PlannerMessages.UnknownResource, "invalid"));
            if (resource == info.Resource) resource = null;
            bool cleared = string.IsNullOrEmpty(resource);
            return ChangeResources(overrides.ResourceState.WithActivity(activityId, resource),
                cleared ? $"Devolver {activityId} a su recurso de VGS" : $"Asignar {resource} a {activityId}",
                cleared ? PlannerMessages.ActivityResourceCleared : PlannerMessages.ActivityResourceAssigned);
        }

        /// <summary>
        /// "+ Recurso manual" (template <c>addManualResource</c>): adds "Equipo manual &lt;n&gt;"; with
        /// <paramref name="assignToActivityId"/> it also becomes that activity's equipment, in the same undo step.
        /// </summary>
        /// <param name="assignToActivityId">An activity to assign it to, or <c>null</c>.</param>
        /// <returns>The outcome; <see cref="LastAddedManualResource"/> is the new resource.</returns>
        public PlannerOutcome AddManualResource(string assignToActivityId = null)
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            if (assignToActivityId != null && !catalog.ContainsKey(assignToActivityId)) return Report(PlannerOutcome.Failure(PlannerMessages.UnknownActivity, "invalid"));
            var list = Planner.ManualResources.Add(overrides.ManualResources, VgsResourceNames(), out var added);
            var next = overrides.ResourceState.WithManual(list);
            if (assignToActivityId != null) next = next.WithActivity(assignToActivityId, added.Name);
            LastAddedManualResource = added;
            return ChangeResources(next, $"Agregar recurso manual {added.Name}", PlannerMessages.ManualResourceAdded);
        }

        /// <summary>
        /// Renames a manual resource (template <c>renameManualResource</c>): the trimmed name must be non-blank, at most
        /// 160 characters and unique, case-insensitively, among VGS and manual resources. Its assignments follow. Undoable.
        /// </summary>
        /// <param name="id">The manual resource (<c>manual:&lt;n&gt;</c>).</param>
        /// <param name="name">The new name.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome RenameManualResource(string id, string name)
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            var current = overrides.ManualResources.FirstOrDefault(r => r.Id == id);
            if (current == null) return Report(PlannerOutcome.Failure(PlannerMessages.UnknownManualResource, "invalid"));
            var list = Planner.ManualResources.Rename(overrides.ManualResources, id, name, VgsResourceNames());
            if (list == null)
            {
                OnPropertyChanged(nameof(ManualResources));   // puts an editor back to the stored name
                return Report(PlannerOutcome.Failure(PlannerMessages.ManualResourceNameInvalid, "invalid"));
            }

            string renamed = list.First(r => r.Id == id).Name;
            return ChangeResources(overrides.ResourceState.WithManual(list).Renamed(current.Name, renamed),
                $"Renombrar {current.Name} a {renamed}", PlannerMessages.ManualResourceRenamed);
        }

        /// <summary>
        /// Removes a manual resource (template <c>removeManualResource</c>); the days and activities assigned to it go
        /// back to their own equipment. Undoable.
        /// </summary>
        /// <param name="id">The manual resource.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome RemoveManualResource(string id)
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            var list = Planner.ManualResources.Remove(overrides.ManualResources, id, out var removed);
            if (removed == null) return Report(PlannerOutcome.Failure(PlannerMessages.UnknownManualResource, "invalid"));
            return ChangeResources(overrides.ResourceState.WithManual(list).Without(removed.Name),
                $"Quitar recurso manual {removed.Name}", PlannerMessages.ManualResourceRemoved);
        }

        /// <summary>
        /// "Exentar Límite" / "Quitar exención": the day's quantity no longer counts against its type's capacity
        /// objective (template <c>setFragmentCapacityExemption</c>). Local only, saved per project.
        /// </summary>
        /// <param name="logicalUnitId">The day.</param>
        /// <param name="exempt">Exempt or not.</param>
        /// <returns>The outcome.</returns>
        public PlannerOutcome SetCapacityExemption(string logicalUnitId, bool exempt)
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            if (!IsPendingUnit(logicalUnitId, out var activity)) return Report(PlannerOutcome.Failure(PlannerMessages.DayNotEditable, "invalid"));
            if (exempt && !IsCapacityExemptible(activity)) return Report(PlannerOutcome.Failure(PlannerMessages.NotExemptible, "invalid"));
            return ChangeOverrides(() => overrides.SetExempt(logicalUnitId, exempt), exempt ? PlannerMessages.ExemptionSet : PlannerMessages.ExemptionCleared);
        }

        private bool IsPendingUnit(string logicalUnitId, out string activityId)
        {
            activityId = null;
            if (string.IsNullOrEmpty(logicalUnitId)) return false;
            var fragment = State.Model.Fragments.FirstOrDefault(f => !f.LockedCompleted && LocalOverrides.LogicalUnitId(f.ActivityId, f.LogicalUnitDate) == logicalUnitId);
            activityId = fragment?.ActivityId;
            return fragment != null;
        }

        private IReadOnlyCollection<string> VgsResourceNames() => ResourceDescriptors.Where(d => !d.IsManual).Select(d => d.Name).ToList();

        // An equipment edit is a plan step: the new settings go on the undo stack with the current plan.
        private PlannerOutcome ChangeResources(ResourceState next, string description, string message)
        {
            if (next.SameAs(overrides.ResourceState)) return Report(PlannerOutcome.Success(message));
            history.Push(history.Current.With(next, description));
            return ApplyResources(next, message);
        }

        // Puts equipment settings in place and saves them (RefreshView included); reports only a storage failure.
        private PlannerOutcome ApplyResources(ResourceState next, string message)
        {
            bool manualChanged = !next.Manual.SequenceEqual(overrides.ManualResources);
            overrides.SetResourceState(next);
            if (manualChanged) UpdateResourceOptions();
            return ChangeOverrides(() => true, message);
        }

        private void UpdateResourceOptions()
        {
            var vgs = catalog.Values.Select(a => a.Resource).Distinct(StringComparer.Ordinal)
                .OrderBy(r => r, StringComparer.Create(CultureInfo.GetCultureInfo("es-MX"), true)).ToList();
            ResourceDescriptors = Planner.ManualResources.Options(vgs, overrides.ManualResources);
            ResourceOptions = ResourceDescriptors.Select(d => d.Name).ToList();
            OnPropertyChanged(nameof(ResourceDescriptors));
            OnPropertyChanged(nameof(ResourceOptions));
            OnPropertyChanged(nameof(ManualResources));
        }

        private PlannerOutcome ChangeOverrides(Func<bool> change, string message)
        {
            if (change())
            {
                try
                {
                    overrides.Save(storage, Snapshot.ProjectKey);
                }
                catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException)
                {
                    RefreshView();
                    return Report(PlannerOutcome.Failure(PlannerMessages.OverridesNotSaved + error.Message, "storage", error.Message));
                }

                RefreshView();
            }

            return Report(PlannerOutcome.Success(message));
        }

        // ------------------------------------------------------------------ VGS

        /// <summary>Gets a value indicating whether "Aplicar a VGS" is enabled.</summary>
        public bool CanApply => ApplyBlockedReason.Length == 0;

        /// <summary>Gets why "Aplicar a VGS" is disabled (template <c>syncApplyPackageControl</c>), or empty.</summary>
        public string ApplyBlockedReason
        {
            get
            {
                if (busy) return PlannerMessages.Busy;
                if (outOfDate) return PlannerMessages.SnapshotStale;
                if (!State.CanDownload) return PlannerMessages.NoLocalMoves;
                if (ConflictAssessment.BlocksOperations) return PlannerMessages.InheritedConflictsBlock;
                if (!IsLive) return PlannerMessages.NotLive;
                return string.Empty;
            }
        }

        /// <summary>Gets a value indicating whether the bridge saved the package of the current plan.</summary>
        public bool IsPackageReady => readyDigest != null;

        /// <summary>Gets the last receipt, or <c>null</c>.</summary>
        public ApplyPackageSaved LastReceipt { get; private set; }

        /// <summary>Gets a value indicating whether "Actualizar" is enabled.</summary>
        public bool CanRefresh => !busy && IsLive;

        /// <summary>The apply package of the current plan (engine rules + wire mapping).</summary>
        /// <returns>The package.</returns>
        /// <exception cref="EngineException">No change, or the plan fails the export checks.</exception>
        public WireApplyPackage BuildApplyPackage() =>
            SnapshotMapper.ToWire(PlannerLogic.BuildVgsApplyPackage(State.Model, SnapshotMapper.Source(Snapshot), State.Horizon));

        /// <summary>
        /// "Aplicar a VGS": builds the package and has the bridge validate and save it for script 3
        /// (template <c>prepareApplyPackage</c>). VGS itself is not changed.
        /// </summary>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The outcome.</returns>
        public async Task<PlannerOutcome> ApplyToVgsAsync(CancellationToken cancellationToken = default)
        {
            string blocked = ApplyBlockedReason;
            if (blocked.Length > 0) return Report(PlannerOutcome.Failure(blocked, "blocked"));
            WireApplyPackage package;
            try
            {
                package = BuildApplyPackage();
            }
            catch (EngineException error)
            {
                return Report(PlannerOutcome.Failure(error.Message, "package", error.Message));
            }
            catch (WireFormatException error)
            {
                return Report(PlannerOutcome.Failure(PlannerMessages.PackageNotPersisted, "package", error.Message));
            }

            string digest = package.ComputeSha256();
            SetBusy(true);
            PlannerOutcome outcome;
            try
            {
                var receipt = await bridge.SubmitApplyPackageAsync(package, cancellationToken);
                if (CurrentDigest() != digest)
                {
                    outcome = PlannerOutcome.Failure(PlannerMessages.PlanChangedDuringSave, "changed");
                }
                else
                {
                    LastReceipt = receipt;
                    readyDigest = digest;
                    outcome = PlannerOutcome.Success(PlannerMessages.PackageReady);
                }
            }
            catch (StpBridgeException error)
            {
                if (error.Kind == StpBridgeErrorKind.Stale) outOfDate = true;
                outcome = PlannerOutcome.Failure(PlannerMessages.ApplyError(error, ActivityLabel), error.Kind.ToString(), error.Message);
            }
            catch (OperationCanceledException)
            {
                outcome = PlannerOutcome.Failure("Operación cancelada.", "cancelled");
            }
            finally
            {
                SetBusy(false);
            }

            return Report(outcome);
        }

        /// <summary>
        /// "Actualizar": reloads the snapshot from VGS (template <c>refreshFromVgs</c>). Local moves are discarded, after
        /// <paramref name="confirm"/> agrees to <see cref="PlannerMessages.RefreshDiscardsChanges"/>. On failure the
        /// local plan is kept.
        /// </summary>
        /// <param name="confirm">Asks the user; <c>null</c> means yes.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The outcome.</returns>
        public async Task<PlannerOutcome> RefreshFromVgsAsync(Func<string, bool> confirm = null, CancellationToken cancellationToken = default)
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            if (!IsLive) return Report(PlannerOutcome.Failure(PlannerMessages.NotLive, "static"));
            if (State.Model.Fragments.Any(f => f.Provenance == "direct" || f.Provenance == "cascade")
                && confirm != null && !confirm(PlannerMessages.RefreshDiscardsChanges))
            {
                return Report(PlannerOutcome.Failure(PlannerMessages.RefreshCancelled, "cancelled"));
            }

            SetBusy(true);
            PlannerOutcome outcome;
            try
            {
                var snapshot = await bridge.GetSnapshotAsync(Snapshot, cancellationToken);
                Load(snapshot, null);
                outOfDate = false;
                string notice = ConflictNotice;
                outcome = notice.Length > 0
                    ? new PlannerOutcome { Ok = true, Message = notice, Code = ConflictAssessment.BlocksOperations ? "conflicts-blocking" : "conflicts", Detail = string.Empty }
                    : PlannerOutcome.Success(PlannerMessages.Refreshed);
            }
            catch (StpBridgeException error)
            {
                outcome = PlannerOutcome.Failure(PlannerMessages.RefreshError(error), error.Kind.ToString(), error.Message);
            }
            catch (EngineException error)
            {
                outcome = PlannerOutcome.Failure(PlannerMessages.RefreshFailed, "snapshot", error.Message);
            }
            catch (OperationCanceledException)
            {
                outcome = PlannerOutcome.Failure("Operación cancelada.", "cancelled");
            }
            finally
            {
                SetBusy(false);
            }

            return Report(outcome, outcome.Ok && ConflictAssessment.BlocksOperations);
        }

        /// <inheritdoc/>
        public void Dispose() => (bridge as IDisposable)?.Dispose();

        // ------------------------------------------------------------------ internals

        private void Load(SnapshotV2 snapshot, CivilHorizon initialHorizon)
        {
            var model = SnapshotMapper.ToModel(snapshot);
            var next = new CivilHorizonController(() => model, snapshot.ProjectKey, storage, initialHorizon);
            Snapshot = snapshot;
            baseModel = model;
            catalog = ActivityCatalog.Build(snapshot, options);
            typeColors = TypePalette.Assign(catalog.Values.Select(a => a.Type));
            overrides = LocalOverrides.Load(storage, snapshot.ProjectKey, catalog.Values.Select(a => a.Resource).Distinct(StringComparer.Ordinal).ToList());
            Attributes = snapshot.Activities.SelectMany(a => a.GridColumns.Select(c => c.Key)).Distinct(StringComparer.Ordinal).ToList();
            controller = next;
            ConflictAssessment = PlannerLogic.AssessPrecedenceConflicts(model, PrecedencePolicy);
            history = new PlanHistory(new PlanState(next.State().Horizon, Array.Empty<PlanOperation>(), string.Empty, overrides.ResourceState), options.UndoLimit);
            appliedSeed = history.Current.Seed;
            appliedOperations = history.Current.Operations;
            readyDigest = null;
            LastReceipt = null;
            if (selectedActivityId != null && !catalog.ContainsKey(selectedActivityId)) selectedActivityId = null;
            UpdateResourceOptions();
            RefreshAll();
            OnPropertyChanged(nameof(Attributes));
            OnPropertyChanged(nameof(SelectedActivityId));
        }

        private PlannerOutcome Execute(PlanOperation operation)
        {
            if (busy) return Report(PlannerOutcome.Failure(PlannerMessages.Busy));
            var result = operation.Run(controller);
            if (result.Ok)
            {
                history.Push(history.Current.With(operation));
                appliedSeed = history.Current.Seed;
                appliedOperations = history.Current.Operations;
                RefreshAll();
            }

            return Report(FromEngine(result));
        }

        // Restores a plan state: its equipment settings, then its operations (replayed unless they are the ones applied).
        private PlannerOutcome Rebuild(PlanState state)
        {
            var saved = state.Resources.SameAs(overrides.ResourceState) ? PlannerOutcome.Success(string.Empty) : ApplyResources(state.Resources, string.Empty);
            if (!ReferenceEquals(state.Operations, appliedOperations) || !ReferenceEquals(state.Seed, appliedSeed)) Replay(state);
            else RaiseView();
            return saved;
        }

        private void Replay(PlanState state)
        {
            var model = baseModel;
            var next = new CivilHorizonController(() => model, Snapshot.ProjectKey, storage, state.Seed);
            foreach (var operation in state.Operations)
            {
                var result = operation.Run(next);
                if (!result.Ok) throw new InvalidOperationException("replay diverged: " + operation.Description + ": " + result.Message);
            }

            controller = next;
            appliedSeed = state.Seed;
            appliedOperations = state.Operations;
            RefreshAll();
        }

        private static PlannerOutcome FromEngine(OperationResult result) => new PlannerOutcome
        {
            Ok = result.Ok,
            Message = PlannerMessages.Human(result),
            Code = result.Code ?? string.Empty,
            Detail = result.Message ?? string.Empty,
        };

        private PlannerOutcome Report(PlannerOutcome outcome, bool error = false)
        {
            if (readyInvalidated)
            {
                // The saved package no longer matches the plan: that notice wins over the command's own message.
                readyInvalidated = false;
                SetStatus(outcome.Ok ? PlannerMessages.PlanChangedAfterReady : outcome.Message + " " + PlannerMessages.PlanChangedAfterReady, true);
                return outcome;
            }

            SetStatus(outcome.Message, error || !outcome.Ok);
            return outcome;
        }

        private void SetStatus(string message, bool error)
        {
            StatusMessage = message ?? string.Empty;
            StatusIsError = error;
        }

        private void SetBusy(bool value)
        {
            busy = value;
            RaiseView();
        }

        private string CurrentDigest()
        {
            try
            {
                return BuildApplyPackage().ComputeSha256();
            }
            catch (Exception error) when (error is EngineException || error is WireFormatException)
            {
                return null;
            }
        }

        private void RefreshAll()
        {
            State = controller.State();
            if (readyDigest != null && CurrentDigest() != readyDigest)
            {
                readyDigest = null;
                LastReceipt = null;
                readyInvalidated = true;
            }

            RefreshView();
        }

        private void RefreshView()
        {
            view = PlannerProjection.Build(Snapshot, catalog, State, controller.FragmentEligibility, controller.ActivityEligibility, new PlannerFilter
            {
                HiddenTypes = hiddenTypes.ToList(),
                Grouping = grouping,
                Sort = sort,
                SortAttribute = sortAttribute,
                Descending = descending,
                Search = search,
                ShowCompleted = showCompleted,
                SelectedActivityId = selectedActivityId,
                Objectives = objectives,
                TypeColors = typeColors,
                Overrides = overrides,
            });
            RaiseView();
        }

        private void RaiseView()
        {
            foreach (string name in ViewProperties) OnPropertyChanged(name);
        }
    }
}
