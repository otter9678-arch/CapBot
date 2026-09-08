# Changelog

All notable changes to CapBot are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Phase 17 — Combat director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Combat/CombatDirector.cs` — the combat-tracking layer (BOUNDED
  DETERMINISTIC REPORT-ONLY **by mandate**: the P7 catalog DOES contain one
  combat-adjacent capability — SET_CAPTAIN_TARGET, executor-dispatchable —
  but its authorship is owned by the P9 emergency director (DangerousCombat)
  and by the legacy captain tick (ComputeDesiredOrder / BoardEnemy / blind
  jump), so P17 produces the combat-side coordination record and creates NO
  tasks). Single `COMBAT:ENGAGEMENT` episode record (bounded set ≤ 8 =
  MaxActiveCombatRecords with deterministic oldest-shedding — defensive-only
  in this contract, house-pattern parity — history ≤ 16 = MaxHistory, hostile
  set empty past 60 s = ActiveExpiryMs decays the record with a one-shot
  `CombatVanished` report, decision cadence 5 s). One-shot edge reports:
  `CombatOpened` (first pass with ≥ 1 authoritative hostile — immediate, no
  dwell: P9 owns immediate severity; record-creation vocabulary, re-emits
  only after the record decays), `HostileEngagementReport` (after
  EngagementDwellMs = 15 s of a STABLE hostile picture — any composition
  change re-arms with a fresh dwell clock; carries hostile count, our/target
  combat levels (INFERRED semantics, data-only), the gap label mirroring
  P9's INFERRED 1.33 threat-readout const, and the player-ship hull
  fraction), `HostileClearedReport` (hostiles drop to zero: one-shot episode
  close; the tracked record survives — hygiene owns expiry — and re-entry
  re-opens the episode), `WarpEngagementReport` (hostiles present while the
  player ship is in warp — pure data picture, vanilla/legacy own all
  warp/escape behavior), `UnderFireReport` (player ship TookDamageRecently
  while hostiles present — pure data: P9 owns fire/hull SEVERITY), 
  `BoarderReport` (InvadersOnboardCount > 0 — independent of hostiles; data
  only: vanilla repel + legacy order-6 own the response), `CombatUncertain`
  fail-safe lines. Fail-safe gates: authority deny-by-default seam (clients
  never report), cadence, snapshot staleness (>20 s / future / never-captured
  / !GameStarted), unknown sentinels (NaN combat levels → "-" never a
  trigger; InvadersOnboardCount −1 → silent + UnknownInputPasses;
  TookDamageRecently false covers "not damaged" and "capture unknown"),
  zero-hostile passes are legitimate quiet passes (not unknowns). NO tasks,
  no ReconcileTasks, no RPCs, no target authorship, no weapon/fire APIs, no
  severity ladder, no config-slider wiring (audit H4: AIReactionSpeed /
  AIAccuracy / CombatEngageRange / CombatDisengageHealth are DEAD; the
  legacy blind-jump hull floor 0.2f + 60 s cooldown are documented as
  data-only constants). Counter readbacks + bounded deterministic
  diagnostics (Lines one per tracked record, StatusLines = 2).
  ResetForTests.
- `Core/Combat/CombatLogBridge.cs` — attaches CapBotLog (COMBAT, existing
  const) as the director's decision listener at boot (same pattern as the
  other phase bridges).
- `WorldSnapshot.cs` + `PulsarWorldSource.cs` — ADDITIVE P6 capture (P9
  ctor pattern): `ShipSnapshot.TookDamageRecently` (game-owned "took damage
  recently" window — `Time.time - LastTookDamageTime() < 10f`, compile-proven
  shipped Patch.cs:242) and `ThreatSnapshot.InvadersOnboardCount`
  (`playerShip.InvadersOnboard` — DLL reflection-verified public
  System.Int32 property; docs/EMERGENCY.md §165) — per-field try/catch
  RecordPartial, original ctors preserved verbatim (defaults false / −1),
  new ctors chain `: this(...)`.
- `docs/COMBAT_DIRECTOR.md` — full contract: report-only-by-mandate
  rationale (ownership argument), rules, gates, lifecycle bookkeeping, data
  flow, the additive capture table, authority model, audit honesty notes
  (H4 dead combat sliders), performance, verified-API table, failure modes,
  tests, scope boundaries.
- `tests/CombatTests.cs` — 77 assertions CS01–CS13: engagement episode
  end-to-end, composition-change re-arm (fresh dwell), episode close +
  live-record re-entry semantics, combat-level gap labeling
  (unfavorable/sub-threshold/NaN), warp-combat picture, under-fire episodes
  with re-arm, boarder episodes (independent of hostiles, −1 sentinel),
  quiet paths (zero hostiles = legitimate quiet), fail-safe inputs
  (null/stale/not-started/future), authority deny-by-default, vanished +
  expiry hygiene + bounded history (fresh-publish-after-advance discipline),
  cadence + counters + diagnostics determinism, additive-capture ctor
  regression. Hooked as f16 (sixteen suites); run_tests.ps1 compiles the
  combat domain file + test file with the rest. **TOTAL 1640/1640** (1563
  prior + 77 new).

### Changed
- `CapBot.csproj` — +2 Compile entries (Core/Combat/CombatDirector.cs,
  Core/Combat/CombatLogBridge.cs) after the Economy entries.
- `Mod.cs` — Phase 17 boot block after the economy seams: CombatLogBridge
  .Ensure() + authority/now/world seams (deny-by-default; no classifier
  seam needed this phase — hostile membership comes from the authoritative
  HostileShips list, no game enums consumed).
- `Patch.cs` — WorldTick Postfix: ONE new guarded block after the economy
  block (`CombatDirector.Evaluate` ONLY — IL 395 → 430 bytes; still 11
  Harmony patch classes, ceiling preserved; no ReconcileTasks — nothing to
  reconcile).
- `tests/run_tests.ps1` — compiles Core/Combat/CombatDirector.cs +
  tests/CombatTests.cs with the rest; `tests/TaskRecoveryTests.cs` — f16
  hook + sixteen-suite TOTAL/gate.

### Notes
- Zero new unverified APIs: the two additive captures are compile-proven
  (Patch.cs:242 window) or DLL reflection-verified (InvadersOnboard Int32
  property); everything else consumes existing P6 snapshot fields.
- Report-only by mandate (ownership argument — §1 of the contract doc);
  SET_CAPTAIN_TARGET authorship stays P9/legacy-owned; no new combat
  capability registered (P7 catalog stays at 7 built-ins).
- Combat-level semantics remain INFERRED (research §6.6) and data-only,
  mirroring the P9 EmergencyDetector precedent.
- Verification summary: Release build 0/0; tests 1640/1640 (16 suites);
  reflection 182 types / 159 named (P16 was 177/155), CombatDirector 53
  members + CombatRecord 7 members, all 15 constants exact, ShipSnapshot 13
  fields / 2 ctors, ThreatSnapshot 8 fields / 2 ctors, P6–P16 intact,
  harmony_patch_classes=11, WorldTick Postfix IL 430, namespace
  CapBot.Core.Combat present.

## [Phase 16 — Economy director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Economy/EconomyDirector.cs` — the economy-tracking layer (BOUNDED
  DETERMINISTIC REPORT-ONLY, P15 mirror): credits tracking in
  `ECONOMY:CREDITS` records (bounded set ≤ 8 = MaxActiveEconomyRecords with
  deterministic oldest-shedding by LastSeenMs (tie → lowest key order,
  `EconomyShed` line), history ≤ 16 = MaxHistory, credits unreadable past
  60 s = ActiveExpiryMs decays the record with a one-shot `EconomyVanished`
  report, decision cadence 5 s). One-shot edge reports: `EconomyOpened`
  (first readable credits), `CreditsLowReport` (credits ≤ ReserveFloor =
  2500 — the legacy CREDIT_RESERVE documentation, audit H4: the dead
  MinCreditsReserve slider is NOT wired — one report per episode re-armed by
  recovery), `CreditsDeltaReport` (change beyond MaxDeltaReportAbs = 10000;
  direction + magnitude only, NEVER cause inference — the snapshot cannot
  attribute deltas), `StoreSectorReport` (shop-class sector per the
  classifier SEAM — production wires the exact compile-proven
  ESectorVisualIndication list shipped Patch.cs:169-171 uses — + 15 s
  StoreDwellMs, one report per sector episode, exit closes/re-entry
  re-arms), `FuelAffordabilityReport`/`CoolantAffordabilityReport` (supply
  low mirroring the P9 warning thresholds as data constants ≤ 2 capsules /
  ≤ 30% AND captured unit price readable AND credits < price — one report
  per episode; pure data: P9 owns low-supply SEVERITY, legacy HandleShop
  owns all buying), `WarpTollReport` (snapshot WARP_STATION Price >
  readable credits — verified comparison shape of Patch.cs:2609 — 15 s
  dwell, cheapest unaffordable toll wins, Price ≤ 0 sentinels never
  trigger), `EconomyUncertain` fail-safe lines. Fail-safe gates: authority
  deny-by-default seam (clients never report; credits are MasterDerived),
  cadence, snapshot staleness (>20 s / future / never-captured /
  !GameStarted), unknown sentinels (Credits −1 / NaN coolant / −1 fuel /
  −1 prices / −1 sector never trigger — UnknownInputPasses counter),
  deny-by-default shop classifier seam (P10 SetRoleNameResolver pattern).
  NO tasks created (no economy capability exists in the P7 catalog; a task
  without CapabilityId metadata fails at start per the P8 executor
  contract — report-only by API-surface necessity, P15 mirror); no
  ReconcileTasks; no RPCs, no credit mutation (audit M10 patterns
  excluded), no CrewPurchaseLimitsEnabled replication, no PLTradeData
  (referenced nowhere — treated as nonexistent), no ShopRepMultiplier
  consumption (private helper, body never verified — base prices only).
  Counter readbacks + bounded deterministic diagnostics (Lines one per
  tracked record, StatusLines = 2). ResetForTests.
