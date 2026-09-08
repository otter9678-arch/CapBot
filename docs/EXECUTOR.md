# Phase 8 — Task Executor (contract)

The execution layer for already-approved tasks: takes one granted task plus
one registered safe capability and performs the allowed operation through
the appropriate safe pathway.

Architecture roles (each phase owns exactly one):

| Layer | Phase | Role |
|---|---|---|
| TaskScheduler | P4 | selects (grants are suggestions) |
| TaskRecoveryManager | P3 | owns failed/stuck/expired work + retry policy |
| CapabilityRegistry | P7 | defines what is legal |
| **TaskExecutor** | **P8** | **performs exactly one approved capability action** |

The executor invents no tasks, changes no priorities, creates no
capabilities, never interprets strings as commands, and never executes
arbitrary code. It consumes scheduler grants; it is NOT a second scheduler.
It hands failures to recovery; it has no retry loops. It validates world
state only through the P7 registry's seams; it never mutates or owns world
state.

## Files

- `Core/Executor/ExecutionResult.cs` — outcome vocabulary
  (`ExecutionOutcome`: Success / FailureRetryable / FailurePermanent /
  Rejected / Unavailable / Cancelled), the bounded immutable
  `ExecutionResult` (reason ≤ 200 chars, ≤ 8 metadata entries, key ≤ 32 /
  value ≤ 128, `WithMeta` derives copies), and the execution seam
  `ICapabilityDispatcher` (the ONLY pathway from an approved task to a
  gameplay action).
- `Core/Executor/TaskExecutor.cs` — the static engine. No per-frame work
  (Tick gate 250 ms, `MaxAttemptsPerTick = 4`), no scene scans, no LINQ,
  holds no collections of its own. Fail-closed: no dispatcher attached ⇒
  every attempt refused `Unavailable`; disabled ⇒ nothing runs.
- `Core/Executor/PulsarCapabilityDispatcher.cs` — game-facing
  implementation. Static, code-reviewed branches on CapabilityId; a
  registered capability WITHOUT a branch here is refused (`Rejected`) —
  registration never makes a capability executable.
