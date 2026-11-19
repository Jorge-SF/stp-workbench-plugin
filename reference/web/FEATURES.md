# Web reference — feature map

The files in this folder are the browser planner resources. They were extracted **verbatim** from `RESOURCE_SOURCES` in `2. VGS_ShortTermPlanner.py`; scripts 1–3 embed identical copies (sha256 prefixes below). The template keeps its placeholders. In particular `__MAPTEK_LOGO_DATA_URL__` is **not** filled, and no binary resource is committed.

| File | Resource name | Lines | sha256 (prefix) |
|---|---|---|---|
| `short_term_planner_template_v2.html` | `short_term_planner_template_v2.html` | 4808 | `ca2c33b1eea37377` |
| `planner_logic.js` | `stp_v2_planner_logic.js` | 1076 | `876acec8c2a32bcb` |
| `timeline_logic.js` | `stp_v2_timeline_logic.js` | 944 | `dfaf3ff1b5001d78` |
| `refresh_client.js` | `stp_v2_refresh_client.js` | 218 | `e58558f413c13d31` |
| `v1_compat_logic.js` | `stp_v2_v1_compat_logic.js` | 952 | `fabc389f9196e3b9` |

All four JS files are DOM-free ES modules. `planner_logic.js` holds the scheduling rules the C# engine must reproduce, and `tools/golden/` pins its outputs. The other three files are presentation, compatibility and bridge logic.

---

## planner_logic.js — scheduling business rules (port first)

Time model: naive civil datetimes, held as BigInt microseconds (`civilScalar`). Days are counted with a proleptic Gregorian day number. Years must be 0001–9999.

