<#
.SYNOPSIS
  Builds the STP UG Workbench plugin without opening Visual Studio.

.DESCRIPTION
  Finds MSBuild with vswhere, restores and builds StpWorkbench.sln, and copies the plugin's own files
  (StpClient, StpEngine, StpBridge) to Deploy\Client - the folder Vulcan loads the plugin from.
  Stops early, with a clear message, when:
    - Visual Studio / MSBuild is not installed,
    - the Workbench runtime is missing (run tools\Setup-WorkbenchRuntime.ps1 or create WorkbenchRuntimeOverride.props),
    - Vulcan or the Workbench still has the plugin DLLs open (close them first: they lock Deploy\Client).

  Then builds the installer, installer\dist\STP-UG-<version>.msi (Release build, WiX 5), unless -NoMsi.

.PARAMETER Configuration
  Debug (default) or Release, for Deploy\Client. The MSI is always built from a separate Release build.

.PARAMETER NoMsi
  Skip the installer (WiX is then not needed).

.PARAMETER Version
  MSI/DLL version x.y.z. Default: <Major>.<Minor> from Build\SolutionVersionNumber.props plus the git commit count,
  so every commit gives a higher version and a new MSI replaces the old one.

.PARAMETER Test
  Also run the C# tests (StpEngine.Tests, StpBridge.Tests), the Python smoke test and the golden check.

.PARAMETER Register
  After a successful build, register Deploy\Client as a Workbench plugin search path
  (tools\Register-PluginSearchPath.ps1; asks for administrator rights). Only needed once per PC.

.PARAMETER Clean
  Rebuild from scratch instead of an incremental build.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Build-Plugin.ps1
  powershell -ExecutionPolicy Bypass -File tools\Build-Plugin.ps1 -Configuration Release -Test
  Build-Plugin.cmd          (double-click, from the repository root)
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Test,
    [switch]$Register,
    [switch]$Clean,
    [switch]$NoMsi,
    [string]$Version,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'StpWorkbench.sln'
$deploy = Join-Path $root 'Deploy\Client'
$started = Get-Date

function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Fail([string]$text) {
    Write-Host "`nBUILD STOPPED: $text" -ForegroundColor Red
    if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}

# 1. MSBuild ---------------------------------------------------------------------------------------------------------
Step 'Looking for MSBuild'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = $null
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
        -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    Fail 'MSBuild not found. Install Visual Studio (or Build Tools) with the ".NET desktop development" workload and .NET Framework 4.8 targeting.'
}
Write-Host "  $msbuild"

# 2. Workbench runtime -----------------------------------------------------------------------------------------------
Step 'Checking the Workbench runtime'
$runtime = Join-Path $root 'WorkbenchRuntime'
$override = Join-Path $root 'WorkbenchRuntimeOverride.props'
if (Test-Path $override) {
    $text = Get-Content $override -Raw
    if ($text -match '<WorkbenchRuntime[^>]*>\s*([^<]+?)\s*</WorkbenchRuntime>') { $runtime = $Matches[1] }
    Write-Host "  from WorkbenchRuntimeOverride.props: $runtime"
} else {
    Write-Host "  default: $runtime"
}
if (-not (Test-Path (Join-Path $runtime 'Build\MSBuild\Maptek.Build.ExternalPlugin.targets'))) {
    Fail "Workbench runtime not found at '$runtime'. Run tools\Setup-WorkbenchRuntime.ps1, or copy WorkbenchRuntimeOverride_Template.props to WorkbenchRuntimeOverride.props and point it at an existing runtime (see README.md)."
}

# 3. Locked DLLs -----------------------------------------------------------------------------------------------------
Step 'Checking that Vulcan / Workbench is not holding the plugin'
$locked = @()
foreach ($name in 'StpClient.dll', 'StpEngine.dll', 'StpBridge.dll') {
    $file = Join-Path $deploy $name
    if (-not (Test-Path $file)) { continue }
    try {
        $stream = [System.IO.File]::Open($file, 'Open', 'ReadWrite', 'None')
        $stream.Close()
    } catch {
        $locked += $name
    }
}
if ($locked.Count -gt 0) {
    $running = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -match '^(WorkbenchClient|envisage|vulcan)' } |
        Select-Object -ExpandProperty ProcessName -Unique
    $who = if ($running) { " (running: $($running -join ', '))" } else { '' }
    Fail "$($locked -join ', ') in Deploy\Client is in use$who. Close Vulcan and the Workbench, then build again."
}
Write-Host '  OK'

