# Task Scheduler — Phase 4 Documentation

Status: **orchestration only** — the scheduler selects and orders existing
registered tasks and hands out bounded per-tick grants; it executes nothing,
decides nothing about gameplay, and stays inert until Phase 8 drives `Tick`
host-side. Future phases (capability registry P7, executor P8, directors
P9/P15–P17, Captain Brain 2.0 P18) must build on the contracts below and may
not weaken them.

## Files

| File | Role |
|---|---|
| `Core/Tasks/TaskScheduler.cs` | Grant/lease scheduler: deterministic candidate ordering, dependency + deadline + owner gates, policy-gated preemption, bounded bookkeeping |
| `Core/Tasks/SchedulerLogBridge.cs` | Attaches Phase 1 `CapBotLog` (TASK) as the scheduler's decision listener at mod boot |
| `Core/Tasks/TaskRegistry.cs` (modified) | Added `LiveSnapshot()` — bounded point-in-time list of live tasks (registry internals stay private) |

The scheduler is **pure C#** (references only `System*`) — no UnityEngine, no
PULSAR/PML types. It compiles and can be tested without the game.

## Model: the queue IS the registry

There is **no parallel queue structure**. The Phase 2 registry's `Queued`
state is the only work pool; scheduler state is limited to:

- **Leases** — one per granted task (bounded ≤ 64, the live cap), remembering
  owner + expiry (5 s). A lease is the duplicate-scheduling guard AND the
  future executor's claim seam (`TryTakeLease`).
- **Preemption records** — one per preempted task (≤ 64), dropped when the
  task leaves the live registry (terminal / expunged); the tally survives
  auto-resume as the task's lifetime preemption count.

Both are dropped when their task leaves the live registry; nothing grows
without a registered task.

## Scheduling pass (Tick)

1. **Gate:** ≥ 1 s since the last pass (`MinRecheckMs`) — a per-frame caller
   still yields ~1 s policy (research §11 cadence; vanilla's decision gate is
   1–1.5 s). The P8 driver must additionally gate on `PhotonNetwork.isMasterClient`.
2. **Hygiene:** expire stale leases; prune preemption records whose task left
   the live registry; auto-resume scheduler-initiated preemption-pauses whose
   15 s window elapsed (only scheduler's own pauses — external pauses and
   recovery's capability-pauses are never touched).
3. **Candidates:** live tasks in `Queued` state, not currently leased.
   (`Running` is already executing; `Created` belongs to its creator;
   `Paused`/`Failed` belong to recovery.)
4. **Deterministic order:** effective priority desc → FCFS (`CreatedTimeMs`
   asc) → TaskId asc. Effective priority = `Priority` + bounded aging bonus
   (+1 per 30 s waited, capped at +5) — supports reordering without unbounded
   inflation. Insertion sort (pool ≤ 64, near-sorted, allocation-free).
5. **Gates, per candidate, in order — first refusal skips the task:**
   | Gate | Reason logged | Rationale |
   |---|---|---|
   | deadline already elapsed | `deadline elapsed` | recovery owns expiring; scheduler never schedules dead work |
   | dependencies unmet | `dependencies unmet` | every dep TaskId must resolve to `Completed` (registry `Get` covers history); unresolvable deps ⇒ never scheduled (P3 contract) |
   | owner busy | `owner busy: <id>` | one grant per owner per pass; an owner with a Running task is busy too (execution outlasts the 5 s lease) |

   Deliberately absent: a retry-headroom gate. A `Queued` task always has a
   pending attempt; retry accounting is recovery's job (an exhausted task is
   cancelled from `Failed` and never appears as a candidate), and such a
   gate would starve `MaxRetries = 0` tasks and the legal final-retry
   attempt.
6. **Grants:** at most `MaxGrantsPerTick` (8) per pass. A grant is a
   *suggestion to start work* — not execution. The future executor claims it
   via `TryTakeLease` (consumes it; double claims fail) before moving the
   task `Queued -> Running`.
7. **Preemption pass** (only if grant budget remains): see below.

## Preemption (explicitly policy-gated)

A `Queued` task may displace a `Running` task only when ALL hold:

- the running task's metadata `Preemptible == "true"` (explicit opt-in; the
  default task is **never** preemptible),
- same `OwnerActorId` (displacement is per-owner; cross-owner scheduling is
  not the scheduler's business),
- candidate's effective priority exceeds the running task's by **more than**
  `PreemptMargin` (+2),
- the running task has had ≥ 3 s (`PreemptMinRunMs`) of runtime,
- the running task has been preempted < 2 times lifetime
  (`MaxPreemptionsPerTask`) — the tally survives auto-resume: after the
  scheduler resumes the victim, its record is demoted to a dormant lifetime
  counter and pruned only when the task leaves the live registry,
- the candidate's owner holds no lease on another task (preemption resolves
  a candidate against its own owner's running work; it never queue-jumps
  behind other grants; other Running tasks of the same owner are allowed —
  the lowest effective-priority victim is chosen among them).

