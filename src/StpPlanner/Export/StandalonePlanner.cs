// STP suelto: the browser planner as one offline HTML file, with the current plan in it and no live bridge
// (template exportStandalonePlanner / standaloneDocumentHtml). Built from the web resources script 2 copies into the
// per-user runtime folder (stp_core/runtime.py materialize_runtime_resources): the template's module placeholders are
// filled as the removed render_planner of script 2 did, and its STANDALONE_BOOTSTRAP block is replaced with
// { snapshot, plannerState: { model, horizon }, localState } and a static bridge, so the page opens on this plan and
// can never apply to VGS.
// localState carries the "Avance real" values and boxes (ActualProgressStore.ToJson, the template's
// standaloneLocalState shape), so the page opens with the same boxes; its mode starts on when there are any.
// Not carried (yet): the plugin's local resource assignments / capacity exemptions; the page uses its own saved
// preferences.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Maptek.Vulcan.Stp.Engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Planner.Export
{
    /// <summary>The web resources the standalone page is built from.</summary>
    public sealed class StandaloneResources
    {
        public const string TemplateName = "short_term_planner_template_v2.html";
        public const string LogoName = "assets/Maptek_highres.jpg";

        /// <summary>Runtime file name → accepted names (bundle name first, then reference/web's), as in runtime.py.</summary>
        private static readonly (string Placeholder, string[] Names)[] Modules =
        {
            ("__VGS_STP_V2_LOGIC__", new[] { "stp_v2_planner_logic.js", "planner_logic.js" }),
            ("__VGS_STP_V2_TIMELINE_LOGIC__", new[] { "stp_v2_timeline_logic.js", "timeline_logic.js" }),
            ("__VGS_STP_V2_REFRESH_CLIENT__", new[] { "stp_v2_refresh_client.js", "refresh_client.js" }),
            ("__VGS_STP_V2_COMPAT_LOGIC__", new[] { "stp_v2_v1_compat_logic.js", "v1_compat_logic.js" }),
        };

        public string Template { get; init; }

        /// <summary>Gets the module sources by placeholder.</summary>
        public IReadOnlyDictionary<string, string> ModuleSources { get; init; }

        /// <summary>Gets the footer logo JPEG, or <c>null</c> (the public repository does not hold it).</summary>
        public byte[] Logo { get; init; }

        /// <summary>Reads the resources from a folder (the runtime folder, or reference/web in a checkout).</summary>
        /// <returns>The resources, or <c>null</c> when the template or a module is missing.</returns>
        public static StandaloneResources TryLoad(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            string template = Path.Combine(folder, TemplateName);
            if (!File.Exists(template)) return null;
            var modules = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (placeholder, names) in Modules)
            {
                string path = names.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
                if (path == null) return null;
                modules[placeholder] = File.ReadAllText(path, Encoding.UTF8);
            }

            string logo = Path.Combine(folder, LogoName.Replace('/', Path.DirectorySeparatorChar));
            return new StandaloneResources
            {
                Template = File.ReadAllText(template, Encoding.UTF8),
                ModuleSources = modules,
                Logo = File.Exists(logo) ? File.ReadAllBytes(logo) : null,
            };
        }
    }

    /// <summary>Renders the standalone planner page.</summary>
    public static class StandalonePlanner
    {
        /// <summary>Default file name (template <c>exportStandalonePlanner</c>).</summary>
        public const string DefaultFileName = "short_term_planner_standalone.html";

        public const string BootstrapBegin = "/*STP_V2_STANDALONE_BOOTSTRAP_BEGIN*/";
        public const string BootstrapEnd = "/*STP_V2_STANDALONE_BOOTSTRAP_END*/";
        private const string LogoMarker = "__MAPTEK_LOGO_DATA_URL__";
        private const string NoLogo = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

        private static readonly string[] DataPlaceholders = { "__VGS_STP_V2_SNAPSHOT__", "__VGS_STP_V2_BRIDGE__" };
        private static readonly Regex AnyPlaceholder = new Regex("__VGS_STP_V2_[A-Z_]+__", RegexOptions.CultureInvariant);
        private static readonly Regex ScriptEnd = new Regex("</script", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Renders the page for the session's current plan.</summary>
        public static string Render(StandaloneResources resources, PlannerSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var bootstrap = new JObject
            {
                ["snapshot"] = session.Snapshot.ToJson(),
                ["plannerState"] = new JObject
                {
                    ["model"] = ModelJson(session.State.Model),
                    ["horizon"] = HorizonJson(session.Horizon),
                },
                ["localState"] = ActualProgressStore.ToJson(session.ActualProgressState),
            };
            var bridge = new JObject
            {
                ["mode"] = "static",
                ["endpoint"] = string.Empty,
                ["token"] = string.Empty,
                ["projectKey"] = session.Snapshot.ProjectKey,
                ["bridgeId"] = string.Empty,
                ["message"] = ExportMessages.StandaloneBridgeMessage,
                ["precedenceConflictPolicy"] = session.PrecedencePolicy,
            };
            return Render(resources, bootstrap, bridge);
        }

        /// <summary>Fills the template (the old script 2 render_planner checks) and replaces its bootstrap block.</summary>
        public static string Render(StandaloneResources resources, JObject bootstrap, JObject bridge)
        {
            if (resources?.Template == null) throw new ArgumentNullException(nameof(resources));
            string template = resources.Template;
            var expected = new HashSet<string>(DataPlaceholders.Concat(resources.ModuleSources.Keys), StringComparer.Ordinal);
            foreach (string placeholder in expected)
            {
                if (Count(template, placeholder) != 1) throw new InvalidDataException("la plantilla debe contener cada marcador V2 exactamente una vez");
            }

            if (!AnyPlaceholder.Matches(template).Cast<Match>().All(m => expected.Contains(m.Value)))
                throw new InvalidDataException("la plantilla contiene un marcador V2 desconocido");

            int begin = template.IndexOf(BootstrapBegin, StringComparison.Ordinal);
            int end = begin < 0 ? -1 : template.IndexOf(BootstrapEnd, begin, StringComparison.Ordinal);
            if (begin < 0 || end < 0) throw new InvalidDataException("No se pudo aislar la configuración independiente.");
            string block = template.Substring(begin, end + BootstrapEnd.Length - begin);
            if (DataPlaceholders.Any(p => !block.Contains(p))) throw new InvalidDataException("No se pudo aislar la configuración independiente.");

            // Same replacement as standaloneDocumentHtml.
            string replacement = BootstrapBegin + "\n"
                + "    const standaloneBootstrap = " + SafeJson(bootstrap) + ";\n"
                + "    const snapshot = standaloneBootstrap.snapshot;\n"
                + "    const bridgeConfig = " + SafeJson(bridge) + ";\n"
                + "    " + BootstrapEnd;
            var html = new StringBuilder(template.Length + 1024);
            html.Append(template, 0, begin).Append(replacement).Append(template, end + BootstrapEnd.Length, template.Length - end - BootstrapEnd.Length);

            int logos = Count(template, LogoMarker);
            if (logos > 1) throw new InvalidDataException("la plantilla debe contener un solo logotipo de pie de página");
            if (logos == 1)
            {
                html.Replace(LogoMarker, resources.Logo == null ? NoLogo : "data:image/jpeg;base64," + Convert.ToBase64String(resources.Logo));
            }

            foreach (var module in resources.ModuleSources) html.Replace(module.Key, SafeLogic(module.Value));
            string result = html.ToString();
            if (result.Contains("__VGS_STP_V2_")) throw new InvalidDataException("marcador V2 sin reemplazar");

            // The page is static: never offer Aplicar a VGS (standaloneDocumentHtml disables the button too).
            return result;
        }

        /// <summary>The engine model with the JS property names (planner_logic.js <c>buildPlannerModel</c>).</summary>
        public static JObject ModelJson(PlannerModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            return new JObject
            {
                ["activities"] = new JArray(model.Activities.Select(a =>
                {
                    var o = new JObject { ["id"] = a.Id };
                    if (a.NativeBars != null)
                    {
                        o["nativeBars"] = new JArray(a.NativeBars.Select(b => new JObject
                        {
                            ["xmlKey"] = b.XmlKey,
                            ["key"] = b.Key,
                            ["start"] = b.Start,
                            ["finish"] = b.Finish,
                            ["quantityPercentage"] = b.QuantityPercentage,
                            ["percentComplete"] = b.PercentComplete,
                            ["completeWorkedMinutes"] = b.CompleteWorkedMinutes,
                        }));
                    }

                    return o;
                })),
                ["precedences"] = new JArray(model.Precedences.Select(l =>
                {
                    var o = new JObject
                    {
                        ["key"] = l.Key,
                        ["predecessorId"] = l.PredecessorId,
                        ["successorId"] = l.SuccessorId,
                        ["predecessorBar"] = l.PredecessorBar,
                        ["successorBar"] = l.SuccessorBar,
                        ["type"] = l.Type,
                    };
                    if (l.LagMicroseconds != null) o["lagMicroseconds"] = l.LagMicroseconds;
                    else if (l.LagSeconds.HasValue) o["lagSeconds"] = l.LagSeconds.Value;
                    if (l.GroupKey != null) o["groupKey"] = l.GroupKey;
                    return o;
                })),
                ["fragments"] = new JArray(model.Fragments.Select(f =>
                {
                    var o = new JObject
                    {
                        ["id"] = f.Id,
                        ["nativeBarXmlKey"] = f.NativeBarXmlKey,
                        ["nativeBarKey"] = f.NativeBarKey,
                        ["start"] = f.Start,
                        ["finish"] = f.Finish,
                        ["originalStart"] = f.OriginalStart,
                        ["originalFinish"] = f.OriginalFinish,
                    };
                    if (f.LogicalUnitDate != null) o["logicalUnitDate"] = f.LogicalUnitDate;
                    if (f.DisplaySuppressed.HasValue) o["displaySuppressed"] = f.DisplaySuppressed.Value;
                    if (f.QuantityPercentage != null) o["quantityPercentage"] = f.QuantityPercentage;
                    o["lockedCompleted"] = f.LockedCompleted;
                    if (f.SplitOrder.HasValue) o["splitOrder"] = f.SplitOrder.Value;
                    o["activityId"] = f.ActivityId;
                    o["provenance"] = f.Provenance ?? string.Empty;
                    o["anchorBefore"] = f.AnchorBefore ?? string.Empty;
                    o["anchorAfter"] = f.AnchorAfter ?? string.Empty;
                    return o;
                })),
            };
        }

        private static JToken HorizonJson(CivilHorizon horizon) => horizon == null
            ? (JToken)JValue.CreateNull()
            : new JObject
            {
                ["horizonStart"] = horizon.HorizonStart,
                ["horizonFinish"] = horizon.HorizonFinish,
                ["durationDays"] = horizon.DurationDays,
            };

        /// <summary>Template <c>jsonForInlineScript</c> (formerly script 2 <c>_safe_json</c>).</summary>
        internal static string SafeJson(JToken value) => value.ToString(Formatting.None)
            .Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026")
            .Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");

        /// <summary>Only an HTML end tag is dangerous in executable source (formerly script 2 <c>_safe_logic</c>).</summary>
        internal static string SafeLogic(string source) => ScriptEnd.Replace(source, "<\\/script").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");

        private static int Count(string text, string value)
        {
            int n = 0;
            for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
