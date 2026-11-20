"""The bridge server accepts the snapshot where Script 2 writes it.

Regression: Script 2 moved its snapshot to the per-user runtime folder (KI-2), but the
server still required it inside the project folder, so every bridge exited at start-up
and the planner always opened in static mode.

    python -I python/tests/test_bridge_config.py
"""

from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

PYTHON_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PYTHON_ROOT))

import stp_core  # noqa: E402,F401
import stp_live_refresh_server_v2 as server  # noqa: E402


class BridgeConfigTest(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        root = Path(self._temporary.name).resolve()
        self.project_dir = root / "project"
        self.runtime = root / "user-runtime"
        self.elsewhere = root / "elsewhere"
        for folder in (self.project_dir, self.runtime / "short_term_planner_v2", self.elsewhere):
            folder.mkdir(parents=True)
        self.project = self.project_dir / "Example.vgantt"
        self.project.write_bytes(b"")
        environment = mock.patch.dict(os.environ, {"STP_RUNTIME_ROOT": str(self.runtime)})
        environment.start()
        self.addCleanup(environment.stop)
        self.addCleanup(self._temporary.cleanup)

    def _raw(self, snapshot: Path) -> dict:
        snapshot.write_text("{}", encoding="utf-8")
        return {
            "projectPath": str(self.project),
            "projectKey": "a" * 64,
            "projectFingerprint": "c" * 64,
            "token": "t" * 43,
            "port": 0,
            "snapshotPath": str(snapshot),
            "bind": "127.0.0.1",
        }

    def test_snapshot_in_per_user_runtime_folder(self) -> None:
        snapshot = self.runtime / "short_term_planner_v2" / "snapshot_v2.json"
        config = server._config(self._raw(snapshot))
        self.assertEqual(config.snapshot_path, snapshot.resolve())

    def test_snapshot_in_project_folder(self) -> None:
        config = server._config(self._raw(self.project_dir / "snapshot_v2.json"))
        self.assertEqual(config.project_path, self.project.resolve())

    def test_snapshot_elsewhere_rejected(self) -> None:
        with self.assertRaises(server.BridgeError):
            server._config(self._raw(self.elsewhere / "snapshot_v2.json"))

    def test_start_up_failure_leaves_error_file(self) -> None:
        ready = self.elsewhere / "ready-0123.json"
        self.assertEqual(
            server.main(["--config", str(self.elsewhere / "missing.json"), "--ready", str(ready)]), 1
        )
        error = self.elsewhere / "error-0123.txt"
        self.assertTrue(error.is_file())
        self.assertIn("Traceback", error.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
