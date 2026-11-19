"""Private runtime bootstrap for the short-lived STP V2 localhost bridge."""

from __future__ import annotations

import http.client
import json
import os
import re
import secrets
import stat
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping
from urllib.parse import urlsplit


RUNTIME_SCHEMA = "vgs-stp-bridge-runtime/1"
READY_SCHEMA = "vgs-stp-bridge-ready/1"
LEASE_SCHEMA = "vgs-stp-bridge-lease/1"
DEFAULT_IDLE_TIMEOUT: int | None = None
MAX_LEGACY_IDLE_TIMEOUT = 4_560
_HEX_32 = re.compile(r"^[0-9a-f]{32}$")
_HEX_64 = re.compile(r"^[0-9a-f]{64}$")
_STATIC_MESSAGE = "No se pudo iniciar la actualización en vivo; se usará el modo estático."
_LIVE_MESSAGE = "Actualización en vivo disponible."
_PROJECT_LOCKS: dict[Path, threading.Lock] = {}
_PROJECT_LOCKS_GUARD = threading.Lock()


@dataclass(frozen=True)
class BridgeDescriptor:
    mode: str
    endpoint: str
    token: str = field(repr=False)
    project_key: str
    bridge_id: str
    message: str

    def page_config(self) -> dict[str, str]:
        return {
            "mode": self.mode,
            "endpoint": self.endpoint,
            "token": self.token,
            "projectKey": self.project_key,
            "bridgeId": self.bridge_id,
            "message": self.message,
        }


def _static(project_key: str = "", bridge_id: str = "") -> BridgeDescriptor:
    return BridgeDescriptor("static", "", "", project_key, bridge_id, _STATIC_MESSAGE)


def _atomic_write_json(path: Path, value: Any) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
        allow_nan=False,
    ).encode("utf-8")
    temporary: Path | None = None
    try:
        descriptor, name = tempfile.mkstemp(
            prefix=f".{destination.name}.", suffix=".tmp", dir=destination.parent
        )
        temporary = Path(name)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
        temporary = None
    finally:
        if temporary is not None:
            try:
                temporary.unlink()
            except OSError:
                pass


def _read_lease_id(path: Path) -> str:
    try:
        value = json.loads(Path(path).read_text(encoding="utf-8"))
        if (
            isinstance(value, dict)
            and set(value) == {"schema", "bridgeId"}
            and value["schema"] == LEASE_SCHEMA
            and isinstance(value["bridgeId"], str)
            and _HEX_32.fullmatch(value["bridgeId"])
        ):
            return value["bridgeId"]
    except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError):
        pass
    return ""


def lease_monitor(
    server, lease_path, bridge_id, idle_timeout: int | None = None, now=time.monotonic
) -> None:
    while not server.stop_event.wait(1.0):
        if _read_lease_id(lease_path) != bridge_id:
            server.shutdown()
            return
        if idle_timeout is not None and now() - server.last_authenticated_at >= idle_timeout:
            server.shutdown()
            return


def _inside(path: Path, directory: Path) -> bool:
    try:
        path.relative_to(directory)
        return True
    except ValueError:
        return False


def _is_indirect_directory(path: Path) -> bool:
    if path.is_symlink():
        return True
    if os.name != "nt":
        return False
    attributes = getattr(os.lstat(path), "st_file_attributes", 0)
    return bool(attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)


def _runtime_root(runtime_dir: Path) -> Path:
    candidate = Path(runtime_dir)
    if candidate.name != ".bridge" or candidate.parent.name != "short_term_planner_v2":
        raise ValueError("invalid runtime directory")
    candidate.mkdir(parents=True, exist_ok=True)
    if _is_indirect_directory(candidate.parent) or _is_indirect_directory(candidate):
        raise ValueError("invalid runtime directory")
    resolved = candidate.resolve(strict=True)
    if resolved.name != ".bridge" or resolved.parent.name != "short_term_planner_v2":
        raise ValueError("invalid runtime directory")
    return resolved


