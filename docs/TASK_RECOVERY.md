# Task Recovery — Phase 3 Documentation

Status: **policy layer only** — no gameplay routes through the recovery system
yet. The recovery manager stays inert until Phase 6 (Game/World State) supplies
a real `ITaskWorldProbe` and a future phase drives `TaskRecoveryManager.Tick`.
Future phases (scheduler P4, duplicate-execution protection P5, world state P6,
directors P9/P15–P17, Captain Brain 2.0 P18) must build on the contracts below
and may not weaken them.

## Files

| File | Role |
|---|---|
| `Core/Tasks/TaskRecovery.cs` | `RecoveryActionType`, `ITaskWorldProbe` (the only world seam), `NullWorldProbe`, `RecoveryRecord`, pure decision function `TaskRecoveryPolicy.Decide` |
| `Core/Tasks/TaskRecoveryManager.cs` | Per-task recovery bookkeeping, 1 s recheck gate, decision executor, backoff, action listener hook |
| `Core/Tasks/RecoveryLogBridge.cs` | Attaches Phase 1 `CapBotLog` as the manager's action listener at mod boot |
| `Core/Tasks/TaskLogBridge.cs` (modified) | The single registry listener now also calls `TaskRecoveryManager.Track(task)` on `Registered` |

The recovery domain is **pure C#** (references only `System*`) — no UnityEngine,
no PULSAR/PML types. It compiles and can be tested without the game.

## Relationship to the Phase 2 lifecycle

Recovery is a **policy layer on top of** the lifecycle, never a bypass. Every
recovery mutation goes through the same idempotent `Try*` transitions; an
illegal or repeated recovery action is rejected by `CapBotTask.ApplyTransition`
and reported as "rejected" through the log bridge. The lifecycle's invariants
(single mutator, terminal states have no exits, once-only terminal stamps,
registry mirrors state) are all inherited untouched.

## Recovery state machine (per task)

```
                    Tick (≥1s recheck gate, per task)
                               |
     TaskRecoveryPolicy.Decide(task, record, probe, now)
     first matching rule wins (deterministic precedence):
                               |
   1. deadline elapsed  ──────────────► EXPIRE  (terminal)
   2. world invalidated premise ──────► CANCEL  (terminal abandonment)
   3. target invalid/lost ────────────► CANCEL  (terminal abandonment)
   4. owner unavailable + Running ────► FAIL    (-> retry policy, rule 8)
      owner unavailable + Paused ─────► CANCEL
   5. capability down + Running ──────► PAUSE   (paused-for-capability)
      capability down + Paused too long ► CANCEL (> 60 s in capability-pause)
   6. capability back + capability-paused ► RESUME
   7. Running, no progress > 15 s ────► FAIL    (stuck)
   8. Failed:
        RetryCount >= MaxRetries ─────► CANCEL  (retries exhausted)
        backoff not elapsed ──────────► (wait; no action)
        backoff elapsed ──────────────► RETRY   (Failed -> Queued, count++)
   otherwise ─────────────────────────► (no action this tick)
```

- **One action max per task per Tick.** `Decide` returns at most one decision;
  the executor applies it once.
- **At most one re-examination per task per 1 s** (`MinRecheckMs`), regardless
  of the caller's tick rate — per-frame callers cannot create per-frame policy.
- **Recovery actions are lifecycle actions:** `Retry` = `TryRetry`, `Pause` =
  `TryPause`, `Resume` = `TryResume`, `Fail` = `TryFail(reason)`, `Cancel` =
  `TryCancel(reason)`, `Expire` = `TryExpire`. All are idempotent; a rejected
  action changes nothing and is logged as `rejected`.

## Retry / backoff / budget behavior

