"""Native, read-only `.vgantt` model reader for the isolated STP V2 flow."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime, time, timedelta
from decimal import Decimal, InvalidOperation
from hashlib import sha256
import json
from pathlib import Path
from types import MappingProxyType
from typing import Callable, Collection, Iterable, Iterator, Mapping, Sequence, final
from xml.etree import ElementTree as ET
from zipfile import ZipFile


DOTNET_EPOCH = datetime(1, 1, 1)


@dataclass(frozen=True)
class NativeBar:
    xml_key: str
    bar_key: str
    start: datetime
    finish: datetime
    quantity_percentage: Decimal
    percent_complete: Decimal
    complete_worked_minutes: Decimal


@dataclass(frozen=True)
class NativeActivity:
    activity_id: str
    collection_key: str
    bars: tuple[NativeBar, ...]
    overall_percent_complete: Decimal


@dataclass(frozen=True)
class PrecedenceLink:
    link_key: str
    predecessor_id: str
    successor_id: str
    predecessor_bar: str
    successor_bar: str
    precedence_type: str
    lag: timedelta
    group_key: str


@dataclass(frozen=True)
class ProjectState:
    path: Path
    date_format: str
    activities: Mapping[str, NativeActivity]
    links: tuple[PrecedenceLink, ...]
    completion_cutoff: datetime | None = None


@dataclass(frozen=True)
class AssociatedLinkReference:
    """The immutable precedence identity attached to one native anchor."""

    link_key: str
    role: str
    endpoint_bar: str
    other_activity_id: str
    other_bar: str
    precedence_type: str
    lag: timedelta
    group_key: str


@dataclass(frozen=True)
class NativeAnchor:
    """An immutable boundary between two original native bars."""

    activity_id: str
    left_bar_xml_key: str
    left_bar_key: str
    right_bar_xml_key: str
    right_bar_key: str
    at: datetime
    cumulative_quantity_percentage: Decimal
    associated_links: tuple[AssociatedLinkReference, ...] = ()


@dataclass(frozen=True)
class CompletionBoundary:
    identifiable: bool
    pending_bar_index: int | None
    at: datetime | None
    reason: str


@dataclass(frozen=True)
class ActivitySpan:
    """The effective interval of one activity or linked native bar."""

    start: datetime
    finish: datetime


@final
@dataclass(frozen=True, slots=True, eq=False)
class CascadeEvidenceMap(Mapping[str, int | str]):
    """Callback-free immutable evidence stored as canonical exact pairs."""

    canonical_items: tuple[tuple[str, int | str], ...]

    def __post_init__(self) -> None:
        if type(self) is not CascadeEvidenceMap:
            raise TypeError("cascade evidence map cannot be subclassed")
        items = self.canonical_items
        if type(items) is not tuple:
            raise TypeError("cascade evidence items must be an exact tuple")
        for item in items:
            if type(item) is not tuple or len(item) != 2:
                raise TypeError("cascade evidence entries must be exact pairs")
            key, value = item
            if type(key) is not str or not key:
                raise TypeError("cascade evidence keys must be exact non-empty strings")
            if type(value) not in (int, str):
                raise TypeError("cascade evidence values must be exact integers or strings")
        keys = tuple(item[0] for item in items)
        if len(set(keys)) != len(keys):
            raise ValueError("cascade evidence keys must be unique")
        object.__setattr__(
            self,
            "canonical_items",
            tuple(sorted(items, key=lambda item: item[0])),
        )

    def __getitem__(self, key: str) -> int | str:
        if type(key) is not str:
            raise KeyError(key)
        for item_key, value in self.canonical_items:
            if item_key == key:
                return value
        raise KeyError(key)

    def __iter__(self) -> Iterator[str]:
        return (item[0] for item in self.canonical_items)

    def __len__(self) -> int:
        return len(self.canonical_items)


def _cascade_evidence_map_from_dict(
    values: dict[str, int | str],
) -> CascadeEvidenceMap:
    if type(values) is not dict:
        raise TypeError("trusted cascade evidence source must be an exact dictionary")
    return CascadeEvidenceMap(tuple(values.items()))


@dataclass(frozen=True)
class CascadePlan:
    """A deterministic, all-or-nothing precedence movement closure."""

    shifts: CascadeEvidenceMap
    provenance: CascadeEvidenceMap
    violations: tuple[str, ...]


SCENARIO_V2_SCHEMA = "vgs-stp-splits/2"
ALLOCATION_MIN_SCALE = 30
ALLOCATION_MAX_POWER = 1_000
# VGS stores display percentages independently from native bar quantities.  Its
# floating-point serialization can differ by a few ten-thousandths of one
# percentage point even when a completed prefix is unambiguous.
COMPLETION_ROUNDING_TOLERANCE = Decimal("0.001")


@dataclass(frozen=True)
class DailyFragment:
    """One immutable native-bar subdivision displayed for a civil day."""

    id: str
    activity_id: str
    native_bar_xml_key: str
    native_bar_key: str
    start: datetime
    finish: datetime
    original_start: datetime
    original_finish: datetime
    day: date
    logical_unit_day: date
    display_suppressed: bool
    quantity_percentage: Decimal
    locked_completed: bool


@dataclass(frozen=True)
class ScenarioFragment:
    id: str
    native_bar_xml_key: str
    native_bar_key: str
    start: datetime
    finish: datetime
    original_start: datetime
    original_finish: datetime
    quantity_percentage: Decimal
    locked_completed: bool


@dataclass(frozen=True)
class ScenarioActivity:
    activity_id: str
    provenance: str
    fragments: tuple[ScenarioFragment, ...]


@dataclass(frozen=True)
class ScenarioV2:
    schema: str
    project_fingerprint: str
    precedence_fingerprint: str
    horizon_start: datetime
    horizon_finish: datetime
    activities: tuple[ScenarioActivity, ...]


@dataclass(frozen=True)
class ValidationDifference:
    code: str
    activity_id: str
    detail: str


@dataclass(frozen=True)
class ValidationReport:
    differences: tuple[ValidationDifference, ...]

    @property
    def ok(self) -> bool:
        return not self.differences


def civil_boundaries(start: datetime, finish: datetime) -> tuple[datetime, ...]:
    """Return local-naive midnight boundaries strictly inside ``[start, finish)``."""
    _validate_naive_interval(start, finish, "civil interval")
    cursor = datetime.combine(start.date() + timedelta(days=1), time.min)
    result: list[datetime] = []
    while cursor < finish:
        result.append(cursor)
        cursor += timedelta(days=1)
    return tuple(result)


def segment_activity(activity: NativeActivity) -> tuple[DailyFragment, ...]:
    """Split only activities whose pending native suffix is identifiable."""
    return _segment_activity(activity, lock_ambiguous=False)


def segment_activity_for_display(
    activity: NativeActivity, completion_cutoff: datetime | None = None
) -> tuple[DailyFragment, ...]:
    """Split for display and honor VGS' enabled global completed-through date."""
    return _segment_activity(
        activity, lock_ambiguous=True, completion_cutoff=completion_cutoff
    )


def _segment_activity(
    activity: NativeActivity, *, lock_ambiguous: bool,
    completion_cutoff: datetime | None = None,
) -> tuple[DailyFragment, ...]:
    """Build civil fragments without ever treating ambiguous work as movable."""
    _validate_unique_bar_identities(activity)
    display_bars: list[tuple[int, NativeBar]] = []
    for bar_index, bar in enumerate(activity.bars):
        # Some VGS saves retain an empty artefact after an interrupted split:
        # it has no duration, quantity, or completion evidence.  It is not
        # work and cannot be scheduled, but it remains in the immutable
        # native payload/fingerprint.  Omitting only that no-op from the
        # display keeps the source project read-only and prevents a single
        # bad marker from blocking every other activity in STP.
        if _is_zero_duration_noop_bar(bar):
            continue
        _validate_naive_interval(
            bar.start, bar.finish, f"native bar '{bar.bar_key}' in activity ID '{activity.activity_id}'"
        )
        display_bars.append((bar_index, bar))
    boundary = completion_boundary(activity)
    if not boundary.identifiable and (
        not lock_ambiguous or not activity.bars or _completion_evidence_error(activity) is not None
    ):
        raise ValueError(
            f"activity ID '{activity.activity_id}' has ambiguous completion: {boundary.reason}"
        )

    fragments: list[DailyFragment] = []
    pending_index = boundary.pending_bar_index
    lock_all = not boundary.identifiable and completion_cutoff is None
    for bar_index, bar in display_bars:
        boundaries = list(civil_boundaries(bar.start, bar.finish))
        if completion_cutoff is not None and bar.start < completion_cutoff < bar.finish:
            boundaries.append(completion_cutoff)
        endpoints = (bar.start, *sorted(set(boundaries)), bar.finish)
        spans = tuple(zip(endpoints, endpoints[1:]))
        quantities = _allocate_quantities(bar.quantity_percentage, spans)
        one_second_prefix = _is_one_second_prefix_with_material_day(
            bar,
            spans,
            terminal_native_bar=bar_index == display_bars[-1][0],
        )
        logical_unit_day = spans[1][0].date() if one_second_prefix else None
        for sequence, ((start, finish), quantity) in enumerate(zip(spans, quantities)):
            display_suppressed = one_second_prefix and sequence == 0
            fragments.append(
                DailyFragment(
                    id=_fragment_id(activity.activity_id, bar, sequence, start, finish),
                    activity_id=activity.activity_id,
                    native_bar_xml_key=bar.xml_key,
                    native_bar_key=bar.bar_key,
                    start=start,
                    finish=finish,
                    original_start=start,
                    original_finish=finish,
                    day=start.date(),
                    logical_unit_day=logical_unit_day if logical_unit_day is not None else start.date(),
                    display_suppressed=display_suppressed,
                    quantity_percentage=quantity,
                    locked_completed=lock_all or (
                        pending_index is not None and bar_index < pending_index
                    ) or (completion_cutoff is not None and finish <= completion_cutoff),
                )
            )
    return tuple(fragments)


