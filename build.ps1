# CapBot reproducible build (Phase 33).
#
# Reproducibility contract:
#   - The game Managed path is a REQUIRED PARAMETER (/p:PulsarManaged or the
#     -PulsarManaged switch). There is NO default and NO hardcoded personal
#     path anywhere in the repo — the csproj's fallback default is a local
#     convenience that CI and scripts override.
#   - NuGet packages restore from packages.config (OpenSesame compiler toolset).
#   - Deterministic=true is set in the csproj (same inputs => same DLL).
#   - The build FAILS if any output would embed the build-machine path
#     (a smoke check greps the DLL for the resolved PulsarManaged string).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 `
#       -PulsarManaged "C:\Path\To\PULSAR_LostColony_Data\Managed" `
#       [-Configuration Release] [-MsBuildPath <explicit.exe>] [-Clean]
#
# Exit codes: 0 = BUILD OK; non-zero = build/smoke failure (message printed).
param(
    [Parameter(Mandatory = $true)]
    [string]$PulsarManaged,

    [string]$Configuration = 'Release',

    [string]$MsBuildPath = '',

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solution = Join-Path $repoRoot 'CapBot.sln'

function Fail($msg) {
    Write-Output ('BUILD FAILED: ' + $msg)
    exit 1
}

# ---- 1. Validate inputs ----------------------------------------------------
if (-not (Test-Path -LiteralPath $PulsarManaged)) {
    Fail ('PulsarManaged path does not exist: ' + $PulsarManaged)
}
foreach ($required in @('Assembly-CSharp.dll','PulsarModLoader.dll','0Harmony.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PulsarManaged $required))) {
        Fail ('PulsarManaged missing required game DLL: ' + $required)
    }
}

# ---- 2. Locate MSBuild ------------------------------------------------------
if ($MsBuildPath -eq '') {
    $candidates = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
    )
    $found = $null
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { $found = $c; break } }
    if (-not $found) {
        Fail ('MSBuild.exe not found; pass -MsBuildPath explicitly')
    }
    $MsBuildPath = $found
}
if (-not (Test-Path -LiteralPath $MsBuildPath)) {
    Fail ('MsBuildPath does not exist: ' + $MsBuildPath)
}

# ---- 3. Optional clean ------------------------------------------------------
if ($Clean) {
    Remove-Item -LiteralPath (Join-Path $repoRoot 'CapBot\bin') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $repoRoot 'CapBot\obj') -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- 4. NuGet restore (packages.config flow) --------------------------------
$nuget = Get-Command nuget -ErrorAction SilentlyContinue
if ($nuget -ne $null) {
    & nuget restore $solution /NonInteractive | ForEach-Object { Write-Output $_ }
    if ($LASTEXITCODE -ne 0) { Fail 'nuget restore failed' }
} else {
    # packages/ is committed, so restore is a no-op when present; require it.
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'packages\OpenSesame.Net.Compilers.Toolset.4.0.1'))) {
        Fail 'nuget not available and packages/ missing — cannot compile (Roslyn toolset)'
    }
    Write-Output 'packages/ present; nuget restore skipped'
}

# ---- 5. Build ---------------------------------------------------------------
Write-Output ('Building ' + $Configuration + ' with PulsarManaged=' + $PulsarManaged)
& $MsBuildPath $solution /p:Configuration=$Configuration /p:PulsarManaged="$PulsarManaged" /v:minimal /nologo /m:8
if ($LASTEXITCODE -ne 0) { Fail ('MSBuild exit ' + $LASTEXITCODE) }

# ---- 6. Smoke checks ----------------------------------------------------------
$dll = Join-Path $repoRoot ('CapBot\bin\' + $Configuration + '\CapBot.dll')
if (-not (Test-Path -LiteralPath $dll)) {
    Fail ('output DLL missing: ' + $dll)
}
$bytes = [System.IO.File]::ReadAllBytes($dll)
if ($bytes.Length -lt 1024) { Fail 'output DLL implausibly small' }
if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { Fail 'output DLL is not a PE image' }

# Path-embedding check: the DLL must not contain the resolved PulsarManaged
# string (a build-machine leak would make the artifact machine-dependent).
$hayText = [System.Text.Encoding]::Unicode.GetString($bytes)
if ($hayText.IndexOf($PulsarManaged, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    Fail 'output DLL embeds the build-machine PulsarManaged path'
}
if ($env:USERNAME -and $hayText.IndexOf($env:USERNAME, [System.StringComparison]::Ordinal) -ge 0) {
    Fail 'output DLL embeds the build-machine username'
}

# ---- 7. Gate line -------------------------------------------------------------
$sizeKb = [int]($bytes.Length / 1024)
Write-Output ('BUILD OK dll=' + $dll + ' bytes=' + $bytes.Length + ' config=' + $Configuration)
exit 0