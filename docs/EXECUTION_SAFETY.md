# Execution Claim Safety — Phase 5 Documentation

Status: **protection layer only** — the claims system guards future execution
against duplicates; it executes nothing, holds no world state, and is inert by
construction (deny-by-default authority policy) until Phase 8 wires the gate
to master-client state and drives it. Future phases (capability registry P7,
executor P8, directors P9/P15–P17, Captain Brain 2.0 P18) must route every
gameplay action through these contracts and may not weaken them.

## Files

| File | Role |
|---|---|
| `Core/Tasks/ActionIdentity.cs` | `ActionOutcome`, deterministic `ActionIdentity.MakeActionId` (FNV-1a), bounded idempotency ledger `ActionLedger` |
| `Core/Tasks/ExecutionClaims.cs` | `ClaimResult`/`ResultStatus`/`ClaimInfo`, `ExecutionClaims` — single-owner claims, bounded leases, idempotent results, deny-by-default authority seam, tick hygiene |
| `Core/Tasks/ClaimLogBridge.cs` | Attaches Phase 1 `CapBotLog` (TASK) as the claims decision listener at mod boot |

The claims domain is **pure C#** (references only `System*`) — no UnityEngine,
no PULSAR/PML/Photon types. It compiles and can be tested without the game.

## The claim model

One `ClaimRecord` per live task (≤ `TaskRegistry.MaxLiveTasks` = 64):

```
TaskId -> { ActionId, ActionKind, AttemptEpoch, Owner, ExpiresAtMs }
```

- **Action identity** — `"<taskId>:<kind>:<epoch>:<hash8>"`, built by
  `ActionIdentity.MakeActionId(taskId, actionKind, attemptEpoch, targetKey)`:
  deterministic within a session; the 32-bit FNV-1a hash (never
  `string.GetHashCode`, which is not stable across sessions) folds in the
  opaque target reference. `actionKind` is validated to a static ASCII
  vocabulary (`[A-Za-z0-9_]`, ≤ 32 chars); untrusted text can never shape an
  identity. The id is **data only** — compared or logged, never parsed into
  code paths, never executed.
- **Attempt epoch** — deterministic default is the task's `RetryCount`
  (attempt 0 = first run, +1 per recovery retry), so a retried task is a
  *different logical action* while a duplicated request for the same attempt
  is the *same* one.
- **Owner** — a bounded string identity (`CAPTAIN`, `BOT:<id>`, future
  executor names). Only one owner may hold a task's claim at a time.