- `Core/Economy/EconomyLogBridge.cs` — attaches CapBotLog (ECONOMY, existing
  const) as the director's decision listener at boot (same pattern as the
  other phase bridges).
- `WorldSnapshot.cs` + `PulsarWorldSource.cs` — ADDITIVE P6 capture (P9
  ctor pattern): `ResourceSnapshot.FuelBasePrice` / `CoolantBasePrice`
  (-1 = unknown) filled from `(int)PLServer.GetFuelBasePrice()` /
  `(int)PLServer.GetCoolantBasePrice()` — both compile-proven in shipped
  Patch.cs HandleShop (lines 2220/2234) — per-field try/catch
  RecordPartial, original 5-arg ResourceSnapshot ctor preserved verbatim
  (defaults −1), new 7-arg ctor chains `: this(...)`.
- `docs/ECONOMY_DIRECTOR.md` — full contract: report-only rationale (API-
  surface argument), rules, gates, lifecycle bookkeeping, data flow, the
  additive capture table, authority model, audit honesty note (H4 dead
  slider), performance, verified-API table, failure modes, tests,
  deliberate scope boundaries.
- `tests/EconomyTests.cs` — 102 assertions covering ES01–ES13: credits
  end-to-end (open → low edge → recovery re-arm), delta reports (large ±,
  small suppressed, moderate-delta-crossing-the-band co-fire), store
  dwell/exit/re-entry episodes, affordability episodes (boundary
  credits==price is affordable), unknown-sentinel quiet paths (prices
  unknown; fuel sentinel with coolant still firing), warp-toll
  dwell/sentinels/cheapest-pick, fail-safe inputs (null/stale/not-started/
  future/unknown-credits), authority deny-by-default, vanished + expiry
  hygiene + bounded history (fresh-publish-after-advance discipline),
  classifier seam deny-by-default (null/faulting = never a shop), cadence +
  counters + diagnostics.

