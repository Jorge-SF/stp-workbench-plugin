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
| `ITimelineRow.Blocks` | One `TimelineBlock(day, label, isCompleted, tag)` per day, sorted by day |
| `ITimelineRow.Move` | `Direct` = solid dark outline, `Cascade` = dashed orange outline (not on completed blocks) |
| `ITimelineRow.IsLocked` | Lock after the ID |
| `Links` | `FromKey` → `ToKey` precedence elbows, from the last block of one row to the first of the other |
| `Changed` | Raise after edits; the control re-reads everything (O(rows + blocks)) |

`TimelineModel` / `TimelineRow` / `TimelineLink` are ready-made mutable implementations.
`TimelineLabels.CompactQuantity` formats block labels like the mockup ("238", "1.7k").

Control properties: `DayWidth` (zoom, 8..240, two-way), `SelectedKey` (two-way), `ShowLinks`, `ShowCompleted`,
`GridWidth`. Events: `BlockClick`, `BlockDragStarted` (routed; row, block, date, position), `SelectedRowChanged`.
Methods: `ScrollIntoView(key)`, `ZoomAt(width, anchorX)`, `SetVerticalOffset`, `SetHorizontalOffset`, `UpdateNow`.

Input: wheel scrolls rows, Shift+wheel scrolls days, Ctrl+wheel zooms around the pointer, Up/Down move the
selection, click selects a row.

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
