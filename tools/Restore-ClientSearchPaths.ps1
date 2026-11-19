<#
.SYNOPSIS
  Recovery helper: rebuilds the Workbench *Client* plugin search paths from the *Server* ones.

.DESCRIPTION
  An earlier version of Register-PluginSearchPath.ps1 recreated
  HKLM\SOFTWARE\Maptek\Workbench\Client\PluginSearchPaths and wiped the entries other Maptek
  products (Vulcan, etc.) had written there. The Server key was not touched, and installers
  register matching ...\Client and ...\Server folders under the same value name. This script
  proposes the Client entry for every Server entry whose sibling "Client" folder exists.

  Run without -Apply first to preview. -Apply writes the values (asks for admin). Existing
  Client values are never changed or removed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\Restore-ClientSearchPaths.ps1
  powershell -ExecutionPolicy Bypass -File Tools\Restore-ClientSearchPaths.ps1 -Apply
#>
param([switch]$Apply)
$ErrorActionPreference = 'Stop'

$serverKey = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Maptek\Workbench\Server\PluginSearchPaths'
$clientKey = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Maptek\Workbench\Client\PluginSearchPaths'

if ($Apply) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Start-Process powershell -Verb RunAs -ArgumentList @('-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Apply') -Wait
        return
    }
}

if (-not (Test-Path $serverKey)) { Write-Warning "No Server search paths found at $serverKey - use the Repair option of each Maptek product instead."; Read-Host 'Press Enter'; return }

$server = Get-ItemProperty -Path $serverKey
$client = if (Test-Path $clientKey) { Get-ItemProperty -Path $clientKey } else { $null }

Write-Host "`nCurrent Client entries:" -ForegroundColor Cyan
if ($client) { $client.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { Write-Host "  $($_.Name) = $($_.Value)" } } else { Write-Host '  (none)' }

$proposals = @()
foreach ($p in $server.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' }) {
    $serverPath = [string]$p.Value
    $leaf = Split-Path $serverPath -Leaf
    $candidate = if ($leaf -ieq 'Server') { Join-Path (Split-Path $serverPath -Parent) 'Client' } else { $serverPath -ireplace '\\Server(\\|$)', '\Client$1' }
    $already = $client -and ($client.PSObject.Properties.Name -contains $p.Name)
    $exists = Test-Path $candidate
    $proposals += [pscustomobject]@{ Name = $p.Name; ServerPath = $serverPath; ClientPath = $candidate; FolderExists = $exists; AlreadyPresent = [bool]$already }
}

Write-Host "`nServer entries and proposed Client entries:" -ForegroundColor Cyan
$proposals | Format-Table Name, ClientPath, FolderExists, AlreadyPresent -AutoSize | Out-String | Write-Host

$toAdd = $proposals | Where-Object { $_.FolderExists -and -not $_.AlreadyPresent }
if (-not $Apply) {
    Write-Host "$(@($toAdd).Count) entr(y/ies) would be added. Re-run with -Apply to write them." -ForegroundColor Yellow
    Write-Host "Entries with FolderExists = False need a Repair of that product (Apps & features > Modify/Repair)."
} else {
    if (-not (Test-Path $clientKey)) { New-Item -Path $clientKey | Out-Null }
    foreach ($e in $toAdd) {
        New-ItemProperty -Path $clientKey -Name $e.Name -Value $e.ClientPath -PropertyType String | Out-Null
        Write-Host "Added $($e.Name) = $($e.ClientPath)" -ForegroundColor Green
    }
    Write-Host 'Done. Restart Workbench/Vulcan.'
}
Read-Host 'Press Enter to close'
