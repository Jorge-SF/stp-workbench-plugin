"""Fail-closed, loopback-only HTTP bridge for the STP V2 wire protocol."""

from __future__ import annotations

import argparse
import copy
import hashlib
import hmac
import json
import os
import re
import socket
import threading
import time
from dataclasses import dataclass
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

import stp_v2_windows_atomic as atomic
import stp_v2_bridge_bootstrap as bootstrap
from stp_v2_core import (
    capture_scenario_v2,
    load_scenario_v2,
    project_fingerprint,
    read_vgantt,
    scenario_v2_to_json_value,
    validate_scenario_v2,
)
from stp_v2_apply_contract import (
    ValidationReport,
    apply_package_to_json_value,
    load_apply_package,
    validate_apply_package,
)
from stp_v2_snapshot_contract import (
    SnapshotContractError,
    build_canonical_snapshot,
    canonicalize_snapshot,
    merge_grid_rows,
    project_key,
)
from stp_v2_excel_export import build_excel_export, validate_excel_export_payload


MAX_BODY_BYTES = 20 * 1024 * 1024
MAX_LENGTH_DIGITS = len(str(MAX_BODY_BYTES))
SCENARIO_SCHEMA = "vgs-stp-splits/2"
_DECIMAL = re.compile(r"^[0-9]+$")
_FINGERPRINT = re.compile(r"^[0-9a-f]{64}$")
_LOCKS: dict[Path, threading.Lock] = {}
_LOCKS_GUARD = threading.Lock()
_OUTPUT_DIRECTORY = "Respaldos_STPUG-VGS_v2"
_OUTPUT_FILENAME = "stp_scenario_pending_v2.json"
_APPLY_PACKAGE_FILENAME = "stp_apply_pending_v2.json"
_APPLY_RECEIPT_FILENAME = "stp_apply_pending_v2.ready.json"
_UNSAFE_APPLY_REASONS = {
    "native_anchor_invalid": "anclaje_nativo",
    "logical_unit_provenance_mixed": "unidad_logica",
    "logical_unit_separated": "unidad_logica",
    "logical_unit_order_changed": "unidad_logica",
    "activity_overlap": "solapamiento",
    "precedence_aggravated": "precedencia",
    "completed_fragment_moved": "fragmento_completado",
    "civil_shift_invalid": "movimiento_civil",
}
_UNSAFE_APPLY_ACTIVITY_LIMIT = 5
_PREFLIGHT_ROUTES = {
    "/health": ("GET", frozenset({"x-stp-v2-token"}), "X-STP-V2-Token"),
    "/snapshot": ("GET", frozenset({"x-stp-v2-token"}), "X-STP-V2-Token"),
    "/scenario": (
        "POST",
        frozenset({"content-type", "x-stp-v2-token"}),
        "Content-Type, X-STP-V2-Token",
    ),
    "/apply-package": (
        "POST",
        frozenset({"content-type", "x-stp-v2-token"}),
        "Content-Type, X-STP-V2-Token",
    ),
    "/export-xlsx": (
        "POST",
        frozenset({"content-type", "x-stp-v2-token"}),
        "Content-Type, X-STP-V2-Token",
    ),
}


def _preflight_requested_headers(values: Sequence[str]) -> frozenset[str] | None:
    """Return one strict, normalized CORS requested-header set, or reject it."""
    if len(values) != 1:
        return None
    names = tuple(name.strip(" \t").lower() for name in values[0].split(","))
    if not names or any(not name for name in names) or len(names) != len(set(names)):
        return None
    return frozenset(names)


class BridgeError(ValueError):
    def __init__(
        self,
        message: str,
        status: int = 422,
        *,
        recovery_path: Path | None = None,
        diagnostic: Mapping[str, object] | None = None,
    ) -> None:
        super().__init__(message)
        self.status = status
        self.recovery_path = recovery_path
        self.diagnostic = dict(diagnostic) if diagnostic is not None else None


