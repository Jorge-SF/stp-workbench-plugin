"""Cross-check the golden apply packages against the Python canonical form.

For every expected/*.apply.canonical.json this re-serialises the package with
json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False) — the
form used by the Python core for `vgs-stp-apply/1` — and checks that the bytes
and the SHA-256 recorded in the matching expected/<case>.json are identical.

Usage: python tools/golden/verify_canonical.py
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

EXPECTED = Path(__file__).resolve().parent / "expected"


def main() -> int:
    files = sorted(EXPECTED.glob("*.apply.canonical.json"))
    if not files:
        print(f"no canonical packages under {EXPECTED}", file=sys.stderr)
        return 1
    failures = 0
    for path in files:
        raw = path.read_bytes()
        package = json.loads(raw)
        canonical = json.dumps(package, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        result = json.loads(path.with_name(path.name.replace(".apply.canonical.json", ".json")).read_text("utf-8"))
        recorded = result["applyPackage"]["sha256"]
        digest = hashlib.sha256(canonical).hexdigest()
        if canonical != raw or digest != recorded or package.get("schema") != "vgs-stp-apply/1":
            failures += 1
            print(f"MISMATCH {path.name}: python={digest} recorded={recorded}")
    print(f"{len(files) - failures}/{len(files)} apply packages match the Python canonical form")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
