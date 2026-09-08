# Crew Personalities (Phase 11)

Status: **IMPLEMENTED** (Phase 11; lifecycle population added in Phase 40).
The personality layer is BOUNDED DATA ONLY:
five fixed traits per agent, derived deterministically from the agent's stable
identity, with static archetypes and role-affinity scores. It is not learning,
not memory, not decision making, and it never influences the scheduler,
recovery, claims, capabilities, executor, or PULSAR world state. Consumers
(role preferences, planning, decision weighting) are LATER phases and read
this data through the lookup surface defined here.

Files: `CapBot/Core/Crew/CrewPersonality.cs` (traits + factory + archetypes +
affinity + registry), `CapBot/Core/Crew/PersonalityLogBridge.cs` (logging
bridge). No Harmony patch is added or extended (still 11 patch classes; the
WorldTick postfix is byte-identical to Phase 10).

## Phase 40 lifecycle population (the zero-personalities fix)

Through P39 the registry was populated only lazily — and no production
caller existed that would trigger derivation for a fresh crew, so live
status showed `Personalities: 0` with active agents. Since P40 the
population is **lifecycle-driven and idempotent** (details in
`docs/LIVE_VALIDATION.md` P40 verdict; hooks live in `CrewAgentRegistry`):

- `CrewPersonalityRegistry.EnsureFor(agentId, nowMs)` — create-if-absent
  via the same deterministic derivation; returns the existing record on
  re-presentation. No duplicates ever, by construction.
- Agent create hook + sync-tail reconcile in `CrewAgentRegistry`
  (outside the registry lock, fail-safe, ≤32 ensures per 1s cadence):
  every live agent ends with exactly one record. The reconcile's removal
  pass runs FIRST and removes only **derived** records of removed agents
  (explicit/neutral/matured records survive agent churn by design).
- Role change: record identity/traits/archetype are stable (pure function
  of the AgentId); a `PersonalityReconciled` line is logged and role
  affinity is read per-lookup. Nothing is re-derived.
- Restore window: `CrewPersistence.IsRestoring` gates the reconcile so a
  post-restore sync never re-derives over restored matured rows; missing
  records are re-ensured only after restore completes.
- Diagnostic vocabulary: `PersonalityCreated`, `PersonalityAssigned`,
  `PersonalityReconciled`, `PersonalityRemoved`, `PersonalityRestored`.
- Visibility: `/capbotstatus personalities` (P40 focused-section argument)
  prints the bounded personality section directly.

The data-only contract is unchanged and re-proven: P40-10 shows task
scheduling bit-identical with personality records present; traits still
have no behavioral consumer (`RoleAffinity` remains a future-phase anchor).

## 1. What a personality IS (and is not)

A `CrewPersonality` is an immutable, bounded record for ONE agent (keyed by
the Phase 10 stable `AgentId`, `"AGT:<hash8>"`):

| Field | Meaning |
|---|---|
| `AgentId` | owner identity (must match the registry key exactly) |
| 5 traits | `Discipline`, `Boldness`, `Sociability`, `Diligence`, `Adaptability` — integers 0..100 (50 = neutral) |
| `Archetype` | static vocabulary token (see §3), never invented at runtime |
| `Source` | `"derived"` / `"explicit"` / `"neutral"` |
| `DerivedTimeMs` | record creation stamp (TaskClock semantics, data only) |

Hard boundaries:

- **DATA ONLY.** Nothing in this layer reads PULSAR state, stores game-object
  references, creates/claims/executes tasks, or touches the scheduler,
  recovery, claims, capabilities, or executor. P09 of the test suite proves
  scheduler grants, owner-busy gating, claim outcomes, and priorities are
  bit-identical with personality churn happening around them.
- No tick driver, no world reads, no per-frame cost. Derivation is on-demand
  (a lookup convenience may lazily derive; nothing runs on a cadence).
- Traits are read-only (`Get(trait)`); out-of-range trait READS return −1
  (invalid query), never a fabricated value. The trait vocabulary is closed:
  no trait is ever invented at runtime.

## 2. Deterministic derivation (the "individual personality" source)

Each agent's personality is a pure function of its stable identity:

