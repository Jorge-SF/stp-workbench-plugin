using System.Collections.Generic;
using System.Linq;
using Maptek.Vulcan.Stp.Bridge;
using Maptek.Vulcan.Stp.Engine;

namespace Maptek.Vulcan.Stp.Planner
{
    /// <summary>
    /// Spanish user messages. Texts that exist in the browser planner are copied verbatim
    /// (timeline_logic.js <c>humanPlannerOutcome</c>/<c>refreshDecision</c>, the template's notices); the others are new
    /// to the plugin and follow the same tone.
    /// </summary>
    public static class PlannerMessages
    {
        /// <summary>Template <c>LIVE_SNAPSHOT_STALE_MESSAGE</c>.</summary>
        public const string SnapshotStale = "La ventana de STP está desactualizada. Actualiza VGS a STP antes de preparar el paquete.";

        /// <summary>timeline_logic.js <c>refreshDecision</c>.</summary>
        public const string RefreshDiscardsChanges = "Actualizar descartará los movimientos locales de STP. ¿Continuar?";

        /// <summary>Template: successful refresh without inherited conflicts.</summary>
        public const string Refreshed = "STP se actualizó desde VGS. No se modificó el proyecto en VGS.";

        /// <summary>Template: failed refresh.</summary>
        public const string RefreshFailed = "No se pudo actualizar. El plan local se conservó; vuelva a ejecutar 2. VGS_ShortTermPlanner.py.";

        /// <summary>Plugin: refresh cancelled by the user.</summary>
        public const string RefreshCancelled = "Actualización cancelada. El plan local se conservó.";

        /// <summary>Template: package saved by the bridge.</summary>
        public const string PackageReady = "Paquete STP listo para ejecutar. No se modificó VGS. Ejecute 3. VGS_ApplySTPToVGS.py desde VGS para revisar y confirmar.";

        /// <summary>Template: generic persistence failure.</summary>
        public const string PackageNotPersisted = "No se pudo validar y persistir el paquete en el puente local.";

        /// <summary>Template: the plan changed while the bridge was saving.</summary>
        public const string PlanChangedDuringSave = "El plan cambió durante la persistencia.";

        /// <summary>Template: the plan changed after the package was saved.</summary>
        public const string PlanChangedAfterReady = "El plan cambió; prepare y valide de nuevo el paquete V2.";

        /// <summary>Template: apply button title without local moves.</summary>
        public const string NoLocalMoves = "Realice un movimiento local válido antes de preparar el paquete V2.";

        /// <summary>Template: apply button title with blocking inherited conflicts.</summary>
        public const string InheritedConflictsBlock = "VGS contiene conflictos de precedencia preexistentes.";

        /// <summary>Template: busy.</summary>
        public const string Busy = "Espere a que termine la operación actual.";

        /// <summary>Plugin: no live bridge.</summary>
        public const string NotLive = "El puente con VGS no está disponible; no se puede preparar el paquete. Vuelva a ejecutar 2. VGS_ShortTermPlanner.py.";

        /// <summary>Plugin: nothing to undo.</summary>
        public const string NothingToUndo = "No hay cambios para deshacer.";

        /// <summary>Plugin: nothing to redo.</summary>
        public const string NothingToRedo = "No hay cambios para rehacer.";

        /// <summary>Plugin: undo done.</summary>
        public const string Undone = "Se deshizo el último cambio.";

        /// <summary>Plugin: redo done.</summary>
        public const string Redone = "Se rehízo el cambio.";

        /// <summary>Plugin: equipment assigned to a day (template's notice style: local only).</summary>
        public const string ResourceAssigned = "Equipo asignado sólo en la vista local. VGS no fue modificado.";

        /// <summary>Plugin: day back to the activity's equipment.</summary>
        public const string ResourceCleared = "El día vuelve al equipo de la actividad. VGS no fue modificado.";

        /// <summary>Plugin: day exempted from the capacity limit.</summary>
        public const string ExemptionSet = "Día exento del límite de capacidad en la vista local. VGS no fue modificado.";

        /// <summary>Plugin: exemption removed.</summary>
        public const string ExemptionCleared = "Se quitó la exención del límite de capacidad.";

        /// <summary>Plugin: unknown or completed day for "Asignar equipo" / "Exentar Límite".</summary>
        public const string DayNotEditable = "Ese día no tiene trabajo pendiente que se pueda ajustar.";

        /// <summary>Plugin: the equipment is not one of the project's.</summary>
        public const string UnknownResource = "El equipo no existe en el proyecto.";

        /// <summary>Plugin: activities without a VGS type cannot be exempted (template <c>capacityExemptible</c>).</summary>
        public const string NotExemptible = "Sólo las actividades con tipo de VGS se pueden exentar del límite de capacidad.";

        /// <summary>Plugin: equipment assigned to a whole activity.</summary>
        public const string ActivityResourceAssigned = "Equipo de la actividad asignado sólo en la vista local. VGS no fue modificado.";

