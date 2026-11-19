"""Fail-closed backup/apply/verify/restore transaction for STP V2.

This module is deliberately independent of Maptek imports.  The VGS entry
script supplies the active project and the one explicit persistence boundary;
tests can therefore exercise every destructive boundary with only temporary
archives and small fakes.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from datetime import datetime
from hashlib import sha256
from html import escape
import inspect
import json
import os
from pathlib import Path
import shutil
import stat
import threading
from types import FunctionType, MethodType
from typing import Any, Callable, Mapping

from stp_v2_apply_contract import load_apply_package, validate_apply_package
from stp_v2_core import (
    ProjectState,
    precedence_fingerprint,
    project_fingerprint,
    read_vgantt,
)
from stp_v2_maptek_apply_adapter import (
    ApplyError,
    CapabilityError,
    NativeOperationCost,
    PreflightError,
    apply_maptek_operations,
    bind_maptek_apply_session,
    build_maptek_operation_plan,
    summarize_native_operation_cost,
    verify_maptek_result,
)
from stp_v2_windows_atomic import atomic_replace_bytes_windows
import stp_v2_windows_atomic as _windows_atomic


_REPARSE_ATTRIBUTE = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
_SENSITIVE_WORDS = ("token", "secret", "password", "credential", "authorization")
_DIRECT_RECOVERY_INSTRUCTION = (
    "No guarde la sesi\u00f3n actual. Conserve el respaldo y los reportes para "
    "diagn\u00f3stico; abra nuevamente el proyecto original cuando corresponda."
)
_TRANSACTION_LOCKS_GUARD = threading.Lock()
_TRANSACTION_LOCKS: dict[str, threading.RLock] = {}
_MAX_NATIVE_OPERATIONS = 80


class ApplicationTransactionError(RuntimeError):
    """A sanitized transaction failure safe to display and serialize."""


class ApplicationRejected(ApplicationTransactionError):
    def __init__(self, code: str, message: str, differences: tuple[object, ...] = ()) -> None:
        super().__init__(message)
        self.code = code
        self.differences = differences


class ApplicationVerificationError(ApplicationTransactionError):
    def __init__(self, message: str, differences: tuple[object, ...] = ()) -> None:
        super().__init__(message)
        self.differences = differences


class ActiveProjectPathError(ApplicationTransactionError):
    """The VGS active-project path cannot be proven equal to the requested one."""

    def __init__(self, expected_path: Path, observed_path: object) -> None:
        self.expected_path = str(expected_path)
        self.observed_path = observed_path if type(observed_path) is str else ""
        expected = _active_path_evidence(self.expected_path)
        observed = _active_path_evidence(self.observed_path)
        self.evidence = {
            "expected_parent": expected["parent"],
            "expected_stem": expected["stem"],
            "expected_prefix": expected["prefix"],
            "observed_parent": observed["parent"],
            "observed_stem": observed["stem"],
            "observed_prefix": observed["prefix"],
            "observed_is_absolute": observed["is_absolute"],
        }
        super().__init__("active project path differs from expected path")


@dataclass(frozen=True)
class VerifiedBackup:
    path: Path
    sha256: str


@dataclass(frozen=True)
class PendingSaveReceipt:
    """Immutable proof for the manual-save verification phase.

    The receipt contains only archive identity and preflight facts.  It never
    serializes a live VGS object or an executable Maptek operation plan.
    """

    project_name: str
    package_sha256: str
    backup_path: Path
    backup_sha256: str
    project_fingerprint: str
    precedence_fingerprint: str
    authorized_ids: frozenset[str]
    native_operation_cost: NativeOperationCost


@dataclass(frozen=True)
class PreparedApplication:
    project_file: Path
    package_file: Path
    backup_path: Path
    html_report_path: Path
    json_report_path: Path
    original_digest: str
    original_state: object
    package: object
    plan: object
    native_operation_cost: NativeOperationCost
    authorized_ids: frozenset[str]
    project_fingerprint: str
    precedence_fingerprint: str
    summary: str


@dataclass(frozen=True)
class ApplicationResult:
    status: str
    backup_path: Path | None
    html_report_path: Path | None
    json_report_path: Path | None
    verified_project_digest: str | None = None
    restored: bool = False
    code: str = ""


_PENDING_SAVE_RECEIPT_SCHEMA = "vgs-stp-pending-save/1"


def write_pending_save_receipt(
    json_report_path: Path,
    receipt: PendingSaveReceipt,
    *,
    atomic_replace: Callable[..., object] = atomic_replace_bytes_windows,
) -> Path:
    """Write one immutable receipt beside its transaction JSON report."""
    value = _pending_save_receipt_value(receipt)
    report = Path(os.path.abspath(os.fspath(json_report_path)))
    if report.suffix.casefold() != ".json" or report.parent.name != "reporte stp":
        raise ValueError("pending receipt report path is invalid")
    project_root = report.parent.parent.parent
    report_dir = _safe_directory(project_root, report.parent, create=False)
    receipt_path = report.with_name(report.stem + ".pending.json")
    _require_literal_descendant(project_root, receipt_path)
    if receipt_path.exists() or receipt_path.is_symlink():
        raise FileExistsError("pending receipt already exists")
    payload = (
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        + "\n"
    ).encode("utf-8")
    atomic_replace(receipt_path, payload, expected_parent=report_dir)
    if _read_regular_bytes(receipt_path) != payload:
        raise OSError("pending receipt verification failed")
    return receipt_path


def load_pending_save_receipt(path: Path) -> PendingSaveReceipt:
    """Load and strictly validate a receipt before any archive is read."""
    receipt_path = Path(os.path.abspath(os.fspath(path)))
    try:
        value = json.loads(_read_regular_bytes(receipt_path))
    except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError) as error:
        raise ValueError("pending receipt is unavailable or invalid") from error
    return _pending_save_receipt_from_value(value)


def _pending_save_receipt_value(receipt: PendingSaveReceipt) -> dict[str, object]:
    if type(receipt) is not PendingSaveReceipt:
        raise TypeError("pending receipt is invalid")
    project_name = _receipt_project_name(receipt.project_name)
    package_digest = _receipt_digest(receipt.package_sha256)
    backup_path = _receipt_backup_path(receipt.backup_path)
    backup_digest = _receipt_digest(receipt.backup_sha256)
    project_digest = _receipt_fingerprint(receipt.project_fingerprint)
    precedence_digest = _receipt_fingerprint(receipt.precedence_fingerprint)
    authorized_ids = _receipt_authorized_ids(receipt.authorized_ids)
    cost = _coerce_native_operation_cost(receipt.native_operation_cost)
    return {
        "schema": _PENDING_SAVE_RECEIPT_SCHEMA,
        "project_name": project_name,
        "package_sha256": package_digest,
        "backup_path": str(backup_path),
        "backup_sha256": backup_digest,
        "project_fingerprint": project_digest,
        "precedence_fingerprint": precedence_digest,
        "authorized_ids": sorted(authorized_ids),
        "native_operation_cost": {
            "split_count": cost.split_count,
            "move_count": cost.move_count,
            "total": cost.total,
            "by_activity": dict(sorted(cost.by_activity.items())),
        },
    }


def _pending_save_receipt_from_value(value: object) -> PendingSaveReceipt:
    if type(value) is not dict or set(value) != {
        "schema", "project_name", "package_sha256", "backup_path", "backup_sha256",
        "project_fingerprint", "precedence_fingerprint", "authorized_ids",
        "native_operation_cost",
    } or value.get("schema") != _PENDING_SAVE_RECEIPT_SCHEMA:
        raise ValueError("pending receipt schema is invalid")
    cost_value = value["native_operation_cost"]
    if type(cost_value) is not dict or set(cost_value) != {
        "split_count", "move_count", "total", "by_activity",
    }:
        raise ValueError("pending receipt operation cost is invalid")
    try:
        cost = NativeOperationCost(
            cost_value["split_count"], cost_value["move_count"], cost_value["total"],
            cost_value["by_activity"],
        )
    except (TypeError, ValueError) as error:
        raise ValueError("pending receipt operation cost is invalid") from error
    receipt = PendingSaveReceipt(
        _receipt_project_name(value["project_name"]),
        _receipt_digest(value["package_sha256"]),
        _receipt_backup_path(value["backup_path"]),
        _receipt_digest(value["backup_sha256"]),
        _receipt_fingerprint(value["project_fingerprint"]),
        _receipt_fingerprint(value["precedence_fingerprint"]),
        _receipt_authorized_ids(value["authorized_ids"]),
        cost,
    )
    # Round-trip validation rejects ambiguous types and non-canonical values.
    _pending_save_receipt_value(receipt)
    return receipt


def _receipt_digest(value: object) -> str:
    if (
        type(value) is not str or len(value) != 64
        or any(character not in "0123456789abcdef" for character in value)
    ):
        raise ValueError("pending receipt digest is invalid")
    return value


def _receipt_project_name(value: object) -> str:
    if (
        type(value) is not str or not value or Path(value).name != value
        or Path(value).suffix.casefold() != ".vgantt"
    ):
        raise ValueError("pending receipt project name is invalid")
    return value


def _receipt_backup_path(value: object) -> Path:
    if type(value) is not str and not isinstance(value, Path):
        raise ValueError("pending receipt backup path is invalid")
    path = Path(os.path.abspath(os.fspath(value)))
    if path.suffix.casefold() != ".vgantt":
        raise ValueError("pending receipt backup path is invalid")
    return path


def _receipt_fingerprint(value: object) -> str:
    if type(value) is not str or not value or len(value) > 256:
        raise ValueError("pending receipt fingerprint is invalid")
    return value


def _receipt_authorized_ids(value: object) -> frozenset[str]:
    if type(value) not in (frozenset, list, tuple):
        raise ValueError("pending receipt authorized ids are invalid")
    items = tuple(value)
    if not items or any(type(item) is not str or not item for item in items):
        raise ValueError("pending receipt authorized ids are invalid")
    result = frozenset(items)
    if len(result) != len(items):
        raise ValueError("pending receipt authorized ids are invalid")
    return result


def prepare_application(
    dependencies: object,
    project_file: Path,
    package_file: Path,
    *,
    timestamp: datetime | None = None,
    expected_package_sha256: str | None = None,
) -> PreparedApplication:
    """Read and validate all untrusted inputs and bind the plan to the original."""
    project = _validated_project_file(project_file)
    project_dir = project.parent
    package_path = _validated_existing_child(project_dir, package_file, "package")
    instant = timestamp if timestamp is not None else _dependency(
        dependencies, "now", datetime.now
    )()
    if type(instant) is not datetime or instant.tzinfo is not None:
        raise ApplicationRejected("clock_invalid", "transaction clock is invalid")

    backup_dir = project_dir / "Back up STP"
    report_dir = backup_dir / "reporte stp"
    _require_literal_descendant(project_dir, backup_dir)
    _require_literal_descendant(project_dir, report_dir)
    _assert_no_reparse(backup_dir, require_exists=False)
    _assert_no_reparse(report_dir, require_exists=False)
    if backup_dir.exists() and not backup_dir.is_dir():
        raise ApplicationRejected("transaction_path_invalid", "backup path is not a directory")
    if report_dir.exists() and not report_dir.is_dir():
        raise ApplicationRejected("transaction_path_invalid", "report path is not a directory")
    stamp = instant.strftime("%Y%m%d_%H%M%S")
    backup_path = backup_dir / f"{project.stem}_STP_V2_{stamp}.vgantt"
    html_report_path = report_dir / f"Aplicacion_STP_V2_{stamp}.html"
    json_report_path = report_dir / f"Aplicacion_STP_V2_{stamp}.json"
    for candidate in (backup_path, html_report_path, json_report_path):
        _require_literal_descendant(project_dir, candidate)
        if candidate.exists() or candidate.is_symlink():
            raise ApplicationRejected(
                "transaction_path_exists", "a transaction output path already exists"
            )

    original_payload = _read_regular_bytes(project)
    original_digest = sha256(original_payload).hexdigest()
    if expected_package_sha256 is not None and (
        type(expected_package_sha256) is not str
        or len(expected_package_sha256) != 64
        or any(character not in "0123456789abcdef" for character in expected_package_sha256)
    ):
        raise ApplicationRejected(
            "package_identity_invalid", "approved package identity is invalid"
        )
    try:
        package_payload = _read_regular_bytes(package_path)
    except (OSError, ValueError) as error:
        raise ApplicationRejected(
            "package_identity_mismatch", "approved package bytes are unavailable"
        ) from error
    if (
        expected_package_sha256 is not None
        and sha256(package_payload).hexdigest() != expected_package_sha256
    ):
        raise ApplicationRejected(
            "package_identity_mismatch", "approved package bytes changed"
        )
    read_archive = _dependency(dependencies, "read_vgantt", read_vgantt)
    load_package = _dependency(dependencies, "load_apply_package", load_apply_package)
    validate_package = _dependency(
        dependencies, "validate_apply_package", validate_apply_package
    )
    build_plan = _dependency(
        dependencies, "build_maptek_operation_plan", build_maptek_operation_plan
    )
    summarize_operation_cost = _dependency(
        dependencies, "summarize_native_operation_cost", summarize_native_operation_cost
    )
    project_hash = _dependency(
        dependencies, "project_fingerprint", project_fingerprint
    )
    precedence_hash = _dependency(
        dependencies, "precedence_fingerprint", precedence_fingerprint
    )
    try:
        original_state = read_archive(project)
        raw_package = json.loads(package_payload)
        package = load_package(raw_package)
        validation = validate_package(package, original_state)
    except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError) as error:
        raise ApplicationRejected(
            "preflight_invalid", "project or application package could not be validated"
        ) from error
    if getattr(validation, "ok", None) is not True:
        differences = tuple(getattr(validation, "differences", ()))
        if any(
            getattr(difference, "code", "")
            == "completion_boundary_requires_physical_split"
            for difference in differences
        ):
            raise ApplicationRejected(
                "completion_boundary_requires_physical_split",
                "application would create a physical VGS split at the visual completion boundary",
                differences,
            )
        raise ApplicationRejected(
            "preflight_rejected",
            "application package is stale or unsafe",
            differences,
        )

    try:
        plan = build_plan(package, original_state)
        native_operation_cost = _coerce_native_operation_cost(
            summarize_operation_cost(plan)
        )
        project_digest = project_hash(original_state)
        precedence_digest = precedence_hash(original_state)
    except PreflightError as error:
        if str(error).startswith("physical VGS split"):
            raise ApplicationRejected(
                "physical_vgs_split_unsupported",
                "application requires an unsupported physical VGS split",
            ) from error
        raise ApplicationRejected(
            "plan_rejected", "application operation plan could not be proven safe"
        ) from error
    except (TypeError, ValueError, RuntimeError, OSError) as error:
        raise ApplicationRejected(
            "plan_rejected", "application operation plan could not be proven safe"
        ) from error
    authorized_ids = frozenset(
        str(activity.activity_id)
        for activity in getattr(package, "activities", ())
    )
    summary = _confirmation_summary(
        project, package, authorized_ids, backup_path,
        html_report_path, json_report_path,
    )
    return PreparedApplication(
        project,
        package_path,
        backup_path,
        html_report_path,
        json_report_path,
        original_digest,
        original_state,
        package,
        plan,
        native_operation_cost,
        authorized_ids,
        str(project_digest),
        str(precedence_digest),
        summary,
    )


def create_verified_backup(project_file: Path, backup_path: Path) -> VerifiedBackup:
    """Create one exclusive backup below ``Back up STP`` and prove its digest."""
    project = _validated_project_file(project_file)
    project_dir = project.parent
    expected_backup_dir = project_dir / "Back up STP"
    backup_dir = _safe_directory(project_dir, expected_backup_dir, create=True)
    backup = Path(os.path.abspath(os.fspath(backup_path)))
    if backup.parent != backup_dir or backup.suffix.casefold() != ".vgantt":
        raise ValueError("backup must be an exact .vgantt child of Back up STP")
    _require_literal_descendant(project_dir, backup)
    if backup.exists() or backup.is_symlink():
        raise FileExistsError("backup destination already exists")

    payload = _read_regular_bytes(project)
    expected_digest = sha256(payload).hexdigest()
    try:
        with backup.open("xb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        shutil.copystat(project, backup, follow_symlinks=False)
        _assert_no_reparse(backup, require_exists=True)
        actual = _read_regular_bytes(backup)
        if sha256(actual).hexdigest() != expected_digest or actual != payload:
            raise OSError("backup verification failed")
    except BaseException:
        _unlink_exact_file(backup)
        raise
    return VerifiedBackup(backup, expected_digest)


def _coerce_native_operation_cost(value: object) -> NativeOperationCost:
    """Validate dependency output so the budget cannot be bypassed by a fake."""
    if type(value) is not NativeOperationCost:
        raise ValueError("native operation cost is invalid")
    # ``NativeOperationCost`` freezes and validates its fields on construction;
    # reconstructing it makes that invariant explicit at this transaction edge.
    return NativeOperationCost(
        value.split_count, value.move_count, value.total, value.by_activity,
    )


def _operation_budget_exceeded(cost: NativeOperationCost) -> bool:
    return cost.total > _MAX_NATIVE_OPERATIONS


def _write_budget_rejection_report(
    writer: Callable[..., object],
    prepared: PreparedApplication,
    atomic_replace: Callable[..., object],
) -> tuple[Path | None, Path | None]:
    """Persist diagnostics only; this deliberately never creates a backup."""
    try:
        backup_dir = _safe_directory(
            prepared.project_file.parent, prepared.backup_path.parent, create=True
        )
        _safe_directory(backup_dir, prepared.html_report_path.parent, create=True)
        return _write_report(
            writer,
            prepared,
            _report_value(
                "rejected", "operation_budget_exceeded", prepared, None,
            ),
            atomic_replace,
        )
    except BaseException:
        return None, None


def _confirmation_summary(
    project: Path,
    package: object,
    authorized_ids: frozenset[str],
    backup_path: Path,
    html_report_path: Path,
    json_report_path: Path,
) -> str:
    """Build the complete, bounded and display-safe consent boundary."""
    horizon_start = getattr(package, "horizon_start", None)
    horizon_finish = getattr(package, "horizon_finish", None)
    activities = tuple(getattr(package, "activities", ()))
    movement_units: dict[tuple[str, str], str] = {}
    hidden_units: set[tuple[str, str]] = set()
    rows: list[str] = []
    for activity in activities:
        activity_id = str(getattr(activity, "activity_id", ""))
        fragments = tuple(getattr(activity, "fragments", ()))
        if not fragments:
            continue
        for fragment in fragments:
            provenance = str(getattr(fragment, "provenance", ""))
            original_start = getattr(fragment, "original_start", None)
            original_finish = getattr(fragment, "original_finish", None)
            start = getattr(fragment, "start", None)
            finish = getattr(fragment, "finish", None)
            moved = (
                type(original_start) is datetime
                and type(original_finish) is datetime
                and type(start) is datetime
                and type(finish) is datetime
                and (start != original_start or finish != original_finish)
            )
            unit_key = (
                activity_id,
                str(getattr(fragment, "logical_unit_id", getattr(fragment, "id", ""))),
            )
            if moved and provenance in ("direct", "cascade"):
                previous = movement_units.setdefault(unit_key, provenance)
                if previous != provenance:
                    raise ApplicationRejected(
                        "confirmation_summary_invalid",
                        "logical unit provenance is inconsistent",
                    )
            if (
                moved
                and provenance in ("direct", "cascade")
                and type(start) is datetime
                and type(finish) is datetime
                and type(horizon_start) is datetime
                and type(horizon_finish) is datetime
                and (start < horizon_start or finish > horizon_finish)
            ):
                hidden_units.add(unit_key)
        original_starts = [
            getattr(fragment, "original_start", None) for fragment in fragments
            if type(getattr(fragment, "original_start", None)) is datetime
        ]
        original_finishes = [
            getattr(fragment, "original_finish", None) for fragment in fragments
            if type(getattr(fragment, "original_finish", None)) is datetime
        ]
        current_starts = [
            getattr(fragment, "start", None) for fragment in fragments
            if type(getattr(fragment, "start", None)) is datetime
        ]
        current_finishes = [
            getattr(fragment, "finish", None) for fragment in fragments
            if type(getattr(fragment, "finish", None)) is datetime
        ]
        if not all((original_starts, original_finishes, current_starts, current_finishes)):
            raise ApplicationRejected(
                "confirmation_summary_invalid", "activity ranges are incomplete"
            )
        raw_name = getattr(activity, "activity_name", None)
        name = "" if raw_name in (None, "") else " - " + _summary_field(raw_name, 24)
        rows.append(
            f"Actividad {_summary_field(activity_id, 16)}{name}: "
            f"{min(original_starts).isoformat()}..{max(original_finishes).isoformat()} "
            f"\u2192 {min(current_starts).isoformat()}..{max(current_finishes).isoformat()}"
        )
    detail_rows = rows[:6]
    remaining_rows = len(rows) - len(detail_rows)
    summary = "\n".join((
        "Aplicar STP V2 al proyecto original después de crear un respaldo verificado",
        f"Proyecto: {_summary_field(project.name, 32)}",
        f"Actividades afectadas: {len(authorized_ids)}",
        f"Movimientos directos: {sum(value == 'direct' for value in movement_units.values())}",
        f"Movimientos por cascada: {sum(value == 'cascade' for value in movement_units.values())}",
        f"Unidades fuera del horizonte visible: {len(hidden_units)}",
        f"Directorio de respaldos: {_summary_field(backup_path.parent, 64)}",
        f"Directorio de reportes: {_summary_field(html_report_path.parent, 64)}",
        *detail_rows,
        *((f"... {remaining_rows} actividades m\u00e1s",) if remaining_rows else ()),
    ))
    return summary[:4096]


def _summary_field(value: object, limit: int = 240) -> str:
    normalized = " ".join(str(value).split())
    return escape(normalized[:limit], quote=True)


def write_application_report(
    html_path: Path,
    json_path: Path,
    report: Mapping[str, object],
    *,
    atomic_replace: Callable[..., object] = atomic_replace_bytes_windows,
) -> tuple[Path, Path]:
    """Atomically write escaped HTML and redacted JSON transaction evidence."""
    if not isinstance(report, Mapping):
        raise TypeError("report must be a mapping")
    html_file = Path(os.path.abspath(os.fspath(html_path)))
    json_file = Path(os.path.abspath(os.fspath(json_path)))
    if html_file.parent != json_file.parent or html_file.suffix.casefold() != ".html" or json_file.suffix.casefold() != ".json":
        raise ValueError("report paths must be sibling HTML and JSON files")
    report_dir = _safe_directory(html_file.parent.parent, html_file.parent, create=False)
    safe_report = _sanitize_report_mapping(report)
    json_payload = (
        json.dumps(safe_report, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    ).encode("utf-8")
    html_payload = _html_report(safe_report).encode("utf-8")
    atomic_replace(json_file, json_payload, expected_parent=report_dir)
    atomic_replace(html_file, html_payload, expected_parent=report_dir)
    if _read_regular_bytes(json_file) != json_payload or _read_regular_bytes(html_file) != html_payload:
        raise OSError("application report verification failed")
    return html_file, json_file


def write_stage_trace(json_report_path: Path, stage: str) -> Path:
    """Durably append one non-sensitive live-transaction boundary.

    This trace intentionally uses a separate JSONL file so a VGS operation
    that never returns still leaves its last entered boundary available for
    diagnosis.  It is only operational evidence; it never changes a project
    archive or participates in the transaction outcome.
    """
    if (
        type(stage) is not str
        or not stage
        or any(character not in "abcdefghijklmnopqrstuvwxyz_" for character in stage)
    ):
        raise ValueError("stage trace name is invalid")
    json_file = Path(os.path.abspath(os.fspath(json_report_path)))
    if json_file.suffix.casefold() != ".json":
        raise ValueError("stage trace requires a JSON report path")
    report_dir = _safe_directory(
        json_file.parent.parent, json_file.parent, create=False
    )
    trace_file = json_file.with_suffix(".trace.jsonl")
    if trace_file.parent != report_dir:
        raise ValueError("stage trace path is outside the report directory")
    payload = (json.dumps({
        "at": datetime.now().isoformat(timespec="seconds"),
        "stage": stage,
    }, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")
    with open(trace_file, "ab") as handle:
        handle.write(payload)
        handle.flush()
        os.fsync(handle.fileno())
    return trace_file


def _try_write_stage_trace(
    writer: Callable[[Path, str], object], json_report_path: Path, stage: str
) -> None:
    """Keep diagnostics best-effort; failures must never affect VGS data."""
    try:
        writer(json_report_path, stage)
    except BaseException:
        pass


def atomic_compare_replace_bytes_windows(
    target: Path,
    payload: bytes,
    *,
    expected_parent: Path,
    expected_current_digests: frozenset[str],
):
    """Atomically compare the protected current digest and replace its bytes.

    The comparison occurs after the Windows atomic primitive has acquired its
    target lock, directory identity guard, and protected target handle.  It is
    therefore part of the same fail-closed replacement/rollback state machine,
    not a path-level preflight check.
    """
    if not _windows_atomic._is_windows():
        raise _windows_atomic.AtomicCommitError(
            "protected compare-and-swap requires Windows"
        )
    if type(payload) is not bytes:
        raise _windows_atomic.AtomicCommitError("atomic payload must be bytes")
    if (
        type(expected_current_digests) is not frozenset
        or not expected_current_digests
        or any(
            type(item) is not str
            or len(item) != 64
            or any(character not in "0123456789abcdef" for character in item)
            for item in expected_current_digests
        )
    ):
        raise _windows_atomic.AtomicCommitError(
            "expected current digests must be exact lowercase SHA-256 values"
        )

    parent = Path(os.path.abspath(os.fspath(expected_parent)))
    final = Path(os.path.abspath(os.fspath(target)))
    if final.parent != parent:
        raise _windows_atomic.AtomicCommitError(
            "target must be an exact child of the protected parent"
        )
    probed_parent = _windows_atomic._probe_directory_identity(parent)
    with _windows_atomic._target_lock(probed_parent, final.name):
        guard = _windows_atomic._open_directory_guard(parent)
        try:
            _windows_atomic._verify_directory_guard(guard)
            if not _windows_atomic._same_file(probed_parent, guard.identity):
                raise _windows_atomic.AtomicCommitError(
                    "protected parent identity changed before commit"
                )
            return _atomic_compare_replace_bytes_windows_locked(
                final, payload, parent, guard, expected_current_digests
            )
        finally:
            try:
                _windows_atomic._close_directory_guard(guard)
            except _windows_atomic.AtomicCommitError:
                pass


def _atomic_compare_replace_bytes_windows_locked(
    final: Path,
    payload: bytes,
    parent: Path,
    guard: object,
    expected_current_digests: frozenset[str],
):
    """Task 4 CAS variant of the existing protected Windows commit state."""
    expected_digest = sha256(payload).hexdigest()
    temporary = None
    temporary_handle = None
    temporary_identity = None
    old_handle = None
    old_identity = None
    old_digest = None
    backup = None
    backup_handle = None
    replaced = False
    try:
        _windows_atomic._verify_directory_guard(guard)
        temporary, temporary_handle = _windows_atomic._create_protected_temp(
            parent, final.name
        )
        _windows_atomic._write_all(temporary_handle, payload)
        _windows_atomic._flush_handle(temporary_handle)
        if _windows_atomic._digest_handle(temporary_handle) != expected_digest:
            raise _windows_atomic.AtomicCommitError(
                "protected temporary digest mismatch"
            )
        _windows_atomic._verify_directory_guard(guard)
        temporary_identity = _windows_atomic._identity_from_handle(temporary_handle)
        visible_temporary = _windows_atomic._visible_identity(temporary)
        if (
            visible_temporary is None
            or not _windows_atomic._same_file(
                temporary_identity, visible_temporary
            )
        ):
            raise _windows_atomic.AtomicCommitError(
                "protected temporary identity changed"
            )

        existing = _windows_atomic._open_existing_target(final)
        if existing is None:
            raise _windows_atomic.AtomicCommitError(
                "compare-and-swap target is unavailable"
            )
        old_handle, old_identity = existing
        old_payload = _windows_atomic._read_all_handle(old_handle)
        old_digest = sha256(old_payload).hexdigest()
        if old_digest not in expected_current_digests:
            raise _windows_atomic.AtomicCommitError(
                "protected target digest differs from expected transaction state"
            )
        backup, backup_handle = _windows_atomic._create_protected_sibling(
            parent, final.name, ".bak"
        )
        _windows_atomic._write_all(backup_handle, old_payload)
        _windows_atomic._flush_handle(backup_handle)
        if _windows_atomic._digest_handle(backup_handle) != old_digest:
            raise _windows_atomic.AtomicCommitError(
                "recovery backup digest mismatch"
            )
        backup_identity = _windows_atomic._identity_from_handle(backup_handle)
        visible_backup = _windows_atomic._visible_identity(backup)
        visible_target = _windows_atomic._visible_identity(final)
        held_old = _windows_atomic._identity_from_handle(old_handle)
        if (
            visible_backup is None
            or visible_target is None
            or not _windows_atomic._same_file(backup_identity, visible_backup)
            or not _windows_atomic._same_file(old_identity, held_old)
            or not _windows_atomic._same_file(old_identity, visible_target)
        ):
            raise _windows_atomic.AtomicCommitError(
                "protected backup or target identity changed"
            )

        _windows_atomic._verify_directory_guard(guard)
        _windows_atomic._rename_handle(
            temporary_handle,
            final,
            expected_parent=guard.path,
            replace=True,
        )
        replaced = True
        visible_committed = _windows_atomic._visible_identity(final)
        if (
            visible_committed is None
            or not _windows_atomic._same_file(
                temporary_identity, visible_committed
            )
        ):
            raise _windows_atomic.AtomicCommitError(
                "committed target identity mismatch"
            )
        _windows_atomic._flush_handle(temporary_handle)
        if _windows_atomic._digest_handle(temporary_handle) != expected_digest:
            raise _windows_atomic.AtomicCommitError(
                "committed target digest mismatch"
            )
        visible_committed = _windows_atomic._visible_identity(final)
        if (
            visible_committed is None
            or not _windows_atomic._same_file(
                temporary_identity, visible_committed
            )
        ):
            raise _windows_atomic.AtomicCommitError(
                "committed target identity mismatch"
            )
        _windows_atomic._verify_directory_guard(guard)
        backup_identity = _windows_atomic._identity_from_handle(backup_handle)
        visible_backup = _windows_atomic._visible_identity(backup)
        if (
            visible_backup is None
            or not _windows_atomic._same_file(backup_identity, visible_backup)
        ):
            raise _windows_atomic.AtomicCommitError(
                "recovery backup identity changed"
            )
        _windows_atomic._delete_file(backup)
        backup = None
        return _windows_atomic.AtomicCommitResult(final, expected_digest)
    except (OSError, TypeError, ValueError) as error:
        if replaced:
            if temporary_handle is None or temporary_identity is None:
                raise _windows_atomic.AtomicCommitIndeterminate(
                    "protected rollback could not be verified", backup
                ) from None
            if (
                backup is None
                or backup_handle is None
                or old_handle is None
                or old_identity is None
                or old_digest is None
            ):
                raise _windows_atomic.AtomicCommitIndeterminate(
                    "protected rollback could not be verified", backup
                ) from None
            _windows_atomic._rollback_existing_target(
                final,
                backup,
                backup_handle,
                old_handle,
                old_identity,
                old_digest,
                guard,
            )
            backup = None
            raise _windows_atomic.AtomicCommitError(
                "protected atomic replacement failed"
            ) from None
        if isinstance(error, _windows_atomic.AtomicCommitError):
            raise
        raise _windows_atomic.AtomicCommitError(
            "protected atomic replacement failed"
        ) from None
    finally:
        for handle in (temporary_handle, old_handle, backup_handle):
            if handle is not None:
                try:
                    _windows_atomic._close_handle(handle)
                except _windows_atomic.AtomicCommitError:
                    pass
        if temporary is not None and not replaced:
            try:
                _windows_atomic._delete_file(temporary, absent_ok=True)
            except _windows_atomic.AtomicCommitError:
                pass
        if backup is not None and not replaced:
            try:
                _windows_atomic._delete_file(backup, absent_ok=True)
            except _windows_atomic.AtomicCommitError:
                pass


def restore_from_backup(
    backup_path: Path,
    project_file: Path,
    *,
    expected_backup_digest: str | None = None,
    allowed_target_digests: frozenset[str] | None = None,
    atomic_replace: Callable[..., object] = atomic_replace_bytes_windows,
    atomic_compare_replace: Callable[..., object] | None = None,
) -> str:
    """Atomically restore backup bytes using a temporary inside the project directory."""
    project = Path(os.path.abspath(os.fspath(project_file)))
    project_dir = _safe_directory(project.parent, project.parent, create=False)
    backup = _validated_existing_child(project_dir, backup_path, "backup")
    if backup.suffix.casefold() != ".vgantt":
        raise ValueError("backup must be a .vgantt archive")
    payload = _read_regular_bytes(backup)
    digest = sha256(payload).hexdigest()
    if expected_backup_digest is not None and digest != expected_backup_digest:
        raise ApplicationVerificationError("backup identity changed before restore")
    if allowed_target_digests is not None:
        current_digest = sha256(_read_regular_bytes(project)).hexdigest()
        if current_digest not in allowed_target_digests:
            raise ApplicationVerificationError(
                "project changed outside the authorized transaction"
            )
    if allowed_target_digests is None:
        atomic_replace(project, payload, expected_parent=project_dir)
    else:
        compare_replace = (
            atomic_compare_replace
            if atomic_compare_replace is not None
            else atomic_compare_replace_bytes_windows
        )
        compare_replace(
            project,
            payload,
            expected_parent=project_dir,
            expected_current_digests=allowed_target_digests,
        )
    restored = _read_regular_bytes(project)
    if restored != payload or sha256(restored).hexdigest() != digest:
        raise ApplicationVerificationError("restored project does not match backup")
    return digest


def run_vgs_apply(
    dependencies: object,
    project_file: Path,
    package_file: Path,
    confirm: Callable[[str], object],
    *,
    expected_package_sha256: str | None = None,
) -> ApplicationResult:
    """Run the complete fail-closed transaction without importing Maptek."""
    project_key = os.path.normcase(
        os.path.abspath(os.fspath(project_file))
    )
    with _TRANSACTION_LOCKS_GUARD:
        transaction_lock = _TRANSACTION_LOCKS.setdefault(
            project_key, threading.RLock()
        )
    with transaction_lock:
        return _run_vgs_apply_locked(
            dependencies, project_file, package_file, confirm,
            expected_package_sha256=expected_package_sha256,
        )


def run_vgs_apply_pending_save(
    dependencies: object,
    project_file: Path,
    package_file: Path,
    confirm: Callable[[str], object],
    *,
    expected_package_sha256: str | None = None,
) -> ApplicationResult:
    """Apply in the open VGS session, deliberately leaving its save to the user.

    This is the live half of the two-phase workflow.  It does not invoke
    ``save``, ``update_ui`` or ``close``.  A durable receipt is written before
    the live mutation so Script 10 can later validate the user's own Save.
    """
    project_key = os.path.normcase(os.path.abspath(os.fspath(project_file)))
    with _TRANSACTION_LOCKS_GUARD:
        transaction_lock = _TRANSACTION_LOCKS.setdefault(project_key, threading.RLock())
    with transaction_lock:
        return _run_vgs_apply_pending_save_locked(
            dependencies, project_file, package_file, confirm,
            expected_package_sha256=expected_package_sha256,
        )


def _run_vgs_apply_pending_save_locked(
    dependencies: object,
    project_file: Path,
    package_file: Path,
    confirm: Callable[[str], object],
    *,
    expected_package_sha256: str | None = None,
) -> ApplicationResult:
    project = Path(os.path.abspath(os.fspath(project_file)))
    package = Path(os.path.abspath(os.fspath(package_file)))
    report_writer = _dependency(dependencies, "write_application_report", write_application_report)
    atomic_replace = _dependency(
        dependencies, "atomic_replace_bytes_windows", atomic_replace_bytes_windows
    )
    now = _dependency(dependencies, "now", datetime.now)
    try:
        stage_trace_writer = _dependency(dependencies, "write_stage_trace", write_stage_trace)
    except BaseException:
        stage_trace_writer = write_stage_trace
    try:
        prepared = prepare_application(
            dependencies, project, package, timestamp=now(),
            expected_package_sha256=expected_package_sha256,
        )
    except ApplicationRejected as error:
        return ApplicationResult("rejected", None, None, None, code=error.code)
    except BaseException:
        return ApplicationResult("rejected", None, None, None, code="preflight_failed")

    try:
        _require_active_path(_dependency(dependencies, "active_project"), prepared.project_file)
    except BaseException:
        return ApplicationResult("rejected", None, None, None, code="active_session_changed")
    if not callable(confirm):
        return _finish_before_backup(
            prepared, report_writer, atomic_replace, "rejected", "confirmation_unavailable",
        )
    try:
        confirmed = confirm(prepared.summary)
    except BaseException:
        confirmed = None
    if confirmed is not True:
        return _finish_before_backup(
            prepared, report_writer, atomic_replace, "cancelled", "confirmation_declined",
        )

    backup: VerifiedBackup | None = None
    try:
        backup_dir = _safe_directory(
            prepared.project_file.parent, prepared.backup_path.parent, create=True
        )
        _safe_directory(backup_dir, prepared.html_report_path.parent, create=True)
        backup_value = _dependency(
            dependencies, "create_verified_backup", create_verified_backup
        )(prepared.project_file, prepared.backup_path)
        backup = _coerce_verified_backup(backup_value, prepared.backup_path)
        backup_payload = _read_regular_bytes(backup.path)
        if (
            Path(os.path.abspath(os.fspath(backup.path))) != prepared.backup_path
            or backup.sha256 != prepared.original_digest
            or sha256(backup_payload).hexdigest() != prepared.original_digest
            or backup_payload != _read_regular_bytes(prepared.project_file)
        ):
            raise OSError("verified backup differs from synchronized project")
        _write_report(
            report_writer, prepared,
            _report_value("confirmed", "backup_verified", prepared, backup), atomic_replace,
        )
        receipt = PendingSaveReceipt(
            prepared.project_file.name,
            sha256(_read_regular_bytes(prepared.package_file)).hexdigest(),
            backup.path,
            backup.sha256,
            prepared.project_fingerprint,
            prepared.precedence_fingerprint,
            prepared.authorized_ids,
            prepared.native_operation_cost,
        )
        _dependency(
            dependencies, "write_pending_save_receipt", write_pending_save_receipt
        )(prepared.json_report_path, receipt, atomic_replace=atomic_replace)
    except BaseException:
        written = _try_write_report(
            report_writer,
            (prepared.html_report_path, prepared.json_report_path),
            _report_value("rejected", "backup_or_receipt_failed", prepared, backup),
            atomic_replace,
        )
        return ApplicationResult(
            "rejected", backup.path if backup else None, written[0], written[1],
            code="backup_or_receipt_failed",
        )

    failure_stage = "pre_bind"
    try:
        active_project = _dependency(dependencies, "active_project")
        _require_active_path(active_project, prepared.project_file)
        bind = _dependency(dependencies, "bind_maptek_apply_session", bind_maptek_apply_session)
        apply = _dependency(dependencies, "apply_maptek_operations", apply_maptek_operations)
        failure_stage = "bind"
        _try_write_stage_trace(stage_trace_writer, prepared.json_report_path, "before_bind")
        session = bind(active_project, prepared.plan, prepared.original_state)
        _try_write_stage_trace(stage_trace_writer, prepared.json_report_path, "after_bind")
        failure_stage = "apply"
        _try_write_stage_trace(stage_trace_writer, prepared.json_report_path, "before_apply")
        apply_log = apply(session, refresh_ui=True, manage_ui=True)
        percent_complete_compatibility = _safe_percent_complete_compatibility(
            apply_log.compatibility_evidence
        )
        _try_write_stage_trace(stage_trace_writer, prepared.json_report_path, "after_apply")
        _write_report(
            report_writer, prepared,
            _report_value(
                "applied_pending_save", "manual_save_required", prepared, backup,
                percent_complete_compatibility=percent_complete_compatibility,
            ),
            atomic_replace,
        )
        return ApplicationResult(
            "applied_pending_save", backup.path, prepared.html_report_path,
            prepared.json_report_path, None, False, "manual_save_required",
        )
    except BaseException as error:
        written = _try_write_report(
            report_writer,
            (prepared.html_report_path, prepared.json_report_path),
            _report_value(
                "live_apply_failed", "live_apply_failed", prepared, backup,
                differences=tuple(getattr(error, "differences", ())),
                requires_reopen=False,
                failure_stage=failure_stage,
                failure_reason=_safe_live_failure_reason(error),
                live_bar_count_evidence=_safe_live_bar_count_evidence(error),
            ),
            atomic_replace,
        )
        return ApplicationResult(
            "live_apply_failed", backup.path, written[0], written[1],
            code="live_apply_failed",
        )


def verify_pending_save(
    dependencies: object,
    project_file: Path,
    package_file: Path,
    receipt_file: Path,
    *,
    expected_package_sha256: str | None = None,
) -> ApplicationResult:
    """Verify the user's manual VGS save without touching the live session."""
    project = Path(os.path.abspath(os.fspath(project_file)))
    package_path = Path(os.path.abspath(os.fspath(package_file)))
    receipt_path = Path(os.path.abspath(os.fspath(receipt_file)))
    prepared: PreparedApplication | None = None
    receipt: PendingSaveReceipt | None = None
    verification_paths: tuple[Path, Path] | None = None
    report_writer = _dependency(dependencies, "write_application_report", write_application_report)
    atomic_replace = _dependency(
        dependencies, "atomic_replace_bytes_windows", atomic_replace_bytes_windows
    )
    try:
        project = _validated_project_file(project)
        receipt_path = _validated_existing_child(project.parent, receipt_path, "receipt")
        receipt = _dependency(
            dependencies, "load_pending_save_receipt", load_pending_save_receipt
        )(receipt_path)
        receipt_stem = receipt_path.name.removesuffix(".pending.json")
        if not receipt_stem:
            raise ApplicationVerificationError("receipt name is invalid")
        verification_paths = (
            receipt_path.parent / f"{receipt_stem}.verification.html",
            receipt_path.parent / f"{receipt_stem}.verification.json",
        )
        if receipt.project_name != project.name:
            raise ApplicationVerificationError("receipt project identity differs")
        backup_path = _validated_existing_child(project.parent, receipt.backup_path, "backup")
        expected_backup_dir = project.parent / "Back up STP"
        if backup_path.parent != expected_backup_dir:
            raise ApplicationVerificationError("receipt backup path differs")
        backup_payload = _read_regular_bytes(backup_path)
        if sha256(backup_payload).hexdigest() != receipt.backup_sha256:
            raise ApplicationVerificationError("receipt backup digest differs")
        package_path = _validated_existing_child(project.parent, package_path, "package")
        package_payload = _read_regular_bytes(package_path)
        package_digest = sha256(package_payload).hexdigest()
        if package_digest != receipt.package_sha256 or (
            expected_package_sha256 is not None and package_digest != expected_package_sha256
        ):
            raise ApplicationVerificationError("receipt package identity differs")
        read_archive = _dependency(dependencies, "read_vgantt", read_vgantt)
        load_package = _dependency(dependencies, "load_apply_package", load_apply_package)
        validate_package = _dependency(
            dependencies, "validate_apply_package", validate_apply_package
        )
        build_plan = _dependency(
            dependencies, "build_maptek_operation_plan", build_maptek_operation_plan
        )
        summarize_operation_cost = _dependency(
            dependencies, "summarize_native_operation_cost", summarize_native_operation_cost
        )
        original_state = read_archive(backup_path)
        package_value = load_package(json.loads(package_payload))
        validation = validate_package(package_value, original_state)
        if getattr(validation, "ok", None) is not True:
            raise ApplicationVerificationError(
                "receipt package no longer validates against backup",
                tuple(getattr(validation, "differences", ())),
            )
        plan = build_plan(package_value, original_state)
        native_cost = _coerce_native_operation_cost(summarize_operation_cost(plan))
        authorized_ids = frozenset(
            str(activity.activity_id) for activity in getattr(package_value, "activities", ())
        )
        project_digest = str(_dependency(
            dependencies, "project_fingerprint", project_fingerprint
        )(original_state))
        precedence_digest = str(_dependency(
            dependencies, "precedence_fingerprint", precedence_fingerprint
        )(original_state))
        if (
            native_cost != receipt.native_operation_cost
            or authorized_ids != receipt.authorized_ids
            or project_digest != receipt.project_fingerprint
            or precedence_digest != receipt.precedence_fingerprint
        ):
            raise ApplicationVerificationError("receipt preflight identity differs")
        prepared = PreparedApplication(
            project, package_path, backup_path,
            verification_paths[0], verification_paths[1],
            receipt.backup_sha256, original_state, package_value, plan, native_cost,
            authorized_ids, project_digest, precedence_digest, "",
        )
        reread_state = read_archive(project)
        _require_verified_result(dependencies, prepared, reread_state)
        verified_digest = sha256(_read_regular_bytes(project)).hexdigest()
        _write_report(
            report_writer, prepared,
            _report_value("applied", "verified_manual_save", prepared, VerifiedBackup(
                backup_path, receipt.backup_sha256,
            ), verified_digest=verified_digest),
            atomic_replace,
        )
        return ApplicationResult(
            "applied", backup_path, prepared.html_report_path, prepared.json_report_path,
            verified_digest, False, "verified_manual_save",
        )
    except BaseException as error:
        if prepared is not None:
            written = _try_write_report(
                report_writer,
                (prepared.html_report_path, prepared.json_report_path),
                _report_value(
                    "verification_failed", "manual_save_verification_failed", prepared,
                    VerifiedBackup(prepared.backup_path, prepared.original_digest),
                    differences=tuple(getattr(error, "differences", ())),
                ),
                atomic_replace,
            )
        elif verification_paths is not None:
            written = _try_write_report(
                report_writer,
                verification_paths,
                _report_value(
                    "verification_failed", "manual_save_verification_failed", None, None,
                ),
                atomic_replace,
            )
        else:
            written = (None, None)
        return ApplicationResult(
            "verification_failed", prepared.backup_path if prepared else (
                receipt.backup_path if receipt is not None else None
            ),
            written[0], written[1], code="manual_save_verification_failed",
        )


