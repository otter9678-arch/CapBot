# Phase 15: Mission Director — Contract

**Status: IMPLEMENTED (Alpha 1.2.2), P52-extended.** Bounded deterministic
mission-tracking layer over the P2–P14 contracts. It is **not** a mission
runner, **not** a dialogue bot, and **creates NO tasks**: Phase 15 is
deliberately REPORT-ONLY — it observes the P6 missions snapshot and emits
bounded transition reports that later phases (Captain Brain 2.0 planning,
Economy Director) can consume as data.

**P52 extension (master prompt §5–§13):** stable per-instance identity +
17-state lifecycle FSM + ONE authoritative return decision. See §13 below.

Files: `CapBot/Core/Missions/MissionDirector.cs` (MissionTrackRecord,
MissionDirector), `CapBot/Core/Missions/MissionLifecycle.cs` (the FSM),
`CapBot/Core/Missions/MissionReturnPolicy.cs` (the return decision),
`MissionCommand.cs` (`/capbotmission`), `MissionLogBridge.cs` (logging
bridge). World input rides the Phase 6 snapshot (no separate capture path).

---

## 1. Why report-only (the API-surface argument)

- The readable mission surface is `MissionTypeId, Ended, Abandoned,
  TotalObjectives, CompletedObjectives, FirstIncompleteObjectiveText` (P6
  snapshot, VERIFIED). Objective *types* are not readable at runtime
  (`WorldSnapshot.cs` P6 build comment), and rewards / decline paths /
  giver-station registries / `PLGlobalMission` are UNVERIFIED (no such API is
  documented anywhere in the repo — treat as nonexistent).
- The P7 capability catalog contains **no mission capability** (deliberately
  excluded in Phase 7), and the P8 executor **rejects** any task whose
  `CapabilityId` has no dispatch branch. So there is no verified,
  executor-dispatchable action a mission plan could request — a director task
  would fail at execution by construction.
- Mission accept/decline dialogue flows (`SelectChoice`, `AllAvailableChoices`
  walking), `AttemptStartMissionOfTypeID` / `AttemptForceEndMissionOfTypeID`
  RPCs, and objective-progress manipulation (`AttemptCompleteObjective`,
  direct `AmountCompleted` writes — legacy audit finding C2) are all OUT OF
  SCOPE. Those need verified capabilities + dispatcher branches in a later
  phase before any task could exist; this director never calls them.

## 2. What the director does

One bounded deterministic evaluation pass per `MinRecheckMs` (5 s), driven
from the WorldTick Postfix after the navigation blocks (host-only):

- **Tracks** up to `MaxActiveMissions` (8) live missions by `MissionTypeId`
  (`MISSION:<typeId>` keys), with deterministic oldest-shedding (LastSeenMs,
  tie → lowest key order, `MissionShed` line) and bounded history (16).
- **One-shot transition reports:** `MissionOpened` (first sighting),
  `MissionProgress` (completed-count increase that does NOT complete the
  mission), `MissionCompleted` (all objectives done or `Ended` — including
  terminal-at-first-sighting), `MissionAbandoned` (`Abandoned` edge),
  `MissionVanished` (tracked mission absent from a readable list).
  Exactly one report per edge; terminal records never re-report.
- **`MissionStallReport`** — no completed-objective progress for
  `StallReportMs` (120 s): once per stall episode, re-armed by the next
  progress edge. A coordination record for later phases, never a task.
- **`MissionlessReport`** — edge-triggered: only after the session tracked at
  least one mission (a pristine zero-mission session is the normal state,
  not an event), and only when the missions list is READABLE (empty list).
- Objective text is carried as bounded DATA (≤ 120 chars, `LatestObjectiveText`)
  and never parsed as behavior (house security rule).

## 3. Gates (fail-safe, mirroring P9/P14)

| Gate | Behavior |
|------|----------|
| Authority | deny-by-default seam; null/faulting/non-master ⇒ no-op (clients never report) |
| Cadence | one pass per `MinRecheckMs` (5 s); gated passes not counted |
| Snapshot | null / never-captured / stale >20 s / future-dated / `!GameStarted` ⇒ `MissionUncertain …` line, no decisions |
| Missions section | constructors normalize null → empty (Bounded contract), so a capture failure is indistinguishable from an empty list downstream; the director's null check is defensive-only (unreachable today) and absence is handled by the bounded MissionVanished path |
| Rule inputs | unknown sentinels: `TotalObjectives == 0` (no readable objectives) never completes or stalls a mission |

## 4. Lifecycle bookkeeping (bounded)

- Live present missions never expire (records refresh every pass). Records
  whose mission vanished, and terminal records that stopped refreshing, decay
  after `ActiveExpiryMs` (60 s) into bounded FIFO history (16) — terminal
  records that linger free their tracked slots without new reports.
- Absence expiry emits one bounded `MissionVanished` line for a record that
  was still live; terminal records just decay (`MissionExpired` diagnostic).
