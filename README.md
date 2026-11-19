# STP Workbench Plugin

Short Term Planner (STP) for the Vulcan Gantt Scheduler (VGS), packaged as a Maptek Workbench plugin.

See `docs/STP_Workbench_Plugin_Design.md` for the architecture and phase plan.

## Layout (planned)

- `python/` – STP Python core (extracted from the bundled VGS scripts)
- `reference/web/` – original HTML/JS planner, kept as the reference for the C# port
- `src/` – C# Workbench plugin (Phase 1+)
