using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using Maptek.Vulcan.Stp.Timeline.Model;

namespace Maptek.Vulcan.Stp.Timeline.Preview
{
    /// <summary>Invented data: the mockup's 12 activities, or a large random plan for performance checks.</summary>
    internal static class FakeData
    {
        private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

        // Mockup type colours (--t-bar ... --t-vol), in mockup order.
        internal static readonly (string Name, string Prefix, Color Color, string Unit, int Qty)[] Types =
        {
            ("Barrenación", "BAR", Rgb(0x3B, 0x78, 0xC4), "m", 300),
            ("Desarrollo", "DES", Rgb(0x2F, 0x8F, 0x5B), "m", 12),
            ("Relleno", "REL", Rgb(0x8A, 0x5C, 0xC4), "m3", 320),
            ("Rezagado", "REZ", Rgb(0xD0, 0x7A, 0x2C), "t", 1700),
            ("Sostenimiento", "SOS", Rgb(0x2B, 0x7F, 0x7A), "pernos", 36),
            ("Servicios", "SRV", Rgb(0xC2, 0x45, 0x5A), "m", 16),
            ("Voladura", "VOL", Rgb(0x4F, 0x86, 0xC6), "t", 6100),
        };

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private sealed class Act
        {
            public string Id, Name, Type, Resource, Unit;
            public int Start, Days, Done, Move;
            public double Qty;
        }

        /// <summary>planner_layout.html: Mon 16/11/2026, 14 days, completed through 21/11, REZ-1425-01 selected.</summary>
        public static TimelineModel Mockup(out string selectedKey)
        {
            var acts = new List<Act>
            {
                A("BAR-1400-02", "Barrenación TJ-1400-04", "Barrenación", "Simba S-02", 2, 4, 0, 238, "m"),
                A("BAR-1425-01", "Barrenación TJ-1425-01", "Barrenación", "Simba S-01", 5, 4, 0, 300, "m", 1),
                A("DES-1425-01", "Crucero 1425 acceso", "Desarrollo", "Jumbo J-02", 1, 3, 3, 7, "m"),
                A("DES-1450-01", "Desarrollo rampa 1450 tramo 1", "Desarrollo", "Jumbo J-01", 0, 3, 3, 12, "m"),
                A("DES-1450-02", "Desarrollo rampa 1450 tramo 2", "Desarrollo", "Jumbo J-01", 3, 4, 0, 12, "m"),
                A("REL-1400-01", "Relleno cementado TJ-1400-03", "Relleno", "Planta relleno", 0, 10, 5, 320, "m3"),
                A("REZ-1400-02", "Rezagado TJ-1400-04", "Rezagado", "Scoop LH-05", 7, 5, 0, 1220, "t"),
                A("REZ-1425-01", "Rezagado TJ-1425-01", "Rezagado", "Scoop LH-07", 10, 5, 0, 1700, "t", 2),
                A("SOS-1450-01", "Sostenimiento rampa 1450", "Sostenimiento", "Empernador B-02", 3, 5, 0, 36, "pernos"),
                A("SRV-1450-01", "Servicios rampa 1450 (aire, agua)", "Servicios", "Cuadrilla servicios", 7, 3, 0, 16, "m"),
                A("VOL-1400-02", "Voladura TJ-1400-04", "Voladura", "Cargadora explosivos", 6, 1, 0, 6100, "t"),
                A("VOL-1425-01", "Voladura TJ-1425-01", "Voladura", "Cargadora explosivos", 9, 1, 0, 8500, "t", 2),
            };
            var links = new[]
            {
                ("DES-1450-01", "DES-1450-02"), ("DES-1450-01", "SOS-1450-01"), ("DES-1450-02", "SRV-1450-01"),
                ("DES-1425-01", "BAR-1425-01"), ("BAR-1425-01", "VOL-1425-01"), ("VOL-1425-01", "REZ-1425-01"),
                ("BAR-1400-02", "VOL-1400-02"), ("VOL-1400-02", "REZ-1400-02"),
            };
            selectedKey = "REZ-1425-01";
            return Build(new DateTime(2026, 11, 16), 14, new DateTime(2026, 11, 21), acts, links);
        }

