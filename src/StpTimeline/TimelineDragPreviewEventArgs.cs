using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline
{
    public delegate void TimelineDragPreviewEventHandler(object sender, TimelineDragPreviewEventArgs e);

    /// <summary>
    /// <see cref="TimelineView.DragPreviewChanged"/>: the drag target, scope or state changed. The host previews the
    /// move without applying it (capacity excesses: the browser planner's <c>previewDailyTail</c> warnings) and calls
    /// <see cref="SetWarnings"/> with the days to mark; the ghosts on those days turn red (the browser's
    /// <c>.drag-preview-warning</c>). <see cref="IsActive"/> false means the drag ended (drop, Esc, lost capture or a
    /// refused start): drop any preview.
    /// </summary>
    public sealed class TimelineDragPreviewEventArgs : RoutedEventArgs
    {
        internal TimelineDragPreviewEventArgs(RoutedEvent routedEvent, object source, bool isActive, ITimelineRow row, int rowIndex,
            int blockIndex, DateTime? date, TimelineMoveScope scope, int dayDelta)
            : base(routedEvent, source)
        {
            IsActive = isActive;
            Row = row;
            RowIndex = rowIndex;
            BlockIndex = blockIndex;
            Block = row != null && blockIndex >= 0 && row.Blocks != null && blockIndex < row.Blocks.Count ? row.Blocks[blockIndex] : (TimelineBlock?)null;
            Date = date;
            Scope = scope;
            DayDelta = dayDelta;
        }

        /// <summary>False when the drag is over: clear the preview.</summary>
        public bool IsActive { get; }

        /// <summary>The dragged activity row, or null when the drag is over.</summary>
        public ITimelineRow Row { get; }

        /// <summary>Activity id (<see cref="ITimelineRow.Key"/>), or null.</summary>
        public string ActivityKey => Row?.Key;

        /// <summary>Index in <see cref="ITimelineModel.Rows"/>, or -1.</summary>
        public int RowIndex { get; }

        /// <summary>The pressed block (its <see cref="TimelineBlock.Tag"/> is the fragment), or null.</summary>
        public TimelineBlock? Block { get; }

        /// <summary>Index in <see cref="ITimelineRow.Blocks"/>, or -1.</summary>
        public int BlockIndex { get; }

        /// <summary>Date of the pressed block, or null.</summary>
        public DateTime? Date { get; }

        /// <summary>Tail (plain drag) or whole activity (Shift+drag).</summary>
        public TimelineMoveScope Scope { get; }

        /// <summary>Current offset in days; 0 while the pointer is back over the origin (nothing to preview).</summary>
        public int DayDelta { get; }

        /// <summary>Where the pressed block would land (<c>moveDailyTail</c>'s target date).</summary>
        public DateTime? TargetDate => Date?.AddDays(DayDelta);

        /// <summary>Days (index from <see cref="ITimelineModel.StartDate"/>) set by <see cref="SetWarnings"/>.</summary>
        public IReadOnlyCollection<int> WarningDays { get; private set; } = Array.Empty<int>();

        /// <summary>Warning text set by <see cref="SetWarnings"/> (shown in the drag badge), or null.</summary>
        public string WarningText { get; private set; }

        /// <summary>The move would exceed capacity on these days: the ghosts there are drawn as warnings.</summary>
        /// <param name="days">Day indexes (from the model's start date).</param>
        /// <param name="text">Short text for the badge, e.g. "Capacidad excedida para Rezagado.".</param>
        public void SetWarnings(IEnumerable<int> days, string text = null)
        {
            WarningDays = (days ?? Enumerable.Empty<int>()).Distinct().ToArray();
            WarningText = string.IsNullOrEmpty(text) ? null : text;
        }

        protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) =>
            ((TimelineDragPreviewEventHandler)genericHandler)(genericTarget, this);
    }
}
