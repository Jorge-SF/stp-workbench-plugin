using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Maptek.Vulcan.Stp.Engine.Internal;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>
    /// The local actual progress ("Avance real") of a project: what was really done per activity and civil day, plus
    /// the days that show a value box. It never reaches VGS or the apply package. JS: <c>standaloneLocalState</c>
    /// (v1_compat_logic.js), whose four maps keep their names here. Immutable: every
    /// <see cref="ActualProgress"/> edit returns a new state.
    /// </summary>
    /// <remarks>
    /// Keys are <see cref="ActualProgress.Key"/> (<c>JSON.stringify([activityId, date])</c>). Maps enumerate in JS
    /// object order. Unlike JS, a state holds only numbers in the value maps and only <c>true</c> slots: JS copies any
    /// other stored value along without ever using it.
    /// </remarks>
    public sealed class ActualProgressState
    {
        /// <summary>The state without any progress.</summary>
        public static readonly ActualProgressState Empty = new ActualProgressState(new JsObjectMap<double>(), new JsObjectMap<double>(), new JsObjectMap<bool>(), new JsObjectMap<bool>());

        internal ActualProgressState(JsObjectMap<double> byDay, JsObjectMap<double> byFragment, JsObjectMap<bool> slots, JsObjectMap<bool> hidden)
        {
            DayMap = byDay;
            FragmentMap = byFragment;
            SlotMap = slots;
            HiddenMap = hidden;
        }

        /// <summary>Gets the value per activity day (<c>actualProgressByDay</c>), in JS order. Values may be negative (ignored by every rule).</summary>
        public IReadOnlyList<KeyValuePair<string, double>> ByDay => DayMap.Entries.ToList();

        /// <summary>Gets the legacy value per fragment (<c>actualProgressByFragment</c>), consumed by <see cref="ActualProgress.Migrate"/>.</summary>
        public IReadOnlyList<KeyValuePair<string, double>> ByFragment => FragmentMap.Entries.ToList();

        /// <summary>Gets the explicit slots (<c>actualProgressSlotsByDay</c>): days without plan that show a value box.</summary>
        public IReadOnlyList<string> Slots => SlotMap.Keys.ToList();

        /// <summary>Gets the hidden slots (<c>hiddenActualProgressSlotsByDay</c>): planned days whose box was removed or moved away.</summary>
        public IReadOnlyList<string> HiddenSlots => HiddenMap.Keys.ToList();

        /// <summary>
        /// Gets a value indicating whether anything was entered: the template turns the mode on at start when there
        /// are values or explicit slots (hidden slots alone do not count).
        /// </summary>
        public bool HasAny => FragmentMap.Count > 0 || DayMap.Count > 0 || SlotMap.Count > 0;

        internal JsObjectMap<double> DayMap { get; }

        internal JsObjectMap<double> FragmentMap { get; }

        internal JsObjectMap<bool> SlotMap { get; }

        internal JsObjectMap<bool> HiddenMap { get; }

        /// <summary>Builds a state from stored entries (later duplicates win, keeping the first position, like a JS object).</summary>
        /// <param name="byDay">Values per activity day.</param>
        /// <param name="byFragment">Legacy values per fragment.</param>
        /// <param name="slots">Explicit slot keys.</param>
        /// <param name="hiddenSlots">Hidden slot keys.</param>
        /// <returns>The state.</returns>
        public static ActualProgressState Create(
            IEnumerable<KeyValuePair<string, double>> byDay = null,
            IEnumerable<KeyValuePair<string, double>> byFragment = null,
            IEnumerable<string> slots = null,
            IEnumerable<string> hiddenSlots = null)
        {
            var day = new JsObjectMap<double>();
            foreach (var pair in byDay ?? Enumerable.Empty<KeyValuePair<string, double>>()) day[pair.Key ?? "undefined"] = pair.Value;
            var fragment = new JsObjectMap<double>();
            foreach (var pair in byFragment ?? Enumerable.Empty<KeyValuePair<string, double>>()) fragment[pair.Key ?? "undefined"] = pair.Value;
            var slot = new JsObjectMap<bool>();
            foreach (string key in slots ?? Enumerable.Empty<string>()) slot[key ?? "undefined"] = true;
            var hidden = new JsObjectMap<bool>();
            foreach (string key in hiddenSlots ?? Enumerable.Empty<string>()) hidden[key ?? "undefined"] = true;
            return new ActualProgressState(day, fragment, slot, hidden);
        }

        /// <summary>Whether two states hold the same entries, in any order.</summary>
        /// <param name="other">The other state.</param>
        /// <returns>Same content.</returns>
        public bool SameContent(ActualProgressState other)
        {
            if (other == null) return false;
            if (ReferenceEquals(this, other)) return true;
            return SameValues(DayMap, other.DayMap) && SameValues(FragmentMap, other.FragmentMap)
                && SameKeys(SlotMap, other.SlotMap) && SameKeys(HiddenMap, other.HiddenMap);
        }

        internal ActualProgressState With(JsObjectMap<double> byDay = null, JsObjectMap<double> byFragment = null, JsObjectMap<bool> slots = null, JsObjectMap<bool> hidden = null) =>
            new ActualProgressState(byDay ?? DayMap, byFragment ?? FragmentMap, slots ?? SlotMap, hidden ?? HiddenMap);

        private static bool SameValues(JsObjectMap<double> left, JsObjectMap<double> right) =>
            left.Count == right.Count && left.Entries.All(p => right.TryGetValue(p.Key, out double value) && value.Equals(p.Value));

        private static bool SameKeys(JsObjectMap<bool> left, JsObjectMap<bool> right) =>
            left.Count == right.Count && left.Keys.All(right.ContainsKey);
    }

    /// <summary>One move of a value (or a value box) from a day to another.</summary>
    public sealed class ActualProgressMove
    {
        /// <summary>Gets the day it leaves.</summary>
        public string OriginDate { get; init; }

        /// <summary>Gets the day it lands on.</summary>
        public string TargetDate { get; init; }

        /// <summary>Gets the value that moves, or <c>null</c> for an empty box.</summary>
        public double? Value { get; init; }
    }

    /// <summary>A segment of a visible day, as input for <see cref="ActualProgress.Migrate"/>.</summary>
    public sealed class ProgressSegmentRef
    {
        /// <summary>Gets the fragment id.</summary>
        public string FragmentId { get; init; }

        /// <summary>Gets the activity id (empty: the fragment id).</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets the civil day <c>YYYY-MM-DD</c>.</summary>
        public string Date { get; init; }
    }

    /// <summary>A plan segment of one visible day of a <see cref="ProgressRow"/>.</summary>
    public sealed class ProgressSegment
    {
        /// <summary>Gets a value indicating whether the work is completed in VGS (it does not count as planned).</summary>
        public bool Completed { get; init; }

        /// <summary>Gets the planned amount (the template's <c>scheduledAmount</c>).</summary>
        public double Amount { get; init; }

        /// <summary>Gets the equipment of the segment (local assignment included), or empty for the row's.</summary>
        public string Resource { get; init; }
    }

    /// <summary>One visible day of a <see cref="ProgressRow"/>; days without plan have no segments.</summary>
    public sealed class ProgressDay
    {
        /// <summary>Gets the civil day <c>YYYY-MM-DD</c>.</summary>
        public string Date { get; init; }

        /// <summary>Gets the segments of the day.</summary>
        public IReadOnlyList<ProgressSegment> Segments { get; init; } = Array.Empty<ProgressSegment>();
    }

    /// <summary>A timeline row as the actual-progress rules see it (template compatibility view row).</summary>
    public sealed class ProgressRow
    {
        /// <summary>Gets the activity id.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets the activity type (empty: <c>Sin tipo</c>).</summary>
        public string Type { get; init; }

        /// <summary>Gets the activity's own equipment (empty: <c>Sin recurso</c>).</summary>
        public IReadOnlyList<string> Resources { get; init; } = Array.Empty<string>();

        /// <summary>Gets every visible day, in order.</summary>
        public IReadOnlyList<ProgressDay> Days { get; init; } = Array.Empty<ProgressDay>();
    }

    /// <summary>The visible days and rows (template <c>currentView</c>).</summary>
    public sealed class ProgressView
    {
        /// <summary>Gets the visible days.</summary>
        public IReadOnlyList<string> Dates { get; init; } = Array.Empty<string>();

        /// <summary>Gets the rows.</summary>
        public IReadOnlyList<ProgressRow> Rows { get; init; } = Array.Empty<ProgressRow>();
    }

    /// <summary>Planned and actual quantity of one type on one day.</summary>
    public sealed class TypeProgressDay
    {
        /// <summary>Gets the day.</summary>
        public string Date { get; init; }

        /// <summary>Gets the planned (pending) quantity.</summary>
        public double Scheduled { get; init; }

        /// <summary>Gets the actual quantity, or <c>null</c> when nothing was entered for the day.</summary>
        public double? Actual { get; init; }
    }

    /// <summary>Planned vs actual of one activity type (template <c>buildActualProgressByType</c> row).</summary>
    public sealed class TypeProgress
    {
        /// <summary>Gets the type.</summary>
        public string Type { get; init; }

        /// <summary>Gets every date of <see cref="ActualProgressSummary.Dates"/>, in JS order.</summary>
        public IReadOnlyList<TypeProgressDay> Days { get; init; }

        /// <summary>Gets the planned quantity per equipment.</summary>
        public IReadOnlyList<KeyValuePair<string, double>> PlannedByResource { get; init; }

        /// <summary>Gets the actual quantity per equipment.</summary>
        public IReadOnlyList<KeyValuePair<string, double>> ActualByResource { get; init; }

        /// <summary>Gets the planned quantity per day and equipment (days with plan only).</summary>
        public IReadOnlyList<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, double>>>> PlannedByResourceByDate { get; init; }

        /// <summary>Gets the actual quantity per day and equipment (days with values only).</summary>
        public IReadOnlyList<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, double>>>> ActualByResourceByDate { get; init; }

        /// <summary>The planned/actual of one day, or <c>null</c> for a date outside the summary.</summary>
        /// <param name="date">The day.</param>
        /// <returns>The day.</returns>
        public TypeProgressDay Day(string date) => Days.FirstOrDefault(d => d.Date == date);

        /// <summary>The actual quantity per equipment of one day (empty when none).</summary>
        /// <param name="date">The day.</param>
        /// <returns>The totals.</returns>
        public IReadOnlyList<KeyValuePair<string, double>> ActualByResourceOn(string date) =>
            ActualByResourceByDate.FirstOrDefault(p => p.Key == date).Value ?? Array.Empty<KeyValuePair<string, double>>();
    }

    /// <summary>Planned vs actual per type (charts, the "Avance real" tab, exports).</summary>
    public sealed class ActualProgressSummary
    {
        /// <summary>Gets the dates: the visible days plus any segment day, sorted.</summary>
        public IReadOnlyList<string> Dates { get; init; }

        /// <summary>Gets the types in JS order (array-index names first, then first appearance).</summary>
        public IReadOnlyList<TypeProgress> Types { get; init; }
    }

    /// <summary>Period totals: planned and actual.</summary>
    public sealed class ProgressTotals
    {
        /// <summary>Gets the planned quantity.</summary>
        public double Scheduled { get; init; }

        /// <summary>Gets the actual quantity, or <c>null</c> when the mode is off (activity totals only).</summary>
        public double? Actual { get; init; }
    }

    /// <summary>
    /// C# port of the local actual-progress rules of v1_compat_logic.js (<c>localActualProgress*</c>,
    /// <c>*LocalActualProgress*</c>, <c>buildActualProgressByType</c>, the period totals) and of the template glue that
    /// combines them (short_term_planner_template_v2.html: drag finish, value box, context menu).
    /// Golden-tested against the JS: tools/golden/actual_progress.mjs.
    /// </summary>
    public static class ActualProgress
    {
        private const int IdMaximum = 240;
        private const string NoType = "Sin tipo";
        private const string NoResource = "Sin recurso";

        // ------------------------------------------------------------------ keys and queries

        /// <summary>The state key of an activity day (JS <c>localActualKey</c>): <c>JSON.stringify([id, date])</c>, or empty when invalid.</summary>
        /// <param name="activityId">The activity (or legacy fragment) id; trimmed, at most 240 characters.</param>
        /// <param name="date">A real civil day <c>YYYY-MM-DD</c>.</param>
        /// <returns>The key, or empty.</returns>
        public static string Key(string activityId, string date)
        {
            string id = SafeText(activityId);
            return id.Length > 0 && IsCalendarDate(date) ? JsonArray(id, date) : string.Empty;
        }

        /// <summary>The actual value of an activity day (JS <c>localActualProgress</c>): finite and not negative, else <c>null</c>.</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <returns>The value.</returns>
        public static double? Value(ActualProgressState state, string activityId, string date)
        {
            string key = Key(activityId, date);
            return key.Length > 0 && state != null && state.DayMap.TryGetValue(key, out double value) && IsFinite(value) && value >= 0 ? value : (double?)null;
        }

        /// <summary>Whether the day has an explicit slot (JS <c>hasLocalActualProgressSlot</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <returns>Has a slot.</returns>
        public static bool HasSlot(ActualProgressState state, string activityId, string date)
        {
            string key = Key(activityId, date);
            return key.Length > 0 && state != null && state.SlotMap.ContainsKey(key);
        }

        /// <summary>Whether the day's box is hidden (JS <c>hasHiddenLocalActualProgressSlot</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <returns>Hidden.</returns>
        public static bool HasHiddenSlot(ActualProgressState state, string activityId, string date)
        {
            string key = Key(activityId, date);
            return key.Length > 0 && state != null && state.HiddenMap.ContainsKey(key);
        }

        /// <summary>Whether any of the days has a visible explicit slot (JS <c>hasVisibleLocalActualProgressSlotInDays</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="dates">The days.</param>
        /// <returns>Has one.</returns>
        public static bool HasVisibleSlotInDays(ActualProgressState state, string activityId, IEnumerable<string> dates) =>
            dates != null && dates.Any(date => HasSlot(state, activityId, date) && !HasHiddenSlot(state, activityId, date));

        /// <summary>
        /// The activities with a visible explicit slot on one of the days (JS <c>visibleLocalActualProgressActivityIdsInDays</c>):
        /// they stay on the timeline even without plan in the range.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="dates">The visible days.</param>
        /// <returns>The activity ids, in slot order.</returns>
        public static IReadOnlyList<string> VisibleSlotActivityIds(ActualProgressState state, IEnumerable<string> dates)
        {
            var visible = new HashSet<string>((dates ?? Enumerable.Empty<string>()).Where(IsCalendarDate), StringComparer.Ordinal);
            var result = new List<string>();
            if (state == null) return result;
            foreach (string key in state.SlotMap.Keys)
            {
                if (!(TryParse(key) is List<object> parts)) continue;   // malformed keys are ignored, like the JS catch
                string id = parts.Count > 0 ? SafeText(parts[0] as string) : string.Empty;
                string date = parts.Count > 1 ? parts[1] as string : null;
                if (id.Length > 0 && date != null && visible.Contains(date) && !HasHiddenSlot(state, id, date) && !result.Contains(id)) result.Add(id);
            }

            return result;
        }

        // ------------------------------------------------------------------ edits

        /// <summary>Hides or shows a day's box (JS <c>setHiddenLocalActualProgressSlot</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <param name="hidden">Hide.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState SetHiddenSlot(ActualProgressState state, string activityId, string date, bool hidden)
        {
            state = state ?? ActualProgressState.Empty;
            string key = Key(activityId, date);
            if (key.Length == 0) return state;
            var map = state.HiddenMap.Copy();
            if (hidden) map[key] = true;
            else map.Remove(key);
            return state.With(hidden: map);
        }

        /// <summary>Adds or removes an explicit slot (JS <c>setLocalActualProgressSlot</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <param name="present">Add.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState SetSlot(ActualProgressState state, string activityId, string date, bool present)
        {
            state = state ?? ActualProgressState.Empty;
            string key = Key(activityId, date);
            if (key.Length == 0) return state;
            var map = state.SlotMap.Copy();
            if (present) map[key] = true;
            else map.Remove(key);
            return state.With(slots: map);
        }

        /// <summary>
        /// "Eliminar avance real" (JS <c>clearLocalActualProgressSlot</c>): removes the day's value and slot; a planned
        /// day is hidden, any other is simply gone.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <param name="scheduled">Whether the day has plan.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState ClearSlot(ActualProgressState state, string activityId, string date, bool scheduled)
        {
            state = state ?? ActualProgressState.Empty;
            string key = Key(activityId, date);
            if (key.Length == 0) return state;
            var byDay = state.DayMap.Copy();
            var slots = state.SlotMap.Copy();
            var hidden = state.HiddenMap.Copy();
            byDay.Remove(key);
            slots.Remove(key);
            if (scheduled) hidden[key] = true;
            else hidden.Remove(key);
            return state.With(byDay, null, slots, hidden);
        }

        /// <summary>
        /// "Agregar avance real" (JS <c>restoreLocalActualProgressSlot</c>): shows the day's box again; a day without
        /// plan gets an explicit slot.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <param name="scheduled">Whether the day has plan.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState RestoreSlot(ActualProgressState state, string activityId, string date, bool scheduled)
        {
            state = state ?? ActualProgressState.Empty;
            string key = Key(activityId, date);
            if (key.Length == 0) return state;
            var slots = state.SlotMap.Copy();
            var hidden = state.HiddenMap.Copy();
            hidden.Remove(key);
            if (!scheduled) slots[key] = true;
            return state.With(slots: slots, hidden: hidden);
        }

        /// <summary>Sets a day's value (JS <c>setLocalActualProgress</c>, four arguments); a negative or non-finite value changes nothing.</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <param name="value">The value.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState SetValue(ActualProgressState state, string activityId, string date, double value)
        {
            state = state ?? ActualProgressState.Empty;
            string key = Key(activityId, date);
            if (key.Length == 0 || !IsFinite(value) || value < 0) return state;
            var byDay = state.DayMap.Copy();
            byDay[key] = value;
            return state.With(byDay: byDay);
        }

        /// <summary>Sets a legacy per-fragment value (JS <c>setLocalActualProgress</c>, three arguments).</summary>
        /// <param name="state">The state.</param>
        /// <param name="fragmentId">The fragment.</param>
        /// <param name="value">The value.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState SetFragmentValue(ActualProgressState state, string fragmentId, double value)
        {
            state = state ?? ActualProgressState.Empty;
            string id = SafeText(fragmentId);
            if (id.Length == 0 || !IsFinite(value) || value < 0) return state;
            var map = state.FragmentMap.Copy();
            map[id] = value;
            return state.With(byFragment: map);
        }

        /// <summary>
        /// The value box's change (template input handler): <c>null</c> (blank) deletes the day's value, anything else
        /// is <see cref="SetValue"/>.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="date">The day.</param>
        /// <param name="value">The typed value, or <c>null</c> when the box was emptied.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState EditValue(ActualProgressState state, string activityId, string date, double? value)
        {
            state = state ?? ActualProgressState.Empty;
            if (value.HasValue) return SetValue(state, activityId, date, value.Value);
            var byDay = state.DayMap.Copy();
            byDay.Remove(JsonArray(activityId, date));   // the template deletes the raw key, without trimming
            return state.With(byDay: byDay);
        }

        /// <summary>Moves one positive value to another day (JS <c>moveLocalActualProgress</c>; exported, unused by the HTML).</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="originDate">The day it leaves.</param>
        /// <param name="targetDate">The day it lands on.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState Move(ActualProgressState state, string activityId, string originDate, string targetDate)
        {
            state = state ?? ActualProgressState.Empty;
            string originKey = Key(activityId, originDate);
            string targetKey = Key(activityId, targetDate);
            if (originKey.Length == 0 || targetKey.Length == 0 || originKey == targetKey
                || !state.DayMap.TryGetValue(originKey, out double value) || !IsFinite(value) || value <= 0)
            {
                return state;
            }

            var byDay = state.DayMap.Copy();
            byDay.Remove(originKey);
            byDay[targetKey] = value;
            return state.With(byDay: byDay);
        }

        /// <summary>
        /// The value cascade of a move (JS <c>visibleLocalActualProgressCascadeMoves</c>): every positive value from the
        /// origin onwards (forward) or up to it (backward) shifts by the same number of visible days.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="originDate">The day dragged.</param>
        /// <param name="targetDate">The day dropped on.</param>
        /// <param name="visibleDates">The visible days.</param>
        /// <returns>The moves (empty when nothing moves).</returns>
        public static IReadOnlyList<ActualProgressMove> CascadeMoves(ActualProgressState state, string activityId, string originDate, string targetDate, IEnumerable<string> visibleDates)
        {
            string activity = SafeText(activityId);
            var dates = UniqueDates(visibleDates);
            int originIndex = dates.IndexOf(originDate);
            int targetIndex = dates.IndexOf(targetDate);
            int offset = targetIndex - originIndex;
            double? origin = Value(state, activity, originDate);
            if (activity.Length == 0 || originIndex < 0 || targetIndex < 0 || offset == 0 || !origin.HasValue || origin.Value <= 0)
            {
                return Array.Empty<ActualProgressMove>();
            }

            return dates
                .Where((date, index) => offset > 0 ? index >= originIndex : index <= originIndex)
                .Select(date => (date, value: Value(state, activity, date)))
                .Where(item => item.value.HasValue && item.value.Value > 0)
                .Select(item => new ActualProgressMove { OriginDate = item.date, TargetDate = AddDays(item.date, offset), Value = item.value })
                .Where(item => item.TargetDate.Length > 0)
                .ToList();
        }

        /// <summary>Applies <see cref="CascadeMoves"/> to the values (JS <c>moveVisibleLocalActualProgressCascade</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="originDate">The day dragged.</param>
        /// <param name="targetDate">The day dropped on.</param>
        /// <param name="visibleDates">The visible days.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState MoveCascade(ActualProgressState state, string activityId, string originDate, string targetDate, IEnumerable<string> visibleDates)
        {
            state = state ?? ActualProgressState.Empty;
            var moves = CascadeMoves(state, activityId, originDate, targetDate, visibleDates);
            if (moves.Count == 0) return state;
            string activity = SafeText(activityId);
            var byDay = state.DayMap.Copy();
            foreach (var move in moves) byDay.Remove(Key(activity, move.OriginDate));
            foreach (var move in moves) byDay[Key(activity, move.TargetDate)] = move.Value.Value;
            return state.With(byDay: byDay);
        }

        /// <summary>
        /// The box cascade of a drag (JS <c>visibleLocalActualProgressSlotCascadeMoves</c>): forward, every visible box
        /// from the origin onwards shifts by the same number of visible days; backward, only the dragged box moves.
        /// Values travel with their boxes (<c>null</c> for an empty box).
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="originDate">The day dragged.</param>
        /// <param name="targetDate">The day dropped on.</param>
        /// <param name="visibleDates">The visible days.</param>
        /// <param name="visibleSlotDates">The days that show a box.</param>
        /// <returns>The moves.</returns>
        public static IReadOnlyList<ActualProgressMove> SlotCascadeMoves(ActualProgressState state, string activityId, string originDate, string targetDate, IEnumerable<string> visibleDates, IEnumerable<string> visibleSlotDates)
        {
            string activity = SafeText(activityId);
            var dates = UniqueDates(visibleDates);
            int originIndex = dates.IndexOf(originDate);
            int targetIndex = dates.IndexOf(targetDate);
            int offset = targetIndex - originIndex;
            if (activity.Length == 0 || originIndex < 0 || targetIndex < 0 || offset == 0) return Array.Empty<ActualProgressMove>();
            if (offset < 0)
            {
                return new[] { new ActualProgressMove { OriginDate = originDate, TargetDate = targetDate, Value = Value(state, activity, originDate) } };
            }

            var slots = new HashSet<string>(visibleSlotDates ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            return dates
                .Where((date, index) => slots.Contains(date) && index >= originIndex)
                .Select(date => new ActualProgressMove { OriginDate = date, TargetDate = AddDays(date, offset), Value = Value(state, activity, date) })
                .ToList();
        }

        /// <summary>Applies <see cref="SlotCascadeMoves"/> to the values (JS <c>moveVisibleLocalActualProgressSlotCascade</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="activityId">The activity.</param>
        /// <param name="originDate">The day dragged.</param>
        /// <param name="targetDate">The day dropped on.</param>
        /// <param name="visibleDates">The visible days.</param>
        /// <param name="visibleSlotDates">The days that show a box.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState MoveSlotCascade(ActualProgressState state, string activityId, string originDate, string targetDate, IEnumerable<string> visibleDates, IEnumerable<string> visibleSlotDates)
        {
            state = state ?? ActualProgressState.Empty;
            var moves = SlotCascadeMoves(state, activityId, originDate, targetDate, visibleDates, visibleSlotDates);
            if (moves.Count == 0) return state;
            string activity = SafeText(activityId);
            var byDay = state.DayMap.Copy();
            foreach (var move in moves) byDay.Remove(Key(activity, move.OriginDate));
            foreach (var move in moves)
            {
                string key = Key(activity, move.TargetDate);
                if (move.Value.HasValue && IsFinite(move.Value.Value) && move.Value.Value >= 0) byDay[key] = move.Value.Value;
                else byDay.Remove(key);
            }

            return state.With(byDay: byDay);
        }

        /// <summary>
        /// Moves legacy values to the activity day (JS <c>migrateLocalActualProgress</c>, run on every render): a
        /// fragment-day key of a visible segment becomes its activity day, a per-fragment total goes to the fragment's
        /// first visible day, invalid values are dropped and the per-fragment map is consumed.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="visibleSegments">The visible segments, in view order.</param>
        /// <returns>The new state.</returns>
        public static ActualProgressState Migrate(ActualProgressState state, IEnumerable<ProgressSegmentRef> visibleSegments)
        {
            state = state ?? ActualProgressState.Empty;
            var byDay = new JsObjectMap<double>();
            var activityDayByFragmentDay = new Dictionary<string, string>(StringComparer.Ordinal);
            var firstActivityDayByFragment = new Dictionary<string, string>(StringComparer.Ordinal);
            var visibleActivityDays = new HashSet<string>(StringComparer.Ordinal);
            foreach (var segment in visibleSegments ?? Enumerable.Empty<ProgressSegmentRef>())
            {
                if (segment == null) continue;
                string id = SafeText(segment.FragmentId);
                string date = IsCalendarDate(segment.Date) ? segment.Date : string.Empty;
                string activityId = SafeText(segment.ActivityId);
                if (activityId.Length == 0) activityId = id;
                if (id.Length == 0 || date.Length == 0 || activityId.Length == 0) continue;
                string fragmentDay = Key(id, date);
                string activityDay = Key(activityId, date);
                activityDayByFragmentDay[fragmentDay] = activityDay;
                visibleActivityDays.Add(activityDay);
                if (!firstActivityDayByFragment.ContainsKey(id)) firstActivityDayByFragment[id] = activityDay;
            }

            void Add(string key, double value)
            {
                if (string.IsNullOrEmpty(key) || !IsFinite(value) || value < 0) return;
                byDay[key] = (byDay.TryGetValue(key, out double old) && old != 0 ? old : 0) + value;
            }

            foreach (var pair in state.DayMap.Entries)
            {
                string target = visibleActivityDays.Contains(pair.Key) ? pair.Key
                    : (activityDayByFragmentDay.TryGetValue(pair.Key, out string mapped) && mapped.Length > 0 ? mapped : pair.Key);
                Add(target, pair.Value);
            }

            foreach (var pair in state.FragmentMap.Entries)
            {
                Add(firstActivityDayByFragment.TryGetValue(pair.Key, out string day) ? day : string.Empty, pair.Value);
            }

            return new ActualProgressState(byDay, new JsObjectMap<double>(), state.SlotMap.Copy(), state.HiddenMap.Copy());
        }

        // ------------------------------------------------------------------ template glue (one timeline row)

        /// <summary>The visible days of a row (template <c>visibleActualProgressDates</c>).</summary>
        /// <param name="row">The row.</param>
        /// <returns>The days.</returns>
        public static IReadOnlyList<string> VisibleDates(ProgressRow row) =>
            (row?.Days ?? Array.Empty<ProgressDay>()).Select(d => d?.Date).Where(d => !string.IsNullOrEmpty(d)).ToList();

        /// <summary>Whether the row's day has plan segments (template <c>actualProgressDayIsScheduled</c>).</summary>
        /// <param name="row">The row.</param>
        /// <param name="date">The day.</param>
        /// <returns>Scheduled.</returns>
        public static bool IsScheduled(ProgressRow row, string date) => (FindDay(row, date)?.Segments?.Count ?? 0) > 0;

        /// <summary>The days of a row that show a value box (template <c>visibleActualProgressSlotDates</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="row">The row.</param>
        /// <returns>The days.</returns>
        public static IReadOnlyList<string> VisibleSlotDates(ActualProgressState state, ProgressRow row) =>
            (row?.Days ?? Array.Empty<ProgressDay>())
                .Where(day => day != null && !string.IsNullOrEmpty(day.Date) && !HasHiddenSlot(state, row.ActivityId, day.Date)
                    && ((day.Segments?.Count ?? 0) > 0 || HasSlot(state, row.ActivityId, day.Date)))
                .Select(day => day.Date).ToList();

        /// <summary>Whether a row's day shows a value box (template <c>renderTimelineRow</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="row">The row.</param>
        /// <param name="date">The day.</param>
        /// <returns>Shows a box.</returns>
        public static bool ShowsBox(ActualProgressState state, ProgressRow row, string date)
        {
            var day = FindDay(row, date);
            if (day == null) return false;
            return !HasHiddenSlot(state, row.ActivityId, date) && ((day.Segments?.Count ?? 0) > 0 || HasSlot(state, row.ActivityId, date));
        }

        /// <summary>Whether "Agregar avance real" is offered on a row's day (template <c>handleTimelineContextMenu</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="row">The row.</param>
        /// <param name="date">The day.</param>
        /// <returns>Can add.</returns>
        public static bool CanAddSlot(ActualProgressState state, ProgressRow row, string date)
        {
            var day = FindDay(row, date);
            if (string.IsNullOrEmpty(date) || day == null || row == null) return false;
            bool scheduled = (day.Segments?.Count ?? 0) > 0;
            bool hidden = HasHiddenSlot(state, row.ActivityId, date);
            return !HasSlot(state, row.ActivityId, date) && !(scheduled && !hidden);
        }

        /// <summary>
        /// Finishes a box drag (template <c>applyVisibleActualProgressSlotCascade</c>): moves the boxes and their values,
        /// hides planned origins, removes the other origins' slots and shows every target.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <param name="row">The row.</param>
        /// <param name="originDate">The day dragged.</param>
        /// <param name="targetDate">The day dropped on.</param>
        /// <param name="result">The new state (the same state when nothing moves).</param>
        /// <returns>Whether anything moved.</returns>
        public static bool TryApplySlotCascade(ActualProgressState state, ProgressRow row, string originDate, string targetDate, out ActualProgressState result)
        {
            result = state ?? ActualProgressState.Empty;
            if (row == null) return false;
            var visibleDates = VisibleDates(row);
            var visibleSlotDates = VisibleSlotDates(result, row);
            var moves = SlotCascadeMoves(result, row.ActivityId, originDate, targetDate, visibleDates, visibleSlotDates);
            if (moves.Count == 0) return false;
            result = MoveSlotCascade(result, row.ActivityId, originDate, targetDate, visibleDates, visibleSlotDates);
            result = ReslotMoves(result, row, moves);
            return true;
        }

        /// <summary>Moves the values of a row as a cascade (template <c>applyVisibleActualProgressCascade</c>).</summary>
        /// <param name="state">The state.</param>
        /// <param name="row">The row.</param>
        /// <param name="originDate">The day dragged.</param>
        /// <param name="targetDate">The day dropped on.</param>
        /// <param name="result">The new state.</param>
        /// <returns>Whether anything moved.</returns>
        public static bool TryApplyCascade(ActualProgressState state, ProgressRow row, string originDate, string targetDate, out ActualProgressState result)
        {
            result = state ?? ActualProgressState.Empty;
            if (row == null) return false;
            var visibleDates = VisibleDates(row);
            var moves = CascadeMoves(result, row.ActivityId, originDate, targetDate, visibleDates);
            if (moves.Count == 0) return false;
            result = MoveCascade(result, row.ActivityId, originDate, targetDate, visibleDates);
            result = ReslotMoves(result, row, moves);
            return true;
        }

        // ------------------------------------------------------------------ summaries

        /// <summary>
        /// Planned vs actual per type, equipment and day (JS <c>buildActualProgressByType</c>): pending segments are
        /// planned; any entered value of a visible day is actual, attributed to the day's first pending equipment.
        /// </summary>
        /// <param name="view">The visible days and rows.</param>
        /// <param name="state">The state.</param>
        /// <returns>The summary.</returns>
        public static ActualProgressSummary BuildByType(ProgressView view, ActualProgressState state)
        {
            var rows = view?.Rows ?? Array.Empty<ProgressRow>();
            var segments = new List<(string Type, string Resource, string Date, double Amount)>();
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.ActivityId)) continue;
                foreach (var day in row.Days ?? Array.Empty<ProgressDay>())
                {
                    if (day == null || string.IsNullOrEmpty(day.Date)) continue;
                    foreach (var segment in day.Segments ?? Array.Empty<ProgressSegment>())
                    {
                        if (segment == null || segment.Completed) continue;
                        string resource = Text(segment.Resource);
                        segments.Add((RowType(row), resource.Length > 0 ? resource : RowResources(row)[0], day.Date, SegmentAmount(segment)));
                    }
                }
            }

            var dates = (view?.Dates ?? Array.Empty<string>()).Where(d => d != null).Concat(segments.Select(s => s.Date))
                .Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList();
            var byType = new JsObjectMap<TypeBuilder>();
            TypeBuilder Ensure(string type)
            {
                if (!byType.TryGetValue(type, out var builder))
                {
                    builder = new TypeBuilder(type, dates);
                    byType[type] = builder;
                }

                return builder;
            }

            foreach (var item in segments)
            {
                var builder = Ensure(item.Type);
                builder.Scheduled[item.Date] += item.Amount;
                AddResource(builder.PlannedByResource, item.Resource, item.Amount);
                AddResource(builder.ByDate(builder.PlannedByResourceByDate, item.Date), item.Resource, item.Amount);
            }

            foreach (var row in rows)
            {
                string activityId = SafeText(row?.ActivityId);
                if (activityId.Length == 0) continue;
                string type = RowType(row);
                foreach (var day in row.Days ?? Array.Empty<ProgressDay>())
                {
                    string date = day != null && IsCalendarDate(day.Date) ? day.Date : string.Empty;
                    double? actual = Value(state, activityId, date);
                    if (date.Length == 0 || !actual.HasValue) continue;
                    var builder = Ensure(type);
                    if (!builder.Scheduled.ContainsKey(date)) continue;   // JS throws on a row day outside the view's days
                    builder.Actual[date] = (builder.Actual.TryGetValue(date, out double? old) && old.HasValue ? old.Value : 0) + actual.Value;
                    string resource = ResourceForDay(row, day);
                    AddResource(builder.ActualByResource, resource, actual.Value);
                    AddResource(builder.ByDate(builder.ActualByResourceByDate, date), resource, actual.Value);
                }
            }

            return new ActualProgressSummary { Dates = dates, Types = byType.Entries.Select(p => p.Value.Build()).ToList() };
        }

        /// <summary>
        /// One activity's totals over its rendered days (JS <c>buildActivityPeriodTotals</c>): every segment counts as
        /// planned (completed included); the actual is <c>null</c> when the mode is off.
        /// </summary>
        /// <param name="row">The row, restricted to the period's days.</param>
        /// <param name="state">The state.</param>
        /// <param name="showActualProgress">Whether the mode is on.</param>
        /// <returns>The totals.</returns>
        public static ProgressTotals ActivityPeriodTotals(ProgressRow row, ActualProgressState state, bool showActualProgress)
        {
            string activityId = SafeText(row?.ActivityId);
            double scheduled = 0;
            double actual = 0;
            foreach (var day in row?.Days ?? Array.Empty<ProgressDay>())
            {
                foreach (var segment in day?.Segments ?? Array.Empty<ProgressSegment>())
                {
                    if (segment != null && IsFinite(segment.Amount)) scheduled += segment.Amount;
                }

                if (!showActualProgress || activityId.Length == 0 || day?.Date == null) continue;
                double? value = Value(state, activityId, day.Date);
                if (value.HasValue) actual += value.Value;
            }

            return new ProgressTotals { Scheduled = scheduled, Actual = showActualProgress ? actual : (double?)null };
        }

        /// <summary>A type's totals over some days (JS <c>buildActualProgressPeriodTotals</c>; repeated days count twice).</summary>
        /// <param name="type">The type row.</param>
        /// <param name="dates">The days.</param>
        /// <returns>The totals (<see cref="ProgressTotals.Actual"/> is never <c>null</c>).</returns>
        public static ProgressTotals TypePeriodTotals(TypeProgress type, IEnumerable<string> dates)
        {
            double scheduled = 0;
            double actual = 0;
            foreach (string date in (dates ?? Enumerable.Empty<string>()).Where(IsCalendarDate))
            {
                var day = type?.Day(date);
                if (day == null) continue;
                scheduled += day.Scheduled;
                actual += day.Actual ?? 0;
            }

            return new ProgressTotals { Scheduled = scheduled, Actual = actual };
        }

        // ------------------------------------------------------------------ JS helpers

        /// <summary>JS <c>calendarParts</c>: <c>YYYY-MM-DD</c> naming a real day of years 0001-9999.</summary>
        /// <param name="value">The text.</param>
        /// <returns>Valid.</returns>
        public static bool IsCalendarDate(string value)
        {
            if (value == null || value.Length != 10 || value[4] != '-' || value[7] != '-') return false;
            for (int i = 0; i < 10; i++)
            {
                if (i != 4 && i != 7 && (value[i] < '0' || value[i] > '9')) return false;
            }

            int year = int.Parse(value.Substring(0, 4), CultureInfo.InvariantCulture);
            int month = int.Parse(value.Substring(5, 2), CultureInfo.InvariantCulture);
            int day = int.Parse(value.Substring(8, 2), CultureInfo.InvariantCulture);
            return year >= 1 && month >= 1 && month <= 12 && day >= 1 && day <= CivilTime.DaysInMonth(year, month);
        }

        /// <summary>JS <c>addDays</c> of v1_compat_logic.js: no range check, the year is zero-padded to four characters.</summary>
        internal static string AddDays(string date, long days)
        {
            if (!IsCalendarDate(date)) return string.Empty;
            long year = long.Parse(date.Substring(0, 4), CultureInfo.InvariantCulture);
            long month = long.Parse(date.Substring(5, 2), CultureInfo.InvariantCulture);
            long day = long.Parse(date.Substring(8, 2), CultureInfo.InvariantCulture);
            var result = CivilTime.DateFromDayNumber(CivilTime.DayNumber(year, month, day) + days);
            return result.Year.ToString(CultureInfo.InvariantCulture).PadLeft(4, '0') + "-"
                + result.Month.ToString("00", CultureInfo.InvariantCulture) + "-" + result.Day.ToString("00", CultureInfo.InvariantCulture);
        }

        private static ActualProgressState ReslotMoves(ActualProgressState state, ProgressRow row, IReadOnlyList<ActualProgressMove> moves)
        {
            foreach (var move in moves)
            {
                state = IsScheduled(row, move.OriginDate)
                    ? SetHiddenSlot(state, row.ActivityId, move.OriginDate, true)
                    : SetSlot(state, row.ActivityId, move.OriginDate, false);
            }

            foreach (var move in moves) state = RestoreSlot(state, row.ActivityId, move.TargetDate, IsScheduled(row, move.TargetDate));
            return state;
        }

        private static ProgressDay FindDay(ProgressRow row, string date) => row?.Days?.FirstOrDefault(d => d != null && d.Date == date);

        private static List<string> UniqueDates(IEnumerable<string> dates) =>
            (dates ?? Enumerable.Empty<string>()).Where(IsCalendarDate).Distinct(StringComparer.Ordinal).ToList();

        private static string RowType(ProgressRow row)
        {
            string type = Text(row?.Type);
            return type.Length > 0 ? type : NoType;
        }

        private static IReadOnlyList<string> RowResources(ProgressRow row)
        {
            var result = (row?.Resources ?? Array.Empty<string>()).Select(Text).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            return result.Count > 0 ? result : new List<string> { NoResource };
        }

        // template: the first pending segment's equipment, else the row's
        private static string ResourceForDay(ProgressRow row, ProgressDay day)
        {
            var pending = (day.Segments ?? Array.Empty<ProgressSegment>()).FirstOrDefault(s => s != null && !s.Completed);
            string resource = Text(pending?.Resource);
            return resource.Length > 0 ? resource : RowResources(row)[0];
        }

        // JS segmentAmount with only scheduledAdvance set: a negative or non-finite amount falls back to a width ratio of 1
        private static double SegmentAmount(ProgressSegment segment) => IsFinite(segment.Amount) && segment.Amount >= 0 ? segment.Amount : 1;

        private static void AddResource(JsObjectMap<double> total, string resource, double amount)
        {
            string name = Text(resource);
            if (name.Length == 0) name = NoResource;
            total[name] = (total.TryGetValue(name, out double old) ? old : 0) + amount;
        }

        private static string Text(string value) => value == null ? string.Empty : Js.Trim(value);

        private static string SafeText(string value)
        {
            string result = Text(value);
            return result.Length > 0 && result.Length <= IdMaximum ? result : string.Empty;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static object TryParse(string key)
        {
            try
            {
                return MiniJson.Parse(key);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // JSON.stringify([a, b]) for two strings (a null element renders as null)
        private static string JsonArray(string first, string second)
        {
            var builder = new StringBuilder("[");
            AppendJsonString(builder, first);
            builder.Append(',');
            AppendJsonString(builder, second);
            return builder.Append(']').ToString();
        }

        // JSON.stringify of a string: well-formed (lone surrogates escaped), non-ASCII kept
        private static void AppendJsonString(StringBuilder builder, string value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); continue;
                    case '\\': builder.Append("\\\\"); continue;
                    case '\b': builder.Append("\\b"); continue;
                    case '\f': builder.Append("\\f"); continue;
                    case '\n': builder.Append("\\n"); continue;
                    case '\r': builder.Append("\\r"); continue;
                    case '\t': builder.Append("\\t"); continue;
                }

                bool lone = (char.IsHighSurrogate(c) && !(i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])))
                    || (char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(value[i - 1])));
                if (c < 0x20 || lone) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else builder.Append(c);
            }

            builder.Append('"');
        }

        private sealed class TypeBuilder
        {
            private readonly string type;

            public TypeBuilder(string type, IEnumerable<string> dates)
            {
                this.type = type;
                foreach (string date in dates)
                {
                    Scheduled[date] = 0;
                    Actual[date] = null;
                }
            }

            public JsObjectMap<double> Scheduled { get; } = new JsObjectMap<double>();

            public JsObjectMap<double?> Actual { get; } = new JsObjectMap<double?>();

            public JsObjectMap<double> PlannedByResource { get; } = new JsObjectMap<double>();

            public JsObjectMap<double> ActualByResource { get; } = new JsObjectMap<double>();

            public JsObjectMap<JsObjectMap<double>> PlannedByResourceByDate { get; } = new JsObjectMap<JsObjectMap<double>>();

            public JsObjectMap<JsObjectMap<double>> ActualByResourceByDate { get; } = new JsObjectMap<JsObjectMap<double>>();

            public JsObjectMap<double> ByDate(JsObjectMap<JsObjectMap<double>> map, string date)
            {
                if (!map.TryGetValue(date, out var totals))
                {
                    totals = new JsObjectMap<double>();
                    map[date] = totals;
                }

                return totals;
            }

            public TypeProgress Build() => new TypeProgress
            {
                Type = type,
                Days = Scheduled.Keys.Select(d => new TypeProgressDay { Date = d, Scheduled = Scheduled[d], Actual = Actual[d] }).ToList(),
                PlannedByResource = PlannedByResource.Entries.ToList(),
                ActualByResource = ActualByResource.Entries.ToList(),
                PlannedByResourceByDate = Nested(PlannedByResourceByDate),
                ActualByResourceByDate = Nested(ActualByResourceByDate),
            };

            private static IReadOnlyList<KeyValuePair<string, IReadOnlyList<KeyValuePair<string, double>>>> Nested(JsObjectMap<JsObjectMap<double>> map) =>
                map.Entries.Select(p => new KeyValuePair<string, IReadOnlyList<KeyValuePair<string, double>>>(p.Key, p.Value.Entries.ToList())).ToList();
        }
    }
}
