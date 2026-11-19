"""On-disk replacement for the bundle's ``materialize_runtime_resources``.

The bundled Script 2 wrote its embedded HTML/JS/JPEG resources and a bridge
launcher into ``<script dir>/.stp_ug_v2_runtime``.  This module does the same
from files on disk:

* web resources are copied from ``STP_WEB_RESOURCES`` (environment variable)
  or, by default, from ``<repo>/reference/web``;
* ``stp_live_refresh_server_v2.py`` is written as a small launcher that puts
  this package on ``sys.path`` and runs the bridge module as ``__main__``
  (the bundle embedded every module source in the launcher instead).
"""

from __future__ import annotations

import os
import shutil
from pathlib import Path

PYTHON_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_RESOURCE_DIR = PYTHON_ROOT.parent / "reference" / "web"

RESOURCE_NAMES = (
    "short_term_planner_template_v2.html",
    "stp_v2_planner_logic.js",
    "stp_v2_refresh_client.js",
    "stp_v2_timeline_logic.js",
    "stp_v2_v1_compat_logic.js",
    "assets/Maptek_highres.jpg",
)

_LAUNCHER_TEMPLATE = '''"""Generated STP UG V2 local live-bridge launcher.  Do not edit."""

import runpy
import sys

_PYTHON_ROOT = {root!r}
if _PYTHON_ROOT not in sys.path:
    sys.path.insert(0, _PYTHON_ROOT)
import stp_core  # noqa: E402,F401

runpy.run_module("stp_live_refresh_server_v2", run_name="__main__", alter_sys=True)
'''


def resource_dir() -> Path:
    configured = os.environ.get("STP_WEB_RESOURCES")
    return Path(configured) if configured else DEFAULT_RESOURCE_DIR


def bridge_launcher_source() -> str:
    return _LAUNCHER_TEMPLATE.format(root=str(PYTHON_ROOT))


def materialize_runtime_resources(root):
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    source_dir = resource_dir()
    missing = [name for name in RESOURCE_NAMES if not (source_dir / name).is_file()]
    if missing:
        raise FileNotFoundError(
            f"STP web resources not found in {source_dir}: {', '.join(missing)} "
            "(set STP_WEB_RESOURCES to the folder that holds them)"
        )
    for name in RESOURCE_NAMES:
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source_dir / name, target)
    bridge_launcher = root / "stp_live_refresh_server_v2.py"
    bridge_launcher.write_text(bridge_launcher_source(), encoding="utf-8", newline="\n")
    return root
