"""Fail-closed Maptek operation planning for validated STP V2 apply packages.

This module deliberately has no Maptek import. Planning is pure and reusable;
only an opaque one-shot session can reach the fixed live surface, and it never
saves the project.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime, time, timedelta
from decimal import Decimal, localcontext
import inspect
import math
import os
from pathlib import Path
from threading import RLock
from types import FunctionType, MappingProxyType, MethodType
from typing import Any, Callable, Literal, Mapping, final
from weakref import WeakKeyDictionary


_VGS_GROUP_QUANTITY_SERIALIZATION_TOLERANCE = Decimal("0.000000000001")
_VGS_SPLIT_CALENDAR_SERIALIZATION_TOLERANCE = timedelta(seconds=1)
_VGS_PROGRESS_BOUNDARY_SERIALIZATION_TOLERANCE = timedelta(seconds=5)

from stp_v2_apply_contract import (
    ApplyActivity,
    ApplyFragment,
    ApplyPackage,
    validate_apply_package,
)
from stp_v2_core import (
    NativeActivity,
    NativeBar,
    ProjectState,
    ValidationDifference,
    ValidationReport,
    precedence_fingerprint,
    project_fingerprint,
)
class MaptekAdapterError(RuntimeError):
    """Base error for the isolated live-application boundary."""


class PreflightError(MaptekAdapterError):
    """The package or immutable operation plan is not safely representable."""


class CapabilityError(MaptekAdapterError):
    """The live object does not expose the exact required Maptek surface."""


class ApplyError(MaptekAdapterError):
    """The live object drifted or rejected an operation."""


class LiveBarCountMismatch(ApplyError):
    """A live activity exposes a safe, count-only split-state mismatch."""

    def __init__(
        self, activity_id: int, expected_count: int, observed_count: int | None,
    ) -> None:
        self.activity_id = activity_id
        self.expected_count = expected_count
        self.observed_count = observed_count
        if observed_count is None:
            message = "live bar count is not a canonical positive decimal string"
        else:
            message = (
                f"live bar count drift for activity {activity_id}: "
                f"expected {expected_count}, found {observed_count}"
            )
        super().__init__(message)


@dataclass(frozen=True)
class SplitOperation:
    activity_id: int
    native_bar_xml_key: str
    native_bar_key: str
    native_bar_index: int
    task_bar_index: int
    boundary: datetime
    proportion: Decimal
    delay_days: Decimal
    expected_bar_count: int

    @property
    def kind(self) -> Literal["split"]:
        return "split"


@dataclass(frozen=True)
class MoveOperation:
    activity_id: int
    native_bar_xml_key: str
    native_bar_key: str
    native_bar_index: int
    one_based_bar_indices: tuple[int, ...]
    logical_unit_ids: tuple[str, ...]
    provenance: tuple[Literal["unchanged", "direct", "cascade"], ...]
    target_start: datetime
    delta_days: int
    expected_bar_count: int

    @property
    def kind(self) -> Literal["move"]:
        return "move"


@dataclass(frozen=True)
class BarIndexEvidence:
    activity_id: int
    native_bar_xml_key: str
    native_bar_key: str
    native_bar_index: int
    final_bar_index: int
    native_descendant_order: int
    logical_unit_id: str
    logical_unit_order: int
    fragment_ids: tuple[str, ...]
    provenance: tuple[Literal["unchanged", "direct", "cascade"], ...]
    original_start: datetime
    original_finish: datetime
    target_start: datetime
    target_finish: datetime

    @property
    def stable_identity(self) -> tuple[object, ...]:
        return (
            self.native_bar_xml_key,
            self.native_bar_key,
            self.native_bar_index,
            self.native_descendant_order,
            self.original_start,
            self.original_finish,
            self.logical_unit_id,
            self.logical_unit_order,
            self.target_start,
            self.target_finish,
        )


@dataclass(frozen=True, slots=True)
class MaptekOperationPlan:
    operations: tuple[SplitOperation | MoveOperation, ...]
    initial_bar_counts: Mapping[int, int]
    final_bar_counts: Mapping[int, int]
    bar_index_evidence: tuple[BarIndexEvidence, ...]
    source_package: ApplyPackage = field(repr=False)
    source_state: ProjectState = field(repr=False)
    source_project_path: Path
    source_project_fingerprint: str
    source_precedence_fingerprint: str

    def __post_init__(self) -> None:
        object.__setattr__(self, "operations", tuple(self.operations))
        object.__setattr__(
            self, "initial_bar_counts", MappingProxyType(dict(self.initial_bar_counts))
        )
        object.__setattr__(
            self, "final_bar_counts", MappingProxyType(dict(self.final_bar_counts))
        )
        object.__setattr__(self, "bar_index_evidence", tuple(self.bar_index_evidence))


@dataclass(frozen=True, slots=True)
class NativeOperationCost:
    """Exact count of native calls that can change a VGS activity chart.

    The count deliberately uses the already-authorized operation plan rather
    than the visual daily fragments.  A bar that moves as one native operation
    remains one operation even when STP renders many civil-day cells.
    """

    split_count: int
    move_count: int
    total: int
    by_activity: Mapping[str, int]

    def __post_init__(self) -> None:
        if (
            type(self.split_count) is not int or self.split_count < 0
            or type(self.move_count) is not int or self.move_count < 0
            or type(self.total) is not int
            or self.total != self.split_count + self.move_count
        ):
            raise ValueError("native operation cost is invalid")
        safe_activity_costs: dict[str, int] = {}
        for activity_id, count in dict(self.by_activity).items():
            key = str(activity_id)
            if not key or type(count) is not int or count <= 0:
                raise ValueError("native operation activity cost is invalid")
            safe_activity_costs[key] = count
        if sum(safe_activity_costs.values()) != self.total:
            raise ValueError("native operation cost does not add up")
        object.__setattr__(
            self, "by_activity", MappingProxyType(safe_activity_costs)
        )


@dataclass(frozen=True)
class MaptekCapabilities:
    project_filename: Callable[..., Any]
    split_activity: Callable[..., Any]
    set_start_of_bar: Callable[..., Any]
    set_pause_ui: Callable[..., Any]
    update_ui: Callable[..., Any]
    save: Callable[..., Any]
    get_number_bars: Callable[..., Any]


@dataclass(frozen=True)
class MaptekApplyLog:
    operations_applied: int
    final_bar_counts: Mapping[int, int]
    compatibility_evidence: Mapping[str, Mapping[str, object]] = field(
        default_factory=dict
    )

    def __post_init__(self) -> None:
        object.__setattr__(
            self, "final_bar_counts", MappingProxyType(dict(self.final_bar_counts))
        )
        object.__setattr__(
            self, "compatibility_evidence",
            _freeze_compatibility_evidence(self.compatibility_evidence),
        )


@final
class MaptekApplySession:
    """Opaque one-shot live capability created only by the bind operation."""

    __slots__ = ("__weakref__",)

    def __new__(cls, token: object = None) -> MaptekApplySession:
        if cls is not MaptekApplySession or token is not _SESSION_CONSTRUCTOR_TOKEN:
            raise TypeError("MaptekApplySession can only be created by bind")
        return super().__new__(cls)


@dataclass(frozen=True)
class _PreparedSplitCall:
    operation: SplitOperation
    proportion: float
    delay_days: float


@dataclass(frozen=True)
class _PreparedMoveCall:
    operation: MoveOperation
    bar_reference: str
    target_start: str


@dataclass(frozen=True, slots=True)
class _LiveBarCountContract:
    initial_bar_counts: Mapping[int, int]
    operation_expected_counts: tuple[int, ...]
    final_bar_counts: Mapping[int, int]
    compatibility_evidence: Mapping[str, Mapping[str, object]]

    def __post_init__(self) -> None:
        object.__setattr__(
            self, "initial_bar_counts",
            MappingProxyType(dict(self.initial_bar_counts)),
        )
        object.__setattr__(
            self, "operation_expected_counts",
            tuple(self.operation_expected_counts),
        )
        object.__setattr__(
            self, "final_bar_counts",
            MappingProxyType(dict(self.final_bar_counts)),
        )
        object.__setattr__(
            self, "compatibility_evidence",
            _freeze_compatibility_evidence(self.compatibility_evidence),
        )


@dataclass(frozen=True, slots=True)
class _NativeSplitSpec:
    native_index: int
    native_bar: NativeBar
    boundary: datetime
    proportion: Decimal
    delay_days: Decimal
    task_bar_index: int
    expected_bar_count: int
    child_one_based_index: int


@dataclass(slots=True)
class _SessionRecord:
    project: object
    capabilities: MaptekCapabilities
    prepared: tuple[_PreparedSplitCall | _PreparedMoveCall, ...]
    live_contract: _LiveBarCountContract
    status: Literal["fresh", "applying", "succeeded", "failed"] = "fresh"


_MISSING = object()
_SESSION_CONSTRUCTOR_TOKEN = object()
_REQUIRED_METHOD_NAMES = (
    "project_filename", "split_activity", "set_start_of_bar", "set_pause_ui",
    "update_ui", "save", "get_number_bars",
)
_MAPPING_PROXY_TYPE = type(MappingProxyType({}))
_SESSION_LOCK = RLock()
_SESSIONS: WeakKeyDictionary[MaptekApplySession, _SessionRecord] = (
    WeakKeyDictionary()
)


def build_maptek_operation_plan(
    package: ApplyPackage, state: ProjectState
) -> MaptekOperationPlan:
    """Build immutable split/index/move evidence without touching a live project."""
    return _build_maptek_operation_plan(package, state)


def summarize_native_operation_cost(plan: MaptekOperationPlan) -> NativeOperationCost:
    """Return a fail-closed, exact cost for an immutable native operation plan."""
    if type(plan) is not MaptekOperationPlan:
        raise PreflightError("native operation plan is invalid")
    split_count = 0
    move_count = 0
    by_activity: dict[str, int] = {}
    for operation in plan.operations:
        if type(operation) is SplitOperation:
            split_count += 1
        elif type(operation) is MoveOperation:
            move_count += 1
        else:
            raise PreflightError("native operation plan contains an invalid operation")
        if type(operation.activity_id) is not int or operation.activity_id < 0:
            raise PreflightError("native operation plan has an invalid activity")
        activity_id = str(operation.activity_id)
        by_activity[activity_id] = by_activity.get(activity_id, 0) + 1
    return NativeOperationCost(
        split_count=split_count,
        move_count=move_count,
        total=split_count + move_count,
        by_activity=by_activity,
    )


def _build_maptek_operation_plan(
    package: ApplyPackage, state: ProjectState
) -> MaptekOperationPlan:
    report = validate_apply_package(package, state)
    if not report.ok:
        raise PreflightError(_report_summary(report))

    activity_inputs = []
    seen_live_ids: set[int] = set()
    for supplied in package.activities:
        if not any(
            fragment.start != fragment.original_start
            or fragment.finish != fragment.original_finish
            for fragment in supplied.fragments
        ):
            continue
        live_id = _maptek_activity_id(supplied.activity_id)
        if live_id in seen_live_ids:
            raise PreflightError("activity IDs collide at the Maptek integer boundary")
        seen_live_ids.add(live_id)
        native = state.activities[supplied.activity_id]
        activity_inputs.append((live_id, supplied, native.bars))
    activity_inputs.sort(key=lambda item: item[0])

    initial_counts: dict[int, int] = {}
    final_counts: dict[int, int] = {}
    split_operations: list[SplitOperation] = []
    move_operations: list[MoveOperation] = []
    all_evidence: list[BarIndexEvidence] = []

    for live_id, supplied, native_bars in activity_inputs:
        completion_cutoff = state.completion_cutoff
        initial_counts[live_id] = len(native_bars)
        fragments_by_bar = _fragments_by_native_bar(supplied, native_bars)
        split_specs, split_child_indices = _native_split_specs(
            native_bars, fragments_by_bar, completion_cutoff,
        )
        for spec in split_specs:
            split_operations.append(SplitOperation(
                activity_id=live_id,
                native_bar_xml_key=spec.native_bar.xml_key,
                native_bar_key=spec.native_bar.bar_key,
                native_bar_index=spec.native_index,
                task_bar_index=spec.task_bar_index,
                boundary=spec.boundary,
                proportion=spec.proportion,
                delay_days=spec.delay_days,
                expected_bar_count=spec.expected_bar_count,
            ))
        evidence, fragment_indices = _index_evidence(
            live_id,
            native_bars,
            fragments_by_bar,
            completion_cutoff,
            split_child_indices=split_child_indices,
        )
        all_evidence.extend(evidence)
        final_counts[live_id] = len(native_bars) + len(split_specs)
        move_operations.extend(_moves_for_activity(
            live_id, supplied, native_bars, fragments_by_bar,
            fragment_indices, final_counts[live_id], evidence,
            frozenset(spec.native_index for spec in split_specs),
        ))

    plan = MaptekOperationPlan(
        operations=tuple((*split_operations, *move_operations)),
        initial_bar_counts=initial_counts,
        final_bar_counts=final_counts,
        bar_index_evidence=tuple(all_evidence),
        source_package=package,
        source_state=state,
        source_project_path=Path(state.path).resolve(),
        source_project_fingerprint=project_fingerprint(state),
        source_precedence_fingerprint=precedence_fingerprint(state),
    )
    _prepare_operations(plan)
    return plan


def probe_maptek_capabilities(project: object) -> MaptekCapabilities:
    """Capture each fixed, plain method once without invoking dynamic lookup hooks."""
    captured: dict[str, Callable[..., Any]] = {}
    project_type = type(project)
    unavailable: list[str] = []
    for name in _REQUIRED_METHOD_NAMES:
        static_member = inspect.getattr_static(project_type, name, _MISSING)
        if static_member is _MISSING or type(static_member) is not FunctionType:
            unavailable.append(name)
            continue
        effective_member = inspect.getattr_static(project, name, _MISSING)
        if effective_member is not static_member:
            unavailable.append(name)
            continue
        bound_member = object.__getattribute__(project, name)
        if (type(bound_member) is not MethodType
                or bound_member.__self__ is not project
                or bound_member.__func__ is not static_member):
            unavailable.append(name)
            continue
        captured[name] = bound_member
    if unavailable:
        raise CapabilityError(
            "required Maptek methods are unavailable: " + ", ".join(unavailable)
        )
    return MaptekCapabilities(
        project_filename=captured["project_filename"],
        split_activity=captured["split_activity"],
        set_start_of_bar=captured["set_start_of_bar"],
        set_pause_ui=captured["set_pause_ui"],
        update_ui=captured["update_ui"],
        save=captured["save"],
        get_number_bars=captured["get_number_bars"],
    )


def bind_maptek_apply_session(
    project: object, plan: MaptekOperationPlan, state: ProjectState,
) -> MaptekApplySession:
    """Bind a canonical plan snapshot to one exact live object for one attempt."""
    snapshot = _canonical_plan_snapshot(plan, state)
    prepared = _prepare_operations(snapshot)
    capabilities = probe_maptek_capabilities(project)
    live_filename = capabilities.project_filename(True)
    if type(live_filename) is not str or not live_filename:
        raise CapabilityError("project_filename returned no exact project path")
    live_path = _canonical_live_project_path(
        live_filename, snapshot.source_project_path,
    )
    if live_path != snapshot.source_project_path:
        raise PreflightError("live project path differs from the planned project path")
    live_contract = _build_live_bar_count_contract(
        snapshot, prepared, capabilities.get_number_bars,
    )
    session = MaptekApplySession(_SESSION_CONSTRUCTOR_TOKEN)
    with _SESSION_LOCK:
        _SESSIONS[session] = _SessionRecord(
            project=project,
            capabilities=capabilities,
            prepared=prepared,
            live_contract=live_contract,
        )
    return session


def _freeze_compatibility_evidence(
    evidence: Mapping[str, Mapping[str, object]],
) -> Mapping[str, Mapping[str, object]]:
    try:
        supplied = dict(evidence)
    except (TypeError, ValueError) as error:
        raise PreflightError("compatibility evidence is invalid") from error
    frozen: dict[str, Mapping[str, object]] = {}
    for activity_id, item in supplied.items():
        if (
            type(activity_id) is not str
            or not activity_id.isascii()
            or not activity_id.isdecimal()
            or activity_id == "0"
            or activity_id.startswith("0")
            or not isinstance(item, Mapping)
        ):
            raise PreflightError("compatibility evidence is invalid")
        values = dict(item)
        if (
            set(values) != {"rule", "initial", "final"}
            or values["rule"] != "percent_complete_hidden_tail"
            or type(values["initial"]) is not int
            or values["initial"] <= 0
            or type(values["final"]) is not int
            or values["final"] < values["initial"]
        ):
            raise PreflightError("compatibility evidence is invalid")
        frozen[activity_id] = MappingProxyType(values)
    return MappingProxyType(frozen)


def _build_live_bar_count_contract(
    plan: MaptekOperationPlan,
    prepared: tuple[_PreparedSplitCall | _PreparedMoveCall, ...],
    get_number_bars: Callable[..., Any],
) -> _LiveBarCountContract:
    raw_counts = {
        activity_id: get_number_bars(activity_id)
        for activity_id in plan.initial_bar_counts
    }
    observed = {
        activity_id: _parse_live_count(raw_counts[activity_id], activity_id, expected)
        for activity_id, expected in plan.initial_bar_counts.items()
    }
    mismatches = tuple(
        activity_id
        for activity_id, expected in plan.initial_bar_counts.items()
        if observed[activity_id] != expected
    )
    if mismatches:
        activity_id = mismatches[0]
        raise LiveBarCountMismatch(
            activity_id,
            plan.initial_bar_counts[activity_id],
            observed[activity_id],
        )

    current = dict(observed)
    operation_expected_counts: list[int] = []
    for call in prepared:
        activity_id = call.operation.activity_id
        operation_expected_counts.append(current[activity_id])
        if type(call) is _PreparedSplitCall:
            current[activity_id] += 1
    contract = _LiveBarCountContract(
        initial_bar_counts=observed,
        operation_expected_counts=tuple(operation_expected_counts),
        final_bar_counts=current,
        compatibility_evidence={},
    )
    _validate_live_bar_count_contract(contract, prepared)
    return contract


def _validate_live_bar_count_contract(
    contract: _LiveBarCountContract,
    prepared: tuple[_PreparedSplitCall | _PreparedMoveCall, ...],
) -> None:
    if (
        type(contract) is not _LiveBarCountContract
        or type(contract.initial_bar_counts) is not _MAPPING_PROXY_TYPE
        or type(contract.operation_expected_counts) is not tuple
        or type(contract.final_bar_counts) is not _MAPPING_PROXY_TYPE
        or type(contract.compatibility_evidence) is not _MAPPING_PROXY_TYPE
        or len(contract.operation_expected_counts) != len(prepared)
    ):
        raise PreflightError("live bar count contract is invalid")
    initial = dict(contract.initial_bar_counts)
    final = dict(contract.final_bar_counts)
    if set(initial) != set(final):
        raise PreflightError("live bar count contract is invalid")
    for activity_id, count in initial.items():
        if (
            type(activity_id) is not int
            or activity_id <= 0
            or type(count) is not int
            or count <= 0
            or type(final[activity_id]) is not int
            or final[activity_id] <= 0
        ):
            raise PreflightError("live bar count contract is invalid")
    current = dict(initial)
    for call, expected in zip(prepared, contract.operation_expected_counts):
        if (
            type(call) not in (_PreparedSplitCall, _PreparedMoveCall)
            or type(expected) is not int
            or expected <= 0
            or call.operation.activity_id not in current
            or expected != current[call.operation.activity_id]
        ):
            raise PreflightError("live bar count contract is invalid")
        if type(call) is _PreparedSplitCall:
            current[call.operation.activity_id] += 1
    if current != final:
        raise PreflightError("live bar count contract is invalid")
    for activity_id, evidence in contract.compatibility_evidence.items():
        live_id = int(activity_id)
        if (
            type(evidence) is not _MAPPING_PROXY_TYPE
            or live_id not in initial
            or evidence["initial"] != initial[live_id]
            or evidence["final"] != final[live_id]
        ):
            raise PreflightError("live bar count contract is invalid")


def _canonical_live_project_path(filename: str, expected_path: Path) -> Path:
    """Resolve VGS's exact bare active name only below the planned directory."""
    raw = _strip_extended_windows_prefix(filename)
    if not os.path.isabs(raw):
        if os.path.basename(raw) != raw or raw != expected_path.name:
            raise PreflightError("live project path differs from the planned project path")
        raw = os.path.join(os.fspath(expected_path.parent), raw)
    try:
        return Path(raw).resolve()
    except (OSError, RuntimeError, ValueError) as error:
        raise CapabilityError("project_filename returned an invalid project path") from error


