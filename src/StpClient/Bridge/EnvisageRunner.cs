// Runs an STP entry script inside Vulcan (Envisage's Python) through IEnvisageInterface.ExecutePythonScript.
// See docs/envisage_python.md for what is verified about that call.
//
// Vulcan connection rules (Pit Design CLAUDE.md 5b):
//  - One script at a time: a second run is refused while one is running (TryBegin), never queued or stacked.
//  - Never stopped: TerminatePythonScript is not called anywhere. Scripts 1 and 3 change the VGS project, and a
//    script killed inside a Vulcan call hangs Vulcan. There is no Cancel; closing Workbench does not stop it.
//  - No Envisage, or Envisage still loading: nothing is sent and the caller shows a message.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Maptek.Vulcan.Envisage;

namespace Maptek.Vulcan.Stp.Bridge
{
    /// <summary>What a run ended with.</summary>
    internal sealed class RunOutcome
    {
        /// <summary>The script's exit status (0 = success); null when the call itself failed.</summary>
        public int? Status { get; set; }

        /// <summary>Why the call failed (exception text), or null.</summary>
        public string Error { get; set; }

        /// <summary>The launcher's log for this run.</summary>
        public string LogPath { get; set; }

        /// <summary>The last lines of that log (the traceback when the script raised).</summary>
        public string LogTail { get; set; }

        public bool Succeeded => Error == null && Status == 0;
    }

    internal static class EnvisageRunner
    {
        private static readonly object Gate = new object();
        private static string _running;

        /// <summary>The label of the script running now, or null.</summary>
        public static string Running
        {
            get { lock (Gate) return _running; }
        }

        /// <summary>Claims the single run slot; false (and who holds it) when a script is running.</summary>
        public static bool TryBegin(string label, out string current)
        {
            lock (Gate)
            {
                current = _running;
                if (_running != null) return false;
                _running = label;
                return true;
            }
        }

        public static void End()
        {
            lock (Gate) _running = null;
        }

        public static string RunFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maptek", "StpUG", "run");

        /// <summary>True when Envisage is there and has finished loading; any failure reading it counts as no.</summary>
        public static bool IsAvailable(IEnvisageInterface envisage)
        {
            if (envisage == null) return false;
            try
            {
                return envisage.EnvisageFullyLoaded;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Runs the script in Envisage on a worker thread. The caller must hold the slot (TryBegin) and releases it
        /// (End) when the task completes. The task never faults: failures come back in RunOutcome.Error.
        /// </summary>
        public static Task<RunOutcome> RunAsync(IEnvisageInterface envisage, StpScript script, string scriptsFolder)
        {
            string scriptPath = Path.Combine(scriptsFolder, script.FileName);
            string logPath = Path.Combine(RunFolder, script.Id + "_last_run.log");
            string launcher = Path.Combine(RunFolder, "stp_run_" + script.Id + "_" + Guid.NewGuid().ToString("N") + ".py");
            return Task.Run(() =>
            {
                var outcome = new RunOutcome { LogPath = logPath };
                try
                {
                    Directory.CreateDirectory(RunFolder);
                    DeleteOldLaunchers();
                    if (File.Exists(logPath)) File.Delete(logPath);
                    File.WriteAllText(launcher, BuildLauncher(scriptPath, StpScriptLocator.PythonRoot(scriptsFolder), logPath),
                                      new UTF8Encoding(false));
                    // Blocks until the script ends (docs/envisage_python.md section 2). Not cancellable on purpose.
                    outcome.Status = envisage.ExecutePythonScript(launcher, Guid.NewGuid().ToString());
                }
                catch (MissingMethodException ex)
                {
                    outcome.Error = "MissingMethodException: " + ex.Message;
                }
                catch (Exception ex)
                {
                    outcome.Error = ex.GetType().Name + ": " + ex.Message;
                }
                // The launcher stays: should the call return before Envisage has read it, deleting it would
                // break the run. Launchers older than a day go at the next run (DeleteOldLaunchers).
                outcome.LogTail = ReadTail(logPath, 20);
                return outcome;
            }, CancellationToken.None);
        }

        /// <summary>
        /// The launcher Envisage runs: sets STP_CORE_PATH and sys.argv, runs the entry script as __main__ (so its
        /// __file__ and "if __name__" work), logs a traceback, and exits with the script's status.
        /// </summary>
        public static string BuildLauncher(string scriptPath, string pythonRoot, string logPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# -*- coding: utf-8 -*-");
            sb.AppendLine("# STP UG launcher, written by the StpClient Workbench plugin. Safe to delete.");
            sb.AppendLine("import os, sys, runpy, traceback, datetime");
            sb.AppendLine("_script = " + PyString(scriptPath));
            sb.AppendLine("_root = " + (pythonRoot == null ? "None" : PyString(pythonRoot)));
            sb.AppendLine("_log = " + PyString(logPath));
            sb.AppendLine("_status = 0");
            sb.AppendLine("_detail = ''");
            sb.AppendLine("if _root:");
            sb.AppendLine("    os.environ['STP_CORE_PATH'] = _root");
            sb.AppendLine("_old_argv = list(getattr(sys, 'argv', []) or [])");
            sb.AppendLine("sys.argv = [_script]");
            sb.AppendLine("try:");
            sb.AppendLine("    runpy.run_path(_script, run_name='__main__')");
            sb.AppendLine("except SystemExit as _e:");
            sb.AppendLine("    if _e.code is None:");
            sb.AppendLine("        _status = 0");
            sb.AppendLine("    elif isinstance(_e.code, int):");
            sb.AppendLine("        _status = _e.code");
            sb.AppendLine("    else:");
            sb.AppendLine("        _status = 1");
            sb.AppendLine("        _detail = str(_e.code)");
            sb.AppendLine("except BaseException:");
            sb.AppendLine("    _status = 3");
            sb.AppendLine("    _detail = traceback.format_exc()");
            sb.AppendLine("finally:");
            sb.AppendLine("    sys.argv = _old_argv");
            sb.AppendLine("try:");
            sb.AppendLine("    with open(_log, 'w', encoding='utf-8') as _f:");
            sb.AppendLine("        _f.write('script: %s\\nfinished: %s\\nstatus: %s\\npython: %s\\n' % (");
            sb.AppendLine("            _script, datetime.datetime.now().isoformat(timespec='seconds'), _status, sys.version.split()[0]))");
            sb.AppendLine("        if _detail:");
            sb.AppendLine("            _f.write('\\n' + _detail)");
            sb.AppendLine("except Exception:");
            sb.AppendLine("    pass");
            sb.AppendLine("sys.exit(_status)");
            return sb.ToString();
        }

        /// <summary>A Python string literal for any text (no raw strings: a trailing backslash breaks them).</summary>
        public static string PyString(string s)
        {
            var sb = new StringBuilder("'");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    default:
                        if (c < 0x20) sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('\'').ToString();
        }

        private static string ReadTail(string path, int lines)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var all = File.ReadAllLines(path, Encoding.UTF8);
                return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void DeleteOldLaunchers()
        {
            foreach (var f in new DirectoryInfo(RunFolder).GetFiles("stp_run_*.py"))
            {
                try
                {
                    if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) f.Delete();
                }
                catch (Exception)
                {
                    // a leftover launcher in %LocalAppData% is harmless
                }
            }
        }
    }
}
