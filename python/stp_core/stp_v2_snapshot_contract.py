"""Shared, fail-closed snapshot and Activity Grid contract for STP V2."""

from __future__ import annotations

import csv
from datetime import datetime
from decimal import Decimal
from hashlib import sha256
from pathlib import Path
import re
from typing import Collection, Mapping
import unicodedata

from stp_v2_core import (
    ProjectState,
    precedence_fingerprint,
    project_fingerprint,
    segment_activity_for_display,
)


SNAPSHOT_SCHEMA = "vgs-stp-snapshot/2"

_ID_COLUMNS = frozenset({"ID", "Id", "Activity ID", "ActivityId"})
_ROOT_FIELDS = frozenset({
    "schema", "projectName", "projectPath", "projectKey", "projectFingerprint",
    "precedenceFingerprint", "exportedAt", "dateFormat", "activities", "precedences",
})
_ACTIVITY_FIELDS = frozenset({
    "id", "collectionKey", "overallPercentComplete", "grid", "nativeBars",
    "fragments", "nativeAnchors",
})
_SENSITIVE_COMPONENTS = frozenset({
    "token", "password", "passwd", "secret", "credential", "authorization",
    "authentication", "cookie", "auth", "bind", "port", "schema",
})
_SENSITIVE_COMPOUNDS = frozenset({
    "apikey", "accesskey", "clientsecret", "projectfingerprint",
    "precedencefingerprint", "projectkey", "projectpath", "snapshotpath",
    "snapshotcomplete", "applyallowed", "nativebars", "nativeanchors", "fragments",
    "collectionkey", "nativebarxmlkey", "nativebarkey", "lockedcompleted",
    "overallpercentcomplete", "completedminutes", "completeworkedminutes",
})
_IMMUTABLE_GRID_FORMS = frozenset({
    "uaid", "complete", "percentcomplete", "overallpercentcomplete", "completedminutes",
    "completeworkedminutes", "nativebarxmlkey", "nativebarkey", "collectionkey",
})


class SnapshotContractError(ValueError):
    """A deliberately non-diagnostic rejection of untrusted snapshot input."""


def _reject() -> None:
    raise SnapshotContractError("invalid snapshot contract data")


def _key_forms(key: str) -> tuple[frozenset[str], str]:
    normalized = unicodedata.normalize("NFKC", key).casefold()
    components = frozenset(re.findall(r"[a-z0-9]+", normalized))
    return components, "".join(re.findall(r"[a-z0-9]+", normalized))


def _is_sensitive_key(key: str) -> bool:
    components, combined = _key_forms(key)
    if components & _SENSITIVE_COMPONENTS:
        return True
    if any(member in combined for member in _SENSITIVE_COMPONENTS | _SENSITIVE_COMPOUNDS):
        return True
    return {"project", "fingerprint"} <= components or {"precedence", "fingerprint"} <= components


def _protected_forms(protected_secrets: Collection[str]) -> tuple[tuple[str, str], ...]:
    result: list[tuple[str, str]] = []
    for secret in protected_secrets:
        if not isinstance(secret, str) or not secret:
            _reject()
        normalized = unicodedata.normalize("NFKC", secret).casefold()
        result.append((secret, normalized))
    return tuple(result)


def _check_value(value: str, protected_forms: Collection[tuple[str, str]]) -> None:
    normalized = unicodedata.normalize("NFKC", value).casefold()
    for raw, secret_normalized in protected_forms:
        digest = sha256(raw.encode("utf-8")).hexdigest()
        if raw in value or secret_normalized in normalized or digest in normalized:
            _reject()


def _check_strings(value: object, protected_forms: Collection[tuple[str, str]]) -> None:
    if isinstance(value, str):
        _check_value(value, protected_forms)
    elif isinstance(value, Mapping):
        for key, item in value.items():
            if isinstance(key, str):
                _check_value(key, protected_forms)
            _check_strings(item, protected_forms)
    elif isinstance(value, (list, tuple)):
        for item in value:
            _check_strings(item, protected_forms)


def parse_activity_grid_csv(path: Path) -> dict[str, dict[str, str]]:
    """Parse the VGS-exported grid without accepting ambiguous CSV identities."""
    try:
        with Path(path).open("r", encoding="utf-8-sig", newline="") as source:
            reader = csv.DictReader(source)
            fieldnames = reader.fieldnames
            if not fieldnames:
                _reject()
            raw_rows = list(reader)
    except (OSError, UnicodeError, csv.Error):
        _reject()
    if (
        not all(isinstance(name, str) and name.strip() for name in fieldnames)
        or len(set(fieldnames)) != len(fieldnames)
    ):
        _reject()
    identifiers = [name for name in fieldnames if name in _ID_COLUMNS]
    if len(identifiers) != 1:
        _reject()
    identifier = identifiers[0]
    result: dict[str, dict[str, str]] = {}
    for row in raw_rows:
        if (
            not isinstance(row, Mapping)
            or set(row) != set(fieldnames)
            or any(not isinstance(key, str) or not isinstance(value, str) for key, value in row.items())
        ):
            _reject()
        value = row.get(identifier)
        if not isinstance(value, str) or not value or value in result:
            _reject()
        result[value] = {key: item for key, item in row.items()}
    return result