        /// <summary>
        /// Random plan shaped like a real one: activities start progressively over the horizon (each level runs a
        /// development - drilling - blasting - mucking cycle), ~0.8 precedence per activity, rows sorted by start.
        /// </summary>
        public static TimelineModel Stress(int count, int days, out string selectedKey)
        {
            var rnd = new Random(42);
            var start = new DateTime(2026, 11, 16);
            int doneDay = Math.Min(days, 5);
            var acts = new List<Act>(count);
            var links = new List<(string, string)>();
            for (int i = 0; i < count; i++)
            {
                var type = Types[i % Types.Length];
                int level = 1200 + 25 * (i % 16);
                int s = Math.Max(0, Math.Min(days - 1, (int)((double)i / count * (days - 6)) + rnd.Next(-3, 4)));
                int n = Math.Max(1, Math.Min(days - s, 1 + rnd.Next(8)));
                int done = Math.Max(0, Math.Min(n, doneDay - s));
                int move = done < n && rnd.NextDouble() < 0.04 ? 1 : done < n && rnd.NextDouble() < 0.06 ? 2 : 0;
                double qty = Math.Round(type.Qty * (0.6 + rnd.NextDouble() * 0.8));
                acts.Add(A($"{type.Prefix}-{level}-{i:D4}", $"{type.Name} TJ-{level}-{i % 97:D2}", type.Name,
                    $"Equipo {type.Prefix[0]}-{rnd.Next(1, 12):D2}", s, n, done, qty, type.Unit, move));
                // Predecessor: a recent activity of the previous type in the cycle that ends before this one starts.
                if (i >= Types.Length && rnd.NextDouble() < 0.8)
                {
                    var pred = acts[i - 1 - rnd.Next(Math.Min(i, 20))];
                    if (pred.Start + pred.Days <= s + 1) links.Add((pred.Id, acts[i].Id));
                }
            }
            selectedKey = acts[count / 2].Id;
            return Build(start, days, start.AddDays(doneDay), acts, links, byStart: true);
        }

        private static Act A(string id, string name, string type, string res, int s, int n, int done, double q, string unit, int move = 0) =>
            new Act { Id = id, Name = name, Type = type, Resource = res, Start = s, Days = n, Done = done, Qty = q, Unit = unit, Move = move };

        private static TimelineModel Build(DateTime start, int days, DateTime completed, List<Act> acts, IEnumerable<(string, string)> links, bool byStart = false)
        {
            var model = new TimelineModel { StartDate = start, DayCount = days, CompletedThrough = completed };
            foreach (var type in Types)
            {
                var group = acts.Where(a => a.Type == type.Name)
                    .OrderBy(a => byStart ? a.Start : 0).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
                if (group.Count == 0) continue;
                model.Rows.Add(TimelineRow.Group("group:" + type.Name, type.Name, type.Color, group.Count));
                foreach (var a in group)
                {
                    var row = new TimelineRow
                    {
                        Kind = TimelineRowKind.Activity,
                        Key = a.Id,
                        Id = a.Id,
                        Name = a.Name,
                        Resource = a.Resource,
                        Plan = (a.Qty * a.Days).ToString("#,0", Mx) + " " + a.Unit,
                        Color = type.Color,
                        IsLocked = a.Done >= a.Days,
                        Move = a.Move == 1 ? TimelineMoveKind.Direct : a.Move == 2 ? TimelineMoveKind.Cascade : TimelineMoveKind.None,
                    };
                    string label = TimelineLabels.CompactQuantity(a.Qty);
                    string tip = "Cantidad: " + a.Qty.ToString("#,0", Mx) + " " + a.Unit + " · turno 07:00 — 19:00";
                    for (int k = 0; k < a.Days; k++)
                        row.Blocks.Add(new TimelineBlock(a.Start + k, label, k < a.Done, $"{a.Id}#{k}", tip));
                    model.Rows.Add(row);
                }
            }
            foreach (var (from, to) in links) model.Links.Add(new TimelineLink(from, to));
            return model;
        }
    }
}
