// STP UG - Workbench client plugin.
// Skeleton only: the plugin loads and does nothing yet. Phase 1 adds the STP menu/toolbar
// (Menus\Commands.xml + Menus\Menus.xml) and the commands that run the STP Python scripts in Vulcan.

using Maptek.Workbench;
using Maptek.Workbench.Client;

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
    }
}
