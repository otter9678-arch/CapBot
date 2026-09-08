# Phase 17: Combat Director — Contract

**Status: IMPLEMENTED (Alpha 1.2.2).** Bounded deterministic combat-tracking
layer over the P2–P16 contracts. It is **not** a fighter, **not** a targeter,
and **creates NO tasks**: Phase 17 is REPORT-ONLY **by mandate, not by
absence** — unlike Phase 16 (where no economy capability existed at all), the
P7 catalog *does* contain one combat-adjacent capability (`SET_CAPTAIN_TARGET`,
executor-dispatchable via `PulsarCapabilityDispatcher.DispatchSetCaptainTarget`),
but its authorship is owned by the Phase 9 emergency director (the
`DangerousCombat` emergency is the only in-tree author of that capability) and
by the legacy captain tick (`ComputeDesiredOrder` / `BoardEnemy` / blind-jump
own targets, orders, `ClaimShip`, `AlertLevel`, `AddHostileShip`, and the
blind jump). Phase 17 produces the combat-side **coordination record** from
the P6 snapshot; it never fires, never sets targets, never assigns severity.

Files: `CapBot/Core/Combat/CombatDirector.cs` (CombatRecord, CombatDirector),
`CombatLogBridge.cs` (logging bridge). Additive P6 capture (P9-ctor pattern):
`ShipSnapshot.TookDamageRecently`, `ThreatSnapshot.InvadersOnboardCount`.

---

## 1. Why report-only by mandate (the ownership argument)

- `SET_CAPTAIN_TARGET` exists (`RegisteredCapabilities.cs`, MasterOnly,
  `Captain_SetTargetShip` [PunRPC] — DLL-verified) and the P8 executor
  dispatches it. Every in-tree author of it is elsewhere:
  - **P9** `EmergencyDirector.DangerousCombat` (the only in-tree emergency
    that authors `SET_CAPTAIN_TARGET` tasks) owns combat **severity** and
    emergency target authorship.
  - **Legacy** `ComputeDesiredOrder` (attack order 4 / board-enemy order 6),
    `BoardEnemy` (`ClaimShip` RPC), alert-level writes, `AddHostileShip`
    enrichment, and the blind-jump flee own all combat mutations.
- A P17 report is therefore DATA ONLY: no tasks, no task metadata, no
  registry writes, no scheduler/claim/executor interaction, no RPCs.
- No new combat capability is registered; the P7 catalog stays at 7 built-ins.

## 2. What the director does

One bounded deterministic evaluation pass per `MinRecheckMs` (5 s), driven
from the WorldTick Postfix after the economy block (host-only):

- **ENGAGEMENT episode** (single `COMBAT:ENGAGEMENT` record):
  - **`CombatOpened`** — first pass with ≥ 1 authoritative hostile
    (`ThreatSnapshot.KnownHostileShipIds` — the ship's own `HostileShips`
    list; QualityImprover-safe: the list is read, hostility logic is never
    called). Immediate (no dwell): P9 owns immediate severity; this line only
    documents the episode.
  - **`HostileEngagementReport`** — after `EngagementDwellMs` (15 s) of a
    **stable** hostile picture (composition = the hostile count the episode
    opened/reported with; any change re-arms with a fresh dwell clock — the
    hostile list can flicker during combat). Carries hostile count, our/target
    combat levels (INFERRED semantics, data-only), the gap label, and the
    player-ship hull fraction. One per episode.
  - **`HostileClearedReport`** — hostiles drop to zero: one-shot episode
    close; the tracked record survives (hygiene owns expiry) and re-entry
    re-opens the episode (fresh dwell; `CombatOpened` re-emits only after the
    record itself decays — it is record-creation vocabulary).
  - **`CombatVanished`** — the record decays when the hostile set stays empty
    past `ActiveExpiryMs` (60 s), into bounded FIFO history (16). Same
    discipline as P15/P16: a LIVE engagement record never expires.
- **`WarpEngagementReport`** — hostiles present while the player ship is in
  warp (`Navigation.InWarp`), under the same stability window, one per
  episode. Pure data picture — vanilla/legacy own all warp/escape behavior.
- **`UnderFireReport`** — the player ship `TookDamageRecently` (Phase 17
  additive capture of the shipped Patch.cs:242 "took damage recently" window)
  while hostiles are present. One per episode, re-armed when the condition
  clears. Pure data — P9 owns fire/hull SEVERITY.
- **`BoarderReport`** — `InvadersOnboardCount > 0` (Phase 17 additive capture:
  `PLShipInfoBase.InvadersOnboard`, DLL reflection-verified `System.Int32`
  property). Independent of hostiles (boarders can arrive without a hostile
  ship being tracked). One per episode, re-armed when boarders clear. Data
  only — vanilla repel + legacy order-6 own the response.
