# Changelog

All notable changes to CapBot are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Phase 10 — Crew agents] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Crew/CrewAgent.cs` — the agent model: bounded data record for one
  crew member (bot or human) with stable identity (`AgentId` = "AGT:<hash8>"
  via the shared FNV-1a `ActionIdentity.ComputeStableHash` over seed
  "B|<playerId>"/"H|<playerId>" — deterministic across rejoins, immune to
  name/class changes), PULSAR identity reference (`PlayerId`),
  role model (`ClassId` raw + bounded `CrewRole` vocabulary
  Captain/Pilot/Scientist/Weapons/Engineer mapped from the verified class
  ids 0–4, unknown→Other, absent→Unknown; `RoleName` resolved data-only
  through the verified static channel `PLPlayer.GetClassNameFromID`),
  captain flag, lifecycle (Active/Inactive/Removed + Created/LastSync/
  AbsentSince stamps), world association (LastKnownTLIName cached
  observation), task relationship (CurrentTaskId/Type/CapabilityId/
  AssignedMs + LastTaskOutcome/ResultMs — assignment metadata and read-only
  observation only), bounded capability-reference list (≤8, data only,
  future-phase integration point) and bounded diagnostics. No
  game-object references, no PLPlayer duplication, no wall-clock reads.
- `Core/Crew/CrewAgentRegistry.cs` — the per-bot state registry keyed by
  stable AgentId (never shared statics): bounded-cadence sync (1 s gate,
  host-only in the shared WorldTick postfix, individually guarded) diffs
  the crew section of the authoritative P6 snapshot — create / update /
  deactivate (absent) / reactivate / remove (15 s grace) / bounded
  history (≤16); agents ≤32, capability refs ≤8; deny-by-default
  authority seam (no probe / faulting probe ⇒ no-op; authority loss
  CLEARS the live map so clients never keep stale authoritative state);
  fail-safe gates (null/never-captured/stale >20 s/future-dated/
  !GameStarted snapshots → no-op with uncertainty logged; null crew
  entries skipped); deterministic lookups (GetAgent/FindByPlayerId);
  task-assignment surface (AssignTask/ClearTask — metadata only, never
  creates/claims/executes; terminal outcomes observed read-only via
  TaskRegistry.Get, Failed-retryable stays assigned, vanished cleared
  after 10 s grace); role-name resolver seam; bounded status/agent
  lines; ResetForTests.
- `Core/Crew/CrewAgentLogBridge.cs` — boots the registry's decision
  listener onto CapBotLog (CREW subsystem) at mod construction.
- `docs/CREW_AGENTS.md` — the full crew-agent contract: identity,
  lifecycle, role model, world-state relationship, task relationship,
  authority/multiplayer behavior, performance bounds, verified-API table,
  future personality/memory integration points, failure modes, tests.
- `tests/CrewAgentTests.cs` — 108 assertions covering all 20 mandated
  scenarios (stable creation, duplicate prevention, bot removal, stale
  reference, captain identification, role mapping, multi-agent isolation,
  task ownership, scheduler/recovery/claims/emergency interaction,
  invalid-player handling, deterministic lookup, bounded registry,
  join/leave lifecycle, captain change, client/master authority, no
  cross-agent contamination, no unauthorized execution, fail-safe gates).

### Changed
- `CapBot.csproj` — three Compile entries for the Crew domain.
- `Mod.cs` — Phase 10 boot block: crew logging bridge + delegate-wired
  seams (authority probe = ExecutionClaims.IsAuthoritative, clock =
  TaskClock.NowMs, world = WorldStateService.Latest, role-name resolver =
  PLPlayer.GetClassNameFromID, fail-safe wrapped). Registry stays INERT
  until the tick driver calls Sync host-side.
- `Patch.cs` — the shared `WorldTick` postfix (PLController.Update)
  extended IN PLACE (still 11 Harmony patch classes): host-only,
  exception-guarded `CrewAgentRegistry.Sync(TaskClock.NowMs)` call after
  the P9 emergency calls; header comment documents Phase 10.
- `tests/run_tests.ps1` — compiles the two Crew domain files and the
  ninth suite; header updated.
- `tests/TaskRecoveryTests.cs` — TestMain runs f9 = CrewAgentTests;
  TOTAL/return gate covers nine suites.

### Notes
- No PULSAR API outside the verified set is touched: the registry's own
  code path consumes WorldSnapshot data only; the role-name resolver is
  the verified static public `PLPlayer.GetClassNameFromID(Int32)`.
- PLPlayer priority management, PLBot behavior trees, PLBotController
  movement, PLFlightAI, RPC patterns, MoreBotsCompatPatch, BotAppearanceFix
  cosmetics and the PML save format are untouched. No competing movement
  AI, no per-frame AI-target manipulation.
- Phase 11+ work (personality, memory, learning, Mission/Economy/Combat
  directors, Captain Brain 2.0, LLM) is NOT implemented.

## [Phase 9 — Emergency director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Emergency/EmergencyState.cs` — the deterministic override-layer
  vocabulary: `EmergencySeverity` (None/Warning/Elevated/Severe/Critical),
  `EmergencyType` (CriticalHull, CriticalCrewHealth, Fire, ReactorCritical,
  DangerousCombat, ImminentDeath reserved, NavigationFailure, FuelCritical,
  CoolantCritical, ObjectiveCritical; WarpFailure deliberately NOT detected —
  no verified failing-vs-charging rule), the six-state `EmergencyState`
  machine with the legal-transition table (`EmergencyStates.CanTransition`
  + `IllegalReason`; Normal→Monitoring→Warning→Emergency→Critical→Recovery→
  Normal, Recovery the only re-entry to Normal), `EmergencyPrecedence`
  (mandated 9-class order: crew survival > ship survival > catastrophe >
  combat > navigation > mission > economy > maintenance; priority =
  100 + class*10 + severity bump — normal work tops out at 13 with aging,
  so any emergency outranks all normal tasks), the bounded immutable
  `EmergencyDecision` (reason ≤ 200 / target ≤ 64, all 15 mandated fields),
  `ActiveEmergency` (dedup record with LastSeenMs/escalating severity) and
  `EmergencyIdentity` ("EID:<TYPE>:<hash8>" via the shared FNV-1a
  `ActionIdentity.ComputeStableHash` — stable across re-evaluations).
