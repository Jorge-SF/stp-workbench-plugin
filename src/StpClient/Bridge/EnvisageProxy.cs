// Version-free access to Vulcan's IEnvisageInterface.
//
// Why not a typed import: Workbench binds an [ImportInterface] only when the exported object can be assigned to the
// importing property's type (PluginManagerServiceBase.BindInterfaces). A property typed IEnvisageInterface from one
// Vulcan's EnvisageInterfacesClient.dll is a different type from the one another Vulcan loads, so the import is
// refused with "version issue ... Loaded version is X while the import ... was built against Y" in every Vulcan
// except the one the plugin was compiled against (seen: built on 2026.1 or 2024.2, refused in Vulcan 2026).
//
// So the plugin imports the export as object (always assignable) and reaches the two members it uses through the
// IEnvisageInterface the running Vulcan provides, found by name. Both members have had the same signature since
// Vulcan 2024.2 (checked 2024.2, 2026, 2026.1):
//   bool EnvisageFullyLoaded { get; }
//   int  ExecutePythonScript(string scriptPath, string id)

using System;
using System.Linq;
using System.Reflection;

namespace Maptek.Vulcan.Stp.Bridge
{
    internal sealed class EnvisageProxy
    {
        /// <summary>Workbench export name (Maptek.Vulcan.Envisage.ClientAPI.EnvisageInterfaceName).</summary>
        public const string InterfaceName = "Maptek.Vulcan.Envisage.Interfaces.Envisage";

        private const string InterfaceTypeName = "Maptek.Vulcan.Envisage.IEnvisageInterface";

        private readonly object _target;
        private readonly PropertyInfo _fullyLoaded;
        private readonly MethodInfo _execute;

        private EnvisageProxy(object target, PropertyInfo fullyLoaded, MethodInfo execute)
        {
            _target = target;
            _fullyLoaded = fullyLoaded;
            _execute = execute;
        }

        /// <summary>The running Vulcan's interface type name, for the log and the about window.</summary>
        public string Description => _target.GetType().FullName;

        /// <summary>Wraps the imported object; null when there is none or it lacks the members the plugin needs.</summary>
        public static EnvisageProxy From(object envisage, out string problem)
        {
            problem = null;
            if (envisage == null)
            {
                problem = "the Envisage interface was not imported (Vulcan not loaded in this Workbench)";
                return null;
            }

            // Members are taken from the interface, not the class: the implementation may be explicit.
            var iface = envisage.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == InterfaceTypeName);
            if (iface == null)
            {
                problem = envisage.GetType().FullName + " does not implement " + InterfaceTypeName;
                return null;
            }
            var fullyLoaded = iface.GetProperty("EnvisageFullyLoaded", typeof(bool));
            var execute = iface.GetMethod("ExecutePythonScript", new[] { typeof(string), typeof(string) });
            if (fullyLoaded == null || execute == null || execute.ReturnType != typeof(int))
            {
                problem = InterfaceTypeName + " in this Vulcan has no EnvisageFullyLoaded / int ExecutePythonScript(string, string)";
                return null;
            }
            return new EnvisageProxy(envisage, fullyLoaded, execute);
        }

        public bool EnvisageFullyLoaded => (bool)Invoke(() => _fullyLoaded.GetValue(_target, null));

        /// <summary>Blocks until the script ends; returns its exit status.</summary>
        public int ExecutePythonScript(string scriptPath, string id) =>
            (int)Invoke(() => _execute.Invoke(_target, new object[] { scriptPath, id }));

        /// <summary>Rethrows what the Vulcan member threw, not the reflection wrapper.</summary>
        private static object Invoke(Func<object> call)
        {
            try
            {
                return call();
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;  // unreachable
            }
        }
    }
}
