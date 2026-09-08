# Planning Director (Phase 22)

## Purpose and scope

`PlanningDirector` (namespace `CapBot.Core.Planning`) is the **first stage of
the planning arc the contracts sketched**: P6's header names `planning` as an
intended snapshot consumer, and the P4/P3/P10 explicitly-not lists all defer
"dynamic planning" to later phases (`TASK_SCHEDULER.md:185`,
`TASK_RECOVERY.md:132`, `CREW_AGENTS.md:221`). Phase 22 establishes the
deterministic planning layer as a bounded **situation-assessment** director:
it tracks planning-relevant situations as data and emits bounded decision
lines. Phase 23 (dynamic task creation) builds on this layer — shipped as
`MissionWorkDirector` (`docs/MISSION_WORK_DIRECTOR.md`), which consumes the
MISSIONWORK episode surface below.

**Design basis note:** no master-plan document exists in the workspace
defining Phase 22 (verified by the P22 research report — direct "Phase
22/23/24" hits: zero). The design is inferred from the deferral trail, the
P18 director template, and the P19 stale-premise screen vocabulary, and
documented as such. If the external master plan (PART 0–68) mandates a
different shape for P22, this phase is the candidate for re-alignment.

The director is **decisions-as-data**: two rules, both emitting bounded log
lines only.

1. **PREMISE_DRIFT** — the planning-granularity mirror of the P19 stale-
   premise screens (`stale premise: sector changed` / `stale premise: in
   warp`). A premise (current sector + warp flag) is captured when the
   episode arms; every readable pass compares against it. Divergence emits
   `PlanningPremiseDrift PLAN:PREMISE_DRIFT sector A->B` and re-arms with the
   fresh premise. Premise-carrying task families (CAPTAIN_DELIB, NAV_RECOVERY)
   rely on exactly these two fields — catching their expiry at planning
   granularity, before the queue is even screened, is this layer's
   contribution. P19 keeps full ownership of its per-queued-task screens
   (P22 never calls the validator).

2. **MISSIONWORK episode** — the trigger surface P23 will author tasks from:
   an incomplete, unended mission exists (bounded scan, first
   `MaxMissionsInScope`=8), the ship is calm (P18 gate family: no P9
   emergency, no P14 plan, no P17 record, no hostiles/boarders/warp, sector
   known), and the task registry has spare live-capacity headroom
   (`LiveCount < MaxLiveTasks`=64 — register-beyond-cap returns null, so the
   planner reacts to pressure instead of retry-storming). Calm + capacity +
   a one-cadence dwell ⇒ the episode opens (`PlanningIntentOpened
   PLAN:MISSIONWORK ...` exactly once); refreshes are silent duplicates.
   The episode is DATA ONLY — **no task is authored in Phase 22**.

## Data-only contract (MUST-NOT list)

The director MUST NOT:

- create, queue, pause, resume, cancel, claim, or otherwise author any task
  (no `CapBotTask.Create`, no `Try*`, no `TaskRegistry.Register`);
- call `TaskScheduler`, `TaskRecoveryManager`, `ExecutionClaims` (beyond its
  own deny-by-default authority seam), `CapabilityRegistry`,
  `DecisionValidator`, `CaptainDirector`, or crew-registry assignment APIs;
- issue orders, move bots, or mutate any game state;
- read or write game state off the game thread (snapshot via the world seam
  on the tick thread only);
- run per-frame work (cadence-gated 5 s), use LINQ, or scan unbounded.

Reflection-verified: **no lifecycle method** (`Create`/`Try*`/`Register`/
`Tick`) is declared on the type; the WorldTick block calls `Evaluate` only.

Deterministic rules always override everything; there is no LLM in the loop
(the P20/P21 advisors are data-only log lines and are never consumed here).
Same-snapshot ⇒ same decisions (test-verified).

## Files

| File | Role |
| --- | --- |
| `CapBot/Core/Planning/PlanningDirector.cs` | Planning director core: gates, premise drift, mission-work episode, hygiene (~600 lines) |
| `CapBot/Core/Planning/PlanningLogBridge.cs` | Boot attach of the `PLANNING` log subsystem |
| `CapBot/Core/Logging/CapBotLog.cs` | +`PLANNING` const (additive) |
| `CapBot/Mod.cs` | Boot wiring: bridge + authority/now/world seams (no config toggle — P18 precedent) |
| `CapBot/Patch.cs` | `WorldTick` postfix: one guarded `Evaluate` block after the P21 advisor block |
| `CapBot/CapBot.csproj` | +2 Compile entries |
| `tests/PlanningDirectorTests.cs` | PD01–PD10 (~55 assertions) |
| `tests/run_tests.ps1`, `tests/TaskRecoveryTests.cs` | Suite registration (21 suites total) |

## Gate order (P18 house shape)

`Evaluate(nowMs)`: authority (deny-by-default, null/fault ⇒ no-op) ⇒ cadence
(5 s, wrap-safe unchecked subtraction) ⇒ snapshot fail-safe (null /
never-captured / stale>20 s / future / `!GameStarted` ⇒ `PlanningUncertain`
line, no decisions) ⇒ bounded rules under one lock ⇒ emission loop
(≤ `MaxPendingLines`=4 lines per pass). Never throws.

## Constants

| Constant | Value | Meaning |
| --- | --- | --- |
| `MinRecheckMs` | 5000 | decision cadence (P18 parity) |
| `MaxStaleSnapshotMs` | 20000 | shared freshness standard |
| `MaxActiveSituations` | 8 | bounded tracked set (shed-oldest defensive) |
| `MaxHistory` | 16 | bounded history ring |
| `ActiveExpiryMs` | 30000 | trigger absent past this ⇒ record decays |
| `DriftRecheckBlockMs` | 15000 | anti-churn re-arm after a drift report |
| `MaxMissionsInScope` | 8 | mission-scan bound |

Semantics details: drift is sector change OR warp start (warp **end** is not
drift — arrival resolves the premise); unknown sentinels never arm the
premise and never fire a comparison (uncertain pass, counted); the drift
record is a one-shot report (P15/P16/P17 semantics) and decays via hygiene;
the mission-work record refreshes while its trigger persists and decays after
`ActiveExpiryMs` past the last readable pass.

## Multiplayer authority

Host-only evaluation (deny-by-default `ExecutionClaims.IsAuthoritative()`
seam, null/fault ⇒ no-op). The director never RPCs; effects (in later phases)
flow through the P3/P4/P5/P7/P8 pipeline, which enforces master-side
execution.

## Config

None. Always-on by construction (the P18 deterministic-director precedent —
config toggles exist only for the optional LLM advisors). Inert on clients
via the authority seam.

## Test inventory (PD01–PD10)

| Suite | Covers |
| --- | --- |
| PD01 | premise capture + sector drift end-to-end + reverse drift after the recheck window |
| PD02 | warp-start drift (+warp detail) and warp-end non-drift |
| PD03 | drift recheck block (anti-churn) with premise re-capture; report resumes after the window |
| PD04 | unknown sentinels never arm/never fire; premise survives unknown passes |
| PD05 | MISSIONWORK episode open (dwell+calm+capacity), silent refresh, decay, re-open |
| PD06 | calm gate: real P9 emergency / P14 plan / P17 record / snapshot hostiles / warp each block; drift still fires |
| PD07 | capacity gate: registry filled to the live cap blocks the open; frees ⇒ opens |
| PD08 | fail-safe inputs (null/stale/not-started/future) + cadence gate |
| PD09 | authority deny-by-default (null/faulting/non-authoritative ⇒ no-op) |
| PD10 | Lines/StatusLines/GetIntent determinism, same-snapshot determinism, data-only proof (registry untouched), post-reset inert |

Test-side lessons: publish-after-advance discipline (publishing a snapshot
stamped before a clock advance makes it stale at eval time — the fail-safe
correctly skips it); the drift anti-churn window applies to any second drift
report, including a "reverse" one.

## Verification (2026-09-08)

- Build: MSBuild Release, 0 warnings / 0 errors.
- Tests: `TOTAL passed=1997 failed=0` ×3 consecutive runs (runs 2–4; raw-output
  grep, zero FAIL lines; suite now 21 files).
- Reflection (`verify_build_p22.ps1`): 211 types (180 named); PlanningDirector
  static class, 65 members, all 16 probed; nested `PlanningIntent` +
  `PlanningPremise`; data-only scan = zero lifecycle methods declared;
  `PlanningLogBridge`; `CapBotLog.PLANNING`; 11 Harmony patch classes intact;
  WorldTick postfix IL 673 → 708 bytes; namespace `CapBot.Core.Planning`
  present.