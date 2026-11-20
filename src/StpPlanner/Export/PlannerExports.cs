using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge.Contracts;

namespace Maptek.Vulcan.Stp.Planner.Export
{
    /// <summary>
    /// The three Exportar commands end to end: build the file from the session's current view and write it where the
    /// user chose (the plugin's SaveFileDialog). Nothing else is written: no temporary file outside the destination's
    /// folder, nothing in the install folder, and neither the plan nor VGS change.
    /// </summary>
    public static class PlannerExports
    {
        /// <summary>Default Excel file name (template <c>downloadExcelExport</c>).</summary>
        public const string ExcelFileName = "STP.xlsx";

        /// <summary>Default PDF file name.</summary>
        public const string PdfFileName = "STP.pdf";

        /// <summary>Excel through the bridge's Python exporter.</summary>
        public static async Task<PlannerOutcome> ExcelAsync(PlannerSession session, string path, string title = null, CancellationToken cancellationToken = default, IReadOnlyList<ExcelExportChartImage> chartImages = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var (outcome, workbook) = await session.ExportExcelAsync(title, cancellationToken, chartImages);
            if (!outcome.Ok) return outcome;
            return Write(path, workbook, ExportMessages.ExcelSaved, ExportMessages.ExcelFailed);
        }

        /// <summary>PDF of the plan (template print report; "Avance real" rows and boxes with the mode on).</summary>
        public static PlannerOutcome Pdf(PlannerSession session, string path, string title = null, DateTime? generatedAt = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (session.IsBusy) return PlannerOutcome.Failure(PlannerMessages.Busy, "busy");
            var export = session.CreateExport(title);
            if (export.Days.Count == 0) return PlannerOutcome.Failure(ExportMessages.NoDays, "blocked");
            if (export.Rows.Count == 0) return PlannerOutcome.Failure(ExportMessages.NoRows, "blocked");
            byte[] pdf;
            try
            {
                pdf = PdfPlanReport.Render(export, generatedAt ?? DateTime.Now);
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException)
            {
                return PlannerOutcome.Failure(ExportMessages.PdfFailed, "render", error.Message);
            }

            return Write(path, pdf, ExportMessages.PdfSaved, ExportMessages.PdfFailed);
        }

        /// <summary>STP suelto from the web resources in <paramref name="resourceFolder"/> (the runtime folder).</summary>
        public static PlannerOutcome Standalone(PlannerSession session, string resourceFolder, string path)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (session.IsBusy) return PlannerOutcome.Failure(PlannerMessages.Busy, "busy");
            StandaloneResources resources;
            try
            {
                resources = StandaloneResources.TryLoad(resourceFolder);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return PlannerOutcome.Failure(ExportMessages.StandaloneFailed + " " + error.Message, "io", error.Message);
            }

            if (resources == null) return PlannerOutcome.Failure(string.Format(ExportMessages.StandaloneNoTemplate, resourceFolder ?? string.Empty), "resources");
            string html;
            try
            {
                html = StandalonePlanner.Render(resources, session);
            }
            catch (InvalidDataException error)
            {
                return PlannerOutcome.Failure(ExportMessages.StandaloneFailed + " " + error.Message, "template", error.Message);
            }

            return Write(path, new UTF8Encoding(false).GetBytes(html), ExportMessages.StandaloneSaved, ExportMessages.StandaloneFailed);
        }

        /// <summary>Writes through a temporary sibling, so a failed write never leaves half a file at <paramref name="path"/>.</summary>
        internal static PlannerOutcome Write(string path, byte[] bytes, string savedFormat, string failed)
        {
            if (string.IsNullOrWhiteSpace(path)) return PlannerOutcome.Failure("Exportación cancelada.", "cancelled");
            string temp = null;
            try
            {
                string full = Path.GetFullPath(path);
                temp = full + ".stp-tmp";
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(full)) File.Delete(full);
                File.Move(temp, full);
                temp = null;
                return PlannerOutcome.Success(string.Format(savedFormat, full));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is NotSupportedException
                || error is ArgumentException || error is System.Security.SecurityException)
            {
                return PlannerOutcome.Failure(failed + " " + error.Message, "io", error.Message);
            }
            finally
            {
                if (temp != null)
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                    {
                        // Best effort.
                    }
                }
            }
        }
    }
}