def _is_one_second_prefix_with_material_day(
    bar: NativeBar,
    spans: tuple[tuple[datetime, datetime], ...],
    *,
    terminal_native_bar: bool,
) -> bool:
    """Recognize VGS' one-second prefix followed by exactly one material day."""
    next_midnight = datetime.combine(bar.start.date() + timedelta(days=1), time.min)
    material_finish = next_midnight + timedelta(days=1)
    inclusive_finish = datetime.combine(next_midnight.date(), time(23, 59, 59))
    return (
        len(spans) == 2
        and bar.start.time() == time(23, 59, 59)
        and spans[0] == (bar.start, next_midnight)
        and (
            spans[1] == (next_midnight, inclusive_finish)
            or (terminal_native_bar and spans[1] == (next_midnight, material_finish))
        )
        and bar.finish == spans[1][1]
    )


def _is_zero_duration_noop_bar(bar: NativeBar) -> bool:
    """Identify only the empty VGS residue safe to omit from the STP display."""
    return (
        bar.start == bar.finish
        and bar.quantity_percentage == Decimal("0")
        and bar.percent_complete == Decimal("0")
        and bar.complete_worked_minutes == Decimal("0")
    )


def load_scenario_v2(payload: Mapping[str, object]) -> ScenarioV2:
    """Parse the narrow V2 wire schema; malformed or ambiguous input is unsafe."""
    root = _strict_mapping(
        payload,
        {
            "schema", "project_fingerprint", "precedence_fingerprint",
            "horizon_start", "horizon_finish", "activities",
        },
        "scenario",
    )
    schema = _required_text(root, "schema", "scenario")
    if schema != SCENARIO_V2_SCHEMA:
        raise ValueError(f"unsupported scenario schema '{schema}'")
    project = _required_text(root, "project_fingerprint", "scenario")
    precedence = _required_text(root, "precedence_fingerprint", "scenario")
    horizon_start = _required_datetime(root, "horizon_start", "scenario")
    horizon_finish = _required_datetime(root, "horizon_finish", "scenario")
    _validate_civil_horizon(horizon_start, horizon_finish)
    activity_values = _required_list(root, "activities", "scenario")
    if not activity_values:
        raise ValueError("scenario activities must not be empty")

    activities: list[ScenarioActivity] = []
    activity_ids: set[str] = set()
    fragment_ids: set[str] = set()
    for item in activity_values:
        raw_activity = _strict_mapping(
            item, {"activity_id", "provenance", "fragments"}, "scenario activity"
        )
        activity_id = _required_text(raw_activity, "activity_id", "scenario activity")
        if activity_id in activity_ids:
            raise ValueError(f"duplicate scenario activity ID '{activity_id}'")
        activity_ids.add(activity_id)
        provenance = _required_text(raw_activity, "provenance", "scenario activity")
        if provenance not in {"direct", "cascade"}:
            raise ValueError(f"unrecognized provenance '{provenance}'")
        raw_fragments = _required_list(raw_activity, "fragments", "scenario activity")
        if not raw_fragments:
            raise ValueError(f"activity ID '{activity_id}' has no fragments")
        fragments: list[ScenarioFragment] = []
        for raw_fragment_value in raw_fragments:
            raw_fragment = _strict_mapping(
                raw_fragment_value,
                {
                    "id", "native_bar_xml_key", "native_bar_key", "start", "finish",
                    "original_start", "original_finish", "quantity_percentage", "locked_completed",
                },
                "scenario fragment",
            )
            fragment = ScenarioFragment(
                id=_required_text(raw_fragment, "id", "scenario fragment"),
                native_bar_xml_key=_required_text(raw_fragment, "native_bar_xml_key", "scenario fragment"),
                native_bar_key=_required_text(raw_fragment, "native_bar_key", "scenario fragment"),
                start=_required_datetime(raw_fragment, "start", "scenario fragment"),
                finish=_required_datetime(raw_fragment, "finish", "scenario fragment"),
                original_start=_required_datetime(raw_fragment, "original_start", "scenario fragment"),
                original_finish=_required_datetime(raw_fragment, "original_finish", "scenario fragment"),
                quantity_percentage=_required_decimal(raw_fragment, "quantity_percentage", "scenario fragment"),
                locked_completed=_required_bool(raw_fragment, "locked_completed", "scenario fragment"),
            )
            _validate_naive_interval(fragment.start, fragment.finish, f"fragment '{fragment.id}'")
            _validate_naive_interval(
                fragment.original_start, fragment.original_finish, f"original fragment '{fragment.id}'"
            )
            if fragment.id in fragment_ids:
                raise ValueError(f"duplicate fragment ID '{fragment.id}'")
            fragment_ids.add(fragment.id)
            fragments.append(fragment)
        activities.append(ScenarioActivity(activity_id, provenance, tuple(fragments)))
    return ScenarioV2(schema, project, precedence, horizon_start, horizon_finish, tuple(activities))


def capture_scenario_v2(scenario: ScenarioV2) -> ScenarioV2:
    """Capture one exact, fully owned direct ``ScenarioV2`` graph."""
    if type(scenario) is not ScenarioV2:
        raise TypeError("scenario must be an exact ScenarioV2")

    schema = scenario.schema
    project = scenario.project_fingerprint
    precedence = scenario.precedence_fingerprint
    horizon_start = scenario.horizon_start
    horizon_finish = scenario.horizon_finish
    activities = scenario.activities
    for label, value in (
        ("schema", schema),
        ("project_fingerprint", project),
        ("precedence_fingerprint", precedence),
    ):
        if type(value) is not str or not value:
            raise TypeError(f"scenario.{label} must be an exact non-empty string")
    if schema != SCENARIO_V2_SCHEMA:
        raise ValueError(f"unsupported scenario schema '{schema}'")
    if type(horizon_start) is not datetime or type(horizon_finish) is not datetime:
        raise TypeError("scenario horizon bounds must be exact datetimes")
    _validate_civil_horizon(horizon_start, horizon_finish)
    if type(activities) is not tuple:
        raise TypeError("scenario.activities must be an exact tuple")
    if not activities:
        raise ValueError("scenario activities must not be empty")

    captured_activities: list[ScenarioActivity] = []
    activity_ids: set[str] = set()
    fragment_ids: set[str] = set()
    for activity in activities:
        if type(activity) is not ScenarioActivity:
            raise TypeError("scenario activities must contain exact ScenarioActivity values")
        activity_id = activity.activity_id
        provenance_value = activity.provenance
        fragments = activity.fragments
        if type(activity_id) is not str or not activity_id:
            raise TypeError("scenario activity ID must be an exact non-empty string")
        if type(provenance_value) is not str or not provenance_value:
            raise TypeError("scenario activity provenance must be an exact non-empty string")
        if provenance_value not in {"direct", "cascade"}:
            raise ValueError(f"unrecognized provenance '{provenance_value}'")
        if type(fragments) is not tuple:
            raise TypeError("scenario activity fragments must be an exact tuple")
        if not fragments:
            raise ValueError(f"activity ID '{activity_id}' has no fragments")
        if activity_id in activity_ids:
            raise ValueError(f"duplicate scenario activity ID '{activity_id}'")
        activity_ids.add(activity_id)

        captured_fragments: list[ScenarioFragment] = []
        for fragment in fragments:
            if type(fragment) is not ScenarioFragment:
                raise TypeError(
                    "scenario fragments must contain exact ScenarioFragment values"
                )
            fragment_id = fragment.id
            native_bar_xml_key = fragment.native_bar_xml_key
            native_bar_key = fragment.native_bar_key
            start = fragment.start
            finish = fragment.finish
            original_start = fragment.original_start
            original_finish = fragment.original_finish
            quantity = fragment.quantity_percentage
            locked_completed = fragment.locked_completed
            for label, value in (
                ("id", fragment_id),
                ("native_bar_xml_key", native_bar_xml_key),
                ("native_bar_key", native_bar_key),
            ):
                if type(value) is not str or not value:
                    raise TypeError(
                        f"scenario fragment {label} must be an exact non-empty string"
                    )
            for label, value in (
                ("start", start),
                ("finish", finish),
                ("original_start", original_start),
                ("original_finish", original_finish),
            ):
                if type(value) is not datetime:
                    raise TypeError(
                        f"scenario fragment {label} must be an exact datetime"
                    )
            if type(quantity) is not Decimal or not quantity.is_finite():
                raise TypeError(
                    "scenario fragment quantity_percentage must be an exact finite Decimal"
                )
            if type(locked_completed) is not bool:
                raise TypeError(
                    "scenario fragment locked_completed must be an exact boolean"
                )
            _validate_naive_interval(start, finish, f"fragment '{fragment_id}'")
            _validate_naive_interval(
                original_start,
                original_finish,
                f"original fragment '{fragment_id}'",
            )
            if fragment_id in fragment_ids:
                raise ValueError(f"duplicate fragment ID '{fragment_id}'")
            fragment_ids.add(fragment_id)
            captured_fragments.append(
                ScenarioFragment(
                    fragment_id,
                    native_bar_xml_key,
                    native_bar_key,
                    start,
                    finish,
                    original_start,
                    original_finish,
                    quantity,
                    locked_completed,
                )
            )
        captured_activities.append(
            ScenarioActivity(
                activity_id,
                provenance_value,
                tuple(captured_fragments),
            )
        )
    return ScenarioV2(
        schema,
        project,
        precedence,
        horizon_start,
        horizon_finish,
        tuple(captured_activities),
    )


