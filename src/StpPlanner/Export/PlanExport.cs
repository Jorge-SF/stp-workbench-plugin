// The command bar's Exportar group: what Excel, PDF and STP suelto export from the current planner view.
// Mirrors the template's buildExcelExportPayload / renderPrintReport (reference/web/short_term_planner_template_v2.html):
// the visible rows (filters, search and sort applied), one column per visible civil day, the planned amount of each
// day's pending segments, and one "Capacidad · <tipo>" aggregate row per type.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Planner.Export
{
    /// <summary>Spanish messages of the Exportar commands (same tone as the template's notices).</summary>
    public static class ExportMessages
    {
        /// <summary>Default report title (template <c>printConfig.title</c> default).</summary>
        public const string DefaultTitle = "Short Term Planner UG";

        /// <summary>Template <c>downloadExcelExport</c>.</summary>
        public const string ExcelNeedsLive = "La exportación Excel requiere STP en vivo.";

        /// <summary>Template <c>downloadExcelExport</c> / <c>printPlanner</c>.</summary>
        public const string NoDays = "No hay días visibles para exportar.";

        /// <summary>Template <c>printPlanner</c>.</summary>
        public const string NoRows = "No hay actividades visibles para exportar.";

        /// <summary>Plugin: the bridge exports at most 31 days.</summary>
        public static readonly string ExcelTooManyDays = $"La exportación Excel admite hasta {ExcelExportRequest.MaxDays} días visibles. Reduzca el rango.";

        /// <summary>Plugin: the bridge exports at most 1000 activities.</summary>
        public static readonly string ExcelTooManyRows = $"La exportación Excel admite hasta {ExcelExportRequest.MaxActivities} actividades visibles. Filtre la vista.";

        /// <summary>Template <c>downloadExcelExport</c>.</summary>
        public const string ExcelFailed = "No se pudo exportar el archivo Excel.";

        /// <summary>Plugin: Excel saved.</summary>
        public const string ExcelSaved = "Archivo Excel exportado: {0}";

        /// <summary>Plugin: PDF saved.</summary>
        public const string PdfSaved = "Reporte PDF exportado: {0}";

        /// <summary>Template <c>printPlanner</c>.</summary>
        public const string PdfFailed = "No se pudo preparar el reporte PDF.";

        /// <summary>Template <c>exportStandalonePlanner</c> (plus the file).</summary>
        public const string StandaloneSaved = "STP independiente exportado sin puente vivo ni permiso para aplicar en VGS: {0}";

        /// <summary>Template <c>exportStandalonePlanner</c>.</summary>
        public const string StandaloneFailed = "No se pudo exportar el STP independiente.";

        /// <summary>Plugin: the web resources script 2 copies into the runtime folder are missing.</summary>
        public const string StandaloneNoTemplate = "No se encontró la plantilla del planificador en {0}. Vuelva a ejecutar 2. VGS_ShortTermPlanner.py.";

        /// <summary>Plugin: static bridge message embedded in the standalone file (template <c>staticBridge.message</c>).</summary>
        public const string StandaloneBridgeMessage = "HTML independiente; actualización en vivo no disponible.";
    }

    /// <summary>One day cell of an exported activity row.</summary>
    public sealed class PlanExportCell
    {
        /// <summary>Gets the sum of the day's pending (not completed) scheduled amounts, or <c>null</c> when none.</summary>
        public double? Planned { get; init; }

        /// <summary>Gets a value indicating whether the day has any segment (pending or completed).</summary>
        public bool HasSegment { get; init; }

        /// <summary>Gets the status of the day's dominant segment (largest amount; the first one when none has an amount).</summary>
        public SegmentStatus Status { get; init; }

        /// <summary>Gets the dominant segment's amount (completed included), or <c>null</c>.</summary>
        public double? Shown { get; init; }
    }

    /// <summary>One exported activity row.</summary>
    public sealed class PlanExportRow
    {
        public string ActivityId { get; init; }

        public string Name { get; init; }

        public string Type { get; init; }

        public string Resource { get; init; }

        /// <summary>Gets the type colour (<c>#RRGGBB</c>).</summary>
        public string Color { get; init; }

        /// <summary>Gets one cell per <see cref="PlanExport.Days"/>.</summary>
        public IReadOnlyList<PlanExportCell> Cells { get; init; }

        /// <summary>Gets the pending amount planned over the exported days.</summary>
        public double PeriodTotal { get; init; }

        /// <summary>Gets the activity unit (may be empty).</summary>
        public string Unit { get; init; }
    }

    /// <summary>One "Capacidad · tipo" row.</summary>
    public sealed class PlanExportCapacity
    {
        public string Type { get; init; }

        public string Color { get; init; }

        public double Objective { get; init; }

        /// <summary>Gets one (scheduled, over) pair per day.</summary>
        public IReadOnlyList<(double Scheduled, bool Over)> Cells { get; init; }

        public string Label => "Capacidad · " + Type;

        /// <summary>Template text: <c>scheduled/objective</c> with an objective, else <c>scheduled</c>.</summary>
        public string CellText(int index) => Objective > 0
            ? PlanExport.Number(Cells[index].Scheduled) + "/" + PlanExport.Number(Objective)
            : PlanExport.Number(Cells[index].Scheduled);
    }

    /// <summary>The planner view frozen for export.</summary>
    public sealed class PlanExport
    {
        private PlanExport()
        {
        }

        public string Title { get; private set; }

        public string ProjectName { get; private set; }

        public string ProjectKey { get; private set; }

        public IReadOnlyList<HorizonDay> Days { get; private set; }

        public IReadOnlyList<PlanExportRow> Rows { get; private set; }

        public IReadOnlyList<PlanExportCapacity> Capacity { get; private set; }

        /// <summary>Gets the type legend (types present in the rows, Spanish order).</summary>
        public IReadOnlyList<(string Label, string Color)> Legend { get; private set; }

        /// <summary>Gets the snapshot time (report metadata).</summary>
        public DateTime SnapshotTime { get; private set; }

        /// <summary>Template <c>rangeLabel</c>: <c>first — last</c>.</summary>
        public string RangeLabel => Days.Count == 0 ? string.Empty : Days[0].Label + " — " + Days[Days.Count - 1].Label;

        /// <summary>Freezes the session's current view.</summary>
        /// <param name="session">The planner.</param>
        /// <param name="title">Report title, or the default.</param>
        /// <returns>The export.</returns>
        public static PlanExport From(PlannerSession session, string title = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var days = session.Days.ToList();
            var index = days.Select((d, i) => (d.Date, i)).ToDictionary(p => p.Date, p => p.i, StringComparer.Ordinal);
            var rows = session.Rows.Select(row => BuildRow(session, row, days, index)).ToList();
            var capacity = session.Capacity.Select(c => new PlanExportCapacity
            {
                Type = c.Type,
                Color = c.Color,
                Objective = c.Objective,
                Cells = days.Select(d =>
                {
                    var cell = c.Cells.FirstOrDefault(x => x.Date == d.Date);
                    return cell == null ? (0d, false) : (cell.Value, cell.IsOver);
                }).ToList(),
            }).ToList();
            var es = StringComparer.Create(CultureInfo.GetCultureInfo("es-MX"), false);
            var legend = rows.Where(r => !string.IsNullOrEmpty(r.Type)).GroupBy(r => r.Type, StringComparer.Ordinal)
                .OrderBy(g => g.Key, es).Select(g => (g.Key, g.First().Color)).ToList();
            return new PlanExport
            {
                Title = string.IsNullOrWhiteSpace(title) ? ExportMessages.DefaultTitle : title.Trim(),
                ProjectName = session.ProjectName ?? string.Empty,
                ProjectKey = session.Snapshot.ProjectKey,
                SnapshotTime = session.Snapshot.ExportedAt,
                Days = days,
                Rows = rows,
                Capacity = capacity,
                Legend = legend,
            };
        }

        private static PlanExportRow BuildRow(PlannerSession session, PlannerRow row, List<HorizonDay> days, Dictionary<string, int> index)
        {
            var byDay = new List<DaySegment>[days.Count];
            foreach (var segment in row.Segments ?? Array.Empty<DaySegment>())
            {
                if (segment.Date == null || !index.TryGetValue(segment.Date, out int i)) continue;
                (byDay[i] = byDay[i] ?? new List<DaySegment>()).Add(segment);
            }

            var cells = new List<PlanExportCell>(days.Count);
            double total = 0;
            foreach (var segments in byDay)
            {
                if (segments == null)
                {
                    cells.Add(new PlanExportCell());
                    continue;
                }

                // buildExcelExportPayload plannedFor: pending segments only; null when the day has none.
                var pending = segments.Where(s => s.Status != SegmentStatus.Completed).ToList();
                double? planned = pending.Count == 0 ? (double?)null : pending.Sum(s => s.Amount ?? 0);
                total += planned ?? 0;

                // summarizePrintDaySegments: the dominant segment has the largest amount.
                DaySegment dominant = segments[0];
                double best = double.NegativeInfinity;
                foreach (var s in segments)
                {
                    if (s.Amount.HasValue && s.Amount.Value > best)
                    {
                        dominant = s;
                        best = s.Amount.Value;
                    }
                }

                double? shown = segments.Any(s => s.Amount.HasValue) ? segments.Where(s => s.Amount.HasValue).Sum(s => s.Amount.Value) : (double?)null;
                cells.Add(new PlanExportCell { Planned = planned, HasSegment = true, Status = dominant.Status, Shown = shown });
            }

            return new PlanExportRow
            {
                ActivityId = row.ActivityId,
                Name = row.Name ?? string.Empty,
                Type = row.Type ?? string.Empty,
                Resource = row.Resource ?? string.Empty,
                Color = session.TypeColor(row.Type),
                Cells = cells,
                PeriodTotal = total,
                Unit = row.Unit ?? string.Empty,
            };
        }

        /// <summary>Template <c>excelNumber</c>: at most two decimals, trailing zeros dropped.</summary>
        public static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "—";
            return Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Checks what the bridge's Excel exporter accepts; returns the Spanish reason or an empty string.</summary>
        public string ExcelBlockedReason()
        {
            if (Days.Count == 0) return ExportMessages.NoDays;
            if (Days.Count > ExcelExportRequest.MaxDays) return ExportMessages.ExcelTooManyDays;
            if (Rows.Count > ExcelExportRequest.MaxActivities) return ExportMessages.ExcelTooManyRows;
            return string.Empty;
        }

        /// <summary>
        /// The <c>vgs-stp-excel-export/1</c> body (template <c>buildExcelExportPayload</c>) without actual progress:
        /// <c>actualProgressMode</c> false, <c>actual</c> all null, no chart types or chart images.
        /// </summary>
        /// <exception cref="InvalidOperationException"><see cref="ExcelBlockedReason"/> is not empty.</exception>
        public ExcelExportRequest ToExcelRequest()
        {
            string blocked = ExcelBlockedReason();
            if (blocked.Length > 0) throw new InvalidOperationException(blocked);
            var request = new ExcelExportRequest
            {
                ProjectKey = ProjectKey,
                Title = Clip(Title, 160),
                RangeLabel = Clip(RangeLabel, 160),
                ActualProgressMode = false,
            };
            foreach (var day in Days) request.Days.Add(new ExcelExportDay(day.Date, day.Label));
            foreach (var row in Rows)
            {
                request.Activities.Add(new ExcelExportActivity
                {
                    Id = Clip(row.ActivityId, 160),
                    Name = Clip(row.Name, 160),
                    Type = Clip(row.Type, 160),
                    Resource = Clip(row.Resource, 160),
                    Planned = row.Cells.Select(c => c.Planned).ToList(),
                    Actual = row.Cells.Select(_ => (double?)null).ToList(),
                    Color = row.Color,
                });
            }

            request.Aggregates = new JArray(Capacity.Select(c => new JObject
            {
                ["label"] = c.Label,
                ["values"] = new JArray(Enumerable.Range(0, Days.Count).Select(c.CellText)),
                ["states"] = new JArray(c.Cells.Select(x => x.Over ? "under-progress" : string.Empty)),
            }));
            request.Legend = new JArray(Legend.Select(l => new JObject { ["label"] = l.Label, ["color"] = l.Color }));
            return request;
        }

        // The server blanks any text longer than its limit (stp_v2_excel_export._text); cut it instead.
        private static string Clip(string text, int maximum)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length <= maximum ? text : text.Substring(0, maximum - 1).TrimEnd() + "…";
        }
    }
}