def _project_lock(scope: Path) -> threading.Lock:
    with _PROJECT_LOCKS_GUARD:
        return _PROJECT_LOCKS.setdefault(scope, threading.Lock())


def _read_json(path: Path) -> Any:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def _load_runtime_file(config_path: Path, ready_path: Path | None = None) -> dict[str, Any]:
    config_file = Path(config_path).resolve(strict=True)
    runtime_dir = config_file.parent
    value = _read_json(config_file)
    required_keys = {"schema", "bridgeId", "leasePath", "bridgeConfig"}
    allowed_keys = required_keys | {"idleTimeoutSeconds"}
    if not isinstance(value, dict) or not required_keys <= set(value) <= allowed_keys:
        raise ValueError("invalid runtime")
    bridge_id = value.get("bridgeId")
    idle_timeout = value.get("idleTimeoutSeconds")
    bridge_config = value.get("bridgeConfig")
    if (
        value.get("schema") != RUNTIME_SCHEMA
        or not isinstance(bridge_id, str)
        or not _HEX_32.fullmatch(bridge_id)
        or (
            idle_timeout is not None
            and (
                isinstance(idle_timeout, bool)
                or not isinstance(idle_timeout, int)
                or not 60 <= idle_timeout <= MAX_LEGACY_IDLE_TIMEOUT
            )
        )
        or config_file.name != f"runtime-{bridge_id}.json"
        or not isinstance(bridge_config, dict)
        or set(bridge_config) != {
            "projectPath",
            "projectKey",
            "projectFingerprint",
            "token",
            "port",
            "snapshotPath",
            "bind",
        }
    ):
        raise ValueError("invalid runtime")
    lease_text = value.get("leasePath")
    if not isinstance(lease_text, str) or not Path(lease_text).is_absolute():
        raise ValueError("invalid runtime")
    lease_path = Path(lease_text).resolve(strict=False)
    if lease_path.parent != runtime_dir or lease_path.name != "active_lease.json":
        raise ValueError("invalid runtime")
    if ready_path is not None:
        ready_file = Path(ready_path).resolve(strict=False)
        if ready_file.parent != runtime_dir or ready_file.name != f"ready-{bridge_id}.json":
            raise ValueError("invalid runtime")
    project_text = bridge_config.get("projectPath")
    snapshot_text = bridge_config.get("snapshotPath")
    project_key = bridge_config.get("projectKey")
    fingerprint = bridge_config.get("projectFingerprint")
    token = bridge_config.get("token")
    if (
        not isinstance(project_text, str)
        or not Path(project_text).is_absolute()
        or Path(project_text).suffix.casefold() != ".vgantt"
        or not isinstance(snapshot_text, str)
        or not Path(snapshot_text).is_absolute()
        or Path(snapshot_text).suffix.casefold() != ".json"
        or not isinstance(project_key, str)
        or not _HEX_64.fullmatch(project_key)
        or not isinstance(fingerprint, str)
        or not _HEX_64.fullmatch(fingerprint)
        or not isinstance(token, str)
        or len(token) < 16
        or bridge_config.get("port") != 0
        or isinstance(bridge_config.get("port"), bool)
        or bridge_config.get("bind") != "127.0.0.1"
    ):
        raise ValueError("invalid runtime")
    if (
        runtime_dir.name != project_key
        or runtime_dir.parent.name != ".bridge"
        or runtime_dir.parent.parent.name != "short_term_planner_v2"
    ):
        raise ValueError("invalid runtime")
    return value


def _load_ready(path: Path) -> dict[str, Any]:
    value = _read_json(path)
    if not isinstance(value, dict) or set(value) != {
        "schema",
        "bridgeId",
        "projectKey",
        "projectFingerprint",
        "baselineTimestamp",
        "port",
        "pid",
    }:
        raise ValueError("invalid ready")
    if (
        value.get("schema") != READY_SCHEMA
        or not isinstance(value.get("bridgeId"), str)
        or not _HEX_32.fullmatch(value["bridgeId"])
        or not isinstance(value.get("projectKey"), str)
        or not _HEX_64.fullmatch(value["projectKey"])
        or not isinstance(value.get("projectFingerprint"), str)
        or not _HEX_64.fullmatch(value["projectFingerprint"])
        or not isinstance(value.get("baselineTimestamp"), str)
        or isinstance(value.get("port"), bool)
        or not isinstance(value.get("port"), int)
        or not 1 <= value["port"] <= 65535
        or isinstance(value.get("pid"), bool)
        or not isinstance(value.get("pid"), int)
        or value["pid"] <= 0
    ):
        raise ValueError("invalid ready")
    return value