| Function(s) | Lines | Feature / business rule |
|---|---|---|
| `civilParts`, `daysInMonth`, `dayNumber`, `dateFromDayNumber`, `civilScalar`, `civilDate`, `addCivilDays`, `midnightText` | L12–L56 | Civil calendar arithmetic. `addCivilDays` keeps the time of day, drops a zero fraction, and throws code `CIVIL_RANGE` outside years 1–9999. |
| `durationDays`, `horizonDayCount` | L57–L64 | A horizon is 1–366 whole days between midnights. |
| `horizonStorageKey`, `loadHorizonPreference`, `saveHorizonPreference`, `parsePreferenceStartDay`, `normalizeHorizonPreference`, `horizonPreferenceFallback`, `acquireHorizonStorage` | L65–L82, L116–L154 | Horizon preference in localStorage. The key is `vgs-stp-v2:horizon:<projectKey>`, with a 1 KiB cap. v2 `{version,duration_days}` migrates to v3 `{version,start_day,duration_days}`. Corrupt values fall back to 28 days with a Spanish warning. Unavailable storage degrades to a throwing stub. |
| `deriveCivilHorizon` | L84–L94 | The default horizon starts at the midnight of the earliest **pending** `originalStart` and runs for N days (default 28). It fails if no work is pending. |
| `validateCivilHorizon` | L96–L114 | The horizon must be midnight-aligned, 1–366 days long, and overlap at least one pending fragment. |
| `escapeHtml` | L156 | HTML escaping helper used by the page. |
| `buildPlannerModel` | L160–L184 | Turns a snapshot into the planner model: activities with `nativeBars`, precedences, and flat `fragments` carrying `activityId` and `provenance:''`. It sets `anchorBefore`/`anchorAfter` from `nativeAnchors` (`rightBarKey`+`rightAt` = fragment start; `leftBarKey`+`at` = fragment finish). |
| `validatePlannerModel`, `preflight`, `validatePlannerStructure` | L186, L785–L840 | Structural validation. Activity, bar and fragment ids must be unique. A fragment's bar must exist. Anchors must not cross the original interval. A locked fragment must sit at its original times. Precedence types and lags must be evaluable. |
| `normalizeModel`, `normalizeAlias`, `normalizeLock`, `normalizeLag` | L400–L459 | camelCase and snake_case aliases are accepted, and conflicting aliases are rejected. The lag is normalised to the string `lagMicroseconds`; `lag`/`lagSeconds` are read as seconds. |
| `preexistingPrecedenceConflicts`, `assessPrecedenceConflicts` | L190–L206 | **Precedence conflict policy.** These are links already violated in the native schedule (inherited). With policy `block-inherited` (the default) `blocksOperations` is true when any exist. `allow-inherited` comes from the bridge config. |
| `outcomeCode` | L208–L216 | Maps an error to a UI code: `civil-range`, `horizon-range`, `completed-work`, `incoming-precedence`, `native-anchor` or `invalid-move`. The last four are derived from the Spanish message text. |
| `createCivilHorizonController` | L218–L397 | **The UI-facing state machine.** It keeps the model and horizon, and every operation returns `{ok,message,code}`. The horizon is frozen while local moves exist (`local-changes`). `state()` exposes `canDownload`, `displayHorizon` (finish shown inclusive) and `status`. `activityEligibility` probes ±1 day for the arrow buttons. `fragmentEligibility` returns completed, out-of-range or eligible. `fragmentState` returns completed, in-range or out-of-range. Operations: `editStart`, `editFinish`, `editDuration`, `savePreference`, `moveActivity`, `moveDailyTail`, `deferDailyTailToNextPeriod`, `staggerDailyTail`, `reset`. |
| `normalType`, `lagUs`, `fragmentsFor`, `span`, `requiredShift` | L462–L497 | **Precedence evaluation.** The span of a (activity, native bar) runs from its earliest start to its latest finish. The deficit is FS `pred.finish+lag−succ.start`, SS `pred.start+lag−succ.start`, FF `pred.finish+lag−succ.finish`, SF `pred.start+lag−succ.finish`, clamped at ≥0. |
| `ceilDays` | L498 | A cascade shift is rounded **up** to whole civil days. |
| `validateShiftBounds`, `applyShift`, `shiftFragments` | L499–L522 | **Day shift.** Moves the pending fragments that are not direct by N days and stamps their provenance. A completed fragment after a pending one is rejected (`prefijo completado ambiguo`). |
| `lockedFragment`, `pendingFragments` | L523–L540 | **Completed-work lock.** `lockedCompleted===true` fragments never move. Pending work must be a suffix. A fully completed activity is rejected (`actividad completada`). |
| `shiftDays` | L541–L548 | A fragment's shift must be equal at start and finish and a whole number of days. |
| `logicalUnits` | L549–L594 | **Splits / logical units.** Fragments are grouped per `logicalUnitDate` (or original start date). All fragments of a unit must shift together, keep their order and keep their internal offsets (`split nativo deformado/separado`, `orden … alterado`, `unión … alterada`). Each unit gets a `currentDate`. |
| `shiftLogicalUnit`, `shiftLogicalUnitByDays` | L595–L610 | Shift a whole logical unit. |
| `validateDailyTailMoveRequest`, `validateDailyStaggerRequest` | L611–L632 | Exact request shapes for tail moves and stagger. |
| `validateNativeTopology` | L633–L648 | A tail move must not change ids, bar keys, original times, quantities, anchors or splitOrder. |
| `existingDirectShifts`, `directActivityIds`, `hasNonUniformDirectState`, `restoreCurrentFragments`, `resetPendingToOriginal`, `rebuildDirectPreview` | L649–L721 | Direct-move bookkeeping. A uniform whole-activity shift is held as a per-activity total; a non-uniform daily-tail state is rebuilt from direct fragments and the cascades are recomputed. |
| `requireDirectContainment`, `requireCurrentContainment` | L722–L735 | Directly moved pending work must stay inside the horizon (code `HORIZON_RANGE`). |
| `checkOverlaps` | L736–L747 | Fragments of an activity must not overlap, and at most one logical unit may sit on a civil day. |
| `incomingSafe`, `originalSchedule`, `baselinePrecedenceDeficits`, `ensurePrecedencesNotWorsened` | L748–L783 | **No aggravation.** A direct move must not increase an incoming precedence deficit beyond its native baseline. After a cascade, no link may be worse than its baseline. |
| `cascadeForward` | L842–L868 | **Successor push.** A BFS from the directly moved activities. Each successor is pushed by `ceilDays(deficit − baselineDeficit)`. It fails when it would push a fully direct successor, push a completed prefix (`prefijo completado impide la cascada`), or not converge (cycle guard). |
| `moveFragment` | L870–L883 | Moves the same-bar tail from a fragment, then cascades. *Exported but not used by the HTML.* |
| `moveDailyTail` | L885–L905 | **Daily-tail insert.** Shifts the selected logical unit and every later unit to the target date, rebuilds the cascades and checks topology. |
| `staggerDailyTail` | L907–L936 | **Split & stagger.** For k=1..divisions, moves the tail from unit (selected+k) to its current date plus `gapDays`, within the horizon. |
| `moveActivity` | L938–L967 | **Whole-activity day shift** by ±N days, accumulated as a per-activity total. It rebuilds from the originals, then checks containment, overlaps and incoming precedence, then cascades. |
| `recomputedPreview`, `requirePreviewMatch` | L969–L1002 | Before export, the model must equal a from-scratch recomputation of the same direct moves (tamper and staleness guard). |
| `buildScenario` | L1004–L1033 | Builds the `vgs-stp-splits/2` scenario. *Exported but not used by the HTML.* |
| `serializeAllAffectedActivitiesByFragment`, `buildVgsApplyPackage` | L1035–L1076 | **Apply package** `vgs-stp-apply/1`. Every fragment of each affected activity is listed, sorted by `originalStart` then id, with `logical_unit_id` = `activityId:date`. Provenance is `direct`, `cascade` or `unchanged`. The package carries the project and precedence fingerprints and the horizon. It fails when nothing changed or when native metadata is missing. |

