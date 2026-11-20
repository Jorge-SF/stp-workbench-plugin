// Design-time data: the invented project of docs/mockups/planner_layout.html (no client data) as a real
// vgs-stp-snapshot/2, so the preview and the tests run it through the same PlannerSession, projection and timeline
// adapter as a real project.
//
// No Workbench types and no Resources: src/StpClient.Tests compiles this file directly.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Planner;

namespace Maptek.Vulcan.Stp.Views.Planner
{
    public static class DesignSnapshot
    {
        /// <summary>Day index 0 of the mockup: Monday 16/11/2026.</summary>
        public static readonly DateTime Day0 = new DateTime(2026, 11, 16);

        /// <summary>The mockup's snapshot instant ("Instantánea 21/11/2026 08:00").</summary>
        public static readonly DateTime ExportedAt = new DateTime(2026, 11, 21, 8, 0, 0);

        /// <summary>The activity moved directly in the mockup; its successors follow by cascade.</summary>
        public const string MovedActivity = "BAR-1425-01";

        /// <summary>The activity selected in the mockup.</summary>
        public const string SelectedActivity = "REZ-1425-01";

        /// <summary>The design project's key.</summary>
        public static readonly string ProjectKey = new string('d', 64);

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class Act
        {
            public string Id, Name, Type, Resource, Unit, Level;
            public int Start, Days, Done, PerDay;
        }

        // Before the mockup's one direct move: BAR-1425-01 starts a day earlier (4) and VOL/REZ-1425-01 follow it.
        private static readonly Act[] Acts =
        {
            new Act { Id = "BAR-1400-02", Name = "Barrenación TJ-1400-04", Type = "Barrenación", Resource = "Simba S-02", Start = 2, Days = 4, PerDay = 238, Unit = "m", Level = "1400" },
            new Act { Id = "BAR-1425-01", Name = "Barrenación TJ-1425-01", Type = "Barrenación", Resource = "Simba S-01", Start = 4, Days = 4, PerDay = 300, Unit = "m", Level = "1425" },
            new Act { Id = "DES-1425-01", Name = "Crucero 1425 acceso", Type = "Desarrollo", Resource = "Jumbo J-02", Start = 1, Days = 3, Done = 3, PerDay = 7, Unit = "m", Level = "1425" },
            new Act { Id = "DES-1450-01", Name = "Desarrollo rampa 1450 tramo 1", Type = "Desarrollo", Resource = "Jumbo J-01", Start = 0, Days = 3, Done = 3, PerDay = 12, Unit = "m", Level = "1450" },
            new Act { Id = "DES-1450-02", Name = "Desarrollo rampa 1450 tramo 2", Type = "Desarrollo", Resource = "Jumbo J-01", Start = 3, Days = 4, PerDay = 12, Unit = "m", Level = "1450" },
            new Act { Id = "REL-1400-01", Name = "Relleno cementado TJ-1400-03", Type = "Relleno", Resource = "Planta relleno", Start = 0, Days = 10, Done = 5, PerDay = 320, Unit = "m3", Level = "1400" },
            new Act { Id = "REZ-1400-02", Name = "Rezagado TJ-1400-04", Type = "Rezagado", Resource = "Scoop LH-05", Start = 7, Days = 5, PerDay = 1220, Unit = "t", Level = "1400" },
            new Act { Id = "REZ-1425-01", Name = "Rezagado TJ-1425-01", Type = "Rezagado", Resource = "Scoop LH-07", Start = 9, Days = 5, PerDay = 1700, Unit = "t", Level = "1425" },
            new Act { Id = "SOS-1450-01", Name = "Sostenimiento rampa 1450", Type = "Sostenimiento", Resource = "Empernador B-02", Start = 3, Days = 5, PerDay = 36, Unit = "pernos", Level = "1450" },
            new Act { Id = "SRV-1450-01", Name = "Servicios rampa 1450 (aire, agua)", Type = "Servicios", Resource = "Cuadrilla servicios", Start = 7, Days = 3, PerDay = 16, Unit = "m", Level = "1450" },
            new Act { Id = "VOL-1400-02", Name = "Voladura TJ-1400-04", Type = "Voladura", Resource = "Cargadora explosivos", Start = 6, Days = 1, PerDay = 6100, Unit = "t", Level = "1400" },
            new Act { Id = "VOL-1425-01", Name = "Voladura TJ-1425-01", Type = "Voladura", Resource = "Cargadora explosivos", Start = 8, Days = 1, PerDay = 8500, Unit = "t", Level = "1425" },
        };

