using System;
using System.Collections.Generic;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>Display status of a segment or row; priority completed &gt; direct &gt; cascade &gt; pending (timeline_logic.js <c>fragmentStatus</c>).</summary>
    public enum SegmentStatus
    {
        /// <summary>Pending work at its native position.</summary>
        Pending,

        /// <summary>Moved by a cascade from a predecessor.</summary>
        Cascade,

        /// <summary>Moved directly by the user.</summary>
        Direct,

        /// <summary>Completed work (locked).</summary>
        Completed,
    }

    /// <summary>The part of one fragment inside one visible civil day (timeline_logic.js <c>segmentGroups</c>).</summary>
    public sealed class DaySegment
    {
        /// <summary>Gets the fragment id.</summary>
        public string FragmentId { get; init; }

        /// <summary>Gets the native bar key.</summary>
        public string NativeBarKey { get; init; }

        /// <summary>Gets the civil day <c>YYYY-MM-DD</c>.</summary>
        public string Date { get; init; }

        /// <summary>Gets the day index inside the horizon (0 = first visible day).</summary>
        public int DayIndex { get; init; }

        /// <summary>Gets the segment start (civil text).</summary>
        public string Start { get; init; }

        /// <summary>Gets the segment finish (civil text).</summary>
        public string Finish { get; init; }

        /// <summary>Gets the offset of the start inside the day, 0-1.</summary>
        public double LeftRatio { get; init; }

        /// <summary>Gets the width inside the day, 0-1.</summary>
        public double WidthRatio { get; init; }

        /// <summary>Gets the scheduled amount (rate × width), or <c>null</c> without a rate.</summary>
        public double? Amount { get; init; }

        /// <summary>Gets the status.</summary>
        public SegmentStatus Status { get; init; }

        /// <summary>Gets a value indicating whether this day of the fragment can be tail-moved (fragmentEligibility).</summary>
        public bool CanMoveTail { get; init; }

        /// <summary>Gets the tail-move guidance when it cannot move.</summary>
        public string TailGuidance { get; init; }
    }

    /// <summary>One activity row of the planner grid (ID · Actividad · Recurso · Plan) with its timeline segments.</summary>
    public sealed class PlannerRow
    {
        /// <summary>Gets the activity id.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets the name.</summary>
        public string Name { get; init; }

        /// <summary>Gets the type.</summary>
        public string Type { get; init; }

        /// <summary>Gets the level.</summary>
        public string Level { get; init; }

        /// <summary>Gets the resource.</summary>
        public string Resource { get; init; }

        /// <summary>Gets the planned (pending, visible) quantity, or <c>null</c> without a rate.</summary>
        public double? PlanQuantity { get; init; }

        /// <summary>Gets the plan column text (e.g. <c>8 500 t</c>), or empty.</summary>
        public string PlanText { get; init; }

        /// <summary>Gets the unit, or empty.</summary>
        public string Unit { get; init; }

        /// <summary>Gets the current start of the whole activity (civil text).</summary>
        public string Start { get; init; }

        /// <summary>Gets the current finish of the whole activity (civil text).</summary>
        public string Finish { get; init; }

        /// <summary>Gets the first visible start (civil text), used for sorting.</summary>
        public string VisibleStart { get; init; }

        /// <summary>Gets the row status (highest segment status over all fragments).</summary>
        public SegmentStatus Status { get; init; }

        /// <summary>Gets a value indicating whether every fragment is completed (🔒).</summary>
        public bool IsLocked { get; init; }

        /// <summary>Gets a value indicating whether the activity has pending work.</summary>
        public bool HasPending { get; init; }

        /// <summary>Gets a value indicating whether this row is the selected activity.</summary>
        public bool IsSelected { get; init; }

        /// <summary>Gets the visible segments, by day.</summary>
        public IReadOnlyList<DaySegment> Segments { get; init; }

        /// <summary>Gets the snapshot order.</summary>
        public int SourceIndex { get; init; }
    }

    /// <summary>A group header row plus its rows.</summary>
    public sealed class PlannerRowGroup
    {
        /// <summary>Gets the group value (type, level or resource; empty when not grouped).</summary>
        public string Key { get; init; }

        /// <summary>Gets the header text.</summary>
        public string Label { get; init; }

        /// <summary>Gets the rows.</summary>
        public IReadOnlyList<PlannerRow> Rows { get; init; }

        /// <summary>Gets the number of rows.</summary>
        public int Count => Rows.Count;
    }

    /// <summary>An activity-type chip of the filter strip; clicking toggles <see cref="IsVisible"/>.</summary>
    public sealed class TypeChip
    {
        /// <summary>Gets the type.</summary>
        public string Type { get; init; }

        /// <summary>Gets the number of visible-range activities of this type.</summary>
        public int Count { get; init; }

        /// <summary>Gets a value indicating whether rows of this type are shown.</summary>
        public bool IsVisible { get; init; }

        /// <summary>Gets the type colour, <c>#RRGGBB</c> (<see cref="TypePalette"/>).</summary>
        public string Color { get; init; } = TypePalette.Fallback;

        /// <summary>Gets the chip text (the type name).</summary>
        public string Name => Type;
    }

    /// <summary>Summary counters ("Resumen").</summary>
    public sealed class PlannerCounters
    {
        /// <summary>Gets the activities with visible work.</summary>
        public int Activities { get; init; }

        /// <summary>Gets the activities with visible pending work ("con pendiente").</summary>
        public int WithPending { get; init; }

        /// <summary>Gets the activities moved directly ("movidas").</summary>
        public int Moved { get; init; }

        /// <summary>Gets the activities moved by cascade ("en cascada").</summary>
        public int Cascaded { get; init; }

        /// <summary>Gets a value indicating whether there are local movements (counters are highlighted).</summary>
        public bool HasChanges => Moved > 0 || Cascaded > 0;
    }

    /// <summary>One capacity cell: one type, one day.</summary>
    public sealed class CapacityCell
    {
        /// <summary>Gets the day <c>YYYY-MM-DD</c>.</summary>
        public string Date { get; init; }

        /// <summary>Gets the scheduled pending amount.</summary>
        public double Value { get; init; }

        /// <summary>Gets a value indicating whether the value exceeds the objective (red).</summary>
        public bool IsOver { get; init; }

        /// <summary>Gets a value indicating whether nothing is scheduled.</summary>
        public bool IsEmpty => Value == 0;

        /// <summary>Gets a value indicating whether the day is Saturday or Sunday.</summary>
        public bool IsWeekend { get; init; }

        /// <summary>Gets the cell text as in the mockup: <c>·</c> when empty, else the compact quantity (<c>1.7k</c>).</summary>
        public string Text => IsEmpty ? "·" : PlannerProjection.CompactQuantity(Value);
    }

    /// <summary>The capacity row of one activity type (v1_compat_logic.js <c>buildCapacityRows</c>, byType).</summary>
    public sealed class CapacityRow
    {
        /// <summary>Gets the type.</summary>
        public string Type { get; init; }

        /// <summary>Gets the daily objective (0 = none).</summary>
        public double Objective { get; init; }

        /// <summary>Gets the "meta" column text (<c>2,500</c>), or <c>—</c> without an objective.</summary>
        public string ObjectiveText => Objective > 0 ? PlannerProjection.QuantityText(Objective, null) : "—";

        /// <summary>Gets the type colour, <c>#RRGGBB</c>.</summary>
        public string Color { get; init; } = TypePalette.Fallback;

        /// <summary>Gets the cells, one per visible day.</summary>
        public IReadOnlyList<CapacityCell> Cells { get; init; }
    }

    /// <summary>A visible day of the horizon (timeline and capacity headers).</summary>
    public sealed class HorizonDay
    {
        /// <summary>Gets the date <c>YYYY-MM-DD</c>.</summary>
        public string Date { get; init; }

        /// <summary>Gets the index from the horizon start.</summary>
        public int Index { get; init; }

        /// <summary>Gets the weekday abbreviation (<c>lun</c> … <c>dom</c>).</summary>
        public string Weekday { get; init; }

        /// <summary>Gets the label <c>DD/MM</c>.</summary>
        public string Label { get; init; }

        /// <summary>Gets the day of the month <c>DD</c> (capacity header).</summary>
        public string DayOfMonth => Date != null && Date.Length >= 10 ? Date.Substring(8, 2) : string.Empty;

        /// <summary>Gets a value indicating whether the day is Saturday or Sunday.</summary>
        public bool IsWeekend { get; init; }
    }

    /// <summary>A precedence link as seen from the selected activity.</summary>
    public sealed class LinkInfo
    {
        /// <summary>Gets the other activity id.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets the other activity name.</summary>
        public string Name { get; init; }

        /// <summary>Gets the type abbreviation: FS, SS, FF or SF.</summary>
        public string Type { get; init; }

        /// <summary>Gets the lag text (e.g. <c>+1 d</c>, <c>-2 h</c>) or empty.</summary>
        public string Lag { get; init; }

        /// <summary>Gets a value indicating whether the other activity has local movements.</summary>
        public bool IsMoved { get; init; }

        /// <summary>Gets the pill text, e.g. <c>VOL-1425-01 FS</c>.</summary>
        public string Text => string.IsNullOrEmpty(Lag) ? $"{ActivityId} {Type}" : $"{ActivityId} {Type} {Lag}";
    }

    /// <summary>Details of the selected activity (side panel).</summary>
    public sealed class ActivityDetails
    {
        /// <summary>Gets the id.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets the name.</summary>
        public string Name { get; init; }

        /// <summary>Gets the type.</summary>
        public string Type { get; init; }

        /// <summary>Gets the level.</summary>
        public string Level { get; init; }

        /// <summary>Gets the resource.</summary>
        public string Resource { get; init; }

        /// <summary>Gets the current start (civil text).</summary>
        public string Start { get; init; }

        /// <summary>Gets the current finish (civil text).</summary>
        public string Finish { get; init; }

        /// <summary>Gets the native start (civil text).</summary>
        public string OriginalStart { get; init; }

        /// <summary>Gets the native finish (civil text).</summary>
        public string OriginalFinish { get; init; }

        /// <summary>Gets the shift of the pending work in whole days (0 when not moved).</summary>
        public long ShiftDays { get; init; }

        /// <summary>Gets the shift pill text (<c>+1 día</c>, <c>-2 días</c>) or empty.</summary>
        public string ShiftText { get; init; }

        /// <summary>Gets the pending quantity (rate × pending days), or <c>null</c>.</summary>
        public double? Quantity { get; init; }

        /// <summary>Gets the daily rate, or <c>null</c>.</summary>
        public double? Rate { get; init; }

        /// <summary>Gets the quantity text, e.g. <c>8 500 t · 1 700 t/día</c>.</summary>
        public string QuantityText { get; init; }

        /// <summary>Gets the predecessors.</summary>
        public IReadOnlyList<LinkInfo> Predecessors { get; init; }

        /// <summary>Gets the successors.</summary>
        public IReadOnlyList<LinkInfo> Successors { get; init; }

        /// <summary>Gets the row status.</summary>
        public SegmentStatus Status { get; init; }

        /// <summary>Gets the "Estado" text: why it moved (direct or cascade from which predecessors).</summary>
        public string StatusText { get; init; }

        /// <summary>Gets the moved predecessors that explain a cascade.</summary>
        public IReadOnlyList<string> CascadeSources { get; init; }

        /// <summary>Gets a value indicating whether the ±day controls are shown.</summary>
        public bool CanShowMoveControls { get; init; }

        /// <summary>Gets a value indicating whether −1 day is possible.</summary>
        public bool CanMoveBackward { get; init; }

        /// <summary>Gets a value indicating whether +1 day is possible.</summary>
        public bool CanMoveForward { get; init; }

        /// <summary>Gets the move guidance, or empty.</summary>
        public string MoveGuidance { get; init; }
    }

    /// <summary>Total plan of one unit (status bar "Plan total 26 150 t · 1 434 m").</summary>
    public sealed class PlanTotal
    {
        /// <summary>Gets the unit (empty when the grid has none).</summary>
        public string Unit { get; init; }

        /// <summary>Gets the total.</summary>
        public double Quantity { get; init; }
    }

    /// <summary>Result of a planner command, with the message the UI shows.</summary>
    public sealed class PlannerOutcome
    {
        /// <summary>Gets a value indicating whether the command succeeded.</summary>
        public bool Ok { get; init; }

        /// <summary>Gets the user message (Spanish).</summary>
        public string Message { get; init; }

        /// <summary>Gets the engine outcome code (see <c>OperationResult.Codes</c>), or a bridge error kind.</summary>
        public string Code { get; init; }

        /// <summary>Gets the engine's own message, when different from <see cref="Message"/>.</summary>
        public string Detail { get; init; }

        internal static PlannerOutcome Success(string message) => new PlannerOutcome { Ok = true, Message = message, Code = string.Empty, Detail = string.Empty };

        internal static PlannerOutcome Failure(string message, string code = "", string detail = "") =>
            new PlannerOutcome { Ok = false, Message = message, Code = code ?? string.Empty, Detail = detail ?? string.Empty };
    }
}