# 4. Build -----------------------------------------------------------------------------------------------------------
$target = if ($Clean) { 'Rebuild' } else { 'Build' }
Step "Building ($Configuration, $target)"
& $msbuild $solution -restore "-t:$target" "-p:Configuration=$Configuration" -m -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { Fail "MSBuild failed (exit code $LASTEXITCODE). The errors are above." }

$missing = 'StpClient.dll', 'StpEngine.dll', 'StpBridge.dll' | Where-Object { -not (Test-Path (Join-Path $deploy $_)) }
if ($missing) { Fail "the build finished but Deploy\Client is missing: $($missing -join ', ')" }

# 5. Tests (optional) ------------------------------------------------------------------------------------------------
$testFailures = @()
if ($Test) {
    Step 'Running tests'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        foreach ($project in 'src\StpEngine.Tests', 'src\StpBridge.Tests') {
            Write-Host "  $project"
            & dotnet test (Join-Path $root $project) --no-build -c $Configuration --nologo -v q
            if ($LASTEXITCODE -ne 0) { $testFailures += $project }
        }
    } else {
        Write-Host '  dotnet not found: C# tests skipped' -ForegroundColor Yellow
    }
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        Write-Host '  python smoke test'
        & python -I (Join-Path $root 'python\tests\smoke_test.py')
        if ($LASTEXITCODE -ne 0) { $testFailures += 'python smoke test' }
    } else {
        Write-Host '  python not found: Python tests skipped' -ForegroundColor Yellow
    }
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) {
        Write-Host '  golden check'
        & node (Join-Path $root 'tools\golden\run.mjs') --check
        if ($LASTEXITCODE -ne 0) { $testFailures += 'golden check' }
    } else {
        Write-Host '  node not found: golden check skipped' -ForegroundColor Yellow
    }
}

