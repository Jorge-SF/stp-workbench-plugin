# STP UG V2 as a Workbench plugin, with a native C# planner view

Status: analysis and design only (2026-11-19). Nothing has been built or changed.
Scope: the five STP scripts in this folder (`0.` to `4.`). `4. DeletPoint FinishStart.py` is an
unrelated geometry script and is left out.

---

## 1. Current system in one page

| Step | Script | Runs where | Touches VGS |
|---|---|---|---|
| Prepare | `1. VGS_PrepareProjectForSTP.py` | Inside Vulcan (Envisage Python) | `save(copy)`, `remove_activities`, `set_pause_ui`, `update_ui`, `export_activities_to_csv` |
| Plan | `2. VGS_ShortTermPlanner.py` | Inside Vulcan, then **browser** + a `python.exe` loopback HTTP bridge | Read-only (`export_activities_to_csv`, reads the `.vgantt` zip/XML directly) |
| Apply | `3. VGS_ApplySTPToVGS.py` | Inside Vulcan | `split_activity`, `set_start_of_bar`, `get_number_bars`, `update_ui`, `set_pause_ui` (never saves) |
| Verify | `4. VGS_VerifySTPSave.py` | Anywhere (no Maptek import) | None, reads files only |
| Diagnose | `0. Diagnostico_Puente_STP.py` | Anywhere | None, probes the bridge |

Packaging: scripts 1-4 each embed the **same** 10 Python modules + 5 web resources + a logo
(~1.3 MB each); only the final ~150-330-line "public script" differs. Script 0 embeds a
zlib/base85 bundle whose `stp_live_refresh_server_v2`, `stp_v2_apply_contract`,
`stp_v2_bridge_bootstrap`, HTML template and `stp_v2_v1_compat_logic.js` **differ** from the
other four (version drift, to be resolved before extraction).

Python modules (identical in scripts 1-4):

