"""Immutable, fail-closed wire contract for applying STP V2 daily fragments."""

from __future__ import annotations

from dataclasses import dataclass, replace
from datetime import date, datetime, time, timedelta
from decimal import Decimal, InvalidOperation
from pathlib import Path
from types import MappingProxyType
from typing import Literal, Mapping

from stp_v2_core import (
    ActivitySpan,
    NativeActivity,
    NativeBar,
    ProjectState,
    ValidationDifference,
    ValidationReport,
    compare_invariants,
    precedence_fingerprint,
    project_fingerprint,
    required_successor_shift,
    segment_activity_for_display,
)


APPLY_PACKAGE_SCHEMA = "vgs-stp-apply/1"


@dataclass(frozen=True)
class ApplyFragment:
    id: str
    native_bar_xml_key: str
    native_bar_key: str
    logical_unit_id: str
    logical_unit_date: date
    provenance: Literal["unchanged", "direct", "cascade"]
    start: datetime
    finish: datetime
    original_start: datetime
    original_finish: datetime
    quantity_percentage: Decimal
    locked_completed: bool


@dataclass(frozen=True)
class ApplyActivity:
    activity_id: str
    fragments: tuple[ApplyFragment, ...]


@dataclass(frozen=True)
class ApplyPackage:
    schema: str
    project_fingerprint: str
    precedence_fingerprint: str
    horizon_start: datetime
    horizon_finish: datetime
    activities: tuple[ApplyActivity, ...]


_PACKAGE_FIELDS = frozenset((
    "schema", "project_fingerprint", "precedence_fingerprint", "horizon_start",
    "horizon_finish", "activities",
))
_ACTIVITY_FIELDS = frozenset(("activity_id", "fragments"))
_FRAGMENT_FIELDS = frozenset((
    "id", "native_bar_xml_key", "native_bar_key", "logical_unit_id",
    "logical_unit_date", "provenance", "start", "finish", "original_start",
    "original_finish", "quantity_percentage", "locked_completed",
))


def load_apply_package(value: object) -> ApplyPackage:
    """Parse one exact JSON-shaped application package without coercion."""
    raw = _mapping(value, "package", _PACKAGE_FIELDS)
    schema = _text(raw["schema"], "package.schema")
    if schema != APPLY_PACKAGE_SCHEMA:
        raise ValueError("unsupported apply package schema")
    activities_value = raw["activities"]
    if type(activities_value) is not list:
        raise ValueError("package.activities must be an exact list")
    activities = tuple(_load_activity(item, index) for index, item in enumerate(activities_value))
    _unique((activity.activity_id for activity in activities), "activity IDs")
    all_fragment_ids = [fragment.id for activity in activities for fragment in activity.fragments]
    _unique(all_fragment_ids, "fragment IDs")
    horizon_start = _datetime(raw["horizon_start"], "package.horizon_start")
    horizon_finish = _datetime(raw["horizon_finish"], "package.horizon_finish")
    if (
        horizon_start.time() != time.min
        or horizon_finish.time() != time.min
        or horizon_start >= horizon_finish
    ):
        raise ValueError("package must contain an ordered midnight civil horizon")
    return ApplyPackage(
        schema=schema,
        project_fingerprint=_text(raw["project_fingerprint"], "package.project_fingerprint"),
        precedence_fingerprint=_text(raw["precedence_fingerprint"], "package.precedence_fingerprint"),
        horizon_start=horizon_start,
        horizon_finish=horizon_finish,
        activities=activities,
    )


def apply_package_to_json_value(package: ApplyPackage) -> dict[str, object]:
    """Return the canonical JSON value for an already parsed package."""
    if type(package) is not ApplyPackage:
        raise TypeError("package must be an exact ApplyPackage")
    return {
        "schema": package.schema,
        "project_fingerprint": package.project_fingerprint,
        "precedence_fingerprint": package.precedence_fingerprint,
        "horizon_start": package.horizon_start.isoformat(),
        "horizon_finish": package.horizon_finish.isoformat(),
        "activities": [
            {"activity_id": activity.activity_id, "fragments": [
                {
                    "id": fragment.id,
                    "native_bar_xml_key": fragment.native_bar_xml_key,
                    "native_bar_key": fragment.native_bar_key,
                    "logical_unit_id": fragment.logical_unit_id,
                    "logical_unit_date": fragment.logical_unit_date.isoformat(),
                    "provenance": fragment.provenance,
                    "start": fragment.start.isoformat(),
                    "finish": fragment.finish.isoformat(),
                    "original_start": fragment.original_start.isoformat(),
                    "original_finish": fragment.original_finish.isoformat(),
                    "quantity_percentage": _decimal_text(fragment.quantity_percentage),
                    "locked_completed": fragment.locked_completed,
                }
                for fragment in activity.fragments
            ]}
            for activity in package.activities
        ],
    }


