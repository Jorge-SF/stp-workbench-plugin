"""Safe preparation of a VGS project copy for Short Term Planner."""

from __future__ import annotations

import csv
from dataclasses import dataclass
from datetime import datetime
import os
from pathlib import Path
import tempfile
from typing import Any, Iterable
from zipfile import BadZipFile, ZipFile


class PreparationError(RuntimeError):
    """Raised when a project cannot be prepared without ambiguity."""


class ActiveCopyVerificationError(PreparationError):
    """Raised when VGS did not change the active project to the new copy."""


@dataclass(frozen=True)
class ActivityWindow:
    activity_id: int
    start: datetime
    finish: datetime


@dataclass(frozen=True)
class ProjectInspection:
    source_path: Path
    project_start: datetime
    project_finish: datetime
    activities: tuple[ActivityWindow, ...]


@dataclass(frozen=True)
class PreparationPlan:
    source_path: Path
    clone_path: Path
    range_start: datetime
    range_finish: datetime
    keep_ids: tuple[int, ...]
    remove_ids: tuple[int, ...]


@dataclass(frozen=True)
class PreparationResult:
    plan: PreparationPlan
    kept_count: int
    removed_count: int
    final_ids: tuple[int, ...]


_VGS_DATE_FORMATS = (
    "%d/%m/%Y %H:%M:%S",
    "%d/%m/%Y %H:%M",
    "%d/%m/%Y",
    "%m/%d/%Y %I:%M:%S %p",
    "%m/%d/%Y %I:%M %p",
    "%Y-%m-%d %H:%M:%S",
    "%Y-%m-%d %H:%M",
    "%Y-%m-%d",
)

_VGS_MDY_DATE_FORMATS = (
    "%m/%d/%Y %I:%M:%S %p",
    "%m/%d/%Y %I:%M %p",
    "%m/%d/%Y %H:%M:%S",
    "%m/%d/%Y %H:%M",
    "%m/%d/%Y",
    "%d/%m/%Y %H:%M:%S",
    "%d/%m/%Y %H:%M",
    "%d/%m/%Y",
    "%Y-%m-%d %H:%M:%S",
    "%Y-%m-%d %H:%M",
    "%Y-%m-%d",
)


def _normalise(value: object) -> str:
    return "" if value is None else str(value).strip()


def parse_stp_date(value: str, label: str) -> datetime:
    try:
        return datetime.strptime(_normalise(value), "%d/%m/%Y")
    except ValueError as error:
        raise PreparationError(
            f"{label} debe usar el formato DD/MM/YYYY."
        ) from error


def _project_date_formats(source_path: Path) -> tuple[str, ...]:
    try:
        with ZipFile(source_path) as archive:
            pattern = archive.read("ManualSaved/ProjectDateTimeFormat").decode(
                "utf-8-sig"
            ).strip()
    except (BadZipFile, KeyError, OSError):
        return _VGS_DATE_FORMATS
    day_position = pattern.find("d")
    month_position = pattern.find("M")
    if month_position >= 0 and (day_position < 0 or month_position < day_position):
        return _VGS_MDY_DATE_FORMATS
    return _VGS_DATE_FORMATS


def _parse_vgs_date(value: object, formats: tuple[str, ...]) -> datetime:
    text = _normalise(value)
    for pattern in formats:
        try:
            return datetime.strptime(text, pattern)
        except ValueError:
            continue
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError as error:
        raise PreparationError(f"La fecha de VGS no se pudo interpretar: {text!r}.") from error


def _required_column(fieldnames: Iterable[str], name: str) -> str:
    names = {_normalise(column).casefold(): column for column in fieldnames}
    column = names.get(name.casefold())
    if not column:
        raise PreparationError(f"La Activity Grid no contiene la columna requerida {name}.")
    return column


