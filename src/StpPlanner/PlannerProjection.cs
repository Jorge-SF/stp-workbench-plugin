using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Engine;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>What the screen shows, filters included. Built by <see cref="PlannerProjection.Build"/>.</summary>
    public sealed class PlannerView
    {
        /// <summary>Gets the visible days.</summary>
        public IReadOnlyList<HorizonDay> Days { get; init; } = Array.Empty<HorizonDay>();

        /// <summary>Gets the type chips (every type with visible work).</summary>
        public IReadOnlyList<TypeChip> TypeChips { get; init; } = Array.Empty<TypeChip>();

        /// <summary>Gets the filtered, grouped and sorted rows.</summary>
        public IReadOnlyList<PlannerRowGroup> Groups { get; init; } = Array.Empty<PlannerRowGroup>();

        /// <summary>Gets the filtered rows in display order.</summary>
        public IReadOnlyList<PlannerRow> Rows { get; init; } = Array.Empty<PlannerRow>();

        /// <summary>Gets the counters (over the visible range, before chip and search filters, like the browser metrics).</summary>
        public PlannerCounters Counters { get; init; } = new PlannerCounters();

        /// <summary>Gets the capacity rows (types shown by the chips).</summary>
        public IReadOnlyList<CapacityRow> Capacity { get; init; } = Array.Empty<CapacityRow>();

        /// <summary>Gets the rows the capacity rows sum (visible types, before the search filter).</summary>
        public IReadOnlyList<PlannerRow> CapacitySource { get; init; } = Array.Empty<PlannerRow>();

        /// <summary>Gets the selected activity's details, or <c>null</c>.</summary>
        public ActivityDetails Selected { get; init; }

        /// <summary>Gets the plan totals per unit (filtered rows).</summary>
        public IReadOnlyList<PlanTotal> PlanTotals { get; init; } = Array.Empty<PlanTotal>();

        /// <summary>Gets the activities with local movements in the whole model ("cambios sin aplicar").</summary>
        public int ChangedActivities { get; init; }
    }

    /// <summary>Filter, grouping and sort settings of the screen.</summary>
    public sealed class PlannerFilter
    {
        /// <summary>Gets the hidden types (chips off).</summary>
        public IReadOnlyCollection<string> HiddenTypes { get; init; } = Array.Empty<string>();

        /// <summary>Gets the grouping.</summary>
        public PlannerGrouping Grouping { get; init; }

        /// <summary>Gets the sort.</summary>
        public PlannerSort Sort { get; init; }

        /// <summary>Gets the grid column used by <see cref="PlannerSort.Attribute"/>.</summary>
        public string SortAttribute { get; init; } = string.Empty;

        /// <summary>Gets a value indicating whether the order is descending.</summary>
        public bool Descending { get; init; }

        /// <summary>Gets the search text (id, name, type, level, resource; case and accent insensitive).</summary>
        public string Search { get; init; } = string.Empty;

        /// <summary>Gets a value indicating whether completed work is shown ("Avance completado").</summary>
        public bool ShowCompleted { get; init; } = true;

        /// <summary>Gets the selected activity id, or <c>null</c>.</summary>
        public string SelectedActivityId { get; init; }

        /// <summary>Gets the daily objective per type.</summary>
        public IReadOnlyDictionary<string, double> Objectives { get; init; } = new Dictionary<string, double>();

        /// <summary>Gets the colour per type (<see cref="TypePalette.Assign"/>); missing types use <see cref="TypePalette.Fallback"/>.</summary>
        public IReadOnlyDictionary<string, string> TypeColors { get; init; } = new Dictionary<string, string>();

        /// <summary>Gets the local equipment assignments and capacity exemptions, or <c>null</c> for none.</summary>
        public LocalOverrides Overrides { get; init; }

        /// <summary>Gets activities shown even without a visible day (an "Avance real" box in the range).</summary>
        public IReadOnlyCollection<string> ExtraActivityIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>Pure projection of the engine state to rows, groups, counters, capacity and details.</summary>
    public static class PlannerProjection
    {
        private static readonly string[] Weekdays = { "dom", "lun", "mar", "mié", "jue", "vie", "sáb" };
        private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-MX");
        private static readonly CompareInfo SpanishCompare = Spanish.CompareInfo;

        /// <summary>Builds the view.</summary>
        /// <param name="snapshot">The snapshot (precedences).</param>
        /// <param name="catalog">Activity facts.</param>
        /// <param name="state">The controller state.</param>
        /// <param name="eligibility">Tail-move eligibility of a fragment.</param>
        /// <param name="activityEligibility">±day eligibility of an activity.</param>
        /// <param name="filter">Filters.</param>
        /// <returns>The view.</returns>
        public static PlannerView Build(
            SnapshotV2 snapshot,
            IReadOnlyDictionary<string, ActivityInfo> catalog,
            ControllerState state,
            Func<string, FragmentEligibility> eligibility,
            Func<string, ActivityEligibility> activityEligibility,
            PlannerFilter filter)
        {
            var fragmentsByActivity = state.Model.Fragments.GroupBy(f => f.ActivityId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var moved = MovedActivities(state.Model);
            var days = Days(state.Horizon);
            var dayIndex = days.ToDictionary(d => d.Date, d => d.Index, StringComparer.Ordinal);

            var extra = new HashSet<string>(filter.ExtraActivityIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var visible = new List<PlannerRow>();
            foreach (var info in catalog.Values.OrderBy(i => i.SourceIndex))
            {
                var fragments = fragmentsByActivity.TryGetValue(info.Id, out var list) ? list : new List<PlannerFragment>();
                var segments = new List<DaySegment>();
                if (state.Horizon != null)
                {
                    foreach (var fragment in fragments)
                    {
                        if (fragment.LockedCompleted && !filter.ShowCompleted) continue;
                        if (fragment.DisplaySuppressed == true) continue;
                        segments.AddRange(Segments(fragment, state.Horizon, dayIndex, info, filter.Overrides, eligibility));
                    }
                }

                // A row needs a visible day, or a visible "Avance real" box (template additionalVisibleActivityIds).
                if (segments.Count == 0 && !extra.Contains(info.Id)) continue;
                var dayResources = segments.Where(s => s.Status != SegmentStatus.Completed).Select(s => s.Resource).Distinct(StringComparer.Ordinal).ToList();
                segments.Sort((a, b) => a.DayIndex != b.DayIndex ? a.DayIndex.CompareTo(b.DayIndex) : CivilTime.Scalar(a.Start).CompareTo(CivilTime.Scalar(b.Start)));
                double? plan = info.Rate.HasValue ? segments.Where(s => s.Status != SegmentStatus.Completed).Sum(s => s.Amount ?? 0) : (double?)null;
                visible.Add(new PlannerRow
                {
                    ActivityId = info.Id,
                    Name = info.Name,
                    Type = info.Type,
                    Level = info.Level,
                    // template: the row lists the equipment of its visible days (local assignments included)
                    Resource = dayResources.Count == 0 ? info.Resource : string.Join(", ", dayResources),
                    PlanQuantity = plan,
                    PlanText = QuantityText(plan, info.Unit),
                    Unit = info.Unit,
                    Start = fragments.Count == 0 ? string.Empty : fragments.OrderBy(f => CivilTime.Scalar(f.Start)).First().Start,
                    Finish = fragments.Count == 0 ? string.Empty : fragments.OrderByDescending(f => CivilTime.Scalar(f.Finish)).First().Finish,
                    VisibleStart = segments.Count > 0 ? segments.OrderBy(s => CivilTime.Scalar(s.Start)).First().Start
                        : fragments.Count > 0 ? fragments.OrderBy(f => CivilTime.Scalar(f.Start)).First().Start
                        : state.Horizon?.HorizonStart ?? "0001-01-01T00:00:00",
                    Status = RowStatus(fragments, moved, info.Id),
                    IsLocked = fragments.Count > 0 && fragments.All(f => f.LockedCompleted),
                    HasPending = fragments.Any(f => !f.LockedCompleted),
                    IsSelected = info.Id == filter.SelectedActivityId,
                    CapacityExemptible = info.HasType,
                    Segments = segments,
                    SourceIndex = info.SourceIndex,
                });
            }

            var counters = new PlannerCounters
            {
                Activities = visible.Count,
                WithPending = visible.Count(r => r.Segments.Any(s => s.Status != SegmentStatus.Completed)),
                Moved = visible.Count(r => moved.TryGetValue(r.ActivityId, out var p) && p == "direct"),
                Cascaded = visible.Count(r => moved.TryGetValue(r.ActivityId, out var p) && p == "cascade"),
            };

            var hidden = new HashSet<string>(filter.HiddenTypes ?? Array.Empty<string>(), StringComparer.Ordinal);
            var chips = visible.GroupBy(r => r.Type).OrderBy(g => g.Key, Comparer<string>.Create(CompareBase))
                .Select(g => new TypeChip { Type = g.Key, Count = g.Count(), IsVisible = !hidden.Contains(g.Key), Color = ColorOf(filter.TypeColors, g.Key) }).ToList();
            var byType = visible.Where(r => !hidden.Contains(r.Type)).ToList();
            var rows = byType.Where(r => Matches(r, filter.Search)).ToList();
            rows.Sort(RowComparer(filter, catalog));

            var groups = filter.Grouping == PlannerGrouping.None
                ? new List<PlannerRowGroup> { new PlannerRowGroup { Key = string.Empty, Label = "Todas las actividades", Rows = rows } }
                : rows.GroupBy(r => GroupKey(r, filter.Grouping)).OrderBy(g => g.Key, Comparer<string>.Create(CompareBase))
                    .Select(g => new PlannerRowGroup { Key = g.Key, Label = g.Key, Rows = g.ToList() }).ToList();
            if (filter.Grouping == PlannerGrouping.None && rows.Count == 0) groups.Clear();

            return new PlannerView
            {
                Days = days,
                TypeChips = chips,
                Groups = groups,
                Rows = groups.SelectMany(g => g.Rows).ToList(),
                Counters = counters,
                CapacitySource = byType,
                Capacity = Capacity(days, chips.Where(c => c.IsVisible).Select(c => c.Type), byType, filter.Objectives, filter.TypeColors),
                Selected = Details(snapshot, catalog, fragmentsByActivity, moved, filter.SelectedActivityId, activityEligibility, filter.Overrides),
                PlanTotals = rows.Where(r => r.PlanQuantity.HasValue).GroupBy(r => r.Unit ?? string.Empty)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new PlanTotal { Unit = g.Key, Quantity = g.Sum(r => r.PlanQuantity.Value) }).ToList(),
                ChangedActivities = moved.Count,
            };
        }

        /// <summary>Activity id to <c>direct</c>/<c>cascade</c> for activities whose fragments moved (direct wins).</summary>
        /// <param name="model">The model.</param>
        /// <returns>The moved activities.</returns>
        public static IReadOnlyDictionary<string, string> MovedActivities(PlannerModel model)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fragment in model.Fragments)
            {
                bool shifted = fragment.Start != fragment.OriginalStart || fragment.Finish != fragment.OriginalFinish;
                if (!shifted || (fragment.Provenance != "direct" && fragment.Provenance != "cascade")) continue;
                if (fragment.Provenance == "direct" || !result.ContainsKey(fragment.ActivityId)) result[fragment.ActivityId] = fragment.Provenance;
            }

            return result;
        }

        /// <summary>The visible days of a horizon.</summary>
        /// <param name="horizon">The horizon, or <c>null</c>.</param>
        /// <returns>The days.</returns>
        public static IReadOnlyList<HorizonDay> Days(CivilHorizon horizon)
        {
            if (horizon == null) return Array.Empty<HorizonDay>();
            long count = CivilTime.DayCount(horizon.HorizonStart, horizon.HorizonFinish);
            var result = new List<HorizonDay>();
            for (int index = 0; index < count; index++)
            {
                string date = CivilTime.AddDays(horizon.HorizonStart, index).Substring(0, 10);
                var day = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).DayOfWeek;
                result.Add(new HorizonDay
                {
                    Date = date,
                    Index = index,
                    Weekday = Weekdays[(int)day],
                    Label = date.Substring(8, 2) + "/" + date.Substring(5, 2),
                    IsWeekend = day == DayOfWeek.Saturday || day == DayOfWeek.Sunday,
                });
            }

            return result;
        }

        /// <summary>Quantity text in es-MX (<c>8,500 t</c>), or empty.</summary>
        /// <param name="value">The quantity.</param>
        /// <param name="unit">The unit.</param>
        /// <returns>The text.</returns>
        public static string QuantityText(double? value, string unit) => value.HasValue
            ? (value.Value.ToString("#,0.##", Spanish) + (string.IsNullOrEmpty(unit) ? string.Empty : " " + unit))
            : string.Empty;

        /// <summary>
        /// Short quantity for timeline blocks and capacity cells, as in the mockup: <c>1.7k</c> from 1000, else an
        /// integer (one decimal when not whole). Invariant culture: the cells are too small for group separators.
        /// </summary>
        /// <param name="value">The quantity.</param>
        /// <returns>The text, or empty for NaN/infinity.</returns>
        public static string CompactQuantity(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return string.Empty;
            if (Math.Abs(value) >= 1000) return (value / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            double rounded = Math.Round(value, 1);
            return rounded == Math.Round(rounded)
                ? Math.Round(rounded).ToString("0", CultureInfo.InvariantCulture)
                : rounded.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>The colour of a type, or <see cref="TypePalette.Fallback"/>.</summary>
        /// <param name="colors">Type to colour, or <c>null</c>.</param>
        /// <param name="type">The type.</param>
        /// <returns>The colour.</returns>
        public static string ColorOf(IReadOnlyDictionary<string, string> colors, string type) =>
            type != null && colors != null && colors.TryGetValue(type, out string color) ? color : TypePalette.Fallback;

        /// <summary>Day shift pill text: <c>+1 día</c>, <c>-2 días</c>, or empty for 0.</summary>
        /// <param name="days">The shift.</param>
        /// <returns>The text.</returns>
        public static string ShiftText(long days) => days == 0 ? string.Empty
            : $"{(days > 0 ? "+" : "-")}{Math.Abs(days)} {(Math.Abs(days) == 1 ? "día" : "días")}";

        private static SegmentStatus Status(PlannerFragment fragment) =>
            fragment.LockedCompleted ? SegmentStatus.Completed
            : fragment.Provenance == "direct" ? SegmentStatus.Direct
            : fragment.Provenance == "cascade" ? SegmentStatus.Cascade
            : SegmentStatus.Pending;

        private static SegmentStatus RowStatus(List<PlannerFragment> fragments, IReadOnlyDictionary<string, string> moved, string id)
        {
            if (moved.TryGetValue(id, out string provenance)) return provenance == "direct" ? SegmentStatus.Direct : SegmentStatus.Cascade;
            return fragments.Count > 0 && fragments.All(f => f.LockedCompleted) ? SegmentStatus.Completed : SegmentStatus.Pending;
        }

        // timeline_logic.js segmentGroups.
        private static IEnumerable<DaySegment> Segments(PlannerFragment fragment, CivilHorizon horizon, IReadOnlyDictionary<string, int> dayIndex, ActivityInfo info, LocalOverrides overrides, Func<string, FragmentEligibility> eligibility)
        {
            double? rate = info.Rate;
            string unit = LocalOverrides.LogicalUnitId(fragment.ActivityId, fragment.LogicalUnitDate);
            string assigned = fragment.LockedCompleted ? null : overrides?.AssignmentOf(fragment.ActivityId, unit);
            bool exempt = info.HasType && !fragment.LockedCompleted && overrides != null && overrides.IsExempt(unit);
            long day = CivilTime.DayMicroseconds;
            long lower = CivilTime.Scalar(horizon.HorizonStart), upper = CivilTime.Scalar(horizon.HorizonFinish);
            long start = CivilTime.Scalar(fragment.Start), finish = CivilTime.Scalar(fragment.Finish);
            long clippedStart = Math.Max(start, lower), clippedFinish = Math.Min(finish, upper);
            if (clippedFinish <= clippedStart) yield break;
            long first = (clippedStart - lower) / day, last = (clippedFinish - lower + day - 1) / day;
            FragmentEligibility tail = null;
            for (long index = first; index < last; index++)
            {
                long dayStart = lower + index * day, dayFinish = dayStart + day;
                long segmentStart = Math.Max(start, dayStart), segmentFinish = Math.Min(finish, dayFinish);
                if (segmentFinish <= segmentStart) continue;
                string date = CivilTime.AddDays(horizon.HorizonStart, index).Substring(0, 10);
                tail = tail ?? SafeEligibility(eligibility, fragment.Id);
                double width = (double)(segmentFinish - segmentStart) / day;
                yield return new DaySegment
                {
                    FragmentId = fragment.Id,
                    NativeBarKey = fragment.NativeBarKey,
                    Date = date,
                    DayIndex = dayIndex.TryGetValue(date, out int i) ? i : (int)index,
                    Start = segmentStart == start ? fragment.Start : date + "T00:00:00",
                    Finish = segmentFinish == finish ? fragment.Finish : CivilTime.AddDays(date + "T00:00:00", 1),
                    LeftRatio = (double)(segmentStart - dayStart) / day,
                    WidthRatio = width,
                    Amount = rate.HasValue ? rate.Value * width : (double?)null,
                    Status = Status(fragment),
                    CanMoveTail = tail.Eligible,
                    TailGuidance = tail.Guidance ?? string.Empty,
                    LogicalUnitId = unit,
                    Resource = assigned ?? info.Resource,
                    HasResourceOverride = assigned != null,
                    IsCapacityExempt = exempt,
                };
            }
        }

        private static FragmentEligibility SafeEligibility(Func<string, FragmentEligibility> eligibility, string fragmentId)
        {
            try
            {
                return eligibility(fragmentId);
            }
            catch (EngineException error)
            {
                return new FragmentEligibility { Eligible = false, Reason = "invalid", Guidance = error.Message };
            }
        }

        private static bool Matches(PlannerRow row, string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            string needle = search.Trim();
            return new[] { row.ActivityId, row.Name, row.Type, row.Level, row.Resource }
                .Any(value => value != null && SpanishCompare.IndexOf(value, needle, Options) >= 0);
        }

        private static string GroupKey(PlannerRow row, PlannerGrouping grouping) =>
            grouping == PlannerGrouping.Level ? row.Level : grouping == PlannerGrouping.Resource ? row.Resource : row.Type;

        // localeCompare(..., 'es', { sensitivity: 'base' }), then ordinal for a stable total order.
        private static int CompareBase(string left, string right)
        {
            int order = SpanishCompare.Compare(left ?? string.Empty, right ?? string.Empty, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
            return order != 0 ? order : string.CompareOrdinal(left, right);
        }

        private static Comparison<PlannerRow> RowComparer(PlannerFilter filter, IReadOnlyDictionary<string, ActivityInfo> catalog)
        {
            int sign = filter.Descending ? -1 : 1;
            switch (filter.Sort)
            {
                case PlannerSort.Id:
                    return (a, b) => Then(sign * JsCompare.LocaleCompare(a.ActivityId, b.ActivityId), a, b);
                case PlannerSort.Attribute:
                    return (a, b) => Then(CompareAttribute(Attribute(catalog, a, filter.SortAttribute), Attribute(catalog, b, filter.SortAttribute), sign), a, b);
                case PlannerSort.Source:
                    return (a, b) => sign * a.SourceIndex.CompareTo(b.SourceIndex);
                default:
                    return (a, b) => Then(sign * CivilTime.Scalar(a.VisibleStart).CompareTo(CivilTime.Scalar(b.VisibleStart)), a, b);
            }
        }

        private static int Then(int order, PlannerRow a, PlannerRow b) => order != 0 ? order : a.SourceIndex.CompareTo(b.SourceIndex);

        private static string Attribute(IReadOnlyDictionary<string, ActivityInfo> catalog, PlannerRow row, string name) =>
            !string.IsNullOrEmpty(name) && catalog.TryGetValue(row.ActivityId, out var info) && info.Grid.TryGetValue(name, out string value) ? value : string.Empty;

        // v1_compat_logic.js compareAttributeValues: blanks last (both directions), numbers numerically.
        private static int CompareAttribute(string leftValue, string rightValue, int sign)
        {
            string left = (leftValue ?? string.Empty).Trim(), right = (rightValue ?? string.Empty).Trim();
            if (left.Length == 0 && right.Length == 0) return 0;
            if (left.Length == 0) return 1;
            if (right.Length == 0) return -1;
            int comparison = double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double l)
                && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
                ? l.CompareTo(r)
                : SpanishCompare.Compare(left, right, CompareOptions.IgnoreCase);
            return sign * comparison;
        }

        // v1_compat_logic.js buildCapacityRows (byType): pending segments only, over = objective > 0 && value > objective.
        private static IReadOnlyList<CapacityRow> Capacity(IReadOnlyList<HorizonDay> days, IEnumerable<string> types, List<PlannerRow> rows, IReadOnlyDictionary<string, double> objectives, IReadOnlyDictionary<string, string> colors)
        {
            var result = new List<CapacityRow>();
            foreach (string type in types)
            {
                double objective = objectives != null && objectives.TryGetValue(type, out double value) && value > 0 ? value : 0;
                var sums = new double[days.Count];
                var counted = new double[days.Count];   // v1_compat_logic.js: typeDay.capacity skips exempt days
                foreach (var segment in rows.Where(r => r.Type == type).SelectMany(r => r.Segments))
                {
                    if (segment.Status == SegmentStatus.Completed || segment.DayIndex < 0 || segment.DayIndex >= sums.Length) continue;
                    sums[segment.DayIndex] += segment.Amount ?? 0;
                    if (!segment.IsCapacityExempt) counted[segment.DayIndex] += segment.Amount ?? 0;
                }

                result.Add(new CapacityRow
                {
                    Type = type,
                    Objective = objective,
                    Color = ColorOf(colors, type),
                    Cells = days.Select(d => new CapacityCell
                    {
                        Date = d.Date, Value = sums[d.Index], CapacityValue = counted[d.Index], IsOver = objective > 0 && counted[d.Index] > objective, IsWeekend = d.IsWeekend,
                    }).ToList(),
                });
            }

            return result;
        }

        private static ActivityDetails Details(
            SnapshotV2 snapshot,
            IReadOnlyDictionary<string, ActivityInfo> catalog,
            Dictionary<string, List<PlannerFragment>> fragmentsByActivity,
            IReadOnlyDictionary<string, string> moved,
            string id,
            Func<string, ActivityEligibility> activityEligibility,
            LocalOverrides overrides)
        {
            if (id == null || !catalog.TryGetValue(id, out var info)) return null;
            var fragments = fragmentsByActivity.TryGetValue(id, out var list) ? list : new List<PlannerFragment>();
            var pending = fragments.Where(f => !f.LockedCompleted).OrderBy(f => CivilTime.Scalar(f.OriginalStart)).ToList();
            long shift = pending.Count == 0 ? 0 : (CivilTime.Scalar(pending[0].Start) - CivilTime.Scalar(pending[0].OriginalStart)) / CivilTime.DayMicroseconds;
            bool uniform = pending.All(f => CivilTime.Scalar(f.Start) - CivilTime.Scalar(f.OriginalStart) == shift * CivilTime.DayMicroseconds);
            double pendingDays = pending.Sum(f => (double)(CivilTime.Scalar(f.Finish) - CivilTime.Scalar(f.Start)) / CivilTime.DayMicroseconds);
            double? quantity = info.Rate.HasValue ? info.Rate.Value * pendingDays : (double?)null;

            LinkInfo Link(string other, SnapshotPrecedence link) => new LinkInfo
            {
                ActivityId = other,
                Name = catalog.TryGetValue(other, out var o) ? o.Name : other,
                Type = TypeAbbreviation(link.Type),
                Lag = LagText(link.LagMicroseconds),
                IsMoved = moved.ContainsKey(other),
            };

            var predecessors = snapshot.Precedences.Where(p => p.SuccessorId == id).Select(p => Link(p.PredecessorId, p)).ToList();
            var successors = snapshot.Precedences.Where(p => p.PredecessorId == id).Select(p => Link(p.SuccessorId, p)).ToList();
            var status = RowStatus(fragments, moved, id);
            var sources = status == SegmentStatus.Cascade ? predecessors.Where(p => p.IsMoved).Select(p => p.ActivityId).Distinct().ToList() : new List<string>();
            string statusText;
            switch (status)
            {
                case SegmentStatus.Direct:
                    statusText = uniform && shift != 0 ? $"Movida directamente ({ShiftText(shift)})" : "Movida directamente (cola diaria)";
                    break;
                case SegmentStatus.Cascade:
                    statusText = sources.Count > 0 ? "Movida por cascada desde " + string.Join(", ", sources) : "Movida por cascada";
                    break;
                case SegmentStatus.Completed:
                    statusText = "Completada";
                    break;
                default:
                    statusText = fragments.Any(f => f.LockedCompleted) ? "Sin cambios (con avance completado)" : "Sin cambios";
                    break;
            }

            ActivityEligibility move;
            try
            {
                move = activityEligibility(id);
            }
            catch (EngineException error)
            {
                move = new ActivityEligibility { ShowControls = false, Guidance = error.Message };
            }

            string unit = info.Unit;
            return new ActivityDetails
            {
                ActivityId = id,
                Name = info.Name,
                Type = info.Type,
                Level = info.Level,
                Resource = overrides?.ActivityResourceOf(id) ?? info.Resource,
                VgsResource = info.Resource,
                Start = fragments.Count == 0 ? string.Empty : fragments.OrderBy(f => CivilTime.Scalar(f.Start)).First().Start,
                Finish = fragments.Count == 0 ? string.Empty : fragments.OrderByDescending(f => CivilTime.Scalar(f.Finish)).First().Finish,
                OriginalStart = fragments.Count == 0 ? string.Empty : fragments.OrderBy(f => CivilTime.Scalar(f.OriginalStart)).First().OriginalStart,
                OriginalFinish = fragments.Count == 0 ? string.Empty : fragments.OrderByDescending(f => CivilTime.Scalar(f.OriginalFinish)).First().OriginalFinish,
                ShiftDays = uniform ? shift : 0,
                ShiftText = uniform ? ShiftText(shift) : (status == SegmentStatus.Direct ? "cola diaria" : string.Empty),
                Quantity = quantity,
                Rate = info.Rate,
                QuantityText = info.Rate.HasValue
                    ? $"{QuantityText(quantity, unit)} · {QuantityText(info.Rate, unit)}/día"
                    : string.Empty,
                Predecessors = predecessors,
                Successors = successors,
                Status = status,
                StatusText = statusText,
                CascadeSources = sources,
                CanShowMoveControls = move.ShowControls,
                CanMoveBackward = move.BackwardEnabled,
                CanMoveForward = move.ForwardEnabled,
                MoveGuidance = move.Guidance ?? string.Empty,
            };
        }

        /// <summary>FS/SS/FF/SF for a VGS precedence type.</summary>
        /// <param name="type">The type.</param>
        /// <returns>The abbreviation (or the type itself when unknown).</returns>
        public static string TypeAbbreviation(string type)
        {
            string key = new string((type ?? string.Empty).Where(char.IsLetter).ToArray()).ToUpperInvariant();
            switch (key)
            {
                case "FINISHSTART": case "FS": return "FS";
                case "STARTSTART": case "SS": return "SS";
                case "FINISHFINISH": case "FF": return "FF";
                case "STARTFINISH": case "SF": return "SF";
                default: return type ?? string.Empty;
            }
        }

        /// <summary>Lag text: whole days (<c>+1 d</c>), hours (<c>-2 h</c>), minutes, or empty for 0.</summary>
        /// <param name="microseconds">The lag.</param>
        /// <returns>The text.</returns>
        public static string LagText(long microseconds)
        {
            if (microseconds == 0) return string.Empty;
            string sign = microseconds > 0 ? "+" : "-";
            long abs = Math.Abs(microseconds);
            const long Hour = 3_600_000_000L, Minute = 60_000_000L;
            if (abs % CivilTime.DayMicroseconds == 0) return $"{sign}{abs / CivilTime.DayMicroseconds} d";
            if (abs % Hour == 0) return $"{sign}{abs / Hour} h";
            return $"{sign}{((double)abs / Minute).ToString("0.##", CultureInfo.InvariantCulture)} min";
        }
    }
}