### Changed
- `Patch.cs` — WorldTick Postfix extended IN PLACE: after the mission
  block, `EconomyDirector.Evaluate(nowMs)` in its own try/catch
  (`CapBotLog.ECONOMY`). Postfix IL bytes 360 → 395 (expected change; no new
  patch class — the permanent ceiling of 11 is preserved).
- `CapBot.csproj` — +2 Compile entries (`Core\Economy\EconomyDirector.cs`,
  `Core\Economy\EconomyLogBridge.cs`).
- `Mod.cs` — Phase 16 boot block after the mission seams:
  `EconomyLogBridge.Ensure()` + authority/now/world seams + the
  shop-sector classifier wired to the compile-proven ESectorVisualIndication
  shop-class list.
- `tests/run_tests.ps1` — +1 domain compile entry
  (`Core\Economy\EconomyDirector.cs`) and +1 suite (`EconomyTests.cs`,
  last).
- `tests/TaskRecoveryTests.cs` — TestMain runs fifteen suites (`f15` =
  EconomyTests); TOTAL line updated.

### Notes
- Zero NEW unverified PULSAR APIs: the director reads only previously
  verified P6 snapshot sections plus the two additive price captures whose
  API surface was already compile-proven in shipped HandleShop code.
  `GetFuelBasePrice()`/`GetCoolantBasePrice()` are the first P6-capture uses
  of a whitelist API pair not previously captured (audit line 86).
- Report-only by API-surface necessity (identical shape to Phase 15): no
  economy capability exists in the P7 catalog, and the P8 executor rejects
  tasks with unbound CapabilityIds — an economy task would fail at
  execution by construction. Legacy BotEconomy/BotExtractor/HandleShop
  keep exclusive ownership of every economy action.
- Credit deltas are direction + magnitude only — no cause inference (the
  snapshot cannot attribute credits movement).
- Consumers are later phases (Captain Brain 2.0 planning); Phase 16
  implements no consumer beyond the bounded reports.
- Verified: build 0 warnings/0 errors; tests TOTAL 1563/1563 (fifteen
  suites; EconomyTests 102 assertions ES01–ES13); reflection 177 types/
  155 named, EconomyDirector 60 members + EconomyRecord nested type +
  all 15 constants exact (MinRecheckMs=5000, MaxActiveEconomyRecords=8,
  MaxHistory=16, ActiveExpiryMs=60000, MaxStaleSnapshotMs=20000,
  StoreDwellMs=15000, WarpTollDwellMs=15000, ReserveFloor=2500,
  MaxDeltaReportAbs=10000, FuelLowCapsules=2, CoolantLowPercent=30,
  MaxTextLen=120, TrackIdPrefix=ECONOMY:, TrackCredits=CREDITS,
  TargetKindEconomy=ECONOMY), ResourceSnapshot 2 fields + 2 ctors,
  P6–P15 intact, harmony_patch_classes=11, WorldTick Postfix IL 395 bytes
  (expected in-place growth from 360), new namespace CapBot.Core.Economy.