def _strip_extended_windows_prefix(value: str) -> str:
    folded = value.casefold()
    if folded.startswith("\\\\?\\unc\\"):
        return "\\\\" + value[8:]
    if folded.startswith("\\\\?\\"):
        return value[4:]
    return value


def apply_maptek_operations(
    session: MaptekApplySession, *, refresh_ui: bool = True,
    manage_ui: bool = True,
) -> MaptekApplyLog:
    """Consume one exact bound session; success and failure are both terminal.

    ``refresh_ui=False`` is for a transaction that must persist the archive
    before asking VGS to clear and rebuild a potentially large chart.
    ``manage_ui=False`` performs only the bound split/move calls; durable
    Script 9 uses it to avoid a global VGS grid pause/rebuild.
    """
    if type(session) is not MaptekApplySession:
        raise PreflightError("an exact bound MaptekApplySession is required")
    with _SESSION_LOCK:
        record = _SESSIONS.get(session)
        if record is None:
            raise PreflightError("Maptek apply session is forged or reconstructed")
        if record.status != "fresh":
            raise PreflightError("Maptek apply session is already consumed")
        record.status = "applying"

    capabilities = record.capabilities
    if not _capabilities_belong_to_project(capabilities, record.project):
        with _SESSION_LOCK:
            record.status = "failed"
        raise PreflightError("Maptek apply session project identity changed")
    try:
        _validate_live_bar_count_contract(record.live_contract, record.prepared)
    except BaseException:
        with _SESSION_LOCK:
            record.status = "failed"
        raise
    applied = 0
    final_counts: dict[int, int] = {}
    operation_error: BaseException | None = None
    resume_error: BaseException | None = None
    refresh_error: BaseException | None = None
    if record.prepared:
        try:
            if manage_ui is True:
                if capabilities.set_pause_ui(True) is not True:
                    raise ApplyError("Maptek rejected set_pause_ui pause operation")
            for operation_index, call in enumerate(record.prepared):
                operation = call.operation
                _require_live_count(
                    capabilities.get_number_bars, operation.activity_id,
                    record.live_contract.operation_expected_counts[operation_index],
                )
                if type(call) is _PreparedSplitCall:
                    result = capabilities.split_activity(
                        operation.activity_id, operation.task_bar_index,
                        call.proportion, call.delay_days,
                    )
                else:
                    result = capabilities.set_start_of_bar(
                        call.bar_reference, call.target_start
                    )
                if result is not True:
                    raise ApplyError(f"Maptek rejected {operation.kind} operation")
                applied += 1
                if type(call) is _PreparedSplitCall:
                    _require_live_count(
                        capabilities.get_number_bars, operation.activity_id,
                        record.live_contract.operation_expected_counts[
                            operation_index
                        ] + 1,
                    )

            for activity_id, expected in record.live_contract.final_bar_counts.items():
                final_counts[activity_id] = _require_live_count(
                    capabilities.get_number_bars, activity_id, expected
                )
        except BaseException as error:
            operation_error = error
        finally:
            if manage_ui is True:
                try:
                    if capabilities.set_pause_ui(False) is not True:
                        raise ApplyError("Maptek rejected set_pause_ui resume operation")
                except BaseException as error:
                    resume_error = error
        if (
            manage_ui is True
            and refresh_ui is True
            and operation_error is None
            and resume_error is None
        ):
            try:
                if capabilities.update_ui() is not True:
                    raise ApplyError("Maptek rejected update_ui refresh operation")
            except BaseException as error:
                refresh_error = error

    try:
        if operation_error is not None:
            if resume_error is not None:
                raise operation_error.with_traceback(
                    operation_error.__traceback__
                ) from resume_error
            raise operation_error.with_traceback(operation_error.__traceback__)
        if resume_error is not None:
            raise resume_error.with_traceback(resume_error.__traceback__)
        if refresh_error is not None:
            raise refresh_error.with_traceback(refresh_error.__traceback__)
    except BaseException:
        with _SESSION_LOCK:
            record.status = "failed"
        raise
    with _SESSION_LOCK:
        record.status = "succeeded"
    return MaptekApplyLog(
        applied, final_counts, record.live_contract.compatibility_evidence,
    )


