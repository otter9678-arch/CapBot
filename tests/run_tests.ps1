# Dev-side test runner for the CapBot pure-C# task domains (Phase 2 lifecycle,
# Phase 3 recovery, Phase 4 scheduler, Phase 5 claims, Phase 6 world state,
# Phase 7 capability registry, Phase 8 task executor, Phase 9 emergency
# director, Phase 10 crew agents, Phase 11 crew personalities). Compiles the
# pure domain files + all ten test suites with Roslyn csc (no game/PML
# references) into a temp exe, runs it, reports the combined summary. Gates
# on the TOTAL line containing failed=0.
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
  (Join-Path $repo 'CapBot\Core\Tasks\ActionIdentity.cs') `
  (Join-Path $repo 'CapBot\Core\Tasks\ExecutionClaims.cs') `
  (Join-Path $repo 'CapBot\Core\World\WorldSnapshot.cs') `
  (Join-Path $repo 'CapBot\Core\World\WorldStateService.cs') `
  (Join-Path $repo 'CapBot\Core\World\WorldSnapshotProbe.cs') `
  (Join-Path $repo 'CapBot\Core\Capabilities\CapabilityDescriptor.cs') `
  (Join-Path $repo 'CapBot\Core\Capabilities\CapabilityRegistry.cs') `
  (Join-Path $repo 'CapBot\Core\Capabilities\RegisteredCapabilities.cs') `
  (Join-Path $repo 'CapBot\Core\Executor\ExecutionResult.cs') `
  (Join-Path $repo 'CapBot\Core\Executor\TaskExecutor.cs') `
  (Join-Path $repo 'CapBot\Core\Emergency\EmergencyState.cs') `
  (Join-Path $repo 'CapBot\Core\Emergency\EmergencyDetector.cs') `
  (Join-Path $repo 'CapBot\Core\Emergency\EmergencyDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Crew\CrewAgent.cs') `
  (Join-Path $repo 'CapBot\Core\Crew\CrewAgentRegistry.cs') `
  (Join-Path $repo 'CapBot\Core\Crew\CrewPersonality.cs') `
  (Join-Path $repo 'CapBot\Core\Crew\CrewExperience.cs') `
  (Join-Path $repo 'CapBot\Core\Crew\CrewMemory.cs') `
  (Join-Path $repo 'CapBot\Core\Navigation\NavigationRecovery.cs') `
  (Join-Path $repo 'CapBot\Core\Missions\MissionDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Missions\MissionReturnPolicy.cs') `
  (Join-Path $repo 'CapBot\Core\Missions\MissionLifecycle.cs') `
  (Join-Path $repo 'CapBot\Core\Economy\EconomyDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Combat\CombatDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Captain\CaptainDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Validation\DecisionValidator.cs') `
  (Join-Path $repo 'CapBot\Core\Commands\CommandGate.cs') `
  (Join-Path $repo 'CapBot\Core\Ollama\OllamaAdvisor.cs') `
  (Join-Path $repo 'CapBot\Core\Qwen\CrewAdvisor.cs') `
  (Join-Path $repo 'CapBot\Core\Planning\PlanningDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Planning\MissionWorkDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Adjustment\AdjustmentDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Learning\AdaptiveLearningDirector.cs') `
  (Join-Path $repo 'CapBot\Core\Tasks\MultiplayerAuthorityMonitor.cs') `
  (Join-Path $repo 'CapBot\Core\Compatibility\CompatManager.cs') `
  (Join-Path $repo 'CapBot\Core\Compatibility\ConflictModel.cs') `
  (Join-Path $repo 'CapBot\Core\Compatibility\ConflictEngine.cs') `
  (Join-Path $repo 'CapBot\Core\Compatibility\ProtectedModList.cs') `
  (Join-Path $repo 'CapBot\Core\Compatibility\QuarantineRecord.cs') `
  (Join-Path $repo 'CapBot\Core\Compatibility\QuarantineExecutor.cs') `
  (Join-Path $repo 'CapBot\Core\Compatibility\SymptomDetectors.cs') `
  (Join-Path $repo 'CapBot\Core\Compatibility\SafeModeGate.cs') `
  (Join-Path $repo 'CapBot\Core\Persistence\CrewPersistence.cs') `
  (Join-Path $repo 'CapBot\Core\Diagnostics\StatusHub.cs') `
  (Join-Path $repo 'CapBot\Core\Update\UpdatePolicy.cs') `
  (Join-Path $repo 'CapBot\Core\Perf\SceneScanGate.cs') `
  (Join-Path $repo 'tests\TaskLifecycleTests.cs') `
  (Join-Path $repo 'tests\TaskRecoveryTests.cs') `
  (Join-Path $repo 'tests\TaskSchedulerTests.cs') `
  (Join-Path $repo 'tests\ExecutionClaimTests.cs') `
  (Join-Path $repo 'tests\WorldStateTests.cs') `
  (Join-Path $repo 'tests\CapabilityTests.cs') `
  (Join-Path $repo 'tests\ExecutionTests.cs') `
  (Join-Path $repo 'tests\EmergencyTests.cs') `
  (Join-Path $repo 'tests\CrewAgentTests.cs') `
  (Join-Path $repo 'tests\CrewPresenceTests.cs') `
  (Join-Path $repo 'tests\PersonalityTests.cs') `
  (Join-Path $repo 'tests\ExperienceTests.cs') `
  (Join-Path $repo 'tests\MemoryTests.cs') `
  (Join-Path $repo 'tests\NavigationTests.cs') `
  (Join-Path $repo 'tests\MissionTests.cs') `
  (Join-Path $repo 'tests\MissionLifecycleTests.cs') `
  (Join-Path $repo 'tests\EconomyTests.cs') `
  (Join-Path $repo 'tests\CombatTests.cs') `
  (Join-Path $repo 'tests\CaptainTests.cs') `
  (Join-Path $repo 'tests\DecisionValidatorTests.cs') `
  (Join-Path $repo 'tests\OllamaAdvisorTests.cs') `
  (Join-Path $repo 'tests\CrewAdvisorTests.cs') `
  (Join-Path $repo 'tests\PlanningDirectorTests.cs') `
  (Join-Path $repo 'tests\MissionWorkDirectorTests.cs') `
  (Join-Path $repo 'tests\AdjustmentDirectorTests.cs') `
  (Join-Path $repo 'tests\AdaptiveLearningTests.cs') `
  (Join-Path $repo 'tests\MultiplayerHardeningTests.cs') `
  (Join-Path $repo 'tests\CompatManagerTests.cs') `
  (Join-Path $repo 'tests\ConflictEngineTests.cs') `
  (Join-Path $repo 'tests\QuarantineExecutorTests.cs') `
  (Join-Path $repo 'tests\SymptomDetectorTests.cs') `
  (Join-Path $repo 'tests\SafeModeGateTests.cs') `
  (Join-Path $repo 'tests\PersistenceTests.cs') `
  (Join-Path $repo 'tests\StatusDiagnosticsTests.cs') `
  (Join-Path $repo 'tests\UpdatePolicyTests.cs') `
  (Join-Path $repo 'tests\PerfGateTests.cs') `
  (Join-Path $repo 'tests\QaInvariantTests.cs') `
  (Join-Path $repo 'tests\PersonalityLifecycleTests.cs') `
  (Join-Path $repo 'tests\CommandGateTests.cs') 2>&1 | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) { Write-Output 'COMPILE FAILED'; exit 3 }

$output = & $exe 2>&1 | ForEach-Object { "$_" }
$output | ForEach-Object { Write-Output $_ }
$summary = $output | Select-Object -Last 1
Write-Output ''
Write-Output ('RUNNER: ' + $summary)
Remove-Item -LiteralPath $outDir -Recurse -Force
if ($summary -notmatch 'failed=0') { exit 1 } else { exit 0 }