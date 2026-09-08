# Phase 19: Decision Validator — Contract

**Status: IMPLEMENTED (Alpha 1.2.2).** A diagnostics-only pre-dispatch
validator that reviews QUEUED capability-bound tasks from the WorldTick
Postfix, immediately BEFORE `TaskScheduler.Tick`, and emits bounded
diagnostics when a task's shape contradicts what its dispatcher will do with
it, or when the author's world premise has gone stale. It is a second pair of
eyes, **not** a third arm: it never mutates lifecycle, never re-runs existing
gates, and can never be the reason healthy work dies.

Files: `CapBot/Core/Validation/DecisionValidator.cs` (DecisionValidator),
`DecisionLogBridge.cs` (logging bridge), additive
`CapBotLog.DECISION` subsystem const. No pipeline file changed beyond the
single guarded block in `Patch.cs` WorldTick Postfix and the boot wiring in
`Mod.cs` — the validator holds no task records and owns no lifecycle state.

---

## 1. The ownership argument (what it must NOT duplicate, and why)

The task pipeline already validates exhaustively before anything executes:

- **P7 registry ladder** (13 gates): malformed → disabled → actor allowlist →
  authority → target shape → precondition → task-type allowlist → ownership →
  cooldown → claim conflict → world freshness.
- **P3 recovery** owns ALL cancel/fail/pause decisions (contract: "a stall
  must never become a task" — recovery reacts to outcomes, and no other layer
  may preempt it).
- **P5 claims** + scheduler leases guard duplicate execution.
- **Dispatcher shape checks** (`PulsarCapabilityDispatcher`) run at execution
  with REAL game data (galaxy/encounter membership scans).

The validator deliberately adds nothing to these. It closes exactly the two
evidence-proven gaps the ladder cannot see because they live between layers:

1. **Dispatcher-only shape gaps.** `ISSUE_MOVE_ORDER` carries registry
   `TargetRequirement.None` (the Vector3 location is derived at execution
   from verified task/world data — raw coordinates can never be validated
   from untrusted task text). The registry's gate 5 therefore passes ANY
   target kind, and a non-SECTOR `ISSUE_MOVE_ORDER` task dies post-start at
   the dispatcher (`ExecutorRefused`, stays Queued). The validator surfaces
   that mismatch pre-dispatch, when the diagnosis is actionable, without
   changing the outcome the dispatcher would produce.
2. **Author-premise staleness.** `CAPTAIN_DELIB` (P18), `NAV_RECOVERY`
   (P14), and `MISSION_WORK` (P23) tasks are authored FROM a world snapshot:
   "move to current sector", "add course goal to current sector". If the ship
   changes sector or enters warp between authoring and dispatch, the task
   executes against a premise that no longer holds. The validator compares
   the task's claimed sector against the CURRENT snapshot premise (positive
   evidence only) and emits `DecisionRejected` with a bounded staleness
   reason. It does not cancel the task — P3 recovery owns that decision; the
   diagnostic arms it. (Phase 23 added `MISSION_WORK` to the family list —
   additive, same authoring shape: one task bound to ISSUE_MOVE_ORDER with
   the current-sector premise.)

Never re-validated (each for a specific reason): registry target-shape for
SectorId/ShipId/BoundedToken requirements (gate 5 owns it — the validator
would need live game data it must not touch); galaxy/encounter membership
(the dispatcher checks it with real data; the validator only sees snapshot
data and may not double-authorize on it); cooldowns/claims/ownership (P7
gates 8-10); dependency/deadline scheduling (P4).

## 2. What the validator does

One pass per cadence window (1s, matching scheduler `MinRecheckMs`):

1. Authority gate: probe null/faulting/false → silent no-op (deny-by-default,
   house pattern).
2. Cadence gate: unchecked subtraction, same-timestamp safe.
3. Snapshot fail-safe: null / never-captured / stale (>20s) / future / not
   started → `DecisionUncertain <reason>` (throttled to one line per pass),
   zero rejections, return. Fail-open on uncertainty.
4. Bounded scan of `TaskRegistry.LiveSnapshot()`: Queued tasks WITH a
   `CapabilityId` metadata binding only. Tasks without the binding (P9
   EMERGENCY coordination tasks — known-fail-by-contract, Phase 9 §9) are
   skipped silently; the validator must not spew diagnostics about behavior
   that is already contract-documented.