def _capabilities_belong_to_project(
    capabilities: MaptekCapabilities, project: object,
) -> bool:
    return all(
        type(method) is MethodType and method.__self__ is project
        for method in (
            capabilities.project_filename, capabilities.split_activity,
            capabilities.set_start_of_bar, capabilities.set_pause_ui,
            capabilities.update_ui, capabilities.save,
            capabilities.get_number_bars,
        )
    )


def _is_vgs_last_key_transfer(
    expected_rows: list[BarIndexEvidence],
    current_bars: tuple[NativeBar, ...],
    item: BarIndexEvidence,
    source: NativeBar,
    bar: NativeBar,
) -> bool:
    """Recognize VGS's persisted re-keying after one or more `LAST` splits.

    VGS can serialize the original left descendant as ``ACT.n`` and move
    ``LAST`` to the final descendant.  This is accepted only after planned
    contiguous splits of a native ``LAST`` bar; every schedule semantic is
    checked by the caller before this structural-key exception is considered.
    """
    siblings = sorted(
        (
            row for row in expected_rows
            if (
                row.native_bar_index == item.native_bar_index
                and row.native_bar_xml_key == item.native_bar_xml_key
                and row.native_bar_key == item.native_bar_key
            )
        ),
        key=lambda row: row.native_descendant_order,
    )
    if (
        source.bar_key != "LAST"
        or len(siblings) < 2
        or item != siblings[0]
        or tuple(row.native_descendant_order for row in siblings) != tuple(range(len(siblings)))
        or not bar.bar_key.startswith("ACT.")
    ):
        return False
    suffix = bar.bar_key[4:]
    right = siblings[-1]
    return (
        suffix.isascii()
        and suffix.isdecimal()
        and suffix[0] != "0"
        and right.final_bar_index <= len(current_bars)
        and current_bars[right.final_bar_index - 1].bar_key == "LAST"
    )