def validate_grid_rows(
    rows: Mapping[str, Mapping[str, str]],
    known_activity_ids: Collection[str],
    *,
    protected_secrets: Collection[str] = (),
) -> Mapping[str, Mapping[str, str]]:
    """Validate and copy presentation-only Activity Grid data."""
    protected_forms = _protected_forms(protected_secrets)
    if not isinstance(rows, Mapping) or any(not isinstance(item, str) or not item for item in known_activity_ids):
        _reject()
    known = set(known_activity_ids)
    result: dict[str, dict[str, str]] = {}
    seen_ids: set[str] = set()
    for row_key, row in rows.items():
        if not isinstance(row_key, str) or not row_key or row_key not in known or not isinstance(row, Mapping):
            _reject()
        if any(not isinstance(key, str) or not key.strip() or not isinstance(value, str) for key, value in row.items()):
            _reject()
        identity_keys = [key for key in row if key in _ID_COLUMNS]
        if len(identity_keys) != 1:
            _reject()
        activity_id = row[identity_keys[0]]
        if activity_id != row_key or activity_id not in known or activity_id in seen_ids:
            _reject()
        seen_ids.add(activity_id)
        copied: dict[str, str] = {}
        for key, value in row.items():
            if _is_sensitive_key(key):
                _reject()
            _check_value(value, protected_forms)
            copied[key] = value
        result[activity_id] = copied
    return result


def _immutable_grid_key(key: str) -> bool:
    return key in _ID_COLUMNS or _key_forms(key)[1] in _IMMUTABLE_GRID_FORMS


def merge_grid_rows(
    baseline: Mapping[str, Mapping[str, str]],
    refreshed: Mapping[str, Mapping[str, str]],
    known_activity_ids: Collection[str],
    *,
    protected_secrets: Collection[str],
) -> Mapping[str, Mapping[str, str]]:
    """Apply only validated presentation refreshes to matching native evidence."""
    baseline_copy = validate_grid_rows(baseline, known_activity_ids, protected_secrets=protected_secrets)
    refreshed_copy = validate_grid_rows(refreshed, known_activity_ids, protected_secrets=protected_secrets)
    known = set(known_activity_ids)
    if set(baseline_copy) != known or set(refreshed_copy) != known:
        _reject()
    result: dict[str, dict[str, str]] = {}
    for activity_id in sorted(known):
        prior, incoming = baseline_copy[activity_id], refreshed_copy[activity_id]
        if set(prior) != set(incoming):
            _reject()
        for key in prior:
            if _immutable_grid_key(key) and incoming[key] != prior[key]:
                _reject()
        result[activity_id] = {key: incoming[key] for key in prior}
    return result


def _iso(value: datetime) -> str:
    if value.microsecond:
        return value.isoformat(timespec="microseconds").rstrip("0").rstrip(".")
    return value.isoformat(timespec="seconds")


def project_key(path: Path) -> str:
    return sha256(str(path.resolve()).casefold().encode("utf-8")).hexdigest()


def _lag_microseconds(value) -> str:
    return str(value.days * 86_400_000_000 + value.seconds * 1_000_000 + value.microseconds)


def _bar_payload(bar) -> dict[str, object]:
    return {"xmlKey": bar.xml_key, "key": bar.bar_key, "start": _iso(bar.start), "finish": _iso(bar.finish), "quantityPercentage": str(bar.quantity_percentage), "percentComplete": str(bar.percent_complete), "completeWorkedMinutes": str(bar.complete_worked_minutes)}


def _fragment_payload(fragment) -> dict[str, object]:
    return {"id": fragment.id, "nativeBarXmlKey": fragment.native_bar_xml_key, "nativeBarKey": fragment.native_bar_key, "start": _iso(fragment.start), "finish": _iso(fragment.finish), "originalStart": _iso(fragment.original_start), "originalFinish": _iso(fragment.original_finish), "logicalUnitDate": fragment.logical_unit_day.isoformat(), "displaySuppressed": fragment.display_suppressed, "quantityPercentage": str(fragment.quantity_percentage), "lockedCompleted": fragment.locked_completed}


def _link_payload(link) -> dict[str, object]:
    return {"key": link.link_key, "predecessorId": link.predecessor_id, "successorId": link.successor_id, "predecessorBar": link.predecessor_bar, "successorBar": link.successor_bar, "type": link.precedence_type, "lagMicroseconds": _lag_microseconds(link.lag), "groupKey": link.group_key}


