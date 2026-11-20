// STP UG - Workbench client plugin.
// Adds an "STP UG" menu to the Vulcan (Envisage) menu bar and an STP UG toolbar. Its items run the STP entry
// scripts (python/scripts/1..4, 2p) inside Vulcan through IEnvisageInterface.ExecutePythonScript (Bridge/EnvisageRunner,
// Bridge/EnvisageProxy, docs/envisage_python.md), one at a time, open the settings/about window, and open the
// planner as a Workbench document: script 2p writes plugin_session.json (docs/plugin_session.md), which becomes a
// StpPlanner.PlannerSession shown by Views/Planner.

using System;
using System.IO;
using System.Windows;
using Maptek.Libraries.DependencyInjection;
using Maptek.Vulcan.Stp.Bridge;
using Maptek.Vulcan.Stp.Planner;
using Maptek.Vulcan.Stp.Views;
using Maptek.Vulcan.Stp.Views.Planner;
using Maptek.Workbench;
using Maptek.Workbench.Client;
using Res = Maptek.Vulcan.Stp.Resources.Resources;

namespace Maptek.Vulcan.Stp
{
    [ClientPlugin(
        "Maptek.Vulcan.Stp.ClientPlugin",
        Workbench.API.Version,
        Version = AssemblyVersionInfo.Version,
        VersionLabel = AssemblyVersionInfo.VersionLabel,
        PackageName = "Maptek.Vulcan.Stp")]
    class StpClientPlugin : WorkbenchPluginBase
    {
        #region Services and interfaces

#pragma warning disable 0649, 0169   // Assigned by the Workbench via dependency injection
        /// <summary>
        /// Vulcan's IEnvisageInterface, imported as object so it binds in every Vulcan version (see EnvisageProxy).
        /// Weak: the plugin also loads in a Workbench without Vulcan (then null).
        /// </summary>
        [ImportInterface(EnvisageProxy.InterfaceName, WeakImport = true)]
        public object EnvisageObject { get; set; }
#pragma warning restore 0649, 0169

        private PlannerDocumentViewModel _planner;
        private EnvisageProxy _envisage;
        private object _envisageWrapped;

        /// <summary>The imported Envisage wrapped for use; null (and logged) when missing or unusable.</summary>
        private EnvisageProxy Envisage
        {
            get
            {
                var obj = EnvisageObject;
                if (!ReferenceEquals(obj, _envisageWrapped))
                {
                    _envisageWrapped = obj;
                    _envisage = EnvisageProxy.From(obj, out string problem);
                    if (_envisage == null) Log.Warning("STP: Envisage not usable: {0}", problem);
                    else Log.Info("STP: bound to Envisage {0}", _envisage.Description);
                }
                return _envisage;
            }
        }

        #endregion

        #region Initialization

        public override bool InitializeAttributes()
        {
            Attributes[StandardPluginAttributes.PluginName] = "StpClient:Resources:Attribute_Name";
            Attributes[StandardPluginAttributes.PluginDescription] = "StpClient:Resources:Attribute_Description";
            return true;
        }

        public override bool Initialize()
        {
            bool result = true;
            result &= CommandManager.AddFromMarkup(this, "Menus/Commands.xml");
            result &= MenuManager.AddFromMarkup(this, "Menus/Menus.xml");
            return result;
        }

        #endregion

        #region Command handlers (bound in Menus/Commands.xml)

        public void RunPrepare() => RunScript(StpScript.Prepare, Res.Menu_Prepare);

