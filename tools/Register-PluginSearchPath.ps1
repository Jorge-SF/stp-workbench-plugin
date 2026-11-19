<#
.SYNOPSIS
  Registers (or removes) the Deploy\Client folder as a machine-wide Workbench plugin search path,
  so the installed Vulcan/Workbench loads the STP plugin from this checkout.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\Register-PluginSearchPath.ps1
  powershell -ExecutionPolicy Bypass -File Tools\Register-PluginSearchPath.ps1 -Unregister
#>
param(
    [string]$Name = 'StpUG.Dev',   # the MSI registers 'StpUG'; never both at once
    [string]$ClientPath = (Join-Path $PSScriptRoot '..\Deploy\Client'),
    [switch]$Unregister
)
$ErrorActionPreference = 'Stop'

# Self-elevate (HKLM needs admin)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argList = @('-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Name', "`"$Name`"", '-ClientPath', "`"$ClientPath`"")
    if ($Unregister) { $argList += '-Unregister' }
    Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait
    return
}

$key = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Maptek\Workbench\Client\PluginSearchPaths'
if ($Unregister) {
    Remove-ItemProperty -Path $key -Name $Name -ErrorAction SilentlyContinue
    Write-Host "Removed '$Name' from $key"
} else {
    $full = (Resolve-Path $ClientPath).Path
    # NOTE: never use 'New-Item -Force' on an existing registry key - it recreates the key and deletes all its values.
    if (-not (Test-Path $key)) { New-Item -Path $key | Out-Null }
    New-ItemProperty -Path $key -Name $Name -Value $full -PropertyType String -Force | Out-Null
    Write-Host "Registered '$Name' = $full" -ForegroundColor Green
    Write-Host "Restart Vulcan/Workbench to load the plugin."
}
Read-Host 'Press Enter to close'
