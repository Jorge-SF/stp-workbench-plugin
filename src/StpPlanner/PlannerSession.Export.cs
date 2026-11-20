using System;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Stp.Bridge;
using Maptek.Vulcan.Stp.Bridge.Contracts;
using Maptek.Vulcan.Stp.Bridge.Json;
using Maptek.Vulcan.Stp.Planner.Export;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>The bridge's Excel exporter (<c>POST /export-xlsx</c>); a separate seam so test bridges need not implement it.</summary>
    public interface IExcelExportBridge
    {
        /// <summary>Builds the workbook with the Python exporter (stp_v2_excel_export).</summary>
        /// <param name="request">The export body.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The <c>.xlsx</c> bytes.</returns>
        Task<byte[]> ExportXlsxAsync(ExcelExportRequest request, CancellationToken cancellationToken);
    }

    /// <summary>The Exportar commands of the session.</summary>
    public sealed partial class PlannerSession
    {
        /// <summary>Gets a value indicating whether Excel can be exported now (live bridge with the exporter, not busy).</summary>
        public bool CanExportExcel => !busy && IsLive && bridge is IExcelExportBridge;

        /// <summary>Gets why Excel cannot be exported, or an empty string.</summary>
        public string ExportExcelBlockedReason => busy ? PlannerMessages.Busy
            : !(IsLive && bridge is IExcelExportBridge) ? ExportMessages.ExcelNeedsLive
            : string.Empty;

        /// <summary>Freezes the current view for an export (template <c>currentView</c>).</summary>
        /// <param name="title">Report title, or the default.</param>
        /// <returns>The export.</returns>
        public PlanExport CreateExport(string title = null) => PlanExport.From(this, title);

        /// <summary>
        /// Excel: the current view through the bridge's Python exporter (template <c>downloadExcelExport</c>).
        /// Read-only: neither the plan nor VGS change.
        /// </summary>
        /// <param name="title">Report title, or the default.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The outcome, and the workbook bytes when it succeeded.</returns>
        public async Task<(PlannerOutcome Outcome, byte[] Workbook)> ExportExcelAsync(string title = null, CancellationToken cancellationToken = default)
        {
            string blocked = ExportExcelBlockedReason;
            if (blocked.Length > 0) return (PlannerOutcome.Failure(blocked, "blocked"), null);
            var export = CreateExport(title);
            blocked = export.ExcelBlockedReason();
            if (blocked.Length > 0) return (PlannerOutcome.Failure(blocked, "blocked"), null);

            ExcelExportRequest request;
            try
            {
                request = export.ToExcelRequest();
                request.Validate();
            }
            catch (WireFormatException error)
            {
                return (PlannerOutcome.Failure(ExportMessages.ExcelFailed, "invalid", error.Message), null);
            }

            SetBusy(true);
            try
            {
                var bytes = await ((IExcelExportBridge)bridge).ExportXlsxAsync(request, cancellationToken);
                return (PlannerOutcome.Success(ExportMessages.ExcelSaved), bytes);
            }
            catch (StpBridgeException error)
            {
                return (PlannerOutcome.Failure(PlannerMessages.ExportError(error), error.Kind.ToString(), error.Message), null);
            }
            catch (OperationCanceledException)
            {
                return (PlannerOutcome.Failure("Operación cancelada.", "cancelled"), null);
            }
            finally
            {
                SetBusy(false);
            }
        }
    }
}