- Counters: `Evaluations, MissionsTracked, ProgressReports, CompletedReports,
  AbandonedReports, VanishedReports, StallReports, MissionlessReports,
  DuplicatesSuppressed, PlansExpired, StaleRejections, SameTypeIdCollisions,
  LastUncertainReason`; diagnostics `Lines()` (≤ one per tracked mission,
  deterministic order) + `StatusLines()` (2 lines).
- No tasks: no `CapBotTask` creation, no `TaskRegistry` writes, no
  scheduler/claim/executor interaction, and therefore no `ReconcileTasks`
  (nothing to reconcile — documented).

## 5. Data flow

```
WorldStateService.Latest (P6 snapshot, 1 Hz refresh)
  -> authority gate (deny-by-default; clients never evaluate)
  -> cadence gate (one pass per MinRecheckMs = 5 s)
  -> snapshot fail-safe gate (stale >20 s / future / never-captured / !GameStarted)
  -> present-map (typeId-deduped; SameTypeIdCollisions counted — audit L2 caveat)
  -> hygiene (absent / terminal-decayed records -> bounded history)
  -> per-mission rules (open / progress / completed / abandoned / stall / missionless)
  -> bounded diagnostic lines via MissionLogBridge (CapBotLog.MISSION)
```

## 6. Identity caveat (audit L2, documented)

`MissionSnapshot` carries only `MissionTypeId`, so two concurrent same-type
missions are indistinguishable in snapshot data. The director collapses
same-type instances (first sighting wins, extra instances counted in
`SameTypeIdCollisions`); progress aggregates across instances. This is the
same limitation legacy `Learning.PollMissions` had (CAPBOT_AUDIT.md L2) —
now documented and counted instead of silent.

## 7. Multiplayer authority model

- The WorldTick driver gates on `PhotonNetwork.isMasterClient`; the director
  additionally consults the authority seam (`ExecutionClaims.IsAuthoritative()`),
  fail-closed. Clients produce no mission reports.
- No Photon actions of any kind: the director never RPCs, never starts/ends
  missions, never touches dialogue. Pure snapshot reads + log lines.

## 8. Performance contract

- Cadence-gated 5 s; reuses the P6 snapshot (no extra game queries, no
  FindObjectsOfType, no scene scans, no LINQ); every collection bounded
  (tracked ≤ 8, history ≤ 16, pending lines ≤ 4); no per-frame work.

## 9. Verified APIs used (zero new)

| Surface | Status |
|---------|--------|
| P6 snapshot missions section (MissionTypeId/Ended/Abandoned/TotalObjectives/CompletedObjectives/FirstIncompleteObjectiveText) | VERIFIED (P6 source reads `PLServer.AllMissions` / `PLMissionBase.Objectives` / `PLMissionObjective.IsCompleted/ObjectiveText`) |
| `WorldSnapshot` Bounded(null)→empty normalization | VERIFIED (WorldSnapshot.cs Bounded contract — capture-failure semantics documented) |
| Mission RPCs / dialogue flows / objective writes | NOT USED (out of scope §1) |
| PLGlobalMission, mission rewards, decline path | NOT VERIFIED ANYWHERE — treated as nonexistent |

## 10. Failure modes (all fail-safe)

| Situation | Behavior |
|-----------|----------|
| No snapshot / never captured / stale / future / not started | `MissionUncertain` logged, nothing reported |
| Missions section capture failure | indistinguishable from empty list (ctor normalization); tracked missions get one-shot MissionVanished |
| Same-type concurrent missions | collapsed; `SameTypeIdCollisions` counts the risk |
| Tracked set full | oldest shed (`MissionShed`), newest retained |
| Persisting live mission | never expires; quiet passes (duplicates suppressed) |
| Mission ends/abandons | exactly ONE terminal report; record decays after 60 s |

## 11. Test coverage

`tests/MissionTests.cs` — 79 checks covering MS01–MS13: tracking end-to-end
(open → progress → completed, single report per edge, DATA carry), transition
suppression, abandonment edges, terminal-at-first-sighting, stall dwell
(once per episode, re-armed by progress), missionless edge (pristine session
baseline quiet, never spam), capture-failure semantics (ctor normalization
→ absence path), fail-safe inputs (null/stale/not-started), authority
deny-by-default, vanished reports + expiry hygiene + bounded history,
bounded tracked set with deterministic shed-oldest, same-type collision
counter, cadence + counters + diagnostics. Full suite: **1461/1461 pass**
(1382 prior + 79 new). The f14 hook runs after f13; run_tests.ps1 compiles
the mission domain file + test file with the rest.

## 12. Deliberate scope boundaries (documented)

- No task creation of any kind (§1).
- No dialogue interaction, no mission start/end RPCs, no objective mutation.
- No objective-type inference at runtime (types are not readable on
  PLMissionObjective instances; the decompiled ObjType 1–20 switch is
  research knowledge only).
- No new Harmony patch class (permanent ceiling of 11 preserved; WorldTick
  Postfix extended in place — IL 325 → 360 bytes).

