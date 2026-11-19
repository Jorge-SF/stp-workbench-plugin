# STP Python core

The Python half of STP UG V2, extracted from the self-contained VGS scripts
`1. VGS_PrepareProjectForSTP.py` … `4. VGS_VerifySTPSave.py` (Phase 0a).

```
python/
  stp_core/            the 10 stp_v2_* modules, byte-for-byte as embedded in scripts 1-4
    __init__.py        puts this folder on sys.path so the modules' flat imports work
    runtime.py         on-disk replacement for the bundle's materialize_runtime_resources
  scripts/             thin VGS entry scripts 1-4 (same prompts and behaviour as before)
  tools/
    unbundle.py        legacy bundles -> stp_core + scripts (or --check against them)
    build_bundle.py    stp_core + scripts + web resources -> legacy single-file bundles
    bundle_format.py   the shared bundle layout
  tests/smoke_test.py  import test outside Vulcan (maptek is stubbed)
```

## Importing

The modules still import each other by flat names (`import stp_v2_core`), exactly as they
did inside the bundles. Import the package first, and then either spelling gives the same
module object:

```python
import stp_core
import stp_v2_core                  # or: from stp_core import stp_v2_core
```

## Entry scripts

Each script in `scripts/` is the public part of the original bundle. The only change is the
marked `STP CORE BOOTSTRAP` block, which finds `stp_core` in the script's parent folder
(override with `STP_CORE_PATH`) instead of activating the embedded importer. `SCRIPT_DIR` is
still the script's own folder. Script 1 writes its work files there, and script 2 writes
`.stp_ug_v2_runtime/` there, as before.

Script 2 also needs the planner web resources. `stp_core.runtime` copies them from
`STP_WEB_RESOURCES`, or by default from `<repo>/reference/web/`, and writes a small bridge
launcher that runs `stp_live_refresh_server_v2` from this package.

## Single-file bundles

The old self-contained scripts can still be produced:

```
python -I python/tools/build_bundle.py --resources <web dir> --out <dir> [--compare <dir with original bundles>]
```

In Phase 0a, `--compare` against the original scripts 1–4 reported **IDENTICAL** for all
four. The extraction is lossless. Keep `stp_core/*.py` and `scripts/*.py` with LF line
endings (`.gitattributes` enforces this), because CRLF would change the embedded bytes.

## Smoke test

```
python -I python/tests/smoke_test.py
```

The script-0 differences are in `docs/drift_script0.md`.