def _run_vgs_apply_locked(
    dependencies: object,
    project_file: Path,
    package_file: Path,
    confirm: Callable[[str], object],
    *,
    expected_package_sha256: str | None = None,
) -> ApplicationResult:
    project = Path(os.path.abspath(os.fspath(project_file)))
    package = Path(os.path.abspath(os.fspath(package_file)))
    report_writer = _dependency(
        dependencies, "write_application_report", write_application_report
    )
    try:
        stage_trace_writer = _dependency(
            dependencies, "write_stage_trace", write_stage_trace
        )
    except BaseException:
        stage_trace_writer = write_stage_trace
    atomic_replace = _dependency(
        dependencies, "atomic_replace_bytes_windows", atomic_replace_bytes_windows
    )
    atomic_compare_replace = _dependency(
        dependencies,
        "atomic_compare_replace_bytes_windows",
        atomic_compare_replace_bytes_windows,
    )
    now = _dependency(dependencies, "now", datetime.now)
    prepared: PreparedApplication | None = None
    backup: VerifiedBackup | None = None
    failure_stage: str | None = None
    active_path_expected: str | None = None
    active_path_observed: str | None = None
    active_path_evidence: dict[str, object] | None = None

    try:
        # The package must match the persisted archive before VGS can bind any
        # operation.  A live-session save is deliberately deferred until after
        # application and final verification, because VGS may recalculate the
        # archive while saving.
        instant = now()
        prepared = prepare_application(
            dependencies, project, package, timestamp=instant,
            expected_package_sha256=expected_package_sha256,
        )
    except ApplicationRejected as error:
        return ApplicationResult("rejected", None, None, None, code=error.code)
    except BaseException:
        return ApplicationResult(
            "rejected", None, None, None, code="preflight_failed"
        )

    if _operation_budget_exceeded(prepared.native_operation_cost):
        written = _write_budget_rejection_report(
            report_writer, prepared, atomic_replace,
        )
        return ApplicationResult(
            "rejected", None, written[0], written[1],
            code="operation_budget_exceeded",
        )

    # Confirm the caller is still on the archive that the package validated,
    # but never save it here.  Saving before binding the package makes VGS
    # recalculate the project and can invalidate an otherwise valid STP
    # package before any operation has been attempted.
    try:
        _require_active_path(
            _dependency(dependencies, "active_project"), prepared.project_file
        )
    except BaseException:
        return ApplicationResult(
            "rejected", None, None, None, code="active_session_changed"
        )

    if not callable(confirm):
        return _finish_before_backup(
            prepared, report_writer, atomic_replace, "rejected", "confirmation_unavailable"
        )
    try:
        confirmed = confirm(prepared.summary)
    except BaseException:
        confirmed = None
    if confirmed is not True:
        return _finish_before_backup(
            prepared, report_writer, atomic_replace, "cancelled", "confirmation_declined"
        )

    backup_creator = _dependency(
        dependencies, "create_verified_backup", create_verified_backup
    )
    try:
        backup_dir = _safe_directory(
            prepared.project_file.parent, prepared.backup_path.parent, create=True
        )
        _safe_directory(
            backup_dir, prepared.html_report_path.parent, create=True
        )
        backup_value = backup_creator(prepared.project_file, prepared.backup_path)
        backup = _coerce_verified_backup(backup_value, prepared.backup_path)
        if Path(os.path.abspath(os.fspath(backup.path))) != prepared.backup_path:
            raise OSError("backup helper returned an unexpected path")
        backup_payload = _read_regular_bytes(backup.path)
        if (
            backup.sha256 != prepared.original_digest
            or sha256(backup_payload).hexdigest() != prepared.original_digest
            or backup_payload != _read_regular_bytes(prepared.project_file)
        ):
            raise OSError("verified backup differs from synchronized project")
        _write_report(
            report_writer,
            prepared,
            _report_value("confirmed", "backup_verified", prepared, backup),
            atomic_replace,
        )
    except BaseException:
        written = _try_write_report(
            report_writer,
            (prepared.html_report_path, prepared.json_report_path),
            _report_value("rejected", "backup_or_report_failed", prepared, backup),
            atomic_replace,
        )
        return ApplicationResult(
            "rejected", backup.path if backup else None, written[0], written[1],
            code="backup_or_report_failed",
        )

    stage = "operation"
    verified_digest: str | None = None
    persisted_digest: str | None = None
    try:
        # Deliberately operate on the active original after its exact backup was
        # verified. This path never creates, activates, or saves a secondary
        # project archive.
        active_project = _dependency(dependencies, "active_project")
        _require_active_path(active_project, prepared.project_file)
        bind = _dependency(
            dependencies, "bind_maptek_apply_session", bind_maptek_apply_session
        )
        apply = _dependency(
            dependencies, "apply_maptek_operations", apply_maptek_operations
        )
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "before_bind"
        )
        session = bind(active_project, prepared.plan, prepared.original_state)
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "after_bind"
        )
        # A global VGS pause/resume clears and repopulates a large activity
        # chart.  Do not invoke that visual rebuild from Script 9: persist and
        # verify the original archive first, then let the user refresh with
        # Update Activities when a new chart/predecessor display is needed.
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "before_apply"
        )
        apply(session, refresh_ui=False, manage_ui=False)
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "after_apply"
        )

        if sha256(_read_regular_bytes(backup.path)).hexdigest() != prepared.original_digest:
            raise ApplicationVerificationError("backup identity changed before persistence")
        stage = "persist"
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "before_persist"
        )
        if _dependency(dependencies, "persist_active_project")(
            active_project, prepared.project_file
        ) is not True:
            raise ApplicationTransactionError("original project persistence was rejected")
        _require_active_path(active_project, prepared.project_file)
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "after_persist"
        )
        stage = "final_reread"
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "before_final_reread"
        )
        final_state = _dependency(dependencies, "read_vgantt", read_vgantt)(
            prepared.project_file
        )
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "after_final_reread"
        )
        persisted_digest = sha256(
            _read_regular_bytes(prepared.project_file)
        ).hexdigest()
        stage = "final_verification"
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "before_final_verification"
        )
        _require_verified_result(dependencies, prepared, final_state)
        _try_write_stage_trace(
            stage_trace_writer, prepared.json_report_path, "after_final_verification"
        )
        if sha256(_read_regular_bytes(prepared.project_file)).hexdigest() != persisted_digest:
            raise ApplicationVerificationError(
                "project changed during final verification"
            )
        verified_digest = persisted_digest
        stage = "final_report"
        _write_report(
            report_writer,
            prepared,
            _report_value(
                "applied", "verified_publish", prepared, backup,
                verified_digest=verified_digest,
            ),
            atomic_replace,
        )
        return ApplicationResult(
            "applied", backup.path, prepared.html_report_path,
            prepared.json_report_path, verified_digest, False, "verified_publish",
        )
    except BaseException as error:
        failure_stage = stage
        if isinstance(error, ActiveProjectPathError):
            active_path_expected = error.expected_path
            active_path_observed = error.observed_path
            active_path_evidence = error.evidence
        restored = False
        cleanup_residue = False
        cleanup_failed = False
        requires_reopen = True
        try:
            known_target_digests = {prepared.original_digest}
            if persisted_digest is not None:
                known_target_digests.add(persisted_digest)
            restore_from_backup(
                backup.path,
                prepared.project_file,
                expected_backup_digest=prepared.original_digest,
                allowed_target_digests=frozenset(known_target_digests),
                atomic_replace=atomic_replace,
                atomic_compare_replace=atomic_compare_replace,
            )
            if _read_regular_bytes(prepared.project_file) != _read_regular_bytes(backup.path):
                raise ApplicationVerificationError("rollback identity mismatch")
            restored = True
            status = "restored_requires_reopen"
            failure_code = "direct_failure_restored"
        except BaseException:
            status = "restore_failed"
            failure_code = "restore_failed"
        written = _try_write_report(
            report_writer,
            (prepared.html_report_path, prepared.json_report_path),
            _report_value(
                status,
                failure_code,
                prepared,
                backup,
                differences=tuple(getattr(error, "differences", ())),
                verified_digest=verified_digest,
                restored=restored,
                cleanup_residue=cleanup_residue,
                cleanup_failed=cleanup_failed,
                requires_reopen=requires_reopen,
                failure_stage=failure_stage,
                active_path_expected=active_path_expected,
                active_path_observed=active_path_observed,
                active_path_evidence=active_path_evidence,
            ),
            atomic_replace,
        )
        return ApplicationResult(
            status,
            backup.path,
            written[0],
            written[1],
            verified_digest,
            restored,
            failure_code,
        )


