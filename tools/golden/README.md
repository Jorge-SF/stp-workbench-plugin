# Golden-test harness for the STP planner rules

This harness pins the behaviour of the browser planner's business rules so the C# engine can be checked against it. Those rules live in [`reference/web/planner_logic.js`](../../reference/web/planner_logic.js).

It runs under Node only, with no browser and no dependencies. It loads `planner_logic.js` unchanged into a `node:vm` context and drives it through `createCivilHorizonController`, the same entry point the HTML page uses (`createPlanner` in the template). The synthetic `vgs-stp-snapshot/2` fixtures are replayed through it, and the harness records:

* each step's outcome object;
* the planner state after the last step;
* the full fragment table;
* the `vgs-stp-apply/1` package, its canonical JSON and the SHA-256 of that JSON.

## Run

```bash
node tools/golden/run.mjs            # regenerate tools/golden/expected/
node tools/golden/run.mjs --check    # compare with the committed expected/ (exit 1 on drift)
python tools/golden/verify_canonical.py   # optional: Python canonical JSON == Node bytes + SHA-256
```

`node tools/golden/make_fixtures.mjs` rebuilds `fixtures/*.snapshot.json` from the compact specs in that script. Run it only when you change those specs, then regenerate `expected/`.

The tested setup is Node 26. The harness only needs `node:vm`, `node:crypto` and BigInt, so it should also run on Node 18 or later.

## Layout

| Path | Content |
|---|---|
| `make_fixtures.mjs` | Builds the `chain_fs`, `completed_lock`, `precedence_conflict` and `native_splits` snapshots. Bars are split into civil-day fragments, with native anchors between bars, `lagMicroseconds` precedences and synthetic fingerprints. |
| `fixtures/<f>.snapshot.json` | Synthetic snapshots with schema `vgs-stp-snapshot/2`. All data is invented. `engine_edges` and `civil_edges` are hand-written (shapes `make_fixtures.mjs` cannot express) and are not rewritten by it. |
| `fixtures/<f>.cases.json` | Cases. Each one is a list of steps run on a fresh controller, with optional seeded `localStorage` (`{projectKey}` is substituted). |
| `expected/<f>.<case>.json` | Result per case: `initial`, `steps[].outcome`, `final`, `fragments`, `storage`, and `applyPackage` (`sha256` + `package`, or `error`). |
| `expected/<f>.<case>.apply.canonical.json` | The exact bytes that are hashed. They have no trailing newline. |
| `expected/index.json` | SHA-256 of `planner_logic.js`, its export list, and a one-line summary per case. |

Step ops map 1:1 to the controller: `moveActivity`, `moveDailyTail`, `deferDailyTail` (→ `deferDailyTailToNextPeriod`), `staggerDailyTail`, `activityEligibility`, `fragmentEligibility`, `fragmentState`, `editStart`, `editFinish`, `editDuration`, `savePreference` and `reset`. The extra ops `assessPrecedenceConflicts`, `moveFragment` and `buildScenario` call the module functions on the current model without changing the controller state.

## Coverage

| Fixture | Rules exercised |
|---|---|
| `chain_fs` | **Day shift** ±N days. **Successor push**, finish-start, with ceil-to-whole-day of a 12 h slack. Backward moves rejected when they would aggravate precedence. +2/−2 round trip produces no package. Daily-tail insert. Defer to next period. Split-and-stagger, including the too-many-divisions error. Horizon edit, containment, and freezing once local moves exist. Horizon preference load (v2 migration, v3, corrupt) and save. |
| `completed_lock` | **Completed-work lock**: only the pending bar moves. A fully completed activity cannot move. A cascade into a completed successor is rejected (StartStart, −24 h lag). A tail move on a completed day is rejected. A mid-day completion boundary rejects a whole-activity move (`split nativo separado`) but allows a later tail move. |
| `precedence_conflict` | **Precedence conflict policy**: an inherited violation with `block-inherited` vs `allow-inherited`. A push counts only what goes beyond the inherited deficit. Aggravation is rejected. StartStart +24 h lag. FinishFinish. |
| `native_splits` | **Splits**: a three-bar native split, with two bars sharing one civil day (one logical unit, two fragments) and a gap day. The whole split moves, and the successor is pushed by ceil(20 h). A tail move from the shared day moves both fragments. A tail move onto an occupied day is rejected; onto the gap day it is accepted. Stagger across the split. |
| `chain_fs` (port coverage, added with the C# engine) | Horizon `editStart`/`editFinish`/`editDuration` rejections (range, no pending work, malformed date, civil range), `local-changes` freeze, `reset` reloading a saved v3 preference. Whole moves after a tail move (non-uniform direct branch). Tail move on a cascaded successor. Direct-vs-cascade conflict. Out-of-range eligibility and state. Stagger `horizon-range` and invalid requests. Unknown activity/fragment, zero move, foreign fragment, same-day target. `moveFragment` and `buildScenario`. Preference edge cases: v3 start without pending work, `2.0`/`1e1` integers, duplicate keys, UTF-8 byte limit, impossible start day, extra key. |
| `engine_edges` | Fractional seconds with trailing zeros (`.250` re-rendered `.25`), `HH:MM` times, `lagSeconds` lag, type aliases `fs` and `Start-Finish`. A tail moved away and back keeps its instants but not its text, so JS still reports a direct change and builds a package the server rejects (`movement_provenance_without_shift`). The C# engine deliberately keeps the original text there; its result is pinned in `src/StpEngine.Tests/TestData/deviations/`. `chain_fs.tail_roundtrip` is the whole-second reference for the correct result. `moveFragment` on part of a bar. |
| `civil_edges` | Work in 9999-12: a derived horizon fails at setup (`CIVIL_RANGE`); with a seeded horizon, forward moves and defer-to-next-period hit `civil-range` and backward moves work. |

## Contract notes for the C# port

* Outcome `code` values are `''`, `invalid-move`, `completed-work`, `incoming-precedence`, `native-anchor`, `horizon-range`, `civil-range` and `local-changes`. JS derives some of them from the Spanish error text (`outcomeCode`, `planner_logic.js` L208). The C# engine must emit the same codes, and the same `message` strings if UI parity is required.
* Canonical JSON means recursively sorted keys, `JSON.stringify` with no whitespace, and UTF-8. This is identical to Python's `json.dumps(sort_keys=True, separators=(",",":"), ensure_ascii=False)`, and `verify_canonical.py` proves it for every golden package. In .NET, sort keys with ordinal comparison and do not escape non-ASCII characters (use `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, or a custom writer).
* All times are naive civil datetimes. They are rendered without a zone, and a fraction is included only when non-zero.
* When `planner_logic.js` changes, `index.json.plannerLogicSha256` changes, and `--check` fails until `expected/` is regenerated and reviewed.
