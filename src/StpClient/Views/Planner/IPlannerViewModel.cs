// The contract between the planner document view (PlannerContent.xaml) and whatever drives it.
//
// The view binds only to this interface. Today DesignPlannerViewModel (fake data from docs/mockups/planner_layout.html)
// implements it, so the screen can be built and checked before the real pieces land:
//   - StpPlanner (view model over StpEngine's CivilHorizonController + StpBridge) implements IPlannerViewModel;
//   - StpTimeline (the Gantt control) is handed to the view through TimelineContent.
// Keep it small: anything the view does not show does not belong here.
//
// Rules for implementers:
//   - Raise PropertyChanged for every property below that can change (null/empty name = "everything changed").
//   - Raise it on the UI thread (the view does not marshal).
//   - Commands decide their own CanExecute; the view only binds Command="...".
//   - Texts are already localized (Spanish); the view adds only the fixed labels from Resources.resx.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;

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

        /// <summary>Indicator text, e.g. "En vivo" or "Sin conexión" (BridgeConfig.Message in the tooltip is fine).</summary>
        string ConnectionText { get; }

        // ---- Command bar: Plan -------------------------------------------------------------------------------------
        ICommand UndoCommand { get; }
        ICommand RedoCommand { get; }

        /// <summary>Descartar: drop every change not applied to VGS.</summary>
        ICommand DiscardCommand { get; }

        // ---- Command bar: VGS --------------------------------------------------------------------------------------
        /// <summary>Aplicar a VGS: the only primary button.</summary>
        ICommand ApplyToVgsCommand { get; }
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
        DateTime RangeStart { get; set; }
        DateTime RangeEnd { get; set; }

        /// <summary>Number of days in the range, shown after the dates.</summary>
        int RangeDays { get; }

        /// <summary>One chip per activity type; clicking a chip toggles <see cref="ActivityTypeFilter.IsVisible"/>.</summary>
        IReadOnlyList<ActivityTypeFilter> ActivityTypes { get; }

        IReadOnlyList<string> GroupByOptions { get; }
        string GroupBy { get; set; }
        IReadOnlyList<string> SortOptions { get; }
        string SortBy { get; set; }
        string SearchText { get; set; }

        // ---- Centre ------------------------------------------------------------------------------------------------
        /// <summary>
        /// The timeline (StpTimeline control, or a view model the host maps to it with a DataTemplate).
        /// Null shows the placeholder.
        /// </summary>
        object TimelineContent { get; }

        /// <summary>Capacidad tab: daily totals per activity type against the goal.</summary>
        CapacityTable Capacity { get; }

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
    }
}
