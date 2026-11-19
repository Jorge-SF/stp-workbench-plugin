"""Shared constants describing the legacy single-file STP UG V2 bundle layout.

The four public VGS scripts (1-4) were distributed as self-contained files that
embed every ``stp_core`` module as a raw string literal.  ``unbundle.py`` reads
that layout and ``build_bundle.py`` writes it back.  Keeping the layout here
lets the round trip be checked byte for byte.
"""

from __future__ import annotations

MODULE_NAMES = (
    "stp_live_refresh_server_v2",
    "stp_v2_apply_contract",
    "stp_v2_apply_transaction",
    "stp_v2_bridge_bootstrap",
    "stp_v2_core",
    "stp_v2_excel_export",
    "stp_v2_maptek_apply_adapter",
    "stp_v2_prepare_project",
    "stp_v2_snapshot_contract",
    "stp_v2_windows_atomic",
)

TEXT_RESOURCE_NAMES = (
    "short_term_planner_template_v2.html",
    "stp_v2_planner_logic.js",
    "stp_v2_refresh_client.js",
    "stp_v2_timeline_logic.js",
    "stp_v2_v1_compat_logic.js",
)

BINARY_RESOURCE_NAMES = ("assets/Maptek_highres.jpg",)

# Public scripts: bundle file name -> thin entry script name (same name).
SCRIPTS = (
    "1. VGS_PrepareProjectForSTP.py",
    "2. VGS_ShortTermPlanner.py",
    "3. VGS_ApplySTPToVGS.py",
    "4. VGS_VerifySTPSave.py",
)

BUNDLE_HEADER = '''"""STP UG V2 public editable self-contained script.  Do not add external core imports."""

from __future__ import annotations

import base64
import importlib.abc
import importlib.util
from pathlib import Path
import sys

# BEGIN MODULE: stp_v2_core.py
# Module sources below remain editable text literals.
# END MODULE: stp_v2_core.py

'''

RESOURCE_HEADER = '''
# BEGIN RESOURCE: stp_v2_planner_logic.js
# Text resources below remain editable text literals.
# END RESOURCE: stp_v2_planner_logic.js

'''

LAUNCHER_HEADER = '''"""Generated STP UG V2 local live-bridge launcher.  Do not edit."""

from __future__ import annotations

import importlib.abc
import importlib.util
import sys

'''

LAUNCHER_FOOTER = '''
class _EmbeddedLoader(importlib.abc.Loader):
    def __init__(self, name):
        self.name = name

    def create_module(self, spec):
        return None

    def exec_module(self, module):
        module.__file__ = f"<embedded-stp-live-bridge:{self.name}>"
        module.__package__ = ""
        exec(compile(MODULE_SOURCES[self.name], module.__file__, "exec"), module.__dict__)


class _EmbeddedFinder(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname not in MODULE_SOURCES:
            return None
        return importlib.util.spec_from_loader(fullname, _EmbeddedLoader(fullname), origin=f"<embedded-stp-live-bridge:{fullname}>")


if not any(isinstance(finder, _EmbeddedFinder) for finder in sys.meta_path):
    sys.meta_path.insert(0, _EmbeddedFinder())

_MODULE_NAME = "stp_live_refresh_server_v2"
_MODULE_PATH = f"<embedded-stp-live-bridge:{_MODULE_NAME}>"
_NAMESPACE = {"__name__": "__main__", "__file__": _MODULE_PATH, "__package__": ""}
exec(compile(MODULE_SOURCES[_MODULE_NAME], _MODULE_PATH, "exec"), _NAMESPACE)'''

LOADER_PRELUDE = '''
class _EmbeddedLoader(importlib.abc.Loader):
    def __init__(self, name):
        self.name = name

    def create_module(self, spec):
        return None

    def exec_module(self, module):
        module.__file__ = f"<embedded-stp:{self.name}>"
        module.__package__ = ""
        exec(compile(MODULE_SOURCES[self.name], module.__file__, "exec"), module.__dict__)


class _EmbeddedFinder(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname not in MODULE_SOURCES:
            return None
        return importlib.util.spec_from_loader(fullname, _EmbeddedLoader(fullname), origin=f"<embedded-stp:{fullname}>")


_EMBEDDED_FINDER = _EmbeddedFinder()


def activate_internal_modules():
    if _EMBEDDED_FINDER not in sys.meta_path:
        sys.meta_path.insert(0, _EMBEDDED_FINDER)


def materialize_runtime_resources(root):
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    for name, value in RESOURCE_SOURCES.items():
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(value, encoding="utf-8", newline="\\n")
    for name, value in BINARY_RESOURCES.items():
        target = root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(base64.b85decode(value.encode("ascii")))
    bridge_launcher = root / "stp_live_refresh_server_v2.py"
    bridge_launcher.write_text(BRIDGE_LAUNCHER_SOURCE, encoding="utf-8", newline="\\n")
    return root

'''

PUBLIC_BEGIN = "# BEGIN PUBLIC SCRIPT\n"
PUBLIC_END = "\n# END PUBLIC SCRIPT"

# In the bundle every public script starts the embedded importer like this ...
BUNDLE_BOOTSTRAP = '''SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))
activate_internal_modules()
'''

# ... while the thin entry scripts find the on-disk ``stp_core`` package instead.
# build_bundle.py swaps the marked block back for BUNDLE_BOOTSTRAP.
THIN_BOOTSTRAP_BEGIN = "# BEGIN STP CORE BOOTSTRAP (build_bundle.py replaces this block)\n"
THIN_BOOTSTRAP_END = "# END STP CORE BOOTSTRAP\n"

# The bundle header supplies ``from __future__ import annotations`` for the public
# script; thin scripts carry this exact line right after their docstring instead.
THIN_FUTURE_LINE = "from __future__ import annotations  # bundles: supplied by the header\n"


def thin_bootstrap(needs_runtime: bool) -> str:
    runtime_import = (
        "from stp_core.runtime import materialize_runtime_resources\n" if needs_runtime else ""
    )
    return (
        THIN_BOOTSTRAP_BEGIN
        + "import os as _stp_os\n"
        + "SCRIPT_DIR = Path(__file__).resolve().parent\n"
        + "_STP_PYTHON_ROOT = Path(_stp_os.environ.get(\"STP_CORE_PATH\") or SCRIPT_DIR.parent)\n"
        + "if str(_STP_PYTHON_ROOT) not in sys.path:\n"
        + "    sys.path.insert(0, str(_STP_PYTHON_ROOT))\n"
        + "import stp_core  # noqa: E402,F401  (exposes the flat stp_v2_* module names)\n"
        + runtime_import
        + THIN_BOOTSTRAP_END
    )


def module_dict_literal(name: str, sources: dict[str, str]) -> str:
    parts = [f"{name} = {{\n"]
    for key, value in sources.items():
        if "'''" in value or value.endswith("\\"):
            raise ValueError(f"{key} cannot be stored as a raw triple-quoted literal")
        parts.append(f"    {key!r}: r'''{value}''',\n")
    parts.append("}\n")
    return "".join(parts)