        private static readonly string[][] Links =
        {
            new[] { "DES-1450-01", "DES-1450-02" }, new[] { "DES-1450-01", "SOS-1450-01" }, new[] { "DES-1450-02", "SRV-1450-01" },
            new[] { "DES-1425-01", "BAR-1425-01" }, new[] { "BAR-1425-01", "VOL-1425-01" }, new[] { "VOL-1425-01", "REZ-1425-01" },
            new[] { "BAR-1400-02", "VOL-1400-02" }, new[] { "VOL-1400-02", "REZ-1400-02" },
        };

        /// <summary>Daily goals per type (Capacidad tab); Servicios has none.</summary>
        public static readonly IReadOnlyDictionary<string, double> Goals = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Barrenación"] = 600, ["Desarrollo"] = 24, ["Relleno"] = 400, ["Rezagado"] = 2500, ["Sostenimiento"] = 40, ["Voladura"] = 8000,
        };

        /// <summary>The snapshot JSON.</summary>
        public static string Json()
        {
            var w = new StringBuilder();
            w.Append("{\"schema\":\"vgs-stp-snapshot/2\",\"projectName\":\"mina_demo.vgantt\",\"projectPath\":\"X:\\\\demo\\\\mina_demo.vgantt\",");
            w.Append("\"projectKey\":\"").Append(ProjectKey).Append("\",\"projectFingerprint\":\"").Append(new string('e', 64));
            w.Append("\",\"precedenceFingerprint\":\"").Append(new string('f', 64)).Append("\",\"exportedAt\":\"").Append(Iso(ExportedAt));
            w.Append("\",\"dateFormat\":\"dd/MM/yyyy\",\"activities\":[");
            w.Append(string.Join(",", Acts.Select(ActivityJson)));
            w.Append("],\"precedences\":[");
            w.Append(string.Join(",", Links.Select((l, i) => LinkJson("L" + (i + 1), l[0], l[1]))));
            w.Append("]}");
            return w.ToString();
        }

        /// <summary>The parsed snapshot.</summary>
        public static SnapshotV2 Snapshot() => SnapshotV2.Parse(Json());

        /// <summary>
        /// A session as the mockup shows it: no live bridge, the type column of the design data, the goals, one direct
        /// move (two cascades follow) and REZ-1425-01 selected.
        /// </summary>
        /// <param name="config">Bridge config; default: static ("Sin conexión").</param>
        /// <param name="bridge">The bridge of a live config (tests use a fake one).</param>
        public static PlannerSession CreateSession(BridgeConfig config = null, IPlannerBridge bridge = null)
        {
            config = config ?? BridgeConfig.Parse("{\"mode\":\"static\",\"endpoint\":\"\",\"token\":\"\",\"projectKey\":\"" + ProjectKey
                + "\",\"bridgeId\":\"\",\"message\":\"Vista previa con datos de diseño.\"}");
            var session = new PlannerSession(Snapshot(), config, bridge, new MemoryHorizonStorage(), new PlannerOptions { TypeObjectives = Goals });
            session.SetRangeStart(Day0.ToString("yyyy-MM-dd", Inv));
            session.SetDuration(14);
            session.MoveActivity(MovedActivity, 1);
            session.SelectedActivityId = SelectedActivity;
            return session;
        }

