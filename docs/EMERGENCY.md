# Phase 9: Emergency Director — Contract

**Status: IMPLEMENTED (Alpha 1.2.2).** Deterministic priority-override layer over
the P2–P8 contracts. It is **not** Captain Brain 2.0, **not** an LLM planner, and
**never** executes anything: it detects, decides, and creates tasks. Every action
still routes through the scheduler (P4), execution claims (P5), capability
validation (P7) and the executor (P8), exactly as for every other task.

Files: `CapBot/Core/Emergency/EmergencyState.cs` (enums, precedence, decision,
identity, active record), `EmergencyDetector.cs` (pure rule engine),
`EmergencyDirector.cs` (dedup, state machine, task creation, reconciliation),
`EmergencyLogBridge.cs` (logging bridge). World input rides the extended Phase 6
source (no separate PulsarEmergencySource — see "World dependencies").

---

## 1. Core principle — deterministic and fail-safe

- No LLM/Ollama/Qwen anywhere in the loop. Every decision is a pure function of
  (verified snapshot data, bounded rule table, clock). All vocabulary is static
  code; nothing is parsed from chat/NPC/mission text.
- **Fail-safe rule:** if the director cannot PROVE an emergency is real (missing,
  stale, future-dated, contradictory or unknown world data), it marks the
  situation uncertain (`EmergencyUncertain …` log line), creates nothing, and
  performs no destructive action.
- Detection inputs are verified PULSAR surfaces only (§9). No telemetry invented.
- Deny-by-default authority: with no authority probe wired (or a faulting one),
  `Evaluate` is a no-op. Production wiring is `ExecutionClaims.IsAuthoritative()`
  (master-client state), so **clients never produce emergency tasks**.

## 2. Data flow (the mandated pipeline)

```
WorldStateService.Latest (P6 snapshot, 1 Hz refresh)
  -> freshness gate (stale >20 s / future-dated / never-captured / !GameStarted => fail-safe)
  -> EmergencyDetector.Detect (pure rules over verified data)
  -> deduplicate against bounded ACTIVE records (deterministic identity)
  -> emergency state machine (legal transitions + dwell hysteresis)
  -> EmergencyDecision (bounded immutable data, logged)
  -> CapBotTask.Create("EMERGENCY", ...) + Register + TryQueue  (P2 lifecycle)
  -> TaskScheduler.Tick grants it (P4; Preemptible="true" lets the scheduler's
     own policy-gated preemption displace lower-priority running work)
  -> TaskExecutor pipeline claims/validates/executes (P8 + P5 + P7)
  -> outcome recorded; failure feeds P3 recovery exactly like any task
```

The director never touches steps after task creation except read-only
bookkeeping (`ReconcileTasks` resolves its own active records when their task
reaches a terminal state — the task itself is left to the lifecycle/recovery).

## 3. Emergency state machine

`Normal -> Monitoring -> Warning -> Emergency -> Critical -> Recovery -> Normal`

| From        | Legal targets                       |
|-------------|-------------------------------------|
| Normal      | Monitoring                          |
| Monitoring  | Warning, Normal                     |
| Warning     | Emergency, Recovery                 |
| Emergency   | Critical, Recovery                  |
| Critical    | Recovery                            |
| Recovery    | Normal (after RecoveryHoldMs = 10 s) |

- **Hysteresis:** every transition requires the current state to have been held
  at least `StateDwellMs` (5 s). De-escalation passes through Recovery; Recovery
  additionally requires `RecoveryHoldMs` (10 s) before Normal.
- Illegal transitions are **rejected, never applied** (`TransitionsRejected`
  counter + `EmergencyTransitionRejected` log line). A direct jump that is not
  table-legal resolves to one escalation rung up (`Normal->Monitoring->Warning
  ->Emergency->Critical`) or Recovery for de-escalation — deterministic, monotone.
- Desired state maps from the strongest current finding: Critical+ severity =>
  Critical, Severe => Emergency, otherwise Warning; no findings => Normal.

## 4. Severity and precedence (the mandated 9-class order)

`EmergencySeverity` ladder: `None < Warning < Elevated < Severe < Critical`.

Priority = `100 + class*10 + bump` (bump: Severe=1, Critical=2). Classes:

| Class | Const              | Emergency types                          |
|-------|--------------------|------------------------------------------|
| 8     | ClassCrewSurvival  | CriticalCrewHealth, ImminentDeath        |
| 7     | ClassShipSurvival  | CriticalHull                             |
| 6     | ClassCatastrophe   | Fire, ReactorCritical, CoolantCritical   |
| 5     | ClassCombat        | DangerousCombat                          |
| 4     | ClassNavigation    | NavigationFailure, FuelCritical          |
| 3     | ClassMission       | ObjectiveCritical                        |
| 2     | ClassEconomy       | (default)                                |
| 1     | ClassMaintenance   | (unused)                                 |

Normal (non-emergency) priorities range 1..8 plus a bounded aging bonus of +5;
the lowest emergency class starts at 111, so **higher emergency classes always
outrank lower-priority tasks** without touching scheduler internals. Within a
class, severity bumps order emergencies deterministically.

## 5. Detection rules (all thresholds are `public const`s on EmergencyDetector)

| Type                | Trigger (verified inputs only)                        | Sev         | Realization |
|---------------------|-------------------------------------------------------|-------------|-------------|
| CriticalHull        | hull ≤ 0.25 / ≤ 0.35 / ≤ 0.50                         | Crit/Sev/Elev | SET_CAPTAIN_ORDER "9" (repair protocols) |
| CriticalCrewHealth  | worst alive bot health ≤ 0.25 / ≤ 0.35 / ≤ 0.50       | Crit/Sev/Elev | SET_CAPTAIN_ORDER "9" |
| Fire                | CountNonNullFires ≥ 3 / ≥ 1                           | Sev/Warn    | SET_CAPTAIN_ORDER "6" (repel/board) |
| ReactorCritical     | reactor temp ≥ 95% / ≥ 90% of max                     | Crit/Sev    | SET_CAPTAIN_ORDER "9" |
| DangerousCombat     | ≥1 authoritative hostile; Severe if ≥3 hostiles or combat-level gap unfavorable (×1.33, INFERRED data-only) | Sev/Warn | SET_CAPTAIN_TARGET (ShipId) |
| NavigationFailure   | bot moved <1 m in 5 s while seeking >7 s (vanilla stuck trigger, research §3.4) | Warn | none — coordination-only |
| FuelCritical        | capsules ≤ 1 / ≤ 2                                    | Crit/Elev   | SET_CAPTAIN_ORDER "1" (at attention; vanilla shopping) |
| CoolantCritical     | coolant ≤ 15% / ≤ 30%                                 | Crit/Elev   | SET_CAPTAIN_ORDER "9" |
| ObjectiveCritical   | exactly 1 objective left on a live mission            | Warn        | none — coordination-only |

Unknown data NEVER triggers: NaN fractions, -1 counts/ids, missing sections,
dead/unknown-alive crew, hostile-free snapshots. Hostility is never assumed —
combat detection reads only the game's authoritative `HostileShips` list (the
Quality-Improver compatibility guarantee: we observe the game's own outcomes,
we never call hostility logic).

**Coordination-only findings (P37, closes live finding L3):** the two rules
with no wired capability (NavigationFailure, ObjectiveCritical — see the
"none — coordination-only" Realization column) never create tasks and never
enter the Active set. They are counted (`CoordinationOnlyNoted`) and logged
(`EmergencyNoted …`) and still drive the state machine, but routing them
through the executor could only end in the bounded fail→retry→cancel churn
observed in the P36 live sessions (349 `no capability bound` events). The
Active set is therefore reserved for capability-backed emergencies — a
coordination-only record can never shed a real emergency out of the bounded
active set.

## 6. Structured EmergencyDecision (bounded immutable data)

`EmergencyId, EmergencyType, Severity, DetectedAtMs, AffectedActor,
AffectedTaskId, Priority, Reason (≤200 chars), RequiredCapability,
TargetReference (≤64 chars), AuthorityRequirement, Preconditions,
ExpirationMs, EscalationState, RecoveryPolicy ("LIFECYCLE")`.

The decision is DATA ONLY — carried, logged, hashed into identities; the
RequiredCapability is advisory: the director does NOT execute it, the executor
validates and dispatches it exactly as for every other task.