def _dependency(dependencies: object, name: str, default: object = ...):
    try:
        value = inspect.getattr_static(dependencies, name)
    except AttributeError:
        if default is ...:
            raise ApplicationRejected(
                "dependency_unavailable", f"required dependency '{name}' is unavailable"
            )
        return default
    if isinstance(value, property):
        raise ApplicationRejected(
            "dependency_unavailable", f"required dependency '{name}' is unavailable"
        )
    return object.__getattribute__(dependencies, name)


def canonical_active_project_path(value: object) -> str:
    """Normalize only a valid local Windows filename returned by VGS."""
    if type(value) is not str or not value:
        raise ValueError("active project filename is unavailable")
    raw = _strip_extended_windows_prefix(value)
    return os.path.normcase(os.path.normpath(os.path.abspath(raw)))


def _active_path_evidence(value: object) -> dict[str, object]:
    """Return non-sensitive shape information for an active VGS filename."""
    raw = value if type(value) is str else ""
    prefix = "plain"
    normalized = raw
    folded = raw.casefold()
    if folded.startswith("\\\\?\\unc\\"):
        prefix = "extended_unc"
        normalized = _strip_extended_windows_prefix(raw)
    elif folded.startswith("\\\\?\\"):
        prefix = "extended"
        normalized = _strip_extended_windows_prefix(raw)
    if not normalized:
        prefix = "invalid"
    path = Path(normalized) if normalized else None
    return {
        "parent": path.parent.name if path is not None else "",
        "stem": path.stem if path is not None else "",
        "prefix": prefix,
        "is_absolute": bool(normalized) and os.path.isabs(normalized),
    }


