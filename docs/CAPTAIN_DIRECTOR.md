# Phase 18: Captain Deliberation Director — Contract

**Status: IMPLEMENTED (Alpha 1.2.2).** The fusion + narrow-authoring layer —
a bounded deterministic CONSUMER of the P6 snapshot and the P9/P14/P15/P16/P17
director readbacks that (a) tracks captain-deliberation situations as data and
(b) authors EXACTLY ONE task family bound to EXACTLY ONE capability:
`ISSUE_MOVE_ORDER` (`RegisteredCapabilities.IssueMoveOrder`). It is **not** a
behavior tree, **not** a utility scorer, and **not** a general planner: every
other captain surface (orders, targets, course changes, blind jumps, alert
levels, shop, comms) stays owned by legacy/P9/P14 and is NOT authored here.

Files: `CapBot/Core/Captain/CaptainDirector.cs` (CaptainIntent,
CaptainDirector), `CaptainLogBridge.cs` (logging bridge). No P6 snapshot
change (the crew section already carries `CurrentTLIName`); no config change
(no Phase 18 SaveValue contract was promised; the dead H4 sliders remain
untouched).

---

## 1. Why exactly one capability is authorable (the ownership argument)

Phase 18 research (all surfaces compile- or DLL-verified) audited every P7
capability for a third author:

- `SET_CAPTAIN_ORDER`: legacy commits orders through its own 2 s/8 s dwell
  machine (shipped Patch.cs:246-266) and P9 is the sanctioned override author.
  A third author would thrash the order banner AND share the 2000 ms registry
  cooldown, delaying P9 emergency orders. NOT safe.
- `SET_CAPTAIN_TARGET`: explicit P17 covenant — authorship owned by P9
  (`DangerousCombat`) + the legacy captain tick. NOT safe.
- `ADD/REMOVE_COURSE_GOAL`: P14 owns them (its header reserves the course-goal
  channels); legacy `SetNextDestiny` clears/rebuilds the course every ~5 s
  (shipped Patch.cs:415-428). NOT safe.
- `CLEAR_COURSE_GOALS`: legacy uses the RPC directly at four sites.
  Destructive. NOT safe.
- `READ_WORLD_SNAPSHOT`: a read contract — directors read the world seam
  directly; authoring read-tasks is pointless. NOT authored.
- `ISSUE_MOVE_ORDER`: **zero in-tree authors**, dispatcher branch ready
  (`PulsarCapabilityDispatcher.DispatchIssueMoveOrder` accepts `TargetKind ==
  "SECTOR"` only, parses the sector id, derives `Vector3` via the galaxy map,
  and RPCs `IssueMoveOrder` to the captain pawn), and the effect is a
  transient 20 s-TTL crew move order that legacy never reads or writes — a
  misfire self-heals within 20 s. THE one safe channel.
- The registry accepts it: `CapabilityRegistry.IsTargetAcceptable` with
  `TargetRequirement.None` accepts any target kind; the director uses the
  dispatcher's own `SECTOR` shape anyway.

## 2. What the director does

One bounded deterministic evaluation pass per `MinRecheckMs` (5 s), driven
from the WorldTick Postfix after the combat block (host-only), then a
reconcile pass (same driver, immediately after):

- **CREWGATHER intent** (single `CAPTAIN:CREWGATHER` record):
  - **`CaptainIntentOpened`** — first pass with readable divergence: at least
    one alive-known, alive, non-captain bot whose readable current location
    (`CrewMemberSnapshot.CurrentTLIName`) differs ordinally from the readable
    captain location. Immediate (no dwell) — this line only documents the
    episode.
  - **`MoveOrderAuthored #n CAPTAIN:CREWGATHER sector=X divergent=M`** — after
    `AuthoringDwellMs` (15 s) of persisting divergence AND a pass through the
    calm gate (§4): one `CAPTAIN_DELIB` task is created, registered, and
    queued through the standard pipeline (owner `CAPTAIN`, priority 8 — top of
    the normal band, never preempts P14 (20) or P9 (110+), 60 s timeout,
    1 retry, `Preemptible=true` metadata) bound to `ISSUE_MOVE_ORDER` with a
    `SECTOR` target = the CURRENT sector, i.e. "gather crew to the captain's
    position". The move order effect is a transient 20 s-TTL crew order.
  - **`MoveOrderRefused #n`** — registered but queue refused (lifecycle owns
    the log; counted as `AuthoringRefused`; the record treats it as resolved
    so the requeue discipline still applies). Registry-refused tasks are
    counted `AuthoringRefused` with no task id kept.
  - **`CaptainIntentExpired`** — divergence stays absent past
    `ActiveExpiryMs` (30 s): the record decays into bounded FIFO history (16)
    with a one-shot report. A DECAYED record resets the authoring budget
    (fresh episode) — the anti-churn cap is per record lifetime.
