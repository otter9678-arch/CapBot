# Crew Experience (Phase 12)

Status: **IMPLEMENTED** (Phase 12). The experience layer is BOUNDED DATA ONLY:
per-agent outcome counters and experience points accrued from the Phase 10
task-resolution funnel, with a deterministic level (1..10) as a pure function
of accrued XP. It is not learning, not memory, not personality adjustment
(that is Phase 25), not decision making, and it never influences the
scheduler, recovery, claims, capabilities, executor, or PULSAR world state.

Files: `CapBot/Core/Crew/CrewExperience.cs` (levels + record + registry),
`CapBot/Core/Crew/ExperienceLogBridge.cs` (logging bridge). The only
pre-existing code change is the additive accrual hook in
`CrewAgentRegistry.ClearTask`. No Harmony patch is added or extended (still
11 patch classes; WorldTick Postfix byte-identical to Phase 10/11).

## 1. What an experience record IS (and is not)

A `CrewExperienceRecord` is a bounded mutable record for ONE agent (keyed by
the stable `AgentId`):

| Field | Meaning |
|---|---|
| `AgentId` / `CreatedTimeMs` | owner identity + record birth stamp (readonly) |
| `TasksCompleted/Cancelled/Expired/Vanished/Failed` | per-outcome counters |
| `TotalOutcomes` | every accrued outcome |
| `ExperiencePoints` | accrued XP (long) |
| `Level` | derived 1..10 (pure function of XP) |
| `LastOutcome` / `LastResultMs` | last resolved outcome + stamp (data only) |
| `UpdateCount` | bounded diagnostics |

Hard boundaries:

- **DATA ONLY.** Nothing here reads PULSAR state, stores game-object
  references, creates/claims/executes tasks, or touches the scheduler,
  recovery, claims, capabilities, executor, or personality records (test X10
  proves scheduler grants, owner-busy gating, claim results, priorities, task
  state, and personality records are bit-identical under experience churn).
- Experience does NOT adjust personality traits in this phase — trait
  adjustment is deliberately deferred to adaptive learning (Phase 25); the
  P11 `SetPersonality` write path remains the sanctioned channel.
- No tick driver, no world reads, no per-frame work: accrual happens only
  when an agent's task assignment resolves, which is itself gated by the
  P10 1-second sync cadence.

## 2. Accrual funnel (the only write path)

`CrewAgentRegistry.ClearTask(agentId, outcome, nowMs)` is the Phase 10 funnel
every task resolution passes through (terminal task observation during Sync,
explicit `ClearTask` calls). Phase 12 extends it ADDITIVELY:

- After the agent lock is released (listener discipline: nothing user-added
  runs inside the registry lock), the registry calls
  `CrewExperienceRegistry.RecordOutcome(agentId, outcome, nowMs)` inside a
  `try/catch` — **a faulting experience layer can never affect agent state or
  task resolution** (test X11 proves the funnel completes and the agent
  records its outcome even when the experience listener throws or the
  registry is at cap).
- Exactly-once semantics: accrual happens only when `ClearTask` actually
  cleared an assignment (unknown agent / no assignment / double clear are
  refused BEFORE any accrual — test X12).

## 3. Points and levels (deterministic)

Outcome vocabulary is the Phase 10 static set; points are fixed constants:

| Outcome | Points |
|---|---|
| COMPLETED | 10 (`PointsCompleted`) |
| CANCELLED / EXPIRED / VANISHED / FAILED | 2 (`PointsOther`) |
| anything else | −1 → refused, never fabricated (test X03) |

Level thresholds are cumulative and fixed
(`ExperienceLevels.Thresholds` = 0, 50, 120, 220, 350, 510, 710, 950, 1230,
1550):

- `LevelForXp` is a bounded pure function (max 10 levels); negative/zero XP →
  level 1; XP beyond 1550 stays at level 10 (bounded, never invented).
- Level is re-derived after every accrual (test X02/X05: exact thresholds,
  monotonicity, cap, and level-crossing through the real accrual path).

## 4. Registry