def require_active_project_path(value: object, expected_path: Path) -> None:
    """Require an active VGS filename to identify the exact expected archive."""
    expected = canonical_active_project_path(os.fspath(expected_path))
    if type(value) is not str or not value:
        raise ActiveProjectPathError(expected_path, value)
    raw = _strip_extended_windows_prefix(value)
    if not os.path.isabs(raw):
        if os.path.basename(raw) != raw or raw != expected_path.name:
            raise ActiveProjectPathError(expected_path, value)
        raw = os.path.join(os.fspath(expected_path.parent), raw)
    try:
        observed = canonical_active_project_path(raw)
    except (TypeError, ValueError, OSError):
        raise ActiveProjectPathError(expected_path, value) from None
    if observed != expected:
        raise ActiveProjectPathError(expected_path, value)


def _strip_extended_windows_prefix(value: str) -> str:
    """Return the ordinary spelling of an extended-length Windows filename."""
    folded = value.casefold()
    if folded.startswith("\\\\?\\unc\\"):
        return "\\\\" + value[8:]
    if folded.startswith("\\\\?\\"):
        return value[4:]
    return value


def _require_active_path(project: object, expected_path: Path) -> None:
    """Require the active VGS session to point at one canonical archive path."""
    filename = _capture_active_project_method(project, "project_filename")(True)
    require_active_project_path(filename, expected_path)