def inspect_grid(source_path: Path, csv_path: Path) -> ProjectInspection:
    source_path = Path(source_path)
    date_formats = _project_date_formats(source_path)
    with csv_path.open("r", encoding="utf-8-sig", newline="") as source:
        reader = csv.DictReader(source)
        if not reader.fieldnames:
            raise PreparationError("La Activity Grid no tiene encabezados.")
        id_column = _required_column(reader.fieldnames, "Id")
        start_column = _required_column(reader.fieldnames, "Start")
        finish_column = _required_column(reader.fieldnames, "Finish")
        activities: list[ActivityWindow] = []
        seen_ids: set[int] = set()
        for row_number, row in enumerate(reader, start=2):
            raw_id = _normalise(row.get(id_column))
            try:
                activity_id = int(raw_id)
            except ValueError as error:
                raise PreparationError(
                    f"El Id de actividad en fila {row_number} debe ser entero: {raw_id!r}."
                ) from error
            if activity_id in seen_ids:
                raise PreparationError(f"El Id de actividad está duplicado: {activity_id}.")
            start = _parse_vgs_date(row.get(start_column), date_formats)
            finish = _parse_vgs_date(row.get(finish_column), date_formats)
            if finish < start:
                raise PreparationError(
                    f"La actividad {activity_id} termina antes de iniciar."
                )
            seen_ids.add(activity_id)
            activities.append(ActivityWindow(activity_id, start, finish))
    if not activities:
        raise PreparationError("La Activity Grid no contiene actividades válidas.")
    return ProjectInspection(
        source_path,
        min(activity.start for activity in activities),
        max(activity.finish for activity in activities),
        tuple(activities),
    )


def unique_stp_copy_path(source_path: Path, start: datetime) -> Path:
    source_path = Path(source_path)
    candidate = source_path.with_name(
        f"{source_path.stem}_STP_{start:%d-%m-%Y}{source_path.suffix}"
    )
    if not candidate.exists():
        return candidate
    for suffix in range(1, 10_000):
        candidate = source_path.with_name(
            f"{source_path.stem}_STP_{start:%d-%m-%Y}_{suffix}{source_path.suffix}"
        )
        if not candidate.exists():
            return candidate
    raise PreparationError("No se pudo encontrar un nombre libre para la copia STP.")


def build_preparation_plan(
    inspection: ProjectInspection, start_text: str, finish_text: str
) -> PreparationPlan:
    range_start = parse_stp_date(start_text, "Inicio")
    range_finish = parse_stp_date(finish_text, "Fin")
    if range_finish < range_start:
        raise PreparationError("La fecha Fin debe ser igual o posterior a Inicio.")
    if range_start < inspection.project_start or range_finish > inspection.project_finish:
        raise PreparationError("El intervalo debe estar dentro del período detectado del proyecto.")
    keep_ids = tuple(
        item.activity_id
        for item in inspection.activities
        if range_start <= item.start <= range_finish
    )
    if not keep_ids:
        raise PreparationError("No hay actividades que inicien dentro del intervalo indicado.")
    keep_set = set(keep_ids)
    return PreparationPlan(
        source_path=inspection.source_path,
        clone_path=unique_stp_copy_path(inspection.source_path, range_start),
        range_start=range_start,
        range_finish=range_finish,
        keep_ids=keep_ids,
        remove_ids=tuple(
            item.activity_id
            for item in inspection.activities
            if item.activity_id not in keep_set
        ),
    )


def _same_path(left: Path, right: Path) -> bool:
    return os.path.normcase(os.path.abspath(str(left))) == os.path.normcase(
        os.path.abspath(str(right))
    )


def active_project_path(project: Any) -> Path:
    candidates: list[str] = []
    for absolute in (True, False):
        try:
            candidates.append(_normalise(project.project_filename(absolute)))
        except Exception:
            continue
    for value in candidates:
        if value:
            return Path(value).resolve()
    raise PreparationError(
        "No se pudo identificar el archivo .vgantt activo. Guarde el proyecto y vuelva a intentar."
    )


