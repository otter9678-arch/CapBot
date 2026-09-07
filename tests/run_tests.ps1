# Dev-side test runner for the Phase 2 task lifecycle + Phase 3 task recovery
# domains. Compiles the 5 pure task-domain files + both test suites with
# Roslyn csc (no game/PML references) into a temp exe, runs it, reports the
# combined summary. Gates on the TOTAL line containing failed=0.
$ErrorActionPreference = 'Stop'
$repo = 'D:\Vortex Downloads & Mods\LoversLab Mods\CapBot-Alpha-1.2.2-Vortex (1)\CapBot-repo'
$cscCandidates = @(
  'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
  'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
)
$csc = $null
foreach ($c in $cscCandidates) { if (Test-Path -LiteralPath $c) { $csc = $c; break } }
if (-not $csc) { Write-Output 'csc.exe not found'; exit 2 }

$outDir = Join-Path $env:TEMP ('capbot_task_tests_' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $outDir | Out-Null
$exe = Join-Path $outDir 'TaskLifecycleTests.exe'

& $csc -nologo -nologo -out:$exe `
  (Join-Path $repo 'CapBot\Core\Tasks\TaskState.cs') `
  (Join-Path $repo 'CapBot\Core\Tasks\CapBotTask.cs') `
  (Join-Path $repo 'CapBot\Core\Tasks\TaskRegistry.cs') `
  (Join-Path $repo 'CapBot\Core\Tasks\TaskRecovery.cs') `
  (Join-Path $repo 'CapBot\Core\Tasks\TaskRecoveryManager.cs') `
  (Join-Path $repo 'CapBot\Core\Tasks\TaskScheduler.cs') `
  (Join-Path $repo 'tests\TaskLifecycleTests.cs') `
  (Join-Path $repo 'tests\TaskRecoveryTests.cs') `
  (Join-Path $repo 'tests\TaskSchedulerTests.cs') 2>&1 | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) { Write-Output 'COMPILE FAILED'; exit 3 }

$output = & $exe 2>&1
$output | ForEach-Object { Write-Output $_ }
$summary = $output | Select-Object -Last 1
Write-Output ''
Write-Output ('RUNNER: ' + $summary)
Remove-Item -LiteralPath $outDir -Recurse -Force
if ($summary -notmatch 'failed=0') { exit 1 } else { exit 0 }