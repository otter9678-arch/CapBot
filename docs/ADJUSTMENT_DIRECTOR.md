# Adjustment Observer (Phase 24)

## Purpose and scope

`AdjustmentDirector` (namespace `CapBot.Core.Adjustment`) is the phase the
pipeline's contracts label "self-adjustment / re-planning" — shipped as
**bounded outcome readback** (recommend-only DATA), not as an actuator.

**Design basis note:** no master-plan document exists in the workspace
defining Phase 24 (verified by the P24 research report — "Phase 24/P24",
"self-adjust", "replanning" searches: zero mandates; the only numbered
references are prohibitions: `TASK_RECOVERY.md:95-96` "re-deriving the task
toward a *new* target is re-planning (later phases), not recovery — this
phase never invents new intent", `NAVIGATION_RECOVERY.md:183` "No warp
replanning"). The design is INFERRED from three verified constraints and the
tree's data-layer-before-consumer pattern (P15 feeds P18, P22 feeds P23):

1. **"Re-planning (re-targeting)" is contractually blocked for actuation** —
   P3 (recovery) owns every task lifecycle mutation ("every auto-resumer
   owns exactly its own pauses"; the MUST-NOT lists in
   `MISSION_WORK_DIRECTOR.md`), and re-deriving tasks toward new targets is
   explicitly reserved to "later phases" by the recovery contract itself.
2. **No safe authoring channel exists** — the P23 ownership census (fresh
   post-P23, in `MISSION_WORK_DIRECTOR.md`) shows ISSUE_MOVE_ORDER already
   carries exactly two authors (P18 + P23) with a documented priority-
   serialization justification; a third author or a new capability
   (registration is inert without a dispatcher branch) would violate the
   ownership-audit discipline.
3. **The "adjust/adaptive" vocabulary in the tree is assigned to Phase 25**
   (trait adjustment via `SetPersonality`, `CREW_EXPERIENCE.md:127-134`,
   `CHANGELOG.md:949`) — P24 is the layer that OBSERVES; P25 is the layer
   that ADJUSTS (traits).

If the external master plan (PART 0–68) mandates an actuating P24
(mutating `BackoffBaseMs`/`BackoffMultiplier` or re-authoring tasks at
adjusted priorities/targets), that phase is the candidate for
re-alignment — the ownership covenant would need explicit renegotiation.

## What the observer does (signal vocabulary)

Three deterministic rules over bounded public readbacks, polled on a 5 s
cadence (poll-based by construction: every listener seam in the tree is
single-slot and boot-occupied by LogBridges — the observer never
subscribes):

1. **CHURN** — a live task is `Failed` or carries retries
   (`RetryCount >= ChurnRetryThreshold`=1). The pipeline is struggling
   with its own work. Detail: up to `MaxChurnTasksPerLine`=3 task ids with
   reasons (`#123:failed`, `#124:retried`). The observer reads the retry
   state that P3 recovery owns and never drives a retry itself.
2. **STARVE** — capacity pressure: the registry is saturated
   (`LiveCount >= MaxLiveTasks`=64), or an authoring-refusal /
   capacity-gate counter DELTA appeared since the previous readable pass
   (CaptainDirector `AuthoringRefusedCount`/`AuthoringCappedCount`,
   PlanningDirector `CapacityGateBlockCount`). Deltas, not levels — the
   counters themselves grow forever; only the delta is a signal.
3. **DRIFT** — `PlanningDirector.DriftReportCount` increased since the
   previous readable pass. The P22 premise-drift layer reported —
   premise-carrying task families (CAPTAIN_DELIB, NAV_RECOVERY) may be
   stale; this is the planning-granularity mirror of the P19
   stale-premise screens, now at meta granularity. P19 keeps full
   ownership of its per-queued-task screens (the observer never calls the
   validator).

Every signal line carries `(recommend-only; no behavior change in Phase
24)`. The last recommendation is readable via `LastRecommendation` (data
for P25 adaptive learning / P28 persistence / P29 dashboard; nothing
consumes it in this phase).

## One-shot record semantics (anti-churn)

Per-record state machine (`AdjustmentRecord`, tracked ids `ADJ:CHURN`,
`ADJ:STARVE`, `ADJ:DRIFT`):

- condition first true ⇒ arm the record + emit the report;
- condition stays true ⇒ silent refresh (`DuplicatesSuppressed`);
- condition clears (seen-flag swept each pass) ⇒ quiet;
- re-fire after a clear ⇒ re-report only after
  `AdjustmentRecheckBlockMs`=20000 from the last REPORT (else
  `RecheckBlocks++`, silent — the condition still marks seen);
- condition absent past `ActiveExpiryMs`=30000 ⇒ hygiene decays the
  record (`AdjustmentRecordExpired` one-shot line); a re-fire after decay
  arms a FRESH record and reports immediately (budget re-armed).

The first readable pass after boot (or authority re-acquisition) arms the
counter baseline ONLY — deltas need a previous pass, so the very first
readable pass is observation-free (the P22 premise-capture analogue).
Hygiene, bounded set (≤`MaxActiveRecords`=8, shed-oldest defensive),
history (≤`MaxHistory`=16), ≤`MaxPendingLines`=4 lines/pass — the P18/
P22/P23 house bounds.

## Data-only contract (MUST-NOT list)

The observer MUST NOT (the strictest list in the tree — it watches the
systems everyone else must not touch):

- create, queue, pause, resume, cancel, retry, expire, or claim any task;
  it never calls `CapBotTask.Create` / any `Try*` /
  `TaskRegistry.Register`. `TaskRegistry` is read through `LiveSnapshot`
  and `LiveCount` reads ONLY. P3 (recovery) owns every lifecycle
  decision — the observer owns no pauses and resumes nothing;
- call `TaskScheduler`, `TaskRecoveryManager`, `TaskExecutor`,
  `ExecutionClaims` (beyond its own deny-by-default authority seam),
  `CapabilityRegistry`, `DecisionValidator`, or the capability
  dispatcher;
- mutate another phase's knobs — `TaskRecoveryManager.BackoffBaseMs` /
  `BackoffMultiplier` are documented "bounded, test-settable"
  configuration; no component anywhere mutates another phase's statics;
- run LLM paths or interpret metadata/log text as behavior
  (EXECUTION_SAFETY security posture); deterministic always, same
  inputs ⇒ same signals (test-verified);
- make game/RPC calls, scene scans, `FindObjectsOfType`, LINQ, or
  per-frame work (cadence-gated 5 s);
- add a Harmony patch class (the WorldTick Postfix gains one guarded
  block IN PLACE after the P23 block; 11-class ceiling kept);
- read stale world: snapshot fail-safe (null / never-captured / stale
  >20 s / future / `!GameStarted` ⇒ uncertain, no signals); readback
  seam faults ⇒ fail-closed uncertain pass (never signal from a
  half-read pipeline).

Reflection-verified: zero forbidden IL references (lifecycle mutators,
scheduler/recovery/executor/claims/validator/dispatcher/Photon/scene
scans/capability RPCs); reads = `TaskRegistry.LiveSnapshot` +
`get_LiveCount` + the CaptainDirector/PlanningDirector counter getters.

## Files

| File | Role |
| --- | --- |
| `CapBot/Core/Adjustment/AdjustmentDirector.cs` | Adjustment observer core: gates, three signal rules, one-shot record lifecycle, hygiene (~570 lines) |
| `CapBot/Core/Adjustment/AdjustmentLogBridge.cs` | Boot attach of the `ADJUSTMENT` log subsystem |
| `CapBot/Core/Logging/CapBotLog.cs` | +`ADJUSTMENT` const (additive) |
| `CapBot/Mod.cs` | Boot wiring: bridge + authority/now/world seams (no config toggle — P18/P22/P23 precedent) |
| `CapBot/Patch.cs` | `WorldTick` postfix: one guarded `Evaluate` block after the P23 block (11 patch classes preserved) |
| `CapBot/CapBot.csproj` | +2 Compile entries |
| `tests/AdjustmentDirectorTests.cs` | ADJ01–ADJ10 (~90 assertions) |
| `tests/run_tests.ps1`, `tests/TaskRecoveryTests.cs` | Suite registration (23 domain files, 14 suites) |

## Gate order (house shape)

`Evaluate(nowMs)`: authority (deny-by-default, null/fault ⇒ no-op) ⇒
cadence (5 s, wrap-safe unchecked subtraction) ⇒ snapshot fail-safe
(null / never-captured / stale>20 s / future / `!GameStarted` ⇒
uncertain line, no signals) ⇒ readbacks outside the lock (one-way lock
order: Adjustment takes nobody's lock while held; fault ⇒ fail-closed
uncertain) ⇒ bounded rules under one lock ⇒ emission loop (≤4 lines per
pass). Never throws. Authors nothing.

## Constants

| Constant | Value | Meaning |
| --- | --- | --- |
| `MinRecheckMs` | 5000 | decision cadence (P18/P22/P23 parity) |
| `MaxStaleSnapshotMs` | 20000 | shared freshness standard |
| `MaxActiveRecords` | 8 | bounded tracked-record set (3 kinds; defensive cap) |
| `MaxHistory` | 16 | bounded resolved-record history |
| `ActiveExpiryMs` | 30000 | condition absent past this ⇒ record decays |
| `AdjustmentRecheckBlockMs` | 20000 | re-arm delay between reports of one record |
| `ChurnRetryThreshold` | 1 | a live task with this many retries is "churning" |
| `MaxPendingLines` | 4 | bounded emission buffer per pass |
| `MaxChurnTasksPerLine` | 3 | bounded task-id list in a churn line |
| `TrackIdPrefix` | `"ADJ:"` | `"ADJ:<kind>"` |

## Multiplayer authority

Host-only evaluation (deny-by-default `ExecutionClaims.IsAuthoritative()`
seam, null/fault ⇒ no-op). The observer never RPCs and never syncs; its
signals are process-local diagnostics (the pipeline's claims/leases/
ledger are already process-local per `EXECUTION_SAFETY.md`).

## Config

None. Always-on by construction (the P18/P22/P23 deterministic-director
precedent — config toggles exist only for the optional LLM advisors). Inert
on clients via the authority seam. The observer does not read or write any
SaveValue, and no existing config value feeds it.

## Test inventory (ADJ01–ADJ10)

| Suite | Covers |
| --- | --- |
| ADJ01 | churn end-to-end (real failed/retried live task ⇒ `ADJ:CHURN` with bounded detail + recommend-only tag), persisting-condition silent refresh, ≤3-ids detail bound |
| ADJ02 | clear ⇒ seen-flag reset; re-fire inside 20 s ⇒ blocked (`RecheckBlocks`); re-fire past window ⇒ genuine re-report; blocked fire keeps seen |
| ADJ03 | starve: registry saturation (real P22 capacity-gate pass in the loop) ⇒ `capacity=64/64` detail; counter-delta-only path (63 live) ⇒ `planCapacity` detail |
| ADJ04 | drift: real P22 premise-drift passes ⇒ `ADJ:DRIFT planningDrift+1`; observer re-fire inside window blocked; quiet-pass seen-reset; re-report past both windows |
| ADJ05 | fail-safe inputs (null/stale/future/not-started) + cadence gate; fail-safe passes never arm a bogus baseline |
| ADJ06 | authority deny-by-default (null/faulting/non-authoritative ⇒ no-op); baseline NOT armed without authority (verified by first-authorized-pass-arms-only) |
| ADJ07 | first readable pass arms baseline only (signal-free), second pass reports |
| ADJ08 | hygiene: clear ⇒ decay past `ActiveExpiryMs` ⇒ expired one-shot line; re-fire after decay ⇒ fresh record + immediate report |
| ADJ09 | determinism (identical scenario re-run ⇒ identical id-free diagnostics) + data-only proof (registry size + P22 counters untouched) + `GetRecord` null-safety + post-reset inert |
| ADJ10 | multi-signal pass (churn + starve + drift in one pass ⇒ 3 signals ≤ `MaxPendingLines`) + multi-expire |

Test-side lessons: `AdjustmentRecordExpired` lines are themselves reports
(one-shot semantics — a decay pass returns 1, not 0, P15/P22/P23 record
semantics); a P22 drift needs an ARMED premise first (a drift pass with no
prior readable pass only arms); counter-delta starve requires the P22
block to happen on a pass where the registry IS saturated, then free a
slot before the observer pass to isolate the delta path.

## Verification (2026-09-08)

- Build: MSBuild Release, 0 warnings / 0 errors.
- Tests: `TOTAL passed=2275 failed=0` ×3 consecutive (runs 3–5; raw-output
  grep, zero FAIL lines; suite now 23 domain files, 14 suites).
- Reflection (`verify_build_p24.ps1`): 80/0 — static class + nested
  `AdjustmentRecord` + bridge; 12 public members + 4 private probed; 13
  consts verified; IL ownership scans (zero forbidden refs incl. every
  `Try*` lifecycle mutator, scheduler/recovery/executor/claims/validator/
  dispatcher, PhotonNetwork, FindObjectsOfType, capability RPC names;
  reads = `TaskRegistry.LiveSnapshot` + `get_LiveCount` only); WorldTick
  postfix IL references `AdjustmentDirector.Evaluate`; Harmony patch
  classes == 11; PlanningDirector/MissionWorkDirector types intact.