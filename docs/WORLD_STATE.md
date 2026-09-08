# World State — Phase 6 Documentation

Status: **observation only** — the world layer reads authoritative game state
into bounded, immutable snapshots. It never executes tasks, mutates game
state, or issues orders. Consumers (probe, recovery, future directors,
validation, planning) read; nobody writes through this layer. Inert by
consumption until later phases act on snapshots.

## Files

| File | Role |
|---|---|
| `Core/World/WorldSnapshot.cs` | Immutable bounded snapshot types + `WorldTransition` |
| `Core/World/WorldStateService.cs` | Pure cache: throttled refresh, transition detection, listener + sticky sector-change flag |
| `Core/World/WorldSnapshotProbe.cs` | `ITaskWorldProbe` adapter answering recovery questions from the latest snapshot (fail-open on stale/empty) |
| `Core/World/PulsarWorldSource.cs` | Game-facing `IWorldSource` reading verified PULSAR registries (read-only, non-throwing, partial-error counting) |
| `Core/World/WorldLogBridge.cs` | Attaches Phase 1 `CapBotLog` (TASK) as the transition listener at mod boot |
| `Mod.cs` (modified) | Boot wiring: bridge, source, recovery probe |
| `Patch.cs` (modified) | `WorldTick` postfix on `PLController.Update` — the only new game hook; refresh-only |
| `CapBot.csproj` (modified) | Compile includes |

## Data flow

```
PLController.Update postfix (WorldTick)
  └─ WorldStateService.Refresh(TaskClock.NowMs)      [throttled 1 s]
      └─ PulsarWorldSource.Capture()                 [read-only, non-throwing]
          ├─ PLServer.Instance: GameHasStarted / AllPlayers / AllMissions /
          │   CurrentCrewCredits / ResearchMaterials / CurrentUpgradeMats /
          │   m_ShipCourseGoals / GetCurrentSector()
          ├─ PLEncounterManager.Instance: AllShips / PlayerShip
          ├─ PlayerShip.HostileShips / TargetShip / MyStats / GetCombatLevel()
          ├─ PLPlayer: GetPlayerName/GetPlayerID/IsBot/GetClassID/TeamID/
          │   GetPawn()/MyCurrentTLI/ActiveMainPriority
          ├─ PLBotController: DistMovedInLast5s / successRateSeekingTarget /
          │   timeSeekingTarget / PathRequestInProgress (via PLPlayer.MyBot)
          ├─ MyFlightAI.cachedRepairDepotList / cachedWarpStationList
          └─ ... (full API table in the phase report)
      └─ transition detection vs last-seen sector/warp
      └─ commit latest snapshot (lock), fire listener OUTSIDE lock
```

## Snapshot model

`WorldSnapshot` is immutable; every collection is bounded at construction
(ships ≤ 24, crew ≤ 16, missions ≤ 16, world objects ≤ 16, hostiles ≤ 16,
course goals ≤ 8, names truncated). Sentinel values: NaN fraction / -1 id /
null name = unknown. Nothing holds game-object references, so a snapshot
survives host migration and sector loads without leaking Unity objects.

- **Per-section authority** (`WorldAuthority`): session =
  `MasterDerived` when this peer is host else `LocallyObserved`; threats /
  navigation = `Synchronized`; resources = `MasterDerived`; world objects =
  `LocallyObserved`. Clients must not act authoritatively on values marked
  weaker than their role allows (research §9).
- **Ship ordering contract:** the player ship is `Ships[0]`; the captain is
  `Crew[0]` (`IsCaptain == true`).
- **`WorldSnapshot.Empty`** — never-captured placeholder (`SnapshotTimeMs ==
  -1`, `IsNeverCaptured == true`); the probe fail-opens on it.

## WorldStateService

