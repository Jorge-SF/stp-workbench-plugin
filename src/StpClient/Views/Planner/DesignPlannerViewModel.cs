// Design-time IPlannerViewModel: the invented data of docs/mockups/planner_layout.html (no client data).
// Used until StpPlanner provides the real view model, by the standalone preview (tools/Preview-Planner.ps1),
// and by the "Abrir planificador" document while no live session is wired.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Res = Maptek.Vulcan.Stp.Resources.Resources;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    public sealed class DesignPlannerViewModel : Observable, IPlannerViewModel
    {
        private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-MX");

        private sealed class Act
        {
            public string Id, Type, Unit;
            public int Start, Days, Done, PerDay;
        }

        private static readonly string[] TypeNames =
            { "Barrenación", "Desarrollo", "Relleno", "Rezagado", "Sostenimiento", "Servicios", "Voladura" };

        private static readonly string[] TypeColors =
            { "#3B78C4", "#2F8F5B", "#8A5CC4", "#D07A2C", "#2B7F7A", "#C2455A", "#4F86C6" };

        // Day 0 = Monday 16/11/2026; same rows as the mockup.
        private static readonly Act[] Acts =
        {
            new Act { Id = "BAR-1400-02", Type = "Barrenación", Start = 2, Days = 4, PerDay = 238, Unit = "m" },
            new Act { Id = "BAR-1425-01", Type = "Barrenación", Start = 5, Days = 4, PerDay = 300, Unit = "m" },
            new Act { Id = "DES-1425-01", Type = "Desarrollo", Start = 1, Days = 3, Done = 3, PerDay = 7, Unit = "m" },
            new Act { Id = "DES-1450-01", Type = "Desarrollo", Start = 0, Days = 3, Done = 3, PerDay = 12, Unit = "m" },
            new Act { Id = "DES-1450-02", Type = "Desarrollo", Start = 3, Days = 4, PerDay = 12, Unit = "m" },
            new Act { Id = "REL-1400-01", Type = "Relleno", Start = 0, Days = 10, Done = 5, PerDay = 320, Unit = "m3" },
            new Act { Id = "REZ-1400-02", Type = "Rezagado", Start = 7, Days = 5, PerDay = 1220, Unit = "t" },
            new Act { Id = "REZ-1425-01", Type = "Rezagado", Start = 10, Days = 5, PerDay = 1700, Unit = "t" },
            new Act { Id = "SOS-1450-01", Type = "Sostenimiento", Start = 3, Days = 5, PerDay = 36, Unit = "pernos" },
            new Act { Id = "SRV-1450-01", Type = "Servicios", Start = 7, Days = 3, PerDay = 16, Unit = "m" },
            new Act { Id = "VOL-1400-02", Type = "Voladura", Start = 6, Days = 1, PerDay = 6100, Unit = "t" },
            new Act { Id = "VOL-1425-01", Type = "Voladura", Start = 9, Days = 1, PerDay = 8500, Unit = "t" },
        };

        /// <summary>Daily goal per type (Capacidad tab); Servicios has none.</summary>
        private static readonly Dictionary<string, int> Goals = new Dictionary<string, int>
        {
            ["Barrenación"] = 600, ["Desarrollo"] = 24, ["Relleno"] = 400, ["Rezagado"] = 2500, ["Sostenimiento"] = 40,
            ["Voladura"] = 8000,
        };

        private bool _showPrecedences = true, _showCompleted = true, _showResources, _showCapacityPanel = true;
        private DateTime _rangeStart = new DateTime(2026, 11, 16), _rangeEnd = new DateTime(2026, 11, 29);
        private string _groupBy, _sortBy, _searchText = "";

        public DesignPlannerViewModel()
        {
            var noop = new RelayCommand(() => { });
            RefreshCommand = UndoCommand = DiscardCommand = ApplyToVgsCommand = VerifySaveCommand =
                ExportExcelCommand = ExportPdfCommand = ExportStandaloneCommand = noop;
            RedoCommand = new RelayCommand(() => { }, () => false);

            ActivityTypes = TypeNames.Select((t, i) => new ActivityTypeFilter(t, TypeColors[i])).ToList();
            ActivityTypes[5].IsVisible = false;   // as in the mockup: Servicios hidden

            GroupByOptions = new[] { Res.Planner_GroupType, Res.Planner_GroupLevel, Res.Planner_GroupResource, Res.Planner_GroupNone };
            SortOptions = new[] { Res.Planner_SortStart, Res.Planner_SortId, Res.Planner_SortAttribute };
            _groupBy = GroupByOptions[0];
            _sortBy = SortOptions[0];

            Capacity = BuildCapacity(_rangeStart, 14);

            Selected = new SelectedActivity
            {
                Id = "REZ-1425-01",
                Name = "Rezagado TJ-1425-01",
                Type = "Rezagado",
                StartText = "26/11",
                ShiftText = "+1 día",
                FinishText = "01/12",
                QuantityText = "8 500 t · 1 700 t/día",
                ResourceOptions = new[] { "Scoop LH-07 (VGS)", "Scoop LH-05", Res.Planner_ManualResource },
                Resource = "Scoop LH-07 (VGS)",
                Predecessors = new[] { "VOL-1425-01 FS" },
                Successors = new[] { "—" },
                StateText = "Movida por cascada desde VOL-1425-01",
            };

            ConfigSections = new[]
            {
                new ConfigSection(Res.Planner_CfgColors), new ConfigSection(Res.Planner_CfgAttributes),
                new ConfigSection(Res.Planner_CfgReports), new ConfigSection(Res.Planner_CfgFronts),
                new ConfigSection(Res.Planner_CfgPrint), new ConfigSection(Res.Planner_CfgGeneral),
            };
        }

        public ICommand RefreshCommand { get; }
        public bool IsLive => true;
        public string ConnectionText => Res.Planner_Live;
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand DiscardCommand { get; }
        public ICommand ApplyToVgsCommand { get; }
        public ICommand VerifySaveCommand { get; }
        public ICommand ExportExcelCommand { get; }
        public ICommand ExportPdfCommand { get; }
        public ICommand ExportStandaloneCommand { get; }

        public bool ShowPrecedences { get => _showPrecedences; set => Set(ref _showPrecedences, value); }
        public bool ShowCompleted { get => _showCompleted; set => Set(ref _showCompleted, value); }
        public bool ShowResources { get => _showResources; set => Set(ref _showResources, value); }
        public bool ShowCapacityPanel { get => _showCapacityPanel; set => Set(ref _showCapacityPanel, value); }

        public int ActivityCount => Acts.Length;
        public int PendingCount => Acts.Count(a => a.Done < a.Days);
        public int MovedCount => 2;
        public int CascadeCount => 3;

        public DateTime RangeStart
        {
            get => _rangeStart;
            set { if (Set(ref _rangeStart, value)) Raise(nameof(RangeDays)); }
        }

        public DateTime RangeEnd
        {
            get => _rangeEnd;
            set { if (Set(ref _rangeEnd, value)) Raise(nameof(RangeDays)); }
        }

        public int RangeDays => Math.Max(0, (int)(_rangeEnd - _rangeStart).TotalDays + 1);

        public IReadOnlyList<ActivityTypeFilter> ActivityTypes { get; }
        public IReadOnlyList<string> GroupByOptions { get; }
        public string GroupBy { get => _groupBy; set => Set(ref _groupBy, value); }
        public IReadOnlyList<string> SortOptions { get; }
        public string SortBy { get => _sortBy; set => Set(ref _sortBy, value); }
        public string SearchText { get => _searchText; set => Set(ref _searchText, value); }

        public object TimelineContent => null;
        public CapacityTable Capacity { get; }
        public SelectedActivity Selected { get; }
        public IReadOnlyList<ConfigSection> ConfigSections { get; }

        public string SnapshotText => string.Format(Res.Planner_SnapshotFormat, "21/11/2026 08:00");
        public string PolicyText => string.Format(Res.Planner_PolicyFormat, "bloquear heredadas");
        public string PendingChangesText => string.Format(Res.Planner_PendingFormat, 5);
        public string TotalsText => string.Format(Res.Planner_TotalsFormat, "26 150 t · 1 434 m");

        private static CapacityTable BuildCapacity(DateTime first, int dayCount)
        {
            var days = Enumerable.Range(0, dayCount)
                .Select(i => first.AddDays(i))
                .Select(d => new CapacityDay(d, d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday))
                .ToList();
            var rows = new List<CapacityRow>();
            foreach (var goalOfType in Goals)
            {
                string type = goalOfType.Key;
                int goal = goalOfType.Value;
                string color = TypeColors[Array.IndexOf(TypeNames, type)];
                var cells = new List<CapacityCell>();
                for (int i = 0; i < dayCount; i++)
                {
                    int v = Acts.Where(a => a.Type == type && i >= a.Start && i < a.Start + a.Days).Sum(a => a.PerDay);
                    string text = v == 0 ? "·" : v >= 1000 ? (v / 1000.0).ToString("0.0", Es) + "k" : v.ToString(Es);
                    cells.Add(new CapacityCell(text, v > goal, v == 0, days[i].IsWeekend));
                }
                rows.Add(new CapacityRow(type, color, goal.ToString("N0", Es), cells));
            }
            return new CapacityTable(days, rows);
        }
    }
}