## [Phase 15 — Mission director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Missions/MissionDirector.cs` — the mission-tracking layer (BOUNDED
  DETERMINISTIC REPORT-ONLY): `MissionTrackRecord` rows (TrackId
  "MISSION:<typeId>", FirstSeen/LastSeen/LastProgress stamps, completed/
  total objective counters, UpdateCount, CompletedReported/AbandonedReported/
  StallReported one-shot flags, LatestObjectiveText bounded DATA carry ≤ 120
  chars — never parsed); `MissionDirector` (tracked set ≤ 8 =
  MaxActiveMissions with deterministic oldest-shedding by LastSeenMs
  (tie → lowest key order, `MissionShed` line), history ≤ 16 = MaxHistory,
  absent/terminal records decay after 60 s = ActiveExpiryMs, live present
  missions never expire, stall dwell 120 s = StallReportMs once per episode
  re-armed by progress, decision cadence 5 s). One-shot transition reports:
  MissionOpened (first sighting), MissionProgress (non-completing
  completed-count increase — one bounded report per edge, a completing
  increase emits MissionCompleted instead), MissionCompleted (all objectives
  done or Ended, incl. terminal-at-first-sighting), MissionAbandoned edge,
  MissionVanished (tracked mission absent from a readable list),
  MissionStallReport (120 s no-progress coordination record for later
  phases — never a task), MissionlessReport (edge-triggered, only after the
  session tracked a mission; pristine zero-mission sessions are quiet).
  Same-type-id instances collapse (first sighting wins, extra instances
  counted in SameTypeIdCollisions — audit L2 caveat now documented and
  counted). Fail-safe gates: authority deny-by-default seam (clients never
  report), cadence, snapshot staleness (>20 s / future / never-captured /
  !GameStarted → MissionUncertain line), unknown sentinels (TotalObjectives
  == 0 never completes or stalls). NO tasks created (the P7 catalog has no
  mission capability and the P8 executor rejects unbound CapabilityIds —
  report-only by API-surface necessity); no ReconcileTasks (nothing to
  reconcile); no RPCs, no dialogue interaction, no objective mutation (legacy
  audit C2 patterns excluded). Counter readbacks + bounded deterministic
  diagnostics (Lines ≤ one per tracked mission, StatusLines = 2). The
  WorldSnapshot ctors normalize a null missions list to empty (Bounded
  contract), so the null-section branch is defensive-only and absence rides
  the bounded MissionVanished path (documented capture-failure semantics).
  ResetForTests.
- `Core/Missions/MissionLogBridge.cs` — attaches CapBotLog (MISSION) as the
  director's decision listener at boot (same pattern as the other phase
  bridges).
- `docs/MISSION_DIRECTOR.md` — full contract: report-only rationale (API-
  surface argument), rules, gates, lifecycle bookkeeping, data flow,
  identity caveat, authority model, performance, verified-API table (zero
  new APIs), failure modes, tests, deliberate scope boundaries.
- `tests/MissionTests.cs` — 79 assertions covering MS01–MS13: tracking
  end-to-end (open → progress → completed, single report per edge, bounded
  DATA carry), transition suppression, abandonment edges,
  terminal-at-first-sighting, stall dwell (once per episode, re-armed by
  progress), missionless edge (pristine-session baseline quiet, never spam),
  capture-failure semantics (ctor normalization → absence path), fail-safe
  inputs (null/stale/not-started), authority deny-by-default (null/faulting/
  non-master), vanished reports + expiry hygiene + bounded history, bounded
  tracked set with deterministic shed-oldest, same-type collision counter,
  cadence + counters + diagnostics.

### Changed
- `Patch.cs` — WorldTick Postfix extended IN PLACE: after the navigation
  blocks, `MissionDirector.Evaluate(nowMs)` in its own try/catch
  (`CapBotLog.MISSION`). Postfix IL bytes 325 → 360 (expected change; no new
  patch class — the permanent ceiling of 11 is preserved).
- `CapBot.csproj` — +2 Compile entries (`Core\Missions\MissionDirector.cs`,
  `Core\Missions\MissionLogBridge.cs`).
- `Mod.cs` — Phase 15 boot block after the navigation seams:
  `MissionLogBridge.Ensure()` + authority/now/world seams
  (`ExecutionClaims.IsAuthoritative` / `TaskClock.NowMs` /
  `WorldStateService.Latest`).
- `tests/run_tests.ps1` — +1 domain compile entry
  (`Core\Missions\MissionDirector.cs`) and +1 suite (`MissionTests.cs`,
  last).
- `tests/TaskRecoveryTests.cs` — TestMain runs fourteen suites (`f14` =
  MissionTests); TOTAL line updated.

### Notes
- Zero NEW PULSAR APIs: the director reads only the P6 snapshot missions
  section (PLServer.AllMissions / PLMissionBase.Objectives /
  PLMissionObjective.IsCompleted/ObjectiveText — all previously verified).
  Mission RPCs, dialogue flows, objective writes, mission rewards/decline
  paths, and PLGlobalMission remain UNVERIFIED territory and are not used.