        private static string ActivityJson(Act a)
        {
            // Completed days in one bar (100 %), pending days in another, joined by a contiguous anchor. Bar keys are not
            // plain numbers: a name that holds a bar key as a word ("tramo 1") is not used as the row label (browser rule).
            var bars = new List<(string Xml, string Key, int From, int To, bool Done)>();
            if (a.Done > 0) bars.Add(("x1", "k1", a.Start, a.Start + a.Done, true));
            if (a.Done < a.Days) bars.Add(("x" + (bars.Count + 1), "k" + (bars.Count + 1).ToString(Inv), a.Start + a.Done, a.Start + a.Days, false));

            var share = DayShares(a.Days);
            var w = new StringBuilder();
            w.Append("{\"id\":\"").Append(a.Id).Append("\",\"collectionKey\":\"c-").Append(a.Id).Append("\",\"overallPercentComplete\":\"");
            w.Append(Dec(100m * a.Done / a.Days)).Append("\",\"grid\":{");
            w.Append(Pair("ID", a.Id)).Append(',').Append(Pair("Description", a.Name)).Append(',').Append(Pair("Resource", a.Resource)).Append(',');
            w.Append(Pair("Activity Type", a.Type)).Append(',').Append(Pair("Nivel", a.Level)).Append(',');
            w.Append(Pair("Rate", a.PerDay.ToString(Inv))).Append(',').Append(Pair("Unit", a.Unit)).Append("},\"nativeBars\":[");
            w.Append(string.Join(",", bars.Select(b =>
                "{\"xmlKey\":\"" + b.Xml + "\",\"key\":\"" + b.Key + "\",\"start\":\"" + Iso(Day(b.From)) + "\",\"finish\":\"" + Iso(Day(b.To))
                + "\",\"quantityPercentage\":\"" + Dec(Enumerable.Range(b.From - a.Start, b.To - b.From).Sum(i => share[i]))
                + "\",\"percentComplete\":\"" + (b.Done ? "100" : "0") + "\",\"completeWorkedMinutes\":\"" + (b.Done ? ((b.To - b.From) * 1440).ToString(Inv) : "0") + "\"}")));
            w.Append("],\"fragments\":[");
            var fragments = new List<string>();
            foreach (var b in bars)
            {
                for (int d = b.From; d < b.To; d++)
                {
                    string start = Iso(Day(d)), finish = Iso(Day(d + 1));
                    fragments.Add("{\"id\":\"" + a.Id + ":" + b.Xml + ":" + b.Key + ":" + (d - b.From).ToString(Inv) + ":" + start + ":" + finish
                        + "\",\"nativeBarXmlKey\":\"" + b.Xml + "\",\"nativeBarKey\":\"" + b.Key + "\",\"start\":\"" + start + "\",\"finish\":\"" + finish
                        + "\",\"originalStart\":\"" + start + "\",\"originalFinish\":\"" + finish + "\",\"logicalUnitDate\":\"" + Day(d).ToString("yyyy-MM-dd", Inv)
                        + "\",\"displaySuppressed\":false,\"quantityPercentage\":\"" + Dec(share[d - a.Start]) + "\",\"lockedCompleted\":" + (b.Done ? "true" : "false") + "}");
                }
            }

            w.Append(string.Join(",", fragments)).Append("],\"nativeAnchors\":[");
            if (bars.Count == 2)
            {
                string at = Iso(Day(bars[1].From));
                w.Append("{\"leftBarXmlKey\":\"x1\",\"leftBarKey\":\"k1\",\"rightBarXmlKey\":\"x2\",\"rightBarKey\":\"k2\",\"at\":\"").Append(at)
                    .Append("\",\"rightAt\":\"").Append(at).Append("\",\"contiguous\":true,\"cumulativeQuantityPercentage\":\"")
                    .Append(Dec(Enumerable.Range(0, a.Done).Sum(i => share[i]))).Append("\",\"links\":[]}");
            }

            return w.Append("]}").ToString();
        }

        private static string LinkJson(string key, string from, string to)
        {
            string fromBar = "k" + BarCount(from).ToString(Inv);   // the predecessor's last bar finishes it
            return "{\"key\":\"" + key + "\",\"predecessorId\":\"" + from + "\",\"successorId\":\"" + to + "\",\"predecessorBar\":\"" + fromBar
                + "\",\"successorBar\":\"k1\",\"type\":\"FinishStart\",\"lagMicroseconds\":\"0\",\"groupKey\":\"\"}";
        }

        private static int BarCount(string id)
        {
            var a = Acts.First(x => x.Id == id);
            return (a.Done > 0 ? 1 : 0) + (a.Done < a.Days ? 1 : 0);
        }

        /// <summary>Per-day share of the activity, 4 decimals, the remainder on the last day (sum = 100).</summary>
        private static decimal[] DayShares(int days)
        {
            var result = new decimal[days];
            decimal each = Math.Floor(100m / days * 10000m) / 10000m;
            for (int i = 0; i < days; i++) result[i] = each;
            result[days - 1] = 100m - each * (days - 1);
            return result;
        }

        private static DateTime Day(int index) => Day0.AddDays(index);

        private static string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv);

        private static string Dec(decimal d) => d.ToString("0.####", Inv);

        private static string Pair(string key, string value) => "\"" + key + "\":\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
