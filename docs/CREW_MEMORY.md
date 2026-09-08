# Crew Memory (Phase 13)

Status: **IMPLEMENTED** (Phase 13). The memory layer is BOUNDED RECALLABLE
DATA ONLY: per-agent rings of fact entries (location observations, task
outcomes, crew events) keyed by the same stable AgentId as the P10/P11/P12
registries. It is not learning, not personality adjustment, not planning,
not decision making, and it never influences the scheduler, recovery,
claims, capabilities, executor, experience, or PULSAR world state.

Files: `CapBot/Core/Crew/CrewMemory.cs` (entry + registry + recall paths),
`CapBot/Core/Crew/MemoryLogBridge.cs` (logging bridge). The only
pre-existing code change is the additive memory hook in
`CrewAgentRegistry.ClearTask`. No Harmony patch is added or extended (still
11 patch classes; WorldTick Postfix byte-identical to Phase 10/11/12).

## 1. What a memory entry IS (and is not)

A `CrewMemoryEntry` is a bounded fact row for ONE agent (keyed by the
stable `AgentId`):

| Field | Meaning |
|---|---|
| `AgentId` / `Kind` / `TaskId` | owner identity + static kind vocabulary + task-scoped key (readonly) |
| `CreatedTimeMs` | first observation stamp (readonly) |
| `Text` | data-only payload (location name / crew-event text), null = none |
| `Outcome` | TaskOutcome payload (Phase 10 static vocabulary), null = none |
| `LastSeenMs` | last write or recall stamp |
| `UpdateCount` | bounded diagnostics (writes + recalls) |

Hard boundaries:

- **DATA ONLY.** Nothing here reads PULSAR state, stores game-object
  references, creates/claims/executes tasks, or touches the scheduler,
  recovery, claims, capabilities, executor, experience, or personality
  records (test M08 proves scheduler grants, owner-busy gating, claim
  results, priorities, task state, personality records, and experience
  records are bit-identical under memory churn).
- **The P6 snapshot remains the ONLY authoritative world observation.**
  Location memory is written through the explicit `RememberLocation` API —
  no snapshot-path changes, no duplicated world data. The P10
  `LastKnownTLIName` cache and P13 location facts are both subordinate
  caches; later phases may feed them from the same observation points.
- Memory is not yet consumed by any consumer: recall APIs exist for later
  phases (role preferences, planning, Captain Brain 2.0). Nothing in this
  phase reads memory to make a decision.
- No tick driver, no world reads, no per-frame work: writes happen only on
  task resolution (gated by the P10 1-second sync cadence) or on explicit
  API calls.

## 2. Write paths

- `RememberTaskOutcome` — the single Phase 13 funnel wiring:
  `CrewAgentRegistry.ClearTask` calls it AFTER the agent lock is released,
  in its own `try/catch`, next to the Phase 12 experience hook — **a
  faulting memory layer can never affect agent state or task resolution**
  (test M10 proves the funnel completes and the agent records its outcome
  even when the memory listener throws or the registry is at cap).
- `RememberLocation` — explicit API (future location providers call it);
  never fabricates a location, refuses empty/overlong text.
- `RememberCrewEvent` — explicit API for arbitrary crew-event text;
  text is DATA ONLY (compared, never parsed or dispatched on).

## 3. Upsert + eviction semantics (deterministic)

- Keys: TaskOutcome = `(Kind, TaskId)`; Location = `(Kind, Text)`;
  CrewEvent = `(Kind, Text)`. Kinds never collide (same text across kinds
  stays two rows — test M12).
- Same key → update in place: `Outcome`/`Text` refreshed, `LastSeenMs`
  stamped, `UpdateCount` incremented, NO eviction. For task outcomes the
  later resolution wins deterministically (test M12).
- New distinct fact with the ring full (8 entries) → evict the OLDEST
  entry by `LastSeenMs` (tie → lowest insertion index), bounded scan
  (≤8). The 8-entry cap is per agent across kinds (one ring per agent).
- Evicted facts are gone permanently (no resurrection after ForgetAgent —
  test M11).

## 4. Recall paths (the only reads)

- `Recall(agentId, kind, text)` / `RecallTaskOutcome(agentId, taskId)` —
  lookup by key; stamps the entry (`LastSeenMs = now`, `UpdateCount++`)
  so eviction favors recently-used facts. Stamps come from the clock seam
  (`SetNowMsProvider`, production: `TaskClock.NowMs`; unset → 0, never a
  wall-clock read). The returned entry is the live domain object (no
  fabrication, null when absent).
- `RecallAll(agentId, kind)` — bounded list in insertion order (oldest →
  newest), read-only (no recall stamps).
- `ForgetAgent(agentId)` — deterministic full forget (agent-removal
  lifecycle hook; the P10 removal pass is a later-phase consumer).

## 5. Registry

