"""Tests for ``stp_core.runtime``: the per-user runtime folder (KI-2) and the web resources.

    python -I python/tests/test_runtime.py
"""

from __future__ import annotations

import os
import re
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

PYTHON_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PYTHON_ROOT))

from stp_core import runtime  # noqa: E402

SCRIPT_2 = PYTHON_ROOT / "scripts" / "2. VGS_ShortTermPlanner.py"


class RuntimeRootTest(unittest.TestCase):
    def test_default_is_per_user_local_app_data(self) -> None:
        root = runtime.default_runtime_root({"LOCALAPPDATA": r"C:\Users\someone\AppData\Local"})
        self.assertEqual(root, Path(r"C:\Users\someone\AppData\Local") / "Maptek" / "StpUG" / "runtime")

    def test_environment_override(self) -> None:
        root = runtime.default_runtime_root({"STP_RUNTIME_ROOT": r"D:\elsewhere", "LOCALAPPDATA": r"C:\x"})
        self.assertEqual(root, Path(r"D:\elsewhere"))

    def test_without_local_app_data_uses_home(self) -> None:
        root = runtime.default_runtime_root({})
        self.assertEqual(root, Path.home() / "AppData" / "Local" / "Maptek" / "StpUG" / "runtime")

    def test_script_2_computes_the_same_rule_inline(self) -> None:
        # Script 2 cannot import default_runtime_root (single-file bundles); keep the two in step.
        source = SCRIPT_2.read_text(encoding="utf-8")
        self.assertIn('os.environ.get("STP_RUNTIME_ROOT")', source)
        self.assertIn('"Maptek" / "StpUG" / "runtime"', source)
        self.assertNotRegex(source, re.compile(r"RUNTIME_ROOT\s*=\s*SCRIPT_DIR"))


class ResourcesTest(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self._temporary.cleanup)
        self.target = Path(self._temporary.name) / "runtime"

    def _materialize(self, source: Path) -> Path:
        with mock.patch.dict(os.environ, {"STP_WEB_RESOURCES": str(source)}):
            return runtime.materialize_runtime_resources(self.target)

    def test_reference_web_names_without_logo(self) -> None:
        # The real checkout files (reference/web names, no logo: the repository is public).
        root = self._materialize(runtime.CHECKOUT_RESOURCE_DIR)
        for name in runtime.RESOURCE_SOURCES:
            self.assertTrue((root / name).is_file(), name)
        self.assertFalse((root / runtime.LOGO_NAME).exists())
        self.assertTrue((root / "stp_live_refresh_server_v2.py").is_file())
        template = (root / runtime.TEMPLATE_NAME).read_text(encoding="utf-8")
        self.assertIn("__VGS_STP_V2_SNAPSHOT__", template)

    def test_bundle_names_and_logo(self) -> None:
        source = Path(self._temporary.name) / "web"
        (source / "assets").mkdir(parents=True)
        for name in runtime.RESOURCE_SOURCES:
            (source / name).write_text(f"// {name}\n", encoding="utf-8")
        (source / runtime.LOGO_NAME).write_bytes(b"jpeg")
        root = self._materialize(source)
        self.assertEqual((root / "stp_v2_planner_logic.js").read_text(encoding="utf-8"), "// stp_v2_planner_logic.js\n")
        self.assertEqual((root / runtime.LOGO_NAME).read_bytes(), b"jpeg")

    def test_missing_resource_is_reported(self) -> None:
        source = Path(self._temporary.name) / "empty"
        source.mkdir()
        with self.assertRaises(FileNotFoundError) as caught:
            self._materialize(source)
        self.assertIn("stp_v2_planner_logic.js", str(caught.exception))

    def test_installed_layout_is_preferred(self) -> None:
        installed = Path(self._temporary.name) / "web"
        installed.mkdir()
        (installed / runtime.TEMPLATE_NAME).write_text("installed", encoding="utf-8")
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("STP_WEB_RESOURCES", None)
            with mock.patch.object(runtime, "INSTALLED_RESOURCE_DIR", installed):
                self.assertEqual(runtime.resource_dir(), installed)
            with mock.patch.object(runtime, "INSTALLED_RESOURCE_DIR", Path(self._temporary.name) / "none"):
                self.assertEqual(runtime.resource_dir(), runtime.CHECKOUT_RESOURCE_DIR)


if __name__ == "__main__":
    unittest.main(verbosity=2)