def validate_apply_package(package: ApplyPackage, state: ProjectState) -> ValidationReport:
    """Return every deterministically detectable safety difference without mutation."""
    differences: list[ValidationDifference] = []
    if type(package) is not ApplyPackage:
        return ValidationReport((ValidationDifference("package_type_invalid", "", "package is not ApplyPackage"),))
    if type(state) is not ProjectState:
        return ValidationReport((ValidationDifference("state_type_invalid", "", "state is not ProjectState"),))
    _fingerprints(differences, package, state)
    by_activity = {activity.activity_id: activity for activity in package.activities}
    proposed: dict[tuple[str, str, str], tuple[datetime, datetime]] = {}
    for activity in package.activities:
        _validate_activity(differences, activity, state, proposed)
    _validate_precedences(differences, state, proposed)
    _validate_invariants(differences, state, proposed, by_activity)
    return ValidationReport(tuple(differences))


def _load_activity(value: object, index: int) -> ApplyActivity:
    raw = _mapping(value, f"activities[{index}]", _ACTIVITY_FIELDS)
    fragments_value = raw["fragments"]
    if type(fragments_value) is not list or not fragments_value:
        raise ValueError(f"activities[{index}].fragments must be a non-empty exact list")
    return ApplyActivity(
        _text(raw["activity_id"], f"activities[{index}].activity_id"),
        tuple(_load_fragment(item, index, fragment_index) for fragment_index, item in enumerate(fragments_value)),
    )


def _load_fragment(value: object, activity_index: int, index: int) -> ApplyFragment:
    label = f"activities[{activity_index}].fragments[{index}]"
    raw = _mapping(value, label, _FRAGMENT_FIELDS)
    provenance = _text(raw["provenance"], label + ".provenance")
    if provenance not in ("unchanged", "direct", "cascade"):
        raise ValueError(label + ".provenance must be unchanged, direct or cascade")
    locked = raw["locked_completed"]
    if type(locked) is not bool:
        raise ValueError(label + ".locked_completed must be an exact boolean")
    return ApplyFragment(
        _text(raw["id"], label + ".id"),
        _text(raw["native_bar_xml_key"], label + ".native_bar_xml_key"),
        _text(raw["native_bar_key"], label + ".native_bar_key"),
        _text(raw["logical_unit_id"], label + ".logical_unit_id"),
        _date(raw["logical_unit_date"], label + ".logical_unit_date"),
        provenance, _datetime(raw["start"], label + ".start"), _datetime(raw["finish"], label + ".finish"),
        _datetime(raw["original_start"], label + ".original_start"),
        _datetime(raw["original_finish"], label + ".original_finish"),
        _decimal(raw["quantity_percentage"], label + ".quantity_percentage"), locked,
    )


