"""Import smoke test for ``stp_core`` outside Vulcan.

Run from anywhere (``-I`` keeps the user site and cwd off ``sys.path``)::

    python -I python/tests/smoke_test.py

Checks, without touching any VGS project:

1. every ``stp_v2_*`` module imports by its flat name and via ``stp_core.``
   (and both spellings give the same module object);
2. ``maptek``/``vulcan_gui`` are replaced by stub modules, so the thin entry
   scripts 1, 3 and 4 import fully; scripts 2 and 2p are compiled only, because importing
   it copies the web resources into ``.stp_ug_v2_runtime``;
3. the generated bridge launcher really runs the bridge module as ``__main__``
   (``--help`` in a subprocess).
"""

from __future__ import annotations

import importlib
import importlib.util
import subprocess
import sys
import tempfile
import types
from pathlib import Path

PYTHON_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PYTHON_ROOT))


def _install_maptek_stub() -> None:
    maptek = types.ModuleType("maptek")
    maptek.__path__ = []  # mark as a package
    for name in ("gantt", "vulcan", "vulcan_gui"):
        sub = types.ModuleType(f"maptek.{name}")
        setattr(maptek, name, sub)
        sys.modules[f"maptek.{name}"] = sub
    sys.modules["maptek"] = maptek
    sys.modules["vulcan_gui"] = types.ModuleType("vulcan_gui")


def main() -> int:
    failures: list[str] = []
    import stp_core

    for name in stp_core.MODULE_NAMES:
        try:
            flat = importlib.import_module(name)
            dotted = importlib.import_module(f"stp_core.{name}")
            if flat is not dotted or getattr(stp_core, name) is not flat:
                raise AssertionError("stp_core alias returned a different module object")
            if Path(flat.__file__).resolve().parent != stp_core.CORE_DIR:
                raise AssertionError(f"imported from {flat.__file__}")
            print(f"ok      module  {name}")
        except Exception as error:  # noqa: BLE001
            failures.append(name)
            print(f"FAILED  module  {name}: {type(error).__name__}: {error}")
    if "maptek" in sys.modules:
        failures.append("maptek imported by a core module")
        print("FAILED  a core module imported maptek at import time")

    import stp_core.runtime as runtime

    _install_maptek_stub()
    for script in sorted((PYTHON_ROOT / "scripts").glob("*.py")):
        try:
            if script.name.startswith("2"):  # 2 and 2p copy web resources on import
                compile(script.read_bytes(), str(script), "exec")
                print(f"ok      compile {script.name}")
                continue
            spec = importlib.util.spec_from_file_location(f"stp_entry_{script.name[0]}", script)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            if not callable(getattr(module, "main", None)):
                raise AssertionError("no main()")
            print(f"ok      import  {script.name}")
        except Exception as error:  # noqa: BLE001
            failures.append(script.name)
            print(f"FAILED  script  {script.name}: {type(error).__name__}: {error}")

    with tempfile.TemporaryDirectory() as temporary:
        launcher = Path(temporary) / "stp_live_refresh_server_v2.py"
        launcher.write_text(runtime.bridge_launcher_source(), encoding="utf-8", newline="\n")
        completed = subprocess.run(
            [sys.executable, "-I", str(launcher), "--help"],
            cwd=temporary, capture_output=True, text=True, timeout=60,
        )
        if completed.returncode == 0 and "--config" in completed.stdout:
            print("ok      launcher runs stp_live_refresh_server_v2 as __main__")
        else:
            failures.append("launcher")
            print(f"FAILED  launcher rc={completed.returncode}\n{completed.stdout}{completed.stderr}")

    print(f"\n{'PASS' if not failures else 'FAIL'}: {len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
