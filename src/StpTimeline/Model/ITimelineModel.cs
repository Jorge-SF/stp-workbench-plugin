using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Maptek.Vulcan.Stp.Timeline.Model
{
    /// <summary>What a timeline row shows.</summary>
    public enum TimelineRowKind
    {
        /// <summary>One activity: a grid row plus one block per planned day.</summary>
        Activity,

        /// <summary>A group header ("Barrenación (2)"): shorter, no blocks.</summary>
        Group,
    }

    /// <summary>Why an activity is drawn with an outline.</summary>
    public enum TimelineMoveKind
    {
        /// <summary>Unchanged.</summary>
        None,

        /// <summary>Moved by the planner directly: solid dark outline.</summary>
        Direct,

        /// <summary>Moved by a precedence cascade: dashed orange outline.</summary>
        Cascade,
    }

    /// <summary>One day of work of an activity.</summary>
    public readonly struct TimelineBlock
    {
        public TimelineBlock(int day, string label, bool isCompleted = false, object tag = null, string toolTip = null)
        {
            Day = day;
            Label = label;
            IsCompleted = isCompleted;
            Tag = tag;
            ToolTip = toolTip;
        }

        /// <summary>Day index from <see cref="ITimelineModel.StartDate"/> (0 = first column). May be outside the range.</summary>
        public int Day { get; }

        /// <summary>Text inside the block, usually the day's quantity ("300", "1.7k"). Null for none.</summary>
        public string Label { get; }

        /// <summary>Locked completed work: drawn hatched, without the move outline; it can't be dragged or moved.</summary>
        public bool IsCompleted { get; }

        /// <summary>Caller data handed back in block events (e.g. the fragment id).</summary>
        public object Tag { get; }

        /// <summary>
        /// Hover tooltip detail under the activity and date lines, e.g. the full quantity and the fragment's start
        /// and finish ("300 m · 16/11 07:00 — 16/11 19:00"). Null shows "Cantidad: <see cref="Label"/>".
        /// </summary>
        public string ToolTip { get; }
    }

    /// <summary>A grid row and its chart lane.</summary>
    public interface ITimelineRow
    {
        TimelineRowKind Kind { get; }

        /// <summary>Unique key, used by links and selection. Activities: the activity id.</summary>
        string Key { get; }

        /// <summary>ID column (activities only).</summary>
        string Id { get; }

        /// <summary>Actividad column; for a group row, the group title.</summary>
        string Name { get; }

        /// <summary>Recurso column.</summary>
        string Resource { get; }

        /// <summary>Plan column (pre-formatted total, e.g. "1,200 m").</summary>
        string Plan { get; }

        /// <summary>Type colour: block fill, and the swatch of a group row.</summary>
        Color Color { get; }

        /// <summary>All work completed and locked: shows a lock after the ID.</summary>
        bool IsLocked { get; }

        TimelineMoveKind Move { get; }

        /// <summary>Group rows: number of activities in the group, shown as "(n)".</summary>
        int GroupCount { get; }

        /// <summary>Blocks in ascending <see cref="TimelineBlock.Day"/> order. Empty for groups.</summary>
        IReadOnlyList<TimelineBlock> Blocks { get; }
    }

    /// <summary>A precedence: an elbow line from the end of <see cref="FromKey"/> to the start of <see cref="ToKey"/>.</summary>
    public interface ITimelineLink
    {
        string FromKey { get; }
        string ToKey { get; }
    }

    /// <summary>Everything the timeline control draws. Raise <see cref="Changed"/> (on the UI thread) after edits.</summary>
    public interface ITimelineModel
    {
        /// <summary>Date of day index 0 (time of day ignored).</summary>
        DateTime StartDate { get; }

        /// <summary>Number of day columns.</summary>
        int DayCount { get; }

        /// <summary>"completado hasta" line: drawn at this instant. Null hides it.</summary>
        DateTime? CompletedThrough { get; }

        /// <summary>Rows in display order (group headers included).</summary>
        IReadOnlyList<ITimelineRow> Rows { get; }

        IReadOnlyList<ITimelineLink> Links { get; }

        event EventHandler Changed;
    }
}
