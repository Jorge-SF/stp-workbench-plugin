"""Rebuild the legacy self-contained VGS scripts from ``stp_core``.

Usage::

    python -I python/tools/build_bundle.py --resources WEB_DIR --out OUT_DIR [--compare BUNDLE_DIR]

Embeds ``python/stp_core/stp_v2_*.py``, the web resources from WEB_DIR and
the public part of each thin entry script in ``python/scripts/`` into the
original single-file layout.  ``--compare`` checks the result byte for byte
against existing bundles (used to prove the extraction is lossless).
"""

from __future__ import annotations

import argparse
import base64
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import bundle_format as fmt  # noqa: E402

PYTHON_ROOT = HERE.parent
CORE_DIR = PYTHON_ROOT / "stp_core"
SCRIPTS_DIR = PYTHON_ROOT / "scripts"


def _read_text(path: Path) -> str:
    raw = path.read_bytes()
    if b"\r" in raw:
        raise ValueError(f"{path}: CRLF line endings would change the embedded source")
    return raw.decode("utf-8")


def public_from_thin(thin: str) -> str:
    if thin.count(fmt.THIN_FUTURE_LINE) != 1:
        raise ValueError("thin script must contain the marked __future__ import exactly once")
    thin = thin.replace(fmt.THIN_FUTURE_LINE, "", 1)
    start = thin.index(fmt.THIN_BOOTSTRAP_BEGIN)
    end = thin.index(fmt.THIN_BOOTSTRAP_END, start) + len(fmt.THIN_BOOTSTRAP_END)
    return thin[:start] + fmt.BUNDLE_BOOTSTRAP + thin[end:]


def build(resource_dir: Path) -> dict[str, str]:
    modules = {name: _read_text(CORE_DIR / f"{name}.py") for name in fmt.MODULE_NAMES}
    resources = {name: _read_text(resource_dir / name) for name in fmt.TEXT_RESOURCE_NAMES}
    binaries = {
        name: base64.b85encode((resource_dir / name).read_bytes()).decode("ascii")
        for name in fmt.BINARY_RESOURCE_NAMES
    }
    module_literal = fmt.module_dict_literal("MODULE_SOURCES", modules)
    launcher = fmt.LAUNCHER_HEADER + module_literal + fmt.LAUNCHER_FOOTER
    prelude = (
        fmt.BUNDLE_HEADER
        + module_literal
        + fmt.RESOURCE_HEADER
        + fmt.module_dict_literal("RESOURCE_SOURCES", resources)
        + "\n"
        + f"BINARY_RESOURCES = {binaries!r}\n"
        + "\n"
        + f"BRIDGE_LAUNCHER_SOURCE = {launcher!r}\n"
        + fmt.LOADER_PRELUDE
    )
    bundles = {}
    for name in fmt.SCRIPTS:
        public = public_from_thin(_read_text(SCRIPTS_DIR / name))
        bundles[name] = prelude + fmt.PUBLIC_BEGIN + public + fmt.PUBLIC_END + "\n"
    return bundles


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--resources", type=Path, required=True)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--compare", type=Path)
    args = parser.parse_args(argv)
    bundles = build(args.resources)
    failures = 0
    for name, text in bundles.items():
        data = text.encode("utf-8")
        if args.out:
            args.out.mkdir(parents=True, exist_ok=True)
            (args.out / name).write_bytes(data)
        if args.compare:
            original = (args.compare / name).read_bytes()
            same = original == data
            failures += not same
            print(f"{'IDENTICAL' if same else 'DIFFERS  '} {name} ({len(data)} vs {len(original)} bytes)")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