        /// <summary>Plugin: activity back to its VGS resource.</summary>
        public const string ActivityResourceCleared = "La actividad vuelve a su recurso de VGS. VGS no fue modificado.";

        /// <summary>Plugin: manual resource added ("+ Recurso manual").</summary>
        public const string ManualResourceAdded = "Recurso manual agregado sólo en la vista local. VGS no fue modificado.";

        /// <summary>Plugin: manual resource renamed.</summary>
        public const string ManualResourceRenamed = "Recurso manual renombrado.";

        /// <summary>Plugin: manual resource removed (its assignments go back to the activity's equipment).</summary>
        public const string ManualResourceRemoved = "Recurso manual quitado; sus días vuelven al equipo de la actividad.";

        /// <summary>Template <c>renameManualResource</c>: a blank or repeated name.</summary>
        public const string ManualResourceNameInvalid = "El nombre del recurso manual debe ser único y no estar vacío.";

        /// <summary>Plugin: unknown manual resource id.</summary>
        public const string UnknownManualResource = "El recurso manual no existe.";

        /// <summary>Plugin: unknown activity.</summary>
        public const string UnknownActivity = "La actividad no existe en el proyecto.";

        /// <summary>Plugin: the local setting could not be saved.</summary>
        public const string OverridesNotSaved = "No se pudo guardar el ajuste local: ";

        private static readonly IReadOnlyDictionary<string, string> OutcomeMessages = new Dictionary<string, string>
        {
            [OperationResult.Codes.HorizonRange] = "El movimiento saldría del horizonte visible. Extienda el rango o reduzca el desplazamiento.",
            [OperationResult.Codes.CivilRange] = "El movimiento excede el rango civil compatible (años 0001 a 9999).",
            [OperationResult.Codes.CompletedWork] = "El avance completado está protegido y no se puede mover.",
            [OperationResult.Codes.IncomingPrecedence] = "El movimiento viola una precedencia entrante. Revise las actividades predecesoras.",
            [OperationResult.Codes.NativeAnchor] = "El movimiento cruzaría una división nativa protegida.",
            [OperationResult.Codes.InvalidMove] = "El movimiento no es válido. Revise el horizonte y la actividad seleccionada.",
        };

        private static readonly IReadOnlyDictionary<string, string> UnsafeReasons = new Dictionary<string, string>
        {
            ["anclaje_nativo"] = "anclaje nativo no preservado",
            ["unidad_logica"] = "unidad lógica inconsistente",
            ["solapamiento"] = "fragmentos superpuestos",
            ["precedencia"] = "precedencia agravada",
            ["fragmento_completado"] = "se movió un fragmento completado",
            ["movimiento_civil"] = "movimiento civil inválido",
            ["integridad"] = "integridad del paquete inválida",
        };

        /// <summary>timeline_logic.js <c>humanPlannerOutcome</c>.</summary>
        /// <param name="result">The engine result.</param>
        /// <returns>The message.</returns>
        public static string Human(OperationResult result)
        {
            if (result == null) return "No se pudo completar la operación solicitada.";
            if (result.Ok && !string.IsNullOrEmpty(result.Message)) return result.Message;
            if (!string.IsNullOrEmpty(result.Code) && OutcomeMessages.TryGetValue(result.Code, out string message)) return message;
            return !string.IsNullOrEmpty(result.Message) ? result.Message : "No se pudo completar la operación solicitada.";
        }

        /// <summary>Template <c>precedenceConflictNotice</c>.</summary>
        /// <param name="assessment">The assessment.</param>
        /// <returns>The notice, or empty.</returns>
        public static string ConflictNotice(PrecedenceConflictAssessment assessment)
        {
            int count = assessment?.Inherited?.Count ?? 0;
            if (count == 0) return string.Empty;
            string label = count == 1 ? "conflicto" : "conflictos";
            return assessment.BlocksOperations
                ? $"El proyecto contiene {count} {label} de precedencias ya existentes en VGS. V2 los muestra sin modificar VGS; los movimientos locales no podrán agravarlos."
                : $"El proyecto contiene {count} {label} de precedencias heredados de VGS. Se permiten cambios y paquete V2; STP no permitirá crear ni agravar conflictos.";
        }

        /// <summary>Status bar text of the precedence policy.</summary>
        /// <param name="policy">The effective policy.</param>
        /// <returns>The text.</returns>
        public static string PolicyLabel(string policy) => policy == PrecedenceConflictPolicy.AllowInherited
            ? "Política de precedencias: permitir heredadas"
            : "Política de precedencias: bloquear heredadas";

        /// <summary>Status bar text of the pending changes.</summary>
        /// <param name="count">Activities changed.</param>
        /// <returns>The text.</returns>
        public static string PendingChanges(int count) => count == 0
            ? "Sin cambios pendientes"
            : count == 1 ? "1 cambio sin aplicar a VGS" : $"{count} cambios sin aplicar a VGS";