`CrewExperienceRegistry` — bounded, keyed by the same stable `AgentId` as the
P10/P11 registries (id validation reuses `PersonalityFactory.IsValidAgentId` —
exact `"AGT:" + 8 hex` shape).

- Bounds: ≤ `MaxRecords` (32) records — matching the P10/P11 bounds.
  Deterministic refusal when full (logged `ExperienceRefused`); existing
  records keep accruing at cap (only NEW records are refused — test X08);
  `Remove` frees slots for future lifecycle integration (P10 agent-removal
  hooks are a later-phase consumer).
- Records are created lazily on the first accrued outcome; `Get` never
  fabricates (null when absent); `LevelOf` returns 0 for absent/invalid ids.
- Diagnostics: `Lines()` (one bounded line per record, deterministic order)
  and `StatusLines()` (single summary) feed the dashboard phase; listener
  fires outside the lock; `ExperienceLogBridge` attaches CapBotLog (CREW) at
  boot; `ResetForTests` clears records, counters, and listener.

## 5. Authority + multiplayer

The layer performs no PULSAR calls, holds no game references, and produces no
networked state — process-local data like every other CapBot domain structure.
Experience records would be rebuilt by continued observation after any host
change; nothing user-visible is networked.

## 6. Performance

- Zero per-frame cost: accrual is event-driven (only on task resolution,
  already gated by the 1 s sync cadence); no tick driver, no timers.
- No LINQ, no scene scans, no allocations on read paths (`Get`/`LevelOf`/
  `PointsForOutcome` are dictionary/array lookups).
- All collections bounded (registry ≤32; counters are primitives).

## 7. Verified PULSAR APIs used

**None.** Pure C# over the Phase 10 funnel. No API verification status
beyond prior phases is consumed; nothing invented.

## 8. Security

Counters and outcome strings are static-vocabulary data. No generated
executable code, no runtime compilation, no DLL loading, no shell/process
execution, no arbitrary reflection execution, no LLM. The registry validates
id shape and outcome vocabulary on every write; the vocabulary is compared,
never parsed or dispatched on.

## 9. Future integration points (NOT implemented in Phase 12)

- Adaptive learning (Phase 25) may adjust personality trait values using
  `Level`/`ExperiencePoints` as inputs through the P11 `SetPersonality`
  write path — Phase 12 implements no trait change.
- Persistence (Phase 28) may serialize the bounded counters — the record is
  already a plain bounded data structure.
- Role preferences (later phases) may combine `LevelOf` with P11
  `RoleAffinity`.
- Agent-removal lifecycle (a future CrewAgentRegistry hook) may call
  `CrewExperienceRegistry.Remove` — deliberately not wired in this phase.

## 10. Failure modes

| Failure | Behavior |
|---|---|
| Invalid agent id | Refused + counted (no record created) |
| Unknown outcome vocabulary | Refused + counted (no points guessed) |
| Registry full (32) | New-record accrual refused deterministically, logged; existing records keep accruing |
| Faulting experience listener | Caught in the funnel; agent state and task resolution unaffected |
| Remove of absent record | Refused (false) |
| Accrual never happens twice for one resolution | ClearTask refuses no-assignment/double-clear before accrual |

## 11. Tests

`tests/ExperienceTests.cs` (108 assertions, suite f11 in
`tests/TaskRecoveryTests.cs` TestMain; total suite now 1138):

X01 end-to-end accrual through the real Sync funnel · X02 deterministic level
math (thresholds, cap, negative) · X03 points vocabulary + unknown refusal ·
X04 per-outcome counters + stamps + bounded diagnostics · X05 level crossing
(60→110→120 xp, level 1→2→3) · X06 registry stability + Remove lifecycle +
fresh record · X07 no cross-agent contamination (incl. bot/human) · X08
bounded registry (cap refusal, existing-record accrual at cap, slot freeing) ·
X09 no invalid-data ingestion · X10 scheduler/claims/priority/personality
isolation under experience churn · X11 fail-safe funnel (throwing listener,
full-registry refusal — agent state and task resolution unaffected) · X12
ClearTask funnel regression (P10 semantics intact, exactly-once accrual).