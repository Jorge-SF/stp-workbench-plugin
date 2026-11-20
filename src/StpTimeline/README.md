# StpTimeline

The planner's timeline (Gantt) control, as specified by `docs/mockups/planner_layout.html`. Plain WPF, net48, no
Workbench, engine or bridge references. `src/StpTimeline.Preview` shows it with fake data.

## Feeding it

Set `TimelineView.Model` to an `ITimelineModel` (`Model/ITimelineModel.cs`):

| Member | Meaning |
|---|---|
| `StartDate`, `DayCount` | Day columns (day index 0 = `StartDate`) |
| `CompletedThrough` | "completado hasta" dashed line; null hides it |
| `Rows` | Display order, group headers included (`TimelineRowKind.Group`, shows `Name (GroupCount)`) |
| `ITimelineRow.Blocks` | One `TimelineBlock(day, label, isCompleted, tag, toolTip, isCapacityExempt)` per day, sorted by day |
| `ITimelineRow.Move` | `Direct` = solid dark outline, `Cascade` = dashed orange outline (not on completed blocks) |
| `ITimelineRow.IsLocked` | Lock after the ID |
| `Links` | `FromKey` → `ToKey` precedence elbows, from the last block of one row to the first of the other |
| `Changed` | Raise after edits; the control re-reads everything (O(rows + blocks)) |

`TimelineModel` / `TimelineRow` / `TimelineLink` are ready-made mutable implementations.
`TimelineBlock.ToolTip` (optional) is the hover detail line, e.g. the full quantity and the fragment's times.
`TimelineLabels.CompactQuantity` formats block labels like the mockup ("238", "1.7k").

Control properties: `DayWidth` (zoom, 8..240, two-way), `SelectedKey` (two-way), `ShowLinks`, `ShowCompleted`,
`GridWidth`, `IsEditable`, `EditCommand`. Events: `BlockClick`, `BlockDragStarted` (routed; row, block, date,
position), `SelectedRowChanged`, and the edit requests below.
Methods: `ScrollIntoView(key)`, `ZoomAt(width, anchorX)`, `SetVerticalOffset`, `SetHorizontalOffset`, `UpdateNow`.

Input: wheel scrolls rows, Shift+wheel scrolls days, Ctrl+wheel zooms around the pointer, arrows move the
selection, click selects a row (and focuses the clicked block).

## Editing

The control never edits the model. It raises a request (routed event, plus the optional `EditCommand` with the same
`TimelineEditEventArgs`), the host asks the engine (`PlannerSession`), then refreshes the model, or calls
`e.Reject(reason)` so the control flashes the blocks red and shows the reason. A rejection that arrives later
(async) uses `ShowBlocked(key, day, reason)`. `IsEditable = false` (host busy) turns editing off.

| Input | Request | Planner call |
|---|---|---|
| Drag a day block (snapped to days, ghost + "+3 días · dom 29/11" badge) | `MoveRequested`, `Scope = Tail`, `DayDelta`, `Block.Tag`, `TargetDate` | `MoveDailyTail(activity, fragment, targetDate)` |
| Shift+drag (Shift can be toggled mid-drag) | `MoveRequested`, `Scope = Activity` | `MoveActivity(activity, days)` |
| Escape during a drag | nothing (cancelled) | |
| Ctrl+Left/Right (focused block / no block) | `MoveRequested` ±1, `Tail` / `Activity`, `Origin = Keyboard` | as above |
| Ctrl+Shift+Left/Right | `MoveRequested` ±1, `Activity` | `MoveActivity` |
| Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z) | `UndoRequested`, `RedoRequested` | host undo stack |
| Right-click a pending block, Shift+F10, menu key | context menu, see below | |

