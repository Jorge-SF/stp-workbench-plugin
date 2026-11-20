using System;
using System.Collections.Generic;
using System.Linq;
using Maptek.Vulcan.Stp.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>
    /// Equipment and per-day view settings the browser keeps in its compatibility preferences: the manual resources
    /// ("+ Recurso manual", template <c>addManualResource</c>), the equipment assigned to one day ("Asignar equipo",
    /// <c>setFragmentResourceOverride</c>) or to a whole activity (<c>setActivityResourceOverride</c>), and the days
    /// exempt from the capacity limit ("Exentar Límite", <c>setFragmentCapacityExemption</c>). Days are keyed by the
    /// logical unit, <c>&lt;activityId&gt;:&lt;logicalUnitDate&gt;</c> (timeline_logic.js <c>logicalUnitProjection</c>);
    /// assignments hold the resource name. Local only: they never reach VGS or the apply package. The equipment settings
    /// (<see cref="ResourceState"/>) are plan steps (undo/redo); exemptions are not.
    /// </summary>
    public sealed class LocalOverrides
    {
        /// <summary>Storage key prefix (plugin-only; the browser's own key holds its full preference schema).</summary>
        public const string StoragePrefix = "stp-plugin:overrides:";

        private const int MaxEntries = 5000;

        private readonly HashSet<string> exemptions = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Gets the equipment settings (manual resources and assignments).</summary>
        public ResourceState ResourceState { get; private set; } = ResourceState.Empty;

        /// <summary>Gets the equipment per logical unit.</summary>
        public IReadOnlyDictionary<string, string> Resources => ResourceState.Days;

        /// <summary>Gets the equipment per activity.</summary>
        public IReadOnlyDictionary<string, string> ActivityResources => ResourceState.Activities;

        /// <summary>Gets the manual resources.</summary>
        public IReadOnlyList<ManualResource> ManualResources => ResourceState.Manual;

        /// <summary>Gets the logical units exempt from the capacity limit.</summary>
        public IReadOnlyCollection<string> Exemptions => exemptions;

        /// <summary>The logical unit id of a fragment.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="logicalUnitDate">The fragment's logical unit date <c>YYYY-MM-DD</c>.</param>
        /// <returns><c>activityId:date</c>.</returns>
        public static string LogicalUnitId(string activityId, string logicalUnitDate) => (activityId ?? string.Empty) + ":" + (logicalUnitDate ?? string.Empty);

        /// <summary>
        /// Reads the stored overrides of a project; anything unreadable reads as none. With <paramref name="vgsResources"/>
        /// the manual resources are normalized against them and assignments to unknown equipment are dropped.
        /// </summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        /// <param name="vgsResources">The project's VGS resource names, or <c>null</c> to keep every assignment.</param>
        /// <returns>The overrides.</returns>
        public static LocalOverrides Load(IHorizonStorage storage, string projectKey, IReadOnlyCollection<string> vgsResources = null)
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
                var manual = Planner.ManualResources.Normalize(root["manualResources"], vgsResources ?? Array.Empty<string>());
                var state = new ResourceState(manual, Names(root["fragmentResourceOverrides"]), Names(root["activityResourceOverrides"]));
                if (vgsResources != null)
                {
                    state = state.Retain(new HashSet<string>(vgsResources.Concat(manual.Select(r => r.Name)), StringComparer.Ordinal));
                }

                result.ResourceState = state;
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
        public string ResourceOf(string logicalUnitId) => logicalUnitId != null && ResourceState.Days.TryGetValue(logicalUnitId, out string value) ? value : null;

        /// <summary>The equipment assigned to a whole activity, or <c>null</c> for its VGS resource.</summary>
        /// <param name="activityId">The activity.</param>
        /// <returns>The equipment.</returns>
        public string ActivityResourceOf(string activityId) => activityId != null && ResourceState.Activities.TryGetValue(activityId, out string value) ? value : null;

        /// <summary>
        /// The assigned equipment of one day (JS <c>effectiveResourceFor</c>): the day's own, else the activity's, else
        /// <c>null</c> (the VGS resource).
        /// </summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="logicalUnitId">The day.</param>
        /// <returns>The equipment.</returns>
        public string AssignmentOf(string activityId, string logicalUnitId) => ResourceOf(logicalUnitId) ?? ActivityResourceOf(activityId);

        /// <summary>Whether a logical unit is exempt.</summary>
        /// <param name="logicalUnitId">The logical unit.</param>
        /// <returns>Exempt.</returns>
        public bool IsExempt(string logicalUnitId) => logicalUnitId != null && exemptions.Contains(logicalUnitId);

        /// <summary>Sets or clears (<c>null</c>) the equipment of a logical unit.</summary>
        /// <param name="logicalUnitId">The logical unit.</param>
        /// <param name="resource">The equipment, or <c>null</c>.</param>
        /// <returns>Whether anything changed.</returns>
        public bool SetResource(string logicalUnitId, string resource) => SetResourceState(ResourceState.WithDay(logicalUnitId, resource));

        /// <summary>Replaces the equipment settings (undo/redo, manual-resource edits).</summary>
        /// <param name="state">The settings.</param>
        /// <returns>Whether anything changed.</returns>
        public bool SetResourceState(ResourceState state)
        {
            state = state ?? ResourceState.Empty;
            if (state.SameAs(ResourceState)) return false;
            ResourceState = state;
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
            JObject Map(IReadOnlyDictionary<string, string> map) =>
                new JObject(map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new JProperty(p.Key, p.Value)));
            var root = new JObject
            {
                ["manualResources"] = Planner.ManualResources.ToJson(ResourceState.Manual),
                ["activityResourceOverrides"] = Map(ResourceState.Activities),
                ["fragmentResourceOverrides"] = Map(ResourceState.Days),
                ["fragmentCapacityExemptions"] = new JObject(exemptions.OrderBy(k => k, StringComparer.Ordinal).Select(k => new JProperty(k, true))),
            };
            storage.SetItem(StoragePrefix + projectKey, root.ToString(Formatting.None));
        }

        private static IEnumerable<KeyValuePair<string, string>> Names(JToken token)
        {
            if (!(token is JObject map)) yield break;
            foreach (var pair in map.Properties().Take(MaxEntries))
            {
                if (pair.Value.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)pair.Value))
                {
                    yield return new KeyValuePair<string, string>(pair.Name, ((string)pair.Value).Trim());
                }
            }
        }
    }
}