def verify_maptek_result(
    plan: MaptekOperationPlan, reread_state: ProjectState,
) -> ValidationReport:
    """Verify Task 4's archive reread against the exact planned result.

    The allowed live Maptek surface has no calendar readback.  Therefore a
    transaction must save the live project, reread its temporary archive as a
    ``ProjectState``, call this function, and publish only when ``report.ok``.
    """
    snapshot = _canonical_plan_snapshot(plan, plan.source_state)
    if type(reread_state) is not ProjectState:
        return ValidationReport((ValidationDifference(
            "reread_state_type_invalid", "",
            "Task 4 must supply an exact core ProjectState archive reread",
        ),))

    before = snapshot.source_state
    differences: list[ValidationDifference] = []
    if before.date_format != reread_state.date_format:
        differences.append(ValidationDifference(
            "date_format_changed", "", "project date format changed",
        ))
    if not _completion_cutoff_progress_is_valid(
        before.completion_cutoff, reread_state.completion_cutoff,
    ):
        differences.append(ValidationDifference(
            "completion_cutoff_retreated", "", "completion cutoff retreated",
        ))
    completion_advanced = (
        before.completion_cutoff is not None
        and reread_state.completion_cutoff is not None
        and reread_state.completion_cutoff > before.completion_cutoff
    )
    try:
        precedence_matches = _precedence_matches_after_authorized_splits(
            snapshot, before, reread_state,
        )
    except ValueError as error:
        differences.append(ValidationDifference(
            "reread_state_invalid", "", f"archive reread is invalid: {error}",
        ))
        precedence_matches = False
    if not precedence_matches:
        differences.append(ValidationDifference(
            "precedence_changed", "", "precedence semantics changed",
        ))

    before_ids = set(before.activities)
    after_ids = set(reread_state.activities)
    for activity_id in sorted(before_ids - after_ids):
        differences.append(ValidationDifference(
            "activity_removed", activity_id, "activity is missing",
        ))
    for activity_id in sorted(after_ids - before_ids):
        differences.append(ValidationDifference(
            "activity_added", activity_id, "unexpected activity exists",
        ))

    affected_ids = {str(activity_id) for activity_id in snapshot.final_bar_counts}
    for activity_id in sorted((before_ids & after_ids) - affected_ids):
        if _activity_schedule_semantics(
            before.activities[activity_id]
        ) != _activity_schedule_semantics(reread_state.activities[activity_id]):
            differences.append(ValidationDifference(
                "collateral_activity_changed", activity_id,
                "activity was not authorized to change",
            ))

    redistributable_groups = _redistributable_native_quantity_groups(snapshot)
    for activity_id in sorted(affected_ids & before_ids & after_ids):
        live_id = int(activity_id)
        prior = before.activities[activity_id]
        current = reread_state.activities[activity_id]
        if current.activity_id != activity_id:
            differences.append(ValidationDifference(
                "activity_identity_changed", activity_id, "activity identity changed",
            ))
        if (
            not completion_advanced
            and current.overall_percent_complete != prior.overall_percent_complete
        ):
            differences.append(ValidationDifference(
                "completion_changed", activity_id,
                "overall activity completion changed",
            ))

        expected_rows = sorted(
            (item for item in snapshot.bar_index_evidence
             if item.activity_id == live_id),
            key=lambda item: item.final_bar_index,
        )
        expected_count = snapshot.final_bar_counts[live_id]
        strict_pairs: tuple[tuple[BarIndexEvidence, NativeBar], ...]
        if completion_advanced:
            cutoff = reread_state.completion_cutoff
            assert cutoff is not None
            strict_expected = tuple(
                item for item in expected_rows if item.target_finish > cutoff
            )
            strict_current = tuple(
                bar for bar in current.bars if bar.finish > cutoff
            )
            if len(strict_current) != len(strict_expected):
                differences.append(ValidationDifference(
                    "final_bar_count_mismatch", activity_id,
                    "expected " + str(len(strict_expected))
                    + " pending bars, reread " + str(len(strict_current)),
                ))
            strict_pairs = tuple(zip(strict_expected, strict_current))
        else:
            if len(current.bars) != expected_count:
                differences.append(ValidationDifference(
                    "final_bar_count_mismatch", activity_id,
                    f"expected {expected_count} bars, reread {len(current.bars)}",
                ))
            strict_pairs = tuple(zip(expected_rows, current.bars))
        for item, bar in strict_pairs:
            source = prior.bars[item.native_bar_index]
            intervals_match = _same_vgs_calendar_interval(
                bar.start, bar.finish, item.target_start, item.target_finish,
            ) or (
                completion_advanced
                and _same_vgs_progress_boundary_interval(
                    bar.start, bar.finish, item.target_start, item.target_finish,
                    reread_state.completion_cutoff,
                )
            )
            if not intervals_match:
                differences.append(ValidationDifference(
                    "calendar_interval_mismatch", activity_id,
                    f"bar {item.final_bar_index} does not match its authorized interval",
                ))
            if (
                not completion_advanced
                and (bar.percent_complete != source.percent_complete
                     or bar.complete_worked_minutes != source.complete_worked_minutes)
            ):
                differences.append(ValidationDifference(
                    "completion_changed", activity_id,
                    f"bar {item.final_bar_index} completion changed",
                ))
            if (
                item.native_descendant_order == 0
                and bar.bar_key != source.bar_key
                and not _is_vgs_last_key_transfer(
                    expected_rows, current.bars, item, source, bar,
                )
            ):
                differences.append(ValidationDifference(
                    "bar_key_mismatch", activity_id,
                    f"bar {item.final_bar_index} structural key changed",
                ))

        for group in _unique_quantity_groups(
            redistributable_groups, live_id
        ):
            if completion_advanced:
                cutoff = reread_state.completion_cutoff
                assert cutoff is not None
                if any(item.target_finish <= cutoff for item in group):
                    continue
                actual_by_index = {
                    item.final_bar_index: bar for item, bar in strict_pairs
                }
                if any(item.final_bar_index not in actual_by_index for item in group):
                    continue
            elif any(item.final_bar_index > len(current.bars) for item in group):
                continue
            expected_total = sum(
                (
                    prior.bars[native_index].quantity_percentage
                    for native_index in {
                        item.native_bar_index for item in group
                    }
                ),
                Decimal("0"),
            )
            actual_total = sum(
                (
                    actual_by_index[item.final_bar_index].quantity_percentage
                    if completion_advanced
                    else current.bars[item.final_bar_index - 1].quantity_percentage
                    for item in group
                ),
                Decimal("0"),
            )
            if not _same_vgs_group_quantity(actual_total, expected_total):
                indices = ",".join(
                    str(item.final_bar_index) for item in group
                )
                differences.append(ValidationDifference(
                    "native_group_quantity_mismatch", activity_id,
                    "bars " + indices + " quantity total changed "
                    + "(expected " + str(expected_total)
                    + ", reread " + str(actual_total) + ")",
                ))

    return ValidationReport(tuple(differences))


def _completion_cutoff_progress_is_valid(
    before: datetime | None, after: datetime | None,
) -> bool:
    """Accept an unchanged completion context or a monotonic advance."""
    if before is None:
        return after is None
    return after is not None and after >= before


def _same_vgs_group_quantity(actual: Decimal, expected: Decimal) -> bool:
    """Accept only VGS's insignificant archive Decimal shortening.

    A physical native group can be rebalanced by VGS while it is saved. Its
    total remains protected, with this bounded exception for the tiny decimal
    tail VGS removes during XML serialization.
    """
    return (
        actual.is_finite()
        and expected.is_finite()
        and abs(actual - expected) <= _VGS_GROUP_QUANTITY_SERIALIZATION_TOLERANCE
    )