def _authenticated_health(endpoint: str, token: str, timeout: float) -> bool:
    parsed = urlsplit(endpoint)
    if parsed.scheme != "http" or parsed.hostname != "127.0.0.1" or parsed.port is None:
        return False
    connection = http.client.HTTPConnection("127.0.0.1", parsed.port, timeout=max(0.05, timeout))
    try:
        connection.request("GET", "/health", headers={"X-STP-V2-Token": token})
        response = connection.getresponse()
        payload = response.read()
        return response.status == 200 and json.loads(payload.decode("utf-8")) == {"status": "ok"}
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError, http.client.HTTPException):
        return False
    finally:
        connection.close()


def _matches_requested_runtime(
    runtime: Mapping[str, Any],
    ready: Mapping[str, Any],
    *,
    bridge_id: str,
    project_path: Path,
    snapshot_path: Path,
    snapshot: Mapping[str, Any],
) -> bool:
    config = runtime["bridgeConfig"]
    return (
        runtime["bridgeId"] == bridge_id
        and ready["bridgeId"] == bridge_id
        and config["projectPath"] == str(project_path)
        and config["snapshotPath"] == str(snapshot_path)
        and config["projectKey"] == snapshot["projectKey"]
        and ready["projectKey"] == snapshot["projectKey"]
        and config["projectFingerprint"] == snapshot["projectFingerprint"]
        and ready["projectFingerprint"] == snapshot["projectFingerprint"]
        and ready["baselineTimestamp"] == snapshot["exportedAt"]
    )


def _reuse_live_bridge(
    scope: Path,
    *,
    project_path: Path,
    snapshot_path: Path,
    snapshot: Mapping[str, Any],
    timeout_seconds: float,
) -> BridgeDescriptor | None:
    bridge_id = _read_lease_id(scope / "active_lease.json")
    if not bridge_id:
        return None
    config_path = scope / f"runtime-{bridge_id}.json"
    ready_path = scope / f"ready-{bridge_id}.json"
    try:
        runtime = _load_runtime_file(config_path, ready_path)
        ready = _load_ready(ready_path)
        if not _matches_requested_runtime(
            runtime,
            ready,
            bridge_id=bridge_id,
            project_path=project_path,
            snapshot_path=snapshot_path,
            snapshot=snapshot,
        ):
            return None
        token = runtime["bridgeConfig"]["token"]
        endpoint = f"http://127.0.0.1:{ready['port']}"
        if not _authenticated_health(endpoint, token, min(max(timeout_seconds, 0.05), 0.5)):
            return None
        if _read_lease_id(scope / "active_lease.json") != bridge_id:
            return None
        return BridgeDescriptor(
            "live", endpoint, token, snapshot["projectKey"], bridge_id, _LIVE_MESSAGE
        )
    except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError, KeyError):
        return None


