<#
.SYNOPSIS
  Shows the planner screen (Views\Planner\PlannerContent) outside Workbench, on the design data.

.DESCRIPTION
  Loads the built StpClient.dll, hosts PlannerContent in a plain WPF window with DesignPlannerViewModel (the real
  PlannerScreenViewModel and timeline adapter over a PlannerSession of the mockup's invented project) and either
  shows it (-Show) or saves a PNG of it. No Workbench or Vulcan needed. Build first with tools\Build-Plugin.ps1.

.PARAMETER Png
  Where to save the screenshot (default: docs\mockups\planner_preview.png).

.PARAMETER Show
  Open the window and wait until it is closed, instead of saving a PNG.

.EXAMPLE
  powershell -STA -ExecutionPolicy Bypass -File tools\Preview-Planner.ps1 -Show
#>
param(
    [string]$Configuration = 'Debug',
    [string]$Png,
    [switch]$Show,
    [int]$Width = 1400,
    [int]$Height = 820
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root "src\StpClient\bin\$Configuration\StpClient.dll"
if (-not (Test-Path $dll)) { throw "Not built: $dll (run tools\Build-Plugin.ps1)" }
if (-not $Png) { $Png = Join-Path $root 'docs\mockups\planner_preview.png' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell -STA' }

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$asm = [Reflection.Assembly]::LoadFrom($dll)
# Newtonsoft.Json is the Workbench's own copy (never copied next to the plugin): take it from the Workbench runtime.
$runtime = Join-Path $root 'WorkbenchRuntime'
$override = Join-Path $root 'WorkbenchRuntimeOverride.props'
if (Test-Path $override) {
    $text = Get-Content $override -Raw
    if ($text -match '<WorkbenchRuntime[^>]*>\s*([^<]+?)\s*</WorkbenchRuntime>') { $runtime = $Matches[1] }
}
$newtonsoft = Join-Path $runtime 'SDK\Third\Newtonsoft.Json\Newtonsoft.Json.dll'
$binDir = Split-Path $dll
# The XAML pack URI (/StpClient;component/...) asks for StpClient by name; hand back the copy just loaded. The other
# Stp* libraries load from the same folder; Newtonsoft.Json from the runtime.
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $e)
    $name = ($e.Name -split ',')[0]
    if ($name -eq 'StpClient') { return $asm }
    if ($name -eq 'Newtonsoft.Json' -and (Test-Path $newtonsoft)) { return [Reflection.Assembly]::LoadFrom($newtonsoft) }
    $local = Join-Path $binDir ($name + '.dll')
    if (Test-Path $local) { return [Reflection.Assembly]::LoadFrom($local) }
    return $null
})
$vm = [Activator]::CreateInstance($asm.GetType('Maptek.Vulcan.Stp.Views.Planner.DesignPlannerViewModel'))
$content = [Activator]::CreateInstance($asm.GetType('Maptek.Vulcan.Stp.Views.Planner.PlannerContent'))
$content.DataContext = $vm

$window = New-Object Windows.Window
$window.Title = 'Planificador STP (vista previa)'
$window.Width = $Width
$window.Height = $Height
$window.Content = $content
$window.WindowStartupLocation = 'CenterScreen'

if ($Show) {
    [void]$window.ShowDialog()
    return
}

$window.ShowInTaskbar = $false
$window.Left = -10000
$window.WindowStartupLocation = 'Manual'
$window.Show()
# Let layout and bindings settle before rendering.
$frame = New-Object Windows.Threading.DispatcherFrame
[Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke([Windows.Threading.DispatcherPriority]::ApplicationIdle,
    [Action]{ $frame.Continue = $false }) | Out-Null
[Windows.Threading.Dispatcher]::PushFrame($frame)

$w = [int]$content.ActualWidth; $h = [int]$content.ActualHeight
$bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
$bmp.Render($content)
$enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
$enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$fs = [IO.File]::Create($Png)
try { $enc.Save($fs) } finally { $fs.Dispose() }
$window.Close()
Write-Host "Saved $Png ($w x $h)"