def scenario_v2_to_json_value(scenario: ScenarioV2) -> dict[str, object]:
    """Return exact built-in JSON values for one captured V2 scenario."""
    captured = capture_scenario_v2(scenario)
    return {
        "schema": captured.schema,
        "project_fingerprint": captured.project_fingerprint,
        "precedence_fingerprint": captured.precedence_fingerprint,
        "horizon_start": captured.horizon_start.isoformat(),
        "horizon_finish": captured.horizon_finish.isoformat(),
        "activities": [
            {
                "activity_id": activity.activity_id,
                "provenance": activity.provenance,
                "fragments": [
                    {
                        "id": fragment.id,
                        "native_bar_xml_key": fragment.native_bar_xml_key,
                        "native_bar_key": fragment.native_bar_key,
                        "start": fragment.start.isoformat(),
                        "finish": fragment.finish.isoformat(),
                        "original_start": fragment.original_start.isoformat(),
                        "original_finish": fragment.original_finish.isoformat(),
                        "quantity_percentage": str(fragment.quantity_percentage),
                        "locked_completed": fragment.locked_completed,
                    }
                    for fragment in activity.fragments
                ],
            }
            for activity in captured.activities
        ],
    }


def scenario_direct_shifts(scenario: ScenarioV2, state: ProjectState) -> Mapping[str, int]:
    """Derive the sole requested civil-day movement for each direct activity."""
    del state
    direct: dict[str, int] = {}
    for item in scenario.activities:
        if item.provenance != "direct":
            continue
        pending = tuple(fragment for fragment in item.fragments if not fragment.locked_completed)
        deltas = {fragment.start - fragment.original_start for fragment in pending}
        if (
            len(deltas) != 1
            or not pending
            or any(fragment.finish - fragment.original_finish not in deltas for fragment in pending)
            or not _is_integer_civil_days(next(iter(deltas)))
            or next(iter(deltas)) == timedelta(0)
        ):
            raise ValueError(
                f"activity ID '{item.activity_id}' lacks one uniform pending shift"
            )
        direct[item.activity_id] = next(iter(deltas)).days
    if not direct:
        raise ValueError("scenario has no direct movement")
    return MappingProxyType(dict(sorted(direct.items())))


def expected_cascade_v2(scenario: ScenarioV2, state: ProjectState) -> CascadePlan:
    """Return the full precedence closure implied by the direct scenario moves."""
    return compute_cascade(state, scenario_direct_shifts(scenario, state))


def validate_scenario_v2(scenario: ScenarioV2, state: ProjectState) -> ValidationReport:
    """Validate a parsed scenario against the current native project without mutation."""
    differences: list[ValidationDifference] = []
    if scenario.schema != SCENARIO_V2_SCHEMA:
        differences.append(ValidationDifference("schema_invalid", "", "unexpected schema"))
        return ValidationReport(tuple(differences))
    try:
        _validate_civil_horizon(scenario.horizon_start, scenario.horizon_finish)
    except (TypeError, ValueError):
        differences.append(
            ValidationDifference("horizon_invalid", "", "scenario horizon is invalid")
        )
        return ValidationReport(tuple(differences))
    try:
        current_fingerprint = project_fingerprint(state)
    except (TypeError, ValueError) as error:
        differences.append(ValidationDifference("invalid_project", "", str(error)))
        return ValidationReport(tuple(differences))
    if scenario.project_fingerprint != current_fingerprint:
        differences.append(ValidationDifference("stale_project", "", "project fingerprint differs"))
    if scenario.precedence_fingerprint != precedence_fingerprint(state):
        differences.append(
            ValidationDifference("stale_precedence", "", "precedence fingerprint differs")
        )

    seen_activity_ids: set[str] = set()
    seen_fragment_ids: set[str] = set()
    for scenario_activity in scenario.activities:
        activity_id = scenario_activity.activity_id
        if activity_id in seen_activity_ids:
            differences.append(ValidationDifference("duplicate_activity", activity_id, "duplicate scenario activity"))
            continue
        seen_activity_ids.add(activity_id)
        activity = state.activities.get(activity_id)
        if activity is None:
            differences.append(ValidationDifference("unknown_activity", activity_id, "not in current project"))
            continue
        if scenario_activity.provenance not in {"direct", "cascade"}:
            differences.append(ValidationDifference("provenance_invalid", activity_id, "unrecognized provenance"))
        boundary = completion_boundary(activity)
        if not boundary.identifiable:
            differences.append(
                ValidationDifference("completion_boundary_ambiguous", activity_id, boundary.reason)
            )
            continue
        _validate_activity_scenario(
            differences, activity, boundary, scenario_activity, seen_fragment_ids, scenario
        )
    if not scenario.activities:
        differences.append(ValidationDifference("scenario_empty", "", "no affected activities"))
    try:
        scenario_direct_shifts(scenario, state)
    except ValueError:
        differences.append(
            ValidationDifference(
                "activity_shift_nonuniform", "", "direct activities require one nonzero uniform pending shift",
            )
        )
    _validate_exact_cascade_closure(differences, scenario, state)
    return ValidationReport(tuple(differences))


def _validate_activity_scenario(
    differences: list[ValidationDifference],
    activity: NativeActivity,
    boundary: CompletionBoundary,
    scenario_activity: ScenarioActivity,
    seen_fragment_ids: set[str],
    scenario: ScenarioV2,
) -> None:
    activity_id = activity.activity_id
    expected = segment_activity(activity)
    expected_by_id = {fragment.id: fragment for fragment in expected}
    supplied_by_id: dict[str, ScenarioFragment] = {}
    for fragment in scenario_activity.fragments:
        if fragment.id in seen_fragment_ids:
            differences.append(ValidationDifference("duplicate_fragment", activity_id, fragment.id))
            continue
        seen_fragment_ids.add(fragment.id)
        supplied_by_id[fragment.id] = fragment
        expected_fragment = expected_by_id.get(fragment.id)
        if expected_fragment is None:
            differences.append(ValidationDifference("fragment_ownership_invalid", activity_id, fragment.id))
            continue
        if (
            fragment.native_bar_xml_key != expected_fragment.native_bar_xml_key
            or fragment.native_bar_key != expected_fragment.native_bar_key
        ):
            differences.append(ValidationDifference("fragment_ownership_invalid", activity_id, fragment.id))
            continue
        if (
            fragment.original_start != expected_fragment.original_start
            or fragment.original_finish != expected_fragment.original_finish
        ):
            differences.append(ValidationDifference("original_interval_invalid", activity_id, fragment.id))
        if fragment.quantity_percentage != expected_fragment.quantity_percentage:
            differences.append(ValidationDifference("quantity_mismatch", activity_id, fragment.id))
        if fragment.locked_completed != expected_fragment.locked_completed:
            differences.append(ValidationDifference("locked_fragment_invalid", activity_id, fragment.id))
        if expected_fragment.locked_completed and (
            fragment.start != fragment.original_start or fragment.finish != fragment.original_finish
        ):
            differences.append(ValidationDifference("locked_fragment_changed", activity_id, fragment.id))
        if not expected_fragment.locked_completed:
            start_shift = fragment.start - fragment.original_start
            finish_shift = fragment.finish - fragment.original_finish
            if start_shift != finish_shift or not _is_integer_civil_days(start_shift):
                differences.append(ValidationDifference("civil_day_movement_invalid", activity_id, fragment.id))
            if scenario_activity.provenance == "direct":
                if not _interval_is_within_horizon(
                    fragment.original_start, fragment.original_finish, scenario
                ):
                    differences.append(
                        ValidationDifference(
                            "direct_original_outside_horizon", activity_id, fragment.id
                        )
                    )
                if not _interval_is_within_horizon(fragment.start, fragment.finish, scenario):
                    differences.append(
                        ValidationDifference(
                            "direct_proposed_outside_horizon", activity_id, fragment.id
                        )
                    )

    if set(supplied_by_id) != set(expected_by_id):
        differences.append(
            ValidationDifference("original_coverage_invalid", activity_id, "all native fragments are required")
        )
    for bar in activity.bars:
        matching = [
            fragment for fragment in scenario_activity.fragments
            if fragment.native_bar_xml_key == bar.xml_key and fragment.native_bar_key == bar.bar_key
        ]
        try:
            matching_quantity = _exact_decimal_sum(
                fragment.quantity_percentage for fragment in matching
            )
        except ValueError as error:
            differences.append(
                ValidationDifference("quantity_representation_invalid", activity_id, str(error))
            )
            continue
        if matching_quantity != bar.quantity_percentage:
            differences.append(ValidationDifference("quantity_mismatch", activity_id, bar.bar_key))


def _interval_is_within_horizon(
    start: datetime, finish: datetime, scenario: ScenarioV2
) -> bool:
    return scenario.horizon_start <= start and finish <= scenario.horizon_finish


def _allocate_quantities(
    quantity: Decimal, spans: Sequence[tuple[datetime, datetime]]
) -> tuple[Decimal, ...]:
    """Allocate finite Decimal subunits with an exact final-fragment remainder.

    A native quantity is first represented as an integer at a fixed minimum
    scale (30 fractional decimal places), extended when its own exponent needs
    more precision.  Every prior fragment receives floor(total_units * duration
    / total_duration); the final fragment receives every remaining integer
    subunit.  No Decimal arithmetic is used for the allocation, so stored
    coefficients conserve the original quantity at any caller precision.
    """
    if not spans:
        raise ValueError("native bar has no duration")
    if not quantity.is_finite():
        raise ValueError("native bar quantity must be finite")
    durations = [finish - start for start, finish in spans]
    if any(duration <= timedelta(0) for duration in durations):
        raise ValueError("fragment duration must be positive")
    scale = _allocation_scale(quantity)
    total_units = _decimal_to_units(quantity, scale)
    total_duration = sum((_timedelta_microseconds(duration) for duration in durations), 0)
    units = [
        total_units * _timedelta_microseconds(duration) // total_duration
        for duration in durations[:-1]
    ]
    units.append(total_units - sum(units))
    return tuple(_units_to_decimal(value, scale) for value in units)


def _allocation_scale(quantity: Decimal) -> int:
    exponent = quantity.as_tuple().exponent
    scale = max(ALLOCATION_MIN_SCALE, -exponent, 0)
    if exponent + scale < 0 or exponent + scale > ALLOCATION_MAX_POWER:
        raise ValueError("native bar quantity cannot be represented safely as allocation subunits")
    return scale