- **Retry legality** is enforced by the lifecycle (`Failed`, `RetryCount <
  MaxRetries`). Recovery adds three bounds on top:
  1. **Backoff:** first retry waits `BackoffBaseMs` (2 s), then ×2 per prior
     retry (`2s → 4s → 8s → 16s → 30s`), capped at `MaxBackoffMs` (30 s). The
     delay is measured from the task's terminal stamp (`CompletedTimeMs`, which
     `TryFail` sets), so it is monotone and race-free.
  2. **Exhaustion:** `RetryCount >= MaxRetries` → abandon (`Cancelled`,
     reason `retries exhausted (n/m): <failureReason>`). No auto-retry beyond
     the cap, no infinite loops by construction.
  3. **Lifetime recovery budget:** at most `MaxRecoveryActions` (12)
     non-terminal recovery actions per task ever. When the budget is spent,
     the next decision degrades to a single terminal `Cancel` ("recovery
     budget exhausted"). This is the backstop against retry/pause ping-pong.
- **Exponential backoff is bounded**, never exponential growth without a cap.

## How invalid / stale world state is handled (research constraints)

Per PULSAR_GAMEAI_RESEARCH.md (host migration loses transient AI state; no
`OnMasterClientSwitched` AI rebuild exists) recovery **never trusts or stores
transient state**:

- The world is observed **only** through `ITaskWorldProbe`
  (`TargetValid` / `OwnerAvailable` / `CapabilityAvailable` /
  `WorldInvalidatesTask`), which must answer from **current authoritative
  state**. Recovery caches no world answers.
- Recovery holds **no paths, no Behave tree state, no sector-scoped objects,
  no Unity references**. Nothing in a recovery record can go stale across a
  host migration or sector change — records are timestamps and counters only.
- A task whose premise was removed by a world change is **abandoned, not
  re-targeted**. Re-deriving the task toward a *new* target is re-planning
  (later phases), not recovery — this phase never invents new intent.
- Phase 3 ships only `NullWorldProbe` (everything valid, nothing invalidates).
  With it (or no probe at all) the manager is correct-but-inert: Tick is safe,
  only the lifecycle's own paths (rule 8 retries on Failed tasks) can act.
- **QualityImprover note:** no recovery rule reads hostility state; the probe
  contract lets the Phase 6 implementation decide what "valid" means per mod
  environment. Recovery never assumes vanilla hostility semantics.

## Cadence / performance

- Intended driver cadence ~1 s, matching vanilla's 1–1.5 s decision gates
  (research §11). The manager itself also enforces `MinRecheckMs` (1 s) per
  task, so even a per-frame driver produces ~1 s policy.
- Bounded collections: one `RecoveryRecord` per registered task (≤ 64, the
  live-task cap), dropped on terminal state. No allocation on the idle path
  (the due-list allocates only when tasks are actually due).
- All timestamps come from `TaskClock` (wrap-safe ms). All interval math is
  unchecked 32-bit subtraction, correct ≪ 24.8 days.

## Logging

- Recovery outcomes are logged through the Phase 1 logger via
  `RecoveryLogBridge` (attached at boot next to `TaskLogBridge`):
  `[CapBot:TASK] [INFO] Recovery applied Retry on #7 TEST_TYPE owner=... state=Queued ... (retry 1/2 after backoff 2000ms; last failure: ...)`
- The manager contains zero logging calls; the pluggable
  `SetActionListener` hook keeps the domain pure and testable. The listener
  fires outside the manager's lock.
- All lines pass CapBotLog's spam/flood guards (8 s per key, 24/10 s flood).

## Explicitly not in this phase

- No scheduler/queue/executor (P4/P8) — recovery never creates, queues, or
  selects work; it only reacts to existing tasks.
- No real world probe (P6) — `NullWorldProbe` only.
- No gameplay actions, no nav writes, no Harmony changes, no RPCs.
- No Captain Brain 2.0, personalities, memory, directors, Ollama/Qwen,
  dynamic planning, persistence, UI, or refactors of existing code.
- No new PULSAR/PML API usage — the domain invented none and calls nothing
  game-facing (consistent with the alignment report: verified-API-only).