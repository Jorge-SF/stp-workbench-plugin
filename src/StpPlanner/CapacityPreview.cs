using System;
using System.Collections.Generic;
using System.Linq;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>What a drag preview would exceed.</summary>
    public enum CapacityExcessKind
    {
        /// <summary>A type's daily amount goes over its objective ("Capacidad excedida para …").</summary>
        Capacity,

        /// <summary>A resource works more activities in a day than its fronts ("Frentes excedidos para …").</summary>
        Fronts,
    }

    /// <summary>One excess: a type (or resource) on a day. timeline_logic.js <c>previewExcesses</c> warning.</summary>
    public sealed class CapacityExcess
    {
        /// <summary>Gets the kind.</summary>
        public CapacityExcessKind Kind { get; init; }

        /// <summary>Gets the day <c>YYYY-MM-DD</c>.</summary>
        public string Date { get; init; }

        /// <summary>Gets the type (<see cref="CapacityExcessKind.Capacity"/>) or the resource (<see cref="CapacityExcessKind.Fronts"/>).</summary>
        public string Label { get; init; }

        /// <summary>Gets the browser message, e.g. <c>Capacidad excedida para Rezagado.</c>.</summary>
        public string Message { get; init; }

        /// <summary>Gets a value indicating whether the excess blocks the move (never: previews only warn).</summary>
        public bool Blocking => false;

        internal string Key => (Kind == CapacityExcessKind.Capacity ? "capacity" : "fronts") + "\u0000" + Label + "\u0000" + Date;
    }

    /// <summary>Front limits per resource (v1_compat_logic.js <c>resourceCapacities</c> / <c>dailyFronts</c>).</summary>
    public sealed class FrontLimits
    {
        /// <summary>Gets the fronts per resource.</summary>
        public IReadOnlyDictionary<string, double> PerResource { get; init; } = new Dictionary<string, double>();

        /// <summary>Gets the fronts of a resource without its own value, or <c>null</c> for no limit.</summary>
        public double? Daily { get; init; }

        internal double? For(string resource) =>
            resource != null && PerResource != null && PerResource.TryGetValue(resource, out double value) ? value : Daily;
    }

    /// <summary>
    /// Capacity preview of a candidate plan: which days and types (or resources) would go over their limit.
    /// A port of timeline_logic.js <c>previewExcesses</c> (L805-836) and of the baseline filter of
    /// <c>previewDailyTail</c> (L873-877): a preview shows only the excesses the candidate adds.
    /// </summary>
    public static class CapacityExcesses
    {
        /// <summary>
        /// timeline_logic.js <c>previewExcesses</c>. Per type and day, the pending amount (the width ratio for
        /// activities without a rate) over the objective; per resource and day, the distinct activities over the
        /// fronts. Capacity excesses first, then fronts, each in first-seen order (rows, then days), as the JS Map.
        /// One deliberate difference: segments exempted with "Exentar Límite" do not count toward the type amount,
        /// as in the capacity panel (v1_compat_logic.js <c>buildCapacityRows</c>, <c>typeDay.capacity</c>); the JS
        /// timeline view has no exemption flag, so for plans without exemptions the result is identical.
        /// Fronts count per segment resource (local equipment assignments included), as <c>buildCapacityRows</c>.
        /// </summary>
        /// <param name="rows">The rows (activity, type, segments).</param>
        /// <param name="objectives">Daily objective per type; types without one are not checked.</param>
        /// <param name="fronts">Front limits, or <c>null</c> to skip the fronts check.</param>
        /// <returns>The excesses.</returns>
        public static IReadOnlyList<CapacityExcess> Find(IEnumerable<PlannerRow> rows, IReadOnlyDictionary<string, double> objectives, FrontLimits fronts = null)
        {
            var typeTotals = new Dictionary<string, double>(StringComparer.Ordinal);
            var typeOrder = new List<(string Label, string Date, string Key)>();
            var resourceActivities = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var resourceOrder = new List<(string Label, string Date, string Key)>();
            foreach (var row in rows ?? Enumerable.Empty<PlannerRow>())
            {
                if (row?.Segments == null) continue;
                foreach (var day in row.Segments.Where(s => s.Status != SegmentStatus.Completed).GroupBy(s => s.Date, StringComparer.Ordinal))
                {
                    // JS: one amount per row-day (sum of its active segments), then added to the type total.
                    double amount = 0;
                    foreach (var segment in day)
                    {
                        if (segment.IsCapacityExempt) continue;
                        amount += segment.Amount.HasValue && !double.IsNaN(segment.Amount.Value) && !double.IsInfinity(segment.Amount.Value)
                            ? segment.Amount.Value : segment.WidthRatio;
                    }

                    string typeKey = row.Type + "\u0000" + day.Key;
                    if (typeTotals.TryGetValue(typeKey, out double total)) typeTotals[typeKey] = total + amount;
                    else
                    {
                        typeTotals[typeKey] = amount;
                        typeOrder.Add((row.Type, day.Key, typeKey));
                    }

                    foreach (string resource in day.Select(s => s.Resource ?? row.Resource).Distinct(StringComparer.Ordinal))
                    {
                        string resourceKey = resource + "\u0000" + day.Key;
                        if (!resourceActivities.TryGetValue(resourceKey, out var activities))
                        {
                            resourceActivities[resourceKey] = activities = new HashSet<string>(StringComparer.Ordinal);
                            resourceOrder.Add((resource, day.Key, resourceKey));
                        }

                        activities.Add(row.ActivityId);
                    }
                }
            }

            var result = new List<CapacityExcess>();
            foreach (var (label, date, key) in typeOrder)
            {
                if (objectives == null || label == null || !objectives.TryGetValue(label, out double objective)) continue;
                if (!double.IsNaN(objective) && !double.IsInfinity(objective) && objective >= 0 && typeTotals[key] > objective)
                    result.Add(new CapacityExcess { Kind = CapacityExcessKind.Capacity, Date = date, Label = label, Message = $"Capacidad excedida para {label}." });
            }

            if (fronts != null)
            {
                foreach (var (label, date, key) in resourceOrder)
                {
                    double? capacity = fronts.For(label);
                    if (capacity is double c && !double.IsNaN(c) && !double.IsInfinity(c) && c >= 0 && resourceActivities[key].Count > c)
                        result.Add(new CapacityExcess { Kind = CapacityExcessKind.Fronts, Date = date, Label = label, Message = $"Frentes excedidos para {label}." });
                }
            }

            return result;
        }

        /// <summary>The excesses of <paramref name="candidate"/> that <paramref name="baseline"/> does not have (previewDailyTail L873-877).</summary>
        /// <param name="baseline">Excesses of the current plan.</param>
        /// <param name="candidate">Excesses of the candidate plan.</param>
        /// <returns>The new excesses, in candidate order.</returns>
        public static IReadOnlyList<CapacityExcess> Added(IEnumerable<CapacityExcess> baseline, IEnumerable<CapacityExcess> candidate)
        {
            var known = new HashSet<string>((baseline ?? Enumerable.Empty<CapacityExcess>()).Select(e => e.Key), StringComparer.Ordinal);
            return (candidate ?? Enumerable.Empty<CapacityExcess>()).Where(e => !known.Contains(e.Key)).ToList();
        }
    }

    /// <summary>The capacity preview of a drag: the candidate's capacity rows and the excesses it adds.</summary>
    public sealed class CapacityPreview
    {
        /// <summary>Gets the dragged activity.</summary>
        public string ActivityId { get; init; }

        /// <summary>Gets the excesses the move would add (timeline_logic.js <c>previewDailyTail</c> <c>warnings</c>).</summary>
        public IReadOnlyList<CapacityExcess> Excesses { get; init; } = Array.Empty<CapacityExcess>();

        /// <summary>Gets the days with an added excess (<c>previewWarning</c> of the preview segments).</summary>
        public IReadOnlyCollection<string> WarningDates { get; init; } = Array.Empty<string>();

        /// <summary>Gets the capacity rows of the candidate plan; cells with an added excess have <see cref="CapacityCell.IsPreviewExcess"/>.</summary>
        public IReadOnlyList<CapacityRow> Capacity { get; init; } = Array.Empty<CapacityRow>();

        /// <summary>Gets a value indicating whether the move adds an excess (<c>hasNonBlockingWarning</c>).</summary>
        public bool HasWarning => Excesses.Count > 0;

        /// <summary>Gets one line per added excess, for the drag badge or a tooltip (empty without excesses).</summary>
        public string WarningText => string.Join(" ", Excesses.Select(e => e.Message).Distinct(StringComparer.Ordinal));

        /// <summary>Builds the preview from the current and candidate views.</summary>
        /// <param name="activityId">The dragged activity.</param>
        /// <param name="current">The current view.</param>
        /// <param name="candidate">The candidate view (same filters).</param>
        /// <param name="objectives">Daily objective per type.</param>
        /// <param name="fronts">Front limits, or <c>null</c>.</param>
        /// <returns>The preview.</returns>
        public static CapacityPreview Build(string activityId, PlannerView current, PlannerView candidate, IReadOnlyDictionary<string, double> objectives, FrontLimits fronts = null)
        {
            // Same rows as the capacity panel: every visible type, before the search filter.
            var added = CapacityExcesses.Added(
                CapacityExcesses.Find(current.CapacitySource, objectives, fronts),
                CapacityExcesses.Find(candidate.CapacitySource, objectives, fronts));
            var over = new HashSet<string>(added.Where(e => e.Kind == CapacityExcessKind.Capacity).Select(e => e.Label + "\u0000" + e.Date), StringComparer.Ordinal);
            return new CapacityPreview
            {
                ActivityId = activityId,
                Excesses = added,
                WarningDates = new HashSet<string>(added.Select(e => e.Date), StringComparer.Ordinal),
                Capacity = candidate.Capacity.Select(row => new CapacityRow
                {
                    Type = row.Type,
                    Objective = row.Objective,
                    Color = row.Color,
                    Cells = row.Cells.Select(cell => cell.WithPreview(over.Contains(row.Type + "\u0000" + cell.Date))).ToList(),
                }).ToList(),
            };
        }
    }
}