@dataclass(frozen=True)
class _Config:
    project_path: Path
    project_key: str
    project_fingerprint: str
    token: str
    port: int
    snapshot_path: Path
    provider: Callable[[dict[str, Any]], Mapping[str, Any]] | None


def _runtime_root() -> Path:
    """Script 2's per-user runtime folder (stp_core.runtime.default_runtime_root), computed
    inline so the single-file bundle works too.  The bridge process inherits Script 2's
    environment, so ``STP_RUNTIME_ROOT`` matches."""
    configured = os.environ.get("STP_RUNTIME_ROOT")
    if configured:
        return Path(configured)
    local = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(local) / "Maptek" / "StpUG" / "runtime"


def _snapshot_roots(project: Path) -> tuple[Path, ...]:
    """Folders the snapshot may live in: the per-user runtime folder Script 2 writes it to
    (docs/known_issues.md, KI-2), or the project folder (bundled Script 2 layout)."""
    roots = [project.parent]
    try:
        roots.append(_runtime_root().resolve(strict=True))
    except (OSError, RuntimeError):
        pass
    return tuple(roots)


def _inside(path: Path, directory: Path) -> bool:
    try:
        path.relative_to(directory)
        return True
    except ValueError:
        return False


def _config(raw: Mapping[str, Any]) -> _Config:
    required = {"projectPath", "projectKey", "projectFingerprint", "token", "port", "snapshotPath"}
    allowed = required | {"bind", "snapshotProvider"}
    if not isinstance(raw, Mapping) or set(raw) - allowed or not required <= set(raw):
        raise BridgeError("invalid configuration", 500)
    if raw.get("bind", "127.0.0.1") != "127.0.0.1":
        raise BridgeError("invalid configuration", 500)
    if isinstance(raw["port"], bool) or not isinstance(raw["port"], int) or not 0 <= raw["port"] <= 65535:
        raise BridgeError("invalid configuration", 500)
    if not all(isinstance(raw[key], str) for key in ("projectPath", "projectKey", "projectFingerprint", "token", "snapshotPath")):
        raise BridgeError("invalid configuration", 500)
    if not raw["projectKey"] or len(raw["token"]) < 16 or not _FINGERPRINT.fullmatch(raw["projectFingerprint"]):
        raise BridgeError("invalid configuration", 500)
    try:
        project, snapshot = Path(raw["projectPath"]).resolve(strict=True), Path(raw["snapshotPath"]).resolve(strict=True)
        if not any(_inside(snapshot, root) for root in _snapshot_roots(project)):
            raise ValueError("snapshot outside the allowed folders")
    except (OSError, RuntimeError, ValueError):
        raise BridgeError("invalid configuration", 500) from None
    if not project.is_file() or project.is_symlink() or not snapshot.is_file() or snapshot.is_symlink():
        raise BridgeError("invalid configuration", 500)
    provider = raw.get("snapshotProvider")
    if provider is not None and not callable(provider):
        raise BridgeError("invalid configuration", 500)
    return _Config(project, raw["projectKey"], raw["projectFingerprint"], raw["token"], raw["port"], snapshot, provider)


def _state(config: _Config, *, require_fingerprint: bool = True):
    try:
        state = read_vgantt(config.project_path)
        current = project_fingerprint(state)
    except (OSError, ValueError, KeyError, UnicodeError):
        raise BridgeError("project unavailable", 409) from None
    if project_key(config.project_path) != config.project_key:
        raise BridgeError("project mismatch", 409)
    if require_fingerprint and current != config.project_fingerprint:
        raise BridgeError("project mismatch", 409)
    return state


