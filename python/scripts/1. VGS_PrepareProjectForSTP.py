"""Prepare the active VGS project as a safe, reduced STP copy."""
from __future__ import annotations  # bundles: supplied by the header


from pathlib import Path
import sys
from typing import Any, Mapping

# BEGIN STP CORE BOOTSTRAP (build_bundle.py replaces this block)
import os as _stp_os
SCRIPT_DIR = Path(__file__).resolve().parent
_STP_PYTHON_ROOT = Path(_stp_os.environ.get("STP_CORE_PATH") or SCRIPT_DIR.parent)
if str(_STP_PYTHON_ROOT) not in sys.path:
    sys.path.insert(0, str(_STP_PYTHON_ROOT))
import stp_core  # noqa: E402,F401  (exposes the flat stp_v2_* module names)
# END STP CORE BOOTSTRAP

from stp_v2_prepare_project import (
    PreparationError,
    PreparationResult,
    inspect_active_project,
    prepare_active_copy,
)


def run(
    project: Any, values: Mapping[str, str], work_dir: Path
) -> PreparationResult:
    inspection = inspect_active_project(project, work_dir)
    return prepare_active_copy(
        project,
        inspection,
        str(values.get("start_date", "")),
        str(values.get("finish_date", "")),
        work_dir,
    )


def _show_panel(vulcan_gui: Any, inspection: Any) -> Mapping[str, str] | None:
    panel = vulcan_gui.panel("Preparar proyecto VGS para STP")
    panel.statictext(
        f"Inicio detectado del proyecto: {inspection.project_start:%d/%m/%Y}",
        bold=True,
    )
    panel.statictext(
        f"Fin detectado del proyecto: {inspection.project_finish:%d/%m/%Y}",
        bold=True,
    )
    panel.statictext(
        "Se abrirá la copia STP en esta misma sesión; el proyecto original no se recortará."
    )
    panel.begin_block(border=True, title="Intervalo que se conservará")
    panel.editbox(
        "start_date",
        "Conservar desde (DD/MM/YYYY):",
        inspection.project_start.strftime("%d/%m/%Y"),
        width=18,
    )
    panel.editbox(
        "finish_date",
        "Conservar hasta (DD/MM/YYYY):",
        inspection.project_finish.strftime("%d/%m/%Y"),
        width=18,
    )
    panel.statictext(
        "Se conservan actividades que inician dentro del intervalo, aunque terminen después."
    )
    panel.end_block()
    if not panel.display():
        return None
    return panel.get_values()


def main() -> None:
    from maptek import gantt, vulcan_gui

    project = gantt.Gantt()
    try:
        project.active_project()
        inspection = inspect_active_project(project, SCRIPT_DIR)
        values = _show_panel(vulcan_gui, inspection)
        if not values:
            return
        result = prepare_active_copy(
            project,
            inspection,
            str(values.get("start_date", "")),
            str(values.get("finish_date", "")),
            SCRIPT_DIR,
        )
        vulcan_gui.message(
            "Copia STP creada y activa.\n\n"
            f"Archivo: {result.plan.clone_path}\n"
            f"Periodo conservado: {result.plan.range_start:%d/%m/%Y} — "
            f"{result.plan.range_finish:%d/%m/%Y}\n"
            f"Actividades conservadas: {result.kept_count}\n"
            f"Actividades eliminadas de la copia: {result.removed_count}\n\n"
            "Revise el Gantt reducido antes de continuar con STP."
        )
    except PreparationError as error:
        vulcan_gui.error(str(error))
    except Exception as error:
        vulcan_gui.error(f"No se pudo preparar el proyecto para STP: {error}")


if __name__ == "__main__":
    main()