The preemption pass applies the scheduler's own gates to the candidate first
(deadline, dependencies, not already granted this pass) — a task the
scheduler would refuse to grant never displaces running work. The owner-busy
gate is the exception by design: being blocked by the victim's own Running
state is exactly what preemption resolves.

The victim is moved with its only legal exit from `Running` besides
completion/failure — `TryPause` (lifecycle-enforced; if the lifecycle
rejects, the preemption is refused and logged). The candidate is granted.
Scheduler-initiated pauses auto-resume after 15 s; **external pauses are
never resumed by the scheduler** (Phase 3 contract: recovery resumes only
capability-pauses; the scheduler resumes only its own preemption-pauses —
every auto-resumer owns exactly its own pauses; after a resume the record is
demoted so a later pass can never auto-resume a pause some other system took
in between).

## Determinism & ordering guarantees

- Same registry snapshot + same clock ⇒ same grants. No randomness, no
  wall-clock reads (Tick callers pass the time source; production passes
  `TaskClock.NowMs`).
- Tie-breaking is total: effective priority → FCFS → TaskId. Equal-priority
  tasks schedule in creation order; identical timestamps fall back to TaskId.
- Priority changes/reordering: `CapBotTask` priority is immutable by design
  (P2 contract); *effective* priority changes only through bounded aging, and
  callers can create a replacement task at a different priority (create →
  cancel old — the lifecycle's cancel path). The scheduler reads whatever is
  in the registry at pass time.

## No-starvation properties

- Aging bonus guarantees a low-priority task gains precedence within ~30 s
  steps (bounded at +5 ≈ 2.5 min worst case before it outranks priority ≤ its
  base − 5).
- `MaxGrantsPerTick` bounds per-pass work; the 1 s gate bounds per-second
  work; FCFS + aging prevents permanent starvation of equal-priority backlog.
- One-grant-per-owner (leases AND Running tasks both count as busy) prevents
  a single owner's flood from monopolizing the scheduler (P8 may add
  work-stealing across owners as policy).
- No infinite loops: every loop iterates a bounded snapshot (≤ 64) or a
  bounded grant budget (8).

## Recovery-state interaction (Phase 3 contract)

- The scheduler **never** retries, fails, expires, pauses-for-capability, or
  resumes recovery-paused tasks. It only *refuses* to schedule:
  - deadline-elapsed tasks (recovery's `SweepExpired` owns expiring),
  - non-`Queued` tasks — recovery-parked tasks (backoff-pending stays Queued
    and remains grantable; failed/paused/terminal are invisible to passes).
  There is deliberately no retry-headroom gate: a Queued task always has a
  pending attempt; exhausted tasks are cancelled by recovery from `Failed`
  and never scheduled again.
- The scheduler's preemption-pauses are distinguishable from recovery's
  capability-pauses (scheduler records track its own), so neither system
  ever resumes the other's pauses.

## Multiplayer / authority constraints (research §9)

- Pure bookkeeping here; the P8 driver must gate `Tick` on
  `PhotonNetwork.isMasterClient` before scheduling (all vanilla AI execution
  is host-side).
- Grants are process-local (host) — nothing syncs, nothing RPCs. When P8
  routes work, it must use the verified vanilla patterns (request →
  master-authoritative response), never `PhotonTargets.All` for actions the
  host already performed.
- The scheduler keeps no transient vanilla AI state (paths/Behave/migration
  hazard): its records reference tasks by id and string owners only.
- Compatible with the four verified captain channels: grants say *what*
  (TaskId/type/owner/priority) — *how* the executor emits channel writes
  (`CaptainSetOrderID`, `IssueMoveOrder`, `Captain_SetTargetShip`,
  `AddCourseGoal`) is P7/P8 policy, not scheduler vocabulary.

## Logging

- One decision line per action via `SchedulerLogBridge` (boot-attached):
  `Granted #12 TEST_TYPE owner=CAPTAIN pri=5 effPri=6 leaseMs=5000`,
  `Refuse #13 dependencies unmet`, `Preempted #7 ... by #15 ...`,
  `LeaseExpired #12`, `GrantLeaseTaken #12`, `PreemptResume #7`.
- The scheduler contains zero logging calls (pluggable
  `SetDecisionListener` keeps the domain pure/testable); the listener fires
  outside the manager's lock; all lines pass CapBotLog's spam/flood guards.

## Security posture

- No arbitrary code execution, no runtime compilation, no DLL loading, no
  shell execution. Task metadata (`Preemptible`, target strings, types) is
  data — read by exact-match comparison (`== "true"`), never parsed into
  code paths, never executed. Mission/NPC/chat text never reaches the
  scheduler at all.

## Explicitly not in this phase

- No executor / capability registry (P7/P8): grants are the contract, unused
  by game code until those phases exist. `Tick` is not wired to any game loop
  this phase — the scheduler is dormant by construction.
- No directors, no Captain Brain 2.0, no LLM anything, no dynamic task
  generation, no persistence, no UI, no performance overhaul.
- No new PULSAR/PML API usage — the domain invented none and calls nothing
  game-facing (alignment report: verified-API-only).
- No refactors of Patch.cs/Autonomy.cs; existing gameplay untouched.