## 13. P52 extension — stable identity, lifecycle FSM, return decision

### 13.1 Stable per-instance identity (closes §6 identity caveat for identified missions)

probe9 verified `MissionData.MissionID` — a stable per-instance int on the
mission's `MyMissionData`. The P6 capture now reads it, and the director's
present map keys on it when captured (>= 0):

- Identified missions track as `MISSION:I<missionId>` — two concurrent
  same-type instances are now SEPARATE records (the audit L2 ambiguity no
  longer applies to them). `SameTypeIdCollisions` remains meaningful for
  legacy-keyed missions only.
- Identity-less captures (MissionId < 0) keep the EXACT Phase 15 key
  `MISSION:<typeId>` — every existing consumer/test key keeps working.

### 13.2 The 17-state lifecycle FSM (`MissionLifecycle`)

A pure, deterministic domain (no game types, no clock reads, no tasks) that
folds every snapshot into a bounded `MissionLifecycleRecord` per mission:

- The master prompt's 17 explicit states (MISSION_NONE → MISSION_DETECTED →
  MISSION_TARGET_SELECTED → … → MISSION_TEMP_UNAVAILABLE), one
  deterministic transition rule (`Apply`), out-of-range state text falls
  back to "MISSION_UNKNOWN" (never parsed — static vocabulary only).
- Gates mirror the director: deny-by-default authority seam, 5 s cadence
  (first eval always runs), stale/never-captured/not-started fail-safe.
- **No-stuck contract (§5):** vanished missions emit one
  `MissionTempUnavailable <trackId>` report (survive via TEMP_UNAVAILABLE,
  never DEAD — §12) and decay after `ActiveExpiryMs` (60 s) so every state
  has a bounded exit; a present mission re-tracks cleanly (TEMP_UNAVAILABLE
  → DETECTED via fresh sighting).
- **Authoritative terminals (§6):** game flags decide — `GameFailed` (or the
  snapshot's abandoned mirror) → MISSION_FAILED; game-active flip with all
  objectives complete → MISSION_RETURNED; all-done/Ended → MISSION_COMPLETE.
- Return-required edge: when a completed mission satisfies the return
  policy, ONE `MissionReturnRequired <trackId> done=N/N` report fires per
  record before the terminal report.
- Counters + readbacks: `TrackedCount`, transition/return/terminal/
  temp-unavailable/stale counters, `GetTrack(trackId)`, deterministic
  `Lines()`/`StatusLines()`; `/capbotmission` shows both the director and
  FSM views. Wired from the WorldTick Postfix after MissionDirector.

### 13.3 ONE authoritative return decision (`MissionReturnPolicy`)

Master prompt §9: exactly ONE return decision exists. The legacy
`Patch.cs MissionShouldReturnToSender` table is ported verbatim as pure
data (no game types, never throws):

- Listed types — legacy-parity EXACT: type 0 → 3 completed objectives;
  25/68/71/72/780/2437/2580/104851 → 2; 69/264/683/81262/24213/24214/25249
  → 1. The table verdict can NEVER be weakened by the game's turn-in flag.
- Unlisted types — additive path (probe8-verified
  `PLServer.IsMissionWithIDReadyToTurnIn`): return when the game confirms
  turn-in readiness AND all objectives are complete; unknown sentinels
  (`GameReadyTurnInKnown=false`) never trigger.
- The legacy consumer at Patch.cs:2769 (`HasActiveMissionInCurrentSector`)
  is preserved untouched.
- Verdicts are published as DATA (`MissionReturnSignal`, static vocabulary
  `MISSION_RETURN_REQUIRED`) — consumers read the policy or the snapshot;
  nothing re-implements the table.

### 13.4 Per-objective capture (`MissionObjectiveSnapshot`)

probe9 verified the objective subtype surface; the capture reads each
objective's real fields: TALK_TO_NPC (ActorTypeID), ENTER_VOLUME
(VolumeName), REACH_SECTOR (SectorToReach), PICKUP_ITEM (ItemType+SubType),
PICKUP_COMPONENT (SlotType+SubType), KILL_ENEMY (EnemyNameFromType),
WITHIN_JUMP_COUNT, plus UNKNOWN fallback carrying ObjectiveText. Amounts
via GetAmt()/GetAmtNeeded(); bounded at 12 per mission. Static vocabulary
only — never parsed as behavior.

### 13.5 Test coverage

`tests/MissionLifecycleTests.cs` — suite f39, 69 checks (ML01–ML14):
detection pipeline with stable-id keys, complete→return-required→terminal,
legacy type-key fallback, failed/returned terminals, TEMP_UNAVAILABLE
survival, no-stuck decay, return-policy table verbatim + additive path +
flag-cannot-weaken, deny-by-default authority, fail-safe inputs, bounded
tracked set, cadence determinism (first eval always runs). Full suite:
**3474/3474 pass ×3 stable** (39 suites).