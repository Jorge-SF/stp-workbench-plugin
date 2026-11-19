"""STP UG V2 Python core, extracted from the self-contained VGS scripts.

The ``stp_v2_*`` modules in this folder are byte-for-byte copies of the
sources that used to be embedded in the bundled scripts.  They import each
other by flat top-level names (``import stp_v2_core``), exactly as they did
inside the bundle, so importing this package puts its own folder on
``sys.path``.

Both spellings then refer to the *same* module object::

    import stp_v2_core
    from stp_core import stp_v2_core      # same object
    import stp_core.stp_v2_core           # same object

Do not edit the ``stp_v2_*`` files casually: they are the source of truth for
``python/tools/build_bundle.py`` and must stay valid as embedded literals.
"""

from __future__ import annotations

import importlib
import importlib.abc
import importlib.util
import sys
from pathlib import Path

__all__ = ["MODULE_NAMES", "CORE_DIR"]

CORE_DIR = Path(__file__).resolve().parent

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

if str(CORE_DIR) not in sys.path:
    sys.path.insert(0, str(CORE_DIR))


class _AliasLoader(importlib.abc.Loader):
    """Make ``stp_core.<name>`` resolve to the flat module ``<name>``."""

    def __init__(self, flat_name: str) -> None:
        self.flat_name = flat_name

    def create_module(self, spec):
        return importlib.import_module(self.flat_name)

    def exec_module(self, module) -> None:
        return None


class _AliasFinder(importlib.abc.MetaPathFinder):
    _PREFIX = __name__ + "."

    def find_spec(self, fullname, path=None, target=None):
        if not fullname.startswith(self._PREFIX):
            return None
        flat_name = fullname[len(self._PREFIX):]
        if flat_name not in MODULE_NAMES:
            return None
        return importlib.util.spec_from_loader(fullname, _AliasLoader(flat_name))


if not any(isinstance(finder, _AliasFinder) for finder in sys.meta_path):
    sys.meta_path.insert(0, _AliasFinder())


def __getattr__(name: str):
    if name in MODULE_NAMES:
        return importlib.import_module(name)
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")