# 6. Installer (MSI) -------------------------------------------------------------------------------------------------
$msi = $null
if (-not $NoMsi) {
    Step 'Building the installer (MSI)'
    $wix = (Get-Command wix -ErrorAction SilentlyContinue).Source
    if (-not $wix) {
        $candidate = Join-Path $HOME '.dotnet\tools\wix.exe'
        if (Test-Path $candidate) { $wix = $candidate }
    }
    if (-not $wix) {
        Fail 'WiX not found. Install it once with:  dotnet tool install --global wix --version 5.0.2   (keep version 5), or build with -NoMsi.'
    }

    # Version: <Major>.<Minor>.<commit count>
    if ($Version) {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "-Version ${Version}: give three numbers, e.g. 1.0.42" }
        $ver = $Version
    } else {
        $props = Get-Content (Join-Path $root 'Build\SolutionVersionNumber.props') -Raw
        $major = [regex]::Match($props, '<VersionDigitMajor[^>]*>(\d+)<').Groups[1].Value
        $minor = [regex]::Match($props, '<VersionDigitMinor[^>]*>(\d+)<').Groups[1].Value
        $count = (& git -C $root rev-list --count HEAD).Trim()
        $ver = "$major.$minor.$count"
    }
    $parts = $ver.Split('.')
    $commit = (& git -C $root rev-parse --short HEAD).Trim()
    $dirty = & git -C $root status --porcelain -- src python installer
    if ($dirty) {
        Write-Host '  note: packaging uncommitted changes:' -ForegroundColor Yellow
        $dirty | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    }
    Write-Host "  version $ver (commit $commit)"

    # A separate Release build into a temporary folder: Deploy\ is left alone.
    $stage = Join-Path $env:TEMP ('stp-msi-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $generated = Join-Path $root 'src\StpClient\Properties\AssemblyVersionInfo.g.cs'   # tracked; the build rewrites it
    $kept = [System.IO.File]::ReadAllBytes($generated)
    try {
        & $msbuild (Join-Path $root 'src\StpClient\StpClient.csproj') -restore -t:Rebuild -p:Configuration=Release `
            "-p:PluginDeployDir=$stage\deploy\" -p:CopyBinaryFilesToWorkbenchClientPlugins=false `
            "-p:VersionDigitMajor=$($parts[0])" "-p:VersionDigitMinor=$($parts[1])" "-p:VersionDigitRevision=$($parts[2])" `
            -nologo -v:minimal
        $code = $LASTEXITCODE
    } finally {
        [System.IO.File]::WriteAllBytes($generated, $kept)
    }
    if ($code -ne 0) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue; Fail "the Release build for the MSI failed (exit code $code)." }

    # Payload: Client\ (DLLs), Client\Python\scripts + stp_core (tracked .py files), VERSION.txt
    $payload = Join-Path $stage 'payload'
    $client = Join-Path $payload 'Client'
    New-Item -ItemType Directory -Path $client | Out-Null
    Copy-Item (Join-Path $stage 'deploy\Client\*') $client -Recurse
    $pyFiles = & git -C $root ls-files -- 'python/scripts/*.py' 'python/stp_core/*.py'
    foreach ($f in $pyFiles) {
        $dest = Join-Path $client ('Python\' + $f.Substring('python/'.Length).Replace('/', '\'))
        New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
        Copy-Item (Join-Path $root $f) $dest
    }
    # The planner's web resources (script 2 copies them into the user's runtime folder; stp_core/runtime.py).
    $web = Join-Path $client 'Python\web'
    New-Item -ItemType Directory -Path $web -Force | Out-Null
    $webFiles = & git -C $root ls-files -- 'reference/web/*.html' 'reference/web/*.js'
    foreach ($f in $webFiles) { Copy-Item (Join-Path $root $f) $web }
    # The Maptek footer logo is not in the public repository: include it only from a private local copy.
    $logo = Join-Path $root 'installer\private\Maptek_highres.jpg'
    if (Test-Path $logo) {
        New-Item -ItemType Directory -Path (Join-Path $web 'assets') -Force | Out-Null
        Copy-Item $logo (Join-Path $web 'assets\Maptek_highres.jpg')
        Write-Host '  logo: included from installer\private'
    } else {
        Write-Host '  logo: none (put Maptek_highres.jpg in installer\private to include it)' -ForegroundColor Yellow
    }
    Set-Content -Path (Join-Path $payload 'VERSION.txt') -Encoding ASCII -Value @("STP UG $ver", "commit $commit", "built $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
    $dlls = (Get-ChildItem $client -Filter *.dll | Select-Object -ExpandProperty Name) -join ', '
    Write-Host "  payload: $dlls; $($pyFiles.Count) Python files; $($webFiles.Count) web files"
    foreach ($need in 'StpClient.dll', 'StpEngine.dll', 'StpBridge.dll') {
        if (-not (Test-Path (Join-Path $client $need))) { Fail "the Release build left no $need" }
    }

    $dist = Join-Path $root 'installer\dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $msi = Join-Path $dist "STP-UG-$ver.msi"
    Push-Location (Join-Path $root 'installer')
    try {
        & $wix build 'StpUG.wxs' -arch x64 -d "Version=$ver" -d "PayloadDir=$payload" `
            -intermediatefolder (Join-Path $stage 'wixobj') -o $msi
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($code -ne 0) { Fail "WiX failed (exit code $code). The errors are above." }
    Remove-Item ([System.IO.Path]::ChangeExtension($msi, '.wixpdb')) -ErrorAction SilentlyContinue
}

# 7. Register (optional) ---------------------------------------------------------------------------------------------
if ($Register) {
    Step 'Registering Deploy\Client with the Workbench'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Register-PluginSearchPath.ps1')
}

# Summary ------------------------------------------------------------------------------------------------------------
$seconds = [int]((Get-Date) - $started).TotalSeconds
Write-Host ''
Get-ChildItem $deploy -Filter *.dll | ForEach-Object {
    Write-Host ("  {0,-16} {1:yyyy-MM-dd HH:mm:ss}" -f $_.Name, $_.LastWriteTime)
}
if ($testFailures.Count -gt 0) {
    Write-Host "`nBuilt in $seconds s, but tests failed: $($testFailures -join ', ')" -ForegroundColor Red
    if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
    exit 2
}
Write-Host "`nBuilt in $seconds s. Plugin files: $deploy" -ForegroundColor Green
if ($msi) { Write-Host ("Installer: {0}  ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB)) -ForegroundColor Green }
Write-Host 'Start Vulcan to load it (first time on this PC: run with -Register, or tools\Register-PluginSearchPath.ps1).'
if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
