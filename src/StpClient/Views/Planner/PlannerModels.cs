// The two screen-only display models of IPlannerViewModel. Everything else the screen shows is StpPlanner's own types
// (TypeChip, HorizonDay, CapacityRow, CapacityCell); the base class and commands are StpPlanner's ObservableObject and
// RelayCommand. Plain classes, no Workbench types, so the standalone preview can load them.

using System;
using System.Collections.Generic;
using Maptek.Vulcan.Stp.Planner;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>The "Actividad seleccionada" panel. All texts preformatted.</summary>
    public sealed class SelectedActivity : ObservableObject
    {
        private string _resource;

        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string StartText { get; set; }

        /// <summary>Shift since the snapshot, e.g. "+1 día"; empty when not moved.</summary>
        public string ShiftText { get; set; }
        public string FinishText { get; set; }
        public string QuantityText { get; set; }

        public IReadOnlyList<string> ResourceOptions { get; set; } = Array.Empty<string>();

        /// <summary>The resource shown in the selector (assigning another one is not built yet).</summary>
        public string Resource { get => _resource; set => Set(ref _resource, value); }

        public IReadOnlyList<string> Predecessors { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Successors { get; set; } = Array.Empty<string>();

        /// <summary>Why it moved, e.g. "Movida por cascada desde VOL-1425-01".</summary>
        public string StateText { get; set; }
    }

    /// <summary>A collapsible section of the Configuración panel.</summary>
    public sealed class ConfigSection : ObservableObject
    {
        private bool _isExpanded;

        public ConfigSection(string title, object content = null)
        {
            Title = title;
            Content = content;
        }

        public string Title { get; }

        /// <summary>The section's editor (any WPF content or a templated view model); null shows "Próximamente".</summary>
        public object Content { get; }

        public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    }
}