## 7. Identity and deduplication

- `EmergencyId = "EID:<TYPE>:<hash8>"` where hash8 is the shared FNV-1a
  (`ActionIdentity.ComputeStableHash`) over type + subject key
  (actor + targetKind:targetRef, key ≤64 chars). Stable across re-evaluations.
- While an emergency is ACTIVE, re-detection **never re-creates tasks**: the
  record's LastSeenMs refreshes and severity may escalate only (one
  `EmergencyEscalated` line; the existing task keeps its lifecycle).
- **No-progress breaker (added P39).** A remediation can resolve WITHOUT
  fixing the condition (live: CoolantCritical + SET_CAPTAIN_ORDER order=9 —
  134 identical re-tasks in one session). When the record is absent
  (resolved) and the SAME severity is re-detected, the per-EmergencyId
  **SuppressionGate** accumulates evidence: at most
  `MaxNoProgressResolutions = 3` bounded attempts, then the identity is
  suppressed — a rate-limited (`SuppressionNotifyMs = 60000`)
  `EmergencySuppressed` line, no task. ANY severity change re-arms the
  breaker (the world moved → fresh bounded attempts are legitimate). The
  gate decays only via the hygiene sweep when re-detections STOP for
  `ActiveExpiryMs` (the condition is actually gone) — re-detections keep it
  warm even while suppressed. Readbacks: `EmergenciesSuppressed`,
  `SuppressionGateCount`; `/capbotstatus` carries `suppressed=` + `gates=`.
- Bounded bookkeeping: active ≤ 8 (`MaxActiveEmergencies`, overflow sheds the
  oldest by LastSeenMs), history ≤ 16 (`MaxHistory`), un-confirmed emergencies
  decay after 30 s (`ActiveExpiryMs`), re-arm delay after a task resolves =
  20 s (`TaskRequeueBlockMs`), task timeout 120 s, maxRetries 1.
- Emergency tasks carry metadata: `EmergencyId`, `EmergencyType`,
  `Preemptible="true"`, `CapabilityId`, `Argument` (when a capability applies).

## 8. Preemption contract

Implemented **only** as the emergency-side contract: emergency tasks are marked
`Preemptible="true"` and carry priorities in the 100+ band, so the P4
scheduler's OWN policy-gated preemption (margin > PreemptMargin=2, victim grace
PreemptMinRunMs=3 s, lifetime cap MaxPreemptionsPerTask=2, same-owner
displacement) displaces lower-priority running work. The director itself never
pauses, fails, or cancels anything. A preempted task remains recoverable: the
scheduler auto-resumes its own pause after PreemptPauseMs=15 s, and
`TryComplete`/`TryCancel` of the emergency task resumes the victim — lifecycle
and recovery (P2/P3) own the victim throughout. (Scheduler redesign: out of
scope, per Phase 9 mandate.)

## 9. Verified PULSAR APIs used (DLL reflection, Assembly-CSharp)

| API | Status |
|-----|--------|
| `PLShipInfo.CountNonNullFires()` | VERIFIED (public) — fire count input |
| `PLShipStats.ReactorTempCurrent / ReactorTempMax` | VERIFIED (public, Obscured-backed) — reactor fraction input |
| `PLShipStats.HullCurrent / HullMax` | VERIFIED — hull fraction (captured by P6 source) |
| `PLPawn.Health / MaxHealth` | VERIFIED (Obscured public) — bot health (captured by P6 source) |
| `PLShipInfoBase.HostileShips` | VERIFIED — authoritative hostile id list |
| `PLShipInfoBase.AlertLevel` | VERIFIED — data only (not a rule input) |
| `PLShipInfo.IsReactorInMeltdown()/IsReactorOverheated()/IsReactorTempCritical()` | VERIFIED — available; the temp-fraction rule uses the continuous value instead |
| `PLShipInfo.InvadersOnboard` | VERIFIED — available for later boarder rules (unused in P9) |
| `PLServer.CaptainSetOrderID(Int32)` [PunRPC] | VERIFIED — executor dispatch channel for order capabilities |
| `PLShipInfoBase.Captain_SetTargetShip(Int32)` [PunRPC] | VERIFIED — executor dispatch channel for SET_CAPTAIN_TARGET |
| Combat-level gap (research §6.6 UI comparison) | INFERRED — data-only severity bound |
| `PLFire` internals | NOT USABLE (only private LastDoneDamageTime) — CountNonNullFires() is the verified counter |
| `PLRepairSystemInstance` | TYPE DOES NOT EXIST (documented; never referenced) |