def _read_snapshot(config: _Config, state) -> dict[str, Any]:
    try:
        if config.snapshot_path.stat().st_size > MAX_BODY_BYTES:
            raise OSError
        source = json.loads(config.snapshot_path.read_text(encoding="utf-8"))
        canonical = canonicalize_snapshot(
            source,
            state,
            config.project_path,
            protected_secrets={config.token},
        )
        if canonical["projectKey"] != config.project_key:
            raise SnapshotContractError("invalid snapshot contract data")
        return canonical
    except (OSError, UnicodeError, json.JSONDecodeError, SnapshotContractError, TypeError, ValueError, KeyError):
        raise BridgeError("snapshot unavailable", 409) from None


def _extract_provider_grid(snapshot: Mapping[str, Any]) -> dict[str, Mapping[str, str]]:
    return {activity["id"]: activity["grid"] for activity in snapshot["activities"]}


@dataclass(frozen=True)
class SnapshotCapture:
    canonical_json: bytes
    grid_json: bytes
    activity_ids: frozenset[str]
    exported_at: datetime

    def baseline_copy(self) -> dict[str, Any]:
        return json.loads(self.canonical_json.decode("utf-8"))

    def grid_rows_copy(self) -> dict[str, dict[str, str]]:
        return json.loads(self.grid_json.decode("utf-8"))


def capture_refresh_baseline(config: Mapping[str, Any]) -> SnapshotCapture:
    validated = _config(config)
    state = _state(validated, require_fingerprint=True)
    baseline = _read_snapshot(validated, state)
    rows = {
        activity["id"]: dict(activity["grid"])
        for activity in baseline["activities"]
    }
    return SnapshotCapture(
        canonical_json=json.dumps(
            baseline, ensure_ascii=False, separators=(",", ":"), sort_keys=True,
        ).encode("utf-8"),
        grid_json=json.dumps(
            rows, ensure_ascii=False, separators=(",", ":"), sort_keys=True,
        ).encode("utf-8"),
        activity_ids=frozenset(rows),
        exported_at=datetime.fromisoformat(baseline["exportedAt"]),
    )


def refresh_snapshot(
    config: Mapping[str, Any],
    capture: SnapshotCapture | None = None,
    refresher: Callable[[dict[str, Any]], Mapping[str, Any]] | None = None,
) -> dict[str, Any]:
    """Re-read native state and rebuild a fresh shared-contract response."""
    try:
        validated = _config(config)
        active_capture = capture if capture is not None else capture_refresh_baseline(config)
        if not isinstance(active_capture, SnapshotCapture):
            raise TypeError
        state = _state(validated, require_fingerprint=False)
        if frozenset(state.activities) != active_capture.activity_ids:
            raise BridgeError("project mismatch", 409)
        baseline_rows = active_capture.grid_rows_copy()
        rebuilt = build_canonical_snapshot(
            state,
            validated.project_path,
            baseline_rows,
            exported_at=active_capture.exported_at,
        )
        callback = refresher if refresher is not None else validated.provider
        if callback is not None:
            provider_result = callback(copy.deepcopy(rebuilt))
            if not isinstance(provider_result, Mapping):
                raise TypeError
            normalized_provider = dict(provider_result)
            provider_activities = normalized_provider.get("activities")
            if not isinstance(provider_activities, list):
                raise TypeError
            normalized_provider["activities"] = sorted(
                provider_activities,
                key=lambda activity: activity["id"],
            )
            candidate = canonicalize_snapshot(
                normalized_provider,
                state,
                validated.project_path,
                protected_secrets={validated.token},
            )
            refreshed_rows = _extract_provider_grid(candidate)
        else:
            refreshed_rows = _extract_provider_grid(rebuilt)
        merged = merge_grid_rows(
            baseline_rows,
            refreshed_rows,
            state.activities.keys(),
            protected_secrets={validated.token},
        )
        result = build_canonical_snapshot(
            state,
            validated.project_path,
            merged,
            exported_at=active_capture.exported_at,
        )
        result.update(snapshotComplete=True, applyAllowed=False)
        return result
    except Exception:
        return {"snapshotComplete": False, "applyAllowed": False}