- **`CombatUncertain`** — fail-safe gate lines (stale / never-captured /
  not-started / future snapshots).
- **`CombatShed`** — tracked-set overflow (defensive; the single-record
  contract never reaches it — kept for house-pattern parity).

## 3. Gates (fail-safe, mirroring P9/P14/P15/P16)

| Gate | Behavior |
|------|----------|
| Authority | deny-by-default seam; null/faulting/non-master ⇒ no-op (clients never report; the hostile list and combat levels are ship-synced master-authoritative values) |
| Cadence | one pass per `MinRecheckMs` (5 s); gated passes not counted |
| Snapshot | null / never-captured / stale >20 s / future-dated / `!GameStarted` ⇒ `CombatUncertain …` line, no decisions |
| Unknown sentinels | NaN combat levels → `-` in the engagement line (never a trigger); `InvadersOnboardCount == -1` never triggers a boarder report (counted in `UnknownInputPasses`); `TookDamageRecently == false` covers both "not recently damaged" and "capture unknown" (fail-safe: false never triggers) |
| Zero hostiles | a readable zero-hostile pass is a legitimate quiet pass (NOT an unknown); it closes episodes and feeds hygiene |

## 4. Lifecycle bookkeeping (bounded)

- The engagement record refreshes every readable pass with hostiles and never
  expires while they remain. When hostiles clear and stay absent past
  `ActiveExpiryMs` (60 s), the record decays into bounded FIFO history (16)
  with a one-shot `CombatVanished` report.
- Counters: `Evaluations, RecordsTracked, OpenedReports, EngagementReports,
  WarpPictureReports, UnderFireReports, BoarderReports, ClearedReports,
  VanishedReports, PlansExpired, StaleRejections, UnknownInputPasses,
  LastUncertainReason`; diagnostics `Lines()` (one per tracked record,
  deterministic order) + `StatusLines()` (2 lines, `combat=` prefix,
  `uncertain=` in the second).
- No tasks: no `CapBotTask` creation, no `TaskRegistry` writes, no
  scheduler/claim/executor interaction, and no `ReconcileTasks` (nothing to
  reconcile — documented).

## 5. Data flow

```
WorldStateService.Latest (P6 snapshot, 1 Hz refresh; Phase 17 +combat fields)
  -> authority gate (deny-by-default; clients never evaluate)
  -> cadence gate (one pass per MinRecheckMs = 5 s)
  -> snapshot fail-safe gate (stale >20 s / future / never-captured / !GameStarted)
  -> engagement episode (open edge -> stable-picture dwell report -> cleared edge)
  -> warp-combat picture (hostiles + Navigation.InWarp, same stability window)
  -> under-fire rule (ShipSnapshot.TookDamageRecently while hostiles present)
  -> boarder rule (ThreatSnapshot.InvadersOnboardCount > 0, independent of hostiles)
  -> hygiene (hostile set empty past 60 s -> bounded history + CombatVanished)
  -> bounded diagnostic lines via CombatLogBridge (CapBotLog.COMBAT)
```

## 6. The additive P6 capture (Phase 17 snapshot change)

Two fields via the P9 additive-ctor pattern (original ctors preserved
verbatim; new ctors chain `: this(...)`):

| Field | Capture | Status |
|-------|---------|--------|
| `ShipSnapshot.TookDamageRecently` | `UnityEngine.Time.time - ship.LastTookDamageTime() < 10f` | VERIFIED (compile-proven shipped Patch.cs:242 same window; `LastTookDamageTime()` on audit whitelist) |
| `ThreatSnapshot.InvadersOnboardCount` | `playerShip.InvadersOnboard` | VERIFIED (DLL reflection: public property returning `System.Int32` on `PLShipInfoBase`; documented DLL-verified in docs/EMERGENCY.md §165) |

Both captures are per-field try/catch (`RecordPartial`); any fault leaves
false / −1 and the under-fire / boarder rules stay silent. Capture rides the
existing ship/threat builders (no new capture pass, no FindObjectsOfType).

## 7. Multiplayer authority model

- The WorldTick driver gates on `PhotonNetwork.isMasterClient`; the director
  additionally consults the authority seam (`ExecutionClaims.IsAuthoritative()`),
  fail-closed. Clients produce no combat reports.
- No Photon actions of any kind: the director never RPCs, never targets,
  never fires. Pure snapshot reads + log lines.

## 8. Audit honesty notes (H4 — the dead combat sliders)