Context menu (the browser's labels): "Mover desde este día: −1 / +1 día", "Mover actividad: −1 / +1 día",
"Asignar equipo" (`AssignResourceRequested`), "Exentar Límite" / "Quitar exención" (`CapacityExemptionRequested`;
hidden until the host shows it in `BlockMenuOpening`), "Mandar al siguiente periodo" (`DeferRequested` →
`DeferDailyTail`), "Dividir y desfasar" (popover "N.º de divisiones" / "Días de desfase", integers ≥ 1, then
`StaggerRequested` with `Divisions`, `GapDays` → `SplitAndStagger`). `BlockMenuOpening` can hide, disable or relabel
entries. Move entries that would leave the horizon are disabled.

Rejected locally, without a request: locked completed work (completed blocks or `IsLocked` rows can't be dragged,
show a not-allowed cursor, get no menu, like the browser) and keyboard moves past the horizon edge. Arrows move the
selection: Up/Down change the activity (the focus ring jumps to its nearest block), Left/Right the focused block.
Hovering a block shows activity, date, quantity (or `ToolTip`) and state.

**Capacity preview while dragging** (timeline_logic.js `previewExcesses` / `previewDailyTail` warnings). During a
plain drag or Shift+drag the control raises `DragPreviewChanged` when the target day or the scope changes, and once
more with `IsActive = false` when the drag ends (drop, Escape, lost capture; before `MoveRequested`, so a refused move
leaves no preview). The host previews the move without applying it (`PlannerSession.PreviewMoveDailyTail` /
`PreviewMoveActivity`) and calls `e.SetWarnings(days, text)`, or later `SetDragWarnings(days, text)`. Ghosts on those
days are drawn like the browser's `.drag-preview-warning` (#ffe2df fill, dashed #b42318), and the badge turns red with
the text ("Capacidad excedida para Rezagado."). StpClient also shows the candidate's capacity cells in the Capacidad tab,
with the newly exceeded days red and outlined.

**Capacity-exempt days** ("Exentar Límite"): `TimelineBlock(..., isCapacityExempt: true)` draws the block green
(#68bd79, the browser's `--exempt-color` / `.segment.capacity-exempt`) instead of the type colour; completed work stays
hatched. The host rebuilds the block without the flag after "Quitar exención", which restores the type colour.

A drag redraws only the overlay layer (one activity), so it costs the same with 2,000 rows: `--bench` measures a
median of ≈0.03 ms per drag frame. Pure math (hit test, pixel → day delta, snapping, what a move carries, the lock,
keyboard navigation) is in `Layout/TimelineEditMath.cs`, unit-tested.

**Try it in the preview:** `StpTimeline.Preview.exe` (or `--data stress`). A fake planner (`FakePlanner.cs`)
applies the requests with simplified rules (horizon, completed work, predecessors, a cascade that pushes successors,
undo/redo) and rejects others, so the red flash can be seen: e.g. select REZ-1425-01 and press Ctrl+Shift+Left.
`--demo drag|drag-activity|blocked|menu|tooltip|stagger --screenshot x.png` stages a state for screenshots.

## Why DrawingVisual (not SkiaSharp)

- **No dependency.** SkiaSharp ships with the Workbench, but referencing it would tie this library to the
  Workbench's SkiaSharp version and its native `libSkiaSharp.dll`, and the preview/tests would need it too.
- **Virtualization makes raw speed irrelevant.** Only the rows and days in the viewport are drawn, so a frame costs
  the same for 20 or 2,000 activities. Measured with the preview's `--bench` on 2,000 activities × 90 days,
  1384×664 viewport: vertical scroll median ≈3 ms, horizontal ≈0.5 ms, zoom ≈1 ms, all 90 days visible ≈2 ms.
- **WPF text and DPI for free**: ClearType, per-monitor DPI, same fonts as the rest of the planner UI.
- **Retained layers**: background, links, blocks and front (header, completed line) are separate `DrawingVisual`s,
  so selection changes or the links toggle redraw one layer; geometry for links and grid lines is batched into one
  `StreamGeometry` per layer; `FormattedText` is cached between frames.

Layout math (row tops, row/day virtualization, day ↔ x, zoom anchor) is in `Layout/TimelineLayout.cs` with no WPF
types, and is unit-tested in `src/StpTimeline.Tests`.