def _validate_activity(
    differences: list[ValidationDifference], activity: ApplyActivity, state: ProjectState,
    proposed: dict[tuple[str, str, str], tuple[datetime, datetime]],
) -> None:
    if activity.activity_id not in state.activities:
        differences.append(ValidationDifference("activity_unknown", activity.activity_id, "activity does not exist"))
        return
    fragments = activity.fragments
    expected = {
        fragment.id: fragment
        for fragment in segment_activity_for_display(
            state.activities[activity.activity_id], state.completion_cutoff
        )
    }
    supplied = {fragment.id: fragment for fragment in fragments}
    if set(supplied) != set(expected):
        differences.append(ValidationDifference(
            "canonical_fragment_mismatch", activity.activity_id,
            "fragment IDs do not match the canonical daily segmentation",
        ))
    by_bar: dict[tuple[str, str, str], list[ApplyFragment]] = {}
    by_unit: dict[str, list[ApplyFragment]] = {}
    for fragment in fragments:
        key = (activity.activity_id, fragment.native_bar_xml_key, fragment.native_bar_key)
        by_bar.setdefault(key, []).append(fragment)
        by_unit.setdefault(fragment.logical_unit_id, []).append(fragment)
        original = expected.get(fragment.id)
        if original is None or (
            fragment.native_bar_xml_key != original.native_bar_xml_key
            or fragment.native_bar_key != original.native_bar_key
            or fragment.original_start != original.original_start
            or fragment.original_finish != original.original_finish
            or fragment.quantity_percentage != original.quantity_percentage
            or fragment.locked_completed != original.locked_completed
            or fragment.logical_unit_date != original.logical_unit_day
            or fragment.logical_unit_id != f"{activity.activity_id}:{original.logical_unit_day.isoformat()}"
        ):
            differences.append(ValidationDifference("canonical_fragment_mismatch", activity.activity_id, fragment.id))
        if fragment.start >= fragment.finish or fragment.original_start >= fragment.original_finish:
            differences.append(ValidationDifference("interval_invalid", activity.activity_id, fragment.id))
            continue
        delta_start, delta_finish = fragment.start - fragment.original_start, fragment.finish - fragment.original_finish
        moved = (
            fragment.start != fragment.original_start
            or fragment.finish != fragment.original_finish
        )
        if fragment.provenance == "unchanged" and moved:
            differences.append(ValidationDifference(
                "unchanged_fragment_moved", activity.activity_id, fragment.id
            ))
        elif fragment.provenance in ("direct", "cascade") and not moved:
            differences.append(ValidationDifference(
                "movement_provenance_without_shift", activity.activity_id,
                fragment.id,
            ))
        if delta_start != delta_finish or not _integer_civil_days(delta_start):
            differences.append(ValidationDifference("civil_shift_invalid", activity.activity_id, fragment.id))
        if original is not None and original.locked_completed and (
            fragment.start != fragment.original_start or fragment.finish != fragment.original_finish
        ):
            differences.append(ValidationDifference("completed_fragment_moved", activity.activity_id, fragment.id))
    for key, grouped in by_bar.items():
        ordered = sorted(grouped, key=lambda item: (item.original_start, item.original_finish, item.id))
        native_bar = next(
            (
                bar for bar in state.activities[activity.activity_id].bars
                if bar.xml_key == key[1] and bar.bar_key == key[2]
            ),
            None,
        )
        proposed[key] = (min(item.start for item in grouped), max(item.finish for item in grouped))
    for unit_id, grouped in by_unit.items():
        ordered = sorted(grouped, key=lambda item: (item.original_start, item.original_finish, item.id))
        shift = ordered[0].start - ordered[0].original_start
        if len({item.provenance for item in ordered}) != 1:
            differences.append(ValidationDifference(
                "logical_unit_provenance_mixed", activity.activity_id, unit_id
            ))
        if any(item.start - item.original_start != shift for item in ordered):
            differences.append(ValidationDifference("logical_unit_separated", activity.activity_id, unit_id))
        current = sorted(grouped, key=lambda item: (item.start, item.finish, item.id))
        if [item.id for item in current] != [item.id for item in ordered]:
            differences.append(ValidationDifference("logical_unit_order_changed", activity.activity_id, unit_id))
    current = sorted(fragments, key=lambda item: (item.start, item.finish, item.id))
    if any(left.finish > right.start for left, right in zip(current, current[1:])):
        differences.append(ValidationDifference("activity_overlap", activity.activity_id, "proposed fragments overlap"))
    _validate_transformed_anchors(differences, activity.activity_id, state, proposed)


def _fingerprints(differences: list[ValidationDifference], package: ApplyPackage, state: ProjectState) -> None:
    try:
        expected_project, expected_precedence = project_fingerprint(state), precedence_fingerprint(state)
    except (TypeError, ValueError) as error:
        differences.append(ValidationDifference("state_invalid", "", str(error)))
        return
    if package.project_fingerprint != expected_project:
        differences.append(ValidationDifference("project_fingerprint_mismatch", "", "project fingerprint differs"))
    if package.precedence_fingerprint != expected_precedence:
        differences.append(ValidationDifference("precedence_fingerprint_mismatch", "", "precedence fingerprint differs"))


def _validate_transformed_anchors(
    differences: list[ValidationDifference], activity_id: str, state: ProjectState,
    proposed: Mapping[tuple[str, str, str], tuple[datetime, datetime]],
) -> None:
    bars = state.activities[activity_id].bars
    for left, right in zip(bars, bars[1:]):
        if left.finish != right.start:
            # A gap already present in VGS is not a split boundary.  It must
            # remain a gap, but it cannot constrain the independently
            # scheduled pending work on either side.
            continue
        if _is_civil_midnight_boundary(left, right):
            # A native join at a civil day boundary is a scheduling boundary.
            # VGS can encode it either at midnight or as a shared inclusive
            # 23:59:59 end-of-day marker.  STP may leave a pause by moving the
            # later unit.
            continue
        left_span = proposed.get((activity_id, left.xml_key, left.bar_key))
        right_span = proposed.get((activity_id, right.xml_key, right.bar_key))
        if left_span is None or right_span is None or left_span[1] != right_span[0]:
            differences.append(ValidationDifference(
                "native_anchor_invalid", activity_id,
                f"native anchor {left.bar_key}->{right.bar_key} is not preserved after movement",
            ))


def _is_civil_midnight_boundary(left: NativeBar, right: NativeBar) -> bool:
    """Return whether a preserved native join is a VGS civil-day boundary."""
    return (
        left.finish == right.start
        and left.finish.time() in {time.min, time(23, 59, 59)}
    )


