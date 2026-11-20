// IPlannerViewModel over StpPlanner's PlannerSession: the command bar, filters, Resumen, capacity, selected activity
// and status bar of docs/mockups/planner_layout.html, plus the timeline adapter (PlannerTimelineModel).
// Call it on the UI thread: PlannerSession is not thread-safe and its async commands resume on the caller's context.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Planner.Export;
using Maptek.Vulcan.Stp.Timeline.Model;
using Res = Maptek.Vulcan.Stp.Resources.Resources;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>What the screen needs from its host (the plugin, or nothing in the preview).</summary>
    public interface IPlannerHost
    {
        /// <summary>Asks a yes/no question.</summary>
        bool Confirm(string text);

        /// <summary>Shows a message.</summary>
        void Notify(string text, bool isError);

        /// <summary>Verificar guardado (script 4); null hides the command.</summary>
        Action VerifySave { get; }

        /// <summary>"Asignar equipo": asks for the day's equipment; null = cancelled.</summary>
        PickedResource PickResource(ResourcePickRequest request);

        /// <summary>Exportar: asks where to save a file (SaveFileDialog); null = cancelled.</summary>
        string PickSaveFile(string title, string filter, string fileName);

        /// <summary>The per-user runtime folder that holds the web resources script 2 copied (STP suelto); null if unknown.</summary>
        string WebResourcesFolder { get; }
    }

    public class PlannerScreenViewModel : ObservableObject, IPlannerViewModel, IDisposable
    {
        private const int ConfirmListLimit = 12;

        private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-MX");

        private readonly IPlannerHost _host;
        private readonly PlannerTimelineModel _timeline;
        private readonly RelayCommand[] _commands;
        private readonly RelayCommand _removeManual;
        private readonly ResourcesConfig _resources;
        private readonly ConfigSection _frontsSection;
        private SelectedActivity _selected;
        private bool _showResources = true;
        private bool _showCapacityPanel = true;
        private bool _exporting;

        public PlannerScreenViewModel(PlannerSession session, IPlannerHost host)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            _host = host;

            var refresh = new RelayCommand(() => Run(RefreshAsync), () => Session.CanRefresh);
            var undo = new RelayCommand(() => Session.Undo(), () => Session.CanUndo);
            var redo = new RelayCommand(() => Session.Redo(), () => Session.CanRedo);
            var discard = new RelayCommand(() => Session.Discard(), () => Session.CanDiscard);
            var apply = new RelayCommand(() => Run(ApplyAsync), () => Session.CanApply);
            var verify = new RelayCommand(() => _host?.VerifySave?.Invoke(), () => _host?.VerifySave != null && !Session.IsBusy);
            var toggle = new RelayCommand(p => Session.ToggleType(p as string), p => p is string);
            var addManual = new RelayCommand(() => AddManualResource(null), () => !Session.IsBusy);
            var removeManual = new RelayCommand(p => Session.RemoveManualResource((p as ResourceItem)?.Id), p => p is ResourceItem item && item.IsManual && !Session.IsBusy);
            _removeManual = removeManual;
            RefreshCommand = refresh;
            UndoCommand = undo;
            RedoCommand = redo;
            DiscardCommand = discard;
            ApplyToVgsCommand = apply;
            VerifySaveCommand = verify;
            ToggleTypeCommand = toggle;
            var excel = new RelayCommand(() => Run(ExportExcelAsync), () => !_exporting && Session.CanExportExcel);
            var pdf = new RelayCommand(ExportPdf, () => !_exporting && !Session.IsBusy && Session.Rows.Count > 0);
            var standalone = new RelayCommand(ExportStandalone, () => !_exporting && !Session.IsBusy);
            ExportExcelCommand = excel;
            ExportPdfCommand = pdf;
            ExportStandaloneCommand = standalone;
            _commands = new[] { refresh, undo, redo, discard, apply, verify, excel, pdf, standalone, addManual, removeManual };

            GroupByOptions = new[] { Res.Planner_GroupType, Res.Planner_GroupLevel, Res.Planner_GroupResource, Res.Planner_GroupNone };
            SortOptions = new[] { Res.Planner_SortStart, Res.Planner_SortId, Res.Planner_SortSource };
            _resources = new ResourcesConfig(addManual);
            _frontsSection = new ConfigSection(Res.Planner_CfgFronts, _resources);
            ConfigSections = new[]
            {
                new ConfigSection(Res.Planner_CfgColors), new ConfigSection(Res.Planner_CfgAttributes),
                new ConfigSection(Res.Planner_CfgReports), _frontsSection,
                new ConfigSection(Res.Planner_CfgPrint), new ConfigSection(Res.Planner_CfgGeneral),
            };
            RebuildResources();

            _timeline = new PlannerTimelineModel(session);
            Editor = new PlannerEditRouter(session, host == null ? (Func<ResourcePickRequest, PickedResource>)null : host.PickResource);
            _selected = BuildSelected(session.Selected);
            Session.PropertyChanged += OnSessionChanged;
        }

        /// <summary>The planner state behind the screen.</summary>
        public PlannerSession Session { get; }

        // ---- Datos -------------------------------------------------------------------------------------------------
        public ICommand RefreshCommand { get; }
        public bool IsLive => Session.IsLive;
        public string ConnectionText => Session.IsLive ? Res.Planner_Live : Res.Planner_Offline;
        public string ConnectionTip => Session.BridgeConfig.Message;

        // ---- Plan --------------------------------------------------------------------------------------------------
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand DiscardCommand { get; }
        public string UndoTip => string.IsNullOrEmpty(Session.UndoDescription) ? Res.Planner_Undo : string.Format(Res.Planner_UndoTip, Session.UndoDescription);
        public string RedoTip => string.IsNullOrEmpty(Session.RedoDescription) ? Res.Planner_Redo : string.Format(Res.Planner_RedoTip, Session.RedoDescription);

        // ---- VGS ---------------------------------------------------------------------------------------------------
        public ICommand ApplyToVgsCommand { get; }
        public string ApplyTip => Session.CanApply ? Res.Planner_ApplyTip : Session.ApplyBlockedReason;
        public ICommand VerifySaveCommand { get; }

        // ---- Exportar: read-only, to a file the user picks (PlannerExports) ------------------------------------------
        public ICommand ExportExcelCommand { get; }
        public string ExportExcelTip => Session.CanExportExcel ? Res.Planner_ExportExcelTip : Session.ExportExcelBlockedReason;
        public ICommand ExportPdfCommand { get; }
        public ICommand ExportStandaloneCommand { get; }

        // ---- Vista -------------------------------------------------------------------------------------------------
        public bool ShowPrecedences { get => Session.ShowPrecedences; set => Session.ShowPrecedences = value; }
        public bool ShowCompleted { get => Session.ShowCompleted; set => Session.ShowCompleted = value; }

        /// <summary>Recurso column of the timeline grid (screen-only setting).</summary>
        public bool ShowResources { get => _showResources; set => Set(ref _showResources, value); }

        public bool ShowCapacityPanel { get => _showCapacityPanel; set => Set(ref _showCapacityPanel, value); }

        // ---- Resumen -----------------------------------------------------------------------------------------------
        public int ActivityCount => Session.Counters.Activities;
        public int PendingCount => Session.Counters.WithPending;
        public int MovedCount => Session.Counters.Moved;
        public int CascadeCount => Session.Counters.Cascaded;

        // ---- Filters -----------------------------------------------------------------------------------------------
        public DateTime? RangeStart
        {
            get => ParseDay(Session.RangeStart);
            set => EditRange(value, RangeStart, d => Session.SetRangeStart(d), nameof(RangeStart));
        }

        public DateTime? RangeEnd
        {
            get => ParseDay(Session.RangeFinish);
            set => EditRange(value, RangeEnd, d => Session.SetRangeFinish(d), nameof(RangeEnd));
        }

        public int RangeDays => Session.DurationDays;
        public bool CanEditRange => Session.CanEditRange;
        public string RangeTip => Session.RangeLockedReason;

        public IReadOnlyList<TypeChip> ActivityTypes => Session.TypeChips;
        public ICommand ToggleTypeCommand { get; }

        public IReadOnlyList<string> GroupByOptions { get; }

        public string GroupBy
        {
            get => GroupByOptions[(int)Session.Grouping];
            set
            {
                int index = Array.IndexOf((string[])GroupByOptions, value);
                if (index >= 0) Session.Grouping = (PlannerGrouping)index;
            }
        }

        public IReadOnlyList<string> SortOptions { get; }

        public string SortBy
        {
            get => Session.Sort == PlannerSort.Id ? SortOptions[1] : Session.Sort == PlannerSort.Source ? SortOptions[2] : SortOptions[0];
            set
            {
                int index = Array.IndexOf((string[])SortOptions, value);
                if (index >= 0) Session.Sort = index == 1 ? PlannerSort.Id : index == 2 ? PlannerSort.Source : PlannerSort.Start;
            }
        }

        public string SearchText { get => Session.SearchText; set => Session.SearchText = value; }

        // ---- Centre ------------------------------------------------------------------------------------------------
        public ITimelineModel Timeline => _timeline;

        public PlannerEditRouter Editor { get; }

        public string SelectedKey
        {
            get => Session.SelectedActivityId;
            set
            {
                // Group header rows are not activities: selecting one clears the activity selection.
                if (value != null && value.StartsWith(PlannerTimelineModel.GroupKeyPrefix, StringComparison.Ordinal)) value = null;
                Session.SelectedActivityId = value;
            }
        }

        public IReadOnlyList<HorizonDay> CapacityDays => Session.Days;
        public IReadOnlyList<CapacityRow> CapacityRows => Session.DragPreview?.Capacity ?? Session.Capacity;

        // ---- Right panel -------------------------------------------------------------------------------------------
        public SelectedActivity Selected => _selected;
        public IReadOnlyList<ConfigSection> ConfigSections { get; }

        // ---- Status bar --------------------------------------------------------------------------------------------
        public string SnapshotText => Session.SnapshotText;
        public string PolicyText => Session.PolicyText;
        public string PendingChangesText => Session.PendingChangesText;
        public string TotalsText => Session.PlanTotalText;
        public string StatusMessage => Session.StatusMessage;
        public bool StatusIsError => Session.StatusIsError;

        public void Dispose()
        {
            Session.PropertyChanged -= OnSessionChanged;
            _timeline.Dispose();
            Editor.Dispose();
        }

        /// <summary>The confirmation text of Aplicar a VGS: which activities, how they moved, and what happens next.</summary>
        public static string ApplyConfirmText(PlannerSession session)
        {
            var moved = PlannerProjection.MovedActivities(session.State.Model);
            var direct = moved.Where(p => p.Value == "direct").Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var cascade = moved.Where(p => p.Value != "direct").Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            string List(List<string> ids) => ids.Count == 0 ? "—"
                : string.Join(", ", ids.Take(ConfirmListLimit)) + (ids.Count > ConfirmListLimit ? string.Format(Res.Planner_ApplyConfirmMore, ids.Count - ConfirmListLimit) : string.Empty);
            return string.Format(Res.Planner_ApplyConfirm, moved.Count, direct.Count, List(direct), cascade.Count, List(cascade),
                ParseDay(session.RangeStart)?.ToString("dd/MM/yyyy", Es) ?? "—", ParseDay(session.RangeFinish)?.ToString("dd/MM/yyyy", Es) ?? "—",
                session.ProjectName);
        }

        private async Task RefreshAsync()
        {
            var outcome = await Session.RefreshFromVgsAsync(text => _host == null || _host.Confirm(text));
            if (!outcome.Ok && outcome.Code != "cancelled") _host?.Notify(outcome.Message, true);
        }

        private async Task ApplyAsync()
        {
            if (_host != null && !_host.Confirm(ApplyConfirmText(Session))) return;
            var outcome = await Session.ApplyToVgsAsync();
            _host?.Notify(outcome.Message, !outcome.Ok);
        }

        private async Task ExportExcelAsync()
        {
            string path = _host?.PickSaveFile(Res.Planner_SaveExcelTitle, Res.Planner_SaveExcelFilter, PlannerExports.ExcelFileName);
            if (path == null) return;
            SetExporting(true);
            try
            {
                var outcome = await PlannerExports.ExcelAsync(Session, path);
                _host?.Notify(outcome.Message, !outcome.Ok);
            }
            finally
            {
                SetExporting(false);
            }
        }

        private void ExportPdf()
        {
            string path = _host?.PickSaveFile(Res.Planner_SavePdfTitle, Res.Planner_SavePdfFilter, PlannerExports.PdfFileName);
            if (path == null) return;
            Export(() => PlannerExports.Pdf(Session, path));
        }

        private void ExportStandalone()
        {
            string path = _host?.PickSaveFile(Res.Planner_SaveStandaloneTitle, Res.Planner_SaveStandaloneFilter, StandalonePlanner.DefaultFileName);
            if (path == null) return;
            Export(() => PlannerExports.Standalone(Session, _host.WebResourcesFolder, path));
        }

        private void Export(Func<PlannerOutcome> export)
        {
            SetExporting(true);
            try
            {
                var outcome = export();
                _host?.Notify(outcome.Message, !outcome.Ok);
            }
            catch (Exception ex)
            {
                _host?.Notify(string.Format(Res.Msg_PlannerCommandFailed, ex.Message), true);
            }
            finally
            {
                SetExporting(false);
            }
        }

        private void SetExporting(bool value)
        {
            _exporting = value;
            foreach (var command in _commands) command.RaiseCanExecuteChanged();
        }

        /// <summary>Runs an async command without letting an exception reach the dispatcher (it would take Vulcan down).</summary>
        private async void Run(Func<Task> command)
        {
            try
            {
                await command();
            }
            catch (Exception ex)
            {
                _host?.Notify(string.Format(Res.Msg_PlannerCommandFailed, ex.Message), true);
            }
        }

        private void EditRange(DateTime? value, DateTime? current, Func<string, PlannerOutcome> edit, string name)
        {
            if (value == null || value == current) return;
            edit(value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            OnPropertyChanged(name);   // a refused edit puts the picker back
        }

        private static DateTime? ParseDay(string day) =>
            DateTime.TryParseExact(day ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;

        private static string DayText(string civil)
        {
            if (!DateTime.TryParseExact(civil ?? string.Empty, new[] { "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF", "yyyy-MM-dd" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return civil ?? string.Empty;
            return t.TimeOfDay == TimeSpan.Zero ? t.ToString("dd/MM/yyyy", Es) : t.ToString("dd/MM/yyyy HH:mm", Es);
        }

        private SelectedActivity BuildSelected(ActivityDetails d)
        {
            if (d == null) return null;
            IReadOnlyList<string> Pills(IReadOnlyList<LinkInfo> links) =>
                links == null || links.Count == 0 ? new[] { "—" } : links.Select(l => l.Text).ToArray();
            var choices = ResourceChoices(Session.ResourceOptions, d.VgsResource);
            var selected = new SelectedActivity(PickActivityResource)
            {
                Id = d.ActivityId,
                Name = d.Name,
                Type = d.Type,
                StartText = DayText(d.Start),
                ShiftText = d.ShiftText ?? string.Empty,
                FinishText = DayText(d.Finish),
                QuantityText = string.IsNullOrEmpty(d.QuantityText) ? "—" : d.QuantityText,
                ResourceOptions = choices,
                Predecessors = Pills(d.Predecessors),
                Successors = Pills(d.Successors),
                StateText = d.StatusText,
            };
            selected.ShowResource(choices.FirstOrDefault(c => !c.IsAddManual && c.Name == d.Resource));
            return selected;
        }

        /// <summary>The Recurso selector: every resource (the activity's VGS one marked "(VGS)"), then "+ Recurso manual…".</summary>
        public static IReadOnlyList<ResourceChoice> ResourceChoices(IEnumerable<string> options, string vgsResource)
        {
            var list = (options ?? Enumerable.Empty<string>())
                .Select(name => new ResourceChoice(name, name == vgsResource ? string.Format(Res.Planner_ResourceVgs, name) : name)).ToList();
            if (vgsResource != null && list.All(c => c.Name != vgsResource)) list.Insert(0, new ResourceChoice(vgsResource, string.Format(Res.Planner_ResourceVgs, vgsResource)));
            list.Add(new ResourceChoice(null, Res.Planner_AddManualResourceChoice, true));
            return list;
        }

        private void PickActivityResource(ResourceChoice choice)
        {
            string activity = Session.SelectedActivityId;
            if (activity == null || choice == null) return;
            if (choice.IsAddManual) AddManualResource(activity);
            else Session.AssignActivityResource(activity, choice.Name);
        }

        /// <summary>"+ Recurso manual": adds one (assigned to <paramref name="activityId"/> when given) and opens Frentes y recursos to rename it.</summary>
        private void AddManualResource(string activityId)
        {
            if (Session.AddManualResource(activityId).Ok) _frontsSection.IsExpanded = true;
        }

        private void RenameManualResource(ResourceItem item, string name)
        {
            if (!Session.RenameManualResource(item.Id, name).Ok)
            {
                item.Reset(Session.ManualResources.FirstOrDefault(r => r.Id == item.Id)?.Name ?? item.Name);
            }
        }

        private void RebuildResources() =>
            _resources.Items = Session.ResourceDescriptors.Select(d => new ResourceItem(d, RenameManualResource, _removeManual)).ToList();

        private void OnSessionChanged(object sender, PropertyChangedEventArgs e)
        {
            string name = e.PropertyName ?? string.Empty;
            switch (name)
            {
                case "":
                    RebuildResources();
                    _selected = BuildSelected(Session.Selected);
                    OnPropertyChanged(string.Empty);
                    break;
                case nameof(PlannerSession.ResourceDescriptors):
                case nameof(PlannerSession.ManualResources):
                    RebuildResources();
                    break;
                case nameof(PlannerSession.Counters):
                    Raise(nameof(ActivityCount), nameof(PendingCount), nameof(MovedCount), nameof(CascadeCount));
                    break;
                case nameof(PlannerSession.TypeChips): Raise(nameof(ActivityTypes)); break;
                case nameof(PlannerSession.Days): Raise(nameof(CapacityDays)); break;
                case nameof(PlannerSession.Capacity): Raise(nameof(CapacityRows)); break;
                case nameof(PlannerSession.DragPreview): Raise(nameof(CapacityRows)); break;
                case nameof(PlannerSession.RangeStart): Raise(nameof(RangeStart)); break;
                case nameof(PlannerSession.RangeFinish): Raise(nameof(RangeEnd)); break;
                case nameof(PlannerSession.DurationDays): Raise(nameof(RangeDays)); break;
                case nameof(PlannerSession.CanEditRange): Raise(nameof(CanEditRange)); break;
                case nameof(PlannerSession.RangeLockedReason): Raise(nameof(RangeTip)); break;
                case nameof(PlannerSession.Selected):
                    _selected = BuildSelected(Session.Selected);
                    Raise(nameof(Selected));
                    break;
                case nameof(PlannerSession.SelectedActivityId): Raise(nameof(SelectedKey)); break;
                case nameof(PlannerSession.SnapshotText): Raise(nameof(SnapshotText)); break;
                case nameof(PlannerSession.PendingChangesText): Raise(nameof(PendingChangesText)); break;
                case nameof(PlannerSession.PlanTotalText): Raise(nameof(TotalsText)); break;
                case nameof(PlannerSession.StatusMessage): Raise(nameof(StatusMessage)); break;
                case nameof(PlannerSession.StatusIsError): Raise(nameof(StatusIsError)); break;
                case nameof(PlannerSession.UndoDescription): Raise(nameof(UndoTip)); break;
                case nameof(PlannerSession.RedoDescription): Raise(nameof(RedoTip)); break;
                case nameof(PlannerSession.ApplyBlockedReason): Raise(nameof(ApplyTip)); break;
                case nameof(PlannerSession.IsBusy): Raise(nameof(ExportExcelTip)); break;
                case nameof(PlannerSession.ShowCompleted): Raise(nameof(ShowCompleted)); break;
                case nameof(PlannerSession.ShowPrecedences): Raise(nameof(ShowPrecedences)); break;
                case nameof(PlannerSession.SearchText): Raise(nameof(SearchText)); break;
                case nameof(PlannerSession.Grouping): Raise(nameof(GroupBy)); break;
                case nameof(PlannerSession.Sort): Raise(nameof(SortBy)); break;
            }

            if (name.Length == 0 || name.StartsWith("Can", StringComparison.Ordinal) || name == nameof(PlannerSession.IsBusy) || name == nameof(PlannerSession.Rows))
            {
                foreach (var command in _commands) command.RaiseCanExecuteChanged();
            }
        }

        private void Raise(params string[] names)
        {
            foreach (string n in names) OnPropertyChanged(n);
        }
    }
}