- **Lease** — `ClaimLeaseDurationMs` (5 s). Expired leases do NOT block:
  any authorized caller may take over (`GrantedTakeover`), and the stale
  owner's loss is logged (`LeaseExpired` + `OwnershipReleased reason=lease
  expired`). Stale owners never retain permanent ownership.

## Claim rules (deterministic, first match wins)

`TryClaim(taskId, actionKind, attemptEpoch, owner, targetKey, nowMs)`:

| Order | Condition | Result |
|---|---|---|
| 0 | malformed args / bad actionKind | `RejectedInvalid` |
| 1 | authority policy denies | `RejectedNotAuthoritative` |
| 2 | task not live | `RejectedTaskMissing` |
| 3 | task terminal | `RejectedTaskTerminal` |
| 4 | task `Failed`/`Paused` | `RejectedRecoveryOwned` |
| 5 | unexpired claim, different owner | `RejectedOwnedByOther` |
| 6 | unexpired claim, same owner, same action, already Succeeded | `DuplicateExecutionRejected` |
| 7 | unexpired claim, same owner, different action | `RejectedActiveClaim` |
| 8 | expired claim | `GrantedTakeover` (explicit, logged) |
| 9 | no claim, action already Succeeded | `DuplicateExecutionRejected` |
| 10 | otherwise | `Granted` |

Repeated rejected attempts against the same claim are log-throttled to one
line per second per claim (`RejectEmitGateMs`) — hot loops cannot spam.

## Idempotency (the two-sided duplicate guard)

The `ActionLedger` (bounded FIFO, 256 entries) remembers action outcomes:

- **Claim side:** a claim whose action identity is already `Succeeded` is
  refused (`DuplicateExecutionRejected`) — the same logical action can never
  be executed twice, even across executors or after lease churn.
- **Result side:** `RecordExecutionResult(taskId, actionId, outcome, nowMs)`
  records the first result for the matching claim and releases the claim.
  - same outcome repeated → `DuplicateIgnored` (duplicate callback, no change);
  - no matching claim → `StaleCallbackIgnored` (late/RPC-duplicate callback);
  - `Failed` then `Succeeded` → upgrade (a failed attempt that still resolves
    successfully within the same attempt is legitimate);
  - `Succeeded` is **sticky** — never downgraded, so the logical truth can
    never be erased by a later failure callback.
- **Explicit release:** `ReleaseClaim(taskId, owner, reason, nowMs)` lets the
  holder drop its claim without a result. Owner is verified; a mismatch is
  logged as `InvariantViolation` and refused — ownership changes are explicit
  and logged, never silent.

## Hygiene (Tick)

`ExecutionClaims.Tick(nowMs)` (deterministic, any cadence) drops claims that
are (a) lease-expired, (b) task-missing, or (c) task-terminal — the last two
cover "task cancelled/completed while claimed" (the lifecycle stays the sole
mutator; the claim layer only reacts). Every drop is logged with its reason.
Returns the number dropped.

## Authority model (multiplayer)

- **Deny-by-default seam:** `SetAuthorityPolicy(Func<bool>)` — with no policy
  set, `IsAuthoritative()` is false and **nothing can claim or record**. Phase
  8 wires it to `PhotonNetwork.isMasterClient` (the verified gate every
  vanilla AI path uses, research §9.1 — verified in-code at `Patch.cs` sites).
  A future executor that skips wiring therefore cannot bypass master-client
  authority; it simply cannot act.
- **Process-local bookkeeping:** claims, leases, results and the ledger live
  only in the authoritative host's memory. No RPCs are sent, nothing syncs,
  no `PhotonTargets.All` pattern is introduced (research §260/§431: host paths
  that also broadcast to All double-execute — vanilla's client→MasterClient
  request pattern remains the only networked route, P8 policy).
- **Host migration:** no claim record encodes world state; claims reference
  task ids + bounded strings only, so a migration leaves nothing stale to
  mis-own (research §266: transient-state survival UNVERIFIED → keep none).
  Expired/stale claims are recoverable deterministically by takeover.

## Scheduler interaction (Phase 4 contract)

Scheduler **grant** and execution **claim** are separate concepts with
separate lifetimes: a grant is the scheduler's bounded selection suggestion
(lease in `TaskScheduler`); a claim is execution safety (here). Claiming
neither requires nor consumes a grant, and grants don't create claims. The
scheduler never becomes an executor through this layer; repeated scheduler
passes cannot double-execute anything (each logical attempt still collides
in the ledger by action identity).

## Recovery interaction (Phase 3 contract)

- Tasks in `Failed`/`Paused` are recovery-owned: new claims are refused
  (`RejectedRecoveryOwned`) — a task undergoing recovery cannot be
  simultaneously claimed by another executor.
- A claim held while recovery capability-pauses the running task simply
  persists (same attempt, same owner); it expires naturally if abandoned.
- A recorded `Failed` result releases the claim immediately — recovery then
  owns the task; a retry re-claims a *fresh epoch* after backoff. This layer
  creates no retry loops (retry policy is exclusively Phase 3's); it only
  makes each attempt idempotent.

## Logging

All decisions flow through the pluggable listener (attached by
`ClaimLogBridge` at boot; zero logging calls in the domain; listener fires
outside the lock; CapBotLog spam/flood guards on top of the domain's own
1 s per-claim rejection throttle):

```
ClaimAccepted #12 12:EXECUTE:0:a1b2c3d4 owner=EXECUTOR leaseMs=5000
ClaimRejected #12 owned by EXECUTOR
DuplicateExecutionRejected #12 12:EXECUTE:0:a1b2c3d4 already succeeded
LeaseExpired #12 (stale owner EXECUTOR)
OwnershipReleased #12 owner=EXECUTOR reason=Succeeded
StaleCallbackIgnored #12 12:EXECUTE:0:a1b2c3d4 (no matching claim)
ClaimDropped #12 reason=task Cancelled
InvariantViolation #12 claim release owner mismatch (expected A, got B)
```

## Security posture

- No arbitrary code execution, no runtime compilation, no DLL loading, no
  shell execution, no interpreting LLM output as code. Action identities and
  `actionKind` tokens are validated bounded data compared byte-for-byte;
  target references are hashed, never embedded or parsed. The authority
  delegate answers from current state only; nothing here executes task
  metadata.

## Explicitly not in this phase

- No executor, no capability registry (P7/P8) — this layer ships with no
  caller; deny-by-default keeps it inert until P8 wires authority + drivers.
- No Phase 6 world state, directors, Captain Brain 2.0, Decision Validator,
  Ollama/Qwen, dynamic task generation, persistence, UI, updater or
  performance work.
- No Harmony/RPC changes, no changes to existing RPC patterns, MoreBots
  compatibility, PML save format, or vanilla AI behavior. No new PULSAR/PML
  API usage — the domain invented none and calls nothing game-facing.