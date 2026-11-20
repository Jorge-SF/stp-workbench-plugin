using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>
    /// The one INotifyPropertyChanged base of the planner (StpPlanner and the StpClient screen models). No WPF types,
    /// no Workbench IL weaving: the planner also runs in tests and in the standalone preview.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        /// <inheritdoc/>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
        /// <param name="name">The property; <c>null</c> or empty means every property.</param>
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Sets a field and raises the change when it differs.</summary>
        /// <typeparam name="T">Field type.</typeparam>
        /// <param name="field">The field.</param>
        /// <param name="value">The value.</param>
        /// <param name="name">The property.</param>
        /// <returns>Whether it changed.</returns>
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    /// <summary>
    /// The one ICommand of the planner. <see cref="ICommand"/> lives in System.dll on .NET 4.5+, so this needs no WPF.
    /// It does not hook WPF's CommandManager: the owner calls <see cref="RaiseCanExecuteChanged"/> when the state behind
    /// <c>canExecute</c> changes (for the planner screen, on every PlannerSession change).
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> execute;
        private readonly Func<object, bool> canExecute;

        /// <summary>Initializes a new instance of the <see cref="RelayCommand"/> class.</summary>
        /// <param name="execute">What the command does.</param>
        /// <param name="canExecute">Whether it is enabled; <c>null</c> means always.</param>
        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(execute == null ? (Action<object>)null : _ => execute(), canExecute == null ? (Func<object, bool>)null : _ => canExecute())
        {
        }

        /// <summary>Initializes a new instance of the <see cref="RelayCommand"/> class with a parameter.</summary>
        /// <param name="execute">What the command does with the command parameter.</param>
        /// <param name="canExecute">Whether it is enabled for a parameter; <c>null</c> means always.</param>
        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            this.execute = execute ?? (_ => { });
            this.canExecute = canExecute;
        }

        /// <summary>A command that is always disabled (features not built yet).</summary>
        /// <returns>The command.</returns>
        public static RelayCommand Disabled() => new RelayCommand(() => { }, () => false);

        /// <inheritdoc/>
        public event EventHandler CanExecuteChanged;

        /// <inheritdoc/>
        public bool CanExecute(object parameter) => canExecute == null || canExecute(parameter);

        /// <inheritdoc/>
        public void Execute(object parameter)
        {
            if (CanExecute(parameter)) execute(parameter);
        }

        /// <summary>Tells bound controls to ask <see cref="CanExecute"/> again.</summary>
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