def launch_read_bridge(
    *,
    project_path: Path,
    snapshot_path: Path,
    snapshot: Mapping[str, Any],
    server_path: Path,
    runtime_dir: Path,
    timeout_seconds: float = 5.0,
) -> BridgeDescriptor:
    project_key = snapshot.get("projectKey") if isinstance(snapshot, Mapping) else ""
    bridge_id = ""
    lease_path: Path | None = None
    project_lock: threading.Lock | None = None
    successful = False
    try:
        if (
            not isinstance(project_key, str)
            or not _HEX_64.fullmatch(project_key)
            or not isinstance(snapshot.get("projectFingerprint"), str)
            or not _HEX_64.fullmatch(snapshot["projectFingerprint"])
            or not isinstance(snapshot.get("exportedAt"), str)
        ):
            raise ValueError("invalid snapshot")
        project = Path(project_path).resolve(strict=True)
        snapshot_file = Path(snapshot_path).resolve(strict=True)
        server = Path(server_path).resolve(strict=True)
        if (
            project.suffix.casefold() != ".vgantt"
            or snapshot_file.suffix.casefold() != ".json"
            or snapshot.get("projectPath") != str(project)
        ):
            raise ValueError("invalid input")
        runtime_root = _runtime_root(runtime_dir)
        scope = runtime_root / project_key
        scope.mkdir(exist_ok=True)
        if _is_indirect_directory(scope):
            raise ValueError("invalid runtime scope")
        scope = scope.resolve(strict=True)
        if scope.parent != runtime_root or not _inside(scope, runtime_root):
            raise ValueError("invalid runtime scope")
        project_lock = _project_lock(scope)
        project_lock.acquire()
        lease_path = scope / "active_lease.json"

        reused = _reuse_live_bridge(
            scope,
            project_path=project,
            snapshot_path=snapshot_file,
            snapshot=snapshot,
            timeout_seconds=timeout_seconds,
        )
        if reused is not None:
            successful = True
            return reused

        _atomic_write_json(lease_path, {"schema": LEASE_SCHEMA, "bridgeId": "0" * 32})
        bridge_id = uuid.uuid4().hex
        token = secrets.token_urlsafe(32)
        if not _HEX_32.fullmatch(bridge_id) or len(token) < 32:
            raise ValueError("invalid secret")
        config_path = scope / f"runtime-{bridge_id}.json"
        ready_path = scope / f"ready-{bridge_id}.json"
        runtime = {
            "schema": RUNTIME_SCHEMA,
            "bridgeId": bridge_id,
            "leasePath": str(lease_path.resolve()),
            "bridgeConfig": {
                "projectPath": str(project),
                "projectKey": project_key,
                "projectFingerprint": snapshot["projectFingerprint"],
                "token": token,
                "port": 0,
                "snapshotPath": str(snapshot_file),
                "bind": "127.0.0.1",
            },
        }
        _atomic_write_json(config_path, runtime)
        _atomic_write_json(lease_path, {"schema": LEASE_SCHEMA, "bridgeId": bridge_id})
        command = [
            sys.executable,
            str(server),
            "--config",
            str(config_path.resolve()),
            "--ready",
            str(ready_path.resolve()),
        ]
        process = subprocess.Popen(
            command,
            cwd=str(server.parent),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        deadline = time.monotonic() + max(0.0, timeout_seconds)
        while True:
            if process.poll() is not None:
                break
            if ready_path.exists():
                try:
                    ready = _load_ready(ready_path)
                except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError):
                    break
                if not _matches_requested_runtime(
                    runtime,
                    ready,
                    bridge_id=bridge_id,
                    project_path=project,
                    snapshot_path=snapshot_file,
                    snapshot=snapshot,
                ):
                    break
                endpoint = f"http://127.0.0.1:{ready['port']}"
                remaining = max(0.05, deadline - time.monotonic())
                if (
                    _authenticated_health(endpoint, token, min(remaining, 0.5))
                    and _read_lease_id(lease_path) == bridge_id
                ):
                    successful = True
                    return BridgeDescriptor(
                        "live", endpoint, token, project_key, bridge_id, _LIVE_MESSAGE
                    )
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            time.sleep(min(0.02, remaining))
    except (OSError, UnicodeError, json.JSONDecodeError, TypeError, ValueError, KeyError):
        pass
    finally:
        if (
            not successful
            and lease_path is not None
            and bridge_id
            and _read_lease_id(lease_path) == bridge_id
        ):
            try:
                _atomic_write_json(
                    lease_path, {"schema": LEASE_SCHEMA, "bridgeId": "0" * 32}
                )
            except (OSError, TypeError, ValueError):
                pass
        if project_lock is not None:
            project_lock.release()
    return _static(project_key if isinstance(project_key, str) else "", bridge_id)