def _decimal_to_units(quantity: Decimal, scale: int) -> int:
    sign, digits, exponent = quantity.as_tuple()
    power = exponent + scale
    if power < 0 or power > ALLOCATION_MAX_POWER:
        raise ValueError("native bar quantity cannot be represented safely as allocation subunits")
    coefficient = int("".join(str(digit) for digit in digits)) if digits else 0
    units = coefficient * (10 ** power)
    return -units if sign else units


def _units_to_decimal(units: int, scale: int) -> Decimal:
    coefficient = str(abs(units))
    digits = tuple(int(digit) for digit in coefficient)
    return Decimal((int(units < 0), digits, -scale))


def _exact_decimal_sum(values: Iterable[Decimal]) -> Decimal:
    """Sum finite Decimals through common-scale integer coefficients only."""
    items = tuple(values)
    if not items:
        return Decimal("0")
    if any(not isinstance(value, Decimal) or not value.is_finite() for value in items):
        raise ValueError("quantity values must be finite Decimals")
    scale = max(max(-value.as_tuple().exponent, 0) for value in items)
    units = sum(_decimal_to_units(value, scale) for value in items)
    return _units_to_decimal(units, scale)


def _validate_exact_cascade_closure(
    differences: list[ValidationDifference], scenario: ScenarioV2, state: ProjectState
) -> None:
    """Require the scenario to equal the complete, safe precedence closure."""
    try:
        expected = expected_cascade_v2(scenario, state)
    except (TypeError, ValueError):
        differences.append(
            ValidationDifference(
                "cascade_plan_invalid", "", "direct movement cannot produce a cascade plan"
            )
        )
        return
    if expected.violations:
        differences.append(
            ValidationDifference(
                "cascade_plan_invalid", "", "computed cascade plan has violations"
            )
        )
        return

    entries = {item.activity_id: item for item in scenario.activities}
    expected_ids = set(expected.shifts)
    supplied_ids = set(entries)
    if supplied_ids != expected_ids:
        differences.append(
            ValidationDifference(
                "cascade_coverage_mismatch", "", "affected activities differ from computed cascade plan"
            )
        )

    for activity_id in sorted(supplied_ids & expected_ids):
        supplied = entries[activity_id]
        if supplied.provenance != expected.provenance[activity_id]:
            differences.append(
                ValidationDifference(
                    "cascade_provenance_mismatch", activity_id,
                    "provenance differs from computed cascade plan",
                )
            )
        activity = state.activities.get(activity_id)
        if activity is None:
            continue
        native_by_id = {fragment.id: fragment for fragment in segment_activity(activity)}
        supplied_by_id = {fragment.id: fragment for fragment in supplied.fragments}
        expected_shift = timedelta(days=expected.shifts[activity_id])
        for fragment_id, native_fragment in native_by_id.items():
            if native_fragment.locked_completed:
                continue
            fragment = supplied_by_id.get(fragment_id)
            if fragment is None or (
                fragment.start != native_fragment.original_start + expected_shift
                or fragment.finish != native_fragment.original_finish + expected_shift
            ):
                differences.append(
                    ValidationDifference(
                        "cascade_shift_mismatch", activity_id,
                        "pending movement differs from computed cascade plan",
                    )
                )
                break


def _fragment_id(
    activity_id: str, bar: NativeBar, sequence: int, start: datetime, finish: datetime
) -> str:
    return f"{activity_id}:{bar.xml_key}:{bar.bar_key}:{sequence}:{start.isoformat()}:{finish.isoformat()}"


def _validate_naive_interval(start: datetime, finish: datetime, label: str) -> None:
    if not isinstance(start, datetime) or not isinstance(finish, datetime):
        raise ValueError(f"{label} bounds must be datetimes")
    if start.tzinfo is not None or finish.tzinfo is not None:
        raise ValueError(f"{label} bounds must be local naive datetimes")
    if start >= finish:
        raise ValueError(f"{label} duration must be positive")


def _validate_civil_horizon(start: datetime, finish: datetime) -> None:
    if not isinstance(start, datetime) or not isinstance(finish, datetime):
        raise ValueError("scenario horizon bounds must be datetimes")
    if start.tzinfo is not None or finish.tzinfo is not None:
        raise ValueError("scenario horizon must be local-naive")
    if start.time() != time.min or finish.time() != time.min:
        raise ValueError("scenario horizon endpoints must be civil midnight")
    if finish <= start or finish - start > timedelta(days=366):
        raise ValueError("scenario horizon interval is invalid")


def _strict_mapping(value: object, fields: set[str], label: str) -> Mapping[str, object]:
    if not isinstance(value, Mapping):
        raise ValueError(f"{label} must be an object")
    unknown = set(value) - fields
    missing = fields - set(value)
    if unknown or missing:
        raise ValueError(f"{label} has invalid fields (missing={sorted(missing)}, unknown={sorted(unknown)})")
    return value


def _required_text(value: Mapping[str, object], key: str, label: str) -> str:
    item = value.get(key)
    if not isinstance(item, str) or not item:
        raise ValueError(f"{label}.{key} must be a non-empty string")
    return item


def _required_list(value: Mapping[str, object], key: str, label: str) -> list[object]:
    item = value.get(key)
    if not isinstance(item, list):
        raise ValueError(f"{label}.{key} must be an array")
    return item


def _required_bool(value: Mapping[str, object], key: str, label: str) -> bool:
    item = value.get(key)
    if type(item) is not bool:
        raise ValueError(f"{label}.{key} must be a boolean")
    return item


def _required_datetime(value: Mapping[str, object], key: str, label: str) -> datetime:
    raw = _required_text(value, key, label)
    if "T" not in raw:
        raise ValueError(f"{label}.{key} must use ISO local datetime form")
    try:
        parsed = datetime.fromisoformat(raw)
    except ValueError as error:
        raise ValueError(f"{label}.{key} is not a valid ISO datetime") from error
    if parsed.tzinfo is not None:
        raise ValueError(f"{label}.{key} must be a local naive datetime")
    return parsed


def _required_decimal(value: Mapping[str, object], key: str, label: str) -> Decimal:
    raw = value.get(key)
    if not isinstance(raw, str) or not raw:
        raise ValueError(f"{label}.{key} must be a decimal string")
    try:
        parsed = Decimal(raw)
    except InvalidOperation as error:
        raise ValueError(f"{label}.{key} is not a decimal") from error
    if not parsed.is_finite():
        raise ValueError(f"{label}.{key} must be finite")
    return parsed


def _is_integer_civil_days(value: timedelta) -> bool:
    return value.microseconds == 0 and value.seconds == 0


@dataclass(frozen=True)
class InvariantDifference:
    code: str
    activity_id: str
    detail: str


@dataclass(frozen=True)
class InvariantReport:
    differences: tuple[InvariantDifference, ...]

    @property
    def ok(self) -> bool:
        return not self.differences


def native_anchors(
    activity: NativeActivity, links: Sequence[PrecedenceLink]
) -> tuple[NativeAnchor, ...]:
    """Return exact native split boundaries, rejecting malformed bar identity."""
    _validate_unique_bar_identities(activity)
    anchors: list[NativeAnchor] = []
    cumulative = Decimal("0")
    for index, left in enumerate(activity.bars[:-1]):
        right = activity.bars[index + 1]
        cumulative = _exact_decimal_sum((cumulative, left.quantity_percentage))
        if left.finish != right.start:
            raise ValueError(
                f"native bars '{left.bar_key}' and '{right.bar_key}' in activity ID "
                f"'{activity.activity_id}' do not share an exact boundary"
            )
        anchors.append(
            NativeAnchor(
                activity_id=activity.activity_id,
                left_bar_xml_key=left.xml_key,
                left_bar_key=left.bar_key,
                right_bar_xml_key=right.xml_key,
                right_bar_key=right.bar_key,
                at=left.finish,
                cumulative_quantity_percentage=cumulative,
                associated_links=_associated_links(activity, left, right, links),
            )
        )
    return tuple(anchors)


def completion_boundary(activity: NativeActivity) -> CompletionBoundary:
    """Identify only a whole-native-bar completed prefix; otherwise fail closed."""
    if not activity.bars:
        return CompletionBoundary(False, None, None, "activity has no native bars")
    evidence_error = _completion_evidence_error(activity)
    if evidence_error is not None:
        return CompletionBoundary(False, None, None, evidence_error)
    completed = [
        index
        for index, bar in enumerate(activity.bars)
        if bar.percent_complete == Decimal("100")
    ]
    partial = [
        bar
        for bar in activity.bars
        if Decimal("0") < bar.percent_complete < Decimal("100")
    ]
    if partial:
        return CompletionBoundary(False, None, None, "partial native bar")
    if not completed and activity.overall_percent_complete > 0:
        return CompletionBoundary(
            False, None, None, "overall percent has no native completed prefix"
        )
    if completed != list(range(len(completed))):
        return CompletionBoundary(False, None, None, "completed bars are not a prefix")
    try:
        completed_quantity = _exact_decimal_sum(
            activity.bars[index].quantity_percentage for index in completed
        )
    except ValueError as error:
        return CompletionBoundary(False, None, None, str(error))
    if abs(activity.overall_percent_complete - completed_quantity) > COMPLETION_ROUNDING_TOLERANCE:
        return CompletionBoundary(
            False, None, None, "overall percent does not match completed native prefix"
        )
    if len(completed) == len(activity.bars):
        return CompletionBoundary(
            True, len(activity.bars), activity.bars[-1].finish, "fully completed"
        )
    index = len(completed)
    at = activity.bars[index].start if index else None
    return CompletionBoundary(True, index, at, "native completed prefix")