- **`CaptainUncertain`** — fail-safe gate lines (stale / never-captured /
  not-started / future snapshots), with `LastUncertainReason`.
- **`CaptainShed`** — tracked-set overflow (defensive; the single-record
  contract never reaches it — kept for house-pattern parity).

Reconciliation (`ReconcileTasks`, P14-mirroring): active intents whose task
reached a terminal state or vanished from the registry get `TaskResolvedMs`
stamped; re-authoring re-arms only after `AuthoringRequeueBlockMs` (20 s) from
that stamp. The task itself is NEVER touched — recovery owns lifecycle.

## 3. Crew-read rules (fail-safe data classification)

Per bot (bounded ≤ `WorldSnapshot.MaxCrew` pass, no LINQ):

| Crew data | Classification |
|-----------|----------------|
| Alive + readable TLI ≠ captain TLI (ordinal) | **divergent** (the trigger input) |
| `AliveKnown == false` | unknown input (capture fault) — never triggers |
| Readable death (`AliveKnown && !Alive`) | excluded, NOT unknown (P9 owns dead/wounded crew through its health ladder) |
| Alive bot with null/empty/oversized TLI | unknown input — never triggers |
| Captain with null/empty TLI | the whole rule is unknown when bots are present (unknown-input accounting) |
| No captain + no bots (null crew section) | quiet — nothing is readable, nothing to decide |

The captain is the FIRST crew entry with `IsCaptain` (bounded scan, break);
bots are counted only when alive-readable AND location-readable.

## 4. The calm gate (fail-closed authoring precondition)

Authoring happens only when NOTHING else is in flight — every input must be
readable, and any unreadable input BLOCKS:

| Input | Read source | Calm requires |
|-------|-------------|---------------|
| P9 emergencies | `EmergencyDirector.CurrentState` + `ActiveCount` (lock-nested reads, one-way Captain→Emergency, deadlock-safe) | state Normal/Monitoring AND zero active |
| P14 recovery plans | `NavigationRecoveryDirector.ActivePlanCount` | 0 |
| P17 combat records | `CombatDirector.ActiveRecordCount` | 0 |
| Snapshot hostiles | `Threats.KnownHostileShipIds.Count` | 0 |
| Boarders | `Threats.InvadersOnboardCount` | ≤ 0 (−1 sentinel also blocks — fail-closed) |
| Warp | `Navigation.InWarp` | false |
| Current sector | `Navigation.CurrentSectorId` | ≥ 0 (known) |

Blocked passes count `CalmGateBlocks`; the intent stays open and the dwell
keeps running — the pass retries after cadence.

## 5. Gates (fail-safe, mirroring P9/P14/P15/P16/P17)

| Gate | Behavior |
|------|----------|
| Authority | deny-by-default seam; null/faulting/non-master ⇒ no-op (the move order is a host-authoritative crew effect) |
| Cadence | one pass per `MinRecheckMs` (5 s); gated passes not counted |
| Snapshot | null / never-captured / stale >20 s / future-dated / `!GameStarted` ⇒ `CaptainUncertain …` line, no decisions |
| Unknown crew data | never triggers; counted in `UnknownInputPasses` (any unknown bot datum, or captain unreadable while bots present) |
| Live task | an intent with a live task suppresses duplicate authoring (`DuplicatesSuppressed`) |

## 6. Lifecycle bookkeeping (bounded)

- Counters: `Evaluations, IntentsTracked, OpenedReports, AuthoringsIssued,
  CalmGateBlocks, DuplicatesSuppressed, AuthoringCapped, AuthoringRefused,
  IntentsExpired, PlansExpired, StaleRejections, UnknownInputPasses,
  LastUncertainReason`; diagnostics `Lines()` (one per tracked intent,
  deterministic order) + `StatusLines()` (2 lines, `captain=` prefix,
  `uncertain=` in the second) + `GetIntent(trackId)` live-record readback.
