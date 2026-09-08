# Crew Agents (Phase 10)

Status: **IMPLEMENTED** (Phase 10). The Crew Agent layer is the AGENT MODEL
only — a persistent, identity-stable, bounded data representation of each
crew member. It is not personality, not memory, not learning, not planning,
not decision making, not an executor, and not a persistence redesign. Those
arrive in later phases and build on the seams this document defines.

Files: `CapBot/Core/Crew/CrewAgent.cs` (model + role vocabulary),
`CapBot/Core/Crew/CrewAgentRegistry.cs` (registry + sync),
`CapBot/Core/Crew/CrewAgentLogBridge.cs` (logging bridge).
Driver: the existing Phase 6 `WorldTick` postfix on `PLController.Update`
(extended in place — still 11 Harmony patch classes; no new patch).

## 1. What an agent IS (and is not)

A `CrewAgent` is a bounded data record for ONE crew member (bot or human):

| Group | Fields |
|---|---|
| Identity | `AgentId` (`AGT:<hash8>`), `PlayerId`, `IsBot`, `TeamId` |
| Role | `ClassId` (raw, −1 = unknown), `Role` (bounded vocabulary), `RoleName` (data only), `Name` (≤32), `IsCaptain` |
| Lifecycle | `Lifecycle` (Active/Inactive/Removed), `CreatedTimeMs`, `LastSyncTimeMs`, `AbsentSinceMs` (−1 = present) |
| World association | `LastKnownTLIName` (≤32, cached observation) |
| Task relationship | `CurrentTaskId` (0 = none), `CurrentTaskType`, `CurrentTaskCapabilityId`, `CurrentTaskAssignedMs` (−1), `LastTaskOutcome`, `LastTaskResultMs` (−1) |
| Capabilities ref | `CapabilityReferences` (≤8 static-vocabulary ids; data only, never dispatched on) |
| Diagnostics | `UpdateCount`, `LastChangeReason` |

Hard boundaries:

- The agent **never stores a game-object reference** (no `PLPlayer`, `PLBot`,
  pawn, ship). Sector transitions invalidate those; agents keep bounded data
  copied from the Phase 6 snapshot. Nothing here duplicates the PLPlayer
  object — the game's own objects remain the only gameplay authority.
- The agent **never creates, queues, claims, or executes tasks** and never
  modifies PULSAR world state. Task fields are assignment metadata and
  read-only observation results.
- All timestamps are explicit nowMs values (TaskClock semantics); no
  wall-clock reads, no `DateTime.Now`.

## 2. Identity

`AgentId = "AGT:" + FNV-1a-32(seed)` where seed = `"B|<playerId>"` (bot) or
`"H|<playerId>"` (human), hashed via
`ActionIdentity.ComputeStableHash` (the same deterministic primitive
`EmergencyIdentity` uses). Properties:

- Deterministic within and across sessions — a bot that leaves and rejoins
  gets the SAME id; a fresh record is created under that id after removal.
- Immune to name/class/role changes (seed is identity-only data).
- Never parsed, never dispatched on — compared byte-for-byte or logged.

`PlayerId` is the PULSAR identity reference (`PLPlayer.GetPlayerID()`,
VERIFIED public Int32). The PULSAR objects themselves are resolved only by
the world capture seam (P6); agents reference them through ids only.

## 3. Role model

PULSAR defines **no class enum** — class ids are plain Int32 named through
the verified public static channel `PLPlayer.GetClassNameFromID(Int32)`
(plus `GetClassColorFromID(Int32)`). Verified class ids (research §6):
`0` Captain, `1` Pilot, `2` Scientist, `3` Weapons, `4` Engineer.

The domain stores the raw `ClassId` and maps it onto the bounded vocabulary
`CrewRole { Unknown, Captain, Pilot, Scientist, Weapons, Engineer, Other }`
(`CrewRoles.FromClassId`). No additional PULSAR classes are invented: a
valid-but-unmapped id maps to `Other`, a negative/absent id maps to
`Unknown`. The game's own class NAME is resolved through the verified
naming channel only (`SetRoleNameResolver` seam, wired in `Mod.cs` to
`PLPlayer.GetClassNameFromID`) and carried as DATA ONLY (`RoleName`) — it
is never parsed or dispatched on.

## 4. Lifecycle

Record lifecycle (not the game player object):

```
            present in latest crew snapshot
   (new) ────────────────────────────────▶ Active
     ▲                                       │ absent from snapshot
     │ present again (any time)              ▼
   Active ◀───────────────────────────── Inactive
     │                                      │ absent ≥ RemovalGraceMs (15 s)
     │ fresh record on later reappearance   ▼
     └────────────────────────────────── Removed (history ring, ≤16)
```

- **Absent → Inactive**: the diff pass deactivates any Active agent whose
  (PlayerId, IsBot) pair is missing from the current crew section (bot
  removal, disconnect, stale player object, sector-transition gaps covered
  by the snapshot cadence).
- **Inactive → Active**: presence again reactivates the same record
  (transient snapshot gaps never lose agent state).
