using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline
{
    /// <summary>What the user did with an "Avance real" box.</summary>
    public enum TimelineProgressAction
    {
        /// <summary>Typed a value (<see cref="TimelineProgressEventArgs.Value"/>; <c>null</c> = emptied).</summary>
        Edit,

        /// <summary>Dragged the box by its grip to <see cref="TimelineProgressEventArgs.TargetDay"/>.</summary>
        Move,

        /// <summary>"Eliminar avance real".</summary>
        Remove,

        /// <summary>"Agregar avance real" on a day without a box.</summary>
        Add,
    }

    /// <summary>
    /// An "Avance real" request (<see cref="TimelineView.ActualProgressRequested"/>). Like the other edit requests, the
    /// control changes nothing itself: the host applies it and refreshes the model, or calls <see cref="Reject"/>.
    /// </summary>
    public sealed class TimelineProgressEventArgs : RoutedEventArgs
    {
        internal TimelineProgressEventArgs(object source, TimelineProgressAction action, ITimelineRow row, int day, DateTime date, int targetDay, DateTime targetDate, double? value)
            : base(TimelineView.ActualProgressRequestedEvent, source)
        {
            Action = action;
            Row = row;
            Day = day;
            Date = date;
            TargetDay = targetDay;
            TargetDate = targetDate;
            Value = value;
        }

        /// <summary>Gets the action.</summary>
        public TimelineProgressAction Action { get; }

        /// <summary>Gets the row.</summary>
        public ITimelineRow Row { get; }

        /// <summary>Gets the row key (activity id).</summary>
        public string ActivityKey => Row?.Key;

        /// <summary>Gets the day of the box (the dragged one for <see cref="TimelineProgressAction.Move"/>).</summary>
        public int Day { get; }

        /// <summary>Gets the date of <see cref="Day"/>.</summary>
        public DateTime Date { get; }

        /// <summary>Gets the drop day (<see cref="TimelineProgressAction.Move"/>), else <see cref="Day"/>.</summary>
        public int TargetDay { get; }

        /// <summary>Gets the date of <see cref="TargetDay"/>.</summary>
        public DateTime TargetDate { get; }

        /// <summary>Gets the typed value (<see cref="TimelineProgressAction.Edit"/>), <c>null</c> for an emptied box.</summary>
        public double? Value { get; }

        /// <summary>Gets why the host refused, or <c>null</c>.</summary>
        public string RejectReason { get; private set; }

        /// <summary>Refuses the request: the box flashes red and the reason is shown.</summary>
        /// <param name="reason">The reason (Spanish).</param>
        public void Reject(string reason) => RejectReason = string.IsNullOrEmpty(reason) ? "No se puede." : reason;
    }

    /// <summary>
    /// The control asks the host about "Avance real" (<see cref="TimelineView.ActualProgressQuery"/>): whether
    /// "Agregar avance real" is offered on a day, or which boxes a drag would move.
    /// </summary>
    public sealed class TimelineProgressQueryEventArgs : RoutedEventArgs
    {
        private readonly List<(int Origin, int Target)> moves = new List<(int, int)>();

        internal TimelineProgressQueryEventArgs(object source, bool isPreview, ITimelineRow row, int day, DateTime date, int targetDay, DateTime targetDate)
            : base(TimelineView.ActualProgressQueryEvent, source)
        {
            IsPreview = isPreview;
            Row = row;
            Day = day;
            Date = date;
            TargetDay = targetDay;
            TargetDate = targetDate;
        }

        /// <summary>Gets a value indicating whether this asks for a drag preview (else: can a box be added on <see cref="Day"/>).</summary>
        public bool IsPreview { get; }

        /// <summary>Gets the row.</summary>
        public ITimelineRow Row { get; }

        /// <summary>Gets the row key.</summary>
        public string ActivityKey => Row?.Key;

        /// <summary>Gets the day asked about, or the dragged day.</summary>
        public int Day { get; }

        /// <summary>Gets the date of <see cref="Day"/>.</summary>
        public DateTime Date { get; }

        /// <summary>Gets the day under the pointer (preview).</summary>
        public int TargetDay { get; }

        /// <summary>Gets the date of <see cref="TargetDay"/>.</summary>
        public DateTime TargetDate { get; }

        /// <summary>Gets or sets a value indicating whether "Agregar avance real" is offered.</summary>
        public bool CanAdd { get; set; }

        /// <summary>Gets the moves the host set for the preview.</summary>
        public IReadOnlyList<(int Origin, int Target)> Moves => moves;

        /// <summary>Sets the preview: each box that would move (day indices of the model).</summary>
        /// <param name="preview">Origin and target days.</param>
        public void SetMoves(IEnumerable<(int Origin, int Target)> preview)
        {
            moves.Clear();
            if (preview != null) moves.AddRange(preview);
        }

        internal IReadOnlyList<int> Origins => moves.Select(m => m.Origin).ToList();

        internal IReadOnlyList<int> Targets => moves.Select(m => m.Target).ToList();
    }
}