`CrewMemorySystem` — bounded, keyed by the same stable `AgentId`
(id validation reuses `PersonalityFactory.IsValidAgentId` — exact
`"AGT:" + 8 hex` shape).

- Bounds: ≤ `MaxAgents` (32) agents — matching the P10/P11/P12 bounds;
  ≤ `MaxMemoriesPerAgent` (8) entries per agent; `MaxTextLen` (32) for
  payloads (same rule as crew names/TLI names). Deterministic refusal
  only when the registry itself is full of NEW agents (logged
  `MemoryRefused`); existing agents keep writing at cap (test M05);
  `ForgetAgent` frees slots.
- Diagnostics: `Lines()` (one bounded line per entry, deterministic
  order), `StatusLines()` (single summary), `StatsOf(agentId)`, counter
  properties (`WriteCount`/`RecallCount`/`EvictionCount`/`RefusedCount`).
  Listener lines are collected under the lock and fired AFTER it is
  released — a listener can never run while the registry lock is held.
  `MemoryLogBridge` attaches CapBotLog (CREW) at boot; `ResetForTests`
  clears everything.

## 6. Authority + multiplayer

The layer performs no PULSAR calls, holds no game references, and
produces no networked state — process-local data like every other CapBot
domain structure. Task-outcome memory accrues only from authoritative
host-side task resolution (the ClearTask funnel is host-only under the P10
authority gate); location/event facts are written only through explicit
APIs. After any host change, memory rebuilds from continued observation;
nothing user-visible is networked.

## 7. Performance

- Zero per-frame cost: the only automatic write path is task resolution
  (already gated by the 1 s sync cadence); no tick driver, no timers.
- No LINQ, no scene scans, no allocations on read paths
  (`Recall`/`RecallTaskOutcome`/`MemoryCountOf` are dictionary + bounded
  list scans over ≤ 8 entries).
- All collections bounded (registry ≤32 agents, ring ≤8 entries, text
  ≤32 chars; counters are primitives). Eviction scan is ≤8 comparisons.

## 8. Verified PULSAR APIs used

**None.** Pure C# over the Phase 10 funnel + explicit APIs. No API
verification status beyond prior phases is consumed; nothing invented.

## 9. Security

Memory payloads are DATA ONLY (static-vocabulary outcomes; bounded text
never parsed or dispatched on). No generated executable code, no runtime
compilation, no DLL loading, no shell/process execution, no arbitrary
reflection execution, no LLM. The registry validates id shape, outcome
vocabulary, task-id sign, and text length on every write; the vocabulary
is compared, never parsed or dispatched on.

## 10. Future integration points (NOT implemented in Phase 13)

- Role preferences / planning / Captain Brain 2.0 (later phases) may read
  memory through `Recall`/`RecallAll` — Phase 13 implements no consumer.
- Navigation recovery (Phase 14) may record/recall last-known locations —
  `RememberLocation` is the sanctioned write path.
- Persistence (Phase 28) may serialize the bounded rings — entries are
  plain bounded data rows.
- Agent-removal lifecycle (a future CrewAgentRegistry hook) may call
  `ForgetAgent` — deliberately not wired in this phase.
- The P6 snapshot's crew `CurrentTLIName` and the P10
  `LastKnownTLIName` cache may feed `RememberLocation` from a later
  provider — deliberately not wired in this phase.

## 11. Failure modes

| Failure | Behavior |
|---|---|
| Invalid agent id | Refused + counted (no memory created) |
| Unknown outcome vocabulary / non-positive taskId | Refused + counted |
| Empty/overlong/null text | Refused + counted |
| Registry full (32 agents) | New-agent writes refused deterministically, logged; existing agents keep writing |
| Ring full (8 entries) | New distinct fact evicts the oldest by LastSeenMs (tie → lowest index); updates never evict |
| Faulting memory listener | Caught in the funnel; agent state and task resolution unaffected |
| Forget of absent agent | Refused (false) |
| Clock seam unset/faulting | Recall stamps 0 (never a wall-clock read) |

## 12. Tests

`tests/MemoryTests.cs` (170 assertions, suite f12 in
`tests/TaskRecoveryTests.cs` TestMain; total suite now 1308):

M01 end-to-end outcome memory through the real Sync funnel · M02 location
upsert + read stamping · M03 crew-event memory (text as DATA) · M04
bounded ring (8/agent, cross-kind oldest-by-LastSeenMs eviction, updates
never evict) · M05 bounded registry (32-agent cap, refusal, slot freeing,
existing-agent writes at cap) · M06 no invalid-data ingestion · M07 no
cross-agent contamination (incl. bot/human) · M08
scheduler/claims/priority/personality/experience isolation under memory
churn · M09 recall stamping via the clock seam + recall-favored eviction ·
M10 fail-safe funnel (throwing listener, full-registry refusal — agent
state and task resolution unaffected) · M11 ForgetAgent lifecycle +
no resurrection · M12 upsert determinism (keys, kinds, later-outcome
wins, stats).