def _require_verified_result(
    dependencies: object, prepared: PreparedApplication, reread_state: object
) -> None:
    verifier = _dependency(
        dependencies, "verify_maptek_result", verify_maptek_result
    )
    report = verifier(prepared.plan, reread_state)
    if getattr(report, "ok", None) is not True:
        raise ApplicationVerificationError(
            "persisted Maptek result was not verified",
            tuple(getattr(report, "differences", ())),
        )


def _safe_percent_complete_compatibility(
    value: object,
) -> dict[str, dict[str, object]]:
    """Copy only the exact safe compatibility evidence emitted by the adapter."""
    if not isinstance(value, Mapping):
        raise ApplicationVerificationError("live compatibility evidence is invalid")
    validated: dict[str, dict[str, object]] = {}
    for activity_id, item in value.items():
        if (
            type(activity_id) is not str
            or not activity_id
            or activity_id[0] not in "123456789"
            or any(character not in "0123456789" for character in activity_id)
            or not isinstance(item, Mapping)
            or len(item) != 3
            or any(type(field) is not str for field in item)
            or set(item) != {"rule", "initial", "final"}
        ):
            raise ApplicationVerificationError(
                "live compatibility evidence is invalid"
            )
        rule = item["rule"]
        initial = item["initial"]
        final = item["final"]
        if (
            type(rule) is not str
            or rule != "percent_complete_hidden_tail"
            or type(initial) is not int
            or initial <= 0
            or type(final) is not int
            or final <= 0
            or final < initial
        ):
            raise ApplicationVerificationError(
                "live compatibility evidence is invalid"
            )
        validated[activity_id] = {
            "rule": rule,
            "initial": initial,
            "final": final,
        }
    return validated


