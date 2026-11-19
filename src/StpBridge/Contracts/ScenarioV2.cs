using System;
using System.Collections.Generic;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Contracts
{
    /// <summary>
    /// <c>vgs-stp-splits/2</c> — the legacy scenario POSTed to <c>/scenario</c>.
    /// Port of <c>stp_v2_core.load_scenario_v2</c> / <c>scenario_v2_to_json_value</c>.
    /// </summary>
    public sealed class ScenarioV2
    {
        public const string SchemaId = "vgs-stp-splits/2";

        public ScenarioV2(string projectFingerprint, string precedenceFingerprint, DateTime horizonStart, DateTime horizonFinish, IEnumerable<ScenarioActivity> activities)
        {
            ProjectFingerprint = ApplyPackage.NonEmpty(projectFingerprint, "scenario.project_fingerprint");
            PrecedenceFingerprint = ApplyPackage.NonEmpty(precedenceFingerprint, "scenario.precedence_fingerprint");
            if (horizonStart.TimeOfDay != TimeSpan.Zero || horizonFinish.TimeOfDay != TimeSpan.Zero)
                throw new WireFormatException("scenario horizon endpoints must be civil midnight");
            if (horizonFinish <= horizonStart || horizonFinish - horizonStart > TimeSpan.FromDays(366))
                throw new WireFormatException("scenario horizon interval is invalid");
            HorizonStart = horizonStart;
            HorizonFinish = horizonFinish;
            Activities = (activities ?? throw new ArgumentNullException(nameof(activities))).ToList().AsReadOnly();
            if (Activities.Count == 0) throw new WireFormatException("scenario activities must not be empty");
            var activityIds = new HashSet<string>(StringComparer.Ordinal);
            var fragmentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var activity in Activities)
            {
                if (!activityIds.Add(activity.ActivityId)) throw new WireFormatException($"duplicate scenario activity ID '{activity.ActivityId}'");
                foreach (var fragment in activity.Fragments)
                {
                    if (!fragmentIds.Add(fragment.Id)) throw new WireFormatException($"duplicate fragment ID '{fragment.Id}'");
                }
            }
        }

        public string Schema => SchemaId;

        public string ProjectFingerprint { get; }

        public string PrecedenceFingerprint { get; }

        public DateTime HorizonStart { get; }

        public DateTime HorizonFinish { get; }

        public IReadOnlyList<ScenarioActivity> Activities { get; }

        public static ScenarioV2 Parse(string json) => FromJson(WireJson.Parse(json));

        public static ScenarioV2 FromJson(JToken token)
        {
            var raw = WireJson.ExactObject(token, "scenario", "schema", "project_fingerprint", "precedence_fingerprint", "horizon_start", "horizon_finish", "activities");
            WireJson.RequireConstant(WireJson.Text(raw, "schema", "scenario"), SchemaId, "scenario schema");
            return new ScenarioV2(
                WireJson.Text(raw, "project_fingerprint", "scenario"),
                WireJson.Text(raw, "precedence_fingerprint", "scenario"),
                Time(raw, "horizon_start", "scenario"),
                Time(raw, "horizon_finish", "scenario"),
                WireJson.List(raw, "activities", "scenario").Select(ScenarioActivity.FromJson));
        }

        /// <summary><c>_required_datetime</c>: a non-empty string containing <c>T</c>.</summary>
        internal static DateTime Time(JObject raw, string key, string label)
            => PyDateTime.ParseDateTime(WireJson.Text(raw, key, label), false, label + "." + key);

        public JObject ToJson() => new JObject
        {
            ["schema"] = Schema,
            ["project_fingerprint"] = ProjectFingerprint,
            ["precedence_fingerprint"] = PrecedenceFingerprint,
            ["horizon_start"] = PyDateTime.IsoFormat(HorizonStart),
            ["horizon_finish"] = PyDateTime.IsoFormat(HorizonFinish),
            ["activities"] = new JArray(Activities.Select(a => a.ToJson())),
        };
    }

    public sealed class ScenarioActivity
    {
        public ScenarioActivity(string activityId, string provenance, IEnumerable<ScenarioFragment> fragments)
        {
            ActivityId = ApplyPackage.NonEmpty(activityId, "scenario activity.activity_id");
            if (provenance != "direct" && provenance != "cascade") throw new WireFormatException($"unrecognized provenance '{provenance}'");
            Provenance = provenance;
            Fragments = (fragments ?? throw new ArgumentNullException(nameof(fragments))).ToList().AsReadOnly();
            if (Fragments.Count == 0) throw new WireFormatException($"activity ID '{activityId}' has no fragments");
        }

        public string ActivityId { get; }

        /// <summary><c>direct</c> or <c>cascade</c>.</summary>
        public string Provenance { get; }

        public IReadOnlyList<ScenarioFragment> Fragments { get; }

        internal static ScenarioActivity FromJson(JToken token)
        {
            var raw = WireJson.ExactObject(token, "scenario activity", "activity_id", "provenance", "fragments");
            return new ScenarioActivity(
                WireJson.Text(raw, "activity_id", "scenario activity"),
                WireJson.Text(raw, "provenance", "scenario activity"),
                WireJson.List(raw, "fragments", "scenario activity").Select(ScenarioFragment.FromJson));
        }

        public JObject ToJson() => new JObject
        {
            ["activity_id"] = ActivityId,
            ["provenance"] = Provenance,
            ["fragments"] = new JArray(Fragments.Select(f => f.ToJson())),
        };
    }

    public sealed class ScenarioFragment
    {
        private const string Label = "scenario fragment";

        public ScenarioFragment(string id, string nativeBarXmlKey, string nativeBarKey, DateTime start, DateTime finish, DateTime originalStart, DateTime originalFinish, PyDecimal quantityPercentage, bool lockedCompleted)
        {
            Id = ApplyPackage.NonEmpty(id, Label + ".id");
            NativeBarXmlKey = ApplyPackage.NonEmpty(nativeBarXmlKey, Label + ".native_bar_xml_key");
            NativeBarKey = ApplyPackage.NonEmpty(nativeBarKey, Label + ".native_bar_key");
            if (start >= finish) throw new WireFormatException($"fragment '{id}' duration must be positive");
            if (originalStart >= originalFinish) throw new WireFormatException($"original fragment '{id}' duration must be positive");
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

        public DateTime Start { get; }

        public DateTime Finish { get; }

        public DateTime OriginalStart { get; }

        public DateTime OriginalFinish { get; }

        public PyDecimal QuantityPercentage { get; }

        public bool LockedCompleted { get; }

        internal static ScenarioFragment FromJson(JToken token)
        {
            var raw = WireJson.ExactObject(token, Label, "id", "native_bar_xml_key", "native_bar_key", "start", "finish", "original_start", "original_finish", "quantity_percentage", "locked_completed");
            return new ScenarioFragment(
                WireJson.Text(raw, "id", Label),
                WireJson.Text(raw, "native_bar_xml_key", Label),
                WireJson.Text(raw, "native_bar_key", Label),
                ScenarioV2.Time(raw, "start", Label),
                ScenarioV2.Time(raw, "finish", Label),
                ScenarioV2.Time(raw, "original_start", Label),
                ScenarioV2.Time(raw, "original_finish", Label),
                PyDecimal.Parse(WireJson.Text(raw, "quantity_percentage", Label)),
                WireJson.Bool(raw, "locked_completed", Label));
        }

        public JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["native_bar_xml_key"] = NativeBarXmlKey,
            ["native_bar_key"] = NativeBarKey,
            ["start"] = PyDateTime.IsoFormat(Start),
            ["finish"] = PyDateTime.IsoFormat(Finish),
            ["original_start"] = PyDateTime.IsoFormat(OriginalStart),
            ["original_finish"] = PyDateTime.IsoFormat(OriginalFinish),
            ["quantity_percentage"] = QuantityPercentage.ToString(),
            ["locked_completed"] = LockedCompleted,
        };
    }
}
