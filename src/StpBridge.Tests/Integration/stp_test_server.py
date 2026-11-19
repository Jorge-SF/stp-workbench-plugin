"""Start the real STP V2 bridge on a synthetic project, for the C# integration tests.

Usage::

    python -I stp_test_server.py --python-root <repo>/python --workdir <empty temp dir>

Writes ``<workdir>/Synthetic Ñandú.vgantt`` (an invented two-activity project, no Vulcan
needed) and its ``snapshot_v2.json``, starts ``stp_live_refresh_server_v2`` on an
ephemeral 127.0.0.1 port, prints ONE JSON line to stdout and serves until stdin closes:

    {"port": ..., "token": ..., "projectKey": ..., "projectFingerprint": ...,
     "precedenceFingerprint": ..., "projectPath": ..., "snapshotPath": ..., "xlsxwriter": bool}

All project data is synthetic.  Nothing here imports ``maptek``.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import secrets
import sys
import threading
import zipfile
from datetime import datetime
from pathlib import Path
from xml.sax.saxutils import quoteattr

_EPOCH = datetime(1, 1, 1)


def _ticks(value: datetime) -> str:
    delta = value - _EPOCH
    return str((delta.days * 86_400 + delta.seconds) * 10_000_000 + delta.microseconds * 10)


def _instances(items: list[tuple[str, list[tuple[str, str]], list[str] | None]]) -> bytes:
    lines = ["<?xml version=\"1.0\" encoding=\"utf-8\"?>", "<instances>"]
    for key, fields, bar_items in items:
        lines.append(f"  <instance key={quoteattr(key)}>")
        for name, value in fields:
            if name == "m_Bars" and bar_items is not None:
                lines.append(f"    <field name=\"m_Bars\">")
                lines.extend(f"      <item value={quoteattr(item)} />" for item in bar_items)
                lines.append("    </field>")
            else:
                lines.append(f"    <field name={quoteattr(name)} value={quoteattr(value)} />")
        lines.append("  </instance>")
    lines.append("</instances>")
    return "\n".join(lines).encode("utf-8")


def write_synthetic_project(path: Path) -> None:
    """A100 (2 days) --FinishStart--> B200 (1 day, right after A100)."""
    bars = [
        ("bA", [("m_Key", "1"), ("m_StartTime", _ticks(datetime(2026, 3, 2, 8))),
                ("m_FinishTime", _ticks(datetime(2026, 3, 4, 8))), ("m_QuantityPercentage", "100"),
                ("m_PercentComplete", "0"), ("m_CompleteWorkedMinutes", "0")], None),
        ("bB", [("m_Key", "1"), ("m_StartTime", _ticks(datetime(2026, 3, 4, 8))),
                ("m_FinishTime", _ticks(datetime(2026, 3, 5, 8))), ("m_QuantityPercentage", "100"),
                ("m_PercentComplete", "0"), ("m_CompleteWorkedMinutes", "0")], None),
    ]
    collections = [
        ("cA", [("m_Bars", ""), ("m_PercentComplete", "0")], ["bA"]),
        ("cB", [("m_Bars", ""), ("m_PercentComplete", "0")], ["bB"]),
    ]
    activities = [
        ("aA", [("m_Id", "A100"), ("m_Bars", "cA")], None),
        ("aB", [("m_Id", "B200"), ("m_Bars", "cB")], None),
    ]
    links = [
        ("l1", [("m_Key", "L1"), ("m_Predecessor", "aA"), ("m_Successor", "aB"),
                ("m_PredecessorBar", "1"), ("m_SuccessorBar", "1"),
                ("m_PrecedenceType", "FinishStart"), ("m_Lag", "0"),
                ("m_PrecedenceGroup", "G1")], None),
    ]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("AutoSaved/Activity", _instances(activities))
        archive.writestr("AutoSaved/ActivityBarCollection", _instances(collections))
        archive.writestr("AutoSaved/ActivityBar", _instances(bars))
        archive.writestr("AutoSaved/ActivityLink", _instances(links))
        archive.writestr("ManualSaved/ProjectDateTimeFormat", "dd/MM/yyyy HH:mm")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--python-root", required=True, type=Path)
    parser.add_argument("--workdir", required=True, type=Path)
    arguments = parser.parse_args(argv)
    sys.path.insert(0, str(arguments.python_root.resolve()))
    import stp_core  # noqa: F401  (puts the flat stp_v2_* modules on sys.path)
    import stp_live_refresh_server_v2 as server_module
    from stp_v2_core import read_vgantt
    from stp_v2_snapshot_contract import build_canonical_snapshot

    workdir = arguments.workdir.resolve()
    project = workdir / "Synthetic Ñandú.vgantt"
    write_synthetic_project(project)
    state = read_vgantt(project)
    grid = {
        "A100": {"ID": "A100", "Nombre": "Galería norte ✓", "Tipo": "Desarrollo"},
        "B200": {"ID": "B200", "Nombre": "Rampa 𝄞 sur", "Tipo": "Producción"},
    }
    snapshot = build_canonical_snapshot(state, project, grid, exported_at=datetime(2026, 3, 1, 9, 30))
    snapshot_path = workdir / "snapshot_v2.json"
    snapshot_path.write_text(json.dumps(snapshot, ensure_ascii=False), encoding="utf-8")

    token = secrets.token_urlsafe(24)
    config = {
        "projectPath": str(project),
        "projectKey": snapshot["projectKey"],
        "projectFingerprint": snapshot["projectFingerprint"],
        "token": token,
        "port": 0,
        "snapshotPath": str(snapshot_path),
        "bind": "127.0.0.1",
    }
    server = server_module.create_server(config)
    info = {
        "port": server.server_port,
        "token": token,
        "projectKey": snapshot["projectKey"],
        "projectFingerprint": snapshot["projectFingerprint"],
        "precedenceFingerprint": snapshot["precedenceFingerprint"],
        "projectPath": str(project),
        "snapshotPath": str(snapshot_path),
        "xlsxwriter": importlib.util.find_spec("xlsxwriter") is not None,
    }
    sys.stdout.write(json.dumps(info, ensure_ascii=True) + "\n")
    sys.stdout.flush()

    def _watch_stdin() -> None:
        try:
            sys.stdin.read()
        finally:
            server.shutdown()

    threading.Thread(target=_watch_stdin, daemon=True).start()
    try:
        server.serve_forever()
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
