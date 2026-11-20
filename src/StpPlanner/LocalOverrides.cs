using System;
using System.Collections.Generic;
using System.Linq;
using Maptek.Vulcan.Stp.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>
    /// Per-day view settings the browser keeps in its compatibility preferences
    /// (template <c>setFragmentResourceOverride</c> / <c>setFragmentCapacityExemption</c>): the equipment assigned to one
    /// day of an activity ("Asignar equipo") and days exempt from the capacity limit ("Exentar Límite"). Keyed by the
    /// logical unit, <c>&lt;activityId&gt;:&lt;logicalUnitDate&gt;</c> (timeline_logic.js <c>logicalUnitProjection</c>).
    /// Local only: they never reach VGS or the apply package, and they are not plan steps (no undo).
    /// </summary>
    public sealed class LocalOverrides
    {
        /// <summary>Storage key prefix (plugin-only; the browser's own key holds its full preference schema).</summary>
        public const string StoragePrefix = "stp-plugin:overrides:";

        private const int MaxEntries = 5000;

        private readonly Dictionary<string, string> resources = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> exemptions = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Gets the equipment per logical unit.</summary>
        public IReadOnlyDictionary<string, string> Resources => resources;

        /// <summary>Gets the logical units exempt from the capacity limit.</summary>
        public IReadOnlyCollection<string> Exemptions => exemptions;

        /// <summary>The logical unit id of a fragment.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="logicalUnitDate">The fragment's logical unit date <c>YYYY-MM-DD</c>.</param>
        /// <returns><c>activityId:date</c>.</returns>
        public static string LogicalUnitId(string activityId, string logicalUnitDate) => (activityId ?? string.Empty) + ":" + (logicalUnitDate ?? string.Empty);

        /// <summary>Reads the stored overrides of a project; anything unreadable reads as none.</summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        /// <returns>The overrides.</returns>
        public static LocalOverrides Load(IHorizonStorage storage, string projectKey)
        {
            var result = new LocalOverrides();
            string text;
            try
            {
                text = storage?.GetItem(StoragePrefix + projectKey);
            }
            catch (Exception)
            {
                return result;
            }

            if (string.IsNullOrEmpty(text)) return result;
            try
            {
                if (!(JToken.Parse(text) is JObject root)) return result;
                if (root["fragmentResourceOverrides"] is JObject map)
                {
                    foreach (var pair in map.Properties().Take(MaxEntries))
                    {
                        if (pair.Value.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)pair.Value)) result.resources[pair.Name] = ((string)pair.Value).Trim();
                    }
                }

                if (root["fragmentCapacityExemptions"] is JObject set)
                {
                    foreach (var pair in set.Properties().Take(MaxEntries))
                    {
                        if (pair.Value.Type == JTokenType.Boolean && (bool)pair.Value) result.exemptions.Add(pair.Name);
                    }
                }
            }
            catch (JsonException)
            {
                // a damaged entry is not worth an error: the user re-assigns
            }

            return result;
        }

        /// <summary>The equipment of a logical unit, or <c>null</c> for the activity's own.</summary>
        /// <param name="logicalUnitId">The logical unit.</param>
        /// <returns>The equipment.</returns>
        public string ResourceOf(string logicalUnitId) => logicalUnitId != null && resources.TryGetValue(logicalUnitId, out string value) ? value : null;

        /// <summary>Whether a logical unit is exempt.</summary>
        /// <param name="logicalUnitId">The logical unit.</param>
        /// <returns>Exempt.</returns>
        public bool IsExempt(string logicalUnitId) => logicalUnitId != null && exemptions.Contains(logicalUnitId);

        /// <summary>Sets or clears (<c>null</c>) the equipment of a logical unit.</summary>
        /// <param name="logicalUnitId">The logical unit.</param>
        /// <param name="resource">The equipment, or <c>null</c>.</param>
        /// <returns>Whether anything changed.</returns>
        public bool SetResource(string logicalUnitId, string resource)
        {
            if (string.IsNullOrEmpty(resource)) return resources.Remove(logicalUnitId);
            if (resources.TryGetValue(logicalUnitId, out string old) && old == resource) return false;
            resources[logicalUnitId] = resource;
            return true;
        }

        /// <summary>Sets or clears an exemption.</summary>
        /// <param name="logicalUnitId">The logical unit.</param>
        /// <param name="exempt">Exempt.</param>
        /// <returns>Whether anything changed.</returns>
        public bool SetExempt(string logicalUnitId, bool exempt) => exempt ? exemptions.Add(logicalUnitId) : exemptions.Remove(logicalUnitId);

        /// <summary>Stores the overrides of a project.</summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        public void Save(IHorizonStorage storage, string projectKey)
        {
            var root = new JObject
            {
                ["fragmentResourceOverrides"] = new JObject(resources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new JProperty(p.Key, p.Value))),
                ["fragmentCapacityExemptions"] = new JObject(exemptions.OrderBy(k => k, StringComparer.Ordinal).Select(k => new JProperty(k, true))),
            };
            storage.SetItem(StoragePrefix + projectKey, root.ToString(Formatting.None));
        }
    }
}
