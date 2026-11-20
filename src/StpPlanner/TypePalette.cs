using System;
using System.Collections.Generic;
using System.Linq;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>
    /// Activity type colours (<c>#RRGGBB</c>) for chips, capacity rows and timeline blocks. The mockup's types keep the
    /// mockup's colours (docs/mockups/planner_layout.html <c>--t-*</c>); other types take the next free colour of the
    /// palette in name order, so one project always gets the same colours.
    /// </summary>
    public static class TypePalette
    {
        /// <summary>Colour of a type with no assignment.</summary>
        public const string Fallback = "#7B8794";

        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Barrenación"] = "#3B78C4",
            ["Desarrollo"] = "#2F8F5B",
            ["Relleno"] = "#8A5CC4",
            ["Rezagado"] = "#D07A2C",
            ["Sostenimiento"] = "#2B7F7A",
            ["Servicios"] = "#C2455A",
            ["Voladura"] = "#4F86C6",
        };

        private static readonly string[] Palette =
        {
            "#3B78C4", "#2F8F5B", "#8A5CC4", "#D07A2C", "#2B7F7A", "#C2455A", "#4F86C6",
            "#9C6B30", "#5E7D1E", "#B0478F", "#3E6D8E", "#A35D5D", "#6A5ACD", "#2E8B8B",
        };

        /// <summary>Assigns a colour to each type.</summary>
        /// <param name="types">The types (any order; duplicates ignored).</param>
        /// <returns>Type to colour.</returns>
        public static IReadOnlyDictionary<string, string> Assign(IEnumerable<string> types)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var distinct = (types ?? Enumerable.Empty<string>()).Where(t => t != null).Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t, StringComparer.Ordinal).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string type in distinct)
            {
                if (Known.TryGetValue(type, out string color))
                {
                    result[type] = color;
                    used.Add(color);
                }
            }

            int overflow = 0;
            foreach (string type in distinct.Where(t => !result.ContainsKey(t)).ToList())
            {
                // More types than colours: reuse the palette in order.
                string color = Palette.FirstOrDefault(c => !used.Contains(c)) ?? Palette[overflow++ % Palette.Length];
                used.Add(color);
                result[type] = color;
            }

            return result;
        }
    }
}