def _same_vgs_calendar_interval(
    actual_start: datetime,
    actual_finish: datetime,
    expected_start: datetime,
    expected_finish: datetime,
) -> bool:
    """Allow VGS's one-second delayed-split serialization boundary."""
    return (
        abs(actual_start - expected_start)
        <= _VGS_SPLIT_CALENDAR_SERIALIZATION_TOLERANCE
        and abs(actual_finish - expected_finish)
        <= _VGS_SPLIT_CALENDAR_SERIALIZATION_TOLERANCE
    )


def _same_vgs_progress_boundary_interval(
    actual_start: datetime,
    actual_finish: datetime,
    expected_start: datetime,
    expected_finish: datetime,
    completion_cutoff: datetime | None,
) -> bool:
    """Accept VGS' bounded pre-cutoff carry on the sole crossing schedule bar."""
    return (
        completion_cutoff is not None
        and expected_start == completion_cutoff
        and actual_start < completion_cutoff
        and completion_cutoff - actual_start
        <= _VGS_PROGRESS_BOUNDARY_SERIALIZATION_TOLERANCE
        and abs(actual_finish - expected_finish)
        <= _VGS_SPLIT_CALENDAR_SERIALIZATION_TOLERANCE
    )


def _precedence_matches_after_authorized_splits(
    plan: MaptekOperationPlan,
    before: ProjectState,
    after: ProjectState,
) -> bool:
    """Accept only an authorized split source rebinding to its terminal child."""
    if precedence_fingerprint(before) == precedence_fingerprint(after):
        return True
    before_links = {link.link_key: link for link in before.links}
    after_links = {link.link_key: link for link in after.links}
    if set(before_links) != set(after_links):
        return False
    for key, prior in before_links.items():
        current = after_links[key]
        if (
            prior.link_key != current.link_key
            or prior.predecessor_id != current.predecessor_id
            or prior.successor_id != current.successor_id
            or prior.precedence_type != current.precedence_type
            or prior.lag != current.lag
            or prior.group_key != current.group_key
        ):
            return False
        for activity_id, prior_bar, current_bar in (
            (prior.predecessor_id, prior.predecessor_bar, current.predecessor_bar),
            (prior.successor_id, prior.successor_bar, current.successor_bar),
        ):
            if prior_bar == current_bar:
                continue
            if not _is_terminal_split_lineage_rebind(
                plan, after, activity_id, prior_bar, current_bar,
            ):
                return False
    return True


def _is_terminal_split_lineage_rebind(
    plan: MaptekOperationPlan,
    after: ProjectState,
    activity_id: str,
    source_bar_key: str,
    current_bar_key: str,
) -> bool:
    """Return whether a link endpoint names its exact saved terminal descendant."""
    try:
        live_id = int(activity_id)
        activity = after.activities[activity_id]
    except (KeyError, ValueError):
        return False
    sibling_groups: dict[str, list[BarIndexEvidence]] = {}
    for item in plan.bar_index_evidence:
        if item.activity_id != live_id or item.native_bar_key != source_bar_key:
            continue
        sibling_groups.setdefault(item.native_bar_xml_key, []).append(item)
    for siblings in sibling_groups.values():
        if len(siblings) < 2:
            continue
        terminal = max(siblings, key=lambda item: item.native_descendant_order)
        matches = [
            bar for bar in activity.bars
            if (
                _same_vgs_calendar_interval(
                    bar.start, bar.finish, terminal.target_start, terminal.target_finish,
                )
                or _same_vgs_progress_boundary_interval(
                    bar.start, bar.finish, terminal.target_start, terminal.target_finish,
                    after.completion_cutoff,
                )
            )
        ]
        if len(matches) == 1 and matches[0].bar_key == current_bar_key:
            return True
    return False


def _activity_schedule_semantics(activity: NativeActivity) -> tuple[object, ...]:
    """Return the persisted scheduling state that VGS must not silently alter."""
    return (
        activity.activity_id,
        activity.overall_percent_complete,
        tuple(
            (
                bar.bar_key,
                bar.start,
                bar.finish,
                bar.quantity_percentage,
                bar.percent_complete,
                bar.complete_worked_minutes,
            )
            for bar in activity.bars
        ),
    )


def _redistributable_native_quantity_groups(
    plan: MaptekOperationPlan,
) -> dict[tuple[int, int], tuple[BarIndexEvidence, ...]]:
    """Return VGS-rebalanced source groups, merging overlapping native moves."""
    evidence_by_activity: dict[int, tuple[BarIndexEvidence, ...]] = {}
    for live_id in plan.final_bar_counts:
        evidence_by_activity[live_id] = tuple(sorted(
            (item for item in plan.bar_index_evidence if item.activity_id == live_id),
            key=lambda item: item.final_bar_index,
        ))
    group_indices: dict[int, list[set[int]]] = {}
    for live_id, evidence in evidence_by_activity.items():
        by_native: dict[int, set[int]] = {}
        for item in evidence:
            by_native.setdefault(item.native_bar_index, set()).add(item.final_bar_index)
        group_indices[live_id] = list(by_native.values())
    for operation in plan.operations:
        if type(operation) is not MoveOperation:
            continue
        logical_unit_ids = frozenset(operation.logical_unit_ids)
        group = {
            item.final_bar_index for item in evidence_by_activity[operation.activity_id]
            if (
                item.logical_unit_id in logical_unit_ids
                and item.target_start - item.original_start
                == timedelta(days=operation.delta_days)
            )
        }
        if len(group) < 2:
            continue
        merged = group_indices[operation.activity_id]
        overlaps = [existing for existing in merged if existing & group]
        for existing in overlaps:
            merged.remove(existing)
            group.update(existing)
        merged.append(group)
    groups: dict[tuple[int, int], tuple[BarIndexEvidence, ...]] = {}
    for live_id, index_groups in group_indices.items():
        evidence_by_index = {
            item.final_bar_index: item for item in evidence_by_activity[live_id]
        }
        for indices in index_groups:
            group = tuple(sorted(
                (evidence_by_index[index] for index in indices),
                key=lambda item: item.final_bar_index,
            ))
            for item in group:
                groups[(live_id, item.final_bar_index)] = group
    return groups


def _unique_quantity_groups(
    groups: Mapping[tuple[int, int], tuple[BarIndexEvidence, ...]],
    activity_id: int,
) -> tuple[tuple[BarIndexEvidence, ...], ...]:
    unique: dict[tuple[int, ...], tuple[BarIndexEvidence, ...]] = {}
    for (live_id, _), group in groups.items():
        if live_id == activity_id:
            unique[tuple(item.final_bar_index for item in group)] = group
    return tuple(unique.values())


def _canonical_plan_snapshot(
    plan: MaptekOperationPlan, state: ProjectState,
) -> MaptekOperationPlan:
    if type(plan) is not MaptekOperationPlan:
        raise PreflightError("plan must be an exact MaptekOperationPlan")
    if type(state) is not ProjectState:
        raise PreflightError("authoritative state must be an exact ProjectState")
    try:
        state_path = Path(state.path).resolve()
        state_project_fingerprint = project_fingerprint(state)
        state_precedence_fingerprint = precedence_fingerprint(state)
    except (OSError, RuntimeError, TypeError, ValueError) as error:
        raise PreflightError("authoritative ProjectState is invalid") from error
    if state_path != plan.source_project_path:
        raise PreflightError("authoritative state project path differs from plan data")
    if state_project_fingerprint != plan.source_project_fingerprint:
        raise PreflightError("authoritative state project fingerprint differs")
    if state_precedence_fingerprint != plan.source_precedence_fingerprint:
        raise PreflightError("authoritative state precedence fingerprint differs")
    package = plan.source_package
    if (type(package) is not ApplyPackage
            or package.project_fingerprint != state_project_fingerprint
            or package.precedence_fingerprint != state_precedence_fingerprint):
        raise PreflightError("Task 1 package fingerprints differ from plan data")
    rebuilt = _build_maptek_operation_plan(package, state)
    if not _same_plan_value(plan, rebuilt):
        raise PreflightError("operation plan differs from canonical Task 1 data")
    return rebuilt


def _same_plan_value(
    left: MaptekOperationPlan, right: MaptekOperationPlan
) -> bool:
    return (
        left.operations == right.operations
        and dict(left.initial_bar_counts) == dict(right.initial_bar_counts)
        and dict(left.final_bar_counts) == dict(right.final_bar_counts)
        and left.bar_index_evidence == right.bar_index_evidence
        and left.source_package == right.source_package
        and left.source_state == right.source_state
        and left.source_project_path == right.source_project_path
        and left.source_project_fingerprint == right.source_project_fingerprint
        and left.source_precedence_fingerprint == right.source_precedence_fingerprint
    )


