# Script 0 drift report

`0. Diagnostico_Puente_STP.py` carries its own copy of the STP core as a zlib/base85
`_BUNDLE_PAYLOADS` blob. Scripts 1–4 embed the same files as plain `MODULE_SOURCES` /
`RESOURCE_SOURCES` literals. All four of scripts 1–4 hold byte-identical copies, and those
copies are now `python/stp_core/` (modules) and the sibling `reference/web/` extraction
(resources).

This report compares the script-0 payloads with that 1–4 copy. **No script-0 change has been
merged.** Choosing a version is the user's decision.

## Summary

| Item | Changed lines (0 vs 1–4) | What script 0 has | Recommendation |
|---|---|---|---|
| `stp_live_refresh_server_v2` | 1 | A required idle timeout | **Keep 1–4** |
| `stp_v2_bridge_bootstrap` | ~30 | A fixed 900 s idle timeout and a stricter runtime schema | **Keep 1–4** (see note) |
| `stp_v2_apply_contract` | ~10 | Midnight-only civil-day boundary | **Keep 1–4** |
| `short_term_planner_template_v2.html` | ~70 | No "period total" labels | **Keep 1–4** |
| `stp_v2_v1_compat_logic.js` | ~45 | No `build*PeriodTotals` helpers | **Keep 1–4** |

Every other payload is byte-identical to 1–4: `stp_v2_apply_transaction`, `stp_v2_core`,
`stp_v2_excel_export`, `stp_v2_maptek_apply_adapter`, `stp_v2_prepare_project`,
`stp_v2_snapshot_contract`, `stp_v2_windows_atomic`, the other three JS files and the logo.

**Overall:** in each of the five cases, script 0 is the *older* snapshot. Scripts 1–4 add
features or relax a check in a backward-compatible way, and none of the diffs goes the other
direction. Script 0 is a read-only diagnostic of the bridge, so its embedded core should be
regenerated from `python/stp_core/`. Otherwise it diagnoses a bridge that production does not
run.

---

## 1. `stp_live_refresh_server_v2`

Script 0 indexes `idleTimeoutSeconds` directly. Scripts 1–4 treat it as optional.

```diff
-                runtime.get("idleTimeoutSeconds"),
+                runtime["idleTimeoutSeconds"],
```

**Keep 1–4.** This change only works together with the bootstrap change below. The 1–4 server
accepts runtimes written by either bootstrap. The script-0 server raises `KeyError` on a
runtime written by the 1–4 bootstrap.

## 2. `stp_v2_bridge_bootstrap`

Script 0 always writes `idleTimeoutSeconds: 900` and requires it, within the range
60–3600 s. Scripts 1–4 make it optional, with a default of `None`, so the bridge is never
stopped for inactivity. When the value is present, 1–4 accept up to 4 560 s
(`MAX_LEGACY_IDLE_TIMEOUT`) and still read runtimes in the old format.

```diff
-DEFAULT_IDLE_TIMEOUT: int | None = None
-MAX_LEGACY_IDLE_TIMEOUT = 4_560
+DEFAULT_IDLE_TIMEOUT = 900
 ...
-        if idle_timeout is not None and now() - server.last_authenticated_at >= idle_timeout:
+        if now() - server.last_authenticated_at >= idle_timeout:
 ...
-    required_keys = {"schema", "bridgeId", "leasePath", "bridgeConfig"}
-    allowed_keys = required_keys | {"idleTimeoutSeconds"}
-    if not isinstance(value, dict) or not required_keys <= set(value) <= allowed_keys:
+    if not isinstance(value, dict) or set(value) != {
+        "schema", "bridgeId", "idleTimeoutSeconds", "leasePath", "bridgeConfig",
+    }:
 ...
+            "idleTimeoutSeconds": DEFAULT_IDLE_TIMEOUT,
```

**Keep 1–4.** It is the newer version and it is backward compatible.

**Note for the plugin design:** with 1–4, the bridge lives until its lease is replaced. No idle
timeout stops it. This matters for the Workbench plugin, which may want the bridge to shut
down on its own once the view closes. If so, set that timeout explicitly in the plugin
flow. Do not restore the fixed 900 s value from script 0.

## 3. `stp_v2_apply_contract`

This is the check that lets STP leave a pause at a native join on a civil-day boundary.
Script 0 recognises only an exact `00:00:00` join. Scripts 1–4 also accept the inclusive
`23:59:59` end-of-day marker that VGS sometimes writes.

```diff
 def _is_civil_midnight_boundary(left: NativeBar, right: NativeBar) -> bool:
-    """Return whether a preserved native join is a VGS civil-day boundary."""
+    """Return whether a preserved native join occurs exactly at midnight."""
     return (
         left.finish == right.start
-        and left.finish.time() in {time.min, time(23, 59, 59)}
+        and left.finish.time() == time.min
     )
```

**Keep 1–4.** It fixes a false rejection in apply-package validation for projects whose bars
end at 23:59:59. Script 0 never applies packages, so its copy only matters if it is used to
validate one.

## 4. `short_term_planner_template_v2.html`

Scripts 1–4 add **period totals**: a "Plan: x · Real: y" label on each activity row, and
"Plan / Meta" and "Real / Plan" labels on the capacity and actual-progress aggregate rows.
Each total is limited to the visible period, and the labels appear on screen and in the
print report. Script 0 has none of this: no CSS classes, no `format*PeriodTotal` functions
and no `periodTotalText` parameters.

```diff
-      const periodTotal = document.createElement('span');
-      periodTotal.className = 'activity-period-total';
-      periodTotal.setAttribute('data-activity-period-total', 'true');
-      periodTotal.textContent = formatActivityPeriodTotal(row);
-      periodTotal.title = 'Total limitado al período visible';
-      primary.append(activityId, name, periodTotal);
+      primary.append(activityId, name);
```

**Keep 1–4.** It is a feature that script 0 lacks, and the C# port should reproduce it. The
script-0 template must stay paired with the script-0 compat JS. Mixing the 1–4 template with
the script-0 JS fails when the import is destructured.

## 5. `stp_v2_v1_compat_logic.js`

This is the JS side of item 4. Scripts 1–4 export `buildActivityPeriodTotals`,
`buildCapacityPeriodTotals` and `buildActualProgressPeriodTotals`, plus the private helpers
`periodDateList` and `sumPeriodFields`. Script 0 removes all of these. Nothing else differs.

```diff
-export function buildActivityPeriodTotals(row, state = {}, showActualProgress = false) {
-  const activityId = safeText(row?.activityId ?? row?.id, 240);
-  let scheduled = 0;
-  let actual = 0;
 ...
-export function buildCapacityPeriodTotals(row, days) {
-  return sumPeriodFields(row, days, ['scheduled', 'objective']);
-}
```

**Keep 1–4.**

---

## Suggested follow-up (not done here)

Regenerate script 0's `_BUNDLE_PAYLOADS` (and its `BUNDLE_MANIFEST` hashes) from
`python/stp_core/` plus the 1–4 resources, or turn script 0 into a thin entry script like
1–4. Its own diagnostic code, after `# --- Diagnóstico STP autónomo de solo lectura ---`,
is independent of these diffs. It does not depend on `idleTimeoutSeconds` being present.

*How this was produced:* the script-0 payloads were decoded with
`zlib.decompress(base64.b85decode(...))` and compared with `diff -u` against the extracted
1–4 sources. In the excerpts, `-` is 1–4 and `+` is script 0.