- `Core/Executor/ExecutorLogBridge.cs` — attaches the decision listener to
  `CapBotLog` at boot (the domain itself stays pure C#).

## The execution flow (per attempt)

All gates run BEFORE the capability action. Order is deliberate (see
"Validate before claim" below).

```
1. resolve live task      TaskRegistry.Get — identity equality, non-terminal,
                          not Failed/Paused (recovery-owned)
2. consume scheduler grant  TaskScheduler.TryTakeLease — no grant ⇒ no
                          execution; lease consumed (exactly-one-executor-pass)
3. lifecycle start        task.TryStart() (Queued -> Running)
4. build request          CapabilityRequest from task fields + metadata
                          "CapabilityId"/"Argument" (untrusted data —
                          validated, never parsed as behavior)
5. registry validation    CapabilityRegistry.Validate — full P7 gate ladder
6. claim                  ExecutionClaims.TryClaim(taskId, capabilityId,
                          attemptEpoch=RetryCount, owner, targetKey, nowMs)
                          — duplicate-execution protection; last gate
7. dispatch               ICapabilityDispatcher.Dispatch — EXACTLY ONE
                          registered capability action; faults become
                          FailureRetryable, never thrown across the seam
8. record result          ExecutionClaims.RecordExecutionResult — idempotent;
                          sticky success; duplicate/stale callbacks ignored;
                          releases the claim
9. resolve lifecycle      Success -> TryComplete; failure/rejection ->
                          TryFail(reason) handing to Phase 3 recovery.
                          NO retry logic here — retry/backoff/abandon is
                          exclusively recovery's.
```

Every refusal still resolves the task through the lifecycle contract — the
executor never leaves a task wedged. Invariant violations (claim vanished
mid-execution, authority lost mid-attempt, recording refused) are logged
loudly (`ExecutorInvariant*`) and leave the task for recovery without
further mutation.

### Validate before claim (ordering note)

The user's flow diagram lists claim before capability validation. The
shipped order is validate → claim → execute → record because the P7
claim-conflict gate consults the P5 claim probe, which treats ANY unexpired
claim on the task as a conflict — an executor that claimed first would fail
its own validation gate. The same components in this order preserve every
guarantee: all capability gates still run before any gameplay action, the
claim-conflict gate catches PRIOR duplicates (ledger success / other paths'
claims), and the recorded result releases the claim afterwards. The claim
remains the LAST gate before the action.

## Multiplayer posture

- Claims + results are process-local bookkeeping on the authoritative host;
  no RPCs are sent by the claims/executor layer, nothing syncs.
- `ExecutionClaims.SetAuthorityPolicy` is wired at boot to
  `PhotonNetwork.isMasterClient` (fail-closed: any fault querying Photon
  denies authority). Clients never execute — vanilla's request→master
  pattern is untouched.
- Every dispatcher branch is master-side. No branch sends BOTH a local
  action and a `PhotonTargets.All` RPC for the same logical action.

## Capability → verified PULSAR API table

All signatures verified by direct reflection against Assembly-CSharp this
session (plus compile-proven shipped call sites in Patch.cs).

| Capability | Verified API | Call shape | Authority |
|---|---|---|---|
| `SET_CAPTAIN_ORDER` | `PLServer.CaptainSetOrderID(Int32)` | direct call (public instance [PunRPC]; Patch.cs:260/1950 shape). Order id validated against the static vocabulary {1,4,6,8,9,10,11,12,13} extracted from shipped `ComputeDesiredOrder` | MasterOnly |
| `ISSUE_MOVE_ORDER` | `PLPlayer.IssueMoveOrder(Vector3)` | `pawn.photonView.RPC("IssueMoveOrder", PhotonTargets.All, new object[]{ sector.Position })` — the method impl is PRIVATE in Assembly-CSharp (DLL-verified), so it is reachable only through its [PunRPC] route; PhotonTargets.All mirrors every shipped course-goal send so the handler runs once per peer, no separate local call. Vector3 derived ONLY from verified galaxy data (`PLSectorInfo.Position`, compile-proven reads) — never parsed from untrusted text. [INFERRED mask on a VERIFIED RPC] | MasterOnly |
| `SET_CAPTAIN_TARGET` | `PLShipInfoBase.Captain_SetTargetShip(Int32)` | direct call on the player ship (DLL-verified public virtual). The target id syncs via the ship's stream (research §131), so no PhotonTargets.All send — no local+All duplicate pattern | MasterOnly |
| `ADD_COURSE_GOAL` | `PLServer.AddCourseGoal(Int32)` | `PLServer.Instance.photonView.RPC("AddCourseGoal", PhotonTargets.All, args)` — exact shipped shape (Patch.cs:2604/2621/2629); sector existence verified against the galaxy table first | MasterOnly |
| `REMOVE_COURSE_GOAL` | `PLServer.RemoveCourseGoal(Int32)` | `photonView.RPC("RemoveCourseGoal", PhotonTargets.All, args)` (Patch.cs:423) | MasterOnly |
| `CLEAR_COURSE_GOALS` | `PLServer.ClearCourseGoals()` | `photonView.RPC("ClearCourseGoals", PhotonTargets.All, new object[0])` (Patch.cs:418/1790/1827/1864) | MasterOnly |
| `READ_WORLD_SNAPSHOT` | `WorldStateService.Latest` | pure P6 read; no gameplay, no authority, no cooldown | ReadOnly |

No other capability has a dispatch branch. Nothing dispatches on
`request.Argument` or arbitrary task metadata.

## The tick driver

The existing Phase 6 `WorldTick` postfix (`PLController.Update`, the 11th
patch — extended in place, no new Harmony patch) now drives, after the
world refresh, behind `PhotonNetwork.isMasterClient` (fail-closed try/catch,
the shipped authority gate Patch.cs:102/Autonomy.cs:26):

```
TaskScheduler.Tick(nowMs)          1 s internal gate, grants ≤ 8/pass
TaskRecoveryManager.Tick(nowMs)    1 s internal gate, budget 12 actions/task
TaskExecutor.Tick(nowMs)           250 ms internal gate, ≤ 4 attempts/pass
```

Each subsystem call is individually exception-guarded so one subsystem can
never take down the others or vanilla Update. The whole driver is INERT
until tasks exist — nothing creates tasks until the P9+ directors, so Phase
8 ships zero gameplay behavior change.

## Logging (CapBotLog, TASK subsystem via ExecutorLogBridge)

`ExecutorRefused` (invalid task/owner/target/authority/precondition/no
grant/terminal/recovery-owned), `ExecutorRejected` (post-start refusals),
`ExecutorResult` (success/failure with task resolution), `ExecutorInvariant`
/ `ExecutorInvariantViolation` (claim vanished, authority flip, recording
refused), `ExecutorStaleCallback`, `ExecutorDuplicateExecution` (already-
succeeded identity → task completed, never dispatched twice). Claims layer
emits its own accepted/rejected/lease/duplicate lines (ClaimLogBridge);
the registry emits approved/rejected per validation (CapabilityLogBridge).

## Tests

`tests/ExecutionTests.cs` (Roslyn harness, virtual clock) — 98 assertions
covering the 20 mandated scenarios (successful execution, unknown
capability, disabled capability, invalid task, invalid owner, invalid
target, failed precondition, wrong authority, missing claim, duplicate
claim, duplicate execution request, stale callback, cancelled task,
completed task, recovery-owned task, retryable failure, permanent failure,
scheduler grant requirement, deterministic execution identity, multiplayer
authority gating) plus the tick driver (idle pass, end-to-end grant →
dispatch → complete, gate, bound, owner fan-out). Suite order in
`TestMain` is f7 after f6; totals gate at `TOTAL failed=0`
(665/665 this phase).