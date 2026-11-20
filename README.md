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
| `src/StpPlanner/` | Planner view model (`PlannerSession`): snapshot→engine mapping, rows/filters/counters/capacity, undo/redo, apply + refresh. No WPF |
| `src/*.Tests/` | xUnit tests (net48) |

## Building

Requirements: Visual Studio 2026 (MSBuild 18) with .NET Framework 4.8 targeting, and a Workbench runtime folder.

1. Get a Workbench runtime: run `tools\Setup-WorkbenchRuntime.ps1`, **or** copy
   `WorkbenchRuntimeOverride_Template.props` to `WorkbenchRuntimeOverride.props` (gitignored) and point it at an
   existing runtime folder. If that folder belongs to another project, also set
   `<CopyBinaryFilesToWorkbenchClientPlugins>false</CopyBinaryFilesToWorkbenchClientPlugins>` there so the build
   does not copy this plugin into it.
2. Build: double-click **`Build-Plugin.cmd`** (no Visual Studio needed), or run
   `powershell -ExecutionPolicy Bypass -File tools\Build-Plugin.ps1`. It:
   - finds MSBuild, checks the Workbench runtime, and stops if Vulcan/Workbench still holds the plugin DLLs;
   - builds the solution (Debug) and copies the plugin's own files to `Deploy\Client\`;
   - builds the installer `installer\dist\STP-UG-<version>.msi` from a separate Release build (needs WiX 5, once:
     `dotnet tool install --global wix --version 5.0.2`; keep version 5: WiX 6+ charges commercial users).

   | Option | What it does |
   |---|---|
   | `-Test` | Also run the C# tests, the Python smoke test and the golden check |
   | `-NoMsi` | Skip the installer (no WiX needed) |
   | `-Configuration Release` | Release build in `Deploy\Client\` |
   | `-Clean` | Rebuild from scratch |
   | `-Register` | Register `Deploy\Client` with the Workbench afterwards (admin, once per PC) |
   | `-Version x.y.z` | Installer/DLL version (default `<Major>.<Minor>.<commit count>`) |

   Same thing by hand: `MSBuild StpWorkbench.sln -restore -p:Configuration=Debug`.
   No Vulcan assembly is referenced: `StpClient` reaches Vulcan's `IEnvisageInterface` by name
   (`src/StpClient/Bridge/EnvisageProxy.cs`), so one build runs in every Vulcan 2024.2 or later.
3. Tests: `dotnet test src/StpEngine.Tests`, `dotnet test src/StpBridge.Tests` and `dotnet test src/StpPlanner.Tests`;
   `python -I python/tests/smoke_test.py`; `node tools/golden/run.mjs --check`.
4. Load in Vulcan: `tools\Register-PluginSearchPath.ps1` registers `Deploy\Client` (value name `StpUG.Dev`), then
   restart Vulcan. Close Vulcan before rebuilding: Workbench locks the DLLs.
   Vulcan then shows an **STP UG** menu (Preparar proyecto, Abrir planificador, Aplicar STP a VGS, Verificar
   guardado, Configuración y acerca de…). Each item runs `python/scripts/1..4` inside Vulcan
   (`docs/envisage_python.md`), one at a time. The scripts folder defaults to `<plugin>\Python\scripts`, else the
   repository's `python\scripts` found above `Deploy\Client`; change it in *Configuración y acerca de…*
   (stored in `%AppData%\Maptek\StpUG\settings.xml`). Run logs: `%LocalAppData%\Maptek\StpUG\run\`.

## Installing for users (MSI)

Hand out `installer\dist\STP-UG-<version>.msi`. Users close Vulcan, double-click it (admin prompt; the MSI is not
signed, so it says "Unknown publisher"), and start Vulcan: the **STP UG** menu appears. It installs to
`C:\Program Files\Maptek\STP UG` (plugin DLLs in `Client\`, the Python scripts in `Client\Python\`) and registers
`Client\` under `HKLM\SOFTWARE\Maptek\Workbench\Client\PluginSearchPaths` as `StpUG`. A higher version replaces the
installed one; uninstall from Apps & features (removes only its own registry value).

On a development PC, don't have both the MSI (`StpUG`) and the dev registration (`StpUG.Dev`) at once: Vulcan would
load the plugin twice. Remove the dev one with `tools\Register-PluginSearchPath.ps1 -Unregister`.

`nuget.config` limits package restore to nuget.org (the test frameworks are the only packages).
`StpBridge` references the Newtonsoft.Json 12 that the Workbench ships and does not copy it.