def _invariant_completion_boundary(
    activity: NativeActivity, completion_cutoff: datetime | None,
) -> CompletionBoundary:
    """Use the cutoff only to reconcile a legacy marker proven by quantity."""
    strict = completion_boundary(activity)
    if strict.identifiable or completion_cutoff is None:
        return strict
    if strict.reason != "overall percent does not match completed native prefix":
        return strict
    if not any(
        bar.start <= completion_cutoff <= bar.finish for bar in activity.bars
    ):
        return strict
    pending_index = 0
    for bar in activity.bars:
        if bar.finish <= completion_cutoff:
            pending_index += 1
        else:
            break
    try:
        completed_quantity = _exact_decimal_sum(
            bar.quantity_percentage for bar in activity.bars[:pending_index]
        )
    except ValueError:
        return strict
    if abs(activity.overall_percent_complete - completed_quantity) > COMPLETION_ROUNDING_TOLERANCE:
        return strict
    return CompletionBoundary(
        True, pending_index, completion_cutoff, "completion cutoff prefix"
    )


def required_successor_shift(
    link: PrecedenceLink, predecessor: ActivitySpan, successor: ActivitySpan
) -> timedelta:
    """Return the forward movement needed to satisfy one precedence relation."""
    if link.precedence_type == "FinishStart":
        return max(timedelta(0), predecessor.finish + link.lag - successor.start)
    if link.precedence_type == "StartStart":
        return max(timedelta(0), predecessor.start + link.lag - successor.start)
    if link.precedence_type == "FinishFinish":
        return max(timedelta(0), predecessor.finish + link.lag - successor.finish)
    if link.precedence_type == "StartFinish":
        return max(timedelta(0), predecessor.start + link.lag - successor.finish)
    raise ValueError(f"Unsupported precedence type: {link.precedence_type}")


def compute_cascade(state: ProjectState, direct_shifts: Mapping[str, int]) -> CascadePlan:
    """Compute the complete, precedence-safe closure of direct civil-day shifts.

    The plan never repairs an unsafe request by modifying a user's direct
    movement or a completed native prefix.  Any uncertainty or conflict makes
    the result non-actionable by returning empty movement maps and violations.
    """
    violations: list[str] = []
    if not isinstance(direct_shifts, Mapping):
        return _cascade_failure(("direct shifts must be a mapping",))

    direct: dict[str, int] = {}
    for activity_id, shift in sorted(direct_shifts.items(), key=lambda item: str(item[0])):
        if not isinstance(activity_id, str) or not activity_id:
            violations.append("direct shift activity ID must be a non-empty string")
            continue
        if activity_id not in state.activities:
            violations.append(f"unknown direct activity ID '{activity_id}'")
            continue
        if type(shift) is not int:
            violations.append(f"direct shift for activity ID '{activity_id}' must be an integer civil day count")
            continue
        if shift == 0:
            violations.append(f"direct shift for activity ID '{activity_id}' must not be zero")
            continue
        direct[activity_id] = shift

    links = tuple(sorted(state.links, key=_link_sort_key))
    for link in links:
        try:
            _precedence_endpoint_kinds(link)
            _resolve_link_endpoints(link, state.activities)
        except ValueError as error:
            violations.append(f"link '{link.link_key}' is not evaluable: {error}")
    if violations:
        return _cascade_failure(violations)

    for link in links:
        try:
            originally_required = _original_link_shift(state, link)
        except OverflowError:
            violations.append(f"link '{link.link_key}' exceeds datetime range before movement")
            continue
        if originally_required > timedelta(0):
            violations.append(
                f"link '{link.link_key}' has a pre-existing precedence violation"
            )
    if violations:
        return _cascade_failure(violations)

    boundaries: dict[str, CompletionBoundary] = {}

    def boundary_for(activity_id: str) -> CompletionBoundary | None:
        cached = boundaries.get(activity_id)
        if cached is not None:
            return cached if cached.identifiable else None
        boundary = completion_boundary(state.activities[activity_id])
        boundaries[activity_id] = boundary
        if not boundary.identifiable:
            violations.append(
                f"activity ID '{activity_id}' has ambiguous completion: {boundary.reason}"
            )
            return None
        return boundary

    for activity_id in sorted(direct):
        boundary = boundary_for(activity_id)
        if boundary is not None:
            activity = state.activities[activity_id]
            if boundary.pending_bar_index == len(activity.bars):
                violations.append(f"fully completed activity ID '{activity_id}' cannot be moved")
            else:
                shift_error = _shift_range_error(activity, boundary, direct[activity_id])
                if shift_error is not None:
                    violations.append(shift_error)
    if violations:
        return _cascade_failure(violations)

    shifts = dict(direct)
    provenance = {activity_id: "direct" for activity_id in direct}

    # A direct backward move is never compensated by moving its predecessor.
    for link in links:
        if direct.get(link.successor_id, 0) >= 0:
            continue
        try:
            required = _required_link_shift(state, link, shifts, boundaries, boundary_for)
        except OverflowError:
            violations.append(f"link '{link.link_key}' exceeds datetime range after direct movement")
            continue
        if required is None:
            continue
        if required > timedelta(0):
            violations.append(
                f"incoming link '{link.link_key}' is violated by backward direct move of activity ID '{link.successor_id}'"
            )
    if violations:
        return _cascade_failure(violations)

    outgoing: dict[str, list[PrecedenceLink]] = {}
    for link in links:
        outgoing.setdefault(link.predecessor_id, []).append(link)
    queue = sorted(activity_id for activity_id, shift in direct.items() if shift > 0)
    revisits: dict[str, int] = {}
    revisit_limit = len(state.activities) * len(links) + 1
    cursor = 0
    while cursor < len(queue):
        predecessor_id = queue[cursor]
        cursor += 1
        for link in outgoing.get(predecessor_id, ()):
            try:
                required = _required_link_shift(state, link, shifts, boundaries, boundary_for)
            except OverflowError:
                violations.append(f"link '{link.link_key}' exceeds datetime range during cascade")
                continue
            if required is None:
                continue
            if required <= timedelta(0):
                continue
            successor_id = link.successor_id
            successor_boundary = boundary_for(successor_id)
            if successor_boundary is None:
                continue
            successor = state.activities[successor_id]
            if successor_boundary.pending_bar_index == len(successor.bars):
                violations.append(
                    f"fully completed successor activity ID '{successor_id}' requires movement for link '{link.link_key}'"
                )
                continue
            days = _round_up_civil_days(required)
            if successor_id in direct:
                if _has_precedence_path(outgoing, successor_id, predecessor_id):
                    violations.append(
                        f"precedence cycle or nonconvergence detected at activity ID '{successor_id}'"
                    )
                    continue
                violations.append(
                    f"direct shift for activity ID '{successor_id}' conflicts with required cascade from link '{link.link_key}'"
                )
                continue
            endpoint_bar = _resolve_endpoint_bar("successor", successor, link.successor_bar)
            if _bar_is_completed(successor, endpoint_bar, successor_boundary):
                violations.append(
                    f"completed prefix of successor activity ID '{successor_id}' prevents required movement for link '{link.link_key}'"
                )
                continue
            accumulated_shift = shifts.get(successor_id, 0) + days
            shift_error = _shift_range_error(successor, successor_boundary, accumulated_shift)
            if shift_error is not None:
                violations.append(shift_error)
                continue
            shifts[successor_id] = accumulated_shift
            provenance[successor_id] = "cascade"
            revisits[successor_id] = revisits.get(successor_id, 0) + 1
            if revisits[successor_id] > revisit_limit:
                violations.append(
                    f"precedence cycle or nonconvergence detected at activity ID '{successor_id}'"
                )
                continue
            queue.append(successor_id)
        if violations:
            return _cascade_failure(violations)

    ordered_ids = sorted(shifts)
    return CascadePlan(
        _cascade_evidence_map_from_dict(
            {activity_id: shifts[activity_id] for activity_id in ordered_ids}
        ),
        _cascade_evidence_map_from_dict(
            {activity_id: provenance[activity_id] for activity_id in ordered_ids}
        ),
        (),
    )


def _cascade_failure(violations: Iterable[str]) -> CascadePlan:
    return CascadePlan(
        _cascade_evidence_map_from_dict({}),
        _cascade_evidence_map_from_dict({}),
        tuple(sorted(set(violations))),
    )


def _original_link_shift(state: ProjectState, link: PrecedenceLink) -> timedelta:
    predecessor = state.activities[link.predecessor_id]
    successor = state.activities[link.successor_id]
    predecessor_bar = _resolve_endpoint_bar("predecessor", predecessor, link.predecessor_bar)
    successor_bar = _resolve_endpoint_bar("successor", successor, link.successor_bar)
    return required_successor_shift(
        link,
        ActivitySpan(predecessor_bar.start, predecessor_bar.finish),
        ActivitySpan(successor_bar.start, successor_bar.finish),
    )


def _shift_range_error(
    activity: NativeActivity, boundary: CompletionBoundary, shift_days: int
) -> str | None:
    """Reject shifts that cannot be represented without touching completed work."""
    try:
        delta = timedelta(days=shift_days)
    except OverflowError:
        return f"activity ID '{activity.activity_id}' shift exceeds datetime range"
    pending_index = boundary.pending_bar_index
    assert pending_index is not None
    for bar in activity.bars[pending_index:]:
        try:
            shifted_start = bar.start + delta
            bar.finish + delta
        except OverflowError:
            return f"activity ID '{activity.activity_id}' shift exceeds datetime range"
        if shift_days < 0 and boundary.at is not None and shifted_start < boundary.at:
            return (
                f"backward shift for activity ID '{activity.activity_id}' crosses protected "
                "completed prefix"
            )
    return None


def _has_precedence_path(
    outgoing: Mapping[str, Sequence[PrecedenceLink]], start_id: str, target_id: str
) -> bool:
    """Return whether the candidate direct conflict closes an actual directed cycle."""
    pending = [start_id]
    seen: set[str] = set()
    while pending:
        current = pending.pop()
        if current == target_id:
            return True
        if current in seen:
            continue
        seen.add(current)
        pending.extend(link.successor_id for link in outgoing.get(current, ()))
    return False


def _precedence_endpoint_kinds(link: PrecedenceLink) -> tuple[str, str]:
    kinds = {
        "FinishStart": ("finish", "start"),
        "StartStart": ("start", "start"),
        "FinishFinish": ("finish", "finish"),
        "StartFinish": ("start", "finish"),
    }.get(link.precedence_type)
    if kinds is None:
        raise ValueError(f"Unsupported precedence type: {link.precedence_type}")
    return kinds