- `Core/Emergency/EmergencyDetector.cs` — the pure rule engine over the P6
  snapshot (zero game access, zero clock reads, zero LINQ, quiet path
  allocation-free). Nine rules, thresholds as public consts: CriticalHull
  (hull ≤ .25/.35/.50), CriticalCrewHealth (worst alive bot ≤ .25/.35/.50),
  Fire (CountNonNullFires ≥ 3 Severe / ≥ 1 Warning), ReactorCritical
  (temp ≥ 95%/90% of max), DangerousCombat (≥1 authoritative hostile;
  Severe if ≥3 hostiles or combat-level gap ×1.33 — INFERRED, data-only),
  NavigationFailure (moved <1 m in 5 s while seeking >7 s — vanilla stuck
  trigger, coordination-only), FuelCritical (capsules ≤ 1/2),
  CoolantCritical (≤ 15%/30%), ObjectiveCritical (exactly 1 objective left,
  coordination-only). Fail-safe on every unknown input (NaN fractions,
  -1 counts/ids, missing sections, dead/unknown crew never trigger).
  Realizations are existing registered capabilities only (orders 9/6/1,
  SET_CAPTAIN_TARGET) — validated again by the registry + executor before
  any action.
- `Core/Emergency/EmergencyDirector.cs` — the deterministic director
  (pure C#, System-only): one evaluation per MinRecheckMs (5 s — no per-frame
  loop), fail-safe world gates (null / never-captured / stale >20 s /
  future-dated / !GameStarted → `EmergencyUncertain` logged, nothing
  created), deny-by-default authority seam (no probe or faulting probe ⇒
  no-op), dedup against bounded ACTIVE records (re-detection refreshes
  LastSeenMs + escalates severity only — never a second task), bounded
  shedding (active ≤ 8 sheds oldest, history ≤ 16, ActiveExpiryMs 30 s,
  TaskRequeueBlockMs 20 s), emergency task creation through the P2 lifecycle
  ONLY (type EMERGENCY, owner CAPTAIN, priority from EmergencyPrecedence,
  maxRetries 1, timeout 120 s, metadata EmergencyId/EmergencyType/
  Preemptible="true"/CapabilityId/Argument; preemption is REQUESTED through
  the P4 scheduler's own policy-gated path — the director never pauses,
  fails or cancels anything), hysteresis-gated state machine
  (StateDwellMs 5 s, RecoveryHoldMs 10 s, illegal transitions counted and
  never applied), ReconcileTasks resolves records whose emergency task
  reached a terminal state (task itself left to lifecycle/recovery),
  StatusLines diagnostics, ResetForTests. Pluggable fail-closed seams:
  authority probe, nowMs provider, world provider, decision listener.
- `Core/Emergency/EmergencyLogBridge.cs` — boots the director's decision
  listener into `CapBotLog` (new EMERGENCY subsystem const).
- `docs/EMERGENCY.md` — the Phase 9 contract document (principle, pipeline,
  state machine, precedence, rules table, identity/dedup, preemption
  contract, verified-API table, world dependencies, multiplayer model,
  performance, unsupported types, failure modes, tests).

### Changed
- `Core/World/WorldSnapshot.cs` — additive Phase 9 extension: new readonly
  fields `PlayerShipFireCount` (int, -1 = unknown) and
  `PlayerShipReactorTempFraction` (float, NaN = unknown); original 16-arg
  constructor preserved verbatim (both fields default to unknown); new 18-arg
  constructor chains via `: this(...)`. All Phase 6–8 callers/tests compile
  unchanged.
- `Core/World/PulsarWorldSource.cs` — Capture() fills the two new fields from
  VERIFIED public APIs (`PLShipInfo.CountNonNullFires()`,
  `PLShipStats.ReactorTempCurrent/ReactorTempMax`), each try/catch-guarded
  into `RecordPartial` (partial-failure bookkeeping, -1/NaN on fault).
- `Mod.cs` — Phase 9 boot block: EmergencyLogBridge.Ensure +
  director seams wired (authority probe = ExecutionClaims.IsAuthoritative,
  nowMs = TaskClock, world = WorldStateService.Latest). Deny-by-default;
  the director stays INERT until the tick driver calls Evaluate host-side.
- `Patch.cs` — WorldTick Postfix (host-only) now also drives the emergency
  director: two individually exception-guarded calls,
  `EmergencyDirector.Evaluate(TaskClock.NowMs)` (5 s internal gate) and
  `EmergencyDirector.ReconcileTasks(...)`, after the executor tick. The
  director never executes anything — tasks still route through P4/P5/P7/P8.
- `CapBotLog.cs` — added the `EMERGENCY` subsystem const.
- `CapBot.csproj` — +4 Compile entries (EmergencyState, EmergencyDetector,
  EmergencyDirector, EmergencyLogBridge).
- `tests/run_tests.ps1` — compiles the three emergency domain files + the
  new test suite (f1–f8).
- `tests/TaskRecoveryTests.cs` — TestMain runs eight suites; TOTAL ×8.

### Tests
- `tests/EmergencyTests.cs` — 141 checks covering all 25 mandated Phase 9
  scenarios (S1–S5 legal state chains with dwell hysteresis, S6 illegal
  transition table, S7–S10 precedence classes/severity bumps, S11 duplicate
  detection, S12 identity determinism, S13 stale/future-dated world,
  S14 invalid targets rejected by the registry, S15 authority rejection +
  deny-by-default, S16 capability rejection executor-side, S17 execution-claim
  rejection on emergency tasks, S18 preemption through the scheduler's own
  policy path, S19 preempted task stays recoverable (auto-resume + cancel
  flow), S20 no-storm (20 passes → 1 task; 5 persisting emergencies → 5
  tasks + 55 dedups; bounded active set), S21 bounded shedding/history,
  S22 Quality-Improver-safe hostility (authoritative list only), S23
  master-only (client produces nothing), S24 repeated evaluation without
  duplicate actions, S25 fail-safe on missing/faulting state) plus per-rule
  detection coverage (R1–R8) and fail-safe inputs. Full suite: 806/806 pass
  (665 prior + 141 new).