- **Inactive → Removed**: after `RemovalGraceMs` (15 s) of continuous
  absence, the record leaves the live map and enters the bounded history
  ring. A later rejoin creates a fresh record under the same stable AgentId.
- Captain change is a data update (`IsCaptain` flag flips, logged
  `AgentCaptainFlag`); role/class changes remap `Role`/`RoleName`
  (`AgentRoleChanged`). Null crew entries are skipped; nothing propagates
  null references.

**P40 personality reconciliation hooks (data plumbing only; the personality
registry is Phase 11's separate bounded store):** on agent create, the
sync calls `CrewPersonalityRegistry.EnsureFor` (idempotent; outside the
registry lock); every sync's tail runs a reconcile whose **removal pass
runs first** (a Removed agent's *derived* personality record is removed
with it — explicit/neutral records survive) and then re-ensures every
live non-Removed agent (≤32 ensures per 1s cadence, fail-safe). Role
changes log a `PersonalityReconciled` line but never re-derive — the
record is keyed by the stable AgentId and role affinity reads per-lookup.
During `CrewPersistence.Restore()` the reconcile is suppressed
(`CrewPersistence.IsRestoring`) so restored matured rows are never
re-derived over. Related events: `PersonalityCreated`, `PersonalityAssigned`,
`PersonalityReconciled`, `PersonalityRemoved`, `PersonalityRestored`
(see `docs/CREW_PERSONALITIES.md` §Phase 40).

## 5. World-state relationship

Phase 6 world state is the ONLY observation source. The registry pulls
`WorldStateService.Latest` through its `SetWorldProvider` seam each sync —
agents never capture snapshots themselves, never read PULSAR objects, and
never own world-state data. The crew section of the snapshot (captain-first
when a class-0 bot exists; ≤16 entries) is the sole input to the diff.

Note (snapshot contract): `Crew[0]` is the captain only when a class-0
team-0 bot exists; consumers must test the `IsCaptain` flag, not the index.
`LastKnownTLIName` is a cached last-seen location for future phases; the
authoritative location is always the current snapshot.

## 6. Task relationship

The agent's task surface is **assignment metadata + observation results**:

- `AssignTask(agentId, taskId, taskType, capabilityId, nowMs)` accepts a
  scheduler-produced task reference as data (the scheduler/claims/
  capabilities/executor are untouched; P4 owns grants, P5 claims, P7
  validation, P8 execution).
- `ClearTask(agentId, outcome, nowMs)` records a terminal outcome
  (COMPLETED/CANCELLED/EXPIRED/FAILED/VANISHED — static vocabulary).
- During Sync, the registry OBSERVES each assigned task read-only via
  `TaskRegistry.Get`: terminal → record outcome + clear; missing ≥
  `TaskVanishedGraceMs` (10 s) → VANISHED. A Failed (retryable) task stays
  assigned — recovery owns the retry decision.
- The agent MUST NOT: create arbitrary tasks, bypass scheduler/recovery/
  claims/capabilities, or execute gameplay code. Owner vocabulary matches
  the P2 convention (`BOT:<playerId>` composes naturally from `PlayerId`;
  the probe's `BOT:` parse contract is the sanctioned resolution path).
- One-grant-per-owner (P4 owner-busy gate) is the per-bot concurrency
  unit; agents add no queueing of their own.

## 7. Authority + multiplayer

Deny-by-default authority seam (`SetAuthorityProbe`, production wiring:
`ExecutionClaims.IsAuthoritative` → `PhotonNetwork.isMasterClient`):

- No probe / faulting probe → Sync is a no-op (clients never build
  authoritative agent state).
- Authority lost mid-flight → the live agent map is CLEARED
  (`AgentsClearedAuthorityLost`), so clients never keep stale
  authoritative state; agents are rebuilt by the next authoritative sync
  (identity is deterministic, nothing user-visible is lost).

Gameplay stays master-side; agents are authoritative representations with
no RPCs, no networked state, and no `PhotonTargets.All` sends. The vanilla
request→master pattern and all existing RPC behavior are untouched. Agent
records are process-local like every other CapBot domain structure.

## 8. Performance constraints

- Sync is gated to ≥ `MinRecheckMs` (1000 ms) — never per frame; runs
  host-only in the shared `WorldTick` postfix, individually exception-
  guarded (`Crew agent sync failed`).
- No scene scans, no `FindObjectsOfType`, no LINQ, no per-frame allocation
  beyond the bounded diff lists; every collection is bounded:
  agents ≤ `MaxAgents` (32), history ≤ 16, capability refs ≤ 8.
- Registry keyed by AgentId — O(1) lookups; per-sync work is O(crew + agents)
  over ≤16 + ≤32 entries. Listeners fire outside the lock.
- Removal pass runs BEFORE the creation pass so grace-expired slots free in
  the same pass (a full registry refuses creation only when every slot is
  live crew).

## 9. Verified PULSAR APIs used

| API | Status | Use |
|---|---|---|
| `PLPlayer.GetPlayerID()` → Int32 | VERIFIED (public instance, DLL inspection) | identity reference (via P6 snapshot population) |
| `PLPlayer.GetClassID()` → Int32 | VERIFIED | ClassId source (via P6 snapshot) |
| `PLPlayer.GetClassNameFromID(Int32)` → String, static, public | VERIFIED | RoleName resolution (data only) |
| `PLPlayer.GetClassName()` / `GetClassColorFromID(Int32)` | VERIFIED (available; unused — color/name are data-only) | naming channel evidence |
| `PLPlayer.IsBot` / `TeamID` | VERIFIED | IsBot/TeamId (via P6 snapshot) |
| `PLShipInfo.CaptainsChairPlayerID` → Int32 | VERIFIED | authoritative captain id (game-side; the P6 source derives `IsCaptain` from the class-0 bot seat) |
| `PLServer.GetPlayerFromPlayerID(Int32)` / `AllPlayers` | VERIFIED | identity lookup evidence (indirect; the registry never calls them directly) |

No API outside the audit whitelist / verified set is used. The registry
performs zero direct PULSAR calls in its own code path — it consumes
`WorldSnapshot` data only.

## 10. Performance/behavioral constraints honored

- PLPlayer priority management (`UpdateAIPriorities`), PLBot behavior trees,
  `PLBotController.HandleMovement`, and `PLFlightAI` are untouched — the
  crew layer adds no competing movement AI and no per-frame AI-target
  manipulation; the `WorldTick` postfix is extended in place (11 patches).
- Per-bot state lives in the keyed registry, never in shared
  statics/floats/flags (the legacy `capisbot`/`crewisbot` statics remain
  untouched and are subsumable by a later phase).
- Registry bounded (32/16/8), sync gated (1 s), deterministic ordering,
  fail-safe on stale/missing/invalid snapshots (20 s staleness window,
  matching the P9 director).

## 11. Security

Agent metadata is DATA ONLY. No generated executable code, no runtime
compilation, no DLL loading, no shell/process execution, no arbitrary
reflection execution, no LLM commands. The registry never parses
`RoleName`/`Name`/`LastChangeReason` into code paths; ids are compared, not
interpreted.

## 12. Future integration points (NOT implemented in Phase 10)

Personality, experience, memory, adaptive learning, role preferences,
planning, decision making, Mission/Economy/Combat directors, Captain Brain
2.0, Ollama/Qwen — all later phases. The hooks reserved for them:

- `CrewAgent.CapabilityReferences` (bounded static vocabulary, data only).
- `AssignTask`/`ClearTask` as the assignment API a future planner calls.
- `Role`/`RoleName` as the role-preference anchor.
- `LastTaskOutcome`/`LastTaskResultMs` as the experience anchor.
- `LastKnownTLIName` as the memory anchor (authoritative world state
  remains the P6 snapshot).
- The registry's `ResetForTests` + seam pattern matches all prior
  subsystems, so future directors integrate with the same discipline.

## 13. Unsupported / not done

No personalities, no memory, no learning, no dynamic planning, no dynamic
task generation, no self-modifying behavior, no persistence redesign, no
UI/dashboard, no LLM. Persistence is unchanged (`PMLSaveData` per-class XP
matrix untouched).

## 14. Failure modes

| Failure | Behavior |
|---|---|
| No/never-captured/stale/future snapshot | Sync is a no-op, uncertainty recorded, counters untouched |
| Game not started | No agents created; existing agents stay (frozen) |
| Authority probe null/faulting/non-master | Sync no-op; authority loss clears the live map |
| Bot removed / player disconnect | Agent → Inactive → (15 s) Removed → history |
| Stale player reference (crew section shrinks) | Same as removal; no null propagation |
| Registry full (32) with all-live crew | Creation refused deterministically, logged |
| Task vanished from registry | Assignment cleared as VANISHED after 10 s grace |
| Role resolver fault | RoleName null (role vocabulary still mapped) |
| Any sync exception | Caught by the WorldTick guard; logged; never kills vanilla Update |

## 15. Tests

`tests/CrewAgentTests.cs` (108 assertions, suite f9 in
`tests/TaskRecoveryTests.cs` TestMain; total suite now 914):

S01 stable creation/deterministic identity · S02 duplicate prevention ·
S03 bot removal · S04 stale reference + fresh rejoin · S05 captain
identification · S06 role/class mapping (incl. unknown→Other) · S07 five
simultaneous agents isolated · S08 task ownership assign/clear · S09
scheduler owner-busy gate unaffected · S10 recovery rule-4 fail→cancel
(observed as CANCELLED; Failed stays assigned) · S11 claims
deny-by-default preserved · S12 emergency-task assignment as data · S13
deterministic lookup + bounded cadence · S14 bounded registry (16-crew +
truncation) · S15 join/leave lifecycle cycles · S16 captain change ·
S17 client/master authority + authority-loss clear · S18 no cross-agent
contamination · S19 no unauthorized execution · S20 fail-safe gates
(empty/stale/not-started/null-crew).