def _scenario(config: _Config, payload: Any):
    if not isinstance(payload, dict):
        raise BridgeError("invalid scenario", 422)
    try:
        scenario = capture_scenario_v2(load_scenario_v2(payload))
    except (TypeError, ValueError, KeyError):
        raise BridgeError("invalid scenario", 422) from None
    try:
        state = _state(config)
        _read_snapshot(config, state)
    except BridgeError:
        raise BridgeError("project or snapshot stale", 409) from None
    report = validate_scenario_v2(scenario, state)
    if not report.ok:
        raise BridgeError("scenario stale or unsafe", 409)
    return scenario


def _apply_package(config: _Config, payload: Any):
    if not isinstance(payload, dict):
        raise BridgeError("invalid apply package", 422)
    try:
        package = load_apply_package(payload)
    except (TypeError, ValueError, KeyError):
        raise BridgeError("invalid apply package", 422) from None
    try:
        state = _state(config)
    except BridgeError:
        raise BridgeError("project stale", 409) from None
    report = validate_apply_package(package, state)
    if not report.ok:
        stale_codes = {"project_fingerprint_mismatch", "precedence_fingerprint_mismatch"}
        if any(difference.code in stale_codes for difference in report.differences):
            raise BridgeError("project stale", 409)
        raise BridgeError("apply package unsafe", 422, diagnostic=_unsafe_apply_diagnostic(report))
    return package


def _unsafe_apply_diagnostic(report: ValidationReport) -> dict[str, object]:
    """Project validator differences into the small diagnostic safe for STP."""
    differences = report.differences
    reason = _UNSAFE_APPLY_REASONS.get(differences[0].code, "integridad") if differences else "integridad"
    activities: list[str] = []
    for difference in differences:
        activity_id = difference.activity_id
        if activity_id and activity_id not in activities:
            activities.append(activity_id)
        if len(activities) == _UNSAFE_APPLY_ACTIVITY_LIMIT:
            break
    return {"reason": reason, "activities": activities}


def _locked_target(config: _Config, filename: str = _OUTPUT_FILENAME) -> tuple[Path, threading.Lock]:
    target = config.project_path.parent / _OUTPUT_DIRECTORY / filename
    with _LOCKS_GUARD:
        return target, _LOCKS.setdefault(target, threading.Lock())