5. Per-task screens (below) under the caller's tick. Diagnostics lines are
   collected and fired AFTER the scan (bounded ≤4/pass).

## 3. The screens (only the evidence-proven gaps)

**(a) Dispatcher-only shape screens** — mirror the dispatcher's own code
exactly, no new reads:

- `ISSUE_MOVE_ORDER`: TargetKind must be `SECTOR`, TargetId must parse as
  int ≥ 0 (registry can't check: TargetReq None).
- `SET_CAPTAIN_ORDER`: TargetId must parse into the verified vanilla order
  vocabulary {1, 4, 6, 8, 9, 10, 11, 12, 13}.
- `ADD_COURSE_GOAL` / `REMOVE_COURSE_GOAL`: TargetKind `SECTOR` + int ≥ 0.
- `SET_CAPTAIN_TARGET` / `CLEAR_COURSE_GOALS` / `READ_WORLD_SNAPSHOT`: no
  dispatcher-only gap exists (registry gate 5 already enforces ShipId/None
  shapes) — no shape screen.

**Author-premise staleness** (only for the snapshot-authored families,
`CAPTAIN_DELIB`, `NAV_RECOVERY`, and — since Phase 23 — `MISSION_WORK`,
SECTOR-targeted):

- Snapshot `Navigation.CurrentSectorId != claimed sector` (both readable) →
  `DecisionRejected ... reason=stale premise: sector changed`.
- Snapshot `Navigation.InWarp == true` → `DecisionRejected ... reason=stale
  premise: in warp`.
- Nav section missing / CurrentSectorId −1 / unreadable target → uncertain
  marker (counted, NO line, NO rejection). Positive evidence only.

**Bounded sanity**: `Argument` metadata present AND != TargetId on a
SECTOR-target task → `reason=argument mismatch` (authoring bug — P18/P14 set
Argument = sector text; divergence is surfaced, not fixed).

## 4. Fail semantics (the load-bearing rule)

- **FAIL-OPEN on uncertainty.** Any unreadable input produces an uncertain
  marker or a silent skip — never a rejection. An uncertain validator must
  never be the reason healthy work dies; the P7/P5/P3 ladder still guards
  execution.
- **FAIL-CLOSED on action.** The validator holds no task records, calls no
  lifecycle API, and mutates nothing. Worst case = a diagnostic line. It
  cannot cancel, fail, pause, or reorder anything — recovery (P3) owns every
  cancel/fail/pause decision; this layer only makes the evidence visible at
  the moment it can still be acted on cheaply.

## 5. Diagnostics vocabulary (house style)

- `DecisionValidated #<id> cap=<cap> type=<type>` — clean pass (counted; the
  per-task clean pass emits no line to keep the quiet path silent — the
  counter is the signal).
- `DecisionRejected #<id> cap=<cap> type=<type> reason=<reason>` — bounded
  reasons: `target kind mismatch`, `order id unknown`, `stale premise:
  sector changed`, `stale premise: in warp`, `argument mismatch`.
- `DecisionUncertain <reason>` — fail-open pass marker (cadence already
  throttles; CapBotLog spam guard bounds the bridge).
- Counters: Validations, Rejections (StalePremise + Shape subsets),
  UncertainPasses, LastUncertainReason. Readbacks: `Lines()`,
  `StatusLines()`, per-counter getters. All bounded.

## 6. Gates (mirroring P16/P17/P18)

Evaluate(nowMs) order: authority (deny-by-default, fault = no-op) → cadence
(unchecked subtraction) → snapshot fail-safe (null/never/stale/future/not-
started → uncertain, return) → bounded scan under lock → lines fired after
lock release. Screens run per-task with no nested lock (state mutated
directly through the static state object; no ref indirection).

## 7. Data flow

WorldTick Postfix (master client) → `DecisionValidator.Evaluate(nowMs)` →
reads `TaskRegistry.LiveSnapshot()` (bounded ≤64) + `WorldStateService.Latest`
via seams → screens → diagnostics via `DecisionLogBridge` →
`CapBotLog.Info(CapBotLog.DECISION, line)`. TaskScheduler.Tick runs
immediately after in the same Postfix — screened tasks are still Queued when
screened (no race with grants/leases/claims), and every rejection is
pre-dispatch by construction.

## 8. Multiplayer authority model

Master-gated: the Evaluate block sits after the WorldTick `isMaster` gate.
Authority seam wired to `ExecutionClaims.IsAuthoritative` (deny-by-default on
clients and when unset) — clients stay silent. No network calls, no whitelisted
RPCs, decisions as data only.

## 9. Performance contract

Cadence-gated 1s (matches scheduler MinRecheckMs — no extra tick cost on the
quiet path). Per pass: one LiveSnapshot alloc (bounded ≤64), zero LINQ, zero
FindObjectsOfType, bounded ≤4 diagnostics lines, bounded counters. No new
Harmony patch class (permanent ceiling 11; WorldTick Postfix extended in
place — IL 499 → 533). MaxStaleSnapshotMs = 20000 reuses the P16/P17/P18
director threshold — one shared freshness standard; P6's own
MaxSnapshotAgeMs (10s) stays the world layer's rule (NOT a third standard).

## 10. Verified APIs used

- `CapBot.Core.Tasks.TaskRegistry.LiveSnapshot()` (Phase 2, VERIFIED)
- `CapBotTask.State/TargetKind/TargetId/TaskId/TaskType/GetMetadata` (Phase 2, VERIFIED)
- `CapBot.Core.Executor.TaskExecutor.MetadataCapabilityId/MetadataArgument` (Phase 8, VERIFIED)
- `CapBot.Core.World.WorldSnapshot.Navigation / NavigationSnapshot.CurrentSectorId / InWarp` (Phase 6, VERIFIED)
- `RegisteredCapabilities.IssueMoveOrder/SetCaptainOrder/AddCourseGoal/RemoveCourseGoal` consts (Phase 7, VERIFIED)
- `PulsarCapabilityDispatcher.CaptainOrderVocabulary {1,4,6,8,9,10,11,12,13}` (Phase 8, VERIFIED — mirrored, not re-derived)
- `ExecutionClaims.IsAuthoritative` (Phase 5, VERIFIED)
- All game data flows through the P6 snapshot — zero new game reads.

## 11. Failure modes (all fail-safe)

- Validator throws → caught by its own Patch try/catch, logged
  (`DECISION "Decision validator tick failed"`), pipeline continues.
- Authority probe faults → no-op (fail-closed, P18 pattern).
- World provider faults → null snapshot → uncertain (fail-open).
- Task data unreadable → uncertain or silent skip, never a rejection.
- Log bridge not attached → listener null → lines silently dropped,
  counters still accurate.

## 12. Test coverage (DV01-DV14)

`tests/DecisionValidatorTests.cs` (476 lines): DV01 clean pass + DV01b real
P18 authoring path validates clean end-to-end; DV02 wrong kind; DV03/DV03b
bad sector ids; DV04/DV04b order vocabulary; DV05a-c course-goal shapes +
REMOVE premise; DV06/DV07 stale premise (sector changed / in warp), task
left Queued (diagnostics-only verified); DV08a-e fail-open (null/never/
stale/future/not-started → uncertain, zero rejections); DV09 uncertain
premise data (CurrentSectorId −1); DV10 authority deny-by-default (null /
false / faulting probe = no-op); DV11 cadence (same-timestamp safe);
DV12/DV12b argument mismatch + missing-argument clean; DV13 no-capability
EMERGENCY task skipped silently; DV14 counters/readbacks determinism +
ResetForTests clears state AND seams. 78 checks. Same-timestamp cadence
discipline (Advance between evals) exercised throughout.

## 13. Deliberate scope boundaries (documented)

- No lifecycle mutation, ever. P3 recovery owns cancel/fail/pause.
- No re-validation of P7 gates 1-13, P3 rules, P5 claims, P4 scheduling.
- No galaxy/encounter membership checks (dispatcher's job with real data).
- No screens for SET_CAPTAIN_TARGET / CLEAR_COURSE_GOALS /
  READ_WORLD_SNAPSHOT beyond premise checks (no dispatcher-only gap).
- No third freshness standard (20s director threshold reused).
- No new Harmony patch class; no new config values; no P6 schema change.
- EMERGENCY tasks without capability bindings are skipped (known-fail-by-
  contract, Phase 9 §9 — not a validator concern).
- Diagnostics-only means diagnostics-only: a rejection never blocks a task;
  if the validator and dispatcher ever disagree, the dispatcher wins and the
  diagnostic line is reviewed post-hoc.