| Module | Lines | Role | Keep in Python? |
|---|---|---|---|
| `stp_v2_core` | 2252 | Read-only `.vgantt` reader (zip + XML), scenario model, fingerprints | Yes |
| `stp_v2_snapshot_contract` | 331 | Builds the canonical snapshot JSON the UI consumes | Yes |
| `stp_v2_apply_contract` | 440 | Validates an apply package (schema `vgs-stp-apply/1`) fail-closed | Yes |
| `stp_v2_apply_transaction` | 2050 | Backup / apply / verify / restore transaction, receipts, HTML report | Yes |
| `stp_v2_maptek_apply_adapter` | 1942 | Plans and runs the `maptek.gantt` calls | Yes (only place that may call Maptek) |
| `stp_v2_prepare_project` | 331 | Script 1 logic | Yes |
| `stp_live_refresh_server_v2` | 770 | Loopback HTTP bridge (`/health`, `/snapshot`, `/scenario`, `/apply-package`, `/export-xlsx`) | Yes, client changes |
| `stp_v2_bridge_bootstrap` | 488 | Starts the bridge subprocess, lease/ready files | Yes |
| `stp_v2_excel_export` | 131 | Writes the xlsx export | Yes (or move to C#, optional) |
| `stp_v2_windows_atomic` | 772 | Atomic, locked file writes on Windows | Yes |

Web resources (replaced by the C# view): `short_term_planner_template_v2.html` (4808 lines),
`stp_v2_planner_logic.js` (1076), `stp_v2_timeline_logic.js` (944),
`stp_v2_v1_compat_logic.js` (952), `stp_v2_refresh_client.js` (218).

---

## 2. What the Workbench offers (checked on this PC, Workbench 2026.3 / Vulcan 2026.1)

Confirmed from the installed assemblies and from the in-house Pit Design plugin
(`D:\14-Temp_Project\Pit_Design_repo\PIT_DESIGN\workbench_plugin`, see its `CLAUDE.md` and
`docs\Workbench_Plugin_Guide.md`, which already solve build, SDK runtime, registration and
installer):

- **Plugins are C# .NET Framework 4.8 WPF class libraries.** The class derives from
  `Maptek.Workbench.Client.WorkbenchPluginBase` with
  `[ClientPlugin("<Company>.<Product>.ClientPlugin", apiVersion, PackageName = ...)]`.
- **Loading:** `HKLM\SOFTWARE\Maptek\Workbench\Client\PluginSearchPaths\<Name> = <Deploy\Client>`
  (Pit Design is already registered this way as `BasePlugin`), or per user in
  `WorkbenchPreferences.xml`.
- **Menus and toolbars** come from embedded `Menus\Commands.xml` + `Menus\Menus.xml`
  (`CommandManager.AddFromMarkup`, `MenuManager.AddFromMarkup`). The Vulcan menu bar segment
  `Maptek.Vulcan.Envisage.Segments.EnvisageMenus` is verified by Pit Design. Toolbars use
  `<Toolbar>` in the same markup.
- **Views:** `WorkbenchViewModelBase` + XAML `wb:WorkbenchViewBase`, `ViewType`
  `Dockable | Document | Dialog`, opened with `ViewManager.OpenViewModel(this, vm, false)`.
  The planner should be a **Document** view (big central tab) or a Dockable one.
- **Running Python inside Vulcan from C#:** Vulcan's plugins import
  `[ImportInterface("Maptek.Vulcan.Envisage.Interfaces.Envisage")] IEnvisageInterface`, which
  has `ExecutePythonScript(string script, string instanceId)`,
  `RunPythonScript(Moniker, RunExecutableContext)` and `TerminatePythonScript(instanceId)`, plus
  `EnvisageFullyLoaded`, `EnvisageMenuVisible`, `ShowUnlicensedOptions` for enabling menus
  (Sawtooth does exactly this). **[UNVERIFIED]** what the first argument of
  `ExecutePythonScript` is (path or source text) and whether the call blocks; the SDK `.chm` in
  the Pit Design runtime folder should answer it, otherwise a 10-line test.
- **Libraries already shipped in the Workbench client** (no extra redistribution): Infragistics
  WPF (`XamDataChart`, `XamDataTree`, `DataPresenter` grids, `Documents.Excel`, `Undo`),
  SkiaSharp + `SkiaSharp.Views.WPF`, CefSharp, Newtonsoft.Json, Maptek WPF controls.
  There is **no Gantt control**; the timeline must be custom-drawn.
- **Built-in web view:** command `Maptek.Workbench.WebExplorer.Commands.WebBrowserView` with a
  `Moniker(url)` (used by Pit Design). Relevant to option C below.

---

## 3. Architecture options

### A. Recommended: native C# view over the existing Python core

```
Workbench client process (C# plugin)                     Vulcan / Envisage process
┌───────────────────────────────────────────┐            ┌─────────────────────────────┐
│ STP menu + toolbar (Menus.xml)             │──Run py──▶│ stp_entry_prepare.py         │
│  Prepare · Plan · Apply · Verify · Diagnose│ (IEnvisage │ stp_entry_plan.py            │
│                                           │  Interface)│ stp_entry_apply.py           │
│ Planner view (WPF Document view)           │            │   └ maptek.gantt calls only │
│  Gantt canvas · grid · filters · charts    │            └──────────────┬──────────────┘
│  Planner engine (port of planner/timeline  │                           │ starts
│  JS logic, C#)                             │                           ▼
│  BridgeClient (HttpClient, token header)  ◀┼──HTTP 127.0.0.1──▶ stp_live_refresh_server_v2
└───────────────────────────────────────────┘   (unchanged      (python.exe, read-only of
                                                 wire protocol)    .vgantt, writes pending JSON)
```

- Python keeps **everything that touches VGS or the project files**: reading `.vgantt`,
  snapshot, validation, the apply transaction, backups, receipts, verify. That code is
  fail-closed and heavily guarded; rewriting it in C# would discard that work and the
  `maptek.gantt` API is Python-only.
- The bridge's wire protocol stays as is. The C# client sends the same JSON the browser sends
  and gets the same validation answers. **The server re-validates every package**
  (`validate_apply_package`), so a bug in the C# planner engine is rejected, not applied.
- Script 2 stops rendering HTML and calling `os.startfile`. It writes the snapshot and the bridge
  page config (`endpoint`, `token`, policy) to a known runtime file (it already writes
  `snapshot_v2.json` and a ready file). The plugin reads it and opens the view.
- Scripts 1, 3, 4 keep their logic; their `vulcan_gui` panels and messages either stay (fastest)
  or are replaced by C# dialogs that pass arguments to the script.

### B. Full C# port (not recommended)

Port the `.vgantt` reader, contracts, transaction and Maptek adapter to C#. Roughly 3-4x the
work of A, and the live VGS calls (`split_activity`, `set_start_of_bar`...) still have to run in
Envisage's Python, so a Python part remains anyway.

### C. Bridge step: host the existing HTML in the Workbench web view

Open the generated `index.html` with `WebBrowserView` inside a Workbench tab. Close to zero
work, and it removes the "opens in an external browser" problem while A is being built.
**[UNVERIFIED]** that the CefSharp view allows `fetch` to `127.0.0.1` from a `file://` page.

---

## 4. Planner view: feature inventory to port

About 7,800 lines of JS + CSS + markup, all Spanish UI text. Line references use these labels:
T = `short_term_planner_template_v2.html`, PL = `stp_v2_planner_logic.js`,
TL = `stp_v2_timeline_logic.js`, CL = `stp_v2_v1_compat_logic.js`, RC = `stp_v2_refresh_client.js`.

**Not present in the web UI (so not needed for parity):**
- undo/redo
- zoom or time-scale switching (fixed day scale: 46 px per day, 286 px activity column)
- today line
- weekend shading
- bar resizing
- polling

### 4.1 Layout
- **Header:** title, project, export date, live or static bridge state.
- **Notice bar.**
- **Control ribbon:**
  - horizon start, inclusive finish, duration (1-366)
  - show completed, sort by start, group by type, show precedences, sort by attribute and direction, type filter
  - Apply range / Reset
- **Tools menu:**
  - Refresh VGS→STP
  - Prepare STP→VGS package
  - Print/PDF
  - Excel
  - standalone HTML
  - Save settings
  - "Actual progress" mode toggle
- **Options ribbon, 5 tabs:**
  - activity colours, objectives and resources (incl. manual resources)
  - visible attributes
  - report-row editor (sum / avg / weighted avg / max / min, with filters)
  - 5 general colours
  - print settings and logo
- **Summary tiles (4)**, **legend**, and the **Gantt timeline** with an SVG link overlay.
- **Aggregate rows:**
  - capacity per type
  - fronts per resource
  - report rows
  - actual progress
- **Charts:**
  - daily stacked bars per type
  - donut per type, plan vs actual by resource
- **Diagnostics** JSON panel.

### 4.2 Interactions
- **Drag a daily unit:**
  - The unit and its **tail** (all later pending units of the activity) move by whole days.
  - The preview is redrawn once per frame; Esc cancels.
  - The move itself is done by `planner.moveDailyTail` (T:3881-3960, PL:885).
- **Arrow Left/Right** on a focused bar moves the tail by ±1 day.
- **Right-click menu on a bar** (T:4382):
  - assign equipment
  - exempt from limit
  - send to next period
  - split and stagger (N divisions, gap days)
- **Resources:**
  - Resource override per activity and per daily unit.
  - Manual resources (`manual:N`) with add, rename and remove.
- **Horizon change:** rebuilds the plan from the baseline snapshot after a confirmation; local moves are discarded.
- **Actual-progress mode:**
  - number inputs per day, plus drag grips with cascade
  - kept in memory only, never sent to VGS
- **Preferences** (localStorage today, per `projectKey`):
  - visual settings (v3, ≤16 KB)
  - compatibility settings (≤1.1 MB, incl. logo)
  - horizon (read only, in practice)
  - **In C#, keep them in a per-user JSON file under `%AppData%\Maptek\STP\` or in Workbench settings.**

### 4.3 Business rules on the client (must be ported 1:1, with tests)
- **Civil time:**
  - Naive local time in microseconds.
  - The horizon runs midnight to midnight, finish exclusive, 1-366 days.
  - It must overlap pending work; the default is the earliest pending start + 28 days (PL:12-144).
- **Logical units:**
  - Fragments are grouped by `logicalUnitDate`.
  - They shift uniformly and keep native split joins (PL:549).
- **Locked completed work:** it never moves, and it blocks tails and cascades (PL:523, 858, 898).
- **Overlaps:** none within an activity; at most one logical unit per day (PL:736).
- **Cascade forward:**
  - Breadth-first over successors.
  - It pushes only the deficit **above the baseline deficit**, rounded up to whole days, with a cycle guard.
  - Moving earlier is allowed only when no incoming precedence gets worse (PL:483, 691, 748, 842).
- **Precedence conflict policy:**
  - `block-inherited` disables the package when the original VGS schedule already has violated links.
  - `allow-inherited` permits it.
  - A move never worsens a deficit in either mode (PL:190-200).
- **Topology check:** ids, keys, originals, quantities, anchors and split order are unchanged (PL:633).
- **Package build:** re-derives the preview and requires an exact match with the model (PL:969-1002, 1061).
- **Capacity:**
  - Type objective and resource fronts (1-4) only warn; they never block.
  - Effective resource: unit override → activity override → grid `Resource`/`Recurso`/`Equipo` → "Sin recurso".
- **Display amounts:** `rate × widthRatio`, where Rate comes from the grid.
- **Error codes:** civil-range, horizon-range, completed-work, incoming-precedence, native-anchor, invalid-move, each with a Spanish message.

### 4.4 Bridge contract the C# client must speak
- **Config:**
  - Fields: `{mode:'live', endpoint:'http://127.0.0.1:<port>', token (≥16), projectKey (64 hex), bridgeId (32 hex), message, precedenceConflictPolicy}`.
  - Header `X-STP-V2-Token` on every call.
  - The server accepts **no `Origin` or `Origin: null`**. `HttpClient` sends none, so it is compatible as is.
- **`GET /snapshot`:**
  - Once at start-up (stale check by fingerprints) and on manual refresh.
  - 409 = stale.
  - The response must have exactly the snapshot keys plus `snapshotComplete:true, applyAllowed:false`.
- **`POST /apply-package`:**
  - Body: `vgs-stp-apply/1`.
  - 201 with a receipt `{status:'saved', schema, package_sha256, project_fingerprint}`.
  - The client recomputes the SHA-256 of **canonical JSON** (keys sorted recursively, separators `,` and `:`, non-ASCII kept). That must byte-match Python's `json.dumps(sort_keys=True, separators=(",",":"), ensure_ascii=False)`, which is a known C# pitfall (number formatting, escaping).
  - 422 `invalid apply package`.
  - 422 `apply package unsafe` comes with `diagnostic{reason, activities[≤5]}`.
- **`POST /export-xlsx`:**
  - Body: `vgs-stp-excel-export/1` with per-day plan/actual, aggregates, chart data and **chart PNGs (≤32, ≤3 MB each)**.
  - The server builds the xlsx with `xlsxwriter`.
  - Alternative: build it in C# with the shipped `InfragisticsWPF.Documents.Excel` and drop the endpoint.
- **Not used by the UI:** `/health`, `/scenario`.
- **Server limits:** 20 MB body, 16 concurrent requests.
- **Static mode** (no bridge): refresh and Excel are disabled, and the package downloads locally "sin validación". **In the plugin, drop static mode or keep it explicit.**

### 4.5 Outputs
- the apply package (above)
- the Excel payload (above)
- **Print/PDF:**
  - 31-day blocks; A4 landscape from 15 days.
  - Header with logo; aggregate rows and legend.
  - Chart pages with up to 8 cards per page.
  - Footer "Maptek Mexico & Centro America".
- **Standalone HTML:** proposal: drop it, or replace it with a saved session file.

---

## 5. C# solution layout (option A)

Start from the Pit Design solution (rename checklist in its `CLAUDE.md` §3) so the SDK runtime,
StyleCop settings, version props, registration script and installer come for free.

```
STP_Workbench.sln
├─ StpClient/                         .NET Framework 4.8 WPF, [ClientPlugin("Maptek.Vulcan.Stp.ClientPlugin")]
│  ├─ StpClientPlugin.cs              imports IEnvisageInterface, registers commands/menus
│  ├─ Menus/Commands.xml, Menus.xml   STP submenu on the Vulcan menu bar + STP toolbar
│  ├─ Python/                         stp_*.py shipped next to the DLL (single shared copy)
│  ├─ Bridge/                         BridgeClient (HttpClient), BridgeConfig, CanonicalJson, receipts
│  ├─ Engine/                         port of PL + TL + CL (no WPF references)
│  ├─ Views/PlannerView.xaml(.cs)     Document view: ribbon, options tabs, summary, legend
│  ├─ Views/Timeline/                 custom Gantt control (DrawingVisual or SkiaSharp), link router
│  ├─ Views/Charts/                   daily bars + donuts (XamDataChart or custom draw), PNG export
│  ├─ Print/                          FixedDocument report (31-day blocks), PDF via print-to-PDF
│  └─ Resources/Resources.resx (+ .es.resx)
└─ StpEngine.Tests/                   golden tests: same snapshot + same moves ⇒ identical apply
                                      package as the JS engine (byte-for-byte canonical JSON)
```

Python side: unbundle into one `stp/` package plus thin entry scripts. Script 2 gains a
"plugin mode" that skips HTML rendering and `os.startfile` and writes
`<runtime>/plugin_session.json` = `{snapshot_path, bridge_config}`.

**Key testing lever:** the JS engine is DOM-free (PL, TL, CL). Run it under Node to produce
reference outputs (moves → apply package, capacity rows, report rows) for many snapshots, and
assert that the C# engine matches. This makes the riskiest 60% of the port verifiable without
Vulcan.

---

## 6. Phases and rough effort (one experienced C#/WPF developer)

| Phase | Content | Effort | Status (2026-11-19) |
|---|---|---|---|
| 0 | Resolve the script-0 version drift. Unbundle Python into one package. No behaviour change, verified in VGS | 2-3 d | **Mostly done.** Python unbundled into `python/stp_core` (PR #1); web reference and golden harness (PR #2). Drift documented in `docs/drift_script0.md`; choosing the script-0 version is still open |
| 1 | Plugin skeleton from Pit Design: STP menu + toolbar running scripts 1-4 inside Envisage (verify `ExecutePythonScript`/`RunPythonScript`); optionally host today's HTML in `WebBrowserView` (option C) | 3-5 d | **Done** (PR #4, plus Envisage binding for any Vulcan version). Optional option C not done |
| 2 | Engine port (PL/TL/CL) + Node-generated golden tests + canonical JSON/SHA-256 | 2-3 wk | **Done** (PRs #5, #6, #8). KI-1 documented in `docs/known_issues.md` |
| 3 | Bridge client + plugin-mode script 2 + read-only timeline (rows, bars, statuses, links) | 1.5-2 wk | **Done in code, not yet checked in Vulcan.** Bridge client (PRs #5, #7), plugin-mode script 2 (PR #3), view model (PR #11), timeline control (PR #10); *Abrir planificador* runs 2p and opens the read-only planner on the user's project (branch `wire-planner`) |
| 4 | Interactions: drag tail with preview, keyboard, context menu, stagger, next period, resources, exemptions, horizon edit, refresh with rollback, package prepare | 2-3 wk | **In progress.** Timeline editing (PR #13) wired to `PlannerSession` (branch `timeline-edit-wiring`): drag/keys/menu moves, next period, split and stagger, undo/redo, per-day equipment and capacity exemptions (local). Not checked in Vulcan yet; drag preview of capacity excesses and manual resources not done |
| 5 | Options tabs, preferences store, aggregate rows, report engine UI, actual-progress mode | 1.5-2 wk | Not started |
| 6 | Charts, Excel export (PNG capture), print/PDF report | 1.5-2 wk | Not started |
| 7 | Spanish/English resx, installer (MSI from Pit Design), testing inside Vulcan on real projects | 1-1.5 wk | **Partly done.** Build script and MSI installer done; per-user runtime folder (KI-2 fixed). Only one `Resources.resx` so far (no English/Spanish pair); testing on real projects pending |

Total ≈ **10-14 weeks**. Phases 2 and 4 carry most of the risk.

---

## 7. Open questions and risks

1. **Python launch from the plugin** (blocking): the signature of
   `IEnvisageInterface.ExecutePythonScript(string, string)` / `RunPythonScript(Moniker, RunExecutableContext)` is
   known, but not whether the first argument is a path, how arguments are passed, or how
   completion is signalled. Check `WorkbenchSDK.chm` in the Pit Design runtime, then a 10-line spike.
   Fallback: a Workbench custom menu item that runs the `.py` (CustomizationClientPlugin).
2. **`maptek.gantt` from a standalone process:** Pit Design talks to Vulcan from short-lived
   standalone processes through the `vulcan_gui` link. Whether `gantt.Gantt.active_project()`
   works the same way is **[UNVERIFIED]**. If it does not, every VGS call must stay in
   Envisage-launched scripts, which is what option A assumes.
3. **Undocumented Gantt calls:** `set_start_of_bar`, `get_number_bars` and `update_ui` are not in the
   API reference for this Vulcan version, although the apply adapter requires them. Confirm them on
   every Vulcan version you target.
4. **Version drift** between script 0 and scripts 1-4 (5 files). Choose the source of truth before
   Phase 0.
5. **Canonical JSON parity** C#↔Python for the package SHA-256 (number formats, Unicode
   escaping, key order). Cover it with golden tests.
6. **Vulcan session concurrency:** Pit Design's §5b rules apply (one connection at a time, never
   kill a process inside a write). Script 3 must never be cancellable mid-apply from the UI.
7. **Workbench lifecycle:** Vulcan locks the plugin DLL. Close Vulcan before rebuilding. Never
   recreate the `PluginSearchPaths` registry key (Pit Design trap #3).
8. **Language:** the UI is Spanish only today. Decide whether the plugin ships `es` only or
   `es` + `en` from the start (resx makes both cheap if done from day 1).