def _required_link_shift(
    state: ProjectState,
    link: PrecedenceLink,
    shifts: Mapping[str, int],
    boundaries: Mapping[str, CompletionBoundary],
    boundary_for: Callable[[str], CompletionBoundary | None],
) -> timedelta | None:
    """Evaluate a link using its actual native endpoint bars, not view scope."""
    predecessor = state.activities[link.predecessor_id]
    successor = state.activities[link.successor_id]
    predecessor_bar = _resolve_endpoint_bar("predecessor", predecessor, link.predecessor_bar)
    successor_bar = _resolve_endpoint_bar("successor", successor, link.successor_bar)
    predecessor_shift = shifts.get(link.predecessor_id, 0)
    successor_shift = shifts.get(link.successor_id, 0)
    predecessor_span = _effective_shifted_bar_span(
        predecessor, predecessor_bar, predecessor_shift, boundaries, boundary_for
    )
    successor_span = _effective_shifted_bar_span(
        successor, successor_bar, successor_shift, boundaries, boundary_for
    )
    if predecessor_span is None or successor_span is None:
        return None
    return required_successor_shift(link, predecessor_span, successor_span)


def _effective_shifted_bar_span(
    activity: NativeActivity,
    bar: NativeBar,
    shift_days: int,
    boundaries: Mapping[str, CompletionBoundary],
    boundary_for: Callable[[str], CompletionBoundary | None],
) -> ActivitySpan | None:
    """Use raw endpoints until a real shift makes completion evidence necessary."""
    if shift_days == 0:
        return ActivitySpan(bar.start, bar.finish)
    boundary = boundaries.get(activity.activity_id)
    if boundary is None or not boundary.identifiable:
        boundary = boundary_for(activity.activity_id)
    if boundary is None:
        return None
    return _effective_bar_span(activity, bar, boundary, shift_days)


def _effective_bar_span(
    activity: NativeActivity, bar: NativeBar, boundary: CompletionBoundary, shift_days: int
) -> ActivitySpan:
    """Shift only bars in the identifiable pending suffix of an activity."""
    index = next(index for index, item in enumerate(activity.bars) if item is bar)
    pending_index = boundary.pending_bar_index
    assert pending_index is not None
    effective_shift = timedelta(days=shift_days) if index >= pending_index else timedelta(0)
    return ActivitySpan(bar.start + effective_shift, bar.finish + effective_shift)


def _bar_is_completed(
    activity: NativeActivity, bar: NativeBar, boundary: CompletionBoundary
) -> bool:
    assert boundary.pending_bar_index is not None
    return next(index for index, item in enumerate(activity.bars) if item is bar) < boundary.pending_bar_index


def _round_up_civil_days(value: timedelta) -> int:
    """Convert a positive duration to the minimum whole number of civil days."""
    assert value > timedelta(0)
    microseconds = _timedelta_microseconds(value)
    day_microseconds = _timedelta_microseconds(timedelta(days=1))
    return (microseconds + day_microseconds - 1) // day_microseconds


def project_fingerprint(state: ProjectState) -> str:
    """Hash the semantically relevant project model as canonical JSON."""
    _validate_project_state(state)
    payload = {
        "date_format": state.date_format,
        # The enabled VGS completed-through date changes which civil fragments
        # are protected.  It is therefore part of the project identity used to
        # reject stale local scenarios.
        "completion_cutoff": (
            state.completion_cutoff.isoformat()
            if state.completion_cutoff is not None
            else None
        ),
        "activities": [
            _activity_fingerprint_value(activity)
            for _, activity in sorted(state.activities.items(), key=lambda item: item[0])
        ],
        "links": [
            _link_fingerprint_value(link)
            for link in sorted(state.links, key=_link_sort_key)
        ],
    }
    canonical = json.dumps(
        payload, sort_keys=True, separators=(",", ":"), ensure_ascii=True
    )
    return sha256(canonical.encode("utf-8")).hexdigest()


def precedence_fingerprint(state: ProjectState) -> str:
    """Hash exactly the immutable precedence evidence carried by a scenario."""
    _validate_project_state(state)
    payload = [
        _link_fingerprint_value(link)
        for link in sorted(state.links, key=_link_sort_key)
    ]
    canonical = json.dumps(
        payload, sort_keys=True, separators=(",", ":"), ensure_ascii=True
    )
    return sha256(canonical.encode("utf-8")).hexdigest()


def compare_invariants(
    before: ProjectState,
    after: ProjectState,
    authorized_ids: Collection[str],
) -> InvariantReport:
    """Report every unsafe semantic change, allowing only authorized pending work."""
    differences: list[InvariantDifference] = []
    authorized = frozenset(authorized_ids)
    _report_state_identity_issues(differences, before, "before")
    _report_state_identity_issues(differences, after, "after")
    _report_unresolved_precedence_endpoints(differences, before, "before")
    _report_unresolved_precedence_endpoints(differences, after, "after")
    if before.date_format != after.date_format:
        differences.append(
            InvariantDifference(
                "date_format_changed", "", "project date format changed"
            )
        )

    before_ids = set(before.activities)
    after_ids = set(after.activities)
    for activity_id in sorted(before_ids - after_ids):
        differences.append(
            InvariantDifference("activity_removed", activity_id, "activity is missing")
        )
    for activity_id in sorted(after_ids - before_ids):
        differences.append(
            InvariantDifference("activity_added", activity_id, "unexpected activity exists")
        )

    for activity_id in sorted(before_ids & after_ids):
        prior = before.activities[activity_id]
        current = after.activities[activity_id]
        if prior.activity_id != activity_id or current.activity_id != activity_id:
            differences.append(
                InvariantDifference(
                    "activity_identity_ambiguous",
                    activity_id,
                    "activity mapping key and activity identity must agree",
                )
            )
            continue
        if activity_id not in authorized:
            if prior != current:
                differences.append(
                    InvariantDifference(
                        "collateral_activity_changed",
                        activity_id,
                        "activity is not authorized to change",
                    )
                )
            continue
        _compare_authorized_activity(
            differences, activity_id, prior, current,
            before.completion_cutoff, after.completion_cutoff,
            before.links, after.links,
        )

    _compare_precedence_links(differences, before.links, after.links)
    return InvariantReport(tuple(differences))


def _completion_evidence_error(activity: NativeActivity) -> str | None:
    if not Decimal("0") <= activity.overall_percent_complete <= Decimal("100"):
        return "overall percent is outside [0, 100]"
    for bar in activity.bars:
        if not Decimal("0") <= bar.quantity_percentage <= Decimal("100"):
            return f"quantity percentage for native bar '{bar.bar_key}' is outside [0, 100]"
        if not Decimal("0") <= bar.percent_complete <= Decimal("100"):
            return f"percent complete for native bar '{bar.bar_key}' is outside [0, 100]"
        if bar.complete_worked_minutes < 0:
            return f"complete worked minutes for native bar '{bar.bar_key}' is negative"
        if bar.percent_complete == 0 and bar.complete_worked_minutes > 0:
            return f"pending native bar '{bar.bar_key}' has complete worked minutes"
    return None


def _associated_links(
    activity: NativeActivity,
    left: NativeBar,
    right: NativeBar,
    links: Sequence[PrecedenceLink],
) -> tuple[AssociatedLinkReference, ...]:
    references: list[AssociatedLinkReference] = []
    anchor_bar_keys = {left.bar_key, right.bar_key}
    for link in links:
        if (
            link.predecessor_id == activity.activity_id
            and link.predecessor_bar in anchor_bar_keys
        ):
            references.append(
                AssociatedLinkReference(
                    link.link_key,
                    "predecessor",
                    link.predecessor_bar,
                    link.successor_id,
                    link.successor_bar,
                    link.precedence_type,
                    link.lag,
                    link.group_key,
                )
            )
        if link.successor_id == activity.activity_id and link.successor_bar in anchor_bar_keys:
            references.append(
                AssociatedLinkReference(
                    link.link_key,
                    "successor",
                    link.successor_bar,
                    link.predecessor_id,
                    link.predecessor_bar,
                    link.precedence_type,
                    link.lag,
                    link.group_key,
                )
            )
    return tuple(sorted(references, key=_associated_link_sort_key))


def _associated_link_sort_key(reference: AssociatedLinkReference) -> tuple[object, ...]:
    return (
        reference.link_key,
        reference.role,
        reference.endpoint_bar,
        reference.other_activity_id,
        reference.other_bar,
        reference.precedence_type,
        _timedelta_microseconds(reference.lag),
        reference.group_key,
    )


def _validate_project_state(state: ProjectState) -> None:
    for mapping_key, activity in state.activities.items():
        if mapping_key != activity.activity_id:
            raise ValueError(
                f"activity mapping key '{mapping_key}' does not match activity ID "
                f"'{activity.activity_id}'"
            )
        _validate_unique_bar_identities(activity)
    link_keys: set[str] = set()
    for link in state.links:
        if link.link_key in link_keys:
            raise ValueError(f"duplicate PrecedenceLink.link_key '{link.link_key}'")
        link_keys.add(link.link_key)
        _resolve_link_endpoints(link, state.activities)


def _report_state_identity_issues(
    differences: list[InvariantDifference], state: ProjectState, label: str
) -> None:
    for mapping_key, activity in state.activities.items():
        if mapping_key != activity.activity_id:
            differences.append(
                InvariantDifference(
                    "activity_mapping_identity_ambiguous",
                    mapping_key,
                    f"{label} mapping key does not match activity ID '{activity.activity_id}'",
                )
            )
        for detail in _bar_identity_errors(activity):
            differences.append(
                InvariantDifference("native_identity_ambiguous", mapping_key, f"{label} state: {detail}")
            )