        /// <summary>
        /// STP UG > Abrir planificador: runs script 2p (snapshot of the open VGS project + local bridge, no browser), then
        /// opens its plugin_session.json as one planner document. A repeat while the document is open activates it
        /// (Actualizar in the document reloads from VGS).
        /// </summary>
        public void OpenPlannerView()
        {
            try
            {
                if (_planner != null && _planner.IsViewOpen())
                {
                    ViewManager.ActivateViewModel(_planner);
                    return;
                }
                DateTime startedUtc = DateTime.UtcNow;
                RunScript(StpScript.PluginPlanner, Res.Menu_PlannerView, () => OnUi(() => OpenPlannerSession(startedUtc)));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "STP: could not open the planner view");
                Show(string.Format(Res.Msg_PlannerFailed, ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>STP UG > Abrir planificador (navegador) (script 2): the browser planner, unchanged.</summary>
        public void RunPlanner() => RunScript(StpScript.Planner, Res.Menu_Planner);

        public void RunApply() => RunScript(StpScript.Apply, Res.Menu_Apply);

        public void RunVerify() => RunScript(StpScript.Verify, Res.Menu_Verify);

        public void ShowSettings()
        {
            try
            {
                SettingsWindow.ShowDialog(OwnerWindow(), EnvisageRunner.IsAvailable(Envisage));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "STP: could not open the settings window");
                Show(ex.Message, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Running a script

        /// <param name="onSuccess">Called on a worker thread when the script ends with status 0.</param>
        private void RunScript(StpScript script, string label, Action onSuccess = null)
        {
            try
            {
                var envisage = Envisage;
                if (!EnvisageRunner.IsAvailable(envisage))
                {
                    Log.Warning("STP: {0} not run, Envisage is not available", script.FileName);
                    Show(Res.Msg_NoEnvisage, MessageBoxImage.Warning);
                    return;
                }

                var settings = StpSettings.Load();
                string folder = StpScriptLocator.Resolve(settings.ScriptsFolder);
                if (folder == null)
                {
                    string looked = string.IsNullOrWhiteSpace(settings.ScriptsFolder)
                        ? Path.Combine(StpScriptLocator.PluginDirectory, "Python", "scripts")
                        : settings.ScriptsFolder;
                    Show(string.Format(Res.Msg_ScriptsNotFound, looked), MessageBoxImage.Warning);
                    return;
                }
                if (!File.Exists(Path.Combine(folder, script.FileName)))
                {
                    Show(string.Format(Res.Msg_ScriptMissing, script.FileName, folder), MessageBoxImage.Warning);
                    return;
                }

                if (!EnvisageRunner.TryBegin(label, out string current))
                {
                    Show(string.Format(Res.Msg_Busy, current), MessageBoxImage.Information);
                    return;
                }

                Log.Info("STP: running {0} from {1}", script.FileName, folder);
                EnvisageRunner.RunAsync(envisage, script, folder).ContinueWith(t =>
                {
                    EnvisageRunner.End();
                    Finished(script, label, t.Result, onSuccess);
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                EnvisageRunner.End();
                Log.Error(ex, "STP: could not start {0}", script.FileName);
                Show(string.Format(Res.Msg_CallFailed, label, ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>On a worker thread. Success says nothing: the scripts show their own Vulcan messages.</summary>
        private void Finished(StpScript script, string label, RunOutcome o, Action onSuccess)
        {
            if (o.Succeeded)
            {
                Log.Info("STP: {0} finished", script.FileName);
                onSuccess?.Invoke();
                return;
            }
            string text;
            if (o.Error != null)
            {
                Log.Error("STP: {0} could not run in Envisage: {1}", script.FileName, o.Error);
                text = string.Format(Res.Msg_CallFailed, label, o.Error);
            }
            else
            {
                Log.Warning("STP: {0} exited with status {1}, log {2}", script.FileName, o.Status, o.LogPath);
                text = string.Format(Res.Msg_Failed, label, o.Status, o.LogTail ?? "", o.LogPath);
            }
            OnUi(() => Show(text, o.Error != null ? MessageBoxImage.Error : MessageBoxImage.Warning));
        }

        #endregion

        #region Planner document

        /// <summary>File times and the clock can disagree by the file system's timestamp resolution.</summary>
        private static readonly TimeSpan SessionClockSlack = TimeSpan.FromSeconds(2);

        /// <summary>The planner's horizon preferences (the browser keeps them in localStorage), next to the session.</summary>
        private const string HorizonFileName = "planner_horizon.json";

        /// <summary>
        /// On the UI thread, after script 2p succeeded: read the session it wrote, build the planner, open the document.
        /// A missing file or one older than this run means 2p did not get to write it (it deletes the old one first).
        /// </summary>
        private void OpenPlannerSession(DateTime startedUtc)
        {
            string path = PluginSession.DefaultPath;
            try
            {
                if (_planner != null && _planner.IsViewOpen())
                {
                    ViewManager.ActivateViewModel(_planner);
                    return;
                }
                if (!File.Exists(path))
                {
                    Log.Warning("STP: script 2p left no session at {0}", path);
                    Show(string.Format(Res.Msg_SessionMissing, path), MessageBoxImage.Warning);
                    return;
                }
                DateTime written = File.GetLastWriteTimeUtc(path);
                if (written < startedUtc - SessionClockSlack)
                {
                    Log.Warning("STP: session {0} written {1:o}, before the run started {2:o}", path, written, startedUtc);
                    Show(string.Format(Res.Msg_SessionStale, path, written.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")), MessageBoxImage.Warning);
                    return;
                }

                PlannerSession session;
                try
                {
                    var plugin = PluginSession.Read(path);
                    var storage = new FileHorizonStorage(Path.Combine(PluginSession.DefaultRuntimeFolder, HorizonFileName));
                    session = PlannerSession.Open(plugin, storage, new PlannerOptions());
                }
                catch (Exception ex)
                {
                    // Wire format, engine rule, I/O: the file is there and current but not usable.
                    Log.Error(ex, "STP: could not read the planner session {0}", path);
                    Show(string.Format(Res.Msg_SessionInvalid, ex.Message, path), MessageBoxImage.Error);
                    return;
                }

                var screen = new PlannerScreenViewModel(session, new PlannerHost(this));
                _planner = new PlannerDocumentViewModel(screen, screen, session);
                ViewManager.OpenViewModel(this, _planner, false);
                Log.Info("STP: planner opened on {0} ({1}, {2} activities)", session.ProjectName, session.BridgeConfig.Mode, session.Snapshot.Activities.Count);
                if (!session.IsLive) Show(string.Format(Res.Msg_BridgeNotLive, session.BridgeConfig.Message), MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "STP: could not open the planner view");
                Show(string.Format(Res.Msg_PlannerFailed, ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>What the planner screen asks of the plugin: questions, messages, and Verificar guardado.</summary>
        private sealed class PlannerHost : IPlannerHost
        {
            private readonly StpClientPlugin _plugin;

            public PlannerHost(StpClientPlugin plugin)
            {
                _plugin = plugin;
            }

            public Action VerifySave => _plugin.RunVerify;

            public bool Confirm(string text) => Ask(text);

            public void Notify(string text, bool isError) => Show(text, isError ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        #endregion

        #region UI helpers

        private static Window OwnerWindow()
        {
            try
            {
                return Application.Current?.MainWindow;
            }
            catch (InvalidOperationException)
            {
                return null;    // not on the UI thread
            }
        }

        private static void OnUi(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess()) a();
            else d.BeginInvoke(a);
        }

        private static bool Ask(string text)
        {
            var owner = OwnerWindow();
            var answer = owner != null
                ? MessageBox.Show(owner, text, Res.Msg_Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(text, Res.Msg_Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
        }

        private static void Show(string text, MessageBoxImage icon)
        {
            var owner = OwnerWindow();
            if (owner != null) MessageBox.Show(owner, text, Res.Msg_Title, MessageBoxButton.OK, icon);
            else MessageBox.Show(text, Res.Msg_Title, MessageBoxButton.OK, icon);
        }

        #endregion
    }
}
