// The screen-only display models of IPlannerViewModel. Everything else the screen shows is StpPlanner's own types
// (TypeChip, HorizonDay, CapacityRow, CapacityCell); the base class and commands are StpPlanner's ObservableObject and
// RelayCommand. Plain classes, no Workbench types, so the standalone preview can load them.

using System;
using System.Collections.Generic;
using System.Windows.Input;
using Maptek.Vulcan.Stp.Planner;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>The "Actividad seleccionada" panel. All texts preformatted.</summary>
    public sealed class SelectedActivity : ObservableObject
    {
        private readonly Action<ResourceChoice> _pickResource;
        private ResourceChoice _resource;

        /// <param name="pickResource">Called when the user picks another entry of the Recurso selector; null = read-only.</param>
        public SelectedActivity(Action<ResourceChoice> pickResource = null)
        {
            _pickResource = pickResource;
        }

        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string StartText { get; set; }

        /// <summary>Shift since the snapshot, e.g. "+1 día"; empty when not moved.</summary>
        public string ShiftText { get; set; }
        public string FinishText { get; set; }
        public string QuantityText { get; set; }

        /// <summary>The Recurso selector: VGS and manual resources, then "+ Recurso manual…".</summary>
        public IReadOnlyList<ResourceChoice> ResourceOptions { get; set; } = Array.Empty<ResourceChoice>();

        /// <summary>The activity's equipment. Picking another one assigns it to the whole activity (local only).</summary>
        public ResourceChoice Resource
        {
            get => _resource;
            set
            {
                if (value == null || ReferenceEquals(value, _resource)) return;
                if (_pickResource == null)
                {
                    Set(ref _resource, value);
                    return;
                }

                // The session decides; the panel is rebuilt from its answer (a refusal puts the old entry back).
                _pickResource(value);
                OnPropertyChanged(nameof(Resource));
            }
        }

        /// <summary>Sets the shown entry without assigning anything.</summary>
        internal void ShowResource(ResourceChoice choice) => _resource = choice;

        public IReadOnlyList<string> Predecessors { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Successors { get; set; } = Array.Empty<string>();

        /// <summary>Why it moved, e.g. "Movida por cascada desde VOL-1425-01".</summary>
        public string StateText { get; set; }
    }

    /// <summary>One entry of the Recurso selector.</summary>
    public sealed class ResourceChoice
    {
        public ResourceChoice(string name, string label, bool isAddManual = false)
        {
            Name = name;
            Label = label;
            IsAddManual = isAddManual;
        }

        /// <summary>The equipment name (null for "+ Recurso manual…").</summary>
        public string Name { get; }

        /// <summary>What the selector shows, e.g. "Scoop LH-07 (VGS)".</summary>
        public string Label { get; }

        /// <summary>True for "+ Recurso manual…": adds a manual resource and assigns it to the activity.</summary>
        public bool IsAddManual { get; }

        public override string ToString() => Label;
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

    /// <summary>Configuración → Frentes y recursos: the project's equipment and "+ Recurso manual".</summary>
    public sealed class ResourcesConfig : ObservableObject
    {
        private IReadOnlyList<ResourceItem> _items = Array.Empty<ResourceItem>();

        public ResourcesConfig(ICommand addCommand)
        {
            AddCommand = addCommand;
        }

        /// <summary>VGS resources (read-only) then manual ones (renamable, removable).</summary>
        public IReadOnlyList<ResourceItem> Items { get => _items; set => Set(ref _items, value ?? Array.Empty<ResourceItem>()); }

        /// <summary>"+ Recurso manual".</summary>
        public ICommand AddCommand { get; }
    }

    /// <summary>One resource of <see cref="ResourcesConfig"/>.</summary>
    public sealed class ResourceItem : ObservableObject
    {
        private readonly Action<ResourceItem, string> _rename;
        private string _name;

        public ResourceItem(ResourceDescriptor descriptor, Action<ResourceItem, string> rename, ICommand removeCommand)
        {
            Id = descriptor.Id;
            IsManual = descriptor.IsManual;
            _name = descriptor.Name;
            _rename = rename;
            RemoveCommand = removeCommand;
        }

        public string Id { get; }

        /// <summary>Manual resources can be renamed and removed; VGS ones only shown.</summary>
        public bool IsManual { get; }

        /// <summary>The name; editing a manual one renames it (when the box loses focus).</summary>
        public string Name
        {
            get => _name;
            set
            {
                if (value == _name) return;
                if (!IsManual || _rename == null) return;
                _rename(this, value);
            }
        }

        /// <summary>Removes it (parameter: this item).</summary>
        public ICommand RemoveCommand { get; }

        /// <summary>Puts the shown name back to the stored one (after a refused rename).</summary>
        internal void Reset(string name)
        {
            _name = name;
            OnPropertyChanged(nameof(Name));
        }
    }
}