def save_scenario(config: Mapping[str, Any], payload: Any) -> Path:
    validated = _config(config)
    scenario = _scenario(validated, payload)
    target, lock = _locked_target(validated)
    encoded = json.dumps(
        scenario_v2_to_json_value(scenario),
        ensure_ascii=False,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")
    with lock:
        try:
            target.parent.mkdir(exist_ok=True)
            result = atomic.atomic_replace_bytes_windows(
                target,
                encoded,
                expected_parent=target.parent,
            )
        except atomic.AtomicCommitIndeterminate as error:
            raise BridgeError(
                "scenario save indeterminate",
                500,
                recovery_path=error.recovery_path,
            ) from None
        except (atomic.AtomicCommitError, OSError):
            raise BridgeError("scenario save failed", 500) from None
    return result.path


@dataclass(frozen=True)
class ApplyPackageSave:
    path: Path
    receipt_path: Path
    package_sha256: str
    project_fingerprint: str


def _save_apply_package(config: Mapping[str, Any], payload: Any) -> ApplyPackageSave:
    """Commit package then its digest-bound readiness receipt under one lock."""
    validated = _config(config)
    package = _apply_package(validated, payload)
    target, lock = _locked_target(validated, _APPLY_PACKAGE_FILENAME)
    receipt_target = target.parent / _APPLY_RECEIPT_FILENAME
    encoded = json.dumps(
        apply_package_to_json_value(package), ensure_ascii=False,
        separators=(",", ":"), sort_keys=True, allow_nan=False,
    ).encode("utf-8")
    digest = hashlib.sha256(encoded).hexdigest()
    receipt = {
        "schema": "vgs-stp-apply-ready/1",
        "package_file": target.name,
        "package_sha256": digest,
        "project_fingerprint": package.project_fingerprint,
    }
    receipt_encoded = json.dumps(
        receipt, ensure_ascii=False, separators=(",", ":"),
        sort_keys=True, allow_nan=False,
    ).encode("utf-8")
    with lock:
        try:
            target.parent.mkdir(exist_ok=True)
            result = atomic.atomic_replace_bytes_windows(
                target, encoded, expected_parent=target.parent,
            )
            receipt_result = atomic.atomic_replace_bytes_windows(
                receipt_target, receipt_encoded, expected_parent=target.parent,
            )
        except atomic.AtomicCommitIndeterminate as error:
            raise BridgeError(
                "apply package save indeterminate", 500,
                recovery_path=error.recovery_path,
            ) from None
        except (atomic.AtomicCommitError, OSError):
            raise BridgeError("apply package save failed", 500) from None
    return ApplyPackageSave(
        result.path, receipt_result.path, digest, package.project_fingerprint,
    )


def save_apply_package(config: Mapping[str, Any], payload: Any) -> Path:
    """Persist a validated package and return its stable package path."""
    return _save_apply_package(config, payload).path


class _Server(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 64

    def __init__(self, *args, **kwargs) -> None:
        self._slots = threading.BoundedSemaphore(16)
        self._reject_slots = threading.BoundedSemaphore(4)
        self._reject_threads: set[threading.Thread] = set()
        self._reject_guard = threading.Lock()
        self._closing = threading.Event()
        self.stop_event = threading.Event()
        self.last_authenticated_at = time.monotonic()
        super().__init__(*args, **kwargs)

    def get_request(self):
        request, address = super().get_request()
        request.settimeout(5)
        return request, address

    def process_request(self, request, client_address) -> None:
        if self._slots.acquire(blocking=False):
            try:
                super().process_request(request, client_address)
            except Exception:
                self._slots.release()
                self.shutdown_request(request)
                raise
            return
        self._reject_slots.acquire()
        thread = threading.Thread(
            target=self._process_busy_request,
            args=(request, client_address),
            daemon=True,
            name="stp-v2-busy-response",
        )
        with self._reject_guard:
            self._reject_threads.add(thread)
        try:
            thread.start()
        except Exception:
            with self._reject_guard:
                self._reject_threads.discard(thread)
            self._reject_slots.release()
            self.shutdown_request(request)
            raise

    def _process_busy_request(self, request, client_address) -> None:
        try:
            _BusyHandler(request, client_address, self)
        finally:
            try:
                self.shutdown_request(request)
            finally:
                self._reject_slots.release()
                with self._reject_guard:
                    self._reject_threads.discard(threading.current_thread())

    def process_request_thread(self, request, client_address) -> None:
        try: super().process_request_thread(request, client_address)
        finally: self._slots.release()

    def shutdown(self) -> None:
        self._closing.set()
        self.stop_event.set()
        super().shutdown()

    def server_close(self) -> None:
        self._closing.set()
        self.stop_event.set()
        super().server_close()
        with self._reject_guard:
            threads = tuple(self._reject_threads)
        for thread in threads:
            thread.join(timeout=6)


class _BusyHandler(BaseHTTPRequestHandler):
    """Consume one saturated request and close only after a complete JSON 503."""

    server_version = ""
    sys_version = ""
    protocol_version = "HTTP/1.1"

    def log_message(self, format: str, *args: Any) -> None:
        return

    def __getattr__(self, name: str):
        if name.startswith("do_"):
            return self._busy
        raise AttributeError(name)

    def _busy(self) -> None:
        authenticated = _headers_authenticated(self.headers, self.server.bridge_config)  # type: ignore[attr-defined]
        if authenticated:
            self.server.last_authenticated_at = time.monotonic()  # type: ignore[attr-defined]
        origin_allowed = authenticated and _headers_origin_allowed(self.headers)
        lengths = self.headers.get_all("Content-Length", [])
        if len(lengths) == 1 and _DECIMAL.fullmatch(lengths[0]) and len(lengths[0]) <= MAX_LENGTH_DIGITS:
            remaining = min(int(lengths[0]), MAX_BODY_BYTES)
            try:
                while remaining:
                    chunk = self.rfile.read(min(remaining, 65_536))
                    if not chunk:
                        break
                    remaining -= len(chunk)
            except (OSError, socket.timeout):
                pass
        status = 503 if authenticated and origin_allowed else 403
        body = b'{"error":"server busy"}' if status == 503 else b'{"error":"forbidden"}'
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        if status == 503 and self.headers.get_all("Origin", []) == ["null"]:
            self.send_header("Access-Control-Allow-Origin", "null")
            self.send_header("Vary", "Origin")
        self.end_headers()
        try:
            self.wfile.write(body)
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, socket.timeout):
            pass
        self.close_connection = True


def _headers_authenticated(headers, bridge_config: Mapping[str, Any]) -> bool:
    values = headers.get_all("X-STP-V2-Token", [])
    if len(values) != 1:
        return False
    try:
        expected = _config(bridge_config).token.encode("utf-8")
        supplied = values[0].encode("utf-8")
    except (BridgeError, UnicodeError):
        return False
    return hmac.compare_digest(hashlib.sha256(supplied).digest(), hashlib.sha256(expected).digest())


def _headers_origin_allowed(headers) -> bool:
    values = headers.get_all("Origin", [])
    return len(values) <= 1 and (not values or values[0] == "null")


class _Handler(BaseHTTPRequestHandler):
    server_version = ""; sys_version = ""

    def __getattr__(self, name: str):
        if name.startswith("do_"): return self._method_not_allowed
        raise AttributeError(name)

    @property
    def bridge_config(self) -> Mapping[str, Any]: return self.server.bridge_config  # type: ignore[attr-defined]
    def log_message(self, format: str, *args: Any) -> None: return

    def handle_one_request(self) -> None:
        try: super().handle_one_request()
        except (OSError, ValueError, TypeError, RuntimeError):
            try:
                if self._auth(): self._reply(500, {"error": "request failed"})
            except (OSError, ValueError, TypeError, RuntimeError): pass

    def _values(self, name: str) -> list[str]: return self.headers.get_all(name, [])
    def _auth(self) -> bool:
        authenticated = _headers_authenticated(self.headers, self.bridge_config)
        if authenticated:
            self.server.last_authenticated_at = time.monotonic()  # type: ignore[attr-defined]
        return authenticated
    def _origin(self) -> bool:
        return _headers_origin_allowed(self.headers)
    def _reply(self, status: int, payload: Mapping[str, Any], cors: bool = False) -> None:
        body = json.dumps(payload, separators=(",", ":"), allow_nan=False).encode("utf-8")
        self.send_response(status); self.send_header("Content-Type", "application/json; charset=utf-8"); self.send_header("Content-Length", str(len(body))); self.send_header("Cache-Control", "no-store"); self.send_header("X-Content-Type-Options", "nosniff")
        if cors and self._values("Origin") == ["null"]: self.send_header("Access-Control-Allow-Origin", "null"); self.send_header("Vary", "Origin")
        self.end_headers()
        try: self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, socket.timeout): pass
    def _reply_binary(self, status: int, body: bytes, filename: str) -> None:
        self.send_response(status); self.send_header("Content-Type", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"); self.send_header("Content-Disposition", f'attachment; filename="{filename}"'); self.send_header("Content-Length", str(len(body))); self.send_header("Cache-Control", "no-store"); self.send_header("X-Content-Type-Options", "nosniff")
        if self._values("Origin") == ["null"]: self.send_header("Access-Control-Allow-Origin", "null"); self.send_header("Vary", "Origin")
        self.end_headers()
        try: self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, socket.timeout): pass
    def _begin(self, allowed: set[str]) -> bool:
        if not self._auth(): self._reply(403, {"error": "forbidden"}); return False
        if not self._origin(): self._reply(403, {"error": "forbidden"}); return False
        all_routes = {"/health", "/snapshot", "/scenario", "/apply-package", "/export-xlsx"}
        if self.path not in all_routes: self._reply(404, {"error": "not found"}, True); return False
        if self.path not in allowed: self._reply(405, {"error": "method not allowed"}, True); return False
        return True
    def _empty(self, cors: bool = True) -> bool:
        lengths = self._values("Content-Length")
        if self._values("Transfer-Encoding") or len(lengths) > 1 or (lengths and lengths[0] != "0"):
            self._reply(400, {"error": "invalid request"}, cors); return False
        return True
    def _json(self) -> dict[str, Any] | None:
        lengths, types = self._values("Content-Length"), self._values("Content-Type")
        if self._values("Transfer-Encoding") or len(lengths) != 1 or len(types) != 1 or len(lengths[0]) > MAX_LENGTH_DIGITS or not _DECIMAL.fullmatch(lengths[0]):
            self._reply(400, {"error": "invalid request"}, True); return None
        if types[0] != "application/json":
            self._reply(415, {"error": "unsupported media type"}, True); return None
        size = int(lengths[0])
        if size > MAX_BODY_BYTES: self._reply(413, {"error": "payload too large"}, True); return None
        try:
            raw = self.rfile.read(size)
            if len(raw) != size: raise ValueError
            value = json.loads(raw.decode("utf-8"))
        except (UnicodeError, ValueError, json.JSONDecodeError, OSError, socket.timeout): self._reply(422, {"error": "invalid scenario"}, True); return None
        if not isinstance(value, dict): self._reply(422, {"error": "invalid scenario"}, True); return None
        return value
    def _error(self, error: BridgeError) -> None:
        payload: dict[str, Any] = {"error": str(error)}
        if error.diagnostic is not None:
            payload["diagnostic"] = error.diagnostic
        if error.recovery_path is not None:
            payload["recoveryPath"] = str(error.recovery_path)
        self._reply(error.status, payload, True)
    def do_GET(self) -> None:
        if not self._begin({"/health", "/snapshot"}) or not self._empty(): return
        if self.path == "/health":
            try:
                _state(_config(self.bridge_config), require_fingerprint=False)
                capture = getattr(self.server, "snapshot_capture", None)
                if not isinstance(capture, SnapshotCapture): raise BridgeError("snapshot unavailable", 409)
                self._reply(200, {"status": "ok"}, True)
            except BridgeError as error: self._error(error)
        else:
            result = refresh_snapshot(
                self.bridge_config,
                capture=self.server.snapshot_capture,  # type: ignore[attr-defined]
            )
            self._reply(200, result, True)
    def do_POST(self) -> None:
        if not self._begin({"/scenario", "/apply-package", "/export-xlsx"}): return
        payload = self._json()
        if payload is None: return
        try:
            if self.path == "/export-xlsx":
                if payload.get("projectKey") != self.bridge_config["projectKey"]: raise BridgeError("invalid Excel export", 422)
                validate_excel_export_payload(payload)
                self._reply_binary(200, build_excel_export(payload), "STP.xlsx")
                return
            if self.path == "/scenario":
                save_scenario(self.bridge_config, payload)
                response = {"status": "saved"}
            else:
                saved = _save_apply_package(self.bridge_config, payload)
                response = {
                    "status": "saved",
                    "schema": "vgs-stp-apply/1",
                    "package_sha256": saved.package_sha256,
                    "project_fingerprint": saved.project_fingerprint,
                }
            self._reply(201, response, True)
        except BridgeError as error: self._error(error)
    def do_OPTIONS(self) -> None:
        preflight = _PREFLIGHT_ROUTES.get(self.path)
        if self._values("Origin") != ["null"] or preflight is None:
            self._reply(403, {"error": "forbidden"}); return
        if not self._empty(False): return
        method, required_headers, response_headers = preflight
        if (
            self._values("Access-Control-Request-Method") != [method]
            or _preflight_requested_headers(
                self._values("Access-Control-Request-Headers")
            ) != required_headers
        ):
            self._reply(403, {"error": "forbidden"}); return
        self.send_response(204); self.send_header("Content-Length", "0"); self.send_header("Cache-Control", "no-store")
        self.send_header("Access-Control-Allow-Origin", "null"); self.send_header("Access-Control-Allow-Methods", method); self.send_header("Access-Control-Allow-Headers", response_headers); self.send_header("Vary", "Origin, Access-Control-Request-Method, Access-Control-Request-Headers")
        self.end_headers()
    def _method_not_allowed(self) -> None:
        if not self._auth(): self._reply(403, {"error": "forbidden"})
        elif not self._origin(): self._reply(403, {"error": "forbidden"})
        elif self.path in {"/health", "/snapshot", "/scenario", "/apply-package", "/export-xlsx"}: self._reply(405, {"error": "method not allowed"}, True)
        else: self._reply(404, {"error": "not found"}, True)