def _report_summary(report: ValidationReport) -> str:
    return "; ".join(
        f"{item.code}[{item.activity_id}]: {item.detail}"
        for item in report.differences
    ) or "apply package validation failed"


def _maptek_activity_id(value: str) -> int:
    if type(value) is not str or not value.isascii() or not value.isdecimal():
        raise PreflightError("activity ID is not an exact Maptek integer")
    converted = int(value)
    if converted <= 0 or str(converted) != value:
        raise PreflightError("activity ID is not a canonical positive Maptek integer")
    return converted


def _fragments_by_native_bar(
    activity: ApplyActivity, native_bars: tuple[NativeBar, ...]
) -> dict[tuple[str, str], tuple[ApplyFragment, ...]]:
    grouped: dict[tuple[str, str], list[ApplyFragment]] = {}
    for fragment in activity.fragments:
        grouped.setdefault(
            (fragment.native_bar_xml_key, fragment.native_bar_key), []
        ).append(fragment)
    result: dict[tuple[str, str], tuple[ApplyFragment, ...]] = {}
    for native_bar in native_bars:
        key = (native_bar.xml_key, native_bar.bar_key)
        fragments = tuple(sorted(
            grouped[key],
            key=lambda item: (item.original_start, item.original_finish, item.id),
        ))
        result[key] = fragments
    return result


def _fragment_civil_delta(fragment: ApplyFragment, native_bar_key: str) -> timedelta:
    delta = fragment.start - fragment.original_start
    if delta.seconds or delta.microseconds:
        raise PreflightError(
            f"native bar {native_bar_key} has a non-civil displacement"
        )
    return delta


def _run_boundaries(
    fragments: tuple[ApplyFragment, ...], native_bar: NativeBar,
    completion_cutoff: datetime | None = None,
) -> tuple[datetime, ...]:
    if not fragments:
        return ()
    if fragments[0].original_start != native_bar.start:
        raise PreflightError("native bar fragments do not start at the native bar start")
    if fragments[-1].original_finish != native_bar.finish:
        raise PreflightError("native bar fragments do not finish at the native bar finish")
    boundaries: list[datetime] = []
    previous_delta = _fragment_civil_delta(fragments[0], native_bar.bar_key)
    for left, right in zip(fragments, fragments[1:]):
        if left.original_finish != right.original_start:
            raise PreflightError("native bar fragments are not an exact contiguous timeline")
        current_delta = _fragment_civil_delta(right, native_bar.bar_key)
        boundary = right.original_start
        if (
            current_delta != previous_delta
            and boundary.time() == time.min
            and native_bar.start < boundary < native_bar.finish
        ):
            boundaries.append(boundary)
        previous_delta = current_delta
    if len(set(boundaries)) != len(boundaries):
        raise PreflightError("run split boundaries are ambiguous")
    return tuple(boundaries)


def _split_segments(
    fragments: tuple[ApplyFragment, ...], boundaries: tuple[datetime, ...],
) -> tuple[tuple[ApplyFragment, ...], ...]:
    segments: list[list[ApplyFragment]] = [[]]
    boundary_set = set(boundaries)
    for position, fragment in enumerate(fragments):
        if position and fragment.original_start in boundary_set:
            segments.append([])
        segments[-1].append(fragment)
    return tuple(tuple(segment) for segment in segments)


def _delay_days(left: ApplyFragment, right: ApplyFragment) -> Decimal:
    gap = right.start - left.finish
    microseconds = (
        (gap.days * 86_400 + gap.seconds) * 1_000_000 + gap.microseconds
    )
    if microseconds < 0:
        raise PreflightError("STP split target overlaps and cannot use a VGS delay")
    return Decimal(microseconds) / Decimal(86_400_000_000)


def _native_split_specs(
    native_bars: tuple[NativeBar, ...],
    fragments_by_bar: Mapping[tuple[str, str], tuple[ApplyFragment, ...]],
    completion_cutoff: datetime | None,
) -> tuple[tuple[_NativeSplitSpec, ...], dict[tuple[int, datetime], int]]:
    """Build left-to-right VGS splits whose delays create the STP gaps."""
    live_count = len(native_bars)
    inserted_before = 0
    specs: list[_NativeSplitSpec] = []
    child_indices: dict[tuple[int, datetime], int] = {}
    for native_index, native_bar in enumerate(native_bars):
        fragments = fragments_by_bar[(native_bar.xml_key, native_bar.bar_key)]
        if not _fragments_changed(fragments):
            continue
        boundaries = _run_boundaries(fragments, native_bar, completion_cutoff)
        if not boundaries:
            continue
        segments = _split_segments(fragments, boundaries)
        left_start = native_bar.start
        task_bar_index = native_index + inserted_before
        for left, right in zip(segments, segments[1:]):
            boundary = right[0].original_start
            with localcontext() as context:
                context.prec = 50
                proportion = (
                    _duration_decimal(boundary - left_start)
                    / _duration_decimal(native_bar.finish - left_start)
                )
            if not Decimal("0") < proportion < Decimal("1"):
                raise PreflightError("daily split has an unsafe proportion")
            child_one_based_index = task_bar_index + 2
            specs.append(_NativeSplitSpec(
                native_index=native_index,
                native_bar=native_bar,
                boundary=boundary,
                proportion=proportion,
                delay_days=_delay_days(left[-1], right[0]),
                task_bar_index=task_bar_index,
                expected_bar_count=live_count,
                child_one_based_index=child_one_based_index,
            ))
            child_indices[(native_index, boundary)] = child_one_based_index
            task_bar_index += 1
            inserted_before += 1
            live_count += 1
            left_start = boundary
    return tuple(specs), child_indices


def _index_evidence(
    activity_id: int,
    native_bars: tuple[NativeBar, ...],
    fragments_by_bar: Mapping[tuple[str, str], tuple[ApplyFragment, ...]],
    completion_cutoff: datetime | None = None,
    *,
    split_child_indices: Mapping[tuple[int, datetime], int] | None = None,
) -> tuple[tuple[BarIndexEvidence, ...], dict[str, int]]:
    evidence: list[BarIndexEvidence] = []
    fragment_indices: dict[str, int] = {}
    logical_unit_orders: dict[str, int] = {}
    child_indices = split_child_indices or {}
    inserted_before = 0
    for native_index, native_bar in enumerate(native_bars):
        fragments = fragments_by_bar[(native_bar.xml_key, native_bar.bar_key)]
        boundaries = (
            _run_boundaries(fragments, native_bar, completion_cutoff)
            if _fragments_changed(fragments) else ()
        )
        segments: list[list[ApplyFragment]] = [[]]
        boundary_set = set(boundaries)
        for position, fragment in enumerate(fragments):
            if position and fragment.original_start in boundary_set:
                segments.append([])
            segments[-1].append(fragment)
        base_one_based_index = native_index + 1 + inserted_before
        for descendant_order, segment in enumerate(segments):
            final_index = base_one_based_index + descendant_order
            if descendant_order:
                child_index = child_indices.get((
                    native_index, segment[0].original_start,
                ))
                if child_index is None:
                    raise PreflightError(
                        "missing physical index for split child of activity "
                        f"{activity_id!r}, native bar {native_bar.bar_key!r} at "
                        f"{segment[0].original_start.isoformat()}"
                    )
                if child_index != final_index:
                    raise PreflightError(
                        "split child physical index does not match immediate "
                        f"insertion for activity {activity_id!r}, native bar "
                        f"{native_bar.bar_key!r} at "
                        f"{segment[0].original_start.isoformat()}"
                    )
            logical_unit_id = segment[0].logical_unit_id
            logical_unit_order = logical_unit_orders.get(logical_unit_id, 0)
            logical_unit_orders[logical_unit_id] = logical_unit_order + 1
            item = BarIndexEvidence(
                activity_id=activity_id,
                native_bar_xml_key=native_bar.xml_key,
                native_bar_key=native_bar.bar_key,
                native_bar_index=native_index,
                final_bar_index=final_index,
                native_descendant_order=descendant_order,
                logical_unit_id=logical_unit_id,
                logical_unit_order=logical_unit_order,
                fragment_ids=tuple(fragment.id for fragment in segment),
                provenance=tuple(fragment.provenance for fragment in segment),
                original_start=segment[0].original_start,
                original_finish=segment[-1].original_finish,
                target_start=segment[0].start,
                target_finish=segment[-1].finish,
            )
            evidence.append(item)
            for fragment in segment:
                fragment_indices[fragment.id] = final_index
        inserted_before += len(segments) - 1
    return tuple(evidence), fragment_indices


def _fragments_changed(fragments: tuple[ApplyFragment, ...]) -> bool:
    return any(
        fragment.start != fragment.original_start
        or fragment.finish != fragment.original_finish
        for fragment in fragments
    )