- Report-only by API-surface necessity: no mission capability exists in the
  P7 catalog, and the P8 executor rejects tasks with unbound CapabilityIds —
  a mission task would fail at execution by construction.
- Identity: snapshot missions carry only MissionTypeId (audit L2) — same-type
  concurrent missions are collapsed and the risk is counted, never silent.
- Consumers are later phases (Captain Brain 2.0 planning, Economy Director);
  Phase 15 implements no consumer beyond the bounded reports.
- Verified: build 0 warnings/0 errors; tests TOTAL 1461/1461 (fourteen
  suites; MissionTests 79 assertions MS01–MS13); reflection 172 types/
  151 named, P15 type surfaces + all 8 constants exact (MinRecheckMs=5000,
  MaxActiveMissions=8, MaxHistory=16, ActiveExpiryMs=60000,
  MaxStaleSnapshotMs=20000, StallReportMs=120000, TrackIdPrefix=MISSION:,
  TargetKindMission=MISSION), P6–P14 intact, harmony_patch_classes=11,
  WorldTick Postfix IL 360 bytes (expected in-place growth from 325), new
  namespace CapBot.Core.Missions.

## [Phase 14 — Navigation recovery] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Navigation/NavigationRecovery.cs` — the navigation recovery director
  (BOUNDED DETERMINISTIC COORDINATION ONLY): `NavRule` enum
  (CourseLost=1/GoalReached=2/StuckStall=3); `NavPlanRecord` plan rows
  (PlanId "NAV:<rule>:S<sectorId>", TaskId 0 = report-only,
  TaskResolvedMs −1 = unresolved, LastSeenMs, UpdateCount, HasLiveTask);
  `NavigationRecoveryDirector` (active plans ≤ 8 = MaxActivePlans with
  oldest-by-LastSeenMs shedding (tie → lowest key order, `NavPlanShed`
  line), history ≤ 16 = MaxHistory, un-refreshed plans expire after
  30 s = ActiveExpiryMs, re-arm blocked 20 s = RequeueBlockMs after task
  resolution, dwell hysteresis CourseLost 10 s / GoalReached 15 s, decision
  cadence 5 s = MinRecheckMs, recovery priority 20 — above normal work
  (1..8 + aging), below emergencies (110+)). Rules: CourseLost (no goals +
  not in warp + known sector, persisted ≥ 10 s → ADD_COURSE_GOAL task
  re-affirming the CURRENT sector — never an invented destination),
  GoalReached (first goal == current sector, persisted ≥ 15 s →
  REMOVE_COURSE_GOAL task), StuckStall (vanilla stuck signature: moved
  < 1 m in 5 s while seeking > 7 s WITH an active course → REPORT-ONLY
  plan + one NavStallReport line; a task without CapabilityId metadata
  fails at start per the P8 executor contract, so stalls are data records,
  never tasks). Task recipe: CAPTAIN-owned NAV_RECOVERY task (maxRetries 1,
  timeout 120 s) + metadata NavId/NavRule/Preemptible="true"/CapabilityId/
  Argument + Register + TryQueue through the standard pipeline.
  Fail-safe gates: authority (deny-by-default seam, fail-closed — clients
  never evaluate), cadence, snapshot staleness (>20 s / future /
  never-captured / !GameStarted → NavRecoveryUncertain), unknown rule
  inputs (NaN metrics / −1 sectors / in-warp / missing section never
  trigger). ReconcileTasks resolves plans whose task went terminal or
  vanished (plan re-arms after the requeue block); the task itself is
  never touched (lifecycle/recovery own it). Counter readbacks + bounded
  deterministic diagnostics (Lines ≤ one per active plan, StatusLines = 2).
  ResetForTests.
- `Core/Navigation/NavigationLogBridge.cs` — attaches CapBotLog
  (NAVIGATION) as the director's decision listener at boot (same pattern
  as EmergencyLogBridge/MemoryLogBridge).
- `docs/NAVIGATION_RECOVERY.md` — full contract: rules + dwell windows,
  plan/task shapes, lifecycle bookkeeping, data flow, integration points,
  authority model, performance, verified-API table (ADD_/REMOVE_COURSE_GOAL
  only — no new game APIs), failure modes, tests, deliberate scope
  boundaries.
- `tests/NavigationTests.cs` — 74 assertions covering N01–N12: CourseLost
  end-to-end (task shape, metadata, target = current sector, queued
  through the standard pipeline, duplicate suppression), dwell +
  requeue-block re-arm, GoalReached + negatives (second goal, in-warp,
  unknown sector), StuckStall report-only (no task ever, report once,
  not repeated), plan lifecycle (expire → history, bounded shed at 8 with
  NavPlanShed), fail-safe inputs (null/stale/not-started/NaN), authority
  deny-by-default (null/faulting/non-master), ReconcileTasks (vanished
  task → resolution), no unauthorized execution (task stays Queued, no
  lease, no claim, TryClaim → RejectedNotAuthoritative), pipeline
  isolation under nav churn, cadence + counters + diagnostics.