def _anchor_payloads(activity, links) -> list[dict[str, object]]:
    anchors: list[dict[str, object]] = []
    cumulative = Decimal("0")
    for left, right in zip(activity.bars, activity.bars[1:]):
        cumulative += left.quantity_percentage
        references: list[dict[str, object]] = []
        for link in links:
            if link.predecessor_id == activity.activity_id and link.predecessor_bar in {left.bar_key, right.bar_key}:
                references.append({"key": link.link_key, "role": "predecessor", "endpointBar": link.predecessor_bar, "otherActivityId": link.successor_id, "otherBar": link.successor_bar, "type": link.precedence_type, "lagMicroseconds": _lag_microseconds(link.lag), "groupKey": link.group_key})
            if link.successor_id == activity.activity_id and link.successor_bar in {left.bar_key, right.bar_key}:
                references.append({"key": link.link_key, "role": "successor", "endpointBar": link.successor_bar, "otherActivityId": link.predecessor_id, "otherBar": link.predecessor_bar, "type": link.precedence_type, "lagMicroseconds": _lag_microseconds(link.lag), "groupKey": link.group_key})
        anchors.append({"leftBarXmlKey": left.xml_key, "leftBarKey": left.bar_key, "rightBarXmlKey": right.xml_key, "rightBarKey": right.bar_key, "at": _iso(left.finish), "rightAt": _iso(right.start), "contiguous": left.finish == right.start, "cumulativeQuantityPercentage": str(cumulative), "links": sorted(references, key=lambda item: (item["key"], item["role"], item["endpointBar"]))})
    return anchors


def _validated_exported_at(value: datetime) -> datetime:
    if not isinstance(value, datetime) or value.tzinfo is not None:
        _reject()
    return value


def build_canonical_snapshot(
    state: ProjectState,
    project_path: Path,
    grid_rows: Mapping[str, Mapping[str, str]],
    *,
    exported_at: datetime,
) -> dict[str, object]:
    """Construct fresh, deterministic native evidence and copied grid rows."""
    exported = _validated_exported_at(exported_at)
    target = Path(project_path).resolve()
    known = set(state.activities)
    grid = validate_grid_rows(grid_rows, known)
    if set(grid) != known:
        _reject()
    try:
        activities = [{"id": activity_id, "collectionKey": activity.collection_key, "overallPercentComplete": str(activity.overall_percent_complete), "grid": dict(grid[activity_id]), "nativeBars": [_bar_payload(bar) for bar in activity.bars], "fragments": [_fragment_payload(fragment) for fragment in segment_activity_for_display(activity, state.completion_cutoff)], "nativeAnchors": _anchor_payloads(activity, state.links)} for activity_id, activity in sorted(state.activities.items())]
        precedences = [_link_payload(link) for link in state.links]
        return {"schema": SNAPSHOT_SCHEMA, "projectName": target.name, "projectPath": str(target), "projectKey": project_key(target), "projectFingerprint": project_fingerprint(state), "precedenceFingerprint": precedence_fingerprint(state), "exportedAt": _iso(exported), "dateFormat": state.date_format, "activities": activities, "precedences": precedences}
    except (ValueError, TypeError, KeyError, OverflowError):
        _reject()


def _source_grids(source: Mapping[str, object], known_ids: Collection[str], protected_forms: Collection[tuple[str, str]]) -> dict[str, dict[str, str]]:
    activities = source.get("activities")
    if not isinstance(activities, list) or len(activities) != len(known_ids):
        _reject()
    grids: dict[str, Mapping[str, str]] = {}
    for activity in activities:
        if not isinstance(activity, Mapping) or set(activity) != _ACTIVITY_FIELDS:
            _reject()
        activity_id = activity.get("id")
        if not isinstance(activity_id, str) or activity_id in grids:
            _reject()
        grid = activity.get("grid")
        if not isinstance(grid, Mapping):
            _reject()
        _check_strings(activity, protected_forms)
        grids[activity_id] = grid
    return dict(validate_grid_rows(grids, known_ids, protected_secrets=()))


def _canonical_equal(source: object, rebuilt: object) -> bool:
    """Compare canonical JSON data without Python's bool/int coercion."""
    if isinstance(rebuilt, Mapping):
        if not isinstance(source, Mapping) or set(source) != set(rebuilt):
            return False
        return all(_canonical_equal(source[key], value) for key, value in rebuilt.items())
    if isinstance(rebuilt, list):
        return (
            isinstance(source, list)
            and len(source) == len(rebuilt)
            and all(_canonical_equal(left, right) for left, right in zip(source, rebuilt))
        )
    return type(source) is type(rebuilt) and source == rebuilt


def canonicalize_snapshot(
    source: Mapping[str, object],
    state: ProjectState,
    project_path: Path,
    *,
    protected_secrets: Collection[str],
) -> dict[str, object]:
    """Accept only snapshots whose native evidence exactly matches current state."""
    protected_forms = _protected_forms(protected_secrets)
    if not isinstance(source, Mapping) or set(source) != _ROOT_FIELDS or source.get("schema") != SNAPSHOT_SCHEMA:
        _reject()
    _check_strings(source, protected_forms)
    raw_exported = source.get("exportedAt")
    if not isinstance(raw_exported, str) or "T" not in raw_exported:
        _reject()
    try:
        exported = datetime.fromisoformat(raw_exported)
    except ValueError:
        _reject()
    if exported.tzinfo is not None or _iso(exported) != raw_exported:
        _reject()
    grids = _source_grids(source, set(state.activities), protected_forms)
    rebuilt = build_canonical_snapshot(state, project_path, grids, exported_at=exported)
    if not _canonical_equal(source, rebuilt):
        _reject()
    return rebuilt
