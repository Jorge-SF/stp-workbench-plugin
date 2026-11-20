using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Maptek.Vulcan.Stp.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>One "Avance real" value box under a timeline day (template <c>.actual-progress-control</c>).</summary>
    public sealed class ActualProgressBox
    {
        /// <summary>Gets the activity.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets the day <c>YYYY-MM-DD</c>.</summary>
        public string Date { get; init; }

        /// <summary>Gets the day index in <see cref="PlannerSession.Days"/>.</summary>
        public int DayIndex { get; init; }

        /// <summary>Gets the entered value, or <c>null</c> for an empty box.</summary>
        public double? Value { get; init; }

        /// <summary>Gets the value as typed back into the box (invariant), or empty.</summary>
        public string Text { get; init; }

        /// <summary>Gets a value indicating whether the day has plan (removing its box hides it instead).</summary>
        public bool IsScheduled { get; init; }
    }

    /// <summary>How an "Avance real" day compares with its plan.</summary>
    public enum ActualProgressLevel
    {
        /// <summary>Nothing entered.</summary>
        None,

        /// <summary>Less than planned (template <c>under-progress</c>).</summary>
        Under,

        /// <summary>At least the plan (template <c>met-progress</c>).</summary>
        Met,
    }

    /// <summary>One day of a type in the "Avance real" tab: <c>real/plan</c>.</summary>
    public sealed class ActualProgressCell
    {
        /// <summary>Gets the day.</summary>
        public string Date { get; init; }

        /// <summary>Gets the cell text (<c>8/10</c>, or <c>—</c>).</summary>
        public string Text { get; init; }

        /// <summary>Gets the hover text.</summary>
        public string ToolTip { get; init; }

        /// <summary>Gets how the day compares with its plan.</summary>
        public ActualProgressLevel State { get; init; }

        /// <summary>Gets a value indicating whether the day is a weekend.</summary>
        public bool IsWeekend { get; init; }

        /// <summary>Gets a value indicating whether the day is under plan.</summary>
        public bool IsUnder => State == ActualProgressLevel.Under;

        /// <summary>Gets a value indicating whether the day met its plan.</summary>
        public bool IsMet => State == ActualProgressLevel.Met;

        /// <summary>Gets a value indicating whether nothing was entered.</summary>
        public bool IsEmpty => State == ActualProgressLevel.None;
    }

    /// <summary>One type row of the "Avance real" tab (template aggregate row <c>Avance real · tipo</c>).</summary>
    public sealed class ActualProgressRow
    {
        /// <summary>Gets the type.</summary>
        public string Type { get; init; }

        /// <summary>Gets the label, <c>Avance real · tipo</c>.</summary>
        public string Label => "Avance real · " + Type;

        /// <summary>Gets the type colour.</summary>
        public string Color { get; init; } = TypePalette.Fallback;

        /// <summary>Gets one cell per visible day.</summary>
        public IReadOnlyList<ActualProgressCell> Cells { get; init; }

        /// <summary>Gets the period total, <c>Real: 12 / Plan: 20</c>.</summary>
        public string TotalText { get; init; }
    }

    /// <summary>Texts of the "Avance real" mode (template notices).</summary>
    public static class ActualProgressMessages
    {
        /// <summary>The mode is off.</summary>
        public const string ModeOff = "Active el modo de avance real.";

        /// <summary>No box on that day.</summary>
        public const string NoBox = "Ese día no tiene avance real.";

        /// <summary>A box is already there, or the day is planned.</summary>
        public const string CannotAdd = "Ese día ya tiene un avance real.";

        /// <summary>Not a valid value.</summary>
        public const string InvalidValue = "Ingrese un número mayor o igual a 0.";

        /// <summary>Nothing moved.</summary>
        public const string NothingToMove = "No hay avance real que desplazar a ese día.";

        /// <summary>Value saved.</summary>
        public const string ValueSaved = "Avance real actualizado sólo en la vista local. VGS no fue modificado.";

        /// <summary>Box added (template notice).</summary>
        public const string Added = "Se agregó un avance real vacío sólo en la vista local. VGS no fue modificado.";

        /// <summary>Box removed (template notice).</summary>
        public const string Removed = "Se eliminó el avance real sólo en la vista local. VGS no fue modificado.";

        /// <summary>Boxes moved (template notice).</summary>
        public const string Moved = "Avance real y su cascada visible desplazados sólo en la vista local. VGS no fue modificado.";

        /// <summary>Could not store the progress.</summary>
        public const string NotSaved = "No se pudo guardar el avance real local: ";

        /// <summary>JS <c>compactNumber</c>: integers as is, anything else with one decimal; <c>—</c> for no number.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The text.</returns>
        public static string CompactNumber(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return "—";
            double v = value.Value;
            return v == Math.Floor(v) && Math.Abs(v) < 1e21 ? v.ToString("R", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Stores the actual progress of a project (plugin-only key) in the browser's <c>localState</c> field names, so
    /// an "STP suelto" page can carry it.
    /// </summary>
    public static class ActualProgressStore
    {
        /// <summary>Storage key prefix.</summary>
        public const string StoragePrefix = "stp-plugin:actual-progress:";

        private const int MaxEntries = 20000;

        /// <summary>Reads the stored progress; anything unreadable reads as none.</summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        /// <returns>The state.</returns>
        public static ActualProgressState Load(IHorizonStorage storage, string projectKey)
        {
            string text;
            try
            {
                text = storage?.GetItem(StoragePrefix + projectKey);
            }
            catch (Exception)
            {
                return ActualProgressState.Empty;
            }

            if (string.IsNullOrEmpty(text)) return ActualProgressState.Empty;
            try
            {
                return FromJson(JToken.Parse(text) as JObject);
            }
            catch (JsonException)
            {
                return ActualProgressState.Empty;
            }
        }

        /// <summary>Stores the progress (an empty state removes nothing but is written as empty maps).</summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        /// <param name="state">The state.</param>
        public static void Save(IHorizonStorage storage, string projectKey, ActualProgressState state) =>
            storage.SetItem(StoragePrefix + projectKey, ToJson(state).ToString(Formatting.None));

        /// <summary>The template's <c>standaloneLocalState</c> shape.</summary>
        /// <param name="state">The state.</param>
        /// <returns>The JSON object.</returns>
        public static JObject ToJson(ActualProgressState state)
        {
            state = state ?? ActualProgressState.Empty;
            return new JObject
            {
                ["actualProgressByDay"] = new JObject(state.ByDay.Select(p => new JProperty(p.Key, p.Value))),
                ["actualProgressByFragment"] = new JObject(state.ByFragment.Select(p => new JProperty(p.Key, p.Value))),
                ["actualProgressSlotsByDay"] = new JObject(state.Slots.Select(k => new JProperty(k, true))),
                ["hiddenActualProgressSlotsByDay"] = new JObject(state.HiddenSlots.Select(k => new JProperty(k, true))),
            };
        }

        /// <summary>Reads the <c>standaloneLocalState</c> shape: numbers and <c>true</c> slots only.</summary>
        /// <param name="root">The object, or <c>null</c>.</param>
        /// <returns>The state.</returns>
        public static ActualProgressState FromJson(JObject root)
        {
            if (root == null) return ActualProgressState.Empty;
            IEnumerable<KeyValuePair<string, double>> Values(string name) => (root[name] as JObject)?.Properties().Take(MaxEntries)
                .Where(p => p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Float)
                .Select(p => new KeyValuePair<string, double>(p.Name, (double)p.Value)) ?? Enumerable.Empty<KeyValuePair<string, double>>();
            IEnumerable<string> Keys(string name) => (root[name] as JObject)?.Properties().Take(MaxEntries)
                .Where(p => p.Value.Type == JTokenType.Boolean && (bool)p.Value).Select(p => p.Name) ?? Enumerable.Empty<string>();
            return ActualProgressState.Create(Values("actualProgressByDay"), Values("actualProgressByFragment"), Keys("actualProgressSlotsByDay"), Keys("hiddenActualProgressSlotsByDay"));
        }
    }
}
