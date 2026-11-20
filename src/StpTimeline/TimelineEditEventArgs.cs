using System;
using System.Collections.Generic;
using System.Windows;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline
{
    /// <summary>What a move carries.</summary>
    public enum TimelineMoveScope
    {
        /// <summary>The pressed day and every later pending day of the activity ("mover desde este día", the browser
        /// planner's daily-tail move, <c>moveDailyTail</c>). Plain drag, Ctrl+Left/Right on a focused block.</summary>
        Tail,

        /// <summary>Every pending day of the activity (<c>moveActivity</c>). Shift+drag, Ctrl+Shift+Left/Right.</summary>
        Activity,
    }

    /// <summary>The edit the user asked for. The control never edits the model; the host decides.</summary>
    public enum TimelineEditAction
    {
        /// <summary><see cref="TimelineView.MoveRequested"/>: shift by <see cref="TimelineEditEventArgs.DayDelta"/> days.</summary>
        Move,

        /// <summary><see cref="TimelineView.DeferRequested"/>: "Mandar al siguiente periodo" (<c>deferDailyTailToNextPeriod</c>).</summary>
        DeferToNextPeriod,

        /// <summary><see cref="TimelineView.StaggerRequested"/>: "Dividir y desfasar" (<c>staggerDailyTail</c>) with
        /// <see cref="TimelineEditEventArgs.Divisions"/> and <see cref="TimelineEditEventArgs.GapDays"/>.</summary>
        SplitAndStagger,

        /// <summary><see cref="TimelineView.AssignResourceRequested"/>: "Asignar equipo" (the host shows its picker).</summary>
        AssignResource,

        /// <summary><see cref="TimelineView.CapacityExemptionRequested"/>: "Exentar Límite" / "Quitar exención".</summary>
        ToggleCapacityExemption,

        /// <summary><see cref="TimelineView.UndoRequested"/>: Ctrl+Z.</summary>
        Undo,

        /// <summary><see cref="TimelineView.RedoRequested"/>: Ctrl+Y or Ctrl+Shift+Z.</summary>
        Redo,
    }

    /// <summary>Which input produced a request.</summary>
    public enum TimelineEditOrigin
    {
        Drag,
        Keyboard,
        ContextMenu,
    }

    public delegate void TimelineEditEventHandler(object sender, TimelineEditEventArgs e);

    /// <summary>
    /// An edit request. The control does not change the model: the handler (or <see cref="TimelineView.EditCommand"/>)
    /// applies it and refreshes the model, or calls <see cref="Reject"/> so the control flashes the blocks red with
    /// the reason. Rejections that come later (async) use <see cref="TimelineView.ShowBlocked(string, int, string)"/>.
    /// </summary>
    public sealed class TimelineEditEventArgs : RoutedEventArgs
    {
        internal TimelineEditEventArgs(RoutedEvent routedEvent, object source, TimelineEditAction action, TimelineEditOrigin origin,
            ITimelineRow row, int rowIndex, int blockIndex, DateTime? date, TimelineMoveScope scope, int dayDelta,
            int divisions = 0, int gapDays = 0)
            : base(routedEvent, source)
        {
            Action = action;
            Origin = origin;
            Row = row;
            RowIndex = rowIndex;
            BlockIndex = blockIndex;
            Block = row != null && blockIndex >= 0 && row.Blocks != null && blockIndex < row.Blocks.Count ? row.Blocks[blockIndex] : (TimelineBlock?)null;
            Date = date;
            Scope = scope;
            DayDelta = dayDelta;
            Divisions = divisions;
            GapDays = gapDays;
        }

        public TimelineEditAction Action { get; }

        public TimelineEditOrigin Origin { get; }

        /// <summary>The activity row (null for undo / redo).</summary>
        public ITimelineRow Row { get; }

        /// <summary>Activity id (<see cref="ITimelineRow.Key"/>), or null.</summary>
        public string ActivityKey => Row?.Key;

        /// <summary>Index in <see cref="ITimelineModel.Rows"/>, or -1.</summary>
        public int RowIndex { get; }

        /// <summary>The day block the action starts from (its <see cref="TimelineBlock.Tag"/> is the fragment), or
        /// null for a whole-activity move without a focused block and for undo / redo.</summary>
        public TimelineBlock? Block { get; }

        /// <summary>Index in <see cref="ITimelineRow.Blocks"/>, or -1.</summary>
        public int BlockIndex { get; }

        /// <summary>Date of <see cref="Block"/>'s day, or null.</summary>
        public DateTime? Date { get; }

        /// <summary>Moves: tail (from this day onward) or whole activity.</summary>
        public TimelineMoveScope Scope { get; }

        /// <summary>Moves: whole days, negative = earlier. Never 0.</summary>
        public int DayDelta { get; }

        /// <summary>Tail moves: the date the pressed block lands on (<see cref="Date"/> + <see cref="DayDelta"/>), the
        /// target date <c>moveDailyTail</c> takes.</summary>
        public DateTime? TargetDate => Date?.AddDays(DayDelta);

        /// <summary>Split and stagger: "N.º de divisiones" (&gt;= 1).</summary>
        public int Divisions { get; }

        /// <summary>Split and stagger: "Días de desfase" (&gt;= 1).</summary>
        public int GapDays { get; }

        /// <summary>Set by <see cref="Reject"/>.</summary>
        public string RejectReason { get; private set; }

        /// <summary>The engine refused: the control flashes the affected blocks red and shows the reason.</summary>
        public void Reject(string reason) => RejectReason = string.IsNullOrEmpty(reason) ? "No se pudo completar la operación solicitada." : reason;

        protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) =>
            ((TimelineEditEventHandler)genericHandler)(genericTarget, this);
    }

    /// <summary>One entry of the block context menu. The host can hide, disable or relabel it in
    /// <see cref="TimelineView.BlockMenuOpening"/>.</summary>
    public sealed class TimelineMenuItem
    {
        public TimelineMenuItem(TimelineEditAction action, string header, TimelineMoveScope scope = TimelineMoveScope.Tail, int dayDelta = 0, string gesture = null)
        {
            Action = action;
            Header = header;
            Scope = scope;
            DayDelta = dayDelta;
            InputGestureText = gesture;
        }

        public TimelineEditAction Action { get; }
        public TimelineMoveScope Scope { get; }
        public int DayDelta { get; }
        public string Header { get; set; }
        public string InputGestureText { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool IsEnabled { get; set; } = true;

        /// <summary>Why it is disabled (shown as the item's tooltip).</summary>
        public string DisabledReason { get; set; }

        /// <summary>A separator is drawn above this item.</summary>
        public bool StartsGroup { get; set; }
    }

    public delegate void TimelineMenuEventHandler(object sender, TimelineMenuEventArgs e);

    /// <summary>
    /// The block context menu is about to open. Default items, in the browser planner's order and labels:
    /// "Mover desde este día" ±1, "Mover actividad" ±1, "Asignar equipo", "Exentar Límite" (hidden until the host
    /// shows it), "Mandar al siguiente periodo", "Dividir y desfasar". Set <see cref="RoutedEventArgs.Handled"/> to
    /// suppress the menu.
    /// </summary>
    public sealed class TimelineMenuEventArgs : RoutedEventArgs
    {
        internal TimelineMenuEventArgs(RoutedEvent routedEvent, object source, ITimelineRow row, int rowIndex, int blockIndex, DateTime date, List<TimelineMenuItem> items)
            : base(routedEvent, source)
        {
            Row = row;
            RowIndex = rowIndex;
            BlockIndex = blockIndex;
            Block = row.Blocks[blockIndex];
            Date = date;
            Items = items;
        }

        public ITimelineRow Row { get; }
        public int RowIndex { get; }
        public TimelineBlock Block { get; }
        public int BlockIndex { get; }
        public DateTime Date { get; }

        /// <summary>Editable list of entries.</summary>
        public IList<TimelineMenuItem> Items { get; }

        /// <summary>The first item for an action (and scope / delta for moves), or null.</summary>
        public TimelineMenuItem Find(TimelineEditAction action)
        {
            foreach (var item in Items) if (item.Action == action) return item;
            return null;
        }

        protected override void InvokeEventHandler(Delegate genericHandler, object genericTarget) =>
            ((TimelineMenuEventHandler)genericHandler)(genericTarget, this);
    }
}