def _moves_for_activity(
    activity_id: int,
    activity: ApplyActivity,
    native_bars: tuple[NativeBar, ...],
    fragments_by_bar: Mapping[tuple[str, str], tuple[ApplyFragment, ...]],
    fragment_indices: Mapping[str, int],
    final_count: int,
    evidence: tuple[BarIndexEvidence, ...],
    split_native_indices: frozenset[int],
) -> tuple[MoveOperation, ...]:
    del activity, fragments_by_bar, fragment_indices
    operations: list[MoveOperation] = []
    for native_index in sorted(split_native_indices):
        matches = sorted(
            (item for item in evidence if item.native_bar_index == native_index),
            key=lambda item: item.native_descendant_order,
        )
        if not matches:
            raise PreflightError("split native bar has no calendar evidence")
        native_bar = native_bars[native_index]
        for item in matches:
            operation = _move_for_split_descendant(
                activity_id, native_bar, native_index, final_count, item,
            )
            if operation is not None:
                operations.append(operation)
    remaining = tuple(
        item for item in evidence if item.native_bar_index not in split_native_indices
    )
    if not any(
        provenance in ("direct", "cascade")
        for item in remaining
        for provenance in item.provenance
    ):
        return tuple(operations)
    operations.extend(_existing_native_moves(
        activity_id, native_bars, final_count, remaining,
    ))
    return tuple(operations)


def _move_for_split_descendant(
    activity_id: int,
    native_bar: NativeBar,
    native_index: int,
    final_count: int,
    item: BarIndexEvidence,
) -> MoveOperation | None:
    """Return the exact target move for one changed physical split child."""
    delta = item.target_start - item.original_start
    if delta.seconds or delta.microseconds:
        raise PreflightError("split descendant has a non-civil displacement")
    if delta == timedelta(0):
        return None
    return MoveOperation(
        activity_id=activity_id,
        native_bar_xml_key=native_bar.xml_key,
        native_bar_key=native_bar.bar_key,
        native_bar_index=native_index,
        one_based_bar_indices=(item.final_bar_index,),
        logical_unit_ids=(item.logical_unit_id,),
        provenance=item.provenance,
        target_start=item.target_start,
        delta_days=delta.days,
        expected_bar_count=final_count,
    )


def _existing_native_moves(
    activity_id: int,
    native_bars: tuple[NativeBar, ...],
    final_count: int,
    evidence: tuple[BarIndexEvidence, ...] | list[BarIndexEvidence],
) -> tuple[MoveOperation, ...]:
    """Emit one VGS move for a contiguous equal-shift native-bar group."""
    items_by_native_index = {
        native_index: sorted(
            (item for item in evidence if item.native_bar_index == native_index),
            key=lambda item: item.native_descendant_order,
        )
        for native_index in range(len(native_bars))
    }
    operations: list[MoveOperation] = []
    current: list[BarIndexEvidence] = []
    current_delta: timedelta | None = None

    def flush() -> None:
        nonlocal current, current_delta
        if not current:
            return
        first = current[0]
        native_bar = native_bars[first.native_bar_index]
        operations.append(MoveOperation(
            activity_id=activity_id,
            native_bar_xml_key=native_bar.xml_key,
            native_bar_key=native_bar.bar_key,
            native_bar_index=first.native_bar_index,
            one_based_bar_indices=(first.final_bar_index,),
            logical_unit_ids=tuple(item.logical_unit_id for item in current),
            provenance=tuple(
                provenance
                for item in current
                for provenance in item.provenance
            ),
            target_start=first.target_start,
            delta_days=current_delta.days,
            expected_bar_count=final_count,
        ))
        current = []
        current_delta = None

    for native_index, native_bar in enumerate(native_bars):
        matches = items_by_native_index[native_index]
        if not matches:
            continue
        for run, delta in _civil_move_runs(matches, native_bar.bar_key):
            if delta == timedelta(0):
                flush()
                continue
            for item in run:
                if current:
                    prior = current[-1]
                    prior_bar = native_bars[prior.native_bar_index]
                    contiguous = (
                        item.native_bar_index == prior.native_bar_index + 1
                        and prior_bar.finish == native_bar.start
                        and prior.target_finish == item.target_start
                    )
                    if current_delta != delta or not contiguous:
                        flush()
                current.append(item)
                current_delta = delta
    flush()
    return tuple(operations)


def _civil_move_runs(
    matches: list[BarIndexEvidence], native_bar_key: str,
) -> tuple[tuple[tuple[BarIndexEvidence, ...], timedelta], ...]:
    """Partition one native bar into contiguous equal-shift descendant runs."""
    if not matches:
        raise PreflightError(f"native bar {native_bar_key} has no index evidence")
    runs: list[tuple[tuple[BarIndexEvidence, ...], timedelta]] = []
    current: list[BarIndexEvidence] = []
    current_delta: timedelta | None = None
    for item in matches:
        delta = item.target_start - item.original_start
        if delta.seconds or delta.microseconds:
            raise PreflightError(
                f"native bar {native_bar_key} has a non-civil displacement"
            )
        if current and delta != current_delta:
            runs.append((tuple(current), current_delta))
            current = []
        current.append(item)
        current_delta = delta
    runs.append((tuple(current), current_delta))
    return tuple(runs)


def _duration_decimal(value: timedelta) -> Decimal:
    microseconds = (
        (value.days * 86_400 + value.seconds) * 1_000_000 + value.microseconds
    )
    if microseconds <= 0:
        raise PreflightError("native split duration is not positive")
    return Decimal(microseconds)


def _prepare_operations(
    plan: MaptekOperationPlan,
) -> tuple[_PreparedSplitCall | _PreparedMoveCall, ...]:
    _validate_plan(plan)
    prepared: list[_PreparedSplitCall | _PreparedMoveCall] = []
    for operation in plan.operations:
        if type(operation) is SplitOperation:
            proportion = float(operation.proportion)
            delay_days = float(operation.delay_days)
            if (
                not 0.0 < proportion < 1.0
                or not math.isfinite(delay_days)
                or delay_days < 0.0
            ):
                raise PreflightError("split conversion is unsafe")
            prepared.append(_PreparedSplitCall(operation, proportion, delay_days))
        else:
            if operation.target_start.microsecond:
                raise PreflightError("Maptek start time would lose sub-second precision")
            prepared.append(_PreparedMoveCall(
                operation,
                f"{operation.activity_id}.{operation.one_based_bar_indices[0]}",
                operation.target_start.strftime("%Y-%m-%d %H:%M:%S"),
            ))
    return tuple(prepared)