### Changed
- `Patch.cs` — WorldTick Postfix extended IN PLACE: after the emergency
  blocks, `NavigationRecoveryDirector.Evaluate(nowMs)` +
  `ReconcileTasks(nowMs)`, each in its own try/catch
  (`CapBotLog.NAVIGATION`). Postfix IL bytes 256 → 325 (expected change;
  no new patch class — the permanent ceiling of 11 is preserved).
- `CapBot.csproj` — +2 Compile entries
  (`Core\Navigation\NavigationRecovery.cs`,
  `Core\Navigation\NavigationLogBridge.cs`).
- `Mod.cs` — Phase 14 boot block after the memory seams:
  `NavigationLogBridge.Ensure()` +
  `NavigationRecoveryDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative)`
  + `SetNowMsProvider(TaskClock.NowMs)` +
  `SetWorldProvider(WorldStateService.Latest)`.
- `tests/run_tests.ps1` — +2 domain compile entries
  (`Core\Navigation\NavigationRecovery.cs`) and +1 suite
  (`NavigationTests.cs`, last).
- `tests/TaskRecoveryTests.cs` — TestMain runs thirteen suites (`f13` =
  NavigationTests); TOTAL line updated.

### Notes
- Zero NEW PULSAR APIs: course-goal mutation rides the already-verified P7
  capability channels (ADD_COURSE_GOAL → PLServer.AddCourseGoal(Int32)
  [PunRPC], REMOVE_COURSE_GOAL → PLServer.RemoveCourseGoal(Int32)
  [PunRPC]); the vanilla navigation stack (PLFlightAI/PLBotController/
  PLStarmap/m_ShipCourseGoals) is untouched.
- StuckStall is report-only by the P8 executor contract (a task without
  CapabilityId metadata → FailStarted): vanilla stuck-teleport owns
  physical unsticking; the director coordinates only.
- Recovery = re-affirming the CURRENT sector; the director never invents
  destinations and never replans warp.
- Consumers are later phases (Captain Brain 2.0 planning, mission
  director); Phase 14 implements no consumer beyond the bounded plan
  records.
- Verified: build 0 warnings/0 errors; tests TOTAL 1382/1382 (thirteen
  suites; NavigationTests 74 assertions N01–N12); reflection 167 types/
  147 named, P14 type surfaces + all 15 constants exact (MinRecheckMs=5000,
  MaxActivePlans=8, MaxHistory=16, RecoveryTaskTimeoutMs=120000,
  CourseLostDwellMs=10000, GoalDwellMs=15000, RequeueBlockMs=20000,
  ActiveExpiryMs=30000, MaxStaleSnapshotMs=20000, RecoveryPriority=20,
  TaskTypeRecovery=NAV_RECOVERY, OwnerCaptain=CAPTAIN,
  TargetKindSector=SECTOR, StuckDistMovedMeters=1,
  StuckTimeSeekingSec=7), NavRule values 1/2/3, P6–P13 intact,
  harmony_patch_classes=11, WorldTick Postfix IL 325 bytes (expected
  in-place growth from 256), new namespace CapBot.Core.Navigation.

## [Phase 13 — Crew memory] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Crew/CrewMemory.cs` — the memory layer (BOUNDED RECALLABLE DATA
  ONLY): `CrewMemoryEntry` fact rows (Kind = Location/TaskOutcome/CrewEvent,
  Text/Outcome payloads ≤32 chars, CreatedTimeMs/LastSeenMs stamps,
  UpdateCount) in per-agent rings; `CrewMemorySystem` (≤32 = MaxAgents
  agents, ≤8 = MaxMemoriesPerAgent entries per agent, deterministic upsert
  semantics — same key updates in place, new distinct fact evicts the
  oldest entry by LastSeenMs with tie → lowest insertion index; bounded
  registry refusal for NEW agents only; recall paths
  `Recall`/`RecallTaskOutcome`/`RecallAll` that stamp reads via the
  `SetNowMsProvider` clock seam (production: TaskClock.NowMs);
  `ForgetAgent` lifecycle hook; `StatsOf` + bounded diagnostics; listener
  lines collected under the lock and fired AFTER release; ResetForTests).
  No game references, no tick driver, no world reads, no
  scheduler/claims/executor/personality/experience influence.
- `Core/Crew/MemoryLogBridge.cs` — attaches CapBotLog (CREW) as the memory
  system's decision listener at boot (same pattern as
  CrewAgentLogBridge/PersonalityLogBridge/ExperienceLogBridge).
- `docs/CREW_MEMORY.md` — full contract: entry shape, write paths, upsert +
  eviction semantics, recall paths, registry rules, authority, performance,
  verified-API table (none used), security, future integration points,
  failure modes, tests.
