# Phase 14: Navigation Recovery — Contract

**Status: IMPLEMENTED (Alpha 1.2.2).** Bounded deterministic director that
maintains the ship's course-goal health over the P2–P13 contracts. It is
**not** a pilot, **not** an autopilot, and **never** executes anything: it
observes the P6 navigation snapshot, opens bounded recovery plans, and creates
DATA-only NAV_RECOVERY tasks. Every action still routes through the scheduler
(P4), execution claims (P5), capability validation (P7) and the executor (P8),
exactly as for every other task.

Files: `CapBot/Core/Navigation/NavigationRecovery.cs` (NavRule enum,
NavPlanRecord, NavigationRecoveryDirector), `NavigationLogBridge.cs` (logging
bridge). World input rides the Phase 6 snapshot (no separate capture path).

---

## 1. Core principle — coordinate, never fly

- Vanilla navigation stack is **untouched**: `PLFlightAI`, `PLBotController`,
  `PLStarmap`, `m_ShipCourseGoals` — no Harmony patch, no direct mutation. The
  only course-goal mutations are the already-verified P7 capability channels
  (`ADD_COURSE_GOAL` → `PLServer.AddCourseGoal(Int32)` [PunRPC],
  `REMOVE_COURSE_GOAL` → `PLServer.RemoveCourseGoal(Int32)` [PunRPC]),
  dispatched by the P8 executor exactly as for every other task.
- **Fail-safe rule:** on missing / stale / future-dated / never-captured /
  not-started snapshots, missing navigation section, or unknown (NaN/-1)
  rule inputs, the director marks the situation uncertain
  (`NavRecoveryUncertain …` line), creates nothing, and takes no action.
- **Recovery means re-affirming the CURRENT sector — via GoalReached
  removal only (revised P39).** The director never invents destinations.
  **P39 root-cause fix:** re-affirming the sector the ship is already in
  is a no-op — GoalReached instantly "reaches" it and removes it, and the
  two rules formed an ADD/REMOVE oscillator (~125 zero-effect cycles in
  the P38 session). CourseLost is therefore **REPORT-ONLY** (one bounded
  `NavCourseLostReport` line per plan-open; the vanilla starmap owns
  unprompted routing). Only GoalReached removal of genuinely stale goals
  still tasks, through registered P7 capabilities.
- Deny-by-default authority: no probe / faulting probe / non-master →
  `Evaluate` is a no-op. Production wiring is `ExecutionClaims.IsAuthoritative()`
  (master-client state), so **clients never produce recovery tasks**.
- No LLM/Ollama/Qwen anywhere in the loop. Every decision is a pure function
  of (verified snapshot data, bounded rule table, clock). All vocabulary is
  static code.

## 2. Rules (all thresholds are `public const`s on NavigationRecoveryDirector)

| Rule | Trigger (verified inputs only) | Dwell | Action |
|------|-------------------------------|-------|--------|
| CourseLost | `CourseGoals.Count == 0`, `!InWarp`, `CurrentSectorId >= 0` | — | **REPORT-ONLY (P39)**: bounded plan record + one `NavCourseLostReport` line per plan-open; vanilla starmap owns unprompted routing |
| GoalReached | `CourseGoals[0] == CurrentSectorId`, `!InWarp`, `CurrentSectorId >= 0` | 15 s (`GoalDwellMs`) | REMOVE_COURSE_GOAL task for that goal |
| StuckStall | active course + `DistMovedInLast5s < 1 m` while `TimeSeekingTargetSec > 7 s` (vanilla stuck signature, research §3.4) + valid nav metrics | — | **REPORT-ONLY**: bounded plan record + one `NavStallReport` line; vanilla stuck-teleport owns physical unsticking |

Unknown data NEVER triggers: NaN metrics, -1 sector ids, missing sections,
in-warp states. StuckStall is report-only by executor contract: a task without
`CapabilityId` metadata fails at start (`FailStarted`), so a stall must never
become a task — it is a data record only.

## 3. Plans and tasks

- `NavPlanRecord`: PlanId `"NAV:<rule>:S<sectorId>"` (StuckStall uses
  `"NAV:Stuck:S<sectorId|unknown>"`), Rule, SectorId, FirstSeenMs, TaskId
  (0 = report-only), TaskResolvedMs (-1 = none), LastSeenMs, UpdateCount.
  `HasLiveTask` = TaskId > 0 && TaskResolvedMs < 0.
