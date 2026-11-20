using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>Equipment added by the user that VGS does not know ("+ Recurso manual"), e.g. <c>manual:1</c> "Equipo manual 1".</summary>
    public sealed class ManualResource : IEquatable<ManualResource>
    {
        /// <summary>Initializes a new instance of the <see cref="ManualResource"/> class.</summary>
        /// <param name="id">The id, <c>manual:&lt;n&gt;</c>.</param>
        /// <param name="name">The name.</param>
        public ManualResource(string id, string name)
        {
            Id = id;
            Name = name;
        }

        /// <summary>Gets the id, <c>manual:&lt;n&gt;</c> (stable across renames).</summary>
        public string Id { get; }

        /// <summary>Gets the name (unique, case-insensitively, among VGS and manual resources).</summary>
        public string Name { get; }

        /// <inheritdoc/>
        public bool Equals(ManualResource other) => other != null && other.Id == Id && other.Name == Name;

        /// <inheritdoc/>
        public override bool Equals(object obj) => Equals(obj as ManualResource);

        /// <inheritdoc/>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id ?? string.Empty) ^ StringComparer.Ordinal.GetHashCode(Name ?? string.Empty);

        /// <inheritdoc/>
        public override string ToString() => Id + " " + Name;
    }

    /// <summary>One entry of the equipment list: a VGS resource or a manual one (v1_compat_logic.js <c>combinedResourceOptions</c>).</summary>
    public sealed class ResourceDescriptor
    {
        /// <summary>Gets the id: <c>vgs:&lt;name&gt;</c> or <c>manual:&lt;n&gt;</c>.</summary>
        public string Id { get; init; }

        /// <summary>Gets the name.</summary>
        public string Name { get; init; }

        /// <summary>Gets a value indicating whether it is a manual resource (renamable, removable).</summary>
        public bool IsManual { get; init; }
    }

    /// <summary>
    /// The manual-resource rules of the browser planner: <c>normalizeManualResources</c> (reference/web/v1_compat_logic.js
    /// L56) and the template's <c>addManualResource</c> / <c>renameManualResource</c> / <c>removeManualResource</c>
    /// (short_term_planner_template_v2.html L4195-L4260). Pure; golden-tested against the JS (tools/golden/compat).
    /// </summary>
    public static class ManualResources
    {
        /// <summary>The id prefix of manual resources.</summary>
        public const string IdPrefix = "manual:";

        /// <summary>The id prefix of VGS resources.</summary>
        public const string VgsPrefix = "vgs:";

        /// <summary>The default name of a new one, followed by a number.</summary>
        public const string DefaultNamePrefix = "Equipo manual ";

        /// <summary>The longest stored name (JS <c>safeText(name, 160)</c>).</summary>
        public const int MaxNameLength = 160;

        private const int MaxIdLength = 80;

        // JS /^manual:[1-9]\d*$/: \d is ASCII only.
        private static readonly Regex IdPattern = new Regex("^manual:[1-9][0-9]*$", RegexOptions.CultureInvariant);
        private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-MX");

        /// <summary>JS <c>resourceNameKey</c>: trimmed, lower case (es-MX); names that share a key clash.</summary>
        /// <param name="name">A name.</param>
        /// <returns>The key.</returns>
        public static string NameKey(string name) => JsTrim(name).ToLower(Spanish);

        /// <summary>JS <c>String.prototype.trim</c> (ECMAScript WhiteSpace and LineTerminator; not U+0085).</summary>
        /// <param name="value">The text, or <c>null</c>.</param>
        /// <returns>The trimmed text; empty for <c>null</c>.</returns>
        public static string JsTrim(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int start = 0, end = value.Length;
            while (start < end && IsJsSpace(value[start])) start++;
            while (end > start && IsJsSpace(value[end - 1])) end--;
            return value.Substring(start, end - start);
        }

        /// <summary>
        /// JS <c>normalizeManualResources</c>: keeps the objects with a <c>manual:&lt;n&gt;</c> id (at most 80 chars) and a
        /// non-blank name (at most 160 chars), both trimmed; drops repeated ids and names that clash with a VGS resource
        /// or an earlier manual one. Anything that is not a list reads as none.
        /// </summary>
        /// <param name="raw">The stored value.</param>
        /// <param name="vgsResources">The project's VGS resource names.</param>
        /// <returns>The accepted resources, in order.</returns>
        public static IReadOnlyList<ManualResource> Normalize(JToken raw, IEnumerable<string> vgsResources)
        {
            var result = new List<ManualResource>();
            if (!(raw is JArray candidates)) return result;
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            var usedNames = new HashSet<string>((vgsResources ?? Enumerable.Empty<string>()).Select(NameKey), StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (!(candidate is JObject item)) continue;
                string id = SafeText(item["id"], MaxIdLength);
                string name = SafeText(item["name"], MaxNameLength);
                string key = NameKey(name);
                if (!IdPattern.IsMatch(id) || name.Length == 0 || usedIds.Contains(id) || usedNames.Contains(key)) continue;
                usedIds.Add(id);
                usedNames.Add(key);
                result.Add(new ManualResource(id, name));
            }

            return result;
        }

        /// <summary><see cref="Normalize(JToken, IEnumerable{string})"/> of an in-memory list.</summary>
        /// <param name="resources">The resources.</param>
        /// <param name="vgsResources">The project's VGS resource names.</param>
        /// <returns>The accepted resources, in order.</returns>
        public static IReadOnlyList<ManualResource> Normalize(IEnumerable<ManualResource> resources, IEnumerable<string> vgsResources) =>
            Normalize(ToJson(resources), vgsResources);

        /// <summary>The stored form: <c>[{"id":…,"name":…}]</c>.</summary>
        /// <param name="resources">The resources.</param>
        /// <returns>The JSON array.</returns>
        public static JArray ToJson(IEnumerable<ManualResource> resources) =>
            new JArray((resources ?? Enumerable.Empty<ManualResource>()).Select(r => new JObject { ["id"] = r.Id, ["name"] = r.Name }));

        /// <summary>
        /// Template <c>addManualResource</c>: the lowest free <c>manual:&lt;n&gt;</c>, named "Equipo manual &lt;k&gt;" with
        /// the first k ≥ n whose name is free.
        /// </summary>
        /// <param name="resources">The current (normalized) list.</param>
        /// <param name="vgsResources">The project's VGS resource names.</param>
        /// <param name="added">The new resource.</param>
        /// <returns>The list with the new resource last.</returns>
        public static IReadOnlyList<ManualResource> Add(IReadOnlyList<ManualResource> resources, IEnumerable<string> vgsResources, out ManualResource added)
        {
            var list = resources ?? Array.Empty<ManualResource>();
            var vgs = (vgsResources ?? Enumerable.Empty<string>()).ToList();
            var existing = new HashSet<string>(list.Select(r => r.Id), StringComparer.Ordinal);
            int number = 1;
            while (existing.Contains(IdPrefix + number.ToString(CultureInfo.InvariantCulture))) number++;
            string id = IdPrefix + number.ToString(CultureInfo.InvariantCulture);
            for (long nameNumber = number; ; nameNumber++)
            {
                var candidate = new ManualResource(id, DefaultNamePrefix + nameNumber.ToString(CultureInfo.InvariantCulture));
                var next = list.Concat(new[] { candidate }).ToList();
                if (Normalize(next, vgs).Any(r => r.Id == id))
                {
                    added = candidate;
                    return next;
                }
            }
        }

        /// <summary>
        /// Template <c>renameManualResource</c>: accepted only when the trimmed name survives normalization (not blank, at
        /// most 160 chars, no clash with VGS or another manual resource).
        /// </summary>
        /// <param name="resources">The current (normalized) list.</param>
        /// <param name="id">The resource.</param>
        /// <param name="name">The new name.</param>
        /// <param name="vgsResources">The project's VGS resource names.</param>
        /// <returns>The renamed list, or <c>null</c> when refused (or the id is unknown).</returns>
        public static IReadOnlyList<ManualResource> Rename(IReadOnlyList<ManualResource> resources, string id, string name, IEnumerable<string> vgsResources)
        {
            if (string.IsNullOrEmpty(id) || resources == null) return null;
            string trimmed = JsTrim(name);
            var renamed = resources.Select(r => r.Id == id ? new ManualResource(r.Id, trimmed) : r).ToList();
            var accepted = Normalize(renamed, vgsResources).FirstOrDefault(r => r.Id == id);
            return accepted == null || accepted.Name != trimmed ? null : renamed;
        }

        /// <summary>Template <c>removeManualResource</c> (the caller also drops what was assigned to it).</summary>
        /// <param name="resources">The current list.</param>
        /// <param name="id">The resource.</param>
        /// <param name="removed">The removed resource, or <c>null</c> when unknown.</param>
        /// <returns>The list without it.</returns>
        public static IReadOnlyList<ManualResource> Remove(IReadOnlyList<ManualResource> resources, string id, out ManualResource removed)
        {
            var list = resources ?? Array.Empty<ManualResource>();
            removed = list.FirstOrDefault(r => r.Id == id);
            return removed == null ? list : list.Where(r => r.Id != id).ToList();
        }

        /// <summary>JS <c>combinedResourceOptions</c>: the VGS resources, then the manual ones.</summary>
        /// <param name="vgsResources">The VGS resource names, in display order.</param>
        /// <param name="manual">The manual resources.</param>
        /// <returns>The descriptors.</returns>
        public static IReadOnlyList<ResourceDescriptor> Options(IEnumerable<string> vgsResources, IEnumerable<ManualResource> manual) =>
            (vgsResources ?? Enumerable.Empty<string>()).Select(name => new ResourceDescriptor { Id = VgsPrefix + name, Name = name, IsManual = false })
                .Concat((manual ?? Enumerable.Empty<ManualResource>()).Select(r => new ResourceDescriptor { Id = r.Id, Name = r.Name, IsManual = true }))
                .ToList();

        // JS safeText: a string, trimmed, non-empty and at most `maximum` UTF-16 units; anything else is ''.
        private static string SafeText(JToken value, int maximum)
        {
            if (value == null || value.Type != JTokenType.String) return string.Empty;
            string result = JsTrim((string)value);
            return result.Length <= maximum ? result : string.Empty;
        }

        private static bool IsJsSpace(char c) =>
            (c >= (char)9 && c <= (char)13) || c == ' ' || c == (char)0x00A0 || c == (char)0xFEFF
            || c == (char)0x2028 || c == (char)0x2029 || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
    }

    /// <summary>
    /// The undoable equipment settings: the manual resources and the equipment assigned to days and to whole activities
    /// (template <c>fragmentResourceOverrides</c> / <c>activityResourceOverrides</c>, here by resource name). Immutable;
    /// a plan state carries one so undo/redo restores it.
    /// </summary>
    public sealed class ResourceState
    {
        /// <summary>No manual resources and no assignments.</summary>
        public static readonly ResourceState Empty = new ResourceState(null, null, null);

        /// <summary>Initializes a new instance of the <see cref="ResourceState"/> class.</summary>
        /// <param name="manual">The manual resources.</param>
        /// <param name="days">The equipment per logical unit.</param>
        /// <param name="activities">The equipment per activity.</param>
        public ResourceState(IEnumerable<ManualResource> manual, IEnumerable<KeyValuePair<string, string>> days, IEnumerable<KeyValuePair<string, string>> activities)
        {
            Manual = (manual ?? Enumerable.Empty<ManualResource>()).ToList();
            Days = Copy(days);
            Activities = Copy(activities);
        }

        /// <summary>Gets the manual resources, in order.</summary>
        public IReadOnlyList<ManualResource> Manual { get; }

        /// <summary>Gets the equipment name per logical unit (<c>activityId:YYYY-MM-DD</c>).</summary>
        public IReadOnlyDictionary<string, string> Days { get; }

        /// <summary>Gets the equipment name per activity.</summary>
        public IReadOnlyDictionary<string, string> Activities { get; }

        /// <summary>The same assignments with another manual list.</summary>
        /// <param name="manual">The manual resources.</param>
        /// <returns>The state.</returns>
        public ResourceState WithManual(IEnumerable<ManualResource> manual) => new ResourceState(manual, Days, Activities);

        /// <summary>Sets or clears (<c>null</c>/empty) the equipment of a day.</summary>
        /// <param name="logicalUnitId">The day.</param>
        /// <param name="name">The equipment.</param>
        /// <returns>The state.</returns>
        public ResourceState WithDay(string logicalUnitId, string name) => new ResourceState(Manual, Set(Days, logicalUnitId, name), Activities);

        /// <summary>Sets or clears (<c>null</c>/empty) the equipment of an activity.</summary>
        /// <param name="activityId">The activity.</param>
        /// <param name="name">The equipment.</param>
        /// <returns>The state.</returns>
        public ResourceState WithActivity(string activityId, string name) => new ResourceState(Manual, Days, Set(Activities, activityId, name));

        /// <summary>Assignments to <paramref name="oldName"/> follow a rename (the browser keys them by id).</summary>
        /// <param name="oldName">The old name.</param>
        /// <param name="newName">The new name.</param>
        /// <returns>The state.</returns>
        public ResourceState Renamed(string oldName, string newName) =>
            new ResourceState(Manual, Days.Select(p => Pair(p.Key, p.Value == oldName ? newName : p.Value)), Activities.Select(p => Pair(p.Key, p.Value == oldName ? newName : p.Value)));

        /// <summary>Drops the assignments to a resource (template <c>removeManualResource</c>).</summary>
        /// <param name="name">The resource.</param>
        /// <returns>The state.</returns>
        public ResourceState Without(string name) => new ResourceState(Manual, Days.Where(p => p.Value != name), Activities.Where(p => p.Value != name));

        /// <summary>Drops the assignments to resources that no longer exist (JS <c>normalizedResourceOverrides</c>).</summary>
        /// <param name="valid">The existing resource names.</param>
        /// <returns>The state.</returns>
        public ResourceState Retain(ICollection<string> valid) => new ResourceState(Manual, Days.Where(p => valid.Contains(p.Value)), Activities.Where(p => valid.Contains(p.Value)));

        /// <summary>Whether two states hold the same settings.</summary>
        /// <param name="other">The other state.</param>
        /// <returns>Same.</returns>
        public bool SameAs(ResourceState other) =>
            other != null && Manual.SequenceEqual(other.Manual) && SameMap(Days, other.Days) && SameMap(Activities, other.Activities);

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

        private static IReadOnlyDictionary<string, string> Copy(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in pairs ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (!string.IsNullOrEmpty(pair.Key) && !string.IsNullOrEmpty(pair.Value)) result[pair.Key] = pair.Value;
            }

            return result;
        }

        private static IEnumerable<KeyValuePair<string, string>> Set(IReadOnlyDictionary<string, string> map, string key, string value)
        {
            var copy = map.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(value)) copy.Remove(key ?? string.Empty);
            else if (!string.IsNullOrEmpty(key)) copy[key] = value;
            return copy;
        }

        private static bool SameMap(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out string value) && value == p.Value);
    }
}