def _export_grid(project: Any, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        destination.unlink()
    result = project.export_activities_to_csv(str(destination), False)
    if result is False or not destination.is_file():
        raise PreparationError("VGS no pudo exportar la Activity Grid.")


def _fallback_work_dir() -> Path:
    """Per-user folder for runs when the script folder is read-only (installed under Program Files)."""
    base = os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()
    return Path(base) / "Maptek" / "StpUG"


def _run_directory(work_dir: Path) -> Path:
    """A new ``prepare_*`` folder in ``<work_dir>/stp_prepare_runs``, or in the per-user folder
    when ``work_dir`` cannot be written to."""
    for base in (Path(work_dir), _fallback_work_dir()):
        root = base / "stp_prepare_runs"
        try:
            root.mkdir(parents=True, exist_ok=True)
            return Path(tempfile.mkdtemp(prefix="prepare_", dir=root))
        except PermissionError:
            continue
    raise PermissionError(
        f"cannot create a Prepare work folder in {Path(work_dir) / 'stp_prepare_runs'} "
        f"or {_fallback_work_dir() / 'stp_prepare_runs'}"
    )


def inspect_active_project(project: Any, work_dir: Path) -> ProjectInspection:
    source_path = active_project_path(project)
    if source_path.suffix.casefold() != ".vgantt":
        raise PreparationError(f"El proyecto activo no es un archivo .vgantt: {source_path}")
    if not source_path.is_file():
        raise PreparationError("El proyecto activo debe guardarse antes de prepararlo para STP.")
    run_dir = _run_directory(work_dir)
    grid_path = run_dir / "activity_grid_source.csv"
    _export_grid(project, grid_path)
    return inspect_grid(source_path, grid_path)


def _remove_activity_ids(project: Any, activity_ids: tuple[int, ...]) -> int:
    if not activity_ids:
        return 0
    batch_remove = getattr(project, "remove_activities", None)
    if callable(batch_remove):
        for start in range(0, len(activity_ids), 500):
            batch = list(activity_ids[start:start + 500])
            if batch_remove(batch) is False:
                raise PreparationError(
                    f"VGS no pudo eliminar el lote que inicia con la actividad {batch[0]}."
                )
        return len(activity_ids)
    remove_one = getattr(project, "remove_activity", None)
    if not callable(remove_one):
        raise PreparationError("VGS no expone un método para eliminar actividades de la copia.")
    for activity_id in activity_ids:
        if remove_one(activity_id) is False:
            raise PreparationError(f"VGS no pudo eliminar la actividad {activity_id}.")
    return len(activity_ids)


def _final_ids(project: Any, run_dir: Path) -> tuple[int, ...]:
    final_grid = run_dir / "activity_grid_final.csv"
    _export_grid(project, final_grid)
    inspection = inspect_grid(active_project_path(project), final_grid)
    return tuple(activity.activity_id for activity in inspection.activities)


def prepare_active_copy(
    project: Any,
    inspection: ProjectInspection,
    start_text: str,
    finish_text: str,
    work_dir: Path,
) -> PreparationResult:
    plan = build_preparation_plan(inspection, start_text, finish_text)
    if project.save(str(plan.clone_path)) is False:
        raise PreparationError("VGS no pudo crear la copia para STP.")
    if not _same_path(active_project_path(project), plan.clone_path):
        raise ActiveCopyVerificationError(
            "VGS no confirmó que la copia esté activa; no se eliminó ninguna actividad."
        )

    try:
        if hasattr(project, "set_pause_ui"):
            project.set_pause_ui(True)
        removed_count = _remove_activity_ids(project, plan.remove_ids)
        if hasattr(project, "update_ui"):
            project.update_ui()
    finally:
        if hasattr(project, "set_pause_ui"):
            project.set_pause_ui(False)

    if project.save(str(plan.clone_path)) is False:
        raise PreparationError("VGS no pudo guardar la copia recortada.")
    final_ids = _final_ids(project, _run_directory(work_dir))
    if tuple(sorted(final_ids)) != tuple(sorted(plan.keep_ids)):
        raise PreparationError(
            "La copia no coincide con la selección validada; no la guarde."
        )
    return PreparationResult(
        plan=plan,
        kept_count=len(plan.keep_ids),
        removed_count=removed_count,
        final_ids=tuple(sorted(final_ids)),
    )
