// Small display models used by IPlannerViewModel. Plain classes, no Workbench types, so StpPlanner can fill them
// and the standalone preview can load them without the Workbench runtime.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    /// <summary>Base for the display models: PropertyChanged without the Workbench IL weaver.</summary>
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A type chip in the filter strip.</summary>
    public sealed class ActivityTypeFilter : Observable
    {
        private bool _isVisible = true;

        public ActivityTypeFilter(string name, string color)
        {
            Name = name;
            Color = color;
        }

        public string Name { get; }

        /// <summary>WPF colour text, e.g. "#3B78C4".</summary>
        public string Color { get; }

        /// <summary>False hides the type (chip drawn faded).</summary>
        public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    }

    /// <summary>The Capacidad tab: one header per day and one row per activity type.</summary>
    public sealed class CapacityTable
    {
        public CapacityTable(IReadOnlyList<CapacityDay> days, IReadOnlyList<CapacityRow> rows)
        {
            Days = days;
            Rows = rows;
        }

        public IReadOnlyList<CapacityDay> Days { get; }
        public IReadOnlyList<CapacityRow> Rows { get; }
    }

    public sealed class CapacityDay
    {
        public CapacityDay(DateTime date, bool isWeekend)
        {
            Date = date;
            IsWeekend = isWeekend;
        }

        public DateTime Date { get; }
        public bool IsWeekend { get; }
        public string Label => Date.ToString("dd");
    }

    public sealed class CapacityRow
    {
        public CapacityRow(string type, string color, string goalText, IReadOnlyList<CapacityCell> cells)
        {
            Type = type;
            Color = color;
            GoalText = goalText;
            Cells = cells;
        }

        public string Type { get; }
        public string Color { get; }
        public string GoalText { get; }
        public IReadOnlyList<CapacityCell> Cells { get; }
    }

    public sealed class CapacityCell
    {
        public CapacityCell(string text, bool isOver, bool isEmpty, bool isWeekend)
        {
            Text = text;
            IsOver = isOver;
            IsEmpty = isEmpty;
            IsWeekend = isWeekend;
        }

        public string Text { get; }

        /// <summary>Above the daily goal (drawn red).</summary>
        public bool IsOver { get; }
        public bool IsEmpty { get; }
        public bool IsWeekend { get; }
    }

    /// <summary>The "Actividad seleccionada" panel. All texts preformatted.</summary>
    public sealed class SelectedActivity : Observable
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

        /// <summary>Two-way: the implementer reacts to a change (assign resource).</summary>
        public string Resource { get => _resource; set => Set(ref _resource, value); }

        public IReadOnlyList<string> Predecessors { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Successors { get; set; } = Array.Empty<string>();

        /// <summary>Why it moved, e.g. "Movida por cascada desde VOL-1425-01".</summary>
        public string StateText { get; set; }
    }

    /// <summary>A collapsible section of the Configuración panel.</summary>
    public sealed class ConfigSection : Observable
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

    /// <summary>Minimal ICommand for the design-time model and simple hosts.</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? (() => { });
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public void Execute(object parameter) => _execute();
    }
}
