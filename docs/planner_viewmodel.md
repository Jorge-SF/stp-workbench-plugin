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

Equipment and per-day local settings (the browser's compatibility preferences, never sent to VGS or the apply
package), saved per project under `stp-plugin:overrides:<projectKey>` in the horizon storage:

* `ResourceOptions` (names) / `ResourceDescriptors` (ids `vgs:<name>` / `manual:<n>`): the VGS resources, sorted,
  then the manual ones. `ManualResources`, `AddManualResource(assignToActivityId)` ("+ Recurso manual"),
  `RenameManualResource(id, name)`, `RemoveManualResource(id)`: the rules of `normalizeManualResources` and the
  template's add/rename/remove (`ManualResources`, golden-tested in `tools/golden/compat`). Names are unique,
  case-insensitively, among VGS and manual resources; a removed one takes its assignments with it.
* `AssignResource(logicalUnitId, resource)` ("Asignar equipo", one day) and `AssignActivityResource(activityId,
  resource)` (the Recurso selector, the whole activity). A day's equipment is its own assignment, else the activity's,
  else the VGS resource (JS `effectiveResourceFor`). Assignments are stored by name and follow a rename.
* These equipment edits are undoable plan steps (`ResourceState` in each history state); they never change the plan.
* `SetCapacityExemption(logicalUnitId, exempt)` ("Exentar Límite"; only for activities with a VGS type,
  `IsCapacityExemptible`), not undoable.

`DaySegment` carries `LogicalUnitId`, `Resource`, `HasResourceOverride` and `IsCapacityExempt`; capacity `IsOver`
ignores exempt days (`CapacityCell.CapacityValue`). `ActivityDetails.Resource` is the activity's equipment and
`VgsResource` its VGS one.
In the plugin, `PlannerEditRouter` maps every timeline edit request to these and the plan commands below.

Plan commands return a `PlannerOutcome`. `Message` is the Spanish text to show (timeline_logic.js
`humanPlannerOutcome`), `Code` is the engine outcome code and `Detail` is the engine's own message. The commands are
`MoveActivity`, `MoveSelected`, `MoveDailyTail`, `DeferDailyTail`, `SplitAndStagger` and the range edits.

## Rules worth knowing

* **Undo/redo:** a history state is a seed horizon plus the list of accepted operations. Undo and redo rebuild a
  `CivilHorizonController` from the snapshot and replay the list. The engine is pure, so the replay is exact; the golden
  tests undo and redo every case to confirm this. Range edits, Discard and the equipment edits (manual resources,
  assignments) are undoable; an equipment-only step restores its `ResourceState` without a replay. Saving the
  preference is not a plan step.
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

Frentes per day and per resource (`dailyFronts`, `resourceCapacities`), resource colours, the reports tab, actual
progress in the print/Excel/STP suelto exports (v1_compat_logic.js), persisted visual preferences
(`vgs-stp-v2:visual:`), and `precedenceLines` geometry.

## Avance real

`PlannerSession.ShowActualProgress` is the mode toggle (Vista → "Avance real"). It starts on when the project already
has values or added boxes. The rules are the C# port in `StpEngine/ActualProgress.cs`, golden-tested against
`v1_compat_logic.js`.

* **State:** `ActualProgressState` holds values per activity day, explicit boxes and hidden boxes. It is local only:
  stored as `stp-plugin:actual-progress:<projectKey>` in the browser's `standaloneLocalState` field names, and never
  sent to VGS.
* **Commands:** `SetActualProgress`, `AddActualProgress`, `RemoveActualProgress` and `MoveActualProgress` (a box drag;
  forward drags carry the later boxes along). `PreviewActualProgressMove` shows what a drag would move. Each is a plan
  step, so Deshacer/Rehacer cover it. A plan move does not change the values, and Descartar keeps them, as in the
  browser.
* **View:** `ActualProgressBoxes` holds the timeline boxes per row. `ActualProgressRows` is the "Avance real" tab
  (`real/plan` per day, red under plan, `Real: x / Plan: y`). `ActualProgressSummary` feeds the "Gráficos" charts.
  An activity with a visible added box stays on the timeline even when it has no plan in the range.
* **Legacy migration:** fragment-keyed values from old standalone pages are migrated once, on load.

