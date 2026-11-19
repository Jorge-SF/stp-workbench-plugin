"""On-disk replacement for the bundle's ``materialize_runtime_resources``, and the runtime folder.

The bundled Script 2 wrote its embedded HTML/JS/JPEG resources and a bridge
launcher into ``<script dir>/.stp_ug_v2_runtime``.  Script 2 now uses a
**per-user** runtime folder instead (``default_runtime_root``), so an installed
copy under Program Files works for normal users and one user's bridge token is
never readable by another (docs/known_issues.md, KI-2).

This module copies the web resources from files on disk:

* from ``STP_WEB_RESOURCES`` (environment variable) if set; else from
  ``<python root>/web`` (the installed layout, see installer/StpUG.wxs); else
  from ``<repo>/reference/web`` (a checkout).  Both file-name styles are
  accepted: the bundle's (``stp_v2_planner_logic.js``) and reference/web's
  (``planner_logic.js``);
* the Maptek footer logo (``assets/Maptek_highres.jpg``) is optional: the public
  repository does not hold it.  Without it the page shows no footer logo;
* ``stp_live_refresh_server_v2.py`` is written as a small launcher that puts
  this package on ``sys.path`` and runs the bridge module as ``__main__``
  (the bundle embedded every module source in the launcher instead).
"""

from __future__ import annotations

import os
import shutil
from pathlib import Path
from typing import Mapping

PYTHON_ROOT = Path(__file__).resolve().parent.parent
INSTALLED_RESOURCE_DIR = PYTHON_ROOT / "web"
CHECKOUT_RESOURCE_DIR = PYTHON_ROOT.parent / "reference" / "web"
# Kept for callers of the first version of this module.
DEFAULT_RESOURCE_DIR = CHECKOUT_RESOURCE_DIR

RUNTIME_ROOT_ENV = "STP_RUNTIME_ROOT"
WEB_RESOURCES_ENV = "STP_WEB_RESOURCES"

TEMPLATE_NAME = "short_term_planner_template_v2.html"
LOGO_NAME = "assets/Maptek_highres.jpg"

# Runtime name -> accepted source names (bundle name first, then reference/web's).
RESOURCE_SOURCES: Mapping[str, tuple[str, ...]] = {
    TEMPLATE_NAME: (TEMPLATE_NAME,),
    "stp_v2_planner_logic.js": ("stp_v2_planner_logic.js", "planner_logic.js"),
    "stp_v2_refresh_client.js": ("stp_v2_refresh_client.js", "refresh_client.js"),
    "stp_v2_timeline_logic.js": ("stp_v2_timeline_logic.js", "timeline_logic.js"),
    "stp_v2_v1_compat_logic.js": ("stp_v2_v1_compat_logic.js", "v1_compat_logic.js"),
}
RESOURCE_NAMES = (*RESOURCE_SOURCES, LOGO_NAME)

_LAUNCHER_TEMPLATE = '''"""Generated STP UG V2 local live-bridge launcher.  Do not edit."""

import runpy
import sys

_PYTHON_ROOT = {root!r}
if _PYTHON_ROOT not in sys.path:
    sys.path.insert(0, _PYTHON_ROOT)
import stp_core  # noqa: E402,F401

runpy.run_module("stp_live_refresh_server_v2", run_name="__main__", alter_sys=True)
'''


def default_runtime_root(environ: Mapping[str, str] | None = None) -> Path:
    """The per-user runtime folder: ``STP_RUNTIME_ROOT`` if set, else
    ``%LOCALAPPDATA%\\Maptek\\StpUG\\runtime``.

    Script 2 computes the same path inline (it must also work as a single-file
    bundle, which does not import this module); keep the two in step.  The C#
    plugin's ``PluginSession.DefaultRuntimeFolder`` is the same path too.
    """
    env = os.environ if environ is None else environ
    configured = env.get(RUNTIME_ROOT_ENV)
    if configured:
        return Path(configured)
    local = env.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(local) / "Maptek" / "StpUG" / "runtime"


def resource_dir() -> Path:
    """The folder the web resources are copied from (see the module docstring)."""
    configured = os.environ.get(WEB_RESOURCES_ENV)
    if configured:
        return Path(configured)
    for candidate in (INSTALLED_RESOURCE_DIR, CHECKOUT_RESOURCE_DIR):
        if (candidate / TEMPLATE_NAME).is_file():
            return candidate
    return CHECKOUT_RESOURCE_DIR


def bridge_launcher_source() -> str:
    return _LAUNCHER_TEMPLATE.format(root=str(PYTHON_ROOT))


def _source_for(source_dir: Path, names: tuple[str, ...]) -> Path | None:
    for name in names:
        if (source_dir / name).is_file():
            return source_dir / name
    return None


def materialize_runtime_resources(root):
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    source_dir = resource_dir()
    sources = {target: _source_for(source_dir, names) for target, names in RESOURCE_SOURCES.items()}
    missing = [target for target, source in sources.items() if source is None]
    if missing:
        raise FileNotFoundError(
            f"STP web resources not found in {source_dir}: {', '.join(missing)} "
            f"(set {WEB_RESOURCES_ENV} to the folder that holds them)"
        )
    for target, source in sources.items():
        shutil.copyfile(source, root / target)
    logo_target = root / LOGO_NAME
    logo_source = source_dir / LOGO_NAME
    if logo_source.is_file():
        logo_target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(logo_source, logo_target)
    bridge_launcher = root / "stp_live_refresh_server_v2.py"
    bridge_launcher.write_text(bridge_launcher_source(), encoding="utf-8", newline="\n")
    return root
