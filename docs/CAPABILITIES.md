# Safe Task Capabilities — Phase 7 Documentation

Status: **contract layer only** — the capability registry defines the bounded
vocabulary of gameplay operations future tasks/executors may request. It
executes nothing, performs no gameplay, calls no PULSAR API, and holds no
world or claim state. There is no executor in this phase: the registry can
approve a request, but nothing consumes that approval. Future phases must
not weaken the boundary below.

## Files

| File | Role |
|---|---|
| `Core/Capabilities/CapabilityDescriptor.cs` | `CapabilityAuthority`/`CapabilityDanger`/`CapabilityReversibility`/`CapabilityValidation`/`TargetRequirement` enums, untrusted `CapabilityRequest`, immutable `CapabilityDescriptor` contract |
| `Core/Capabilities/CapabilityRegistry.cs` | `CapabilityRegistry` — bounded static allowlist (≤ 32), deterministic validation gate ladder, cooldowns, enable/disable, pluggable seams |
| `Core/Capabilities/RegisteredCapabilities.cs` | The Phase 7 built-in catalog (7 capabilities, all documented from PunRPC-verified APIs) + production seam wiring |
| `Core/Capabilities/CapabilityLogBridge.cs` | Attaches Phase 1 `CapBotLog` (CAPABILITY) as the registry decision listener at mod boot |

The capabilities domain is **pure C#** (references only `System*` plus the
P2–P6 domain namespaces) — no UnityEngine, no PULSAR/PML/Photon types. It
compiles and can be tested without the game.

## Security boundary (the core rule)

A capability is a **data contract, never an executable instruction**:

- No arbitrary C# generation, no runtime compilation, no DLL loading, no
  shell/process execution, no reflection-driven method invocation.
- LLM output, game text, chat, mission/NPC text, and task metadata are all
  **untrusted data**. The registry validates them as bounded tokens or
  integers, accepts/rejects/hashes them — it never interprets them as
  commands.
- Dispatch on a `CapabilityId` is the future executor's job, via static,
  code-reviewed branches. The registry never dispatches, and no string in
  a request can select behavior.

## The descriptor contract

Every capability is an immutable `CapabilityDescriptor`:

| Field | Contract |
|---|---|
| `CapabilityId` | Stable identity: bounded token `[A-Za-z0-9_]`, ≤ 32 chars — the same vocabulary as Phase 5's `actionKind`, so a CapabilityId *is* a valid actionKind by construction |
| `Name` / `Description` | Bounded static text (≤ 64 / ≤ 256 chars) |
| `AllowedOwners` | Owner allowlist (≤ 8); empty = any valid task owner. Owner strings are the Phase 2 vocabulary (`CAPTAIN`, `BOT:<id>`, …) |
| `Authority` | `MasterOnly` (default for gameplay), `ClientRequest`, `ClientOnly`, `ReadOnly` |
| `Danger` / `Reversibility` | `Benign`/`Reversible`/`Irreversible` + `NotApplicable`/`Reversible`/`Irreversible` — review-visible classification |
| `TaskTypes` | Task-type allowlist (≤ 8); empty = any task type |
| `TargetReq` + `AllowedTargetKinds` | `None`, `SectorId`, `ShipId`, `MissionId` (TargetId must parse as int ≥ 0), `BoundedToken` (`[A-Za-z0-9_]` ≤ 32); kind strings exact-match |
| `CooldownMs` | Minimum ms between APPROVED requests (0 = none); stamped only on approval |
| `RequiresFreshWorldState` | When true, validation consults the P6 snapshot (see gate 11) |
| `Precondition` / `TargetValidator` | Pure `Predicate<CapabilityRequest>` delegates — no side effects, no clock reads, no game access; a throwing validator = reject (fail-closed) |
| `VerifiedApi` | Static text documenting the exact verified PULSAR API/RPC the future executor will call |
| `LoggingCategory` | CapBotLog subsystem tag (all built-ins use `CAPABILITY`) |

## Validation gate ladder (deterministic, first failure wins)

`CapabilityRegistry.Validate(capabilityId, request, task)` walks one ladder;
the same inputs (+ same seam state) always yield the same outcome:

| Order | Gate | Outcome |
|---|---|---|
| 1 | malformed request/task/owner (null, TaskId ≤ 0, bad owner) | `RejectedInvalidRequest` |
| 2 | unknown capability id | `RejectedUnknownCapability` |
| 3 | capability disabled | `RejectedDisabled` |
| 4 | task owner not in `AllowedOwners` | `RejectedActorNotAllowed` |
| 5 | authority not satisfied by this process | `RejectedAuthority` |
| 6 | target kind/id shape or `TargetValidator` fails | `RejectedTargetInvalid` |
| 7 | declared `Precondition` false | `RejectedPrecondition` |
| 8 | task type not in `TaskTypes` | `RejectedTaskMismatch` |
| 9 | task not live in `TaskRegistry` (identity) or request owner ≠ live task owner | `RejectedOwnershipMismatch` |
| 10 | cooldown active (`CooldownMs > 0` and clock available) | `RejectedCooldown` |
| 11 | P5 claim conflict: unexpired claim on task OR action id already `Succeeded` in ledger | `RejectedClaimConflict` |
| 12 | `RequiresFreshWorldState` and P6 snapshot missing | `RejectedWorldStateMissing` |
| 13 | `RequiresFreshWorldState` and snapshot older than `MaxSnapshotAgeMs` | `RejectedWorldStateStale` |
| — | all gates pass | `Approved` (cooldown stamped) |

