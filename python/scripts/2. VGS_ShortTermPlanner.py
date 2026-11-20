"""Read-only VGS adapter that prepares an STP V2 planner session for the Workbench plugin.

It exports the snapshot, starts the read bridge and writes ``plugin_session.json`` in the
per-user runtime folder; the C# planner view opens that session.  The old browser page
(``index.html`` opened with the default browser) is gone: the plugin is the only UI.
The web resources are still copied into the runtime folder because the plugin's
"STP suelto" export builds its standalone HTML from them.
"""
from __future__ import annotations  # bundles: supplied by the header


from datetime import datetime
import json
import os
from pathlib import Path
import re
import sys
import tempfile
from typing import Any, Mapping

# BEGIN STP CORE BOOTSTRAP (build_bundle.py replaces this block)
import os as _stp_os
SCRIPT_DIR = Path(__file__).resolve().parent
_STP_PYTHON_ROOT = Path(_stp_os.environ.get("STP_CORE_PATH") or SCRIPT_DIR.parent)
if str(_STP_PYTHON_ROOT) not in sys.path:
    sys.path.insert(0, str(_STP_PYTHON_ROOT))
import stp_core  # noqa: E402,F401  (exposes the flat stp_v2_* module names)
from stp_core.runtime import materialize_runtime_resources
# END STP CORE BOOTSTRAP
# Per-user runtime folder (docs/known_issues.md, KI-2): never beside the script, which may be in Program Files.
# Same rule as stp_core.runtime.default_runtime_root; computed inline so the single-file bundle works too.
RUNTIME_ROOT = Path(
    os.environ.get("STP_RUNTIME_ROOT")
    or Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local") / "Maptek" / "StpUG" / "runtime"
)
RESOURCE_DIR = materialize_runtime_resources(RUNTIME_ROOT)
# Now that it exists, use its full long path (an 8.3 short TEMP/LOCALAPPDATA such as RUNNER~1 is expanded), so the
# paths script 2 hands out match resolve_plugin_session_path and the plugin's view of the folder.
RUNTIME_ROOT = RESOURCE_DIR = Path(RESOURCE_DIR).resolve()

from stp_v2_bridge_bootstrap import launch_read_bridge
from stp_v2_core import read_vgantt
from stp_v2_snapshot_contract import build_canonical_snapshot, parse_activity_grid_csv
import stp_v2_windows_atomic


# Workbench plugin session: see docs/plugin_session.md.
PLUGIN_SESSION_FILE_NAME = "plugin_session.json"
PLUGIN_SESSION_ARGUMENT = "--plugin-session"
PLUGIN_SESSION_ENV = "STP_PLUGIN_SESSION"


def build_snapshot(project: Any, project_file: Path, csv_path: Path) -> dict[str, Any]:
    """Export the grid then enrich it with the authoritative, read-only archive state."""
    target = Path(project_file).resolve()
    adapter_path = Path(project.project_filename(True)).resolve()
    if adapter_path != target:
        raise ValueError("project path does not match the VGS project adapter")
    csv_parent = Path(csv_path).resolve().parent
    csv_parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix="stp_v2_activity_grid_", suffix=".csv", dir=csv_parent)
    os.close(descriptor)
    export_path = Path(temporary_name)
    export_path.unlink()
    try:
        result = project.export_activities_to_csv(str(export_path), False)
        if result is not True or not export_path.is_file():
            raise ValueError("VGS did not export the Activity Grid CSV")
        grid = parse_activity_grid_csv(export_path)
    finally:
        export_path.unlink(missing_ok=True)
    state = read_vgantt(target)
    return build_canonical_snapshot(
        state,
        target,
        grid,
        exported_at=datetime.now().replace(microsecond=0),
    )


def _atomic_write_json(path: Path, value: Mapping[str, Any]) -> Path:
    """Write UTF-8 JSON without exposing a partial destination."""
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
        allow_nan=False,
    ).encode("utf-8")
    temporary_path: Path | None = None
    try:
        descriptor, name = tempfile.mkstemp(
            prefix=f".{destination.name}.", suffix=".tmp", dir=destination.parent
        )
        temporary_path = Path(name)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_path, destination)
        temporary_path = None
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)
    return destination


def planner_bridge_config(
    bridge_config: Mapping[str, Any],
    *,
    precedence_conflict_policy: str,
) -> dict[str, Any]:
    """The exact bridge config the plugin session carries."""
    if precedence_conflict_policy not in {"block-inherited", "allow-inherited"}:
        raise ValueError("unsupported precedence conflict policy")
    return {
        **bridge_config,
        "precedenceConflictPolicy": precedence_conflict_policy,
    }


def default_plugin_session_path() -> Path:
    return RUNTIME_ROOT / PLUGIN_SESSION_FILE_NAME