- The authoring budget is per record lifetime: ≤ `MaxAuthoringsPerIntent`
  (3) authorings per intent record; a decayed record (hygiene) resets the
  budget with a fresh episode.
- Every collection bounded (tracked ≤ 8, history ≤ 16, pending lines ≤ 4);
  timestamps are explicit nowMs values (TaskClock semantics); no wall-clock
  reads.

## 7. Data flow

```
WorldStateService.Latest (P6 snapshot, 1 Hz refresh; crew section carries TLI names)
  -> authority gate (deny-by-default; clients never evaluate)
  -> cadence gate (one pass per MinRecheckMs = 5 s)
  -> snapshot fail-safe gate (stale >20 s / future / never-captured / !GameStarted)
  -> crew scan (captain TLI + bot divergence classification)
  -> CREWGATHER episode (open edge -> 15 s dwell -> calm gate -> CAPTAIN_DELIB task)
  -> task pipeline (P3 register -> P5 queue -> P4/P7/P8 executor -> ISSUE_MOVE_ORDER RPC)
  -> reconcile (terminal/vanished task stamps resolution; 20 s requeue)
  -> hygiene (divergence absent past 30 s -> bounded history + CaptainIntentExpired)
  -> bounded diagnostic lines via CaptainLogBridge (CapBotLog.CAPTAIN)
```

## 8. The deliberation task shape (deterministic)

```
CapBotTask.Create(
  "CAPTAIN_DELIB",        // static vocabulary, never parsed
  "CAPTAIN",              // matches the P7 capability owners + P9/P14 task owner
  "captain deliberation: crew not with captain; gather to current sector",
  priority 8,             // top of normal band 1..8 (+aging ≤5; emergencies preempt instantly)
  1 retry,                // deliberation may retry once; never a storm
  60000 ms timeout,       // deliberation tasks self-expire (bounded work)
  "SECTOR", <currentSectorId>, null)
+ metadata: DelibId=CAPTAIN:CREWGATHER, DelibKind=CREWGATHER,
            Preemptible=true, CapabilityId=ISSUE_MOVE_ORDER, Argument=<sectorId>
```

The P8 executor dispatches via `PulsarCapabilityDispatcher.DispatchIssueMoveOrder`
(SECTOR target → galaxy-map position → `IssueMoveOrder` RPC to the captain
pawn). Tasks the executor cannot dispatch fail safe: the capability metadata
is always present, so the P8 capability check passes by construction.

## 9. Multiplayer authority model

- The WorldTick driver gates on `PhotonNetwork.isMasterClient`; the director
  additionally consults the authority seam (`ExecutionClaims.IsAuthoritative()`),
  fail-closed. Clients produce no captain lines and author nothing.
- The director itself never RPCs. Task creation flows through the P3/P4/P5/P7/P8
  pipeline, which already enforces master-side execution and the correct RPC
  masks — the coordinator inherits the pipeline's multiplayer safety.

## 10. Performance contract

- Cadence-gated 5 s; reuses the P6 snapshot (no extra game queries, no
  FindObjectsOfType, no scene scans, no LINQ); every collection bounded;
  no per-frame work. The calm gate is a fixed set of bounded readbacks (three
  lock-nested counter reads + four snapshot field reads).
- The lock discipline is one-way (Captain → Emergency/Registry reads); no
  director call ever runs while holding another director's lock.

## 11. Verified APIs used

| Surface | Status |
|---------|--------|
| P6 snapshot crew section (`IsCaptain` / `IsBot` / `AliveKnown` / `Alive` / `CurrentTLIName`) | VERIFIED (P6, compile-proven; TLI captured from `player.MyCurrentTLI.TeleporterLocationName` with per-field try/catch) |
| `PulsarCapabilityDispatcher.DispatchIssueMoveOrder` (SECTOR-only target, galaxy Vector3 derivation, RPC) | VERIFIED (DLL/read — research report; P8 dispatch path) |
| `CapabilityRegistry.IsTargetAcceptable` (`TargetRequirement.None` ⇒ any kind) | VERIFIED (P7) |
| `RegisteredCapabilities.IssueMoveOrder` = `ISSUE_MOVE_ORDER` | VERIFIED (P7 const) |
| `EmergencyDirector.CurrentState` / `ActiveCount` lock-guarded readbacks | VERIFIED (P9) |
| `NavigationRecoveryDirector.ActivePlanCount` | VERIFIED (P14) |
| `CombatDirector.ActiveRecordCount` | VERIFIED (P17) |
| `CapBotTask.Create` (9 args) + `SetMetadata` + `TaskRegistry.Register` + `TryQueue` | VERIFIED (P3/P5, compile-proven) |
| `TaskClock.NowMs` semantics + `IsTerminal` (Completed/Cancelled/Expired, NOT Failed — mirrors P14 ReconcileTasks) | VERIFIED (P2/P14) |
| `ISSUE_MOVE_ORDER` effect (transient 20 s-TTL crew move order; legacy never reads/writes it) | VERIFIED (research report §safe-authorship) |
| SET_CAPTAIN_ORDER / SET_CAPTAIN_TARGET / ADD·REMOVE·CLEAR_COURSE_GOALS authorship | NOT USED (out of scope §1 — legacy/P9/P14-owned mutations) |

