# Running a Python script inside Vulcan from a Workbench plugin

Phase 1 research (2026-11-19). Sources: the installed Vulcan 2026.1 assemblies (read by reflection and by
decoding the IL of the methods involved), Vulcan 2020.1 / 2024.2 / 2026 for compatibility, the Workbench SDK
help (`WorkbenchSDK.chm`, decompiled), Maptek's VS templates (`WorkbenchTemplates.vsix`) and the Pit Design
plugin (`D:\14-Temp_Project\Pit_Design_repo\PIT_DESIGN\workbench_plugin`).

Labels: **[verified]** read from the binaries or from working code; **[inferred]** follows from the
binaries but has not run on this machine yet; **[unverified]** a guess until somebody tests it in Vulcan.

## 1. The interface

| What | Value | Status |
|---|---|---|
| Interface | `Maptek.Vulcan.Envisage.IEnvisageInterface` in `EnvisageInterfacesClient.dll` (`<Vulcan>\bin\workbench\Client\Plugins\`) | verified |
| Export name | `Maptek.Vulcan.Envisage.ClientAPI.EnvisageInterfaceName` = `"Maptek.Vulcan.Envisage.Interfaces.Envisage"` | verified |
| Exported by | `EnvisageClientPlugin.m_EnvisageInterface`, `[ExportInterface("Maptek.Vulcan.Envisage.Interfaces.Envisage", typeof(IEnvisageInterface))]`, **no API version** | verified |
| Import | `[ImportInterface(ClientAPI.EnvisageInterfaceName, WeakImport = true)] public IEnvisageInterface Envisage { get; set; }` | inferred (attribute and `WeakImport` property verified in `DependencyInjection.dll`) |
| Assembly identity | `EnvisageInterfacesClient, Version=19.1.0.215, PublicKeyToken=null` (Vulcan 2026.1). Not strong-named, so the CLR binds by simple name and a plugin built against 2026.1 loads the copy of any Vulcan version | verified (identity); inferred (binding) |
| Not in the SDK | The Workbench SDK has no Vulcan assemblies. StpClient references the installed Vulcan's copy (`Private=false`, never shipped; property `VulcanClientPlugins`) | verified |

`WeakImport = true` lets the plugin load in a Workbench without Vulcan; the property then stays `null` and
the commands say Vulcan is not available.

Members useful to us (all verified by reflection):

```csharp
bool EnvisageFullyLoaded { get; }       // also EnvisageMenuLoaded/Visible/Enabled, EnvisageAppSelected
int  ExecutePythonScript(string pythonScript, string instanceId);
void TerminatePythonScript(string instanceId);
void RunPythonScript(Moniker script, RunExecutableContext context);
string VulcanInstallPath();
```

### Version differences

| Vulcan | `ExecutePythonScript` | `RunPythonScript` | `TerminatePythonScript` |
|---|---|---|---|
| 2020.1 | `int (string)` | `void (Moniker)` | absent |
| 2024.2, 2026, 2026.1 | `int (string, string)` | `void (Moniker, RunExecutableContext)` | present |

A plugin compiled against 2026.1 throws `MissingMethodException` on 2020.1. StpClient catches it and says
which Vulcan is needed. Supported: **Vulcan 2024.2 and later** [verified signatures; behaviour unverified].

## 2. `ExecutePythonScript(path, instanceId)`: what it does

Client side, `EnvisageClientInterface.ExecutePythonScript` → `EnvisageClientPlugin.ExecutePythonScript` →
the server service `IEnvisageServerInterfaceService.ExecutePythonScript` (WCF, `"Maptek.Vulcan.Envisage.Services.Envisage"`)
→ `UEnvisageManagerProxy.ExecutePythonScript` (unmanaged Envisage). [verified call chain]

The only Maptek caller is the Workflow "Python script" process (`Maptek.Vulcan.Envisage.PythonProcess.Evaluate`).
Its IL shows: [verified]

1. It writes a **wrapper `.py` to a temp file** (`Path.GetTempFileName()` renamed to `.py`): `exec(open(r"<script>", encoding="UTF-8").read())`
   inside `try/except`, catches `SystemExit` into `_exit_status`, writes `locals()` to an output file, ends with
   `sys.exit(_exit_status)`.
2. It sets `m_RunningPythonInstanceId = Guid.NewGuid().ToString()`.
3. It calls `ExecutePythonScript(<wrapper path>, <guid>)` and keeps the `int`.
4. Right after the call it reads the output file and, if the `int` is non-zero, reports
   "Process exited with a non-zero status of {0}".
5. `ProcessReset()` (the workflow's stop) calls `TerminatePythonScript(<guid>)` while the id is set.

Therefore:

- **The first argument is a path to a `.py` file**, not source text. [inferred, strongly: the local passed is the wrapper's path]
- **The call blocks until the script ends**, and **returns the script's exit status** (0 = success). [inferred, strongly: the output file is read immediately after]
- **`instanceId` is any unique string** (a GUID); it is the handle for `TerminatePythonScript`. [verified usage]
- The script runs in **Envisage's own Python** (the one Vulcan's File ▸ Python menu uses), so `maptek.vulcan_gui`
  panels and `maptek.gantt` work as when the user runs the script by hand. [unverified until tested in Vulcan]
- Which thread the workflow calls it from is unknown; StpClient calls it from a worker thread (`Task.Run`)
  so the Workbench UI stays responsive. [unverified that a worker thread is accepted]
- `__file__` and `__name__`: the Maptek wrapper `exec`s the file text, so it cannot rely on `__file__`. Ours
  runs `runpy.run_path(script, run_name="__main__")`, which sets both. [verified Python semantics]

## 3. `RunPythonScript(moniker, context)`: what it does

Decoded IL of `EnvisageClientPlugin.RunPythonScript(object, RunExecutableContext)`: [verified]

```
msg = "FILES_PYTHON_SCRIPT:" + <first moniker path>
if context != null:
    msg += (context.Properties.RunMode == 1 /*NativeWindow*/) ? ";IN_SHELL" : ";INTEGRATED"
    msg += context.Properties.CloseWindowOnCompletion ? ";CLOSE_ON_COMPLETE" : ";HOLD_ON_COMPLETE"
    msg += ";" + context.CommandLine
ServerInterface.SendEnvisMessage(msg)      // void: fire and forget
```

`RunMode` (ExecutableManagerInterfaces): `NoWindow=0, NativeWindow=1, IntegratedWindow=2`. This is the same
message Envisage gets when a `.py` is run from the Workbench file explorer. It **returns at once** and gives no
exit status and no handle, so we could not keep "one call at a time" with it. Not used. Also needs `Moniker`
(DataManager/FileSupport) and `RunExecutableContext` types at compile time.

## 4. Choice for StpClient (Phase 1)

`ExecutePythonScript`, through `Bridge/EnvisageRunner.cs`:

1. `Envisage == null` or `!Envisage.EnvisageFullyLoaded` → message box "Vulcan no está disponible", nothing runs.
2. One script at a time (a process-wide gate). A second click while one runs gets a message, never a second call.
3. It writes a launcher to `%LOCALAPPDATA%\Maptek\StpUG\run\` that sets `STP_CORE_PATH`, `sys.argv`,
   runs the script with `runpy`, writes the status and traceback (if any) to `<script>_last_run.log` and exits
   with the status. Launchers are kept (in case the call returns before Envisage reads the file) and deleted
   after a day. Checked outside Vulcan with Vulcan's python 3.11: script 4 runs as `__main__` and finds
   `stp_core`; a raising script gives status 3 and its traceback in the log; paths with spaces and `'` work.
4. Calls `ExecutePythonScript(launcher, guid)` on a worker thread and shows a message on a non-zero status
   (success shows nothing: the scripts show their own Vulcan messages).
5. **Never calls `TerminatePythonScript`**: scripts 1 and 3 write to VGS, and killing a script inside a
   Vulcan call hangs Vulcan (Pit Design `CLAUDE.md` §5b rule 3). There is no Cancel. Closing Workbench does
   not stop a running script either.

## 5. Other things checked

- **Pit Design** never uses `IEnvisageInterface`: it starts Vulcan's `python.exe` as a separate process and
  talks to Vulcan through `maptek.vulcan_gui`'s link (`Bridge/PythonBridge.cs`, `BridgeSettings.VulcanPythons`).
  That route remains the fallback if `ExecutePythonScript` turns out not to work. Whether `maptek.gantt` works
  from such a standalone process is still unknown (design doc §7.2).
- **The SDK help** documents nothing Vulcan-specific: `ExecutePythonScript` is not in it. [verified by search]
- **Toolbars:** `<Toolbar Name=… MenuSegmentName=… Title=… InitialTrayName="Maptek.Workbench.MainToolbar"
  GroupName="Maptek.Workbench.MainToolbarGroup" VisualizeForOwnerWhenAdded="true"/>` in `Menus.xml`, from
  Maptek's `ExampleApp` template. Whether it shows inside the Vulcan window (Envisage draws its own toolbars)
  is [unverified].

## 6. To test in Vulcan (closes the open points)

1. STP UG ▸ Verificar guardado: confirms the path argument, the blocking call and the exit status (script 4
   imports nothing from Maptek).
2. STP UG ▸ Preparar proyecto: confirms `vulcan_gui` panels and `maptek.gantt` work in this mode.
3. Click a second item while one runs: the second must be refused with a message.
4. Look for the STP UG toolbar in the Vulcan window and in the Workbench main toolbar tray.
