# Planner view model (`src/StpPlanner`)

`Maptek.Vulcan.Stp.Planner.PlannerSession` is the state the planner screen (`docs/mockups/planner_layout.html`) binds
to. It is a net48 class library with no WPF or Workbench references. It implements `INotifyPropertyChanged` and must
be called from the UI thread. Its async commands resume on the caller's context.

## Opening

```csharp
var plugin = PluginSession.Read(PluginSession.DefaultPath);           // written by script 2 in plugin mode
var storage = new FileHorizonStorage(Path.Combine(prefsFolder, "horizon.json"));
using (var session = PlannerSession.Open(plugin, storage, new PlannerOptions()))
{
    // bind the view to session
}
```

`Open` throws `EngineException` when the snapshot breaks a planner rule. Show its message.

## Mapping (`SnapshotMapper`)

* `SnapshotV2` to `PlannerSnapshot`. Times are rendered with `PyDateTime.SnapshotIso`, so the engine sees exactly
  Python's text. Decimals use `PyDecimal.ToString()` (`str(Decimal)`) and lags the integer microsecond text.
* Engine `ApplyPackage` to wire `ApplyPackage`. Its `ToCanonicalBytes()` are byte-equal to the bytes the bridge stores
  and hashes (Python `isoformat()`, KI-1). The tests check this against `python_vectors.json` `goldenNormalized`.

## What the mockup areas bind to

| Mockup area | Members |
|---|---|
| 1 Datos | `RefreshFromVgsAsync(confirm)`, `CanRefresh`, `IsLive`, `ConnectionText` |
| Plan | `Undo()`, `Redo()`, `Discard()`, `CanUndo`/`CanRedo`/`CanDiscard`, `UndoDescription`/`RedoDescription` |
| VGS | `ApplyToVgsAsync()`, `CanApply`, `ApplyBlockedReason` (tooltip), `IsPackageReady`, `LastReceipt`, `BuildApplyPackage()` |
| Vista | `ShowPrecedences`, `ShowCompleted` |
| 2 Filters | `RangeStart`/`RangeFinish`/`DurationDays` + `SetRangeStart/SetRangeFinish/SetDuration`, `CanEditRange`, `RangeLockedReason`, `SaveRangePreference()`; `TypeChips` + `ToggleType`; `Grouping`; `Sort`, `SortAttribute`, `SortDescending`, `Attributes`; `SearchText` |
| 3 Resumen | `Counters` (`Activities`, `WithPending`, `Moved`, `Cascaded`, `HasChanges`) |
| 4 Grid + timeline | `Groups` (header label and count, plus rows) or `Rows`; `PlannerRow.Segments` (day index, left/width ratio, status, amount, tail eligibility); `Days` (header labels, weekends) |
| 5 Capacidad | `Capacity` (per type, per day: value, objective, `IsOver`), `SetTypeObjective` |
| 6 Selected activity | `SelectedActivityId`, `Selected` (dates, shift pill, quantity, resource, predecessors/successors, `StatusText` explaining direct/cascade, ±day eligibility) |
| 8 Status bar | `SnapshotText`, `PolicyText`, `PendingChangesText`, `PlanTotalText`, `StatusMessage`/`StatusIsError`, `ConflictNotice` |

Display fields for the screen: `TypeColors`/`TypeColor(type)` (`TypePalette`: the mockup colours for its types, then a
fixed palette by name), `TypeChip.Color`/`Name`, `CapacityRow.Color`/`ObjectiveText`, `CapacityCell.Text` (`1.7k`, `·`),
`HorizonDay.DayOfMonth`, and `PlannerProjection.CompactQuantity`. The MVVM helpers of the whole planner are here too
(`Mvvm.cs`: `ObservableObject`, `RelayCommand`, no WPF types).

In the plugin, `src/StpClient/Views/Planner/PlannerScreenViewModel.cs` maps this session to the screen
(`IPlannerViewModel`) and `PlannerTimelineModel.cs` maps its groups, rows, day segments and precedences to the
timeline control (`StpTimeline.ITimelineModel`).

Plan commands return a `PlannerOutcome`. `Message` is the Spanish text to show (timeline_logic.js
`humanPlannerOutcome`), `Code` is the engine outcome code and `Detail` is the engine's own message. The commands are
`MoveActivity`, `MoveSelected`, `MoveDailyTail`, `DeferDailyTail`, `SplitAndStagger` and the range edits.

## Rules worth knowing

* **Undo/redo:** a history state is a seed horizon plus the list of accepted operations. Undo and redo rebuild a
  `CivilHorizonController` from the snapshot and replay the list. The engine is pure, so the replay is exact; the golden
  tests undo and redo every case to confirm this. Range edits and Discard are undoable. Saving the preference is not a
  plan step.
* **Rows** are activities with at least one visible segment, as in the browser's `buildTimelineView`. Counters are
  taken before the chip and search filters, as in the browser metrics. Capacity respects the chips.
* **Grid columns:** by default the type comes only from `Activity Type`, as in the browser. `PlannerOptions` can add
  others, for example `Type`, which the golden fixtures use. Level comes from `Nivel`/`Level`, rate from `Rate` (per
  civil day) and unit from `Unit`/`Unidad`.
* **Apply** is disabled while busy, when the snapshot is stale (after a 409), with no local moves, with blocking
  inherited conflicts (`block-inherited`) or when not live. After a successful save, any plan change clears
  `IsPackageReady` and shows "El plan cambió; prepare y valide de nuevo el paquete V2.".
* **Bridge errors** map to Spanish messages in `PlannerMessages.ApplyError`/`RefreshError`. A failed refresh keeps the
  local plan and its history.

## Not yet covered

The resource override selector (manual resources), the reports, charts, actual-progress and print/Excel
tabs (v1_compat_logic.js), drag previews and capacity warnings (`previewExcesses`), persisted visual preferences
(`vgs-stp-v2:visual:`), and `precedenceLines` geometry.