def _validate_plan(plan: MaptekOperationPlan) -> None:
    if type(plan) is not MaptekOperationPlan:
        raise PreflightError("plan must be an exact MaptekOperationPlan")
    if type(plan.operations) is not tuple or type(plan.bar_index_evidence) is not tuple:
        raise PreflightError("plan sequences must be exact tuples")
    if (type(plan.initial_bar_counts) is not _MAPPING_PROXY_TYPE
            or type(plan.final_bar_counts) is not _MAPPING_PROXY_TYPE):
        raise PreflightError("plan count evidence must be immutable")
    initial = dict(plan.initial_bar_counts)
    final = dict(plan.final_bar_counts)
    if set(initial) != set(final):
        raise PreflightError("plan count evidence does not cover the same activities")
    if not initial:
        if plan.operations or plan.bar_index_evidence:
            raise PreflightError("empty count evidence cannot carry operations")
        return
    for activity_id, count in initial.items():
        if type(activity_id) is not int or activity_id <= 0:
            raise PreflightError("plan activity ID is invalid")
        if type(count) is not int or count <= 0:
            raise PreflightError("plan initial bar count is invalid")
        if type(final[activity_id]) is not int or final[activity_id] < count:
            raise PreflightError("plan final bar count is invalid")

    counts = dict(initial)
    moves_started = False
    for operation in plan.operations:
        if type(operation) is SplitOperation:
            if moves_started:
                raise PreflightError("split operation appears after a move")
            _validate_split(operation, counts)
            counts[operation.activity_id] += 1
        elif type(operation) is MoveOperation:
            moves_started = True
            _validate_move(operation, counts)
        else:
            raise PreflightError("plan contains an unknown operation type")
    if counts != final:
        raise PreflightError("plan operations do not produce the final bar counts")

    evidence_by_activity: dict[int, list[BarIndexEvidence]] = {
        activity_id: [] for activity_id in initial
    }
    for item in plan.bar_index_evidence:
        if type(item) is not BarIndexEvidence or item.activity_id not in evidence_by_activity:
            raise PreflightError("plan contains invalid bar-index evidence")
        if (type(item.fragment_ids) is not tuple or not item.fragment_ids
                or not item.logical_unit_id
                or type(item.provenance) is not tuple
                or len(item.provenance) != len(item.fragment_ids)
                or any(value not in ("unchanged", "direct", "cascade") for value in item.provenance)
                or any(type(value) is not datetime or value.tzinfo is not None for value in (
                    item.original_start, item.original_finish,
                    item.target_start, item.target_finish,
                ))
                or item.original_start >= item.original_finish
                or item.target_start >= item.target_finish
                or item.original_finish - item.original_start != item.target_finish - item.target_start):
            raise PreflightError("plan bar-index evidence is incomplete")
        evidence_by_activity[item.activity_id].append(item)
    for activity_id, items in evidence_by_activity.items():
        indices = sorted(item.final_bar_index for item in items)
        if indices != list(range(1, final[activity_id] + 1)):
            raise PreflightError("plan bar-index evidence is not exact")
        native_indices = {item.native_bar_index for item in items}
        if native_indices != set(range(initial[activity_id])):
            raise PreflightError("plan native-bar evidence is not exact")
        for item in items:
            if (type(item.native_bar_index) is not int
                    or type(item.final_bar_index) is not int
                    or type(item.native_descendant_order) is not int
                    or item.native_descendant_order < 0
                    or type(item.logical_unit_order) is not int
                    or item.logical_unit_order < 0
                    or type(item.native_bar_xml_key) is not str
                    or not item.native_bar_xml_key
                    or type(item.native_bar_key) is not str
                    or not item.native_bar_key):
                raise PreflightError("plan bar-index evidence is invalid")
        for matches in _evidence_groups(items).values():
            ordered = sorted(matches, key=lambda item: item.native_descendant_order)
            if [item.native_descendant_order for item in ordered] != list(
                range(len(ordered))
            ):
                raise PreflightError("native descendant order evidence is invalid")
        logical_groups: dict[str, list[BarIndexEvidence]] = {}
        for item in items:
            logical_groups.setdefault(item.logical_unit_id, []).append(item)
        for matches in logical_groups.values():
            ordered = sorted(
                matches,
                key=lambda item: (
                    item.original_start,
                    item.original_finish,
                    item.native_bar_index,
                    item.native_descendant_order,
                ),
            )
            if [item.logical_unit_order for item in ordered] != list(
                range(len(ordered))
            ):
                raise PreflightError("logical unit order evidence is invalid")

    _validate_operations_against_evidence(plan, evidence_by_activity)


def _validate_operations_against_evidence(
    plan: MaptekOperationPlan,
    evidence_by_activity: Mapping[int, list[BarIndexEvidence]],
) -> None:
    expected_splits: list[tuple[object, ...]] = []
    source_state = plan.source_state
    supplied_by_id = {
        _maptek_activity_id(activity.activity_id): activity
        for activity in plan.source_package.activities
        if _maptek_activity_id(activity.activity_id) in evidence_by_activity
    }
    split_indices_by_activity: dict[int, frozenset[int]] = {}
    for activity_id in sorted(evidence_by_activity):
        supplied = supplied_by_id[activity_id]
        native_bars = source_state.activities[str(activity_id)].bars
        fragments_by_bar = _fragments_by_native_bar(supplied, native_bars)
        specs, _ = _native_split_specs(
            native_bars, fragments_by_bar, source_state.completion_cutoff,
        )
        split_indices_by_activity[activity_id] = frozenset(
            spec.native_index for spec in specs
        )
        expected_splits.extend(
            (
                activity_id, spec.native_bar.xml_key, spec.native_bar.bar_key,
                spec.native_index, spec.task_bar_index, spec.boundary,
                spec.proportion, spec.delay_days, spec.expected_bar_count,
            )
            for spec in specs
        )
    actual_splits = [
        (
            operation.activity_id, operation.native_bar_xml_key,
            operation.native_bar_key, operation.native_bar_index,
            operation.task_bar_index, operation.boundary, operation.proportion,
            operation.delay_days, operation.expected_bar_count,
        )
        for operation in plan.operations if type(operation) is SplitOperation
    ]
    if actual_splits != expected_splits:
        raise PreflightError("split operations disagree with exact calendar evidence")

    expected_move_operations: list[MoveOperation] = []
    for activity_id in sorted(evidence_by_activity):
        supplied = supplied_by_id[activity_id]
        native_bars = source_state.activities[str(activity_id)].bars
        expected_move_operations.extend(_moves_for_activity(
            activity_id, supplied, native_bars,
            _fragments_by_native_bar(supplied, native_bars), {},
            plan.final_bar_counts[activity_id],
            tuple(evidence_by_activity[activity_id]),
            split_indices_by_activity[activity_id],
        ))
    actual_move_operations = [
        operation for operation in plan.operations if type(operation) is MoveOperation
    ]
    if actual_move_operations != expected_move_operations:
        raise PreflightError("move operations disagree with exact calendar evidence")


def _evidence_groups(
    items: list[BarIndexEvidence],
) -> dict[tuple[int, str, str], list[BarIndexEvidence]]:
    grouped: dict[tuple[int, str, str], list[BarIndexEvidence]] = {}
    for item in items:
        grouped.setdefault(
            (item.native_bar_index, item.native_bar_xml_key, item.native_bar_key), []
        ).append(item)
    return grouped


def _validate_split(operation: SplitOperation, counts: dict[int, int]) -> None:
    if operation.activity_id not in counts:
        raise PreflightError("split references an unknown activity")
    if (type(operation.expected_bar_count) is not int
            or operation.expected_bar_count != counts[operation.activity_id]):
        raise PreflightError("split count evidence is stale")
    if (type(operation.task_bar_index) is not int
            or not 0 <= operation.task_bar_index < operation.expected_bar_count):
        raise PreflightError("split bar index is invalid")
    if type(operation.native_bar_index) is not int or operation.native_bar_index < 0:
        raise PreflightError("split native index is invalid")
    if type(operation.proportion) is not Decimal or not Decimal("0") < operation.proportion < Decimal("1"):
        raise PreflightError("split proportion is invalid")
    if (
        type(operation.delay_days) is not Decimal
        or not operation.delay_days.is_finite()
        or operation.delay_days < Decimal("0")
    ):
        raise PreflightError("split delay is invalid")
    if type(operation.boundary) is not datetime or operation.boundary.tzinfo is not None:
        raise PreflightError("split boundary is invalid")


def _validate_move(operation: MoveOperation, counts: dict[int, int]) -> None:
    if operation.activity_id not in counts:
        raise PreflightError("move references an unknown activity")
    if (type(operation.expected_bar_count) is not int
            or operation.expected_bar_count != counts[operation.activity_id]):
        raise PreflightError("move count evidence is stale")
    if (type(operation.one_based_bar_indices) is not tuple
            or not operation.one_based_bar_indices
            or any(type(index) is not int for index in operation.one_based_bar_indices)):
        raise PreflightError("move bar-index evidence is invalid")
    indices = operation.one_based_bar_indices
    if (indices != tuple(sorted(set(indices)))
            or indices != tuple(range(indices[0], indices[-1] + 1))
            or indices[0] < 1 or indices[-1] > counts[operation.activity_id]):
        raise PreflightError("move bar-index evidence is not contiguous")
    if type(operation.target_start) is not datetime or operation.target_start.tzinfo is not None:
        raise PreflightError("move target is invalid")
    if (type(operation.native_bar_index) is not int or operation.native_bar_index < 0
            or type(operation.logical_unit_ids) is not tuple
            or not operation.logical_unit_ids
            or any(type(item) is not str or not item for item in operation.logical_unit_ids)
            or type(operation.provenance) is not tuple
            or not operation.provenance
            or len(operation.provenance) < len(operation.logical_unit_ids)
            or any(type(item) is not str for item in operation.provenance)
            or type(operation.delta_days) is not int):
        raise PreflightError("move evidence is invalid")
    if any(item not in ("unchanged", "direct", "cascade") for item in operation.provenance):
        raise PreflightError("move provenance is invalid")


def _require_live_count(
    get_number_bars: Callable[..., Any], activity_id: int, expected: int
) -> int:
    actual = get_number_bars(activity_id)
    return _parse_live_count(actual, activity_id, expected, require_match=True)


def _parse_live_count(
    actual: Any, activity_id: int, expected: int, *, require_match: bool = False,
) -> int:
    if (type(actual) is not str or not actual.isascii() or not actual.isdecimal()
            or actual == "0" or actual.startswith("0")):
        raise LiveBarCountMismatch(activity_id, expected, None)
    parsed = int(actual)
    if require_match and parsed != expected:
        raise LiveBarCountMismatch(activity_id, expected, parsed)
    return parsed
