using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Maptek.Vulcan.Stp.Bridge.Json;
using Newtonsoft.Json.Linq;

namespace Maptek.Vulcan.Stp.Bridge.Contracts
{
    /// <summary>
    /// <c>vgs-stp-excel-export/1</c> — the body of <c>POST /export-xlsx</c>.
    /// <see cref="Validate"/> mirrors every rejecting check of <c>stp_v2_excel_export.validate_excel_export_payload</c>
    /// (plus the server's <c>projectKey</c> match), so the client fails before sending.
    /// <c>aggregates</c>, <c>legend</c>, <c>plannedByResource</c> and <c>actualTotalsByResource</c> are free-form for the
    /// server and stay <see cref="JToken"/> here.
    /// </summary>
    public sealed class ExcelExportRequest
    {
        public const string SchemaId = "vgs-stp-excel-export/1";
        public const int MaxDays = 31;
        public const int MaxActivities = 1000;
        public const int MaxChartImages = 32;
        public const int MaxPngBytes = 3_000_000;

        private static readonly Regex DateText = new Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);
        private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public string ProjectKey { get; set; }

        public string Title { get; set; } = "";

        public string RangeLabel { get; set; } = "";

        public bool ActualProgressMode { get; set; }

        public IList<ExcelExportDay> Days { get; } = new List<ExcelExportDay>();

        public IList<ExcelExportActivity> Activities { get; } = new List<ExcelExportActivity>();

        public JArray Aggregates { get; set; } = new JArray();

        public IList<ExcelExportChartType> ChartTypes { get; } = new List<ExcelExportChartType>();

        public IList<ExcelExportChartImage> ChartImages { get; } = new List<ExcelExportChartImage>();

        public JArray Legend { get; set; } = new JArray();

        public void Validate()
        {
            if (!WireJson.IsLowerHex(ProjectKey, 64)) throw Invalid("projectKey");
            if (Days.Count < 1 || Days.Count > MaxDays) throw Invalid("days");
            foreach (var day in Days)
            {
                if (day == null || day.Date == null || !DateText.IsMatch(day.Date)) throw Invalid("days[].date");
            }
            if (Activities.Count > MaxActivities) throw Invalid("activities");
            foreach (var activity in Activities)
            {
                if (activity == null) throw Invalid("activities[]");
                Series(activity.Planned, "activities[].planned");
                Series(activity.Actual, "activities[].actual");
            }
            foreach (var chart in ChartTypes)
            {
                if (chart == null || chart.ActualByResource == null) throw Invalid("chartTypes[]");
                Series(chart.Planned, "chartTypes[].planned");
                foreach (var series in chart.ActualByResource.Values) Series(series, "chartTypes[].actualByResource");
            }
            if (ChartImages.Count > MaxChartImages) throw Invalid("chartImages");
            foreach (var image in ChartImages)
            {
                if (image?.Png == null || image.Png.Length < 8 || image.Png.Length > MaxPngBytes || !image.Png.Take(8).SequenceEqual(PngMagic))
                    throw Invalid("chartImages[].pngDataUrl");
            }
            if (Aggregates == null || Legend == null) throw Invalid("aggregates/legend");
        }

        private void Series(IList<double?> values, string label)
        {
            if (values == null || values.Count != Days.Count) throw Invalid(label);
            if (values.Any(v => v.HasValue && (double.IsNaN(v.Value) || double.IsInfinity(v.Value)))) throw Invalid(label);
        }

        private static WireFormatException Invalid(string field) => new WireFormatException("invalid Excel export: " + field);

        public JObject ToJson()
        {
            Validate();
            return new JObject
            {
                ["schema"] = SchemaId,
                ["projectKey"] = ProjectKey,
                ["title"] = Title ?? "",
                ["rangeLabel"] = RangeLabel ?? "",
                ["actualProgressMode"] = ActualProgressMode,
                ["days"] = new JArray(Days.Select(d => new JObject { ["date"] = d.Date, ["label"] = d.Label ?? "" })),
                ["activities"] = new JArray(Activities.Select(a => new JObject
                {
                    ["id"] = a.Id ?? "",
                    ["name"] = a.Name ?? "",
                    ["type"] = a.Type ?? "",
                    ["resource"] = a.Resource ?? "",
                    ["planned"] = SeriesJson(a.Planned),
                    ["actual"] = SeriesJson(a.Actual),
                    ["color"] = a.Color ?? "",
                })),
                ["aggregates"] = Aggregates.DeepClone(),
                ["chartTypes"] = new JArray(ChartTypes.Select(c =>
                {
                    var actual = new JObject();
                    foreach (var pair in c.ActualByResource) actual[pair.Key] = SeriesJson(pair.Value);
                    return new JObject
                    {
                        ["type"] = c.Type ?? "",
                        ["planned"] = SeriesJson(c.Planned),
                        ["actualByResource"] = actual,
                        ["plannedByResource"] = c.PlannedByResource?.DeepClone() ?? JValue.CreateNull(),
                        ["actualTotalsByResource"] = c.ActualTotalsByResource?.DeepClone() ?? JValue.CreateNull(),
                    };
                })),
                ["chartImages"] = new JArray(ChartImages.Select(i => new JObject
                {
                    ["title"] = i.Title ?? "",
                    ["pngDataUrl"] = "data:image/png;base64," + Convert.ToBase64String(i.Png),
                })),
                ["legend"] = Legend.DeepClone(),
            };
        }

        private static JArray SeriesJson(IEnumerable<double?> values)
            => new JArray(values.Select(v => v.HasValue ? new JValue(v.Value) : JValue.CreateNull()));
    }

    public sealed class ExcelExportDay
    {
        public ExcelExportDay(string date, string label)
        {
            Date = date;
            Label = label;
        }

        /// <summary><c>YYYY-MM-DD</c>.</summary>
        public string Date { get; }

        /// <summary>Column header (the server keeps at most 32 characters).</summary>
        public string Label { get; }
    }

    public sealed class ExcelExportActivity
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string Type { get; set; }

        public string Resource { get; set; }

        /// <summary>One value per day; null leaves the cell blank.</summary>
        public IList<double?> Planned { get; set; } = new List<double?>();

        public IList<double?> Actual { get; set; } = new List<double?>();

        /// <summary><c>#RRGGBB</c>; anything else becomes white on the server.</summary>
        public string Color { get; set; }
    }

    public sealed class ExcelExportChartType
    {
        public string Type { get; set; }

        public IList<double?> Planned { get; set; } = new List<double?>();

        public IDictionary<string, IList<double?>> ActualByResource { get; set; } = new Dictionary<string, IList<double?>>();

        public JToken PlannedByResource { get; set; } = new JObject();

        public JToken ActualTotalsByResource { get; set; } = new JObject();
    }

    public sealed class ExcelExportChartImage
    {
        public ExcelExportChartImage(string title, byte[] png)
        {
            Title = title;
            Png = png;
        }

        public string Title { get; }

        public byte[] Png { get; }
    }
}
