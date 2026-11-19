"""VGS entry point for the verified STP V2 application transaction."""
from __future__ import annotations  # bundles: supplied by the header


from datetime import datetime
from hashlib import sha256
import inspect
import json
import os
from pathlib import Path
import sys
from types import FunctionType
from typing import NamedTuple

# BEGIN STP CORE BOOTSTRAP (build_bundle.py replaces this block)
import os as _stp_os
SCRIPT_DIR = Path(__file__).resolve().parent
_STP_PYTHON_ROOT = Path(_stp_os.environ.get("STP_CORE_PATH") or SCRIPT_DIR.parent)
if str(_STP_PYTHON_ROOT) not in sys.path:
    sys.path.insert(0, str(_STP_PYTHON_ROOT))
import stp_core  # noqa: E402,F401  (exposes the flat stp_v2_* module names)
# END STP CORE BOOTSTRAP

# This file is the only STP V2 application boundary allowed to import Maptek.
from maptek import gantt, vulcan_gui

from stp_v2_apply_transaction import (
    ApplicationResult,
    run_vgs_apply_pending_save,
)


def confirmation_gateway(gui_module):
    """Return only the installed documented confirmation API, with no fallback."""
    confirm_member = inspect.getattr_static(gui_module, "confirm", None)
    if type(confirm_member) is not FunctionType:
        raise RuntimeError("VGS no ofrece una confirmacion explicita compatible")

    def confirm(summary):
        return confirm_member(summary, ok="Aplicar", cancel="Cancelar") is True

    return confirm


class VerifiedApplyPackage(NamedTuple):
    path: Path
    sha256: str


def find_latest_apply_package(project_file: Path) -> VerifiedApplyPackage:
    """Return only a package with the bridge's exact matching ready receipt."""
    project = Path(os.path.abspath(os.fspath(project_file)))
    output = project.parent / "Respaldos_STPUG-VGS_v2"
    package = output / "stp_apply_pending_v2.json"
    receipt = output / "stp_apply_pending_v2.ready.json"
    if not package.is_file() or not receipt.is_file():
        raise FileNotFoundError("No existe un paquete STP V2 vigente y validado")
    try:
        package_value = json.loads(package.read_text(encoding="utf-8"))
        receipt_value = json.loads(receipt.read_text(encoding="utf-8"))
        if type(package_value) is not dict or type(receipt_value) is not dict:
            raise ValueError
        if set(receipt_value) != {
            "schema", "package_file", "package_sha256", "project_fingerprint"
        }:
            raise ValueError
        canonical = json.dumps(
            package_value, ensure_ascii=False, separators=(",", ":"),
            sort_keys=True, allow_nan=False,
        ).encode("utf-8")
        digest = sha256(canonical).hexdigest()
        if (
            receipt_value["schema"] != "vgs-stp-apply-ready/1"
            or receipt_value["package_file"] != package.name
            or receipt_value["package_sha256"] != digest
            or package_value.get("schema") != "vgs-stp-apply/1"
            or receipt_value["project_fingerprint"]
                != package_value.get("project_fingerprint")
        ):
            raise ValueError
    except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError):
        raise RuntimeError(
            "El recibo no coincide con el paquete STP V2 vigente y validado"
        ) from None
    return VerifiedApplyPackage(package, digest)


def maptek_dependencies(active_project):
    """Expose only the active session required for the live operation phase."""
    return type("MaptekTransactionDependencies", (), {
        "active_project": active_project,
        "now": staticmethod(datetime.now),
    })()


def compact_confirmation(_summary: str) -> str:
    """Return the one-line VGS prompt approved for the apply gateway."""
    return "Aplicar cambios de STP a VGS activo."


def result_user_message(result: ApplicationResult) -> str:
    """Return safe, actionable Spanish guidance for known transaction outcomes."""
    if result.status == "rejected" and result.code == "active_session_changed":
        return (
            "VGS tenía cambios pendientes distintos de la instantánea STP. "
            "no se aplicó el paquete ni se guardó nada desde Script 3. "
            "Revise o guarde sus cambios normales en VGS y ejecute "
            "2. VGS_ShortTermPlanner.py y prepare un nuevo paquete."
        )
    if (
        result.status == "rejected"
        and result.code == "completion_boundary_requires_physical_split"
    ):
        return (
            "El movimiento requiere crear un split físico en la frontera de avance. "
            "No se aplicó ni guardó nada para proteger el trabajo completado. "
            "Mantenga sin mover el primer bloque pendiente que toca esa frontera; "
            "los bloques posteriores se pueden reprogramar."
        )
    if result.status == "rejected" and result.code == "physical_vgs_split_unsupported":
        return (
            "El paquete requiere un split físico nuevo en VGS. No se aplicó ni "
            "guardó nada para proteger el proyecto. Los splits existentes sí se "
            "pueden mover como grupo; reprograme el tramo completo sin crear un "
            "split físico nuevo."
        )
    if result.status == "rejected" and result.code == "preflight_rejected":
        return (
            "El proyecto VGS ya no coincide con la instantánea STP. "
            "No se aplicó ni guardó nada. Cierre y vuelva a abrir el proyecto, "
            "ejecute 2. VGS_ShortTermPlanner.py y prepare un nuevo paquete."
        )
    messages = {
        "applied": "Aplicaci\u00f3n STP V2 completada y verificada.",
        "applied_pending_save": (
            "Movimiento aplicado en la sesión actual; VGS no fue guardado por Script 3. "
            "Revise el resultado, guarde normalmente desde VGS cuando convenga y después "
            "ejecute 4. VGS_VerifySTPSave.py para verificar el archivo guardado."
        ),
        "live_apply_failed": (
            "La aplicación en vivo no terminó. Script 3 no guardó ni restauró el proyecto. "
            "Revise la sesión actual antes de decidir si guarda o descarta sus cambios."
        ),
        "restored_requires_reopen": (
            "El archivo original fue restaurado. No guarde la sesi\u00f3n actual; "
            "abra nuevamente el proyecto original."
        ),
        "restore_failed": (
            "La restauraci\u00f3n fue bloqueada para no sobrescribir cambios externos. "
            "No guarde la sesi\u00f3n actual. Conserve el respaldo "
            "y los reportes para diagn\u00f3stico; abra nuevamente el proyecto original "
            "cuando corresponda."
        ),
    }
    message = messages.get(
        result.status,
        "Aplicacion STP V2 rechazada antes de una persistencia verificable.",
    )
    if result.html_report_path is not None:
        message += "\nReporte: " + str(result.html_report_path)
    return message


def main():
    try:
        confirm = confirmation_gateway(vulcan_gui)
        project = gantt.active_project()
        project_name = project.project_filename(True)
        if type(project_name) is not str or not project_name:
            raise RuntimeError("VGS no devolvio un proyecto activo")
        project_file = Path(os.path.abspath(project_name))
        package_identity = find_latest_apply_package(project_file)
        result = run_vgs_apply_pending_save(
            maptek_dependencies(project), project_file, package_identity.path,
            lambda summary: confirm(compact_confirmation(summary)),
            expected_package_sha256=package_identity.sha256,
        )
        vulcan_gui.message(result_user_message(result))
    except Exception:
        # Never display exception details: wrapper errors can contain local paths or tokens.
        vulcan_gui.message("Aplicacion STP V2 rechazada antes de una persistencia verificable.")


if __name__ == "__main__":
    main()