- `tests/MemoryTests.cs` — 170 assertions covering the Phase 13 scenarios
  (M01–M12): end-to-end outcome memory through the real Sync funnel,
  location upsert + read stamping, crew-event text as DATA, bounded ring
  (8/agent, cross-kind oldest-by-LastSeenMs eviction, updates never evict),
  bounded registry (32-agent cap, refusal, slot freeing, existing-agent
  writes at cap), invalid-input refusal, no cross-agent contamination,
  scheduler/claims/priority/personality/experience isolation under churn,
  recall stamping via the clock seam + recall-favored eviction, fail-safe
  funnel (throwing listener and full-registry refusal leave agent state and
  task resolution untouched), ForgetAgent lifecycle + no resurrection, and
  upsert determinism (keys, kinds, later-outcome wins, stats).

### Changed
- `Core/Crew/CrewAgentRegistry.cs` — ClearTask extended additively: after
  the agent lock is released (next to the Phase 12 experience hook), a
  fail-safe memory write (`CrewMemorySystem.RememberTaskOutcome`) runs in
  its own try/catch — a faulting memory layer can never affect agent state
  or task resolution. Agent state shape and P10 semantics unchanged.
- `CapBot.csproj` — +2 Compile entries (`Core\Crew\CrewMemory.cs`,
  `Core\Crew\MemoryLogBridge.cs`).
- `Mod.cs` — Phase 13 boot block: `MemoryLogBridge.Ensure()` +
  `CrewMemorySystem.SetNowMsProvider(TaskClock.NowMs)`.
- `tests/run_tests.ps1` — +1 domain compile entry (`CrewMemory.cs`) and
  +1 suite (`MemoryTests.cs`).
- `tests/TaskRecoveryTests.cs` — TestMain runs twelve suites (`f12` =
  MemoryTests); TOTAL line updated.

### Notes
- Zero PULSAR APIs used (pure C# over the Phase 10 ClearTask funnel +
  explicit APIs); no Harmony patch change (still 11 patch classes; WorldTick
  Postfix 256 IL bytes unchanged).
- The P6 snapshot remains the only authoritative world observation —
  location memory is an explicit-API cache, no snapshot-path changes.
- Consumers are later phases (role preferences, planning, Captain Brain
  2.0, navigation recovery); Phase 13 implements no decision consumer.
- Verified: build 0 warnings/0 errors; tests TOTAL 1308/1308 (twelve
  suites; MemoryTests 170 assertions M01–M12); reflection 161 types/142
  named, P13 type surfaces + constants (32/8/32) exact, P6–P12 intact.

## [Phase 12 — Crew experience] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Crew/CrewExperience.cs` — the experience layer (DATA ONLY): bounded
  `CrewExperienceRecord` per agent (per-outcome counters, ExperiencePoints,
  deterministic Level 1..10 via fixed cumulative thresholds 0/50/120/220/350/
  510/710/950/1230/1550, LastOutcome/LastResultMs stamps, UpdateCount);
  `CrewExperienceRegistry` (≤32 = MaxRecords, lazy record creation on first
  accrued outcome, existing records keep accruing at cap, Remove lifecycle
  hook, LevelOf/Get lookups that never fabricate, bounded diagnostics,
  listener outside the lock, ResetForTests); points vocabulary = Phase 10
  static outcomes (COMPLETED=10, others=2, unknown refused −1). No game
  references, no tick driver, no world reads, no scheduler/claims/executor/
  personality influence.
- `Core/Crew/ExperienceLogBridge.cs` — attaches CapBotLog (CREW) as the
  experience registry's decision listener at boot (same pattern as
  CrewAgentLogBridge/PersonalityLogBridge).
- `docs/CREW_EXPERIENCE.md` — full contract: funnel, points/levels, registry
  rules, authority, performance, verified-API table (none used), security,
  future integration points, failure modes, tests.
- `tests/ExperienceTests.cs` — 108 assertions covering the Phase 12
  scenarios (X01–X12): end-to-end accrual through the real Sync funnel,
  deterministic level math and level crossing, points vocabulary with
  unknown-outcome refusal, per-outcome counters, registry stability +
  remove lifecycle, no cross-agent contamination, bounded registry (cap
  refusal, accrual-at-cap, slot freeing), invalid-input refusal,
  scheduler/claims/priority/personality isolation under churn, fail-safe
  funnel (throwing listener and full-registry refusal leave agent state and
  task resolution untouched), and ClearTask funnel regression with
  exactly-once accrual.

### Changed
- `Core/Crew/CrewAgentRegistry.cs` — ClearTask extended additively: after
  the agent lock is released, a fail-safe experience accrual
  (`CrewExperienceRegistry.RecordOutcome`) runs in try/catch — a faulting
  experience layer can never affect agent state or task resolution; accrual
  happens only when the clear actually happened (exactly-once). Agent state
  shape and P10 semantics unchanged.
- `CapBot.csproj` — +2 Compile entries (`Core\Crew\CrewExperience.cs`,
  `Core\Crew\ExperienceLogBridge.cs`).