Details:

- **Gate order is fixed** (malformed → disabled → actor → authority →
  target → precondition → task-mismatch → ownership → cooldown → claim →
  world) so exactly one outcome is emitted per request.
- **Ownership** compares the *live registered task*: `TaskRegistry.Get`
  must return a task equal to the presented one (identity by TaskId), and
  the request's claimed owner must match the live task's owner. A task
  that was cancelled/replaced cannot authorize a capability call.
- **Cooldown** is per-capability (`m_LastApprovedMs`), uses the caller's
  clock via the seam, and is stamped **only on approval** — rejections
  never extend a cooldown. A faulting clock disables cooldown checks for
  that call (no spurious rejections).
- **Claim conflict** builds the real P5 action identity:
  `ActionIdentity.MakeActionId(task.TaskId, capabilityId, task.RetryCount,
  request.TargetId)`. Conflict = an unexpired `ExecutionClaims` lease on
  the task, or the identical action already `Succeeded` in the
  `ActionLedger` (duplicate execution). A seam fault counts as a conflict
  (fail-closed). Reads are policy-free — the claims layer's own
  deny-by-default gate only guards actual claiming.
- **World freshness** (only when `RequiresFreshWorldState`): no snapshot /
  never-captured → `RejectedWorldStateMissing`; snapshot older than
  `WorldStateService.MaxSnapshotAgeMs` (10 s) → `RejectedWorldStateStale`.
  No built-in capability sets this flag yet — live-validated commands that
  check world data are the executor phase's responsibility (the registry
  only supplies the gate).

## Authority model (multiplayer)

| Authority | Meaning | Who passes validation |
|---|---|---|
| `MasterOnly` | Authoritative gameplay; default for all gameplay-affecting capabilities | Only a process whose authority probe returns true |
| `ClientRequest` | Clients may request, the master decides/executes | Only the authoritative process (routing arrives with P8) |
| `ClientOnly` | Local-only effect on this peer | Anyone (nothing authoritative) |
| `ReadOnly` | Observation, no side effects | Anyone |

- **Fail-closed:** with no authority probe wired, `MasterOnly` and
  `ClientRequest` can never be satisfied — an unwired registry cannot
  authorize gameplay. Production wiring is
  `ExecutionClaims.IsAuthoritative()` (itself deny-by-default until P8
  wires `PhotonNetwork.isMasterClient`), so MasterOnly capabilities are
  doubly fail-closed today.
- Clients can never pass `MasterOnly` validation, so client code can never
  authorize an authoritative operation through this layer.

## Seams (pluggable, all optional, all fault → fail-closed)

| Seam | Production wiring (boot) | Unwired behavior |
|---|---|---|
| `SetAuthorityProbe(Func<bool>)` | `ExecutionClaims.IsAuthoritative()` | `MasterOnly`/`ClientRequest` never satisfied |
| `SetNowMsProvider(Func<int>)` | `TaskClock.NowMs` | cooldowns disabled |
| `SetWorldProvider(Func<WorldSnapshot>)` | `WorldStateService.Latest` | `RequiresFreshWorldState` → `RejectedWorldStateMissing` |
| `SetClaimProbe(Func<long,string,bool>)` | `GetClaim(taskId).Active || Ledger.Observe(actionId)==Succeeded` | no claim conflicts detected |

The registry holds **no world state and no claim state** — it consults the
P5/P6 layers through these seams (world ownership stays outside the
registry).

## The Phase 7 catalog (7 built-ins)

All registered at boot by `RegisteredCapabilities.RegisterBuiltIns()`
(duplicate-safe; a second call registers nothing). Every command channel is
an owner-restricted (`CAPTAIN`), `MasterOnly`, reversible capability with a
cooldown guarding vanilla cadence (research §11: AI decisions 1–1.5 s):