def _validate_precedences(
    differences: list[ValidationDifference], state: ProjectState,
    proposed: Mapping[tuple[str, str, str], tuple[datetime, datetime]],
) -> None:
    for link in state.links:
        predecessor = _span_for(state, proposed, link.predecessor_id, link.predecessor_bar)
        successor = _span_for(state, proposed, link.successor_id, link.successor_bar)
        try:
            original = required_successor_shift(
                link, _span_for(state, {}, link.predecessor_id, link.predecessor_bar),
                _span_for(state, {}, link.successor_id, link.successor_bar),
            )
            current = required_successor_shift(link, predecessor, successor)
        except (KeyError, ValueError) as error:
            differences.append(ValidationDifference("precedence_invalid", "", str(error)))
            continue
        if current > original:
            differences.append(ValidationDifference("precedence_aggravated", link.successor_id, link.link_key))


def _span_for(
    state: ProjectState, proposed: Mapping[tuple[str, str, str], tuple[datetime, datetime]],
    activity_id: str, bar_key: str,
) -> ActivitySpan:
    activity = state.activities[activity_id]
    matches = [bar for bar in activity.bars if bar.bar_key == bar_key]
    if len(matches) != 1:
        raise ValueError(f"precedence bar is ambiguous: {activity_id}:{bar_key}")
    bar = matches[0]
    start, finish = proposed.get((activity_id, bar.xml_key, bar.bar_key), (bar.start, bar.finish))
    return ActivitySpan(start, finish)


def _validate_invariants(
    differences: list[ValidationDifference], state: ProjectState,
    proposed: Mapping[tuple[str, str, str], tuple[datetime, datetime]],
    activities: Mapping[str, ApplyActivity],
) -> None:
    rebuilt: dict[str, NativeActivity] = {}
    for activity_id, activity in state.activities.items():
        bars = tuple(replace(bar, start=proposed[(activity_id, bar.xml_key, bar.bar_key)][0], finish=proposed[(activity_id, bar.xml_key, bar.bar_key)][1])
                     if (activity_id, bar.xml_key, bar.bar_key) in proposed else bar for bar in activity.bars)
        rebuilt[activity_id] = replace(activity, bars=bars)
    candidate = ProjectState(state.path, state.date_format, MappingProxyType(rebuilt), state.links, state.completion_cutoff)
    stable_codes = {
        "date_format_changed", "activity_removed", "activity_added",
        "activity_identity_ambiguous", "activity_identity_changed",
        "collateral_activity_changed", "completion_changed", "native_quantity_changed",
        "native_identity_missing", "completion_boundary_ambiguous",
        "completion_boundary_changed", "completed_prefix_changed",
        "precedence_link_changed", "precedence_link_added", "precedence_link_removed",
        "precedence_endpoint_unresolved",
    }
    for difference in compare_invariants(state, candidate, tuple(activities)).differences:
        if difference.code in stable_codes:
            differences.append(ValidationDifference("invariant_" + difference.code, difference.activity_id, difference.detail))


def _mapping(value: object, label: str, fields: frozenset[str]) -> Mapping[str, object]:
    if type(value) is not dict or set(value) != fields:
        raise ValueError(label + " has invalid fields")
    return value


def _text(value: object, label: str) -> str:
    if type(value) is not str or not value:
        raise ValueError(label + " must be a non-empty exact string")
    return value


def _datetime(value: object, label: str) -> datetime:
    text = _text(value, label)
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as error:
        raise ValueError(label + " must be an ISO datetime") from error
    if parsed.tzinfo is not None:
        raise ValueError(label + " must be naive")
    return parsed


def _date(value: object, label: str) -> date:
    text = _text(value, label)
    try:
        return date.fromisoformat(text)
    except ValueError as error:
        raise ValueError(label + " must be an ISO date") from error


def _decimal(value: object, label: str) -> Decimal:
    text = _text(value, label)
    try:
        parsed = Decimal(text)
    except InvalidOperation as error:
        raise ValueError(label + " must be decimal") from error
    if not parsed.is_finite():
        raise ValueError(label + " must be finite")
    return parsed


def _unique(values: object, label: str) -> None:
    sequence = tuple(values)  # type: ignore[arg-type]
    if len(sequence) != len(set(sequence)):
        raise ValueError(label + " must be unique")


def _integer_civil_days(delta: timedelta) -> bool:
    return delta.seconds == 0 and delta.microseconds == 0


def _decimal_text(value: Decimal) -> str:
    """Serialize Decimal exactly, without applying the ambient precision context.

    ``Decimal.normalize()`` rounds according to the active decimal context.  The
    local bridge must preserve the exact fragment quantity supplied by STP so
    its canonical package remains byte-for-byte equivalent to the VGS snapshot.
    """
    return format(value, "f")