def _report_unresolved_precedence_endpoints(
    differences: list[InvariantDifference], state: ProjectState, label: str
) -> None:
    for link in state.links:
        try:
            _resolve_link_endpoints(link, state.activities)
        except ValueError as error:
            differences.append(
                InvariantDifference(
                    "precedence_endpoint_unresolved",
                    link.predecessor_id,
                    f"{label} link '{link.link_key}': {error}",
                )
            )


def _resolve_link_endpoints(
    link: PrecedenceLink, activities: Mapping[str, NativeActivity]
) -> None:
    predecessor = activities.get(link.predecessor_id)
    successor = activities.get(link.successor_id)
    if predecessor is None:
        raise ValueError(f"unknown predecessor activity ID '{link.predecessor_id}'")
    if successor is None:
        raise ValueError(f"unknown successor activity ID '{link.successor_id}'")
    _resolve_endpoint_bar("predecessor", predecessor, link.predecessor_bar)
    _resolve_endpoint_bar("successor", successor, link.successor_bar)


def _compare_native_identity_survival(
    differences: list[InvariantDifference],
    activity_id: str,
    prior: NativeActivity,
    current: NativeActivity,
) -> None:
    current_identities: dict[tuple[str, str], int] = {}
    for bar in current.bars:
        identity = (bar.xml_key, bar.bar_key)
        current_identities[identity] = current_identities.get(identity, 0) + 1
    for bar in prior.bars:
        identity = (bar.xml_key, bar.bar_key)
        count = current_identities.get(identity, 0)
        if count != 1:
            differences.append(
                InvariantDifference(
                    "native_identity_missing",
                    activity_id,
                    f"native identity xml='{bar.xml_key}' bar='{bar.bar_key}' occurs {count} times after",
                )
            )


def _compare_subdivision_topology(
    differences: list[InvariantDifference],
    activity_id: str,
    prior: NativeActivity,
    current: NativeActivity,
) -> None:
    segments_by_original: list[list[NativeBar]] = [[] for _ in prior.bars]
    previous_original_index = -1
    invalid = False
    for bar in current.bars:
        matches = [
            index
            for index, original in enumerate(prior.bars)
            if bar.start >= original.start and bar.finish <= original.finish and bar.start < bar.finish
        ]
        if len(matches) != 1:
            invalid = True
            continue
        index = matches[0]
        if index < previous_original_index:
            invalid = True
        previous_original_index = index
        segments_by_original[index].append(bar)
    for original, segments in zip(prior.bars, segments_by_original):
        if not segments:
            invalid = True
            continue
        if segments[0].start != original.start or segments[-1].finish != original.finish:
            invalid = True
        if any(left.finish != right.start for left, right in zip(segments, segments[1:])):
            invalid = True
        try:
            segment_quantity = _exact_decimal_sum(
                bar.quantity_percentage for bar in segments
            )
        except ValueError:
            invalid = True
        else:
            if segment_quantity != original.quantity_percentage:
                invalid = True
    if invalid:
        differences.append(
            InvariantDifference(
                "subdivision_invalid",
                activity_id,
                "after bars are not contiguous, complete subdivisions of original native intervals",
            )
        )


def _activity_fingerprint_value(activity: NativeActivity) -> dict[str, object]:
    return {
        "activity_id": activity.activity_id,
        "collection_key": activity.collection_key,
        "overall_percent_complete": _decimal_text(activity.overall_percent_complete),
        "bars": [
            {
                "bar_key": bar.bar_key,
                "start": bar.start.isoformat(),
                "finish": bar.finish.isoformat(),
                "quantity_percentage": _decimal_text(bar.quantity_percentage),
                "percent_complete": _decimal_text(bar.percent_complete),
                "complete_worked_minutes": _decimal_text(bar.complete_worked_minutes),
            }
            for bar in activity.bars
        ],
    }


def _link_fingerprint_value(link: PrecedenceLink) -> dict[str, object]:
    return {
        "link_key": link.link_key,
        "predecessor_id": link.predecessor_id,
        "successor_id": link.successor_id,
        "predecessor_bar": link.predecessor_bar,
        "successor_bar": link.successor_bar,
        "precedence_type": link.precedence_type,
        "lag_microseconds": _timedelta_microseconds(link.lag),
        "group_key": link.group_key,
    }


def _link_sort_key(link: PrecedenceLink) -> tuple[object, ...]:
    return (
        link.link_key,
        link.predecessor_id,
        link.successor_id,
        link.predecessor_bar,
        link.successor_bar,
        link.precedence_type,
        _timedelta_microseconds(link.lag),
        link.group_key,
    )


def _timedelta_microseconds(value: timedelta) -> int:
    return value.days * 86_400_000_000 + value.seconds * 1_000_000 + value.microseconds


def _decimal_text(value: Decimal) -> str:
    """Return a scale-independent, non-exponent decimal JSON representation."""
    if value == 0:
        return "0"
    return format(value.normalize(), "f")


def _compare_authorized_activity(
    differences: list[InvariantDifference],
    activity_id: str,
    prior: NativeActivity,
    current: NativeActivity,
    before_cutoff: datetime | None,
    after_cutoff: datetime | None,
    before_links: Sequence[PrecedenceLink],
    after_links: Sequence[PrecedenceLink],
) -> None:
    if prior.collection_key != current.collection_key:
        differences.append(
            InvariantDifference(
                "activity_identity_changed", activity_id, "bar collection identity changed"
            )
        )
    if prior.overall_percent_complete != current.overall_percent_complete:
        differences.append(
            InvariantDifference(
                "completion_changed", activity_id, "overall completion changed"
            )
        )
    try:
        prior_quantity = _exact_decimal_sum(bar.quantity_percentage for bar in prior.bars)
        current_quantity = _exact_decimal_sum(bar.quantity_percentage for bar in current.bars)
    except ValueError:
        differences.append(
            InvariantDifference(
                "native_quantity_changed", activity_id, "native quantity cannot be represented exactly"
            )
        )
    else:
        if prior_quantity != current_quantity:
            differences.append(
                InvariantDifference(
                    "native_quantity_changed", activity_id, "total native quantity changed"
                )
            )

    _compare_native_identity_survival(differences, activity_id, prior, current)
    _compare_subdivision_topology(differences, activity_id, prior, current)

    before_boundary = _invariant_completion_boundary(prior, before_cutoff)
    after_boundary = _invariant_completion_boundary(current, after_cutoff)
    if not before_boundary.identifiable:
        differences.append(
            InvariantDifference(
                "completion_boundary_ambiguous",
                activity_id,
                f"before state: {before_boundary.reason}",
            )
        )
    if not after_boundary.identifiable:
        differences.append(
            InvariantDifference(
                "completion_boundary_ambiguous",
                activity_id,
                f"after state: {after_boundary.reason}",
            )
        )
    if before_boundary.identifiable and after_boundary.identifiable:
        # ``at`` is the start of the first pending native bar.  That bar is
        # intentionally movable; immutable completion is represented by the
        # prefix identity/index and is enforced below with exact bar checks.
        if (
            before_boundary.pending_bar_index != after_boundary.pending_bar_index
            or before_boundary.reason != after_boundary.reason
        ):
            differences.append(
                InvariantDifference(
                    "completion_boundary_changed",
                    activity_id,
                    "completed native prefix boundary changed",
                )
            )
        _compare_completed_prefix(differences, activity_id, prior, current, before_boundary)

    try:
        before_anchors = native_anchors(prior, before_links)
    except ValueError as error:
        differences.append(
            InvariantDifference("native_anchor_ambiguous", activity_id, f"before state: {error}")
        )
        before_anchors = ()
    try:
        after_anchors = native_anchors(current, after_links)
    except ValueError as error:
        differences.append(
            InvariantDifference("native_anchor_ambiguous", activity_id, f"after state: {error}")
        )
        after_anchors = ()
    for anchor in before_anchors:
        if anchor not in after_anchors:
            differences.append(
                InvariantDifference(
                    "native_anchor_missing",
                    activity_id,
                    f"native anchor {anchor.left_bar_key}->{anchor.right_bar_key} at "
                    f"{anchor.at.isoformat()} is absent",
                )
            )


def _compare_completed_prefix(
    differences: list[InvariantDifference],
    activity_id: str,
    prior: NativeActivity,
    current: NativeActivity,
    boundary: CompletionBoundary,
) -> None:
    assert boundary.pending_bar_index is not None
    current_by_identity = {
        (bar.xml_key, bar.bar_key): bar
        for bar in current.bars
    }
    for bar in prior.bars[: boundary.pending_bar_index]:
        if current_by_identity.get((bar.xml_key, bar.bar_key)) != bar:
            differences.append(
                InvariantDifference(
                    "completed_prefix_changed",
                    activity_id,
                    f"completed native bar '{bar.bar_key}' changed or is missing",
                )
            )


def _compare_precedence_links(
    differences: list[InvariantDifference],
    before: Sequence[PrecedenceLink],
    after: Sequence[PrecedenceLink],
) -> None:
    before_by_key = _links_by_key(before, "before", differences)
    after_by_key = _links_by_key(after, "after", differences)
    if before_by_key is None or after_by_key is None:
        return
    for link_key in sorted(set(before_by_key) - set(after_by_key)):
        link = before_by_key[link_key]
        differences.append(
            InvariantDifference(
                "precedence_removed",
                link.predecessor_id,
                f"precedence link '{link_key}' is missing",
            )
        )
    for link_key in sorted(set(after_by_key) - set(before_by_key)):
        link = after_by_key[link_key]
        differences.append(
            InvariantDifference(
                "precedence_added",
                link.predecessor_id,
                f"unexpected precedence link '{link_key}' exists",
            )
        )
    for link_key in sorted(set(before_by_key) & set(after_by_key)):
        if before_by_key[link_key] != after_by_key[link_key]:
            differences.append(
                InvariantDifference(
                    "precedence_changed",
                    before_by_key[link_key].predecessor_id,
                    f"precedence link '{link_key}' changed",
                )
            )