- One plan per condition; re-detection refreshes `LastSeenMs` and suppresses
  duplicates while a task is live (`DuplicatesSuppressed` counter).
- Task recipe: `CapBotTask.Create("NAV_RECOVERY", "CAPTAIN", reason,
  priority=20, maxRetries=1, timeout=120 s, "SECTOR", "<sectorId>", null)`
  + metadata `NavId` (plan id), `NavRule`, `Preemptible="true"`,
  `CapabilityId` (ADD_/REMOVE_COURSE_GOAL), `Argument` (sectorId)
  + `TaskRegistry.Register` + `TryQueue`.
- Priority 20 (`RecoveryPriority`): above normal work (1..8 + aging ≤ 13) and
  below emergencies (110+). No scheduler internals touched.
- Create/queue failures fail safe: the plan stays open and the next dwell
  window re-arms (no retry storm, one bounded retry via maxRetries=1).

## 4. Plan lifecycle bookkeeping (bounded)

- Active plans ≤ 8 (`MaxActivePlans`); overflow sheds the oldest by
  LastSeenMs (tie → lowest key order) with a `NavPlanShed` line.
- History ≤ 16 (`MaxHistory`), resolved/expired plans enqueue + dequeue FIFO.
- Un-reconfirmed plans decay after 30 s (`ActiveExpiryMs`) — a condition that
  stops persisting (fresh snapshot, no rule match) stops refreshing the plan
  and it expires.
- After a plan's task resolves (terminal state or vanished from the registry,
  detected by `ReconcileTasks`), re-arm is blocked for 20 s
  (`RequeueBlockMs`).
- Every counter is readable (`Evaluations`, `TasksCreated`, `ReportsRecorded`,
  `DuplicatesSuppressed`, `PlansExpired`, `TaskResolutions`,
  `StaleRejections`, `LastUncertainReason`) plus deterministic bounded
  diagnostics (`Lines()` ≤ one per active plan, `StatusLines()` = 2 lines).

## 5. Data flow (the mandated pipeline)

```
WorldStateService.Latest (P6 snapshot, 1 Hz refresh)
  -> authority gate (deny-by-default; clients never evaluate)
  -> cadence gate (one pass per MinRecheckMs = 5 s)
  -> snapshot fail-safe gate (stale >20 s / future / never-captured / !GameStarted => uncertain)
  -> rule inputs validated (NaN/-1/missing section => uncertain, no trigger)
  -> hygiene pass (expire un-refreshed plans, bounded history)
  -> Rule 1 CourseLost / Rule 2 GoalReached / Rule 3 StuckStall (report-only)
  -> CapBotTask.Create("NAV_RECOVERY", ...) + Register + TryQueue (P2 lifecycle)
  -> TaskScheduler.Tick grants it (P4)
  -> TaskExecutor pipeline claims/validates/executes (P8 + P5 + P7)
  -> outcome recorded; failure feeds P3 recovery exactly like any task
  -> ReconcileTasks resolves the plan when its task goes terminal/vanished
```

The director never touches steps after task creation except read-only
bookkeeping (ReconcileTasks). It never claims, starts, pauses, cancels, or
retries anything.

## 6. Structured integration points

- **WorldTick driver (Patch.cs, in-place Postfix extension):** after the
  emergency director blocks, `Evaluate(nowMs)` then `ReconcileTasks(nowMs)`,
  each in its own try/catch (`CapBotLog.NAVIGATION`). IL bytes of the Postfix
  grew from 256 to 325 — expected change, no new patch class.
- **Mod.cs boot:** `NavigationLogBridge.Ensure()` +
  `SetAuthorityProbe(ExecutionClaims.IsAuthoritative)` +
  `SetNowMsProvider(TaskClock.NowMs)` + `SetWorldProvider(WorldStateService.Latest)`
  — same seams as the P9 director.
- **Crew integration hook (future):** plans are keyed data
  (`NAV:<rule>:S<sectorId>`); the P13 memory system can later record
  `RememberCrewEvent` when a NavRecoveryTaskCreated line fires (via the
  decision listener), without any director change.

