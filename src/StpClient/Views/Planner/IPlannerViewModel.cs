// The contract between the planner document view (PlannerContent.xaml) and whatever drives it.
//
// The view binds only to this interface. PlannerScreenViewModel implements it over StpPlanner's PlannerSession (the
// real project, and the design data of DesignPlannerViewModel for the preview). The display types come from StpPlanner
// (TypeChip, HorizonDay, CapacityRow/Cell) and StpTimeline (ITimelineModel); SelectedActivity and ConfigSection are
// the only screen-only models (PlannerModels.cs).
//
// Rules for implementers:
//   - Raise PropertyChanged for every property below that can change (null/empty name = "everything changed").
//   - Raise it on the UI thread (the view does not marshal).
//   - Commands decide their own CanExecute and raise CanExecuteChanged; the view only binds Command="...".
//   - Texts are already localized (Spanish); the view adds only the fixed labels from Resources.resx.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>Everything the planner document shows and the commands it offers.</summary>
    public interface IPlannerViewModel : INotifyPropertyChanged
    {
        // ---- Command bar: Datos ------------------------------------------------------------------------------------
        /// <summary>Actualizar: reload the snapshot from VGS (bridge GetSnapshotAsync).</summary>
        ICommand RefreshCommand { get; }

        /// <summary>True when the live bridge answers; drives the green "En vivo" indicator.</summary>
        bool IsLive { get; }

        /// <summary>Indicator text, e.g. "En vivo" or "Sin conexión".</summary>
        string ConnectionText { get; }

        /// <summary>Indicator tooltip (the bridge's own message).</summary>
        string ConnectionTip { get; }

        // ---- Command bar: Plan -------------------------------------------------------------------------------------
        ICommand UndoCommand { get; }
        ICommand RedoCommand { get; }

        /// <summary>Descartar: drop every change not applied to VGS.</summary>
        ICommand DiscardCommand { get; }

        /// <summary>What Deshacer / Rehacer would do (tooltips); empty when nothing.</summary>
        string UndoTip { get; }
        string RedoTip { get; }

        // ---- Command bar: VGS --------------------------------------------------------------------------------------
        /// <summary>Aplicar a VGS: the only primary button.</summary>
        ICommand ApplyToVgsCommand { get; }

        /// <summary>Why Aplicar a VGS is disabled, or its normal tooltip.</summary>
        string ApplyTip { get; }

        ICommand VerifySaveCommand { get; }

        // ---- Command bar: Exportar ---------------------------------------------------------------------------------
        ICommand ExportExcelCommand { get; }
        ICommand ExportPdfCommand { get; }

        /// <summary>STP suelto: standalone HTML planner.</summary>
        ICommand ExportStandaloneCommand { get; }

        // ---- Command bar: Vista (two-way) --------------------------------------------------------------------------
        bool ShowPrecedences { get; set; }
        bool ShowCompleted { get; set; }
        bool ShowResources { get; set; }

        /// <summary>Shows or hides the bottom panel (Capacidad, Reportes, Gráficos, Avance real).</summary>
        bool ShowCapacityPanel { get; set; }

        // ---- Command bar: Resumen ----------------------------------------------------------------------------------
        int ActivityCount { get; }
        int PendingCount { get; }

        /// <summary>Moved directly by the user; shown orange when non-zero.</summary>
        int MovedCount { get; }

        /// <summary>Moved by precedence cascade; shown orange when non-zero.</summary>
        int CascadeCount { get; }

        // ---- Filter strip (two-way) --------------------------------------------------------------------------------
        DateTime? RangeStart { get; set; }
        DateTime? RangeEnd { get; set; }

        /// <summary>Number of days in the range, shown after the dates.</summary>
        int RangeDays { get; }

        /// <summary>False while there are local moves (the engine locks the horizon); see <see cref="RangeTip"/>.</summary>
        bool CanEditRange { get; }

        /// <summary>Why the range is locked, or empty.</summary>
        string RangeTip { get; }

        /// <summary>One chip per activity type; <see cref="ToggleTypeCommand"/> (parameter: <see cref="TypeChip.Type"/>) hides/shows it.</summary>
        IReadOnlyList<TypeChip> ActivityTypes { get; }

        ICommand ToggleTypeCommand { get; }

        IReadOnlyList<string> GroupByOptions { get; }
        string GroupBy { get; set; }
        IReadOnlyList<string> SortOptions { get; }
        string SortBy { get; set; }
        string SearchText { get; set; }

        // ---- Centre ------------------------------------------------------------------------------------------------
        /// <summary>What the timeline control draws; null shows the placeholder.</summary>
        ITimelineModel Timeline { get; }

        /// <summary>Applies the timeline's edit requests (drag, keys, menu) to the plan; its IsEditable feeds the control.</summary>
        PlannerEditRouter Editor { get; }

        /// <summary>Selected activity id, two-way with the timeline's SelectedKey; null when none.</summary>
        string SelectedKey { get; set; }

        /// <summary>Capacidad tab: day headers.</summary>
        IReadOnlyList<HorizonDay> CapacityDays { get; }

        /// <summary>Capacidad tab: daily totals per activity type against the goal.</summary>
        IReadOnlyList<CapacityRow> CapacityRows { get; }

        // ---- Right panel -------------------------------------------------------------------------------------------
        /// <summary>Actividad seleccionada; null when nothing is selected.</summary>
        SelectedActivity Selected { get; }

        /// <summary>Configuración: collapsible sections (Colores y metas, Atributos visibles, ...).</summary>
        IReadOnlyList<ConfigSection> ConfigSections { get; }

        // ---- Status bar --------------------------------------------------------------------------------------------
        /// <summary>e.g. "Instantánea 21/11/2026 08:00".</summary>
        string SnapshotText { get; }

        /// <summary>e.g. "Política de precedencias: bloquear heredadas".</summary>
        string PolicyText { get; }

        /// <summary>e.g. "5 cambios sin aplicar a VGS"; empty when there are none.</summary>
        string PendingChangesText { get; }

        /// <summary>e.g. "Plan total 26 150 t · 1 434 m".</summary>
        string TotalsText { get; }

        /// <summary>The last command's message (Spanish); empty when none.</summary>
        string StatusMessage { get; }

        /// <summary>True when <see cref="StatusMessage"/> is an error or a warning (drawn orange).</summary>
        bool StatusIsError { get; }
    }
}