def _links_by_key(
    links: Sequence[PrecedenceLink],
    label: str,
    differences: list[InvariantDifference],
) -> dict[str, PrecedenceLink] | None:
    result: dict[str, PrecedenceLink] = {}
    for link in links:
        if link.link_key in result:
            differences.append(
                InvariantDifference(
                    "precedence_ambiguous",
                    link.predecessor_id,
                    f"{label} state has duplicate precedence key '{link.link_key}'",
                )
            )
            return None
        result[link.link_key] = link
    return result


def ticks_to_datetime(value: str) -> datetime:
    """Convert .NET DateTime ticks using integer arithmetic."""
    ticks = int(value)
    return DOTNET_EPOCH + timedelta(microseconds=ticks // 10)


def timespan_ticks(value: str) -> timedelta:
    """Convert .NET TimeSpan ticks using integer arithmetic."""
    return timedelta(microseconds=int(value) // 10)


def read_vgantt(path: Path) -> ProjectState:
    """Read the native Gantt XML archive without loading Maptek libraries."""
    project_path = Path(path)
    with ZipFile(project_path) as archive:
        activities_xml = _read_instances(archive, "Activity")
        collections_xml = _read_instances(archive, "ActivityBarCollection")
        bars_xml = _read_instances(archive, "ActivityBar")
        # VGS does not write this archive entry until a project has its first
        # precedence.  Its absence therefore means an empty link collection.
        links_xml = _read_instances(archive, "ActivityLink", allow_missing=True)
        date_format = archive.read("ManualSaved/ProjectDateTimeFormat").decode("utf-8-sig")
        completion_cutoff = _completion_cutoff_from_archive(archive)

    activity_instances = _instances_by_key(activities_xml, "Activity")
    bar_collections = _instances_by_key(collections_xml, "ActivityBarCollection")
    bar_instances = _instances_by_key(bars_xml, "ActivityBar")
    link_instances = _instances_by_key(links_xml, "ActivityLink")
    bars = {xml_key: _native_bar(instance) for xml_key, instance in bar_instances.items()}

    activities_by_xml_key: dict[str, NativeActivity] = {}
    activities: dict[str, NativeActivity] = {}
    for xml_key, instance in activity_instances.items():
        collection_key = _field_value(instance, "m_Bars")
        if collection_key not in bar_collections:
            raise ValueError(
                f"Activity XML key '{xml_key}' references unknown "
                f"ActivityBarCollection XML key '{collection_key}'"
            )
        collection = bar_collections[collection_key]
        ordered_bar_keys = []
        for item in _field(collection, "m_Bars").findall("item"):
            bar_key = item.attrib.get("value")
            if bar_key is None:
                raise ValueError(
                    f"ActivityBarCollection XML key '{collection_key}' has a bar "
                    "reference without a value"
                )
            ordered_bar_keys.append(bar_key)
        for bar_key in ordered_bar_keys:
            if bar_key not in bars:
                raise ValueError(
                    f"ActivityBarCollection XML key '{collection_key}' references "
                    f"unknown ActivityBar XML key '{bar_key}'"
                )
        activity = NativeActivity(
            activity_id=_field_value(instance, "m_Id"),
            collection_key=collection_key,
            bars=tuple(bars[bar_key] for bar_key in ordered_bar_keys),
            overall_percent_complete=Decimal(_field_value(collection, "m_PercentComplete")),
        )
        _validate_unique_bar_identities(activity)
        if activity.activity_id in activities:
            raise ValueError(f"duplicate activity ID '{activity.activity_id}'")
        activities_by_xml_key[xml_key] = activity
        activities[activity.activity_id] = activity

    links: list[PrecedenceLink] = []
    link_keys: set[str] = set()
    for instance in link_instances.values():
        link = _precedence_link(instance, activities_by_xml_key)
        if link.link_key in link_keys:
            raise ValueError(f"duplicate PrecedenceLink.link_key '{link.link_key}'")
        link_keys.add(link.link_key)
        links.append(link)
    return ProjectState(
        path=project_path,
        date_format=date_format,
        activities=MappingProxyType(activities),
        links=tuple(links),
        completion_cutoff=completion_cutoff,
    )


def _read_instances(
    archive: ZipFile, name: str, *, allow_missing: bool = False
) -> tuple[ET.Element, ...]:
    entry = f"AutoSaved/{name}"
    try:
        contents = archive.read(entry)
    except KeyError:
        if allow_missing:
            return ()
        raise
    root = ET.fromstring(contents)
    return tuple(root.findall("instance"))


def _completion_cutoff_from_archive(archive: ZipFile) -> datetime | None:
    """Read VGS' enabled task-time-phase completed-through date, fail closed."""
    try:
        setup = ET.fromstring(archive.read("AutoSaved/GeneralSetup"))
        instances = setup.findall("instance")
        if len(instances) != 1:
            return None
        instance = instances[0]
        enabled = _strict_optional_setup_field(
            instance, "m_UseTaskTimePhasePercentCompleteUpdate", "Boolean"
        )
        if enabled != "True":
            return None
        value = _strict_optional_setup_field(
            instance, "m_ProjectPlanStartDate", "DateTime"
        )
        if value is None:
            return None
        ticks = int(value)
        if ticks <= 0 or ticks % 10:
            return None
        return ticks_to_datetime(value)
    except (KeyError, ValueError, OverflowError, ET.ParseError):
        return None


def _strict_optional_setup_field(
    instance: ET.Element, name: str, expected_type: str
) -> str | None:
    """Return one typed GeneralSetup field, otherwise reject it fail-closed."""
    matches = [
        field for field in instance.findall("field")
        if field.attrib.get("name") == name
    ]
    if len(matches) != 1:
        return None
    field = matches[0]
    if field.attrib.get("type") != expected_type:
        return None
    return field.attrib.get("value")


def _instances_by_key(
    instances: tuple[ET.Element, ...], instance_type: str
) -> dict[str, ET.Element]:
    keyed_instances: dict[str, ET.Element] = {}
    for instance in instances:
        xml_key = instance.attrib.get("key")
        if xml_key is None:
            raise ValueError(f"{instance_type} XML instance is missing a key")
        if xml_key in keyed_instances:
            raise ValueError(f"duplicate {instance_type} XML key '{xml_key}'")
        keyed_instances[xml_key] = instance
    return keyed_instances


def _validate_unique_bar_identities(activity: NativeActivity) -> None:
    bar_keys: set[str] = set()
    xml_keys: set[str] = set()
    for bar in activity.bars:
        if bar.bar_key in bar_keys:
            raise ValueError(
                f"duplicate NativeBar.bar_key '{bar.bar_key}' in activity ID "
                f"'{activity.activity_id}'"
            )
        bar_keys.add(bar.bar_key)
        if bar.xml_key in xml_keys:
            raise ValueError(
                f"duplicate NativeBar.xml_key '{bar.xml_key}' in activity ID "
                f"'{activity.activity_id}'"
            )
        xml_keys.add(bar.xml_key)


def _bar_identity_errors(activity: NativeActivity) -> tuple[str, ...]:
    errors: list[str] = []
    try:
        _validate_unique_bar_identities(activity)
    except ValueError as error:
        errors.append(str(error))
    return tuple(errors)


def _resolve_endpoint_bar(
    endpoint: str, activity: NativeActivity, bar_key: str
) -> NativeBar:
    matches = [bar for bar in activity.bars if bar.bar_key == bar_key]
    if len(matches) != 1:
        raise ValueError(
            f"{endpoint} bar '{bar_key}' does not resolve uniquely in activity ID "
            f"'{activity.activity_id}'"
        )
    return matches[0]


def _native_bar(instance: ET.Element) -> NativeBar:
    return NativeBar(
        xml_key=instance.attrib["key"],
        bar_key=_field_value(instance, "m_Key"),
        start=ticks_to_datetime(_field_value(instance, "m_StartTime")),
        finish=ticks_to_datetime(_field_value(instance, "m_FinishTime")),
        quantity_percentage=Decimal(_field_value(instance, "m_QuantityPercentage")),
        percent_complete=Decimal(_field_value(instance, "m_PercentComplete")),
        complete_worked_minutes=Decimal(_field_value(instance, "m_CompleteWorkedMinutes")),
    )


def _precedence_link(
    instance: ET.Element, activities_by_xml_key: Mapping[str, NativeActivity]
) -> PrecedenceLink:
    predecessor_xml_key = _field_value(instance, "m_Predecessor")
    successor_xml_key = _field_value(instance, "m_Successor")
    if predecessor_xml_key not in activities_by_xml_key:
        raise ValueError(
            f"unknown predecessor activity XML key '{predecessor_xml_key}'"
        )
    if successor_xml_key not in activities_by_xml_key:
        raise ValueError(f"unknown successor activity XML key '{successor_xml_key}'")
    predecessor = activities_by_xml_key[predecessor_xml_key]
    successor = activities_by_xml_key[successor_xml_key]
    predecessor_bar = _field_value(instance, "m_PredecessorBar")
    successor_bar = _field_value(instance, "m_SuccessorBar")
    _resolve_endpoint_bar("predecessor", predecessor, predecessor_bar)
    _resolve_endpoint_bar("successor", successor, successor_bar)
    return PrecedenceLink(
        link_key=_field_value(instance, "m_Key"),
        predecessor_id=predecessor.activity_id,
        successor_id=successor.activity_id,
        predecessor_bar=predecessor_bar,
        successor_bar=successor_bar,
        precedence_type=_field_value(instance, "m_PrecedenceType"),
        lag=timespan_ticks(_field_value(instance, "m_Lag")),
        group_key=_field_value(instance, "m_PrecedenceGroup"),
    )


def _field(instance: ET.Element, name: str) -> ET.Element:
    for field in instance.findall("field"):
        if field.attrib.get("name") == name:
            return field
    raise ValueError(
        f"XML instance '{instance.attrib.get('key')}' missing required field '{name}'"
    )


def _field_value(instance: ET.Element, name: str) -> str:
    field = _field(instance, name)
    if "value" not in field.attrib:
        raise ValueError(
            f"XML instance '{instance.attrib.get('key')}' has no value for required field "
            f"'{name}'"
        )
    return field.attrib["value"]
