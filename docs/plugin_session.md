# `plugin_session.json`: hand-over from script 2 to the Workbench plugin

In **plugin mode**, script 2 (`python/scripts/2. VGS_ShortTermPlanner.py`) does what it always did up to the
bridge: it exports the snapshot, writes `snapshot_v2.json` and starts the read-only loopback bridge. Then it
does **not** render `index.html` and does **not** open a browser. It writes `plugin_session.json` instead, and
the C# planner view reads it to find the snapshot and talk to the bridge.

Browser mode (the default) is unchanged.

## Selecting plugin mode

The first of these that applies wins:

| How | Example | Notes |
|---|---|---|
| Entry script `2p` | run `python/scripts/2p. VGS_ShortTermPlanner_Plugin.py` | **Recommended for the plugin.** Needs no arguments or environment, so it works with Vulcan's script runner whatever it passes. |
| Command-line argument | `"2. VGS_ShortTermPlanner.py" --plugin-session` | Optional value: `--plugin-session <path>` or `--plugin-session=<path>`. |
| Environment, explicit path | `STP_PLUGIN_SESSION=plugin_session.json` | Also honoured by `2p`. |
| Environment, flag | `STP_PLUGIN_MODE=1` (`true`, `yes`, `on`) | Default path. |
| Python call | `run_planner(plugin_session=True)` or `run_planner(plugin_session=<path>)` | `None` (the default) is browser mode. |

Vulcan's embedded Python may not give a usable `sys.argv`, and environment variables have to be set before
Vulcan starts, because the Envisage process inherits its environment then. So the plugin should run the `2p`
script. The argument and the variables are there for manual runs and tests.

`<path>` is optional. A relative path resolves against the runtime folder. Any path must name a `.json` file
**directly inside** the runtime folder; anything else raises `ValueError` before VGS is touched.

## Location

```
<folder of script 2>/.stp_ug_v2_runtime/plugin_session.json                       (default)
<folder of script 2>/.stp_ug_v2_runtime/short_term_planner_v2/snapshot_v2.json   (snapshot)
<folder of script 2>/.stp_ug_v2_runtime/short_term_planner_v2/.bridge/...        (bridge runtime, lease, ready files)
```

The script prints one line on stdout, `STP_PLUGIN_SESSION=<absolute path>`, and `run_planner` returns the
same path.

## Format

UTF-8 **without BOM**, compact JSON (`,` and `:` separators), keys sorted, non-ASCII kept (the `message` is
Spanish). The top level has exactly two keys:

```json
{
  "bridge_config": {
    "bridgeId": "9f0c…32 hex",
    "endpoint": "http://127.0.0.1:53817",
    "message": "Actualización en vivo disponible.",
    "mode": "live",
    "precedenceConflictPolicy": "allow-inherited",
    "projectKey": "3b1e…64 hex",
    "token": "…at least 32 URL-safe characters…"
  },
  "snapshot_path": "C:\\…\\.stp_ug_v2_runtime\\short_term_planner_v2\\snapshot_v2.json"
}
```

| Field | Meaning |
|---|---|
| `snapshot_path` | Absolute path of the `vgs-stp-snapshot/2` JSON that the bridge serves. It is the same data the HTML gets in `__VGS_STP_V2_SNAPSHOT__`. |
| `bridge_config` | **Exactly** what the HTML gets in `__VGS_STP_V2_BRIDGE__`: `BridgeDescriptor.page_config()` plus `precedenceConflictPolicy`, built by the same `planner_bridge_config()` function. |
| `bridge_config.mode` | `"live"`, or `"static"` if the bridge could not start. In static mode `endpoint` and `token` are `""` and `bridgeId` may be `""`. The plugin should then say so and disable refresh, apply and Excel export (design doc §4.4). |
| `bridge_config.endpoint` | `http://127.0.0.1:<port>`. Send `X-STP-V2-Token: <token>` on every request. |
| `bridge_config.precedenceConflictPolicy` | `"allow-inherited"` (script 2 default) or `"block-inherited"`. |

Readers should ignore unknown keys inside `bridge_config`, so the HTML config can grow without breaking them.

## Atomicity and lifetime

1. At the start of every plugin-mode run, script 2 **deletes** any existing session file before it touches VGS.
2. It writes `snapshot_v2.json`, starts (or reuses) the bridge, and only then writes `plugin_session.json`
   atomically with `stp_v2_windows_atomic.atomic_replace_bytes_windows`: a protected temporary sibling, a
   flush, and one rename. If the file exists, it is complete and belongs to the latest run.
3. Temporary and backup siblings start with `.plugin_session.json.`. Readers must open only the exact name.
4. The session is valid while that bridge is alive. A new run of script 2 takes the lease, and the old bridge
   exits. If the endpoint stops answering, or `GET /snapshot` returns 409 (stale), run script 2 again.

**Who deletes it:** the plugin should delete the file once it has read it and has checked
`GET /health` with the token. Script 2 deletes and replaces it on the next plugin run anyway. Browser mode
never reads, writes or deletes it.

## Security notes

- **The file holds the bridge token.** Anyone who can read it can make the bridge validate and save apply
  packages for that project while the bridge runs. Treat it like the bridge's own `runtime-*.json`, which
  already holds the same token in `.bridge/`.
- It must stay in the per-user runtime folder (`.stp_ug_v2_runtime`, next to the script). Script 2 refuses any
  other location, symlinked session files and dot-names. Keep the scripts in a folder only the user can
  write, not a shared network folder.
- Do not log the file's contents, copy it elsewhere, or attach it to bug reports. `.stp_ug_v2_runtime/` is
  gitignored, so it is never committed.
- The bridge binds `127.0.0.1` only and rejects browser `Origin`s (see design doc §4.4).
  `HttpClient` sends no `Origin`.