def _capture_active_project_method(project: object, name: str) -> Callable[..., object]:
    project_type = type(project)
    member = inspect.getattr_static(project_type, name, None)
    effective = inspect.getattr_static(project, name, None)
    if type(member) is not FunctionType or effective is not member:
        raise ApplicationRejected(
            "capability_unavailable", f"active project method '{name}' is unavailable"
        )
    bound = object.__getattribute__(project, name)
    if (
        type(bound) is not MethodType
        or bound.__self__ is not project
        or bound.__func__ is not member
    ):
        raise ApplicationRejected(
            "capability_unavailable", f"active project method '{name}' is unavailable"
        )
    return bound


def _finish_before_backup(
    prepared: PreparedApplication,
    writer: Callable[..., object],
    atomic_replace: Callable[..., object],
    status: str,
    code: str,
) -> ApplicationResult:
    del prepared, writer, atomic_replace
    return ApplicationResult(status, None, None, None, code=code)


def _write_report(
    writer: Callable[..., object],
    prepared: PreparedApplication,
    report: Mapping[str, object],
    atomic_replace: Callable[..., object],
) -> tuple[Path, Path]:
    writer(
        prepared.html_report_path,
        prepared.json_report_path,
        report,
        atomic_replace=atomic_replace,
    )
    return prepared.html_report_path, prepared.json_report_path