| CapabilityId | Authority | Target | Cooldown | VerifiedApi (executor will call) |
|---|---|---|---|---|
| `SET_CAPTAIN_ORDER` | MasterOnly | `BoundedToken` / kind `ORDER` | 2000 ms | `PLServer.CaptainSetOrderID(Int32)` [PunRPC, VERIFIED] — sets `PLServer.CaptainsOrdersID` (ObscuredInt, synced) |
| `ISSUE_MOVE_ORDER` | MasterOnly | None | 1000 ms | `PLPlayer.IssueMoveOrder(Vector3)` [PunRPC, VERIFIED] — location derived by executor from verified data, never parsed from untrusted text |
| `SET_CAPTAIN_TARGET` | MasterOnly | `ShipId` / kind `SHIP` | 1000 ms | `PLShipInfoBase.Captain_SetTargetShip(Int32)` [PunRPC, VERIFIED] — sets `CaptainTargetedSpaceTargetID` |
| `ADD_COURSE_GOAL` | MasterOnly | `SectorId` / kind `SECTOR` | 1000 ms | `PLServer.AddCourseGoal(Int32)` [PunRPC, VERIFIED] — appends to `m_ShipCourseGoals` |
| `REMOVE_COURSE_GOAL` | MasterOnly | `SectorId` / kind `SECTOR` | 1000 ms | `PLServer.RemoveCourseGoal(Int32)` [PunRPC, VERIFIED] — removes from `m_ShipCourseGoals` |
| `CLEAR_COURSE_GOALS` | MasterOnly | None | 5000 ms | `PLServer.ClearCourseGoals()` [PunRPC, VERIFIED] — wipes the whole course (higher cooldown) |
| `READ_WORLD_SNAPSHOT` | ReadOnly | None | 0 | `CapBot.Core.World.WorldStateService.Latest` (Phase 6, VERIFIED) — no owners restriction, no side effects |

Deliberately excluded in this phase:

- **No speculative combat/mission/economy/build capabilities** — their APIs
  and authority were not verified for this phase.
- **Sighted but unregistered APIs** (checked against Assembly-CSharp during
  verification): `PLServer.Captain_SetAutoMode(Bool)` (empty body — no
  observable effect to contract), `PLServer.Captain_NameShip(String)`,
  `PLServer.SkipWarp/SkipWarpAt`. None were requested for Phase 7; none are
  registered.
- **No live gameplay preconditions invented** (ship exists, sector in warp
  range, …) — those require runtime checks against verified APIs and belong
  to the executor phase.

## Task-system integration (P2 lifecycle / P3 recovery / P4 scheduler)

- The registry **reads** the live task from `TaskRegistry` (ownership gate)
  and reads `task.RetryCount` as the attempt epoch for action identity. It
  never mutates tasks.
- The gate ladder is what the P8 executor will call before claiming via P5
  (`TryClaim`) — the registry decides *legality*, P5 decides *safety of
  this attempt*, the scheduler decides *which task runs next*, and the
  executor *performs*. None of those callers exist in this phase; the
  scheduler remains inert (grants are suggestions; nothing consumes them)
  and recovery still has no tick driver.
- Registry state is process-local: nothing here sends RPCs or syncs
  (research §260/§431 — no `PhotonTargets.All` pattern is introduced).

## Compatibility (research §12)

- **Better AI / MoreBots / Quality Improver:** the registry introduces no
  Harmony patches, no AI-class replacement, and no vanilla API calls. When
  the executor arrives (P8), hostile checks must route through the game's
  current `ShouldBeHostileToShip` (Quality Improver may replace it) — the
  `SET_CAPTAIN_TARGET` contract deliberately encodes no hostility
  assumptions.
- **Vanilla hostility:** no assumption is baked into any descriptor.

## Performance

- Lookup is a bounded `Dictionary` (`StringComparer.Ordinal`) — exact-match,
  no scanning, no LINQ, no per-frame work; the registry has no tick.
- The gate ladder allocates nothing on the hot path except the bounded
  `RegisteredIds()`/`StatusLines()` diagnostic copies and the action-id
  string during claim-conflict checks.
- Listener fires outside the lock; validator/precondition faults are caught
  (fail-closed, never throw into the caller).

## Logging

All decisions flow through the pluggable listener (attached by
`CapabilityLogBridge` at boot; zero logging calls in the domain; CapBotLog
spam/flood guards bound output):

```
CapabilityRegistered SET_CAPTAIN_ORDER
CapabilityApproved SET_CAPTAIN_ORDER #12 owner=CAPTAIN
CapabilityRejected RejectedCooldown cap=SET_CAPTAIN_ORDER 812ms < 2000ms task=#12 owner=CAPTAIN
CapabilityRejected RejectedOwnershipMismatch cap=ADD_COURSE_GOAL owner=BOT:3 vs task owner=CAPTAIN task=#7
CapabilityRejected RejectedClaimConflict cap=SET_CAPTAIN_TARGET 12:SET_CAPTAIN_TARGET:0:1a2b3c4d task=#12 owner=CAPTAIN
```

## Security posture

Task metadata, target ids, owner strings and arguments are untrusted data:
validated as bounded tokens or parsed integers, compared byte-for-byte,
logged inline, or hashed into action identities — never parsed into code
paths, never executed. There is no path from an LLM/game/chat string to a
behavior: only the 7 registered CapabilityIds can even be validated, and
nothing validates → nothing executes in this phase.

## Explicitly not in this phase

- **No executor** — no component consumes an `Approved` outcome; no PULSAR
  API is called anywhere in this layer (the `VerifiedApi` fields are static
  documentation text).
- No P8 executor routing, P9 directors, Captain Brain 2.0, Decision
  Validator, Ollama/Qwen, dynamic task generation, persistence, UI, updater
  or performance work.
- No Harmony patches, no new RPCs, no changes to vanilla AI behavior, PML
  save format, or mod compatibility surfaces.
- No capability auto-granting: registration adds vocabulary only —
  validation still requires a live, owned, correctly-typed task with
  satisfied authority and a clean claim state.