```
traitHash(agentId, SALT) = ActionIdentity.ComputeStableHash("P|" + agentId + "|" + SALT)
traitValue               = ((hash >> 24) & 0xFF) * 100 / 255   // 0..100
```

- Salts are the fixed strings `DISCIPLINE`, `BOLDNESS`, `SOCIABILITY`,
  `DILIGENCE`, `ADAPTABILITY`.
- The same crew member therefore ALWAYS gets the same personality — across
  rejoins, class changes, name changes, agent-record removal/recreation, and
  registry resets (test P10 proves cross-round identity with a cleared
  registry). No randomness, no wall clock, no session drift.
- The hash primitive is the same FNV-1a used by AgentId and EmergencyIdentity
  (never `string.GetHashCode`, which is not platform-stable).
- Distinctness: identity-derived trait vectors differ across distinct agents
  (test P01 samples 40 agents and requires ≥35 distinct signatures).

`PersonalityFactory.FromValues` (explicit, clamped 0..100) and `.Neutral`
(all-50) exist as bounded alternate sources for later phases (hand-authored
or persisted personalities). The registry never silently replaces: `DeriveFor`
returns the EXISTING record; replacement happens only through the explicit
`SetPersonality` API and is counted.

## 3. Archetypes (static vocabulary)

A trait is **dominant at ≥ 70** (`PersonalityArchetypes.DominantThreshold`);
ties resolve to the FIRST trait in `PersonalityTrait` enum order (fixed,
deterministic). No dominant trait ⇒ `BALANCED`.

| Archetype | Dominant trait |
|---|---|
| `SENTINEL` | Discipline |
| `VANGUARD` | Boldness |
| `COORDINATOR` | Sociability |
| `TECHNICIAN` | Diligence |
| `ADAPTER` | Adaptability |
| `BALANCED` | none ≥ 70 |

Archetype tokens are compared byte-for-byte or logged — never parsed or
dispatched on.

## 4. Role affinity (deterministic preference anchor)

`RoleAffinity.Score(personality, role)` → integer 0..100: a fixed per-role
weight table (summing to exactly 100) applied to the trait vector with plain
integer math. Weights per role (Discipline, Boldness, Sociability, Diligence,
Adaptability):

| Role | Weights | Rationale (bounded design choice, data only) |
|---|---|---|
| Captain | 40/20/30/5/5 | discipline + people-orientation |
| Pilot | 20/30/10/5/35 | boldness + adaptability |
| Scientist | 25/5/10/40/20 | diligence-first |
| Weapons | 30/40/5/20/5 | boldness-first |
| Engineer | 30/5/5/45/15 | diligence-first |
| Unknown/Other | 20/20/20/20/20 | uniform — no preference expressible |

- Weight tables are returned as DEFENSIVE COPIES (test P03 proves callers
  cannot mutate the static tables).
- Null personality ⇒ −1 (invalid query, never a fabricated score).
- A neutral (all-50) personality scores exactly 50 for every role.
- Affinity is DATA for later phases (role preferences, task-assignment
  weighting). Nothing here dispatches, schedules, or executes; the P4
  scheduler, P7 capability gates, and P8 dispatcher are untouched.

## 5. Registry

`CrewPersonalityRegistry` — bounded, keyed by the same stable `AgentId` the
P10 `CrewAgentRegistry` uses (identity alignment is proven in test P01: the
id derived via `CrewAgentRegistry.MakeAgentId` resolves the personality
derived for the crew member the agent sync created).

- Bounds: ≤ `MaxPersonalities` (32) records — matching `CrewAgentRegistry.
  MaxAgents`; deterministic refusal when full (logged `PersonalityRefused`);
  explicit `SetPersonality` replacement at cap is allowed (no size change).
- Identity integrity: `SetPersonality` requires `personality.AgentId ==
  agentId` (mismatched records refused, counted); archetype tokens ≤ 32 chars
  (overlong refused); agent ids must be the canonical `"AGT:" + 8 hex` shape
  (exactly 12 chars).
- Lookups: `Get` (null when absent — no lazy fabrication), `DeriveFor`
  (derive-or-existing), `AffinityToRole` / `ArchetypeOf` (derive-on-demand
  conveniences), `Remove` (future-phase lifecycle hook), `Lines`/`StatusLines`
  (bounded diagnostics).
