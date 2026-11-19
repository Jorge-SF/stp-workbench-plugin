using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Contracts
{
    /// <summary>
    /// <c>vgs-stp-snapshot/2</c> — the native evidence + Activity Grid the planner consumes
    /// (<c>snapshot_v2.json</c> and the body of <c>GET /snapshot</c>).
    /// Mirrors <c>stp_v2_snapshot_contract.build_canonical_snapshot</c>: exact field sets, decimals as
    /// Python <c>str(Decimal)</c> strings, times as the snapshot's <c>_iso</c> form, lags as integer
    /// microsecond strings. Non-canonical spellings are rejected, as <c>canonicalize_snapshot</c> does.
    /// </summary>
    public sealed class SnapshotV2
    {
        public const string SchemaId = "vgs-stp-snapshot/2";

        internal static readonly string[] RootFields =
        {
            "schema", "projectName", "projectPath", "projectKey", "projectFingerprint",
            "precedenceFingerprint", "exportedAt", "dateFormat", "activities", "precedences",
        };

        private SnapshotV2(JObject raw)
        {
            const string L = "snapshot";
            WireJson.RequireConstant(WireJson.Text(raw, "schema", L), SchemaId, "snapshot schema");
            ProjectName = WireJson.String(raw, "projectName", L);
            ProjectPath = WireJson.Text(raw, "projectPath", L);
            ProjectKey = Hex64(raw, "projectKey", L);
            ProjectFingerprint = Hex64(raw, "projectFingerprint", L);
            PrecedenceFingerprint = Hex64(raw, "precedenceFingerprint", L);
            ExportedAt = Snap.Time(raw, "exportedAt", L);
            DateFormat = WireJson.String(raw, "dateFormat", L);
            Activities = WireJson.List(raw, "activities", L).Select((a, i) => new SnapshotActivity(a, $"activities[{i}]")).ToList().AsReadOnly();
            Precedences = WireJson.List(raw, "precedences", L).Select((p, i) => new SnapshotPrecedence(p, $"precedences[{i}]")).ToList().AsReadOnly();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var activity in Activities)
            {
                if (!ids.Add(activity.Id)) throw new WireFormatException($"duplicate snapshot activity '{activity.Id}'");
            }
        }

        public string Schema => SchemaId;

        public string ProjectName { get; }

        public string ProjectPath { get; }

        public string ProjectKey { get; }

        public string ProjectFingerprint { get; }

        public string PrecedenceFingerprint { get; }

        public DateTime ExportedAt { get; }

        public string DateFormat { get; }

        public IReadOnlyList<SnapshotActivity> Activities { get; }

        public IReadOnlyList<SnapshotPrecedence> Precedences { get; }

        public static SnapshotV2 Parse(string json) => FromJson(WireJson.Parse(json));

        public static SnapshotV2 FromJson(JToken token) => new SnapshotV2(WireJson.ExactObject(token, "snapshot", RootFields));

        internal static SnapshotV2 FromValidatedObject(JObject raw) => new SnapshotV2(raw);

        public JObject ToJson() => new JObject
        {
            ["schema"] = Schema,
            ["projectName"] = ProjectName,
            ["projectPath"] = ProjectPath,
            ["projectKey"] = ProjectKey,
            ["projectFingerprint"] = ProjectFingerprint,
            ["precedenceFingerprint"] = PrecedenceFingerprint,
            ["exportedAt"] = PyDateTime.SnapshotIso(ExportedAt),
            ["dateFormat"] = DateFormat,
            ["activities"] = new JArray(Activities.Select(a => a.ToJson())),
            ["precedences"] = new JArray(Precedences.Select(p => p.ToJson())),
        };

        internal static string Hex64(JObject raw, string key, string label)
        {
            var value = WireJson.String(raw, key, label);
            if (!WireJson.IsLowerHex(value, 64)) throw new WireFormatException($"{label}.{key} must be 64 lowercase hex characters");
            return value;
        }
    }

    /// <summary>Field readers for the snapshot's canonical string encodings.</summary>
    internal static class Snap
    {
        public static DateTime Time(JObject raw, string key, string label)
        {
            var text = WireJson.Text(raw, key, label);
            var value = PyDateTime.ParseDateTime(text, false, label + "." + key);
            if (PyDateTime.SnapshotIso(value) != text) throw new WireFormatException($"{label}.{key} is not in canonical snapshot form");
            return value;
        }

        public static DateTime Date(JObject raw, string key, string label)
            => PyDateTime.ParseDate(WireJson.Text(raw, key, label), label + "." + key);

        public static PyDecimal Decimal(JObject raw, string key, string label)
        {
            var text = WireJson.Text(raw, key, label);
            if (!PyDecimal.TryParse(text, out var value) || value.ToString() != text)
                throw new WireFormatException($"{label}.{key} must be a canonical decimal string");
            return value;
        }

        public static long Microseconds(JObject raw, string key, string label)
        {
            var text = WireJson.Text(raw, key, label);
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                || value.ToString(CultureInfo.InvariantCulture) != text)
                throw new WireFormatException($"{label}.{key} must be an integer string");
            return value;
        }

        public static string Micro(long value) => value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class SnapshotActivity
    {
        private static readonly string[] Fields = { "id", "collectionKey", "overallPercentComplete", "grid", "nativeBars", "fragments", "nativeAnchors" };

        /// <summary>The grid identity columns (<c>_ID_COLUMNS</c>); exactly one must be present and equal <see cref="Id"/>.</summary>
        public static readonly IReadOnlyCollection<string> IdColumns = new[] { "ID", "Id", "Activity ID", "ActivityId" };

        internal SnapshotActivity(JToken token, string label)
        {
            var raw = WireJson.ExactObject(token, label, Fields);
            Id = WireJson.Text(raw, "id", label);
            CollectionKey = WireJson.String(raw, "collectionKey", label);
            OverallPercentComplete = Snap.Decimal(raw, "overallPercentComplete", label);
            if (!(raw["grid"] is JObject grid)) throw new WireFormatException(label + ".grid must be an object");
            var columns = new List<KeyValuePair<string, string>>();
            foreach (var property in grid.Properties())
            {
                if (property.Name.Trim().Length == 0 || property.Value.Type != JTokenType.String)
                    throw new WireFormatException(label + ".grid must map non-blank column names to strings");
                columns.Add(new KeyValuePair<string, string>(property.Name, (string)property.Value));
            }
            var identities = columns.Where(c => IdColumns.Contains(c.Key)).ToList();
            if (identities.Count != 1 || identities[0].Value != Id)
                throw new WireFormatException(label + ".grid must contain exactly one identity column equal to the activity id");
            GridColumns = columns.AsReadOnly();
            Grid = new ReadOnlyDictionary<string, string>(columns.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal));
            NativeBars = WireJson.List(raw, "nativeBars", label).Select((b, i) => new SnapshotNativeBar(b, $"{label}.nativeBars[{i}]")).ToList().AsReadOnly();
            Fragments = WireJson.List(raw, "fragments", label).Select((f, i) => new SnapshotFragment(f, $"{label}.fragments[{i}]")).ToList().AsReadOnly();
            NativeAnchors = WireJson.List(raw, "nativeAnchors", label).Select((a, i) => new SnapshotNativeAnchor(a, $"{label}.nativeAnchors[{i}]")).ToList().AsReadOnly();
        }

        public string Id { get; }

        public string CollectionKey { get; }

        public PyDecimal OverallPercentComplete { get; }

        /// <summary>Activity Grid row (presentation only), keyed by column name.</summary>
        public IReadOnlyDictionary<string, string> Grid { get; }

        /// <summary>The same row in the original column order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> GridColumns { get; }

        public IReadOnlyList<SnapshotNativeBar> NativeBars { get; }

        public IReadOnlyList<SnapshotFragment> Fragments { get; }

        public IReadOnlyList<SnapshotNativeAnchor> NativeAnchors { get; }

        public JObject ToJson()
        {
            var grid = new JObject();
            foreach (var column in GridColumns) grid[column.Key] = column.Value;
            return new JObject
            {
                ["id"] = Id,
                ["collectionKey"] = CollectionKey,
                ["overallPercentComplete"] = OverallPercentComplete.ToString(),
                ["grid"] = grid,
                ["nativeBars"] = new JArray(NativeBars.Select(b => b.ToJson())),
                ["fragments"] = new JArray(Fragments.Select(f => f.ToJson())),
                ["nativeAnchors"] = new JArray(NativeAnchors.Select(a => a.ToJson())),
            };
        }
    }

    public sealed class SnapshotNativeBar
    {
        internal SnapshotNativeBar(JToken token, string label)
        {
            var raw = WireJson.ExactObject(token, label, "xmlKey", "key", "start", "finish", "quantityPercentage", "percentComplete", "completeWorkedMinutes");
            XmlKey = WireJson.Text(raw, "xmlKey", label);
            Key = WireJson.String(raw, "key", label);
            Start = Snap.Time(raw, "start", label);
            Finish = Snap.Time(raw, "finish", label);
            QuantityPercentage = Snap.Decimal(raw, "quantityPercentage", label);
            PercentComplete = Snap.Decimal(raw, "percentComplete", label);
            CompleteWorkedMinutes = Snap.Decimal(raw, "completeWorkedMinutes", label);
        }

        public string XmlKey { get; }

        public string Key { get; }

        public DateTime Start { get; }

        public DateTime Finish { get; }

        public PyDecimal QuantityPercentage { get; }

        public PyDecimal PercentComplete { get; }

        public PyDecimal CompleteWorkedMinutes { get; }

        public JObject ToJson() => new JObject
        {
            ["xmlKey"] = XmlKey,
            ["key"] = Key,
            ["start"] = PyDateTime.SnapshotIso(Start),
            ["finish"] = PyDateTime.SnapshotIso(Finish),
            ["quantityPercentage"] = QuantityPercentage.ToString(),
            ["percentComplete"] = PercentComplete.ToString(),
            ["completeWorkedMinutes"] = CompleteWorkedMinutes.ToString(),
        };
    }

    public sealed class SnapshotFragment
    {
        internal SnapshotFragment(JToken token, string label)
        {
            var raw = WireJson.ExactObject(token, label, "id", "nativeBarXmlKey", "nativeBarKey", "start", "finish", "originalStart", "originalFinish", "logicalUnitDate", "displaySuppressed", "quantityPercentage", "lockedCompleted");
            Id = WireJson.Text(raw, "id", label);
            NativeBarXmlKey = WireJson.Text(raw, "nativeBarXmlKey", label);
            NativeBarKey = WireJson.String(raw, "nativeBarKey", label);
            Start = Snap.Time(raw, "start", label);
            Finish = Snap.Time(raw, "finish", label);
            OriginalStart = Snap.Time(raw, "originalStart", label);
            OriginalFinish = Snap.Time(raw, "originalFinish", label);
            LogicalUnitDate = Snap.Date(raw, "logicalUnitDate", label);
            DisplaySuppressed = WireJson.Bool(raw, "displaySuppressed", label);
            QuantityPercentage = Snap.Decimal(raw, "quantityPercentage", label);
            LockedCompleted = WireJson.Bool(raw, "lockedCompleted", label);
        }

        public string Id { get; }

        public string NativeBarXmlKey { get; }

        public string NativeBarKey { get; }

        public DateTime Start { get; }

        public DateTime Finish { get; }

        public DateTime OriginalStart { get; }

        public DateTime OriginalFinish { get; }

        public DateTime LogicalUnitDate { get; }

        public bool DisplaySuppressed { get; }

        public PyDecimal QuantityPercentage { get; }

        public bool LockedCompleted { get; }

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["nativeBarXmlKey"] = NativeBarXmlKey,
            ["nativeBarKey"] = NativeBarKey,
            ["start"] = PyDateTime.SnapshotIso(Start),
            ["finish"] = PyDateTime.SnapshotIso(Finish),
            ["originalStart"] = PyDateTime.SnapshotIso(OriginalStart),
            ["originalFinish"] = PyDateTime.SnapshotIso(OriginalFinish),
            ["logicalUnitDate"] = PyDateTime.DateIso(LogicalUnitDate),
            ["displaySuppressed"] = DisplaySuppressed,
            ["quantityPercentage"] = QuantityPercentage.ToString(),
            ["lockedCompleted"] = LockedCompleted,
        };
    }

    public sealed class SnapshotNativeAnchor
    {
        internal SnapshotNativeAnchor(JToken token, string label)
        {
            var raw = WireJson.ExactObject(token, label, "leftBarXmlKey", "leftBarKey", "rightBarXmlKey", "rightBarKey", "at", "rightAt", "contiguous", "cumulativeQuantityPercentage", "links");
            LeftBarXmlKey = WireJson.Text(raw, "leftBarXmlKey", label);
            LeftBarKey = WireJson.String(raw, "leftBarKey", label);
            RightBarXmlKey = WireJson.Text(raw, "rightBarXmlKey", label);
            RightBarKey = WireJson.String(raw, "rightBarKey", label);
            At = Snap.Time(raw, "at", label);
            RightAt = Snap.Time(raw, "rightAt", label);
            Contiguous = WireJson.Bool(raw, "contiguous", label);
            CumulativeQuantityPercentage = Snap.Decimal(raw, "cumulativeQuantityPercentage", label);
            Links = WireJson.List(raw, "links", label).Select((l, i) => new SnapshotAnchorLink(l, $"{label}.links[{i}]")).ToList().AsReadOnly();
        }

        public string LeftBarXmlKey { get; }

        public string LeftBarKey { get; }

        public string RightBarXmlKey { get; }

        public string RightBarKey { get; }

        public DateTime At { get; }

        public DateTime RightAt { get; }

        public bool Contiguous { get; }

        public PyDecimal CumulativeQuantityPercentage { get; }

        public IReadOnlyList<SnapshotAnchorLink> Links { get; }

        public JObject ToJson() => new JObject
        {
            ["leftBarXmlKey"] = LeftBarXmlKey,
            ["leftBarKey"] = LeftBarKey,
            ["rightBarXmlKey"] = RightBarXmlKey,
            ["rightBarKey"] = RightBarKey,
            ["at"] = PyDateTime.SnapshotIso(At),
            ["rightAt"] = PyDateTime.SnapshotIso(RightAt),
            ["contiguous"] = Contiguous,
            ["cumulativeQuantityPercentage"] = CumulativeQuantityPercentage.ToString(),
            ["links"] = new JArray(Links.Select(l => l.ToJson())),
        };
    }

    public sealed class SnapshotAnchorLink
    {
        internal SnapshotAnchorLink(JToken token, string label)
        {
            var raw = WireJson.ExactObject(token, label, "key", "role", "endpointBar", "otherActivityId", "otherBar", "type", "lagMicroseconds", "groupKey");
            Key = WireJson.String(raw, "key", label);
            Role = WireJson.Text(raw, "role", label);
            if (Role != "predecessor" && Role != "successor") throw new WireFormatException(label + ".role must be predecessor or successor");
            EndpointBar = WireJson.String(raw, "endpointBar", label);
            OtherActivityId = WireJson.Text(raw, "otherActivityId", label);
            OtherBar = WireJson.String(raw, "otherBar", label);
            Type = WireJson.String(raw, "type", label);
            LagMicroseconds = Snap.Microseconds(raw, "lagMicroseconds", label);
            GroupKey = WireJson.String(raw, "groupKey", label);
        }

        public string Key { get; }

        /// <summary><c>predecessor</c> or <c>successor</c>.</summary>
        public string Role { get; }

        public string EndpointBar { get; }

        public string OtherActivityId { get; }

        public string OtherBar { get; }

        public string Type { get; }

        public long LagMicroseconds { get; }

        public string GroupKey { get; }

        public JObject ToJson() => new JObject
        {
            ["key"] = Key,
            ["role"] = Role,
            ["endpointBar"] = EndpointBar,
            ["otherActivityId"] = OtherActivityId,
            ["otherBar"] = OtherBar,
            ["type"] = Type,
            ["lagMicroseconds"] = Snap.Micro(LagMicroseconds),
            ["groupKey"] = GroupKey,
        };
    }

    public sealed class SnapshotPrecedence
    {
        internal SnapshotPrecedence(JToken token, string label)
        {
            var raw = WireJson.ExactObject(token, label, "key", "predecessorId", "successorId", "predecessorBar", "successorBar", "type", "lagMicroseconds", "groupKey");
            Key = WireJson.String(raw, "key", label);
            PredecessorId = WireJson.Text(raw, "predecessorId", label);
            SuccessorId = WireJson.Text(raw, "successorId", label);
            PredecessorBar = WireJson.String(raw, "predecessorBar", label);
            SuccessorBar = WireJson.String(raw, "successorBar", label);
            Type = WireJson.String(raw, "type", label);
            LagMicroseconds = Snap.Microseconds(raw, "lagMicroseconds", label);
            GroupKey = WireJson.String(raw, "groupKey", label);
        }

        public string Key { get; }

        public string PredecessorId { get; }

        public string SuccessorId { get; }

        public string PredecessorBar { get; }

        public string SuccessorBar { get; }

        /// <summary>VGS precedence type: <c>FinishStart</c>, <c>StartStart</c>, <c>FinishFinish</c> or <c>StartFinish</c>.</summary>
        public string Type { get; }

        public long LagMicroseconds { get; }

        public string GroupKey { get; }

        public JObject ToJson() => new JObject
        {
            ["key"] = Key,
            ["predecessorId"] = PredecessorId,
            ["successorId"] = SuccessorId,
            ["predecessorBar"] = PredecessorBar,
            ["successorBar"] = SuccessorBar,
            ["type"] = Type,
            ["lagMicroseconds"] = Snap.Micro(LagMicroseconds),
            ["groupKey"] = GroupKey,
        };
    }

    /// <summary>
    /// Body of <c>GET /snapshot</c>: either a complete snapshot plus <c>snapshotComplete:true, applyAllowed:false</c>,
    /// or exactly <c>{"snapshotComplete":false,"applyAllowed":false}</c> when the server could not rebuild it.
    /// </summary>
    public sealed class SnapshotRefresh
    {
        private SnapshotRefresh(SnapshotV2 snapshot) => Snapshot = snapshot;

        public bool SnapshotComplete => Snapshot != null;

        /// <summary>Always false: a refreshed snapshot never authorizes an apply by itself.</summary>
        public bool ApplyAllowed => false;

        /// <summary>The snapshot, or null when <see cref="SnapshotComplete"/> is false.</summary>
        public SnapshotV2 Snapshot { get; }

        public static SnapshotRefresh FromJson(JToken token)
        {
            if (!(token is JObject obj)) throw new WireFormatException("snapshot response must be an object");
            if (obj.Count == 2 && obj["snapshotComplete"]?.Type == JTokenType.Boolean && !(bool)obj["snapshotComplete"])
            {
                WireJson.ExactObject(obj, "snapshot response", "snapshotComplete", "applyAllowed");
                if (WireJson.Bool(obj, "applyAllowed", "snapshot response")) throw new WireFormatException("snapshot response must not allow apply");
                return new SnapshotRefresh(null);
            }
            WireJson.ExactObject(obj, "snapshot response", SnapshotV2.RootFields.Concat(new[] { "snapshotComplete", "applyAllowed" }).ToArray());
            if (!WireJson.Bool(obj, "snapshotComplete", "snapshot response") || WireJson.Bool(obj, "applyAllowed", "snapshot response"))
                throw new WireFormatException("snapshot response must be snapshotComplete:true, applyAllowed:false");
            var copy = (JObject)obj.DeepClone();
            copy.Remove("snapshotComplete");
            copy.Remove("applyAllowed");
            return new SnapshotRefresh(SnapshotV2.FromValidatedObject(copy));
        }
    }
}