## timeline_logic.js — daily grid projection

| Function(s) | Lines | Feature |
|---|---|---|
| civil helpers (`parseDate`, `dayNumber`, `addDateDays`, `scalar`, `midnight`, …) | L17–L98 | The same civil arithmetic as the planner. |
| `visualDefaults`, `validateVisualValue`, `loadVisualPreferences`, `saveVisualPreferences` | L100–L191 | Visual preferences under `vgs-stp-v2:visual:<projectKey>` (16 KiB cap, v1–v3 migrate to v3). Fields: showCompleted, showPrecedences, attributes, sorting. |
| `horizonInfo`, `headers` | L193–L233 | Horizon days, with headers labelled `"<dom…sáb> DD/MM"`. |
| `activityIdentity`, `gridText`, `activityType`, `activityResource`, `activityRate` | L235–L278 | The row label comes from the first of `Description` / `Name` / `Activity Name` that is not a date. Type, resource and rate come from grid columns, with defaults `Sin tipo` and `Sin recurso`. |
| `splitProtection`, `fragmentStatus`, `segmentGroups` | L288–L357 | Segments per day with left and width ratios, `scheduledAmount = rate × widthRatio`, protected native-split flags, and status `completed` > `direct` > `cascade` > `pending`. |
| `logicalUnitProjection`, `normalizedFragmentEligibility` | L369–L420 | Logical-unit id and current date, plus per-unit eligibility. |
| `precedenceLines`, `continuityLines`, `diagnostics` | L422–L533 | Link overlay geometry (FS/SS/FF/SF) and diagnostics. |
| **`buildTimelineView`** | L558–L664 | The main projection: rows, sort order (type, attribute, start, source), lines, metrics, legend. |
| `civilDragDelta`, `captureVisibleDailyTail`, `previewVisibleDailyTail`, `previewExcesses` | L666–L836 | Drag: a whole-day delta, a frozen tail at drag start, a live preview, and non-blocking capacity and fronts warnings. |
| `previewWholePendingMove`, `previewDailyTail` | L673, L838 | Exported but not used by the HTML. |
| `refreshDecision`, `humanPlannerOutcome` | L923–L944 | Confirms before a refresh discards local moves. Maps outcome codes to Spanish messages. |

## v1_compat_logic.js — V1-style views, capacity, reports, local actual progress

| Function(s) | Lines | Feature |
|---|---|---|
| helpers, `vgsResourceOptions`, `normalizeManualResources`, `combinedResourceOptions` | L20–L100 | VGS resources (`vgs:<name>`) and manual resources (`manual:<n>`). Grid attributes with sensitive names are dropped. |
| `deriveCompatibilityCatalog` | L157–L204 | Per-activity type, resource, safe attributes and original order. |
| `normalizeCompatibilityPreferences`, `migrateCompatibilityPreferences` | L206–L310 | The full compatibility preference schema: extraDays 0–31, fronts and capacities 1–4, type objectives, overrides, exemptions, reports, colours, print config. |
| `resourceOptions`, `effectiveResourceFor` | L274–L292 | Resource resolution: fragment override, then activity override, then VGS, then `Sin recurso`. |
| `orderCompatibilityRows`, `clipContextDays` | L312–L376 | Stable row ordering; horizon days plus context days. |
| **`buildCapacityRows`** | L482–L524 | Capacity per type per day against its objective, and fronts per resource per day against capacity. |
| **`buildReportRows`** | L541–L586 | Daily reports: sum, average, weightedAverage, maximum, minimum, with prorated attributes. |
| **`buildActualProgressByType`**, period totals | L605–L715 | Planned vs actual per type and resource (charts, Excel). |
| local actual-progress slot and value functions | L653–L952 | Local actual progress keyed by `[activityId,date]`: slots, hidden slots, drag cascades, legacy migration. `moveLocalActualProgress` (L814) is not used by the HTML. |