- `Mod.cs` — Phase 12 boot block: `ExperienceLogBridge.Ensure()` only.
- `tests/run_tests.ps1` — compiles the experience domain file and
  `tests\ExperienceTests.cs` (eleven suites).
- `tests/TaskRecoveryTests.cs` — TestMain runs `ExperienceTests.Run()` as
  f11; TOTAL aggregates eleven suites.

### Notes
- Zero PULSAR APIs used (pure C# over the Phase 10 funnel).
- No new Harmony patch and no change to the WorldTick postfix (verified
  byte-identical, 256 IL bytes; 11 patch classes unchanged).
- No in-game behavior change beyond the data accrual itself: experience is
  read by nothing yet; scheduling, claims, priorities, task state, and
  personality records are proven unchanged under experience churn (test X10).
- Phase 25 (adaptive learning) owns trait adjustment; `SetPersonality`
  remains the only personality write path. Phase 28 (persistence) may
  serialize the bounded counters.

## [Phase 11 — Crew personalities] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Crew/CrewPersonality.cs` — the personality layer (DATA ONLY): five
  fixed traits (`Discipline/Boldness/Sociability/Diligence/Adaptability`,
  ints 0..100, closed vocabulary), immutable `CrewPersonality` records keyed
  by the Phase 10 stable AgentId, `PersonalityFactory` with three sources —
  identity-derived (deterministic FNV-1a over "P|<agentId>|<TRAIT-SALT>",
  byte-scaled to 0..100: same crew member always gets the same personality
  across rejoins/class changes/rounds, no randomness, no wall clock),
  explicit clamped values (future-phase hook), and neutral all-50;
  `PersonalityArchetypes` static vocabulary (SENTINEL/VANGUARD/COORDINATOR/
  TECHNICIAN/ADAPTER/BALANCED, dominant ≥70, ties → first trait in enum
  order); `RoleAffinity` deterministic 0..100 per-role scores from fixed
  weight tables summing to 100 (Unknown/Other uniform; null ⇒ −1; tables
  returned as defensive copies); `CrewPersonalityRegistry` bounded ≤32
  (= MaxAgents) with identity-integrity writes (record.AgentId must equal
  key), derive-or-existing `DeriveFor` (never silent replacement), explicit
  replace counted, Remove lifecycle hook, derive-on-demand affinity/archetype
  conveniences, bounded diagnostics, listener fired outside the lock,
  ResetForTests. No game references, no tick driver, no world reads, no
  task/claim/scheduler/executor influence.
- `Core/Crew/PersonalityLogBridge.cs` — attaches CapBotLog (CREW) as the
  registry's decision listener at boot (same pattern as CrewAgentLogBridge).
- `docs/CREW_PERSONALITIES.md` — full contract: what a personality is/is not,
  deterministic derivation, archetypes, role affinity, registry rules,
  authority/multiplayer, performance, verified-API table (none used),
  security, future integration points, failure modes, tests.
- `tests/PersonalityTests.cs` — 116 assertions covering the Phase 11
  scenarios (P01–P12): deterministic identity-derived personalities and
  distinctness across agents, archetype tokens (incl. threshold tie-first),
  role-differentiated affinity with bounded scores and immutable weight
  tables, clamping and invalid-input refusal, explicit assign/replace
  counting, neutral + remove lifecycle, registry stability across time,
  no cross-agent contamination (bot/human distinctness), scheduler/claims
  isolation under personality churn, no cross-round drift after reset,
  bounded registry (cap refusal, replacement-at-cap, slot freeing), and
  no invalid-data ingestion (forged archetypes, stolen records).

### Changed
- `CapBot.csproj` — +2 Compile entries (`Core\Crew\CrewPersonality.cs`,
  `Core\Crew\PersonalityLogBridge.cs`).
- `Mod.cs` — Phase 11 boot block: `PersonalityLogBridge.Ensure()` only
  (the layer is inert data; consumers are later phases).
- `tests/run_tests.ps1` — compiles the personality domain file and
  `tests\PersonalityTests.cs` (ten suites; TOTAL gate unchanged).
- `tests/TaskRecoveryTests.cs` — TestMain runs `PersonalityTests.Run()` as
  f10; TOTAL aggregates ten suites.

### Notes
- Zero PULSAR APIs used by the personality layer (pure C# over the P10 agent
  identity). The research doc's PLAIIO/AIData profile channel was deliberately
  NOT used — it would touch the vanilla AI brain-swap surface and vanilla
  save files (out of scope for an additive bounded data layer).
- No new Harmony patch and no change to the WorldTick postfix (verified
  byte-identical, 256 IL bytes; 11 patch classes unchanged).
- No in-game behavior change: personalities are derived on demand and read
  by nothing yet; scheduler grants, owner-busy gating, claims, and task
  priorities are proven unchanged under personality churn (test P09).
- Phase 12 (experience) is NOT implemented here: traits are static derived
  values; `SetPersonality` exists only as the future write path.

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