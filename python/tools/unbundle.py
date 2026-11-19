"""Extract the ``stp_core`` modules and thin entry scripts from legacy bundles.

Usage (run with ``python -I``; nothing from the bundles is executed)::

    python -I python/tools/unbundle.py BUNDLE_DIR [--resources OUT_DIR] [--check]

``BUNDLE_DIR`` holds the self-contained ``1.``-``4.`` VGS scripts.  Embedded
module sources are parsed with ``ast`` and written byte for byte to
``python/stp_core/``; every bundle must carry identical copies.  The public
part of each script becomes a thin entry script in ``python/scripts/``.
``--resources`` also writes the embedded HTML/JS/JPEG resources to OUT_DIR.
``--check`` writes nothing and fails if the repository copies differ.
"""

from __future__ import annotations

import argparse
import ast
import base64
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import bundle_format as fmt  # noqa: E402

PYTHON_ROOT = HERE.parent
CORE_DIR = PYTHON_ROOT / "stp_core"
SCRIPTS_DIR = PYTHON_ROOT / "scripts"


def _literal_dicts(text: str) -> dict[str, object]:
    found: dict[str, object] = {}
    for node in ast.parse(text).body:
        if isinstance(node, ast.Assign) and isinstance(node.targets[0], ast.Name):
            name = node.targets[0].id
            if name in {"MODULE_SOURCES", "RESOURCE_SOURCES", "BINARY_RESOURCES", "BRIDGE_LAUNCHER_SOURCE"}:
                found[name] = ast.literal_eval(node.value)
    return found


def read_bundle(path: Path) -> dict[str, object]:
    raw = path.read_bytes()
    if b"\r" in raw:
        raise ValueError(f"{path.name}: expected LF line endings")
    text = raw.decode("utf-8")
    data = _literal_dicts(text)
    start = text.index(fmt.PUBLIC_BEGIN) + len(fmt.PUBLIC_BEGIN)
    end = text.rindex(fmt.PUBLIC_END)
    data["PUBLIC"] = text[start:end]
    data["TRAILER"] = text[end + len(fmt.PUBLIC_END):]
    return data


def thin_script(public: str) -> str:
    if public.count(fmt.BUNDLE_BOOTSTRAP) != 1:
        raise ValueError("public script does not contain the standard bootstrap exactly once")
    needs_runtime = "materialize_runtime_resources(" in public
    thin = public.replace(fmt.BUNDLE_BOOTSTRAP, fmt.thin_bootstrap(needs_runtime))
    docstring = ast.parse(thin).body[0]
    if not (isinstance(docstring, ast.Expr) and isinstance(docstring.value, ast.Constant)):
        raise ValueError("public script must start with a docstring")
    lines = thin.splitlines(keepends=True)
    cut = docstring.end_lineno
    return "".join(lines[:cut]) + fmt.THIN_FUTURE_LINE + "".join(lines[cut:])


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("bundle_dir", type=Path)
    parser.add_argument("--resources", type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)

    bundles = {name: read_bundle(args.bundle_dir / name) for name in fmt.SCRIPTS}
    reference = bundles[fmt.SCRIPTS[0]]
    for name, data in bundles.items():
        for key in ("MODULE_SOURCES", "RESOURCE_SOURCES", "BINARY_RESOURCES", "BRIDGE_LAUNCHER_SOURCE"):
            if data[key] != reference[key]:
                raise SystemExit(f"{name}: {key} differs from {fmt.SCRIPTS[0]}")
    modules = reference["MODULE_SOURCES"]
    if tuple(modules) != fmt.MODULE_NAMES:
        raise SystemExit(f"unexpected module set: {list(modules)}")

    outputs: dict[Path, bytes] = {}
    for name, source in modules.items():
        outputs[CORE_DIR / f"{name}.py"] = source.encode("utf-8")
    for name, data in bundles.items():
        outputs[SCRIPTS_DIR / name] = thin_script(data["PUBLIC"]).encode("utf-8")
    if args.resources:
        for name, value in reference["RESOURCE_SOURCES"].items():
            outputs[args.resources / name] = value.encode("utf-8")
        for name, value in reference["BINARY_RESOURCES"].items():
            outputs[args.resources / name] = base64.b85decode(value.encode("ascii"))

    mismatches = 0
    for target, content in outputs.items():
        if args.check:
            if not target.is_file() or target.read_bytes() != content:
                print(f"DIFFERS {target}")
                mismatches += 1
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
            print(f"wrote {target} ({len(content)} bytes)")
    if args.check:
        print("OK" if not mismatches else f"{mismatches} file(s) differ")
    return 1 if mismatches else 0


if __name__ == "__main__":
    raise SystemExit(main())
