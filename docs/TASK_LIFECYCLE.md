# Task Lifecycle — Phase 2 Documentation

Status: **infrastructure only** — no gameplay routes through the task system yet.
Future phases (scheduler P4, recovery P3, duplicate-execution protection P5,
directors P15–P17, Captain Brain 2.0 P18) must build on the contracts below and
may not weaken them.

## Files

| File | Role |
|---|---|
| `Core/Tasks/TaskState.cs` | `TaskState` enum, `TaskIds` (identity source), `TaskClock` (wrap-safe ms clock), `TaskTransitions` (the single legal-transition table) |
| `Core/Tasks/CapBotTask.cs` | The task model: immutable identity/scheduling inputs, guarded lifecycle mutations, deterministic status reporting |
| `Core/Tasks/TaskRegistry.cs` | Bounded registry of live + historical tasks; transition listener hook |
| `Core/Tasks/TaskLogBridge.cs` | Attaches Phase 1 `CapBotLog` as the registry's transition listener at mod boot |

The task domain is **pure C#** (references only `System*`) — no UnityEngine, no
PULSAR/PML types. It compiles and can be tested without the game.

## Task states

`Created → Queued → Running → (Paused ↔ Running) → Completed | Failed | Cancelled | Expired`

- `Created` — object exists, not yet offered to any queue.
- `Queued` — waiting to be picked up (Phase 4 will own queues).
- `Running` — an owner is executing it (Phase 8 will own execution).
- `Paused` — execution temporarily suspended; resumable only to `Running`.
- `Completed` — terminal success.
- `Failed` — attempt failed; may retry back to `Queued` while `RetryCount < MaxRetries`; otherwise must end `Cancelled`/`Expired` (or stay Failed — Failed is a valid resting state).
- `Cancelled` — terminal; owner or system aborted it. `CancellationReason` set.
- `Expired` — terminal, deadline elapsed (from any non-terminal state). `CancellationReason = "timeout"`.

## Valid transitions (complete table)

| From \ To | Queued | Running | Paused | Completed | Failed | Cancelled | Expired |
|---|---|---|---|---|---|---|---|
| **Created** | ✅ queue | — | — | — | ✅ reject-at-validation | ✅ cancel | ✅ expire |
| **Queued** | — | ✅ start | — | — | — | ✅ cancel | ✅ expire |
| **Running** | — | — | ✅ pause | ✅ complete | ✅ fail | ✅ cancel | ✅ expire |
| **Paused** | — | ✅ resume | — | — | — | ✅ cancel | ✅ expire |
| **Failed** | ✅ retry (≤ MaxRetries) | — | — | — | — | ✅ cancel | ✅ expire |
| **Completed** | — | — | — | — | — | — | — |
| **Cancelled** | — | — | — | — | — | — | — |
| **Expired** | — | — | — | — | — | — | — |

Terminal states (`Completed`, `Cancelled`, `Expired`) have **no outgoing
transitions**. Every other cell is an **illegal transition** and is rejected
(`Try*` returns `false`; state, timestamps and reasons are left untouched).

## Invalid transitions (explicitly rejected, non-exhaustive)

- `Created → Running` (must be queued first)
- `Created → Completed` (unstarted work cannot succeed)
- `Queued → Paused`, `Queued → Completed`, `Queued → Failed`
- `Paused → Completed`, `Paused → Failed`
- `Completed/Cancelled/Expired → anything` (double-completion is impossible)
- `Failed → Running` directly (must pass back through `Queued`)
- `Failed → Failed` (re-fail without retry)

## Invariants

1. **Identity:** `TaskId` is assigned once by `TaskIds.Next()` (monotonic counter, never reused in-session) and is immutable. Equality/hash is TaskId-only.
2. **Single mutator:** task state changes ONLY via `CapBotTask.ApplyTransition`, which consults `TaskTransitions` first. No other code may assign `State`.
3. **Terminal stamps are once-only:** `CompletedTimeMs` is stamped exactly once (guarded by `< 0` check); a terminal task can never re-stamp.
4. **Idempotence:** calling the same `Try*` twice returns `false` the second time and leaves the task unchanged (first call already moved it to terminal).
5. **Reasons are static-safe:** `FailureReason`/`CancellationReason` are truncated to 200 chars; they are treated as **data, never executed** (master safety rule).
6. **Registry mirrors state:** the registry bucket move (live→history) happens inside the same `RecordTransition` call chain triggered by the task's own transition — buckets can never disagree with task state.
7. **Bounded memory:** ≤ 64 live tasks (registration fails at the cap — no eviction), ≤ 128 history entries (ring drop), ≤ 16 dependencies, ≤ 32 metadata entries, ≤ 64-char keys/types/owners, ≤ 200-char reasons. Nothing in the domain grows unbounded.
8. **No gameplay execution:** the task model holds no game-object references, exposes no execution method, and never calls PULSAR/PML APIs. `TargetKind`/`TargetId` are opaque strings.
9. **Progress is bounded:** clamped to [0, 1]; `Completed` tasks report exactly `1`.
10. **Clock wrap safety:** all interval math is unchecked 32-bit subtraction (`TaskClock.DeltaMs`), correct for spans ≪ 24.8 days; `TaskClock.ElapsedMs` additionally counts wraps for absolute ages.