def _try_write_report(
    writer: Callable[..., object],
    paths: tuple[Path, Path] | None,
    report: Mapping[str, object],
    atomic_replace: Callable[..., object],
) -> tuple[Path | None, Path | None]:
    if paths is None:
        return None, None
    try:
        writer(paths[0], paths[1], report, atomic_replace=atomic_replace)
        return paths
    except BaseException:
        return None, None


def _report_value(
    status: str,
    code: str,
    prepared: PreparedApplication | None,
    backup: VerifiedBackup | None,
    differences: tuple[object, ...] = (),
    *,
    verified_digest: str | None = None,
    restored: bool = False,
    cleanup_residue: bool = False,
    cleanup_failed: bool = False,
    requires_reopen: bool = False,
    failure_stage: str | None = None,
    failure_reason: str | None = None,
    live_bar_count_evidence: Mapping[str, int] | None = None,
    percent_complete_compatibility: Mapping[
        str, Mapping[str, object]
    ] | None = None,
    active_path_expected: str | None = None,
    active_path_observed: str | None = None,
    active_path_evidence: Mapping[str, object] | None = None,
) -> dict[str, object]:
    rows = []
    for item in differences:
        rows.append({
            "code": str(getattr(item, "code", "validation_difference")),
            "activity_id": str(getattr(item, "activity_id", "")),
            "detail": str(getattr(item, "detail", "validation failed")),
        })
    result: dict[str, object] = {
        "schema": "vgs-stp-application-report/1",
        "status": status,
        "code": code,
        "restored": restored,
        "requires_reopen": requires_reopen,
        "cleanup_residue": cleanup_residue,
        "cleanup_failed": cleanup_failed,
        "recovery_instruction": (
            _DIRECT_RECOVERY_INSTRUCTION
            if cleanup_residue or status == "restore_failed" else (
                "Cierre y vuelva a abrir VGS antes de continuar."
                if requires_reopen else (
                    "" 
                )
            )
        ),
        "differences": rows,
    }
    if prepared is not None:
        result.update({
            "project_name": prepared.project_file.name,
            "project_fingerprint": prepared.project_fingerprint,
            "precedence_fingerprint": prepared.precedence_fingerprint,
            "original_sha256": prepared.original_digest,
            "authorized_activity_ids": sorted(prepared.authorized_ids),
            "html_report_path": str(prepared.html_report_path),
            "json_report_path": str(prepared.json_report_path),
            "native_operations_total": prepared.native_operation_cost.total,
            "native_operation_limit": _MAX_NATIVE_OPERATIONS,
            "native_splits": prepared.native_operation_cost.split_count,
            "native_moves": prepared.native_operation_cost.move_count,
            "native_operations_by_activity": dict(
                prepared.native_operation_cost.by_activity
            ),
        })
    if backup is not None:
        result.update({
            "backup_name": backup.path.name,
            "backup_path": str(backup.path),
            "backup_sha256": backup.sha256,
        })
    if verified_digest is not None:
        result["verified_project_sha256"] = verified_digest
    if failure_stage is not None:
        result["failure_stage"] = failure_stage
    if failure_reason is not None:
        result["failure_reason"] = failure_reason
    if live_bar_count_evidence is not None:
        result["live_bar_count_evidence"] = dict(live_bar_count_evidence)
    if percent_complete_compatibility:
        result["percent_complete_compatibility"] = {
            activity_id: dict(evidence)
            for activity_id, evidence in percent_complete_compatibility.items()
        }
    if active_path_expected is not None:
        result["active_path_expected"] = active_path_expected
    if active_path_observed is not None:
        result["active_path_observed"] = active_path_observed
    if active_path_evidence is not None:
        result["active_path_evidence"] = dict(active_path_evidence)
    return result


