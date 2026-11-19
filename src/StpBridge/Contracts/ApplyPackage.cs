using System;
using System.Collections.Generic;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Contracts
{
    /// <summary>Fragment provenance in <c>vgs-stp-apply/1</c>.</summary>
    public enum ApplyProvenance
    {
        Unchanged,
        Direct,
        Cascade,
    }

    /// <summary>
    /// <c>vgs-stp-apply/1</c> — the package POSTed to <c>/apply-package</c>.
    /// Port of <c>stp_v2_apply_contract.load_apply_package</c> / <c>apply_package_to_json_value</c>.
    /// Only the parse-time checks live here; the safety validation stays on the server.
    /// </summary>
    public sealed class ApplyPackage
    {
        public const string SchemaId = "vgs-stp-apply/1";

        private static readonly string[] Fields =
        {
            "schema", "project_fingerprint", "precedence_fingerprint", "horizon_start", "horizon_finish", "activities",
        };

        public ApplyPackage(
            string projectFingerprint,
            string precedenceFingerprint,
            DateTime horizonStart,
            DateTime horizonFinish,
            IEnumerable<ApplyActivity> activities)
        {
            ProjectFingerprint = NonEmpty(projectFingerprint, "package.project_fingerprint");
            PrecedenceFingerprint = NonEmpty(precedenceFingerprint, "package.precedence_fingerprint");
            HorizonStart = horizonStart;
            HorizonFinish = horizonFinish;
            Activities = (activities ?? throw new ArgumentNullException(nameof(activities))).ToList().AsReadOnly();
            if (horizonStart.TimeOfDay != TimeSpan.Zero || horizonFinish.TimeOfDay != TimeSpan.Zero || horizonStart >= horizonFinish)
                throw new WireFormatException("package must contain an ordered midnight civil horizon");
            Unique(Activities.Select(a => a.ActivityId), "activity IDs");
            Unique(Activities.SelectMany(a => a.Fragments).Select(f => f.Id), "fragment IDs");
        }

        public string Schema => SchemaId;

        public string ProjectFingerprint { get; }

        public string PrecedenceFingerprint { get; }

        public DateTime HorizonStart { get; }

        public DateTime HorizonFinish { get; }

        public IReadOnlyList<ApplyActivity> Activities { get; }

        public static ApplyPackage Parse(string json) => FromJson(WireJson.Parse(json));

        public static ApplyPackage FromJson(JToken token)
        {
            var raw = WireJson.ExactObject(token, "package", Fields);
            WireJson.RequireConstant(WireJson.Text(raw, "schema", "package"), SchemaId, "apply package schema");
            var activities = raw["activities"] as JArray ?? throw new WireFormatException("package.activities must be an exact list");
            return new ApplyPackage(
                WireJson.Text(raw, "project_fingerprint", "package"),
                WireJson.Text(raw, "precedence_fingerprint", "package"),
                PyDateTime.ParseDateTime(WireJson.Text(raw, "horizon_start", "package"), true, "package.horizon_start"),
                PyDateTime.ParseDateTime(WireJson.Text(raw, "horizon_finish", "package"), true, "package.horizon_finish"),
                activities.Select((item, index) => ApplyActivity.FromJson(item, index)));
        }

        /// <summary>The JSON value Python re-serializes after parsing (<c>apply_package_to_json_value</c>).</summary>
        public JObject ToJson()
        {
            return new JObject
            {
                ["schema"] = Schema,
                ["project_fingerprint"] = ProjectFingerprint,
                ["precedence_fingerprint"] = PrecedenceFingerprint,
                ["horizon_start"] = PyDateTime.IsoFormat(HorizonStart),
                ["horizon_finish"] = PyDateTime.IsoFormat(HorizonFinish),
                ["activities"] = new JArray(Activities.Select(a => a.ToJson())),
            };
        }

        /// <summary>The exact bytes the server stores and hashes for the receipt.</summary>
        public byte[] ToCanonicalBytes() => CanonicalJson.ToBytes(ToJson());

        /// <summary>The <c>package_sha256</c> the server must return for this package.</summary>
        public string ComputeSha256() => CanonicalJson.Sha256Hex(ToCanonicalBytes());

        internal static string NonEmpty(string value, string label)
        {
            if (string.IsNullOrEmpty(value)) throw new WireFormatException(label + " must be a non-empty exact string");
            return value;
        }

        private static void Unique(IEnumerable<string> values, string label)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (!seen.Add(value)) throw new WireFormatException(label + " must be unique");
            }
        }
    }

    public sealed class ApplyActivity
    {
        public ApplyActivity(string activityId, IEnumerable<ApplyFragment> fragments)
        {
            ActivityId = ApplyPackage.NonEmpty(activityId, "activity_id");
            Fragments = (fragments ?? throw new ArgumentNullException(nameof(fragments))).ToList().AsReadOnly();
            if (Fragments.Count == 0) throw new WireFormatException("fragments must be a non-empty exact list");
        }

        public string ActivityId { get; }

        public IReadOnlyList<ApplyFragment> Fragments { get; }

        internal static ApplyActivity FromJson(JToken token, int index)
        {
            var label = $"activities[{index}]";
            var raw = WireJson.ExactObject(token, label, "activity_id", "fragments");
            if (!(raw["fragments"] is JArray fragments) || fragments.Count == 0)
                throw new WireFormatException(label + ".fragments must be a non-empty exact list");
            return new ApplyActivity(
                WireJson.Text(raw, "activity_id", label),
                fragments.Select((item, i) => ApplyFragment.FromJson(item, $"{label}.fragments[{i}]")));
        }

        public JObject ToJson() => new JObject
        {
            ["activity_id"] = ActivityId,
            ["fragments"] = new JArray(Fragments.Select(f => f.ToJson())),
        };
    }

    public sealed class ApplyFragment
    {
        private static readonly string[] Fields =
        {
            "id", "native_bar_xml_key", "native_bar_key", "logical_unit_id", "logical_unit_date", "provenance",
            "start", "finish", "original_start", "original_finish", "quantity_percentage", "locked_completed",
        };

        public ApplyFragment(
            string id,
            string nativeBarXmlKey,
            string nativeBarKey,
            string logicalUnitId,
            DateTime logicalUnitDate,
            ApplyProvenance provenance,
            DateTime start,
            DateTime finish,
            DateTime originalStart,
            DateTime originalFinish,
            PyDecimal quantityPercentage,
            bool lockedCompleted)
        {
            Id = ApplyPackage.NonEmpty(id, "fragment.id");
            NativeBarXmlKey = ApplyPackage.NonEmpty(nativeBarXmlKey, "fragment.native_bar_xml_key");
            NativeBarKey = ApplyPackage.NonEmpty(nativeBarKey, "fragment.native_bar_key");
            LogicalUnitId = ApplyPackage.NonEmpty(logicalUnitId, "fragment.logical_unit_id");
            if (logicalUnitDate.TimeOfDay != TimeSpan.Zero) throw new WireFormatException("fragment.logical_unit_date must be a date");
            LogicalUnitDate = logicalUnitDate;
            Provenance = provenance;
            Start = start;
            Finish = finish;
            OriginalStart = originalStart;
            OriginalFinish = originalFinish;
            QuantityPercentage = quantityPercentage;
            LockedCompleted = lockedCompleted;
        }

        public string Id { get; }

        public string NativeBarXmlKey { get; }

        public string NativeBarKey { get; }

        public string LogicalUnitId { get; }

        public DateTime LogicalUnitDate { get; }

        public ApplyProvenance Provenance { get; }

        public DateTime Start { get; }

        public DateTime Finish { get; }

        public DateTime OriginalStart { get; }

        public DateTime OriginalFinish { get; }

        public PyDecimal QuantityPercentage { get; }

        public bool LockedCompleted { get; }

        internal static ApplyFragment FromJson(JToken token, string label)
        {
            var raw = WireJson.ExactObject(token, label, Fields);
            var provenanceText = WireJson.Text(raw, "provenance", label);
            ApplyProvenance provenance;
            switch (provenanceText)
            {
                case "unchanged": provenance = ApplyProvenance.Unchanged; break;
                case "direct": provenance = ApplyProvenance.Direct; break;
                case "cascade": provenance = ApplyProvenance.Cascade; break;
                default: throw new WireFormatException(label + ".provenance must be unchanged, direct or cascade");
            }
            return new ApplyFragment(
                WireJson.Text(raw, "id", label),
                WireJson.Text(raw, "native_bar_xml_key", label),
                WireJson.Text(raw, "native_bar_key", label),
                WireJson.Text(raw, "logical_unit_id", label),
                PyDateTime.ParseDate(WireJson.Text(raw, "logical_unit_date", label), label + ".logical_unit_date"),
                provenance,
                DateField(raw, "start", label),
                DateField(raw, "finish", label),
                DateField(raw, "original_start", label),
                DateField(raw, "original_finish", label),
                PyDecimal.Parse(WireJson.Text(raw, "quantity_percentage", label)),
                WireJson.Bool(raw, "locked_completed", label));
        }

        private static DateTime DateField(JObject raw, string key, string label)
            => PyDateTime.ParseDateTime(WireJson.Text(raw, key, label), true, label + "." + key);

        public static string ProvenanceText(ApplyProvenance provenance)
        {
            switch (provenance)
            {
                case ApplyProvenance.Unchanged: return "unchanged";
                case ApplyProvenance.Direct: return "direct";
                case ApplyProvenance.Cascade: return "cascade";
                default: throw new ArgumentOutOfRangeException(nameof(provenance));
            }
        }

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["native_bar_xml_key"] = NativeBarXmlKey,
            ["native_bar_key"] = NativeBarKey,
            ["logical_unit_id"] = LogicalUnitId,
            ["logical_unit_date"] = PyDateTime.DateIso(LogicalUnitDate),
            ["provenance"] = ProvenanceText(Provenance),
            ["start"] = PyDateTime.IsoFormat(Start),
            ["finish"] = PyDateTime.IsoFormat(Finish),
            ["original_start"] = PyDateTime.IsoFormat(OriginalStart),
            ["original_finish"] = PyDateTime.IsoFormat(OriginalFinish),
            ["quantity_percentage"] = QuantityPercentage.ToFixedString(),
            ["locked_completed"] = LockedCompleted,
        };
    }
}
