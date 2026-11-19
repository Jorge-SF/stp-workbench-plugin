"""Unit tests for script 2's plugin mode (``plugin_session.json``) and its unchanged browser mode.

Run outside Vulcan (``maptek`` is stubbed, VGS and the bridge process are mocked)::

    python -I python/tests/test_plugin_mode.py

Script 2 and 2p are copied into a temporary folder, and ``STP_RUNTIME_ROOT`` points their
runtime folder there too.  The web resources are dummy files: the template only carries the placeholders.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import re
import shutil
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

PYTHON_ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = PYTHON_ROOT / "scripts"
SCRIPT_2 = "2. VGS_ShortTermPlanner.py"
SCRIPT_2P = "2p. VGS_ShortTermPlanner_Plugin.py"
sys.path.insert(0, str(PYTHON_ROOT))

import stp_core  # noqa: E402,F401
from stp_v2_bridge_bootstrap import BridgeDescriptor  # noqa: E402

TEMPLATE = """<!doctype html><html><body>
<script>window.SNAPSHOT=__VGS_STP_V2_SNAPSHOT__;</script>
<script>window.BRIDGE=__VGS_STP_V2_BRIDGE__;</script>
<script>__VGS_STP_V2_LOGIC__</script>
<script>__VGS_STP_V2_TIMELINE_LOGIC__</script>
<script>__VGS_STP_V2_REFRESH_CLIENT__</script>
<script>__VGS_STP_V2_COMPAT_LOGIC__</script>
</body></html>
"""
TOKEN = "t" * 43
PROJECT_KEY = "a" * 64
BRIDGE_ID = "b" * 32


class _FakeGantt:
    def __init__(self, project_file: Path) -> None:
        self._project_file = project_file

    def project_filename(self, _full: bool) -> str:
        return str(self._project_file)


def _install_maptek_stub(project_file: Path) -> None:
    maptek = types.ModuleType("maptek")
    maptek.__path__ = []
    gantt = types.ModuleType("maptek.gantt")
    gantt.Gantt = lambda: _FakeGantt(project_file)
    maptek.gantt = gantt
    sys.modules["maptek"] = maptek
    sys.modules["maptek.gantt"] = gantt
    sys.modules["vulcan_gui"] = types.ModuleType("vulcan_gui")


def _load(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class PlannerModesTest(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        root = Path(self._temporary.name)
        self.script_dir = root / "scripts"
        self.script_dir.mkdir()
        for name in (SCRIPT_2, SCRIPT_2P):
            shutil.copyfile(SCRIPTS / name, self.script_dir / name)
        resources = root / "web"
        (resources / "assets").mkdir(parents=True)
        (resources / "short_term_planner_template_v2.html").write_text(TEMPLATE, encoding="utf-8")
        for name in (
            "stp_v2_planner_logic.js",
            "stp_v2_refresh_client.js",
            "stp_v2_timeline_logic.js",
            "stp_v2_v1_compat_logic.js",
        ):
            (resources / name).write_text(f"// {name}\n", encoding="utf-8")
        (resources / "assets" / "Maptek_highres.jpg").write_bytes(b"not a real image")
        self.project_file = root / "Example.vgantt"
        self.project_file.write_bytes(b"")
        self.runtime = root / "user-runtime"
        environment = {
            "STP_CORE_PATH": str(PYTHON_ROOT),
            "STP_WEB_RESOURCES": str(resources),
            "STP_RUNTIME_ROOT": str(self.runtime),
        }
        self._environment = mock.patch.dict(os.environ, environment)
        self._environment.start()
        for name in ("STP_PLUGIN_MODE", "STP_PLUGIN_SESSION"):
            os.environ.pop(name, None)
        self._saved_modules = {
            name: sys.modules.get(name) for name in ("maptek", "maptek.gantt", "vulcan_gui")
        }
        _install_maptek_stub(self.project_file)
        self.planner = _load(self.script_dir / SCRIPT_2, "stp_test_script_2")
        self.snapshot = {
            "schema": "vgs-stp-snapshot/2",
            "projectKey": PROJECT_KEY,
            "projectFingerprint": "c" * 64,
            "projectPath": str(self.project_file.resolve()),
            "exportedAt": "2026-11-19T08:00:00",
            "note": "sintético <&> ñ",
        }
        self.bridge = BridgeDescriptor(
            "live", "http://127.0.0.1:50123", TOKEN, PROJECT_KEY, BRIDGE_ID,
            "Actualización en vivo disponible.",
        )
        self.launch = mock.Mock(return_value=self.bridge)
        self.open_planner = mock.Mock(return_value=(True, "abierto"))
        self.report = mock.Mock()
        for name, value in (
            ("build_snapshot", mock.Mock(return_value=self.snapshot)),
            ("launch_read_bridge", self.launch),
            ("open_planner", self.open_planner),
            ("_report", self.report),
        ):
            patcher = mock.patch.object(self.planner, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def tearDown(self) -> None:
        for name, module in self._saved_modules.items():
            if module is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = module
        self._environment.stop()
        self._temporary.cleanup()

    # -- helpers -----------------------------------------------------------------

    def _run(self, **kwargs):
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            result = self.planner.run_planner(**kwargs)
        return result, stdout.getvalue()

    def _session_path(self) -> Path:
        return self.runtime / "plugin_session.json"

    def _browser_bridge_config(self) -> dict:
        html = (self.runtime / "short_term_planner_v2" / "index.html").read_text(encoding="utf-8")
        match = re.search(r"window\.BRIDGE=(\{.*?\});</script>", html)
        self.assertIsNotNone(match)
        return json.loads(match.group(1))

    # -- plugin mode ---------------------------------------------------------------

    def test_plugin_mode_writes_session_and_skips_html(self) -> None:
        result, stdout = self._run(plugin_session=True)
        session_path = self._session_path()
        self.assertEqual(result, session_path.resolve())
        self.assertIn(f"STP_PLUGIN_SESSION={session_path.resolve()}", stdout)
        raw = session_path.read_bytes()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), "session must be UTF-8 without BOM")
        session = json.loads(raw.decode("utf-8"))
        self.assertEqual(set(session), {"snapshot_path", "bridge_config"})
        snapshot_path = Path(session["snapshot_path"])
        self.assertTrue(snapshot_path.is_absolute())
        self.assertEqual(
            snapshot_path, (self.runtime / "short_term_planner_v2" / "snapshot_v2.json").resolve()
        )
        self.assertEqual(json.loads(snapshot_path.read_text(encoding="utf-8")), self.snapshot)
        self.assertEqual(
            session["bridge_config"],
            {**self.bridge.page_config(), "precedenceConflictPolicy": "allow-inherited"},
        )
        self.assertFalse((self.runtime / "short_term_planner_v2" / "index.html").exists())
        self.open_planner.assert_not_called()
        self.report.assert_not_called()
        self.assertEqual(
            sorted(p.name for p in self.runtime.iterdir() if p.name.startswith(".plugin")), [],
            "no temporary or backup siblings may be left behind",
        )

    def test_plugin_mode_launches_bridge_like_browser_mode(self) -> None:
        self._run(plugin_session=True)
        plugin_call = self.launch.call_args
        self._run()
        self.assertEqual(self.launch.call_args, plugin_call)

    def test_bridge_config_identical_to_browser_page(self) -> None:
        for policy in ("allow-inherited", "block-inherited"):
            with self.subTest(policy=policy):
                self._run(precedence_conflict_policy=policy, plugin_session=True)
                session = json.loads(self._session_path().read_text(encoding="utf-8"))
                self._run(precedence_conflict_policy=policy)
                self.assertEqual(session["bridge_config"], self._browser_bridge_config())

    def test_static_bridge_is_reported_in_session(self) -> None:
        static = BridgeDescriptor("static", "", "", PROJECT_KEY, "", "estático")
        self.launch.return_value = static
        self._run(plugin_session=True)
        session = json.loads(self._session_path().read_text(encoding="utf-8"))
        self.assertEqual(session["bridge_config"]["mode"], "static")
        self.assertEqual(session["bridge_config"]["token"], "")

    def test_rerun_replaces_session(self) -> None:
        self._run(plugin_session=True)
        newer = BridgeDescriptor(
            "live", "http://127.0.0.1:50999", "n" * 43, PROJECT_KEY, "d" * 32, "ok"
        )
        self.launch.return_value = newer
        self._run(plugin_session=True)
        session = json.loads(self._session_path().read_text(encoding="utf-8"))
        self.assertEqual(session["bridge_config"]["bridgeId"], "d" * 32)

    def test_stale_session_removed_before_vgs_is_read(self) -> None:
        self.runtime.mkdir(exist_ok=True)
        self._session_path().write_text('{"stale":true}', encoding="utf-8")
        self.planner.build_snapshot.side_effect = ValueError("VGS did not export")
        with self.assertRaises(ValueError):
            self._run(plugin_session=True)
        self.assertFalse(self._session_path().exists())

    @unittest.skipUnless(os.name == "nt", "protected atomic replace is Windows-only")
    def test_session_written_with_windows_atomic_replace(self) -> None:
        atomic = self.planner.stp_v2_windows_atomic
        with mock.patch.object(
            atomic, "atomic_replace_bytes_windows", wraps=atomic.atomic_replace_bytes_windows
        ) as spy:
            self._run(plugin_session=True)
        spy.assert_called_once()
        target, payload = spy.call_args.args
        self.assertEqual(Path(target), self._session_path().resolve())
        self.assertEqual(spy.call_args.kwargs["expected_parent"], self.runtime.resolve())
        self.assertEqual(payload, self._session_path().read_bytes())

    def test_custom_session_name(self) -> None:
        result, _ = self._run(plugin_session="other_session.json")
        self.assertEqual(result, (self.runtime / "other_session.json").resolve())
        self.assertTrue(result.is_file())
        self.assertFalse(self._session_path().exists())

    # -- path validation -------------------------------------------------------------

    def test_session_path_must_stay_in_runtime_folder(self) -> None:
        outside = Path(self._temporary.name) / "plugin_session.json"
        for requested in (
            str(outside),
            "sub/plugin_session.json",
            "../plugin_session.json",
            "plugin_session.txt",
            ".hidden.json",
            42,
            False,
        ):
            with self.subTest(requested=requested):
                with self.assertRaises(ValueError):
                    self.planner.resolve_plugin_session_path(requested)
        with self.assertRaises(ValueError):
            self._run(plugin_session=str(outside))
        self.planner.build_snapshot.assert_not_called()
        self.assertFalse(outside.exists())

    def test_session_path_defaults(self) -> None:
        expected = self._session_path().resolve()
        self.assertEqual(self.planner.resolve_plugin_session_path(True), expected)
        self.assertEqual(self.planner.resolve_plugin_session_path(""), expected)
        self.assertEqual(self.planner.resolve_plugin_session_path(str(expected)), expected)

    # -- mode selection --------------------------------------------------------------

    def test_plugin_session_request(self) -> None:
        request = self.planner.plugin_session_request
        default = self._session_path().resolve()
        custom = (self.runtime / "x.json").resolve()
        script = str(self.script_dir / SCRIPT_2)
        cases = (
            ([script], {}, None),
            ([], {}, None),
            ([script, "--other"], {}, None),
            ([script, "--plugin-session"], {}, default),
            ([script, "--plugin-session", "--other"], {}, default),
            ([script, "--plugin-session", "x.json"], {}, custom),
            ([script, "--plugin-session=x.json"], {}, custom),
            ([script], {"STP_PLUGIN_MODE": "1"}, default),
            ([script], {"STP_PLUGIN_MODE": "TRUE"}, default),
            ([script], {"STP_PLUGIN_MODE": "0"}, None),
            ([script], {"STP_PLUGIN_MODE": ""}, None),
            ([script], {"STP_PLUGIN_SESSION": "x.json"}, custom),
            ([script], {"STP_PLUGIN_SESSION": "  "}, None),
            ([script, "--plugin-session"], {"STP_PLUGIN_SESSION": "x.json"}, default),
        )
        for argv, environ, expected in cases:
            with self.subTest(argv=argv[1:], environ=environ):
                self.assertEqual(request(argv, environ), expected)

    def test_request_without_sys_argv(self) -> None:
        with mock.patch.object(sys, "argv", None, create=True):
            self.assertIsNone(self.planner.plugin_session_request(environ={}))
        saved = sys.__dict__.pop("argv", None)
        try:
            self.assertIsNone(self.planner.plugin_session_request(environ={}))
        finally:
            if saved is not None:
                sys.argv = saved

    def test_main_passes_request_to_run_planner(self) -> None:
        with mock.patch.object(self.planner, "run_planner") as run, mock.patch.object(
            sys, "argv", ["script"]
        ):
            self.planner.main()
            run.assert_called_once_with(plugin_session=None)
            run.reset_mock()
            with mock.patch.dict(os.environ, {"STP_PLUGIN_MODE": "1"}):
                self.planner.main()
            run.assert_called_once_with(plugin_session=self._session_path().resolve())

    def test_entry_script_2p_selects_plugin_mode(self) -> None:
        entry = _load(self.script_dir / SCRIPT_2P, "stp_test_script_2p")
        self.assertEqual(entry._PLANNER_SCRIPT, (self.script_dir / SCRIPT_2).resolve())
        with mock.patch.object(entry, "_load_planner", return_value=self.planner):
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                result = entry.main()
            self.assertEqual(result, self._session_path().resolve())
            with mock.patch.dict(os.environ, {"STP_PLUGIN_SESSION": "x.json"}):
                with contextlib.redirect_stdout(stdout):
                    result = entry.main()
            self.assertEqual(result, (self.runtime / "x.json").resolve())
        self.open_planner.assert_not_called()

    # -- browser mode unchanged ------------------------------------------------------

    def test_browser_mode_unchanged(self) -> None:
        self.runtime.mkdir(exist_ok=True)
        self._session_path().write_text('{"previous":true}', encoding="utf-8")
        result, stdout = self._run()
        self.assertIsNone(result)
        self.assertNotIn("STP_PLUGIN_SESSION", stdout)
        index = (self.runtime / "short_term_planner_v2" / "index.html").resolve()
        self.assertTrue(index.is_file())
        self.open_planner.assert_called_once_with(index)
        self.report.assert_called_once()
        message = self.report.call_args.args[0]
        self.assertIn("Actualización en vivo disponible.", message)
        self.assertIn("abierto", message)
        self.assertEqual(
            self._browser_bridge_config(),
            {**self.bridge.page_config(), "precedenceConflictPolicy": "allow-inherited"},
        )
        self.assertEqual(
            self._session_path().read_text(encoding="utf-8"), '{"previous":true}',
            "browser mode must not touch the plugin session file",
        )
        html = index.read_text(encoding="utf-8")
        self.assertNotIn("__VGS_STP_V2_", html)
        self.assertIn("\\u003c\\u0026\\u003e", html, "snapshot is embedded with HTML-safe escaping")

    def test_render_planner_rejects_bad_policy(self) -> None:
        with self.assertRaises(ValueError):
            self.planner.planner_bridge_config({}, precedence_conflict_policy="other")
        with self.assertRaises(ValueError):
            self._run(precedence_conflict_policy="other", plugin_session=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