def create_server(config: Mapping[str, Any]) -> ThreadingHTTPServer:
    validated = _config(config)
    capture = capture_refresh_baseline(config)
    server = _Server(("127.0.0.1", validated.port), _Handler)
    server.bridge_config = dict(config)  # type: ignore[attr-defined]
    server.snapshot_capture = capture  # type: ignore[attr-defined]
    return server


def serve(config: Mapping[str, Any]) -> None:
    server = create_server(config)
    try: server.serve_forever()
    finally: server.server_close()


def run_bridge_from_file(config_path: Path, ready_path: Path) -> int:
    runtime = bootstrap._load_runtime_file(config_path, ready_path)
    bridge_id = runtime["bridgeId"]
    lease_path = Path(runtime["leasePath"])
    if bootstrap._read_lease_id(lease_path) != bridge_id:
        raise BridgeError("invalid runtime", 500)
    server = create_server(runtime["bridgeConfig"])
    monitor: threading.Thread | None = None
    try:
        if bootstrap._read_lease_id(lease_path) != bridge_id:
            raise BridgeError("invalid runtime", 500)
        ready = {
            "schema": bootstrap.READY_SCHEMA,
            "bridgeId": bridge_id,
            "projectKey": runtime["bridgeConfig"]["projectKey"],
            "projectFingerprint": runtime["bridgeConfig"]["projectFingerprint"],
            "baselineTimestamp": server.snapshot_capture.baseline_copy()["exportedAt"],  # type: ignore[attr-defined]
            "port": server.server_port,
            "pid": os.getpid(),
        }
        bootstrap._atomic_write_json(Path(ready_path), ready)
        monitor = threading.Thread(
            target=bootstrap.lease_monitor,
            args=(
                server,
                lease_path,
                bridge_id,
                runtime.get("idleTimeoutSeconds"),
            ),
            daemon=True,
            name="stp-v2-lease-monitor",
        )
        monitor.start()
        server.serve_forever()
        return 0
    finally:
        server.stop_event.set()
        server.server_close()
        if monitor is not None:
            monitor.join(timeout=2)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--ready", required=True, type=Path)
    arguments = parser.parse_args(argv)
    try:
        return run_bridge_from_file(arguments.config, arguments.ready)
    except Exception:
        # stdout/stderr go to DEVNULL; leave the cause beside the ready file for diagnosis.
        try:
            import traceback

            ready = Path(arguments.ready)
            error_file = ready.with_name(ready.name.replace("ready-", "error-", 1)).with_suffix(".txt")
            error_file.write_text(traceback.format_exc(), encoding="utf-8")
        except Exception:
            pass
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