## Ownership rules

- `OwnerActorId` is set at creation and immutable (e.g. `CAPTAIN`, `BOT:<playerId>`, `HOST`). Vocabulary is free-form but static per call site; never parsed or dispatched on.
- Phase 2 has no executor: nothing may claim, steal, or reassign ownership yet. Phase 8 (Task Manager) must resolve ownership through the registry, not by mutating tasks.
- Only the owner (or host-level code on behalf of the owner) may move a task through `Queued/Running/Paused/Completed/Failed`; anyone may observe.

## Cancellation rules

- Legal from every non-terminal state (including `Created` and `Failed`).
- `TryCancel(reason)` is idempotent; second call returns `false`.
- From `Failed`, cancelling is final — a cancelled task can never retry.
- Reason is stored (truncated 200 chars) and included in status lines and logs.

## Failure rules

- Legal only from `Running` (an attempt must have started) or `Created` (rejected before first queue, e.g. failed validation).
- `TryFail(reason)` stores `FailureReason`; idempotent.
- `Failed` is semi-terminal: the only forward paths are retry→`Queued`, `Cancelled`, or `Expired`.

## Retry semantics

- `TryRetry()` requires state `Failed` AND `RetryCount < MaxRetries` (both checked before the transition).
- Retry moves the task to `Queued`, increments `RetryCount`, clears `CompletedTimeMs` (task is live again), and **keeps** `FailureReason` (documents the last failure), `StartedTimeMs`, `CreatedTimeMs`, and `Priority`.
- `MaxRetries` is immutable after creation (validated 0–10 at `Create`).
- Exhausted retries leave the task resting in `Failed` — no auto-cancel, so the reason remains inspectable.

## Dependency representation

- `Dependencies` is an immutable `IReadOnlyList<long>` of TaskIds, deduplicated, bounded to 16, captured at creation.
- Phase 2 stores them only — **no scheduling semantics yet**. Phase 4 (Scheduler) owns the rule: a task may leave `Queued` only when every dependency TaskId resolves to a task in `Completed` (looked up via `TaskRegistry.Get`, which covers history).
- Self-dependency is not blocked here (an id cannot depend on itself at creation time since its own id doesn't exist yet) but is meaningless; the scheduler must treat unresolvable dependencies as never-ready (recovery phase P3 owns that policy).

## Lifecycle logging

- Every transition is emitted through the Phase 1 logger via `TaskLogBridge`:
  `[CapBot:TASK] [INFO] Task Registered: #12 NAV_ALIGN owner=CAPTAIN pri=5 state=Queued progress=0.00 age=0ms`
- Task-domain code contains **zero logging calls**; the bridge is attached once at boot (`Mod` constructor → `TaskLogBridge.Ensure()`), keeping the domain pure and testable.
- Deterministic status: `CapBotTask.ToStatusLine(nowMs)` and `TaskRegistry.StatusLines(nowMs)` produce sorted, single-line summaries (TaskId, type, owner, priority, state, progress, retries, reason, age). Phase 29's dashboard should consume these directly.
- All task logging flows through CapBotLog's spam/flood guards, so a misbehaving future phase cannot flood the PML log.

## What Phase 2 deliberately does NOT do

- No scheduler, no queue, no executor (P3/P4/P8).
- No gameplay action routing — the existing captain AI is untouched.
- No duplicate-execution protection (P5) — single-mutator + idempotence here is the groundwork, not the policy.
- No persistence — task state is session-scoped runtime data (P28 will decide what, if anything, is saved; PML save format untouched).
- No new PULSAR/PML API usage — the domain invented none; nothing needed verification because nothing game-facing was called.