- Listener (`SetDecisionListener`) fires OUTSIDE the lock — same discipline
  as every prior registry; `PersonalityLogBridge` attaches CapBotLog (CREW)
  at boot.
- `ResetForTests` clears records, counters, and the listener (test/dev
  isolation only).

## 6. Authority + multiplayer

The layer performs no PULSAR calls, holds no game references, and produces no
networked state — it is process-local data like every other CapBot domain
structure. Client/host authority is irrelevant to it by construction (no
authoritative action exists in the layer). Personality data would be rebuilt
identically on any host after an authority change because derivation is a
pure function of the stable AgentId.

## 7. Performance

- Zero per-frame cost: no tick driver, no timers, derivation is on-demand
  and O(1) after first derivation (cached in the bounded registry).
- No LINQ, no scene scans, no FindObjectsOfType, no allocations on read
  paths (`Get`/`Score` are pure integer math), defensive copies only on
  the diagnostic `Weights()` accessor.
- All collections bounded (registry ≤32; trait arrays fixed at 5).

## 8. Verified PULSAR APIs used

**None.** The personality layer performs zero PULSAR calls in its own code
path — it is pure C# over the Phase 10 agent identity (`ActionIdentity.
ComputeStableHash`). No API verification status beyond P10's existing
verified set is consumed. (The research doc's `PLAIIO`/AIData profile
channel was deliberately NOT used: it would touch the vanilla AI brain-swap
surface and vanilla save files — out of scope for an additive, bounded,
data-only layer.)

## 9. Security

Trait vectors, archetypes, and sources are static-vocabulary data. No
generated executable code, no runtime compilation, no DLL loading, no
shell/process execution, no arbitrary reflection execution, no LLM. The
registry validates id shape and identity match on every write; nothing is
parsed into code paths.

## 10. Future integration points (NOT implemented in Phase 11)

- Role preferences / task-assignment weighting (later phases) read
  `RoleAffinity.Score` / `ArchetypeOf`.
- Experience (Phase 12) may adjust trait VALUES through `SetPersonality`
  (the explicit-source path exists for exactly this) — Phase 11 itself
  derives static values and implements no experience.
- Memory (Phase 13) anchors on the same stable AgentId keys.
- A future director may present personalities via the status/dashboard
  phases through `Lines()`/`StatusLines()`.

## 11. Failure modes

| Failure | Behavior |
|---|---|
| Invalid agent id (null/empty/wrong shape/overlong) | Factory returns null; registry refuses + counts |
| Record/registry key mismatch | Refused (identity integrity) |
| Overlong archetype | Refused |
| Out-of-range trait value | Clamped to 0..100 at construction |
| Out-of-range trait READ | −1 (invalid query) |
| Null personality in affinity query | −1 (never fabricated) |
| Registry full (32) | Derivation refused deterministically, logged; explicit replacement still allowed |
| Faulting listener | Exception propagates to caller of Emit only after lock release; registry state unaffected |

## 12. Tests

`tests/PersonalityTests.cs` (116 assertions, suite f10 in
`tests/TaskRecoveryTests.cs` TestMain; total suite now 1030):

P01 identity-derived determinism + individuality + P10 identity alignment ·
P02 archetype assignment (all six tokens, tie-first, threshold edge) ·
P03 role affinity (role-differentiated preferences, bounded scores, weight
sums, table immutability, neutral=50, null refused, convenience parity) ·
P04 trait clamping + invalid-id/record refusal + invalid trait reads ·
P05 explicit registration (assign/replace counted, size stable, emitted) ·
P06 neutral source + remove lifecycle + bounded diagnostics ·
P07 registry stability across time · P08 no cross-agent contamination (incl.
bot/human distinctness) · P09 scheduler/claims isolation (personality churn
changes nothing: grants, owner-busy gate, claim results, priorities) ·
P10 no cross-round drift (registry cleared, derivation identical) ·
P11 bounded registry (cap refusal, replacement-at-cap, slot freeing) ·
P12 no invalid-data ingestion (garbage ids, forged archetypes, stolen
records, null/empty removals).