## [Phase 8 — Task executor] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Executor/ExecutionResult.cs` — the execution result contract:
  `ExecutionOutcome` (Success / FailureRetryable / FailurePermanent /
  Rejected / Unavailable / Cancelled), the bounded immutable
  `ExecutionResult` (reason ≤ 200, ≤ 8 diagnostic metadata entries, key
  ≤ 32 / value ≤ 128, `WithMeta` derived copies), and `ICapabilityDispatcher`
  — the ONLY pathway from an approved task to a gameplay action. No attempt
  ever throws across the seam; everything is data, never executed.
- `Core/Executor/TaskExecutor.cs` — the static executor engine (pure C#):
  per-attempt pipeline resolve live task → consume scheduler grant
  (`TryTakeLease`, exactly-one-executor-pass) → `TryStart` through the P2
  contract → build `CapabilityRequest` from task fields + "CapabilityId"/
  "Argument" metadata (untrusted; registry-validated) → P7 gate-ladder
  validation → P5 `TryClaim` (attemptEpoch = RetryCount — each recovery
  retry is a new action identity) → dispatch EXACTLY ONE registered
  capability → `RecordExecutionResult` (sticky success, duplicate/stale
  ignored) → lifecycle resolution (Success → TryComplete; failure/rejection
  → TryFail handing to P3 recovery; NO retry logic — recovery owns retry,
  backoff, abandon). Validate-before-claim ordering documented (the P7
  claim-probe seam makes claim-then-validate self-conflict; all gates still
  run before any action and the claim remains the last gate). Tick gate
  250 ms, `MaxAttemptsPerTick = 4`, no dispatcher ⇒ `Unavailable`
  (fail-closed), dispatcher faults wrapped as FailureRetryable, invariant
  violations logged and left to recovery. Every refusal resolves the task
  through the lifecycle — nothing wedges. `SetDispatcher`,
  `SetDecisionListener`, `Enabled`, `ResetForTests`.
- `Core/Executor/PulsarCapabilityDispatcher.cs` — the game-facing
  dispatcher: static, code-reviewed branches on CapabilityId calling
  VERIFIED PULSAR APIs only (signatures verified by direct reflection this
  session; call shapes copied from compile-proven shipped sites).
  `SET_CAPTAIN_ORDER` → `PLServer.CaptainSetOrderID(Int32)` direct call
  (order validated against the static vocabulary {1,4,6,8,9,10,11,12,13}
  from shipped `ComputeDesiredOrder`); `ISSUE_MOVE_ORDER` →
  `pawn.photonView.RPC("IssueMoveOrder", PhotonTargets.All, sector.Position)`
  (impl is private in Assembly-CSharp — reachable only via its [PunRPC]
  route; Vector3 derived only from `PLSectorInfo.Position`, never parsed
  from untrusted text); `SET_CAPTAIN_TARGET` → direct
  `PLShipInfoBase.Captain_SetTargetShip(Int32)` (target syncs via stream —
  no PhotonTargets.All duplicate pattern); course-goal channels via the
  exact shipped `PLServer.Instance.photonView.RPC(..., PhotonTargets.All,
  ...)` shapes with sector existence verified against the galaxy table;
  `READ_WORLD_SNAPSHOT` → pure `WorldStateService.Latest` read. A
  registered capability WITHOUT a branch is refused (`Rejected`) —
  registration never makes a capability executable. No reflection dispatch,
  no method-name lookup, no runtime compilation, no interpretation of task
  metadata/chat/mission text as commands.
- `Core/Executor/ExecutorLogBridge.cs` — boots the executor decision
  listener into `CapBotLog` (TASK subsystem): accepted/rejected,
  capability/authority/precondition failures, duplicate execution, stale
  callbacks, claim releases, invariant violations.
- `docs/EXECUTOR.md` — the Phase 8 contract document (flow, ordering note,
  capability→API table with verification basis, tick driver, logging,
  tests).
- `tests/ExecutionTests.cs` — 98 assertions covering all 20 mandated
  scenarios (successful execution, unknown capability, disabled capability,
  invalid task, invalid owner, invalid target, failed precondition, wrong
  authority, missing claim, duplicate claim, duplicate execution request,
  stale callback, cancelled task, completed task, recovery-owned task,
  retryable failure, permanent failure, scheduler grant requirement,
  deterministic execution identity, multiplayer authority gating) plus the
  tick driver. **Suite total now 665/665** (97 + 57 + 89 + 106 + 80 + 138
  + 98).

### Changed
- `Patch.cs` — the Phase 6 `WorldTick` postfix (11th Harmony patch,
  PLController.Update) extended IN PLACE (no new patch) to also drive, host-
  side only (`PhotonNetwork.isMasterClient`, fail-closed try/catch — the
  shipped authority gate), `TaskScheduler.Tick` + `TaskRecoveryManager.Tick`
  + `TaskExecutor.Tick`. Each call individually exception-guarded; all
  subsystems self-throttle (1 s scheduler/recovery, 250 ms executor), so
  the per-frame anchor yields vanilla decision cadence. INERT until tasks
  exist.
- `Mod.cs` — Phase 8 boot block: `ExecutorLogBridge.Ensure()`,
  `TaskExecutor.SetDispatcher(new PulsarCapabilityDispatcher())`,
  `ExecutionClaims.SetAuthorityPolicy` wired to `PhotonNetwork.
  isMasterClient` (fail-closed: any fault denies authority; clients never
  execute — vanilla's request→master pattern untouched).
- `CapBot.csproj` — four `Core\Executor\` Compile entries.
- `tests/run_tests.ps1` / `tests/TaskRecoveryTests.cs` (TestMain) — added
  the ExecutionResult/TaskExecutor domain files and the f7 ExecutionTests
  suite to the harness.

## [Phase 7 — Safe task capability registry] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Capabilities/CapabilityDescriptor.cs` — the capability contract
  vocabulary: `CapabilityAuthority` (MasterOnly/ClientRequest/ClientOnly/
  ReadOnly — MasterOnly is the default for gameplay), `CapabilityDanger`,
  `CapabilityReversibility`, `CapabilityValidation` (15 deterministic
  outcomes), `TargetRequirement` (None/SectorId/ShipId/MissionId/
  BoundedToken), untrusted `CapabilityRequest` holder, and the immutable
  `CapabilityDescriptor` (bounded lists ≤ 8, bounded static text, pure
  `Precondition`/`TargetValidator` delegates, `VerifiedApi` documentation
  text, deterministic `ToContractLine`). Capabilities are data contracts,
  never executable instructions.
- `Core/Capabilities/CapabilityRegistry.cs` — bounded (≤ 32) static
  allowlist with duplicate-safe registration (ids validated to the same
  `[A-Za-z0-9_]` ≤ 32 vocabulary as Phase 5 `actionKind`, so a CapabilityId
  IS a valid actionKind), cheap exact-match lookup (Ordinal dictionary, no
  scanning/LINQ), enable/disable, and the deterministic validation gate
  ladder (malformed → disabled → actor → authority → target → precondition
  → task mismatch → ownership → cooldown → P5 claim conflict → P6 world
  freshness; first failure wins). Ownership gate resolves the LIVE task
  (identity + non-terminal + owner match + request consistency); cooldowns
  stamp only on approval; claim conflict builds the real P5 action identity
  and treats seam faults as conflict (fail-closed). Pluggable seams
  (authority probe / clock / world provider / claim probe) — all
  fail-closed when unwired or faulting; the registry holds no world or
  claim state. `StatusLines` + `ResetForTests`.
- `Core/Capabilities/RegisteredCapabilities.cs` — the Phase 7 catalog: 7
  built-ins (`SET_CAPTAIN_ORDER`, `ISSUE_MOVE_ORDER`, `SET_CAPTAIN_TARGET`,
  `ADD_COURSE_GOAL`, `REMOVE_COURSE_GOAL`, `CLEAR_COURSE_GOALS`,
  `READ_WORLD_SNAPSHOT`), all documenting PunRPC-verified vanilla channels
  (`PLServer.CaptainSetOrderID(Int32)`, `PLPlayer.IssueMoveOrder(Vector3)`,
  `PLShipInfoBase.Captain_SetTargetShip(Int32)`,
  `PLServer.AddCourseGoal/RemoveCourseGoal(Int32)/ClearCourseGoals()`),
  `CAPTAIN`-owner-restricted, `MasterOnly`, cooldowns 1000–5000 ms guarding
  vanilla cadence, plus the read-only Phase 6 snapshot contract.
  `RegisterBuiltIns()` (duplicate-safe) + `AttachProductionSeams()` (wires
  authority→`ExecutionClaims.IsAuthoritative`, clock→`TaskClock.NowMs`,
  world→`WorldStateService.Latest`, claim probe→`GetClaim.Active ||
  Ledger.Observe==Succeeded`). Deliberately excluded: `Captain_SetAutoMode`
  (empty body), `Captain_NameShip`, `SkipWarp/SkipWarpAt` (unrequested),
  and all speculative combat/mission/economy/build capabilities.
- `Core/Capabilities/CapabilityLogBridge.cs` — attaches Phase 1 `CapBotLog`
  (new CAPABILITY subsystem tag) as the registry decision listener at mod
  boot; the domain contains zero logging calls.
- `Core/Logging/CapBotLog.cs` (modified) — added the `CAPABILITY` subsystem
  tag (additive; OLLAMA remains reserved).
- `CapBot.csproj` (modified) — compile entries for the four new files.
- `Mod.cs` (modified) — boot wiring: `CapabilityLogBridge.Ensure()`,
  `RegisteredCapabilities.RegisterBuiltIns()`,
  `RegisteredCapabilities.AttachProductionSeams()`.
- `docs/CAPABILITIES.md` — full contract: security boundary (contracts not
  executable instructions — no C# generation/runtime compilation/DLL
  loading/shell execution/reflection invocation/LLM-text-as-commands),
  descriptor table, 13-gate validation ladder table, authority matrix
  (fail-closed defaults), seam table, the 7-capability catalog with
  verified APIs, task-system integration contracts (P2/P3/P4/P5/P6),
  compatibility posture (Better AI/MoreBots/Quality Improver — no hostility
  assumptions), performance, logging examples, explicit not-in-phase list.
- `tests/CapabilityTests.cs` — 138 dev-side assertions (not shipped)
  covering all 14 mandated scenarios: registration (+ id vocabulary
  boundaries), duplicate registration, bounded registry cap, unknown
  rejection, deterministic lookup (instance-stable Get, sorted bounded id
  list), happy-path approval, malformed request/task (incl. request TaskId
  ≤ 0 and null request owner — request data validated as untrusted),
  disabled capability, actor allowlist (+ wrong-case owner), authority
  rejection (deny-by-default + faulting probe fail-closed), invalid targets
  (kind/non-integer/negative/empty/over-length token/punctuation), declared
  preconditions + target validators (+ faulting validator fail-closed),
  task-type mismatch, ownership mismatch (not-registered, wrong owner,
  spoofed task id, cancelled task still in registry history), P5 claim
  integration (CapabilityId as actionKind, ledger-Succeeded duplicate
  rejection, unexpired-claim conflict, expired-lease clearance, faulting
  claim probe), P6 world integration (missing seam → RejectedWorldStateMissing,
  never-captured, fresh, stale → RejectedWorldStateStale, boundary,
  faulting provider), catalog metadata integrity (all 7 capabilities'
  authority/target/cooldown/VerifiedApi/owner assertions, bounded fields,
  ToContractLine, StatusLines, enable/disable round-trip). Harness
  `run_tests.ps1` + TestMain wired for six suites. Combined TOTAL:
  **passed=567 failed=0** (97 lifecycle + 57 recovery + 89 scheduler +
  106 claims + 80 world + 138 capability).

### Notes
- Contract layer only: the registry validates and approves — it never
  executes. No PULSAR API is called anywhere in this layer (`VerifiedApi`
  fields are static documentation), no executor exists to consume an
  `Approved` outcome, and no gameplay can route through the registry yet.
  The authority seam is doubly fail-closed (registry gate +
  claims deny-by-default) until P8 wires `PhotonNetwork.isMasterClient`.
- No Harmony patches added (still 11), no RPC changes, no vanilla AI
  behavior changes; Better AI/MoreBots/Quality Improver compatibility
  unaffected (no hostility assumptions encoded). No new PULSAR/PML API
  usage — pure C# domain + P2–P6 integration.
- Sighted but deliberately unregistered during API verification:
  `PLServer.Captain_SetAutoMode(Bool)` (empty body — no observable effect),
  `PLServer.Captain_NameShip(String)`, `PLServer.SkipWarp/SkipWarpAt`.
- Not implemented (later phases): executor (P8), directors (P9/P15–P17),
  Captain Brain 2.0, Decision Validator, Ollama/Qwen, dynamic task
  generation, persistence, UI, updater security, performance refactoring.

## [Phase 6 — Game/World State observation layer] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/World/WorldSnapshot.cs` — immutable, bounded, value-style snapshots:
  `WorldSnapshot` root (session/game-started/host/hub, ships, crew, missions,
  threats, navigation, resources, world objects, per-section
  `WorldAuthority`), section types (`ShipSnapshot`, `CrewMemberSnapshot`,
  `MissionSnapshot`, `ThreatSnapshot`, `NavigationSnapshot`,
  `ResourceSnapshot`, `WorldObjectSnapshot`), `WorldTransition`
  (`SECTOR_CHANGED`/`WARP_STARTED`/`WARP_ENDED`). All collections bounded at
  construction (ships ≤ 24, crew ≤ 16, missions ≤ 16, world objects ≤ 16,
  hostiles ≤ 16, course goals ≤ 8, research ≤ 8); names truncated; NaN/-1/
  null = unknown sentinels; no game-object references held. Player ship is
  `Ships[0]`, captain is `Crew[0]` (ordering contract).
  `WorldSnapshot.Empty` = never-captured placeholder.
- `Core/World/WorldStateService.cs` — pure C# cache/transition detector:
  throttled `Refresh(nowMs)` (1 s default = vanilla decision cadence),
  transition detection from last-seen sector/warp values (survives
  `SetSource` resets; listener fires OUTSIDE the lock, never on first
  capture, ≤ 4 per refresh), sticky
  `HasUnacknowledgedSectorChange()`/`AcknowledgeSectorChanged()` for
  slow-cadence consumers, freshness (`GetFreshness`, 10 s `MaxSnapshotAgeMs`),
  diagnostics (`RefreshCount`/`ErrorCount`/`LastError`), `ResetForTests`.
  Pluggable `IWorldSource` seam; dormant by construction until a source is
  set AND a tick caller refreshes.
- `Core/World/WorldSnapshotProbe.cs` — recovery's real `ITaskWorldProbe`
  (Phase 3 deliverable) answering from the latest snapshot. **Fail-open on
  uncertainty**: never-captured/stale/empty-view snapshots never drive
  destructive recovery actions; positive evidence only (SHIP/MISSION target
  presence, crew membership for `CAPTAIN`/`BOT:<id>` owners with
  `AliveKnown` death evidence; populated-crew absence = positive).
  `CapabilityAvailable` defers to P7; `WorldInvalidatesTask` stays false (no
  invented premise semantics). Injectable snapshot/time providers for
  deterministic tests.
- `Core/World/PulsarWorldSource.cs` — game-facing `IWorldSource` reading
  verified registries ONLY (`PLServer.Instance` GameHasStarted/AllPlayers/
  AllMissions/CurrentCrewCredits/ResearchMaterials/CurrentUpgradeMats/
  m_ShipCourseGoals/GetCurrentSector, `PLEncounterManager.Instance` AllShips/
  PlayerShip, PLShipInfoBase MyStats/HostileShips/TargetShip/GetCombatLevel/
  AlertLevel/InWarp/WarpChargeStage/WarpTargetID/MyFlightAI caches,
  PLPlayer GetPlayerName/GetPlayerID/IsBot/GetClassID/TeamID/GetPawn/
  MyCurrentTLI/ActiveMainPriority, PLBotController stuck metrics via
  PLPlayer.MyBot, PLMissionBase objectives, MyFlightAI.cachedRepairDepotList/
  cachedWarpStationList, PLBeaconInfo beacons). Zero FindObjectsOfType, zero
  scene scans. Non-throwing by section (`PartialErrorCount`/
  `LastPartialError` diagnostics); hostiles read from the game's own
  `HostileShips` list (Quality Improver-safe: never calls hostility logic).
- `Core/World/WorldLogBridge.cs` — attaches Phase 1 `CapBotLog` (TASK) as
  the transition listener at mod boot; the world domain contains zero
  logging calls.
- `CapBot.csproj` (modified) — compile entries for the five new files +
  `PilotAIBuild.dll` reference (transitive base-class assembly of the
  flight-AI type).
- `Mod.cs` (modified) — boot wiring: `WorldLogBridge.Ensure()`,
  `WorldStateService.SetSource(new PulsarWorldSource())`,
  `TaskRecoveryManager.Probe = new WorldSnapshotProbe()`.
- `Patch.cs` (modified) — `WorldTick` Harmony postfix on `PLController.Update`:
  the only new game hook; calls the read-only throttled refresh, exception-
  guarded so it can never alter controller behavior.
- `docs/WORLD_STATE.md` — full contract: data flow, snapshot model (bounds,
  sentinels, authority marks, ordering contracts), service semantics
  (throttle, transitions, sticky flag, freshness), source read-only/non-
  throwing posture, fail-open probe decision table, multiplayer/host-
  migration constraints, performance, security posture, not-in-phase list.
- `tests/WorldStateTests.cs` — 80 dev-side assertions (not shipped) covering
  the mandated scenarios: null/missing objects (Empty + null sections), no
  crew, multiple bots, missing captain (positive absence), sector
  transition (incl. never-fabricated from unknown ids), no active mission,
  multiple missions, destroyed targets (SHIP/MISSION positive-absence),
  invalid/stale refs (stale = fail-open, boundary exactness), host/client
  authority marking, deterministic construction (identical summary lines),
  bounded collection sizes (all seven bounds). Service: throttle, dormant
  null-source, freshness, transitions (first-capture silence, warp edges,
  SetSource reset), source-throw containment, reset. Harness
  `run_tests.ps1` + TestMain wired for five suites. Combined TOTAL:
  **passed=429 failed=0** (97 lifecycle + 57 recovery + 89 scheduler +
  106 claims + 80 world).

### Notes
- Observation layer only: reads authoritative game state into bounded
  immutable snapshots. It executes nothing, mutates nothing, issues no
  orders, and adds no RPCs. The refresh tick is read-only; the probe is
  attached but recovery still has no tick driver, so no gameplay routes
  through world state yet — existing behavior is unchanged.
- Hostility semantics are deliberately assumption-free (Quality Improver can
  replace `ShouldBeHostileToShip`): the authoritative hostile set is the
  game's own `HostileShips` id list; team counts are raw observations.
  Combat-level semantics INFERRED per research §6.6, carried as data only.
- Mission objective *types* are not readable on `PLMissionObjective`
  instances (no `ObjType` member): snapshots carry completion counts + first
  incomplete objective text instead.
- `PLWarpStation`/`PLRepairDepot` have no static registries; world objects
  read the player ship's own flight-AI cached lists (shipped-code-proven).
- Not implemented (later phases): capability registry, executor, directors,
  Captain Brain 2.0, Decision Validator, LLM integration, dynamic task
  generation, persistence, UI, secure updater, performance refactoring.

## [Phase 5 — Duplicate execution protection (claims/leases/idempotency)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/ActionIdentity.cs` — deterministic, data-only action identity
  for future executors: `ActionIdentity.MakeActionId(taskId, actionKind,
  attemptEpoch, targetKey)` → `"<taskId>:<kind>:<epoch>:<hash8>"`, hashed
  with session-stable FNV-1a 32-bit (never `string.GetHashCode`, which is
  not stable). `actionKind` is validated to a bounded static ASCII
  vocabulary (`[A-Za-z0-9_]`, ≤ 32 chars); the opaque `targetKey` is hashed,
  never embedded. `ActionOutcome` enum + `ActionLedger`: bounded (256-entry
  FIFO) idempotency memory — sticky `Succeeded` (never downgraded),
  `Failed→Succeeded` upgrade allowed, duplicate/repeated outcomes are
  no-ops. Identity is compared or logged, never parsed or dispatched on.
- `Core/Tasks/ExecutionClaims.cs` — single-owner execution claims with
  bounded 5 s leases (`ClaimLeaseDurationMs`), one claim per live task
  (≤ 64 = registry cap), attempt epoch default = task `RetryCount`
  (retried task = new logical action; duplicate request = same action).
  `TryClaim` is a deterministic gate ladder (invalid args → not
  authoritative → task missing → terminal → recovery-owned Failed/Paused →
  owned-by-other → duplicate-active → expired-takeover → ledger
  already-succeeded → Granted); `GrantedTakeover` makes stale-lease recovery
  explicit and logged (`LeaseExpired` + `OwnershipReleased`), so stale owners
  never retain ownership. Idempotency two-sided: claim side refuses actions
  already `Succeeded` in the ledger (`DuplicateExecutionRejected`); result
  side (`RecordExecutionResult`) records the first result, releases the
  claim, and ignores duplicate/stale callbacks (`DuplicateIgnored` /
  `StaleCallbackIgnored`). `ReleaseClaim` verifies the owner — mismatch is
  logged `InvariantViolation` and refused. `Tick` hygiene drops
  expired/missing/terminal-task claims. **Deny-by-default authority seam**
  `SetAuthorityPolicy(Func<bool>)`: with no policy, nothing can claim or
  record (fail-closed); Phase 8 wires it to `PhotonNetwork.isMasterClient`.
  Per-claim 1 s rejection-log throttle; no RPCs, no Photon targets, no
  process-external state; bounded memory throughout; no LINQ.
- `Core/Tasks/ClaimLogBridge.cs` — attaches the Phase 1 `CapBotLog` (TASK
  subsystem) as the claims decision listener at mod boot; the domain
  contains zero logging calls.
- `CapBot.csproj` (modified) — compile entries for the three new files.
- `Mod.cs` (modified) — boot wiring: `ClaimLogBridge.Ensure()` next to the
  lifecycle/recovery/scheduler bridges.
- `docs/EXECUTION_SAFETY.md` — full contract: claim model (record shape,
  identity format + FNV-1a rationale, attempt epochs, lease + takeover
  semantics), deterministic claim-rules table (orders 0–10), the two-sided
  idempotency guard (claim side + result side, sticky success, upgrade rule,
  explicit release + invariant logging), Tick hygiene, authority model
  (deny-by-default seam, P8 wiring to `isMasterClient`, process-local
  bookkeeping, no RPCs, host-migration-safe), scheduler interaction (grant
  vs claim separate lifetimes), recovery interaction (Failed/Paused refuse
  claims; claims persist through capability-pause; failure results release;
  fresh epoch after retry; no retry loops), logging examples, security
  posture, explicit not-in-phase list.
- `tests/ExecutionClaimTests.cs` — 106 dev-side assertions (not shipped)
  covering all 15 required scenarios: deterministic identity, deny-by-default
  authority gating, duplicate/same-owner/different-owner claims, lease
  expiry + stale takeover, duplicate completion/failure, stale callbacks,
  release ownership invariants, task cancelled/completed while claimed,
  recovery interaction (owner-down fail → release → recovery-owned refusal →
  fresh-epoch re-claim; capability-pause persistence), scheduler pass
  repeated twice (re-grant vs duplicate-execution refusal), bounded ledger
  eviction + live-claim cleanup, `MakeDefaultActionId` epoch determinism,
  null-reason release, status snapshot. Harness `run_tests.ps1` + TestMain
  wired for four suites. Combined TOTAL: **passed=349 failed=0** (97
  lifecycle + 57 recovery + 89 scheduler + 106 claims).

### Notes
- Protection layer only: claims/leases/idempotency for future executors
  (P7/P8). It executes nothing, holds no world state, and is deny-by-default
  inert until the authority policy is wired (P8 → `isMasterClient`).
  No gameplay routes through it; the scheduler and recovery behavior are
  unchanged. No new PULSAR/PML/Photon API usage (pure System* domain), no
  RPC/Harmony/vanilla-AI changes, no MoreBots-compat or save-format impact.
- Not implemented (later phases): capability registry, executor, world
  state, directors, Captain Brain 2.0, Decision Validator, Ollama/Qwen,
  dynamic task generation, persistence, UI, updater security, performance.

## [Phase 4 — Task scheduler (orchestration-only)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/TaskScheduler.cs` — deterministic grant/lease scheduler over the
  Phase 2 registry's `Queued` pool (the queue IS the registry — no parallel
  queue structure). One pass (`Tick(nowMs)`): 1 s global re-check gate
  (vanilla decision cadence), lease-expiry hygiene, preemption-record prune +
  auto-resume of scheduler-initiated pauses, deterministic candidate ordering
  (effective priority desc with bounded aging +1/30 s capped +5 → FCFS →
  TaskId; insertion sort, no LINQ in the tick path), gated grant loop
  (deadline-elapsed refused; dependencies must resolve to `Completed`;
  one grant per owner — leases AND Running tasks both count as busy;
  `MaxGrantsPerTick` = 8, `LeaseDurationMs` = 5000), and a policy-gated
  preemption pass (explicit `Preemptible=="true"` metadata opt-in,
  `PreemptMargin` > 2, ≥ 3 s min runtime, < 2 lifetime preemptions — tally
  survives auto-resume as a dormant record —, owner not leased elsewhere;
  victim paused through the lifecycle-enforced `TryPause`). Scheduler state
  is two bounded dictionaries (leases, preemption records, both ≤ live cap,
  dropped when the task leaves the live registry). Grants are suggestions,
  not execution: the P8 executor claims via `TryTakeLease` (consumes the
  lease; double claims fail). `Enabled` switch, decision-listener hook,
  `ActiveGrantCount`, `HasLease`, `SchedulerStatusLines` diagnostics. The
  scheduler never retries, expires, fails, or resumes recovery-paused tasks —
  refusal-only interaction with Phase 3 recovery, and it resumes ONLY its own
  preemption-pauses.
- `Core/Tasks/SchedulerLogBridge.cs` — attaches the Phase 1 `CapBotLog`
  (TASK subsystem) as the scheduler's decision listener at mod boot; the
  scheduler itself contains zero logging calls.
- `Core/Tasks/TaskRegistry.cs` (modified) — added `LiveSnapshot()`: bounded
  point-in-time list of live tasks so scheduler/recovery passes never touch
  registry internals (additive, no behavior change).
- `Mod.cs` (modified) — boot wiring: `SchedulerLogBridge.Ensure()` next to
  the lifecycle/recovery bridges.
- `CapBot.csproj` (modified) — compile entries for the two new files.
- `docs/TASK_SCHEDULER.md` — full contract: queue-is-registry model, pass
  description, gates table (incl. why there is deliberately no
  retry-headroom gate), deterministic ordering, preemption policy (all
  conditions + record lifecycle), no-starvation properties, recovery-state
  interaction, multiplayer/authority constraints for the future P8 driver,
  logging, security posture, explicit not-in-scope list.
- `tests/TaskSchedulerTests.cs` — 89 dev-side assertions (not shipped):
  deterministic ordering (equal-priority FCFS/TaskId, priority precedence,
  bounded aging), dependency gating (Completed/history/unresolvable/expired
  deps; scheduler never expires), terminal-task invisibility, deadline
  refusal without expiry, recovery interaction (backoff-pending and
  final-retry attempts grantable; scheduler never touches recovery pauses),
  owner gates (lease + Running busy), bounded behavior (8 grants/pass, 1 s
  gate, lease cap), lease model (claim seam, double-claim rejection,
  expiry/re-grant), and the full preemption path (pause, auto-resume,
  re-preemption, lifetime cap, dormant tally, foreign-pause non-interference,
  margin/min-run/opt-in/cross-owner/victim-selection gates). Combined TOTAL:
  **passed=243 failed=0** (97 lifecycle + 57 recovery + 89 scheduler).

### Notes
- Orchestration only: the scheduler selects/orders existing registered tasks
  and issues bounded grants; it executes no gameplay code, writes no nav
  fields, touches no vanilla priority/behavior-tree system, makes no LLM
  decisions, and holds no world/transient vanilla state (records reference
  tasks by id + string owners). `Tick` is not wired to any game loop this
  phase — the scheduler is dormant by construction; the future P8 driver must
  additionally gate on `PhotonNetwork.isMasterClient`.
- Existing gameplay untouched: no changes to Patch.cs, Autonomy.cs, any
  Harmony patch, RPC pattern, or PML save format. No new PULSAR/PML API usage.

## [Phase 3 — Task recovery foundation] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/TaskRecovery.cs` — recovery policy: `RecoveryActionType`
  (None/Retry/Pause/Resume/Fail/Cancel/Expire), `ITaskWorldProbe` (the single
  seam through which recovery observes current authoritative world state),
  `NullWorldProbe` (correct-but-inert default until Phase 6 supplies a real
  probe), and `TaskRecoveryPolicy.Decide` — a pure, deterministic decision
  function with fixed rule precedence (timeout → world-invalidated →
  target-invalid → owner-loss → capability → stuck → retry/abandon).
- `Core/Tasks/TaskRecoveryManager.cs` — per-task recovery bookkeeping (bounded:
  one record per registered task, ≤ 64, dropped on terminal), 1 s recheck gate
  per task, bounded exponential retry backoff (2 s base, ×2, 30 s cap),
  lifetime recovery budget (12 non-terminal actions → forced terminal abandon),
  capability-pause ceiling (60 s), pluggable action-listener hook (fired
  outside the manager's lock).
- `Core/Tasks/RecoveryLogBridge.cs` — attaches the Phase 1 `CapBotLog` (TASK
  subsystem) as the manager's action listener at mod boot; recovery outcomes
  log as `Recovery applied/rejected <Action> on <task status line> (reason)`.
- `Core/Tasks/TaskLogBridge.cs` (modified) — the single registry listener now
  also feeds `TaskRecoveryManager.Track` on task registration (records exist
  only for registry-tracked tasks).
- `docs/TASK_RECOVERY.md` — full contract documentation: recovery state
  machine, rule precedence table, retry/backoff/budget semantics, stale-world
  handling (research constraints: no reliance on transient vanilla AI state,
  host-migration-safe), cadence rules, explicit not-in-scope list.
- `tests/TaskRecoveryTests.cs` — 57 dev-side assertions (not shipped):
  backoff curve, every recovery rule incl. precedence, retry exhaustion,
  capability pause/resume/abandon, external-pause non-interference, stuck
  detection with progress-refresh, budget backstop, recheck gate, disabled
  manager, null-probe safety, record lifecycle, status snapshot. Combined with
  the Phase 2 suite: **TOTAL passed=154 failed=0**.

### Notes
- Policy layer only: recovery never creates, queues, selects, or executes
  gameplay work — every mutation flows through Phase 2's idempotent lifecycle
  transitions, and the manager is inert until Phase 6 provides a real
  `ITaskWorldProbe` and a tick driver. No Harmony/RPC/gameplay behavior
  touched; no new PULSAR/PML API usage.
- Research constraints honored (PULSAR_GAMEAI_RESEARCH.md): recovery caches
  no world state, holds no Unity/path/Behave references, re-derives decisions
  from current probe answers only (host-migration-safe by construction); ~1 s
  decision cadence matching vanilla's decision gates; hostility semantics are
  probe-owned (QualityImprover-safe).

## [Phase 2 — Task lifecycle infrastructure] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/TaskState.cs` — `TaskState` enum (Created/Queued/Running/Paused/
  Completed/Failed/Cancelled/Expired), `TaskIds` monotonic identity counter,
  `TaskClock` wrap-safe millisecond clock, and `TaskTransitions` — the single
  legal-transition table (terminal states have no outgoing transitions).
- `Core/Tasks/CapBotTask.cs` — the task model: immutable identity/owner/priority/
  retries/timeout/dependencies/target data; guarded, idempotent transitions
  (`TryQueue/TryStart/TryPause/TryResume/TryComplete/TryFail/TryCancel/
  TryExpire/TryRetry`); bounded metadata; deterministic `ToStatusLine` reporting.
  Pure C# (System-only) — no Unity/PULSAR/PML references, holds no game objects.
- `Core/Tasks/TaskRegistry.cs` — bounded registry (≤ 64 live tasks, registration
  fails at the cap — no eviction; ≤ 128 history entries, ring drop) that mirrors
  task state automatically and exposes a transition-listener hook plus
  deterministic `StatusLines` reporting.
- `Core/Tasks/TaskLogBridge.cs` — attaches the Phase 1 `CapBotLog` (TASK
  subsystem) as the registry's transition listener at mod boot; the only file
  connecting the task domain to logging, keeping the domain pure/testable.
- `docs/TASK_LIFECYCLE.md` — full contract documentation: states, complete
  transition table, 10 invariants, ownership/cancellation/failure/retry
  semantics, dependency representation, lifecycle logging, explicit
  not-in-scope list for later phases.
- `tests/TaskLifecycleTests.cs` + `tests/run_tests.ps1` — dev-side unit tests
  (not shipped in the mod): 97 assertions covering validation, every legal/
  illegal transition, idempotence, retry/exhaustion semantics, expiry sweep,
  registry bounds (live cap, history ring), identity/equality, metadata caps
  and deterministic status reporting. Result: **97 passed / 0 failed**.
- Boot wiring: `Mod()` constructor calls `TaskLogBridge.Ensure()`.

### Notes
- Infrastructure only: no gameplay routes through the task system yet; the
  existing captain AI, Harmony patches, RPC patterns and PML save format are
  untouched. Scheduler (P4), recovery (P3), duplicate-execution protection (P5),
  directors and Captain Brain 2.0 (P15–P18) are explicitly out of scope and
  must build on the contracts documented in `docs/TASK_LIFECYCLE.md`.
- No new PULSAR/PML API usage — the domain invented none and calls nothing
  game-facing.

## [Phase 1 — Logging & error hardening] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Logging/CapBotLog.cs` — central leveled logger (`Trace`…`Critical`) with
  subsystem tags (CORE, CAPTAIN, CREW, MISSION, NAVIGATION, COMBAT, ECONOMY,
  RESEARCH, PERSISTENCE, NETWORK, COMPAT, UPDATER, UI). Backs onto PML's
  `Logger.Info` only — the one PML logging API verified by reflection.
- Spam/flood guards in the logger: at most one line per message key per 8 s,
  global 24 messages / 10 s flood window, 256-key cap, wrap-safe
  `Environment.TickCount` deltas. The logger itself can never throw.
- `VerboseLogging` persistent setting + "Verbose Logging" button in
  Mod Settings → CapBot. `Trace`/`Debug` lines are only emitted when it is on.
- Outer guard around the whole captain tick (`Patch.Postfix` → `PostfixCore`)
  so a failure in any scripted-sector handler can no longer break the patched
  `PLPlayer.UpdateAIPriorities` (audit finding C2).
- Per-handler guards for AtColony, WarpGuardianBattle, WastedWing, HandleShop,
  GetMissionFromHub, PlanetExploration (orders 12/13), BoardEnemy, HandleComms,
  AtWDWeapons, Burrow, AtRaces, HighRollers and SetNextDestiny — each failure is
  logged (subsystem-tagged warning) and the tick section is skipped.

### Fixed
- Null-dereference crashes in scripted sectors (audit finding C2):
  - `AtRaces`: race start screen not spawned yet → handler now exits cleanly.
  - `AtWDWeapons`: `PLBurrowArena` not spawned yet → clean exit; mission 59682
    objective 1 only marked when the mission exists and has ≥ 2 objectives
    (`Objectives` is a `List<>`, verified by reflection).
  - `BoardEnemy`: target ship cleared between check and handler → clean exit.
- All 32 silent `catch { }` blocks (audit finding H1) now log through CapBotLog
  with static message keys and the exception type/message, instead of vanishing.
- `/updateall` no longer null-refs when run before the local player exists
  (audit finding M7); command failures are logged.
- Mod updater: staged-apply failures, per-mod check failures and boot-time
  auto-update failures are all logged (they were previously invisible).

### Changed
- All remaining direct `Logger.Info("[CapBot] ...")` calls are routed through
  CapBotLog with subsystem tags.
- Build: game-assembly references now resolve through the `$(PulsarManaged)`
  MSBuild property (default `C:\SteamLibrary\steamapps\common\PULSARLostColony\PULSAR_LostColony_Data\Managed`)
  instead of hardcoded relative paths to a non-existent `D:\SteamLibrary`
  (audit finding H3). Override with `msbuild /p:PulsarManaged=<path>`.
- Machine-specific PostBuildEvent XCOPY copy step removed from the csproj.
- Compiler toolset dependency (OpenSesame.Net.Compilers.Toolset 4.0.1, supplies
  the compiler that allows compiling against internal game members) is restored
  via `nuget restore`; `IgnoresAccessChecksToAttribute` resolves from 0Harmony
  exactly as in the original build.
- `LangVersion` pinned to 8.0 to match the toolchain.

### Notes
- No gameplay/AI behavior changed: this phase only adds observability, guards
  the same code paths, and fixes crash paths that could never have worked
  (the three null-deref handlers). RPC patterns, walkthrough coordinates,
  PML save format, NonCaptainMenu, executor election and anti-spam logic are
  untouched (audit §8 do-not-touch list).