def _safe_live_failure_reason(error: BaseException) -> str:
    """Classify a rejected live call without serializing exception text."""
    if isinstance(error, CapabilityError):
        return "capability_unavailable"
    if isinstance(error, ApplyError):
        return "live_state_mismatch"
    if isinstance(error, PreflightError):
        return "maptek_plan_rejected"
    if isinstance(error, ActiveProjectPathError):
        return "active_session_changed"
    return "unexpected_live_error"


def _safe_live_bar_count_evidence(
    error: BaseException,
) -> dict[str, int] | None:
    """Return only validated integer bar-count evidence from a live rejection."""
    if not isinstance(error, ApplyError):
        return None
    activity_id = getattr(error, "activity_id", None)
    expected_count = getattr(error, "expected_count", None)
    observed_count = getattr(error, "observed_count", None)
    if (
        type(activity_id) is not int or activity_id < 0
        or type(expected_count) is not int or expected_count <= 0
        or type(observed_count) is not int or observed_count <= 0
    ):
        return None
    return {
        "activity_id": activity_id,
        "expected": expected_count,
        "observed": observed_count,
    }


def _sanitize_report_mapping(report: Mapping[str, object]) -> dict[str, object]:
    sanitized: dict[str, object] = {}
    for raw_key, raw_value in report.items():
        key = str(raw_key)
        lowered = key.casefold()
        if any(word in lowered for word in _SENSITIVE_WORDS) or "working_path" in lowered:
            continue
        sanitized[key] = _sanitize_report_value(raw_value)
    return sanitized


def _sanitize_report_value(value: object) -> object:
    if value is None or type(value) in (bool, int):
        return value
    if type(value) is float:
        return value if value == value and value not in (float("inf"), float("-inf")) else None
    if isinstance(value, Mapping):
        return _sanitize_report_mapping(value)
    if isinstance(value, (list, tuple, set, frozenset)):
        return [_sanitize_report_value(item) for item in value]
    text = str(value)
    lowered = text.casefold()
    if any(word in lowered for word in _SENSITIVE_WORDS):
        return "[redacted]"
    if ".working" in lowered or ".tmp" in lowered:
        return "[temporary path redacted]"
    return text


def _html_report(report: Mapping[str, object]) -> str:
    differences = report.get("differences", [])
    rows = []
    for key, value in report.items():
        if key == "differences":
            continue
        rows.append(
            "<tr><th>" + escape(str(key), quote=True) + "</th><td>"
            + escape(str(value), quote=True) + "</td></tr>"
        )
    difference_rows = []
    if isinstance(differences, list):
        for item in differences:
            if isinstance(item, Mapping):
                difference_rows.append(
                    "<li>" + escape(str(item.get("code", "")), quote=True)
                    + " [" + escape(str(item.get("activity_id", "")), quote=True)
                    + "]: " + escape(str(item.get("detail", "")), quote=True) + "</li>"
                )
    return (
        "<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\">"
        "<title>Aplicacion STP V2</title></head><body>"
        "<h1>Aplicacion STP V2</h1><table>" + "".join(rows) + "</table>"
        "<h2>Diferencias</h2><ul>" + "".join(difference_rows) + "</ul>"
        "</body></html>\n"
    )


def _coerce_verified_backup(value: object, expected_path: Path) -> VerifiedBackup:
    if type(value) is VerifiedBackup:
        return value
    path = Path(getattr(value, "path", expected_path))
    digest = getattr(value, "sha256", None)
    if digest is None and path.is_file():
        digest = sha256(_read_regular_bytes(path)).hexdigest()
    if type(digest) is not str:
        raise OSError("backup helper returned no verified digest")
    return VerifiedBackup(path, digest)


def _validated_project_file(path: Path) -> Path:
    project = Path(os.path.abspath(os.fspath(path)))
    if project.suffix.casefold() != ".vgantt":
        raise ApplicationRejected("project_path_invalid", "project must be a .vgantt file")
    _assert_no_reparse(project, require_exists=True)
    if not project.is_file():
        raise ApplicationRejected("project_path_invalid", "project file is unavailable")
    return project


def _validated_existing_child(root: Path, path: Path, label: str) -> Path:
    root_absolute = Path(os.path.abspath(os.fspath(root)))
    candidate = Path(os.path.abspath(os.fspath(path)))
    _require_literal_descendant(root_absolute, candidate)
    _assert_no_reparse(candidate, require_exists=True)
    if not candidate.is_file():
        raise ValueError(f"{label} must be a regular file")
    return candidate


def _safe_directory(root: Path, path: Path, *, create: bool) -> Path:
    root_absolute = Path(os.path.abspath(os.fspath(root)))
    directory = Path(os.path.abspath(os.fspath(path)))
    if directory != root_absolute:
        _require_literal_descendant(root_absolute, directory)
    _assert_no_reparse(root_absolute, require_exists=True)
    if create:
        directory.mkdir(parents=True, exist_ok=True)
    _assert_no_reparse(directory, require_exists=True)
    if not directory.is_dir():
        raise ValueError("protected path must be a directory")
    return directory


def _require_literal_descendant(root: Path, candidate: Path) -> None:
    root_absolute = Path(os.path.abspath(os.fspath(root)))
    candidate_absolute = Path(os.path.abspath(os.fspath(candidate)))
    if candidate_absolute == root_absolute:
        raise ValueError("path must be below the project directory")
    try:
        candidate_absolute.relative_to(root_absolute)
    except ValueError as error:
        raise ValueError("path escapes the project directory") from error


def _assert_no_reparse(path: Path, *, require_exists: bool) -> None:
    candidate = Path(os.path.abspath(os.fspath(path)))
    existing: list[Path] = []
    cursor = candidate
    while True:
        if cursor.exists() or cursor.is_symlink():
            existing.append(cursor)
        elif cursor == candidate and require_exists:
            raise FileNotFoundError("protected path is unavailable")
        if cursor.parent == cursor:
            break
        cursor = cursor.parent
    for component in reversed(existing):
        information = component.lstat()
        attributes = int(getattr(information, "st_file_attributes", 0))
        if component.is_symlink() or attributes & _REPARSE_ATTRIBUTE:
            raise ValueError("reparse points are not permitted")


def _read_regular_bytes(path: Path) -> bytes:
    _assert_no_reparse(path, require_exists=True)
    information = path.stat()
    if not stat.S_ISREG(information.st_mode):
        raise ValueError("protected path must be a regular file")
    return path.read_bytes()


def _unlink_exact_file(path: Path) -> None:
    candidate = Path(path)
    try:
        if candidate.is_symlink():
            return
        if candidate.exists() and candidate.is_file():
            candidate.unlink()
    except OSError:
        pass