def resolve_plugin_session_path(requested: Any) -> Path:
    """Validate a requested session path: a ``.json`` file directly in the runtime folder.

    ``True`` or an empty string means the default ``<runtime>/plugin_session.json``; a
    relative path is taken relative to the runtime folder.  The session file holds the bridge
    token, so it may not be written anywhere else.
    """
    if requested is True or requested == "":
        candidate = default_plugin_session_path()
    elif isinstance(requested, (str, os.PathLike)):
        candidate = Path(requested)
        if not candidate.is_absolute():
            candidate = RUNTIME_ROOT / candidate
    else:
        raise ValueError("invalid plugin session path")
    RUNTIME_ROOT.mkdir(parents=True, exist_ok=True)
    runtime_root = RUNTIME_ROOT.resolve(strict=True)
    try:
        parent = candidate.parent.resolve(strict=True)
    except OSError:
        parent = None
    if (
        parent != runtime_root
        or candidate.suffix.casefold() != ".json"
        or candidate.name.startswith(".")
    ):
        raise ValueError(f"plugin session file must be a .json file directly in {runtime_root}")
    resolved = runtime_root / candidate.name
    if resolved.is_symlink() or resolved.is_dir():
        raise ValueError("invalid plugin session path")
    return resolved


def plugin_session_request(
    argv: Any = None,
    environ: Mapping[str, str] | None = None,
) -> Path:
    """Return the plugin session path to write.

    ``--plugin-session <path>`` / ``--plugin-session=<path>`` on the command line, else
    ``STP_PLUGIN_SESSION=<path>`` in the environment, else the default
    ``<runtime>/plugin_session.json``.  Vulcan's runner may give no usable ``sys.argv``.
    """
    if argv is None:
        argv = getattr(sys, "argv", None) or []
    if environ is None:
        environ = os.environ
    arguments = [str(item) for item in list(argv)[1:]]
    for index, argument in enumerate(arguments):
        if argument == PLUGIN_SESSION_ARGUMENT:
            following = arguments[index + 1] if index + 1 < len(arguments) else ""
            if following.startswith("--"):
                following = ""
            return resolve_plugin_session_path(following)
        if argument.startswith(PLUGIN_SESSION_ARGUMENT + "="):
            return resolve_plugin_session_path(argument.split("=", 1)[1])
    configured = environ.get(PLUGIN_SESSION_ENV, "").strip()
    return resolve_plugin_session_path(configured or True)


def write_plugin_session(
    session_path: Path,
    snapshot_path: Path,
    bridge_config: Mapping[str, Any],
) -> Path:
    """Atomically publish ``{snapshot_path, bridge_config}`` for the Workbench plugin."""
    destination = Path(session_path)
    session = {
        "snapshot_path": str(Path(snapshot_path).resolve(strict=True)),
        "bridge_config": dict(bridge_config),
    }
    if os.name != "nt":  # test hosts only; Vulcan always runs on Windows
        return _atomic_write_json(destination, session)
    payload = json.dumps(
        session,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
        allow_nan=False,
    ).encode("utf-8")
    stp_v2_windows_atomic.atomic_replace_bytes_windows(
        destination, payload, expected_parent=destination.parent
    )
    return destination


def remove_plugin_session(session_path: Path) -> None:
    """Drop a previous session first, so the plugin never opens a stale one."""
    try:
        Path(session_path).unlink()
    except FileNotFoundError:
        pass


def run_planner(
    *,
    precedence_conflict_policy: str = "allow-inherited",
    output_directory_name: str = "short_term_planner_v2",
    plugin_session: Any = True,
) -> Path:
    """Export the snapshot, start the read bridge and publish the plugin session.

    ``plugin_session`` is ``True`` (default ``<runtime>/plugin_session.json``) or a path, see
    ``resolve_plugin_session_path``.  The session path is printed and returned; the bridge is
    left running for the plugin.
    """
    if precedence_conflict_policy not in {"block-inherited", "allow-inherited"}:
        raise ValueError("unsupported precedence conflict policy")
    if (
        not isinstance(output_directory_name, str)
        or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", output_directory_name)
    ):
        raise ValueError("invalid planner output directory name")
    session_path = resolve_plugin_session_path(plugin_session)
    remove_plugin_session(session_path)
    from maptek.gantt import Gantt  # type: ignore[import-not-found]

    project = Gantt()
    project_file = Path(project.project_filename(True))
    output_dir = RUNTIME_ROOT / output_directory_name
    output_dir.mkdir(parents=True, exist_ok=True)
    snapshot = build_snapshot(project, project_file, output_dir / "activity_grid.csv")
    snapshot_path = _atomic_write_json(output_dir / "snapshot_v2.json", snapshot)
    bridge = launch_read_bridge(
        project_path=project_file,
        snapshot_path=snapshot_path,
        snapshot=snapshot,
        server_path=RESOURCE_DIR / "stp_live_refresh_server_v2.py",
        runtime_dir=output_dir / ".bridge",
    )
    written = write_plugin_session(
        session_path,
        snapshot_path,
        planner_bridge_config(
            bridge.page_config(),
            precedence_conflict_policy=precedence_conflict_policy,
        ),
    )
    print(f"STP_PLUGIN_SESSION={written}")
    return written


def main() -> Path:
    """Run the standard safe V2 planner inside VGS for the Workbench plugin."""
    return run_planner(plugin_session=plugin_session_request())

if __name__ == "__main__":
    main()
