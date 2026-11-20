using System;
using System.Windows;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline
{
    public delegate void TimelineBlockEventHandler(object sender, TimelineBlockEventArgs e);

    /// <summary>A block was clicked, or a drag started on it (the drag itself is handled by the caller).</summary>
    public sealed class TimelineBlockEventArgs : RoutedEventArgs
    {
        internal TimelineBlockEventArgs(RoutedEvent routedEvent, object source, ITimelineRow row, int rowIndex,
            TimelineBlock block, int blockIndex, DateTime date, Point position)
            : base(routedEvent, source)
        {
            Row = row;
            RowIndex = rowIndex;
            Block = block;
            BlockIndex = blockIndex;
            Date = date;
            Position = position;
        }

        public ITimelineRow Row { get; }

        /// <summary>Index in <see cref="ITimelineModel.Rows"/>.</summary>
        public int RowIndex { get; }

        public TimelineBlock Block { get; }

        /// <summary>Index in <see cref="ITimelineRow.Blocks"/>.</summary>
        public int BlockIndex { get; }

        /// <summary>Date of the block's day.</summary>
        public DateTime Date { get; }

        /// <summary>Press position relative to the <see cref="TimelineView"/>.</summary>
        public Point Position { get; }

        protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) =>
            ((TimelineBlockEventHandler)genericHandler)(genericTarget, this);
    }
}
