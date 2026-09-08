# Phase 16: Economy Director — Contract

**Status: IMPLEMENTED (Alpha 1.2.2).** Bounded deterministic economy-tracking
layer over the P2–P15 contracts. It is **not** a trader, **not** a shopper, and
**creates NO tasks**: Phase 16 is deliberately REPORT-ONLY — it observes the
P6 snapshot resources/navigation/world-objects sections (plus the Phase 16
additive unit-price capture) and emits bounded economy reports that later
phases (Captain Brain 2.0 planning) can consume as data. The legacy
`BotEconomy`/`BotExtractor`/`HandleShop` behaviors keep exclusive ownership of
every economy ACTION; this director never performs one.

Files: `CapBot/Core/Economy/EconomyDirector.cs` (EconomyRecord, EconomyDirector),
`EconomyLogBridge.cs` (logging bridge). Additive P6 capture:
`ResourceSnapshot.FuelBasePrice` / `CoolantBasePrice` (P9-style ctor pattern).

---

## 1. Why report-only (the API-surface argument)

- The P7 capability catalog contains **no economy/trade/purchase capability**
  (deliberately excluded in Phase 7 — docs/CAPABILITIES.md: "no speculative
  combat/mission/economy/build capabilities"), and the P8 executor **rejects**
  any task whose `CapabilityId` has no dispatch branch. So there is no
  verified, executor-dispatchable action an economy plan could request — a
  director task would fail at execution by construction.
- Trade RPCs (`BuyComponent`/`SellComponent`/`CaptainBuy_Fuel`/`_Coolant`/
  `_MissileRefill`/`AttemptExtraction`), direct credit mutation patterns
  (legacy audit M10), `CrewPurchaseLimitsEnabled = false` (audit L4), and
  `PLTradeData` (referenced nowhere in the repo or research doc — treated as
  nonexistent) are all OUT OF SCOPE. Legacy code that uses them is untouched.
- `ShopRepMultiplier()` internals (private Patch.cs helper, body never
  verified) are never consumed numerically — the director reports
  affordability against the *base* prices it captured, and says so.

## 2. What the director does

One bounded deterministic evaluation pass per `MinRecheckMs` (5 s), driven
from the WorldTick Postfix after the mission block (host-only):

- **Tracks** the credits picture in an `ECONOMY:CREDITS` record (bounded set
  of `MaxActiveEconomyRecords` = 8 records, deterministic oldest-shedding by
  LastSeenMs — tie → lowest key order, `EconomyShed` line; history ≤ 16).
- **`EconomyOpened`** — first readable credits reading (one-shot per record).
- **`CreditsLowReport`** — credits fall to or below `ReserveFloor` (2500, the
  legacy `CREDIT_RESERVE` documentation — see §8). One report per episode,
  re-armed when credits recover above the band. Data-only; no purchases.
- **`CreditsDeltaReport`** — a credits change larger than
  `MaxDeltaReportAbs` (10000) between passes: direction + magnitude only,
  **never cause inference** (the snapshot cannot attribute deltas to missions
  vs purchases vs rewards). Small deltas are counted, not reported.
- **`StoreSectorReport`** — the current sector is shop-class per the
  **classifier seam** (production wires the exact compile-proven
  `ESectorVisualIndication` list shipped Patch.cs:169-171 uses) AND the ship
  has dwelled there `StoreDwellMs` (15 s). One report per sector episode;
  leaving the sector closes the episode (re-entry re-arms).
- **`FuelAffordabilityReport` / `CoolantAffordabilityReport`** — supply low
  (mirroring the P9 warning thresholds as data constants: ≤ 2 capsules /
  ≤ 30%) AND the captured unit price readable AND credits < price (cannot buy
  even one unit). One report per episode, re-armed when the condition clears.
  Pure data — the emergency director owns low-supply SEVERITY (P9 rules),
  legacy HandleShop owns buying; this is the economy-side coordination record
  ("can the crew afford the fix AT ALL").
- **`WarpTollReport`** — a snapshot `WARP_STATION` carries a positive `Price`
  that exceeds readable credits (the verified comparison shape of
  Patch.cs:2609). Dwell-gated (`WarpTollDwellMs` = 15 s — the flight-AI cache
  list is not order-stable), one report per toll episode, cheapest
  unaffordable toll wins; `Price <= 0` sentinels never trigger.
- **`EconomyVanished`** — the credits record decays (inputs unreadable for
  `ActiveExpiryMs` = 60 s) with a one-shot report (P15 mirror).
- **`EconomyUncertain`** — fail-safe gate lines (stale / never-captured /
  not-started / future snapshots).

## 3. Gates (fail-safe, mirroring P9/P14/P15)

| Gate | Behavior |
|------|----------|
| Authority | deny-by-default seam; null/faulting/non-master ⇒ no-op (clients never report — credits are MasterDerived host-computed values) |
| Cadence | one pass per `MinRecheckMs` (5 s); gated passes not counted |
| Snapshot | null / never-captured / stale >20 s / future-dated / `!GameStarted` ⇒ `EconomyUncertain …` line, no decisions |
| Unknown sentinels | `Credits == -1` / NaN coolant / `-1` fuel / `-1` prices / `-1` sector never trigger a rule (counted in `UnknownInputPasses`) |
| Classifier | null/faulting shop-sector classifier ⇒ never a shop sector (deny-by-default seam, P10 SetRoleNameResolver pattern) |

## 4. Lifecycle bookkeeping (bounded)

- The credits record refreshes every readable pass and never expires while
  inputs stay readable. When credits go unreadable and stay so past
  `ActiveExpiryMs` (60 s), the record decays into bounded FIFO history (16)
  with a one-shot `EconomyVanished` report.
- Counters: `Evaluations, RecordsTracked, OpenedReports, LowReports,
  DeltaReports, StoreReports, FuelAffordabilityReports,
  CoolantAffordabilityReports, WarpTollReports, VanishedReports,
  DuplicatesSuppressed, PlansExpired, StaleRejections, UnknownInputPasses,
  LastUncertainReason`; diagnostics `Lines()` (one per tracked record,
  deterministic order) + `StatusLines()` (2 lines).
- No tasks: no `CapBotTask` creation, no `TaskRegistry` writes, no
  scheduler/claim/executor interaction, and therefore no `ReconcileTasks`
  (nothing to reconcile — documented).

## 5. Data flow

```
WorldStateService.Latest (P6 snapshot, 1 Hz refresh; Phase 16 +prices)
  -> authority gate (deny-by-default; clients never evaluate)
  -> cadence gate (one pass per MinRecheckMs = 5 s)
  -> snapshot fail-safe gate (stale >20 s / future / never-captured / !GameStarted)
  -> credits rules (opened / delta edge / reserve-band low edge)
  -> affordability rules (fuel / coolant: low supply + price readable + credits < price)
  -> store-sector rule (classifier seam + 15 s dwell, one report per episode)
  -> warp-toll rule (snapshot WARP_STATION price > credits, 15 s dwell)
  -> hygiene (credits unreadable past 60 s -> bounded history + EconomyVanished)
  -> bounded diagnostic lines via EconomyLogBridge (CapBotLog.ECONOMY)
```

## 6. The additive P6 capture (Phase 16 snapshot change)

`ResourceSnapshot` gains two readonly fields (P9 additive-ctor pattern; the
original 5-arg constructor is preserved verbatim and defaults them to −1):

| Field | Capture | Status |
|-------|---------|--------|
| `FuelBasePrice` | `(int)PLServer.GetFuelBasePrice()` | VERIFIED (compile-proven shipped Patch.cs:2220/2227) |
| `CoolantBasePrice` | `(int)PLServer.GetCoolantBasePrice()` | VERIFIED (compile-proven shipped Patch.cs:2234/2241) |

Capture is per-field try/catch (`RecordPartial`); any fault leaves −1 and the
affordability rules stay silent. `PulsarWorldSource` fills both in the
existing resources section (no new capture pass, no FindObjectsOfType).

## 7. Multiplayer authority model

- The WorldTick driver gates on `PhotonNetwork.isMasterClient`; the director
  additionally consults the authority seam (`ExecutionClaims.IsAuthoritative()`),
  fail-closed. Clients produce no economy reports.
- No Photon actions of any kind: the director never RPCs, never buys, never
  sells. Pure snapshot reads + log lines.

## 8. Audit honesty note (H4 — the dead slider)

The legacy `MinCreditsReserve` config slider is DEAD (nothing reads it —
grep-verified in CAPBOT_AUDIT.md H4; `BotEconomy` hardcodes
`CREDIT_RESERVE = 2500`). This director documents the legacy constant as
`ReserveFloor = 2500` — a reporting threshold, not a behavior change — and
does **NOT** wire the slider. Config wiring is a separate later-phase concern
and would silently change legacy behavior if adopted here. `ShopRepMultiplier`
is likewise not consumed (its body was never verified); affordability reports
compare against captured **base** prices.

## 9. Performance contract

- Cadence-gated 5 s; reuses the P6 snapshot (no extra game queries, no
  FindObjectsOfType, no scene scans, no LINQ); every collection bounded
  (tracked ≤ 8, history ≤ 16, pending lines ≤ 4); no per-frame work. The
  warp-toll scan is a bounded ≤ 16-element pass over the snapshot's
  world-objects list.

## 10. Verified APIs used

| Surface | Status |
|---------|--------|
| P6 snapshot resources/navigation/world-objects sections (Credits/FuelCapsules/CoolantLevelPercent/SectorVisualIndication/WARP_STATION Price) | VERIFIED (P6, compile-proven) |
| `PLServer.GetFuelBasePrice()` / `GetCoolantBasePrice()` (new capture) | VERIFIED (shipped Patch.cs HandleShop, lines 2220/2234) |
| `ESectorVisualIndication` shop-class list (classifier wiring) | VERIFIED (shipped Patch.cs:169-171 — same list, same vocabulary) |
| Warp-toll comparison shape (`GetPriceForSectorID` result vs credits) | VERIFIED (shipped Patch.cs:2609); director compares the already-captured snapshot Price |
| Trade RPCs / direct credit mutation / PLTradeData / ShopRepMultiplier | NOT USED (out of scope §1) |

## 11. Failure modes (all fail-safe)

| Situation | Behavior |
|-----------|----------|
| No snapshot / never captured / stale / future / not started | `EconomyUncertain` logged, nothing reported |
| Credits unknown (−1) | rules silent, unknown pass counted; record decays after 60 s |
| Prices unknown (−1) | affordability rules silent only |
| Classifier null/faulting | never a shop sector |
| Credits == price boundary | AFFORDABLE (strict `<`) — never reports |
| Persisting readable inputs | never expire; quiet passes (small deltas counted) |
| Tracked set full | oldest shed (`EconomyShed`), newest retained |

## 12. Test coverage

`tests/EconomyTests.cs` — 102 checks covering ES01–ES13: credits tracking
end-to-end (open → low edge → recovery re-arm), delta reports (large ±,
small suppressed, moderate-delta-crossing-the-band co-fire), store-sector
dwell/exit/re-entry episodes, fuel/coolant affordability episodes (boundary
`credits == price` is affordable), unknown-sentinel quiet paths (prices,
fuel sentinel with coolant still firing), warp-toll dwell/sentinels/cheapest-
pick, fail-safe inputs (null/stale/not-started/future/unknown-credits),
authority deny-by-default, vanished + expiry hygiene + bounded history
(fresh-publish-after-advance discipline), classifier seam deny-by-default,
cadence + counters + diagnostics. Full suite: **1563/1563 pass** (1461 prior
+ 102 new). The f15 hook runs after f14; run_tests.ps1 compiles the economy
domain file + test file with the rest.

## 13. Deliberate scope boundaries (documented)

- No task creation of any kind (§1); no ReconcileTasks.
- No trade RPCs, no credit mutation, no config-slider wiring (§8), no
  `CrewPurchaseLimitsEnabled` replication, no `PLTradeData`, no
  `ShopRepMultiplier` consumption.
- No cause inference for credit deltas (direction + magnitude only).
- No new Harmony patch class (permanent ceiling of 11 preserved; WorldTick
  Postfix extended in place — IL 360 → 395 bytes).