The legacy `AIReactionSpeed`, `AIAccuracy`, `CombatEngageRange`, and
`CombatDisengageHealth` config sliders are DEAD (nothing reads them —
grep-verified in CAPBOT_AUDIT.md H4; README misdocuments them as functional).
The legacy blind-jump flee logic hardcodes a hull floor of `0.2f` and a 60 s
cooldown (Patch.cs:431). This director documents those legacy constants as
data-only thresholds (`LegacyBlindJumpHullFraction = 0.2f`,
`LegacyBlindJumpCooldownSec = 60`, `HostileSevereCount = 3` mirroring P9's
`FireSevereCount`, `CombatLevelGapUnfavorable = 1.33f` mirroring P9's
INFERRED threat-readout const) and does **NOT** wire any slider. Config
wiring is a separate later-phase concern and would silently change legacy
behavior. Engagement-range reporting is impossible from the snapshot (no
compile-proven `PLShipInfoBase.Position` read exists in the repo) — range
logic is out of scope.

## 9. Performance contract

- Cadence-gated 5 s; reuses the P6 snapshot (no extra game queries, no
  FindObjectsOfType, no scene scans, no LINQ); every collection bounded
  (tracked ≤ 8, history ≤ 16, pending lines ≤ 4); no per-frame work. The
  player-ship join is a bounded ≤ 24-element pass over the snapshot's ships
  list (breaks on first player-ship match).

## 10. Verified APIs used

| Surface | Status |
|---------|--------|
| P6 snapshot threat/navigation/ships sections (KnownHostileShipIds / OurCombatLevel / PlayerTargetCombatLevel / InWarp / HullFraction) | VERIFIED (P6, compile-proven) |
| `PLShipInfoBase.LastTookDamageTime()` + `Time.time` 10 s window (new capture) | VERIFIED (shipped Patch.cs:242) |
| `PLShipInfoBase.InvadersOnboard` (new capture) | VERIFIED (DLL reflection: Int32 property; docs/EMERGENCY.md §165) |
| `HostileShips` list read (never hostility logic) | VERIFIED (house rule — QualityImprover replaces `ShouldBeHostileToShip`; the list is the game's own outcome) |
| Combat-level INFERRED semantics | INFERRED, data-only (research §6.6; P9 `CombatLevelGapUnfavorable` precedent) |
| Weapon/fire/turret/missile RPCs, target assignment, `AddHostileShip`, blind jump, `ClaimShip` | NOT USED (out of scope §1 — legacy/vanilla-owned mutations) |

## 11. Failure modes (all fail-safe)

| Situation | Behavior |
|-----------|----------|
| No snapshot / never captured / stale / future / not started | `CombatUncertain` logged, nothing reported |
| Zero hostiles (readable) | quiet pass; closes episodes; feeds 60 s expiry hygiene |
| NaN combat levels | `-` in the engagement line, never a trigger |
| Boarders unknown (−1) | boarder rule silent, unknown pass counted |
| Took-damage capture fault | flag false, under-fire rule silent |
| Hostile composition change | engagement report re-arms (fresh dwell), warp picture re-arms |
| Persisting hostiles | record refreshes; quiet passes |
| Tracked set full | oldest shed (`CombatShed`) — defensive-only in this contract |

## 12. Test coverage

`tests/CombatTests.cs` — 77 checks covering CS01–CS13: engagement episode
end-to-end (open → stable-picture dwell report → repeat quiet), composition-
change re-arm with fresh dwell, episode close + live-record re-entry semantics
(`CombatOpened` is record-creation vocabulary), combat-level gap labeling
(unfavorable / sub-threshold / NaN), warp-combat picture (co-fire, repeat
quiet, warp-exit quiet), under-fire episodes with re-arm, boarder episodes
(independent of hostiles, −1 sentinel counted as unknown inputs), quiet paths
(zero hostiles = legitimate quiet, no unknown accounting), fail-safe inputs
(null/stale/not-started/future), authority deny-by-default (null/faulting/
non-master), vanished + expiry hygiene + bounded history (fresh-publish-after-
advance discipline), cadence + counters + diagnostics determinism, and
additive-capture ctor regression (original ctors default the new fields).
Full suite: **1640/1640 pass** (1563 prior + 77 new). The f16 hook runs after
f15; run_tests.ps1 compiles the combat domain file + test file with the rest.

## 13. Deliberate scope boundaries (documented)

- No task creation of any kind (§1); no ReconcileTasks; no severity ladder
  (P9 owns combat severity).
- No target authorship, no weapon/fire/turret/missile APIs, no
  `AddHostileShip`, no alert-level writes, no blind-jump logic, no
  `ClaimShip`, no `ShouldBeHostileToShip` calls (list-read only).
- No engagement-range logic (no verified ship-position capture; dead slider).
- No config-slider wiring (§8); no new Harmony patch class (permanent ceiling
  of 11 preserved; WorldTick Postfix extended in place — IL 395 → 430 bytes).
- No game enums in the pure domain (no classifier seam needed this phase —
  hostile membership comes from the authoritative `HostileShips` list).