## 12. Failure modes (all fail-safe)

| Situation | Behavior |
|-----------|----------|
| No snapshot / never captured / stale / future / not started | `CaptainUncertain` logged, nothing decided |
| Unknown bot liveness/TLI, unreadable captain with bots present | rule unknown — quiet pass, `UnknownInputPasses` counted |
| Dead bots | excluded (not unknown); P9 owns dead/wounded crew |
| Any calm-gate input unreadable or active | authoring blocked (`CalmGateBlocks`), intent stays open, dwell keeps running |
| Live task on the intent | duplicates suppressed |
| Budget exhausted (3 authorings) | capped; record keeps tracking; decay resets the budget |
| Register/queue refused | `AuthoringRefused`; no retry storm — the next dwell window re-arms |
| Task terminal/vanished | reconcile stamps resolution; re-arm after 20 s |
| Divergence cleared | episode closes; intent decays after 30 s into bounded history |
| Tracked set full | oldest shed (`CaptainShed`) — defensive-only in this contract |

## 13. Test coverage

`tests/CaptainTests.cs` — CT01–CT14 covering: authoring end-to-end (intent
open → below-dwell quiet → dwell + calm → task created/registered/queued
through the REAL pipeline with the full shape asserted: type/owner/priority/
timeout/target/capability/argument/preemptible metadata), live-task
suppression + reconcile-stamped resolution + requeue block + re-arm, dwell
gate, calm gate (P17 combat record blocks, then clears → authoring; snapshot
threats / warp / unknown sector block), vanished-task reconciliation,
anti-churn cap + record decay resetting the budget, dead-bot exclusion (not
unknown), unknown crew data never triggering (unknown liveness; unreadable
captain with bots present — the sawBot accounting), fail-safe inputs
(null/stale/not-started/future), authority deny-by-default, cadence gate +
counters + diagnostics determinism + GetIntent guards.

Harness honesty note: this suite (and CombatTests) originally gated its
`Run()` with a hardcoded `return 0;` — the TOTAL failed=0 gate LIED while 4
stale CS expectations and several CT bugs existed (1718/0 now only because
the gates return `s_Failed` and every failure was fixed: CT02/CT09 need
`TryStart()` before `TryComplete()` (Queued→Completed is illegal per the
transition table), CT06 needs a cadence advance after `CombatDirector.ResetForTests()`,
CS03/CS06/CS07/CS11 expectations corrected to the directors' actual cumulative /
co-fire semantics, and the director's unknown-input accounting extended to the
unreadable-captain-with-bots case). Full suite: **1718/1718 pass** (1640 prior
+ 78 new). The f17 hook runs after f16; run_tests.ps1 compiles the captain
domain file + test file with the rest.

## 14. Deliberate scope boundaries (documented)

- Authoring is EXACTLY ONE task family (`CAPTAIN_DELIB`) bound to EXACTLY ONE
  capability (`ISSUE_MOVE_ORDER`, SECTOR target = current sector). No order,
  target, course, blind-jump, alert, shop, or comms authorship (§1).
- No behavior tree, no utility scoring, no Ollama/Qwen LLM calls (later
  phases' concern); the director is the deterministic bounded substrate they
  would consume.
- No severity ladder, no health logic (P9 territory); location divergence is
  the sole trigger.
- No config-slider wiring; no new SaveValue; no new Harmony patch class
  (permanent ceiling of 11 preserved; WorldTick Postfix extended in place —
  IL 430 → 499 bytes).
- No game enums in the pure domain; no scene scans; no per-frame work.