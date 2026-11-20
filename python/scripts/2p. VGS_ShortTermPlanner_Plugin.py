"""STP V2 planner for the Workbench C# view.

Runs ``2. VGS_ShortTermPlanner.py``: builds the snapshot, starts the read bridge and writes
``plugin_session.json`` in the per-user runtime folder.  It exists because Vulcan's script
runner cannot always pass command-line arguments or per-run environment variables.  See
docs/plugin_session.md.

``STP_PLUGIN_SESSION`` (a file name or path inside the runtime folder) still overrides the
default session file name.
"""
from __future__ import annotations

import importlib.util
import os
from pathlib import Path

_PLANNER_SCRIPT = Path(__file__).resolve().parent / "2. VGS_ShortTermPlanner.py"


def _load_planner():
    spec = importlib.util.spec_from_file_location("stp_v2_short_term_planner", _PLANNER_SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> Path:
    planner = _load_planner()
    requested = os.environ.get(planner.PLUGIN_SESSION_ENV, "").strip() or True
    return planner.run_planner(plugin_session=requested)


if __name__ == "__main__":
    main()
