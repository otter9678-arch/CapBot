# Mission Work Director (Phase 23)

## Purpose and scope

`MissionWorkDirector` (namespace `CapBot.Core.Planning`) is the **authoring
stage of the planning arc**: Phase 22 established the deterministic planning
layer as a bounded situation-assessment director and documented its
MISSIONWORK episode as "the trigger surface P23 will author tasks from"
(`docs/PLANNING_DIRECTOR.md`, `CHANGELOG.md` Phase 22 section). Phase 23
consumes that surface and — under the full P18 anti-churn discipline —
authors EXACTLY ONE task family bound to EXACTLY ONE capability:
`MISSION_WORK` tasks bound to `ISSUE_MOVE_ORDER`
(`RegisteredCapabilities.IssueMoveOrder`).

**Design basis note:** no master-plan document exists in the workspace
defining Phase 23 (the P22/P23 research passes verified direct "Phase
22/23/24" hits: zero). The design is inferred from the deferral trail
(`TASK_SCHEDULER.md:185`, `TASK_RECOVERY.md:132`, `CREW_AGENTS.md:221`,
`CAPABILITIES.md:229` all defer "dynamic task generation" to later phases),
the P22 forward references, and the P18 director template, and documented as
such. If the external master plan (PART 0–68) mandates a different shape for
P23, this phase is the candidate for re-alignment.

The single authoring rule: **MISSIONWORK** — with the P22 episode formally
opened, a workable mission present (`!Ended && !Abandoned && MissionTypeId
>= 0 && TotalObjectives > 0 && CompletedObjectives < TotalObjectives`, the
identical P22 predicate, bounded scan ≤ 8), a 15 s stability dwell served,
the ship calm (P18 gate family), the registry under its live cap, and no
live work task from this record, the director authors one `MISSION_WORK`
task (owner `CAPTAIN`, priority 4, 60 s timeout, 1 retry, `Preemptible`)
bound to `ISSUE_MOVE_ORDER` with a SECTOR target = the CURRENT sector:
"hold/assemble crew at the current position while mission work is pending".
The mission type id rides as metadata for diagnostics only (data, never
parsed). The effect is the same transient 20 s-TTL crew move order P18
authors — a misfire self-heals in ≤ 20 s.

## Trigger surface (deterministic, two conditions)

1. **P22 episode formally opened**: `PlanningDirector.GetIntent(
   "PLAN:MISSIONWORK")` exists with `OpenedReported == true` — the
   machine-readable open signal P22 emits after its own dwell + calm +
   capacity gates pass. P23 never authors before the planning layer opens
   the episode; if P22 is inert, P23 is inert. Fail-closed: a faulting
   planning readback counts as not-opened.
2. **Work mission re-derived from the same snapshot** with the identical P22
   predicate and scan bounds. Mission id/objective counts are not carried in
   the `PlanningIntent` record (only in the opened line text), so
   re-derivation — the house precedent of reading the world seam directly
   (P18) — is the deterministic single source for WHAT to author.

## Capability ownership audit (fresh post-P23 census, all VERIFIED)

The load-bearing design rule. Every `CapabilityId` metadata author in the
tree as it stands AFTER Phase 23:

| Capability | In-tree authors | Verdict for P23 |
| --- | --- | --- |
| `SET_CAPTAIN_ORDER` | P9 only (`EmergencyDetector` findings → `EmergencyDirector`) | NOT safe — shares the 2000 ms registry cooldown with P9 emergencies |
| `SET_CAPTAIN_TARGET` | P9 + legacy captain tick (explicit covenant, `CombatDirector.cs` mandate) | NOT safe |
| `ADD_COURSE_GOAL` / `REMOVE_COURSE_GOAL` | P14 only (`NavigationRecovery.cs:302/:327`) | NOT safe — legacy `SetNextDestiny` rebuilds the course every ~5 s |
| `CLEAR_COURSE_GOALS` | zero, BY DESIGN (legacy calls the RPC directly at four sites; destructive) | NOT safe |
| `READ_WORLD_SNAPSHOT` | zero, BY DESIGN (a read contract — directors read the world seam directly) | NOT authored |
| `ISSUE_MOVE_ORDER` | **P18 (`CaptainDirector`) + P23 (`MissionWorkDirector`) — exactly two** | THE safe channel: transient 20 s-TTL crew effect legacy never reads or writes; dispatcher branch ready (`PulsarCapabilityDispatcher.DispatchIssueMoveOrder`, SECTOR target); the only capability that ever had zero authors before P18 |

`ISSUE_MOVE_ORDER` sharing details for the second author (P23):

- The 1000 ms registry cooldown is shared with P18's gather orders. The
  cooldown stamps only on approval, and the two channels serialize through
  the scheduler's owner-busy gate (both owners are `CAPTAIN`), so
  simultaneous dispatch contention is structurally impossible.
- Priority 4 (below P18's 8) keeps the channels non-contending: a
  MISSION_WORK task queues behind a running CAPTAIN_DELIB and can never
  preempt it — its aged ceiling (4 + `MaxAgingBonus` 5 = 9) never beats the
  preemption margin over P18's base 8 (`PreemptMargin` 2 ⇒ needs > 10).
- No third author was added: the audit was re-run this phase against the
  tree as it exists after P23 (census above), and no new capability was
  registered (`BuiltInCount` stays 7; registering without a dispatcher
  branch is inert by construction).

## What the director MUST NOT do

- Call `TaskScheduler`, `TaskRecoveryManager`, `TaskExecutor`,
  `ExecutionClaims` (beyond its own deny-by-default authority seam),
  `CapabilityRegistry`, `DecisionValidator`, `CaptainDirector`,
  `CrewAgentRegistry`, or the dispatcher;
- author any task family other than `MISSION_WORK`, or any capability other
  than `ISSUE_MOVE_ORDER`; mutate, cancel, pause, or touch any task after
  authoring (recovery owns lifecycle — `ReconcileTasks` reads
  `TaskRegistry.Get` only);
- RPC, call game APIs, scan scenes, use `FindObjectsOfType`, use LINQ, or
  read game state off the game thread;
- run per-frame work (cadence-gated 5 s), scan unbounded missions
  (`MaxMissionsInScope` = 8 within `WorldSnapshot.MaxMissions` = 16), or
  retry a refused authoring in-pass.

Reflection-verified (IL-level): zero forbidden references
(scheduler/recovery/executor/claims/dispatcher/director types, every
`Try*` lifecycle mutator, `PhotonNetwork`, `FindObjectsOfType`); the
authoring path references exactly `CapBotTask.Create` →
`TaskRegistry.Register` → `TryQueue` plus `TaskRegistry.Get` reads.

## Files

| File | Role |
| --- | --- |
| `CapBot/Core/Planning/MissionWorkDirector.cs` | Mission work director core: trigger-surface gate, anti-churn authoring, calm/capacity gates, reconcile (~700 lines) |
| `CapBot/Core/Planning/MissionWorkLogBridge.cs` | Boot attach of the `MISSIONWORK` log subsystem |
| `CapBot/Core/Logging/CapBotLog.cs` | +`MISSIONWORK` const (additive) |
| `CapBot/Mod.cs` | Boot wiring: bridge + authority/now/world seams (no config toggle — P18/P22 precedent) |
| `CapBot/Patch.cs` | `WorldTick` postfix: one guarded `Evaluate` + `ReconcileTasks` pair after the P22 planning block |
| `CapBot/Core/Validation/DecisionValidator.cs` | Additive: `MISSION_WORK` joins the stale-premise family list |
| `CapBot/CapBot.csproj` | +2 Compile entries |
| `tests/MissionWorkDirectorTests.cs` | MW01–MW10 (~70 assertions) |
| `tests/run_tests.ps1`, `tests/TaskRecoveryTests.cs` | Suite registration (22 suites total) |

## Gate order (P18 house shape)

`Evaluate(nowMs)`: authority (deny-by-default, null/fault ⇒ no-op) ⇒ cadence
(5 s, wrap-safe unchecked subtraction) ⇒ snapshot fail-safe (null /
never-captured / stale>20 s / future / `!GameStarted` ⇒ `MissionWorkUncertain`
line, nothing authored) ⇒ bounded rules under one lock ⇒ emission loop
(≤ `MaxPendingLines`=4 lines per pass). Never throws.

Authoring gate order inside the rules (all must pass, in order):
P22-episode-opened ⇒ no live task (else `DuplicatesSuppressed`) ⇒ budget
(`AuthoringsIssued < MaxAuthoringsPerIntent=3`, else `AuthoringCapped`) ⇒
dwell (`EpisodeFirstSeenMs` ≥ 15000 ms old) ⇒ requeue
(`TaskResolvedMs` < 0 or ≥ 20000 ms old) ⇒ calm gate (fail-closed) ⇒
capacity gate (`LiveCount < MaxLiveTasks=64`, else `CapacityGateBlocks`) ⇒
author.

## Constants

| Constant | Value | Meaning |
| --- | --- | --- |
| `MinRecheckMs` | 5000 | decision cadence (P18/P22 parity) |
| `MaxStaleSnapshotMs` | 20000 | shared freshness standard |
| `MaxActiveIntents` | 8 | bounded tracked set (shed-oldest defensive) |
| `MaxHistory` | 16 | bounded history ring |
| `ActiveExpiryMs` | 30000 | trigger absent past this ⇒ record decays |
| `AuthoringDwellMs` | 15000 | mission presence before first authoring |
| `AuthoringRequeueBlockMs` | 20000 | re-arm after a work task resolves |
| `WorkTaskTimeoutMs` | 60000 | work tasks self-expire (bounded work) |
| `WorkPriority` | 4 | below P18's 8; aged ceiling 9 < preemption margin over P18 |
| `MaxAuthoringsPerIntent` | 3 | anti-churn cap per record lifetime |
| `MaxMissionsInScope` | 8 | mission-scan bound (identical P22 scan) |
| `MaxPendingLines` | 4 | bounded emission buffer per pass |

## Failure semantics (no retry storms)

`CapBotTask.Create` null / any `SetMetadata` false / `TaskRegistry.Register`
false ⇒ `AuthoringRefused++`, author nothing this window, the episode stays
open and the next dwell window re-arms. `TryQueue` false ⇒ count refused,
keep the registered task (it expires on its own 60 s deadline), stamp
`TaskResolvedMs = nowMs` so requeue discipline applies
(`MoveOrderRefused` line). The budget resets only when the record decays via
`ActiveExpiryMs` hygiene — a decayed record and a fresh episode re-arm
authoring (MW10).

## Multiplayer authority

Host-only evaluation (deny-by-default `ExecutionClaims.IsAuthoritative()`
seam, null/fault ⇒ no-op). The director never RPCs; effects flow through the
P2–P8 pipeline (scheduler/claims/validator/executor), which enforces
master-side execution. Driver timing: the P23 block runs AFTER the
P22 planning block and AFTER `TaskScheduler.Tick`/`TaskExecutor.Tick` in the
same Postfix, so a task authored this pass is first screened/scheduled/
executed on the following WorldTick — no same-tick race.

## Config

None. Always-on by construction (the P18/P22 deterministic-director
precedent). Inert on clients via the authority seam.

## Test inventory (MW01–MW10)

| Suite | Covers |
| --- | --- |
| MW01 | end-to-end authoring (P22 opens → dwell → task), full task shape (type/owner/priority/retries/timeout/target/metadata), live-task suppression |
| MW02 | trigger-surface gate: P22 record tracked-but-unopened and record-absent both refuse authoring |
| MW03 | dwell gate (no authoring below `AuthoringDwellMs`) |
| MW04 | calm gate: real P9 / P14 / P17 injections + snapshot hostiles + warp each block; hostile-from-start keeps P22 silent ⇒ P23 silent |
| MW05 | capacity gate (registry filled to the live cap blocks; freed ⇒ authors) |
| MW06 | anti-churn: 3-authoring budget cap + requeue-block re-arm (fresh task ids) |
| MW07 | P19 screening end-to-end via `DecisionValidator`: clean pass / stale premise (sector) / stale premise (warp) / argument mismatch |
| MW08 | fail-safe inputs (null/stale/not-started/future) + cadence gate |
| MW09 | authority deny-by-default (null/faulting/non-authoritative ⇒ no-op) |
| MW10 | reconcile (non-terminal/terminal/vanished), decay, re-open with budget reset, Lines/StatusLines determinism, post-reset inert |

Test-side lessons: the DV stale-premise screen runs BEFORE the argument
screen and returns the first verdict — an argument-mismatch test must
publish a calm (no-warp) snapshot first; a P22 episode re-open needs two
real planning passes (arm below dwell, open at dwell — the PD05 discipline).

## Verification (2026-09-08)

- Build: MSBuild Release, 0 warnings / 0 errors.
- Tests: `TOTAL passed=2152 failed=0` ×3 consecutive runs (runs 2–4; raw-
  output grep, zero FAIL lines; suite now 22 files, 2183 output lines).
- Reflection (`verify_build_p23.ps1`): 73/0 checks — MissionWorkDirector
  static class + nested `MissionWorkIntent` + bridge; all 10 public and 4
  private members; 13 constants asserted; IL ownership scans (zero
  forbidden refs, authoring path present, P22 data-only invariant intact);
  DV family strings; `CapBotLog.MISSIONWORK`; patch classes == 11;
  WorldTick postfix IL 708 → 777 bytes.