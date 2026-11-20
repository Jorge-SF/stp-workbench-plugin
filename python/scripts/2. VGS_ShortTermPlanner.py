"""Read-only VGS adapter that exports a self-contained STP V2 planner page."""
from __future__ import annotations  # bundles: supplied by the header


import base64
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


SNAPSHOT_PLACEHOLDER = "__VGS_STP_V2_SNAPSHOT__"
BRIDGE_PLACEHOLDER = "__VGS_STP_V2_BRIDGE__"
LOGIC_PLACEHOLDER = "__VGS_STP_V2_LOGIC__"
TIMELINE_LOGIC_PLACEHOLDER = "__VGS_STP_V2_TIMELINE_LOGIC__"
REFRESH_CLIENT_PLACEHOLDER = "__VGS_STP_V2_REFRESH_CLIENT__"
COMPAT_LOGIC_PLACEHOLDER = "__VGS_STP_V2_COMPAT_LOGIC__"
MAPTEK_LOGO_MARKER = "__MAPTEK_LOGO_DATA_URL__"
_PLACEHOLDERS = (
    SNAPSHOT_PLACEHOLDER,
    BRIDGE_PLACEHOLDER,
    LOGIC_PLACEHOLDER,
    TIMELINE_LOGIC_PLACEHOLDER,
    REFRESH_CLIENT_PLACEHOLDER,
    COMPAT_LOGIC_PLACEHOLDER,
)

# Plugin mode (Workbench C# view): see docs/plugin_session.md.
PLUGIN_SESSION_FILE_NAME = "plugin_session.json"
PLUGIN_SESSION_ARGUMENT = "--plugin-session"
PLUGIN_MODE_ENV = "STP_PLUGIN_MODE"
PLUGIN_SESSION_ENV = "STP_PLUGIN_SESSION"
_TRUE_VALUES = {"1", "true", "yes", "on"}


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


def _safe_json(value: Mapping[str, Any]) -> str:
    return (
        json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)
        .replace("<", "\\u003c")
        .replace(">", "\\u003e")
        .replace("&", "\\u0026")
        .replace("\u2028", "\\u2028")
        .replace("\u2029", "\\u2029")
    )


def _safe_logic(value: str) -> str:
    # Only an HTML end-tag is dangerous in executable source.  Escaping every
    # less-than sign would corrupt JavaScript comparison operators.
    return re.sub(r"</script", r"<\\/script", value, flags=re.IGNORECASE).replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")


# A transparent 1x1 GIF: the footer shows no logo when the approved image is not installed.
_NO_LOGO_DATA_URL = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"