- **Refresh(nowMs)** — throttled to `MinRefreshIntervalMs` (default 1 s).
  `SetSource` resets the cache; a null source makes Refresh a no-op (dormant
  by construction until `WorldTick` calls it). Time comes from the caller
  (production: `TaskClock.NowMs`); no wall-clock reads in the domain.
- **Transitions** — detected from last-seen sector/warp values (survives
  `SetSource` resets): `SECTOR_CHANGED` (both ids ≥ 0 and different),
  `WARP_STARTED`/`WARP_ENDED` edges. Listener fires **outside** the lock,
  after commit, at most once per refresh, never on the first capture. Bounded
  to 4 transitions per refresh.
- **Sticky sector-change flag** — `HasUnacknowledgedSectorChange()` stays
  true until `AcknowledgeSectorChanged()`, so slow consumers (once-per-task
  cadence) cannot miss a change between their checks.
- **Diagnostics** — `RefreshCount`, `ErrorCount`/`LastError` (source threw),
  `ResetForTests()`.

## PulsarWorldSource

Read-only, non-throwing by section: each section builder is wrapped, failures
count into `PartialErrorCount`/`LastPartialError`, null sections render as
"unknown" defaults. Hostility: reads `PlayerShip.HostileShips` (the game's
own authoritative list; Quality Improver can replace `ShouldBeHostileToShip`
— this layer never calls hostility logic, it observes outcomes). Team counts
(team 0/1/2 over `AllShips`) are raw observations, not hostility claims.
Combat-level semantics are INFERRED (research §6.6) and carried as data only.

## WorldSnapshotProbe (Phase 3 deliverable: recovery's real probe)

Answers `ITaskWorldProbe` from the latest snapshot; nothing is cached between
calls. **Fail-open on uncertainty**: never-captured / stale (> 10 s) /
provider-threw snapshots answer "valid/available"; empty-but-captured views
are not evidence of absence, so they also fail open. Destructive recovery
actions (Cancel/Fail) require positive evidence:

- `TargetValid`: `TargetKind == "SHIP"` → id must appear in captured ships;
  `"MISSION"` → id must appear in captured missions. Other kinds (SECTOR,
  COMPONENT, …) are unverifiable from bounded data → true. Unparseable ids →
  true (data, not evidence).
- `OwnerAvailable`: `CAPTAIN` / `BOT:<id>` are crew-membership questions —
  the owner must be present in the captured crew, unavailable only with
  `AliveKnown && !Alive`. `HOST` and unknown formats → true. Populated crew
  without the owner is positive absence → false.
- `CapabilityAvailable` → true (P7 owns capabilities).
- `WorldInvalidatesTask` → false (no invented premise semantics this phase).

## Multiplayer / authority constraints (research §9)

All snapshot construction is process-local registry reads; no RPCs added.
Host-migration hazard: snapshots carry no game-object references and the
service holds no transient vanilla AI state — the latest snapshot is
disposable, and the probe fail-opens after long staleness. Authority marks
per section; the session section is `MasterDerived` only while this peer is
host.

## Performance

One throttled (~1 Hz) bounded pass over the existing registries — no
per-frame scene scans, no FindObjectsOfType, no LINQ on the capture path.
Collections are sized once per refresh. `ToSummaryLine()` allocates one
string per call and is intended for diagnostics only.

## Security posture

Task target strings are opaque data (parsed only as `int` for id lookups,
never executed). Names/objective text are truncated bounded strings carried
as data. No arbitrary code execution, no runtime compilation, no shell, no
new RPCs.

## Explicitly not in this phase

- No capability registry (P7), executor (P8), directors (P9+), Captain Brain
  2.0 (P18), LLM, dynamic task generation, persistence redesign, UI.
- No gameplay routes through world state: the refresh is read-only, the
  probe is attached but recovery still has no tick driver, and no consumer
  acts on snapshots yet. Existing gameplay is untouched.
- No new PULSAR APIs beyond the verified table in the phase report; every
  member used is documented VERIFIED / INFERRED / UNVERIFIED there.