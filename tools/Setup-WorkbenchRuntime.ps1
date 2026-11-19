<#
.SYNOPSIS
  Builds the WorkbenchRuntime folder (SDK + build system + runnable debug Workbench) from the Maptek NuGet packages.

.DESCRIPTION
  The Maptek Visual Studio templates (and this solution) expect a folder laid out like Maptek's internal Workbench repo:
    WorkbenchRuntime\SDK\Bin\Client|Shared|Server   reference assemblies     (nupkg lib/net45/SDK/*)
    WorkbenchRuntime\SDK\Third, SDK\Schema
    WorkbenchRuntime\Build                          MSBuild targets + weavers (Maptek.Workbench.Tools build/net45/Build)
    WorkbenchRuntime\Runtime\Client|Server|...      runnable Workbench       (nupkg build/net45/*)
  Run once after cloning, and again after updating the packages.

.PARAMETER PackagesDir
  Folder containing <Id>.<Version>\<Id>.<Version>.nupkg (default: the examples repo's packages folder).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\Setup-WorkbenchRuntime.ps1
  powershell -ExecutionPolicy Bypass -File Tools\Setup-WorkbenchRuntime.ps1 -PackagesDir D:\Dev\workbench-plugin-examples-master\packages -Clean
#>
param(
    [string]$PackagesDir = (Join-Path $PSScriptRoot '..\..\packages'),
    [string]$Version = '4.1.0.994',
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\WorkbenchRuntime'),
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$PackagesDir = (Resolve-Path $PackagesDir).Path
if ($Clean -and (Test-Path $OutputDir)) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

# package id => list of @(zip prefix, destination subfolder)
$map = [ordered]@{
    'Maptek.Workbench.Shared'      = @(@('lib/net45/SDK/', 'SDK'), @('build/net45/', 'Runtime'), @('doc/', 'SDK\Documentation'))
    'Maptek.Workbench.Client'      = @(@('lib/net45/SDK/', 'SDK'), @('build/net45/', 'Runtime'))
    'Maptek.Workbench.Server'      = @(@('lib/net45/SDK/', 'SDK'), @('build/net45/', 'Runtime'))
    'Maptek.Workbench.Tools'       = @(@('build/net45/Build/', 'Build'))
    'Maptek.SharedPlugins.Shared'  = @(@('lib/net45/SDK/', 'SDK'), @('build/net45/', 'Runtime'))
    'Maptek.SharedPlugins.Client'  = @(@('lib/net45/SDK/', 'SDK'), @('build/net45/', 'Runtime'))
    'Maptek.SharedPlugins.Server'  = @(@('lib/net45/SDK/', 'SDK'), @('build/net45/', 'Runtime'))
    'Maptek.Account.Broker'        = @(@('build/net45/', 'Runtime'))
}

foreach ($id in $map.Keys) {
    $nupkg = Join-Path $PackagesDir "$id.$Version\$id.$Version.nupkg"
    if (-not (Test-Path $nupkg)) { Write-Warning "Missing package: $nupkg"; continue }
    Write-Host "Extracting $id ..."
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $name = [Uri]::UnescapeDataString($entry.FullName)
            foreach ($rule in $map[$id]) {
                $prefix = $rule[0]
                if (-not $name.StartsWith($prefix)) { continue }
                $rel = $name.Substring($prefix.Length)
                # skip the package's own MSBuild glue files (Maptek.*.props/targets at build/net45 root)
                if ($prefix -eq 'build/net45/' -and $rel -notmatch '/' -and $rel -match '^Maptek\..*\.(props|targets)$') { continue }
                $dest = Join-Path (Join-Path $OutputDir $rule[1]) ($rel -replace '/', '\')
                New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
                break
            }
        }
    } finally { $zip.Dispose() }
}

$client = Join-Path $OutputDir 'Runtime\Client\WorkbenchClient.exe'
if (Test-Path $client) { Write-Host "Done. Debug Workbench: $client" -ForegroundColor Green }
else { Write-Warning "WorkbenchClient.exe not found - check the packages folder." }