def _maptek_logo_data_url(template_path: Path) -> str:
    """Return the approved footer mark as an offline-safe JPEG data URL.

    The public repository does not hold the logo, so it is optional: without
    ``assets/Maptek_highres.jpg`` beside the template the footer image is blank.
    """
    logo_path = Path(template_path).parent / "assets" / "Maptek_highres.jpg"
    try:
        encoded = base64.b64encode(logo_path.read_bytes()).decode("ascii")
    except OSError:
        return _NO_LOGO_DATA_URL
    return f"data:image/jpeg;base64,{encoded}"


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
    """The exact bridge config the planner gets (``__VGS_STP_V2_BRIDGE__`` or the plugin session)."""
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
) -> Path | None:
    """Return the plugin session path asked for, or ``None`` for browser mode.

    Plugin mode is selected by ``--plugin-session [path]`` / ``--plugin-session=<path>`` on
    the command line, or by ``STP_PLUGIN_SESSION=<path>`` / ``STP_PLUGIN_MODE=1`` in the
    environment.  Vulcan's runner may give no usable ``sys.argv``; that means browser mode.
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
    if configured:
        return resolve_plugin_session_path(configured)
    if environ.get(PLUGIN_MODE_ENV, "").strip().casefold() in _TRUE_VALUES:
        return resolve_plugin_session_path(True)
    return None


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


def render_planner(
    snapshot: Mapping[str, Any],
    bridge_config: Mapping[str, Any],
    template_path: Path,
    planner_logic_path: Path,
    timeline_logic_path: Path,
    refresh_client_path: Path,
    compatibility_logic_path: Path,
    output_path: Path,
    *,
    precedence_conflict_policy: str = "block-inherited",
) -> Path:
    """Embed all planner inputs only after exact placeholder validation."""
    if precedence_conflict_policy not in {"block-inherited", "allow-inherited"}:
        raise ValueError("unsupported precedence conflict policy")
    template = Path(template_path).read_text(encoding="utf-8")
    if any(template.count(placeholder) != 1 for placeholder in _PLACEHOLDERS):
        raise ValueError("template must contain every V2 placeholder exactly once")
    discovered = set(re.findall(r"__VGS_STP_V2_[A-Z_]+__", template))
    if discovered != set(_PLACEHOLDERS):
        raise ValueError("template contains an unknown or malformed V2 placeholder")
    rendered_bridge_config = planner_bridge_config(
        bridge_config, precedence_conflict_policy=precedence_conflict_policy
    )
    rendered = template
    if MAPTEK_LOGO_MARKER in rendered:
        if rendered.count(MAPTEK_LOGO_MARKER) != 1:
            raise ValueError("template must contain one Maptek footer logo marker")
        rendered = rendered.replace(MAPTEK_LOGO_MARKER, _maptek_logo_data_url(Path(template_path)))
    replacements = (
        (SNAPSHOT_PLACEHOLDER, _safe_json(snapshot)),
        (BRIDGE_PLACEHOLDER, _safe_json(rendered_bridge_config)),
        (
            LOGIC_PLACEHOLDER,
            _safe_logic(Path(planner_logic_path).read_text(encoding="utf-8")),
        ),
        (
            TIMELINE_LOGIC_PLACEHOLDER,
            _safe_logic(Path(timeline_logic_path).read_text(encoding="utf-8")),
        ),
        (
            REFRESH_CLIENT_PLACEHOLDER,
            _safe_logic(Path(refresh_client_path).read_text(encoding="utf-8")),
        ),
        (
            COMPAT_LOGIC_PLACEHOLDER,
            _safe_logic(Path(compatibility_logic_path).read_text(encoding="utf-8")),
        ),
    )
    for placeholder, replacement in replacements:
        rendered = rendered.replace(placeholder, replacement)
    if "__VGS_STP_V2_" in rendered:
        raise ValueError("unreplaced V2 placeholder")
    destination = Path(output_path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        descriptor, name = tempfile.mkstemp(
            prefix=f".{destination.name}.", suffix=".tmp", dir=destination.parent
        )
        temporary_path = Path(name)
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(rendered)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_path, destination)
        temporary_path = None
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)
    return destination


def open_planner(path: Path) -> tuple[bool, str]:
    target = Path(path).resolve(strict=True)
    try:
        os.startfile(str(target))
        return True, f"STP V2 abierto: {target}"
    except OSError:
        return False, f"No se pudo abrir automáticamente. Abra este archivo: {target}"


def _report(message: str) -> None:
    try:
        import vulcan_gui  # type: ignore[import-not-found]

        vulcan_gui.message(message)
    except (ImportError, AttributeError, OSError, RuntimeError, TypeError):
        print(message)


def run_planner(
    *,
    precedence_conflict_policy: str = "allow-inherited",
    output_directory_name: str = "short_term_planner_v2",
    plugin_session: Any = None,
) -> Path | None:
    """Run the V2 exporter with an explicit UI policy and isolated output directory.

    ``plugin_session=None`` is browser mode: render ``index.html`` and open it (returns None).
    Otherwise (``True`` or a path, see ``resolve_plugin_session_path``) nothing is rendered or
    opened: the snapshot and the plugin session file are written, the bridge is left running
    as in browser mode, and the session path is printed and returned.
    """
    if precedence_conflict_policy not in {"block-inherited", "allow-inherited"}:
        raise ValueError("unsupported precedence conflict policy")
    if (
        not isinstance(output_directory_name, str)
        or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]*", output_directory_name)
    ):
        raise ValueError("invalid planner output directory name")
    session_path = (
        None if plugin_session is None else resolve_plugin_session_path(plugin_session)
    )
    if session_path is not None:
        remove_plugin_session(session_path)
    from maptek.gantt import Gantt  # type: ignore[import-not-found]

    project = Gantt()
    project_file = Path(project.project_filename(True))
    resource_root = RESOURCE_DIR
    output_dir = RUNTIME_ROOT / output_directory_name
    output_dir.mkdir(parents=True, exist_ok=True)
    snapshot = build_snapshot(project, project_file, output_dir / "activity_grid.csv")
    snapshot_path = _atomic_write_json(output_dir / "snapshot_v2.json", snapshot)
    bridge_runtime_dir = output_dir / ".bridge"
    bridge = launch_read_bridge(
        project_path=project_file,
        snapshot_path=snapshot_path,
        snapshot=snapshot,
        server_path=resource_root / "stp_live_refresh_server_v2.py",
        runtime_dir=bridge_runtime_dir,
    )
    if session_path is not None:
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
    output = render_planner(
        snapshot,
        bridge.page_config(),
        resource_root / "short_term_planner_template_v2.html",
        resource_root / "stp_v2_planner_logic.js",
        resource_root / "stp_v2_timeline_logic.js",
        resource_root / "stp_v2_refresh_client.js",
        resource_root / "stp_v2_v1_compat_logic.js",
        output_dir / "index.html",
        precedence_conflict_policy=precedence_conflict_policy,
    )
    _, open_message = open_planner(output)
    bridge_message = (
        "Actualización en vivo disponible."
        if bridge.mode == "live"
        else "No se pudo iniciar la actualización en vivo; se usará el modo estático."
    )
    policy_message = (
        "STP V2 — conflictos heredados permitidos. "
        if precedence_conflict_policy == "allow-inherited"
        else ""
    )
    _report(f"{policy_message}{bridge_message} {open_message}")
    return None


def main() -> None:
    """Run the standard safe V2 planner inside VGS (plugin mode when asked for)."""
    run_planner(plugin_session=plugin_session_request())


if __name__ == "__main__":
    main()
