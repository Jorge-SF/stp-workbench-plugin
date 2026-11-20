using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Maptek.Vulcan.Stp.Bridge.Contracts;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>Presentation facts of one activity, read from its Activity Grid row.</summary>
    public sealed class ActivityInfo
    {
        /// <summary>Gets the activity id.</summary>
        public string Id { get; init; }

        /// <summary>Gets the row label: the human name, or <c>Actividad &lt;id&gt;</c>.</summary>
        public string Name { get; init; }

        /// <summary>Gets the activity type (<c>Sin tipo</c> when the grid has none).</summary>
        public string Type { get; init; }

        /// <summary>Gets the level (<c>Sin nivel</c> when the grid has none).</summary>
        public string Level { get; init; }

        /// <summary>Gets the VGS resource (<c>Sin recurso</c> when the grid has none).</summary>
        public string Resource { get; init; }

        /// <summary>Gets the daily rate (quantity per civil day), or <c>null</c>.</summary>
        public double? Rate { get; init; }

        /// <summary>Gets the quantity unit, or empty.</summary>
        public string Unit { get; init; }

        /// <summary>Gets the snapshot order.</summary>
        public int SourceIndex { get; init; }

        /// <summary>Gets the grid row (column name to text).</summary>
        public IReadOnlyDictionary<string, string> Grid { get; init; }
    }

    /// <summary>
    /// Reads names, types, resources and rates from the Activity Grid like reference/web/timeline_logic.js
    /// (<c>activityIdentity</c>, <c>activityType</c>, <c>activityResource</c>, <c>activityRate</c>): column names are
    /// compared trimmed, with runs of spaces collapsed and lower-cased; the first non-blank value wins.
    /// </summary>
    public static class ActivityCatalog
    {
        /// <summary>The default when no type column has a value.</summary>
        public const string NoType = "Sin tipo";

        /// <summary>The default when no resource column has a value.</summary>
        public const string NoResource = "Sin recurso";

        /// <summary>The default when no level column has a value.</summary>
        public const string NoLevel = "Sin nivel";

        private static readonly string[] NameColumns = { "Description", "Name", "Activity Name" };
        private static readonly Regex IsoDateTime = new Regex(@"\d{4}-\d{2}-\d{2}T", RegexOptions.CultureInvariant);
        private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-MX");

        /// <summary>Builds the catalog of a snapshot.</summary>
        /// <param name="snapshot">The snapshot.</param>
        /// <param name="options">Column names.</param>
        /// <returns>Activity facts by id.</returns>
        public static IReadOnlyDictionary<string, ActivityInfo> Build(SnapshotV2 snapshot, PlannerOptions options)
        {
            var result = new Dictionary<string, ActivityInfo>(StringComparer.Ordinal);
            int index = 0;
            foreach (var activity in snapshot.Activities)
            {
                var grid = activity.GridColumns;
                result[activity.Id] = new ActivityInfo
                {
                    Id = activity.Id,
                    Name = Identity(activity),
                    Type = Text(grid, options.TypeColumns) is string type && type.Length > 0 ? type : NoType,
                    Level = Text(grid, options.LevelColumns) is string level && level.Length > 0 ? level : NoLevel,
                    Resource = Text(grid, options.ResourceColumns) is string resource && resource.Length > 0 ? resource : NoResource,
                    Rate = Rate(Text(grid, options.RateColumns)),
                    Unit = Text(grid, options.UnitColumns),
                    SourceIndex = index++,
                    Grid = activity.Grid,
                };
            }

            return result;
        }

        /// <summary>JS <c>normalizedKey</c>: trimmed, spaces collapsed, lower case (es-MX).</summary>
        /// <param name="value">A column name.</param>
        /// <returns>The normalized name.</returns>
        public static string NormalizedKey(string value) => Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ").ToLower(Spanish);

        private static string Text(IReadOnlyList<KeyValuePair<string, string>> grid, IEnumerable<string> accepted)
        {
            var keys = new HashSet<string>(accepted.Select(NormalizedKey), StringComparer.Ordinal);
            foreach (var column in grid)
            {
                if (keys.Contains(NormalizedKey(column.Key)) && !string.IsNullOrWhiteSpace(column.Value))
                {
                    return column.Value.Trim();
                }
            }

            return string.Empty;
        }

        private static double? Rate(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= 0 && !double.IsInfinity(value)
                ? value
                : (double?)null;
        }

        // JS activityIdentity + containsNativeBarKey.
        private static string Identity(SnapshotActivity activity)
        {
            foreach (string key in NameColumns)
            {
                if (activity.Grid.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value)
                    && !IsoDateTime.IsMatch(value) && !ContainsNativeBarKey(value, activity))
                {
                    return value.Trim();
                }
            }

            return "Actividad " + activity.Id;
        }

        private static bool ContainsNativeBarKey(string value, SnapshotActivity activity) => activity.NativeBars.Any(bar =>
        {
            string key = (bar.Key ?? string.Empty).Trim();
            return key.Length > 0 && Regex.IsMatch(value, @"(?:^|[^\p{L}\p{N}])" + Regex.Escape(key) + @"(?=$|[^\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        });
    }
}
