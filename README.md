# STP Workbench Plugin

Short Term Planner (STP) for the Vulcan Gantt Scheduler (VGS), packaged as a Maptek Workbench plugin.

See `docs/STP_Workbench_Plugin_Design.md` for the architecture and phase plan.

## Layout

| Path | What |
|---|---|
| `python/` | STP Python core (`stp_core`), entry scripts 1–4, bundle tools, smoke test |
| `reference/web/` | Original HTML/JS planner, the reference for the C# port (`FEATURES.md` maps it) |
| `tools/golden/` | Node harness that replays the JS planner logic and records golden outputs |
| `tools/*.ps1` | Workbench runtime setup and plugin search-path registration |
| `StpWorkbench.sln` | C# solution |
| `src/StpClient/` | Workbench client plugin (.NET Framework 4.8, WPF): menus, commands, views |
| `src/StpEngine/` | Planning-rules engine, C# port of `planner_logic.js`. No Workbench/WPF/JSON dependencies |
| `src/StpBridge/` | Wire models, canonical JSON + SHA-256, HTTP client for the Python loopback server |
| `src/*.Tests/` | xUnit tests (net48) |

## Building

Requirements: Visual Studio 2026 (MSBuild 18) with .NET Framework 4.8 targeting, and a Workbench runtime folder.

1. Get a Workbench runtime: run `tools\Setup-WorkbenchRuntime.ps1`, **or** copy
   `WorkbenchRuntimeOverride_Template.props` to `WorkbenchRuntimeOverride.props` (gitignored) and point it at an
   existing runtime folder. If that folder belongs to another project, also set
   `<CopyBinaryFilesToWorkbenchClientPlugins>false</CopyBinaryFilesToWorkbenchClientPlugins>` there so the build
   does not copy this plugin into it.
2. Build: `MSBuild StpWorkbench.sln -restore -p:Configuration=Debug`
   (the plugin's own files land in `Deploy\Client\`).
3. Tests: `dotnet test src/StpEngine.Tests` and `dotnet test src/StpBridge.Tests`;
   `python -I python/tests/smoke_test.py`; `node tools/golden/run.mjs --check`.
4. Load in Vulcan: `tools\Register-PluginSearchPath.ps1` registers `Deploy\Client` (value name `StpUG`), then
   restart Vulcan. Close Vulcan before rebuilding: Workbench locks the DLLs.

`nuget.config` limits package restore to nuget.org (the test frameworks are the only packages).
`StpBridge` references the Newtonsoft.Json 12 that the Workbench ships and does not copy it.
