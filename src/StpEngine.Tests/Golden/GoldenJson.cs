using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Engine.Tests.Golden
{
    /// <summary>JSON ↔ engine mapping for the golden tests (the production DTO mapping belongs to StpBridge).</summary>
    internal static class GoldenJson
    {
        public static JToken Load(string path)
        {
            using (var reader = new JsonTextReader(new StreamReader(path)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
            {
                return JToken.ReadFrom(reader);
            }
        }

        public static JToken Parse(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
            {
                return JToken.ReadFrom(reader);
            }
        }

        // ---------------------------------------------------------------- snapshot → engine

        public static PlannerSnapshot ToSnapshot(JToken json) => new PlannerSnapshot
        {
            Activities = json["activities"]?.Select(a => new SnapshotActivity
            {
                Id = Str(a["id"]),
                NativeBars = a["nativeBars"]?.Select(b => new NativeBar
                {
                    Key = Str(b["key"]),
                    XmlKey = Str(b["xmlKey"]),
                    Start = Str(b["start"]),
                    Finish = Str(b["finish"]),
                    QuantityPercentage = Str(b["quantityPercentage"]),
                    PercentComplete = Str(b["percentComplete"]),
                    CompleteWorkedMinutes = Str(b["completeWorkedMinutes"]),
                }).ToArray(),
                Fragments = a["fragments"]?.Select(ToFragment).ToArray(),
                NativeAnchors = a["nativeAnchors"]?.Select(n => new NativeAnchor
                {
                    LeftBarXmlKey = Str(n["leftBarXmlKey"]),
                    LeftBarKey = Str(n["leftBarKey"]),
                    RightBarXmlKey = Str(n["rightBarXmlKey"]),
                    RightBarKey = Str(n["rightBarKey"]),
                    At = Str(n["at"]),
                    RightAt = Str(n["rightAt"]),
                }).ToArray(),
            }).ToArray(),
            Precedences = json["precedences"]?.Select(l => new PrecedenceLink
            {
                Key = Str(l["key"]),
                PredecessorId = Str(l["predecessorId"] ?? l["predecessor_id"]),
                SuccessorId = Str(l["successorId"] ?? l["successor_id"]),
                PredecessorBar = Str(l["predecessorBar"] ?? l["predecessor_bar"]),
                SuccessorBar = Str(l["successorBar"] ?? l["successor_bar"]),
                Type = Str(l["type"] ?? l["precedenceType"] ?? l["precedence_type"]),
                LagMicroseconds = Str(l["lagMicroseconds"] ?? l["lag_microseconds"]),
                LagSeconds = (long?)(l["lagSeconds"] ?? l["lag_seconds"] ?? l["lag"]),
                GroupKey = Str(l["groupKey"]),
            }).ToArray(),
        };

        private static PlannerFragment ToFragment(JToken f) => new PlannerFragment
        {
            Id = Str(f["id"]),
            NativeBarXmlKey = Str(f["nativeBarXmlKey"]),
            NativeBarKey = Str(f["nativeBarKey"]),
            Start = Str(f["start"]),
            Finish = Str(f["finish"]),
            OriginalStart = Str(f["originalStart"]),
            OriginalFinish = Str(f["originalFinish"]),
            LogicalUnitDate = Str(f["logicalUnitDate"]),
            DisplaySuppressed = (bool?)f["displaySuppressed"],
            QuantityPercentage = Str(f["quantityPercentage"]),
            LockedCompleted = (bool?)f["lockedCompleted"] ?? false,
            SplitOrder = (int?)f["splitOrder"],
        };

        public static CivilHorizon ToHorizon(JToken json) => json == null || json.Type == JTokenType.Null ? null : new CivilHorizon
        {
            HorizonStart = Str(json["horizonStart"]),
            HorizonFinish = Str(json["horizonFinish"]),
            DurationDays = (int?)json["durationDays"],
        };

        private static string Str(JToken token) => token == null || token.Type == JTokenType.Null ? null : (string)token;

        // ---------------------------------------------------------------- engine → JSON (JS property names)

        public static JToken Of(OperationResult r) => new JObject { ["ok"] = r.Ok, ["message"] = r.Message, ["code"] = r.Code };

        public static JToken Of(ActivityEligibility r) => new JObject
        {
            ["showControls"] = r.ShowControls,
            ["backwardEnabled"] = r.BackwardEnabled,
            ["forwardEnabled"] = r.ForwardEnabled,
            ["guidance"] = r.Guidance,
        };

        public static JToken Of(FragmentEligibility r) => new JObject { ["eligible"] = r.Eligible, ["reason"] = r.Reason, ["guidance"] = r.Guidance };

        public static JToken Of(PrecedenceConflictAssessment r) => new JObject
        {
            ["inherited"] = new JArray(r.Inherited.Select(c => new JObject { ["key"] = c.Key, ["predecessorId"] = c.PredecessorId, ["successorId"] = c.SuccessorId })),
            ["blocksOperations"] = r.BlocksOperations,
        };

        public static JToken Of(EngineException e) => new JObject { ["error"] = e.Message, ["code"] = e.Code };

        public static JToken Of(CivilHorizon h) => h == null ? (JToken)JValue.CreateNull() : new JObject
        {
            ["horizonStart"] = h.HorizonStart,
            ["horizonFinish"] = h.HorizonFinish,
            ["durationDays"] = h.DurationDays,
        };

        public static JToken Of(HorizonDisplay h) => h == null ? (JToken)JValue.CreateNull() : new JObject
        {
            ["start"] = h.Start,
            ["finish"] = h.Finish,
            ["durationDays"] = h.DurationDays,
        };

        /// <summary>run.mjs <c>stateSummary</c>.</summary>
        public static JToken Summary(ControllerState s) => new JObject
        {
            ["horizon"] = Of(s.Horizon),
            ["displayHorizon"] = Of(s.DisplayHorizon),
            ["hasPending"] = s.HasPending,
            ["hasLocalMovements"] = s.HasLocalMovements,
            ["canDownload"] = s.CanDownload,
            ["status"] = s.Status,
        };

        /// <summary>run.mjs <c>fragmentRows</c> (sorted by activity id, then id, with <c>localeCompare</c>).</summary>
        public static JToken FragmentRows(PlannerModel model) => new JArray(model.Fragments
            .OrderBy(f => f, Comparer<PlannerFragment>.Create((a, b) => a.ActivityId == b.ActivityId
                ? JsCompare.LocaleCompare(a.Id, b.Id)
                : JsCompare.LocaleCompare(a.ActivityId, b.ActivityId)))
            .Select(f => new JObject
            {
                ["id"] = f.Id,
                ["start"] = f.Start,
                ["finish"] = f.Finish,
                ["provenance"] = f.Provenance ?? string.Empty,
                ["lockedCompleted"] = f.LockedCompleted,
            }));

        public static JObject Of(ApplyPackage p) => new JObject
        {
            ["schema"] = p.Schema,
            ["project_fingerprint"] = p.ProjectFingerprint,
            ["precedence_fingerprint"] = p.PrecedenceFingerprint,
            ["horizon_start"] = p.HorizonStart,
            ["horizon_finish"] = p.HorizonFinish,
            ["activities"] = new JArray(p.Activities.Select(a => new JObject
            {
                ["activity_id"] = a.ActivityId,
                ["fragments"] = new JArray(a.Fragments.Select(f => new JObject
                {
                    ["id"] = f.Id,
                    ["native_bar_xml_key"] = f.NativeBarXmlKey,
                    ["native_bar_key"] = f.NativeBarKey,
                    ["logical_unit_id"] = f.LogicalUnitId,
                    ["logical_unit_date"] = f.LogicalUnitDate,
                    ["provenance"] = f.Provenance,
                    ["start"] = f.Start,
                    ["finish"] = f.Finish,
                    ["original_start"] = f.OriginalStart,
                    ["original_finish"] = f.OriginalFinish,
                    ["quantity_percentage"] = f.QuantityPercentage,
                    ["locked_completed"] = f.LockedCompleted,
                })),
            })),
        };

        public static JObject Of(ScenarioPackage p) => new JObject
        {
            ["schema"] = p.Schema,
            ["project_fingerprint"] = p.ProjectFingerprint,
            ["precedence_fingerprint"] = p.PrecedenceFingerprint,
            ["horizon_start"] = p.HorizonStart,
            ["horizon_finish"] = p.HorizonFinish,
            ["activities"] = new JArray(p.Activities.Select(a => new JObject
            {
                ["activity_id"] = a.ActivityId,
                ["provenance"] = a.Provenance,
                ["fragments"] = new JArray(a.Fragments.Select(f => new JObject
                {
                    ["id"] = f.Id,
                    ["native_bar_xml_key"] = f.NativeBarXmlKey,
                    ["native_bar_key"] = f.NativeBarKey,
                    ["start"] = f.Start,
                    ["finish"] = f.Finish,
                    ["original_start"] = f.OriginalStart,
                    ["original_finish"] = f.OriginalFinish,
                    ["quantity_percentage"] = f.QuantityPercentage,
                    ["locked_completed"] = f.LockedCompleted,
                })),
            })),
        };
    }
}
