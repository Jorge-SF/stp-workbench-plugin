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
        public TimelineBlock(int day, string label, bool isCompleted = false, object tag = null, string toolTip = null, bool isCapacityExempt = false)
        {
            Day = day;
            Label = label;
            IsCompleted = isCompleted;
            Tag = tag;
            ToolTip = toolTip;
            IsCapacityExempt = isCapacityExempt;
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

        /// <summary>
        /// Exempted from the capacity limit ("Exentar Límite"): drawn green instead of the type colour (the browser
        /// planner's <c>.segment.capacity-exempt</c>, <c>--exempt-color: #68bd79</c>). Ignored for completed work.
        /// </summary>
        public bool IsCapacityExempt { get; }
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
    /// <summary>One "Avance real" value box under a day (template <c>.actual-progress-control</c>).</summary>
    public readonly struct TimelineProgressBox
    {
        /// <summary>Initializes a new instance of the <see cref="TimelineProgressBox"/> struct.</summary>
        /// <param name="day">Day index (0 = <see cref="ITimelineModel.StartDate"/>).</param>
        /// <param name="value">The entered value, or <c>null</c> for an empty box.</param>
        /// <param name="text">The value as shown and edited (empty for none).</param>
        /// <param name="tag">Host data.</param>
        public TimelineProgressBox(int day, double? value, string text, object tag = null)
        {
            Day = day;
            Value = value;
            Text = text ?? string.Empty;
            Tag = tag;
        }

        /// <summary>Gets the day index.</summary>
        public int Day { get; }

        /// <summary>Gets the value, or <c>null</c>.</summary>
        public double? Value { get; }

        /// <summary>Gets the text.</summary>
        public string Text { get; }

        /// <summary>Gets the host data.</summary>
        public object Tag { get; }
    }

    /// <summary>
    /// Optional: a row with "Avance real" value boxes, drawn when <see cref="TimelineView.ShowActualProgress"/> is on.
    /// Boxes are sorted by day.
    /// </summary>
    public interface ITimelineProgressRow
    {
        /// <summary>Gets the value boxes.</summary>
        IReadOnlyList<TimelineProgressBox> ProgressBoxes { get; }
    }

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