## 10. World dependencies (Phase 6 additive extension)

Detection reads two new snapshot fields the P6 source fills at capture time:

- `WorldSnapshot.PlayerShipFireCount` (int, **-1 = unknown**) — from
  `PLShipInfo.CountNonNullFires()`, try/catch-guarded (`RecordPartial`).
- `WorldSnapshot.PlayerShipReactorTempFraction` (float, **NaN = unknown**) —
  from `PLShipStats.ReactorTempCurrent/ReactorTempMax`, try/catch-guarded.

The original 16-arg WorldSnapshot constructor is preserved verbatim (both new
fields default to unknown), so every Phase 6–8 caller and test compiles
unchanged. **No separate PulsarEmergencySource was created**: detection rides
the extended P6 source under its existing 1 s throttle and partial-failure
bookkeeping — a second capture path would double game queries per tick.

## 11. Multiplayer authority model

- The driver (Patch.cs WorldTick Postfix) gates ALL emergency work on
  `PhotonNetwork.isMasterClient` — the shipped authority gate
  (Patch.cs:102/Autonomy.cs:26).
- The director additionally consults the authority seam
  (`ExecutionClaims.IsAuthoritative()`), fail-closed: a faulting Photon query
  denies authority. Clients therefore cannot produce emergency tasks, records,
  or state transitions even if a future driver bug calls Evaluate on them.
- No duplicate Photon actions: emergency tasks carry the same identity/dedup
  protections as every other task (P5 claims + P7 claim-probe + duplicate
  execution ledger).

## 12. Performance contract

- No per-frame loop: the driver calls Evaluate + ReconcileTasks per WorldTick
  frame; Evaluate self-throttles to one pass per `MinRecheckMs` (5 s).
- Detection reuses the P6 snapshot already refreshed at 1 Hz — no extra game
  queries, no repeated FindObjectsOfType, no LINQ on hot paths.
- Bounded allocations: findings list capacity 2; the quiet path (no emergencies)
  allocates only the empty list. Bounded collections everywhere (active ≤ 8,
  history ≤ 16).

## 13. Unsupported emergency types (deliberate, documented)

- **WarpFailure** — warp state IS readable (InWarp/WarpChargeStage) but no
  verified rule separates "failing" from "normal charging"; NOT detected.
- **ImminentDeath** — enum member reserved (crew-survival class) but no
  detection rule ships in P9; CriticalCrewHealth covers provable low-health
  crew emergencies.
- No speculative wiring for boarders (InvadersOnboard is verified but unused),
  sector hazards, or any unverified telemetry.

## 14. Failure modes (all fail-safe)

| Situation | Behavior |
|-----------|----------|
| No snapshot / never captured | `EmergencyUncertain` logged, nothing created |
| Snapshot older than 20 s or future-dated | counted (`StaleRejections`), nothing created |
| `!GameStarted` | nothing created |
| Player ship missing from snapshot | detector returns no findings |
| Faulting world provider / authority probe | fail-closed no-op |
| Task creation refused (registry full) | record still created (`EmergencyTaskRefused` line) — prevents re-detection storms |
| Task vanishes from registry | reconcile resolves the record after the re-arm delay |
| Persisting emergency | exactly ONE task total (S20: 20 passes → 1 task, 19 dedups) |

## 15. Test coverage

`tests/EmergencyTests.cs` — 141 checks covering all 25 mandated scenarios
(S1–S25: state transitions, illegal transitions, precedence, dedup, identity
determinism, stale world, invalid targets, authority/capability/claim
rejection, preemption request + recovery, no-storm/bounded-history, QI-safe
hostility, master-only, no duplicate actions, fail-safe) plus per-rule
detection coverage (R1–R8 + fail-safe inputs). Full suite: **806/806 pass**
(665 prior + 141 new). The f8 hook runs after f7; run_tests.ps1 compiles the
three emergency domain files + test file with the rest.