        /// <summary>Message for a bridge error while saving the apply package.</summary>
        /// <param name="error">The error.</param>
        /// <param name="nameOf">Activity id to "id name" label, for unsafe diagnostics.</param>
        /// <returns>The message.</returns>
        public static string ApplyError(StpBridgeException error, System.Func<string, string> nameOf)
        {
            switch (error.Kind)
            {
                case StpBridgeErrorKind.Stale:
                    return SnapshotStale;
                case StpBridgeErrorKind.InvalidRequest:
                    return "El paquete no tiene un formato válido (422). Revisa los movimientos y vuelve a generar el paquete.";
                case StpBridgeErrorKind.ApplyUnsafe:
                    return UnsafeMessage(error, nameOf);
                case StpBridgeErrorKind.SaveIndeterminate:
                    return string.IsNullOrEmpty(error.RecoveryPath)
                        ? "No se pudo confirmar si el paquete se guardó. Revise la carpeta de respaldos antes de continuar."
                        : $"No se pudo confirmar si el paquete se guardó. Revise {error.RecoveryPath} antes de continuar.";
                default:
                    return Common(error) ?? PackageNotPersisted;
            }
        }

        /// <summary>Message for a bridge error while refreshing from VGS.</summary>
        /// <param name="error">The error.</param>
        /// <returns>The message.</returns>
        public static string RefreshError(StpBridgeException error)
        {
            switch (error.Kind)
            {
                case StpBridgeErrorKind.SnapshotUnavailable:
                case StpBridgeErrorKind.Stale:
                    return "VGS no pudo generar una instantánea completa. Guarde el proyecto en VGS y vuelva a actualizar. El plan local se conservó.";
                default:
                    return Common(error) is string common ? common + " El plan local se conservó." : RefreshFailed;
            }
        }

        /// <summary>Message for a bridge error while exporting Excel (<c>POST /export-xlsx</c>).</summary>
        /// <param name="error">The error.</param>
        /// <returns>The message.</returns>
        public static string ExportError(StpBridgeException error)
        {
            const string failed = "No se pudo exportar el archivo Excel.";
            switch (error.Kind)
            {
                case StpBridgeErrorKind.InvalidRequest:
                    return failed + " El puente rechazó los datos de la exportación (422).";
                case StpBridgeErrorKind.ServerError:
                    return failed + " El puente local no pudo generar el libro (¿falta xlsxwriter en el Python de VGS?).";
                case StpBridgeErrorKind.ProtocolViolation:
                    return failed + " La respuesta del puente no es un libro .xlsx.";
                default:
                    string common = Common(error);
                    return common == null ? failed : failed + " " + common;
            }
        }

        private static string Common(StpBridgeException error)
        {
            switch (error.Kind)
            {
                case StpBridgeErrorKind.Unreachable:
                    return "No se pudo conectar con el puente local. Vuelva a ejecutar 2. VGS_ShortTermPlanner.py.";
                case StpBridgeErrorKind.Timeout:
                    return "El puente local no respondió a tiempo. Intente de nuevo.";
                case StpBridgeErrorKind.Forbidden:
                    return "El puente local rechazó la autorización (403). Vuelva a abrir el planificador desde VGS.";
                case StpBridgeErrorKind.PayloadTooLarge:
                    return "La solicitud supera el tamaño máximo del puente local (20 MB).";
                case StpBridgeErrorKind.Busy:
                    return "El puente local está ocupado. Intente de nuevo en unos segundos.";
                case StpBridgeErrorKind.ServerError:
                    return "El puente local no pudo completar la operación (error del servidor).";
                case StpBridgeErrorKind.ProtocolViolation:
                    return "El puente no confirmó el paquete exacto.";
                default:
                    return null;
            }
        }

        // Template safeBridgePackageError, 'apply package unsafe' branch.
        private static string UnsafeMessage(StpBridgeException error, System.Func<string, string> nameOf)
        {
            const string Generic = "El paquete fue rechazado por seguridad (422). Revisa los movimientos y vuelve a generar el paquete.";
            var diagnostic = error.Diagnostic;
            if (diagnostic == null || !UnsafeReasons.TryGetValue(diagnostic.Reason ?? string.Empty, out string reason)) return Generic;
            var activities = diagnostic.Activities ?? new string[0];
            if (activities.Count > 5 || activities.Any(string.IsNullOrWhiteSpace) || activities.Distinct().Count() != activities.Count) return Generic;
            var labels = activities.Select(id => nameOf?.Invoke(id) ?? id).ToList();
            string activityMessage = labels.Count == 0 ? string.Empty : $" Actividad{(labels.Count == 1 ? string.Empty : "es")}: {string.Join(", ", labels)}.";
            return $"El paquete fue rechazado por seguridad (422): {reason}.{activityMessage}";
        }
    }
}