## refresh_client.js — authenticated live refresh

| Function(s) | Lines | Feature |
|---|---|---|
| `captureBridgeConfig`, `validBridgeConfig`, validators | L21–L100 | The bridge config has mode `live`, endpoint `http://127.0.0.1:<port>`, a token of at least 16 characters, projectKey, a 32-hex bridgeId and a message. Optional `precedenceConflictPolicy` ∈ {`allow-inherited`, `block-inherited`}. |
| `validCurrentSnapshot`, `validRefreshedSnapshot` | L102–L126 | A refreshed snapshot needs schema `vgs-stp-snapshot/2`, the same projectPath and projectKey, `snapshotComplete===true` and `applyAllowed===false`. |
| `snapshotIdentityMatches` | L148–L162 | The VGS state is unchanged when the key and both fingerprints are equal. |
| `fetchRefreshedSnapshot`, `prepareRefreshedSession` | L164–L218 | `GET /snapshot` with header `X-STP-V2-Token`, `no-store`. A 409 means a stale snapshot. The candidate is then prepared. |

## short_term_planner_template_v2.html — UI shell (to be replaced by WPF)

The CSS spans L7–L706 and the DOM L708–L830. Placeholders sit at:

* L831–L834: module sources `__VGS_STP_V2_LOGIC__`, `__VGS_STP_V2_TIMELINE_LOGIC__`, `__VGS_STP_V2_REFRESH_CLIENT__`, `__VGS_STP_V2_COMPAT_LOGIC__`;
* L838–L839: `__VGS_STP_V2_SNAPSHOT__` and `__VGS_STP_V2_BRIDGE__`, inside the standalone-bootstrap markers;
* L2543: `'__MAPTEK_LOGO_DATA_URL__'`, the print footer logo.

| Feature area | Lines | Key functions |
|---|---|---|
| Bootstrap, module import, bridge policy | L836–L951 | `importEmbeddedModule` (L842). `precedenceConflictPolicy` (L888). `acquireHorizonStorage(localStorage)`. Initial `assessPrecedenceConflicts` (L925). |
| Planner wiring | L972–L977 | `createPlanner` = `validatePlannerModel(buildPlannerModel(snapshot))` + `createCivilHorizonController`. |
| Compatibility preferences | L979–L1156, L1412–L1453 | Storage prefix `vgs-stp-v2:compatibility:`. `buildCompatibilityView`. |
| Apply package, canonical JSON, SHA-256, receipt | L1185–L1402 | `buildCurrentApplyPackage` (L1193). `canonicalJsonValue`/`canonicalPackageText`/`canonicalPackageDigest` (L1270–L1289). `persistApplyPackageWhenLive` (L1333) POSTs `/apply-package` and expects a 201 receipt `{package_sha256,project_fingerprint,schema,status:'saved'}`. `prepareApplyPackage` (L1355) downloads `stp_apply_pending_v2.json`. |
| Standalone HTML export | L1455–L1548 | `standaloneDocumentHtml`, `exportStandalonePlanner`. |
| Print / PDF | L1550–L1919 | Pages of at most 31 days, `printPlanner`. |
| Excel export | L1921–L2059 | `buildExcelExportPayload` (`vgs-stp-excel-export/1`), POST `/export-xlsx`. |
| Daily-tail actions | L2061–L2106 | Tail move, defer to next period, stagger. |
| Timeline, aggregate rows, charts, colours, option panels, link overlay | L2108–L3394 | `renderTimelineRow`, `renderAggregateRows`, `renderProgressCharts`, `renderOptionPanels`, `renderLinkOverlay`. |
| Horizon controls | L3423–L3524 | `updateHorizonDraft`, `requestHorizonChange`. |
| Main render, drag, actual-progress drag, keyboard and context menu | L3526–L4489 | `renderPlanner` (L3535), `beginDelegatedDrag`/`finishDelegatedDrag`, popovers. |
| Live refresh | L4060–L4178 | `refreshFromVgs`: confirm, `prepareRefreshedSession`, re-assess conflicts, commit or roll back. |
| Event wiring and startup | L4490–L4806 | Buttons, inputs, initial `renderPlanner()`, `detectLiveSnapshotVersion()`. |
