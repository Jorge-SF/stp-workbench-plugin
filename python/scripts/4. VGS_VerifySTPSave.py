"""Read-only verifier for a manually saved STP V2 VGS application.

Run this only after Script 3 has applied the live movement and the user has
used VGS's normal Save command. This module intentionally never imports the
Maptek API or opens the active VGS session.
"""
from __future__ import annotations  # bundles: supplied by the header


import os
from pathlib import Path
import sys


# BEGIN STP CORE BOOTSTRAP (build_bundle.py replaces this block)
import os as _stp_os
SCRIPT_DIR = Path(__file__).resolve().parent
_STP_PYTHON_ROOT = Path(_stp_os.environ.get("STP_CORE_PATH") or SCRIPT_DIR.parent)
if str(_STP_PYTHON_ROOT) not in sys.path:
    sys.path.insert(0, str(_STP_PYTHON_ROOT))
import stp_core  # noqa: E402,F401  (exposes the flat stp_v2_* module names)
# END STP CORE BOOTSTRAP

from stp_v2_apply_transaction import (
    ApplicationResult,
    load_pending_save_receipt,
    verify_pending_save,
)


def find_current_pending_receipt(project_root: Path) -> Path:
    """Return the newest immutable receipt created by Script 3 in this folder."""
    root = Path(os.path.abspath(os.fspath(project_root)))
    report_dir = root / "Back up STP" / "reporte stp"
    candidates = [
        path for path in report_dir.glob("*.pending.json")
        if path.is_file() and not path.is_symlink()
    ] if report_dir.is_dir() else []
    if not candidates:
        raise NothingToVerify(
            "No hay ninguna aplicación pendiente de verificar en esta carpeta "
            f"({report_dir}). Primero use Aplicar a VGS desde el planificador "
            "(con el puente en vivo), guarde con el comando Guardar de VGS y "
            "después ejecute la verificación."
        )
    return max(candidates, key=lambda path: (path.stat().st_mtime_ns, path.name))


def find_current_apply_package(project_root: Path) -> Path:
    root = Path(os.path.abspath(os.fspath(project_root)))
    package = root / "Respaldos_STPUG-VGS_v2" / "stp_apply_pending_v2.json"
    if not package.is_file() or package.is_symlink():
        raise NothingToVerify(
            f"Hay un recibo pendiente, pero falta el paquete STP V2 que le corresponde ({package})."
        )
    return package


class NothingToVerify(Exception):
    """A precondition for verification is missing; the message says which, in Spanish."""


def result_user_message(result: ApplicationResult, detail: str = "") -> str:
    if result.status == "applied":
        message = "Guardado manual verificado: STP V2 coincide con el respaldo y el paquete."
    else:
        message = (
            "No se pudo verificar el guardado manual. Script 4 no modificó VGS ni "
            "restauró archivos; revise el reporte y conserve el respaldo."
        )
    if detail:
        message += "\nMotivo: " + detail
    if result.html_report_path is not None:
        message += "\nReporte: " + str(result.html_report_path)
    return message


def main(project_root: Path | None = None) -> ApplicationResult:
    """Verify the latest receipt rooted at VGS's current working directory."""
    detail = ""
    try:
        root = Path.cwd() if project_root is None else Path(project_root)
        receipt_path = find_current_pending_receipt(root)
        receipt = load_pending_save_receipt(receipt_path)
        project_file = root / receipt.project_name
        package_file = find_current_apply_package(root)
        result = verify_pending_save(
            object(), project_file=project_file, package_file=package_file,
            receipt_file=receipt_path, expected_package_sha256=receipt.package_sha256,
        )
    except NothingToVerify as error:
        result = ApplicationResult("verification_failed", None, None, None)
        detail = str(error)
    except Exception as error:
        result = ApplicationResult("verification_failed", None, None, None)
        detail = f"{type(error).__name__}: {error}"
    print(result_user_message(result, detail))
    return result


if __name__ == "__main__":
    main()