## 7. Multiplayer authority model

- The driver gates on the existing WorldTick authority path; the director
  additionally consults the authority seam (`ExecutionClaims.IsAuthoritative()`),
  fail-closed: a faulting probe denies authority. Clients therefore cannot
  produce recovery plans or tasks.
- No duplicate Photon actions: recovery tasks carry the same identity/dedup
  protections as every other task (P5 claims + P7 claim-probe + duplicate
  execution ledger); one live task suppresses duplicates.

## 8. Performance contract

- No per-frame loop: the driver calls Evaluate + ReconcileTasks per WorldTick
  frame; Evaluate self-throttles to one pass per `MinRecheckMs` (5 s).
- Detection reuses the P6 snapshot already refreshed at 1 Hz — no extra game
  queries, no FindObjectsOfType, no LINQ on hot paths.
- Bounded allocations: pending list capacity 2, expired list capacity ≤ 8;
  bounded collections everywhere (plans ≤ 8, history ≤ 16).

## 9. Failure modes (all fail-safe)

| Situation | Behavior |
|-----------|----------|
| No snapshot / never captured | `NavRecoveryUncertain` logged, nothing created |
| Snapshot older than 20 s or future-dated | counted (`StaleRejections`), nothing created |
| `!GameStarted` / navigation section missing | nothing created |
| NaN/unknown rule inputs | that rule never triggers; others may still evaluate |
| Faulting world provider / authority probe | fail-closed no-op |
| Task creation refused | plan stays open; next dwell window re-arms (no storm) |
| Task vanishes from registry | reconcile resolves the plan after the re-arm delay |
| Persisting condition | exactly ONE live task per plan; duplicates suppressed |
| Condition stops persisting | plan stops refreshing → expires after 30 s → re-opens later if condition returns |

## 10. Verified PULSAR APIs used (no new game APIs)

| API | Status |
|-----|--------|
| `PLServer.AddCourseGoal(Int32)` [PunRPC] | VERIFIED (P7 research) — executor dispatch channel for ADD_COURSE_GOAL |
| `PLServer.RemoveCourseGoal(Int32)` [PunRPC] | VERIFIED (P7 research) — executor dispatch channel for REMOVE_COURSE_GOAL |
| Navigation snapshot fields (P6 source) | VERIFIED — CurrentSectorId/Name, InWarp, CourseGoals, DistMovedInLast5s, TimeSeekingTargetSec, HasBotNavigationMetrics |
| Stuck signature (moved <1 m / 5 s, seeking >7 s) | VERIFIED vanilla constant (PULSAR_GAMEAI_RESEARCH §3.4) — data-only input |
| PLFlightAI / PLBotController / PLStarmap / m_ShipCourseGoals | NOT TOUCHED (deliberate) — vanilla navigation stack remains authoritative |

## 11. Test coverage

`tests/NavigationTests.cs` — 74 checks covering N01–N12: CourseLost
end-to-end (task shape, metadata, target = current sector, queued through the
standard pipeline, duplicate suppression), dwell + requeue-block windows,
GoalReached + negatives (second goal, in-warp, unknown sector), StuckStall
report-only (no task ever, report once), plan lifecycle (expire → history,
bounded shed at 8 with `NavPlanShed`), fail-safe inputs (null/stale/
not-started/NaN), authority deny-by-default (null/faulting/non-master),
ReconcileTasks (vanished task → resolution), no unauthorized execution (task
stays Queued, no lease, no claim, `TryClaim` → `RejectedNotAuthoritative`),
pipeline isolation under nav churn, cadence + counters + diagnostics
determinism. Full suite: **1382/1382 pass** (1308 prior + 74 new). The f13
hook runs after f12; run_tests.ps1 compiles the navigation domain files +
test file with the rest.

## 12. Deliberate scope boundaries (documented)

- No warp replanning: InWarp states never trigger rules (warp target/goal
  interplay is unverified).
- No route suggestion beyond the current sector: the director never invents
  destinations.
- No physical unsticking: vanilla owns stuck-teleport; StuckStall is a
  coordination report.
- No new Harmony patch class (permanent ceiling of 11 preserved; WorldTick
  Postfix extended in place).