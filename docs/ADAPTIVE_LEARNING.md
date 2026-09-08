# ADAPTIVE LEARNING (Phase 25)

> **Ownership argument (read this first).** The deferral trail assigns the
> "adjust/adaptive" vocabulary to Phase 25: it adjusts personality trait
> VALUES through the P11 `SetPersonality` write path — the sole sanctioned
> personality write channel (docs/CREW_EXPERIENCE.md §9,
> docs/CREW_PERSONALITIES.md §10). Every other layer stays read-only on
> personality. This document is the binding contract for any consumer phase
> (P26+ multiplayer hardening, P28 persistence, P29 status): trait
> maturation is EVENT-DRIVEN off the P10 `ClearTask` funnel, deny-by-default
> on authority, and writes ONLY `CrewPersonalityRegistry`.

## 1. What Phase 25 is

Bounded, deterministic **trait maturation** for crew agents. When an agent's
CrewExperience **level crosses** the level this layer last armed (baseline
armed on the first readable pass — the P22 premise-capture analogue), the
layer performs at most **one** bounded trait adjustment, using the agent's
OWN outcome mix at the crossing moment to pick a direction:

| Outcome mix at crossing | Rule | Trait | Delta |
|---|---|---|---|
| `2*TasksCompleted >= TotalOutcomes` | `completion` | Diligence | +1 |
| `2*(TasksFailed+TasksVanished) >= TotalOutcomes` | `adversity` | Adaptability | +1 |
| neither | none — crossing consumed silently | — | 0 |

Completion outranks adversity by enum order (Diligence precedes
Adaptability); both rules only co-match when cancelled/expired outcomes
dilute the mix, and the silent-consumption path (neutral mix) never
fabricates an adjustment.

## 2. What Phase 25 is NOT

- No task creation, no lifecycle mutation of any kind (P3 owns the
  lifecycle; this layer never calls `CapBotTask.Create`, `TaskRegistry.*`
  mutators, scheduler/recovery/executor/claims/validator/dispatcher).
- No new Harmony patch class (the 11-class ceiling is untouched). **No
  tick driver, no WorldTick block** — integration is the ONE additive
  fail-safe hook in `CrewAgentRegistry.ClearTask` after the P12/P13 hooks,
  fired outside the agent-registry lock with its own try/catch.
- No game reads, no wall-clock reads (`nowMs` is TaskClock semantics passed
  down the funnel), no LINQ, no per-frame work, no LLM, no reflection.
- No config toggle (P18/P22/P23/P24 deterministic-director precedent —
  always-on, authority seam keeps clients inert).
- No consumer of trait VALUES exists in the tree today (P25 research
  verified): traits remain DATA until a consumer phase reads them.

## 3. Authority model (deny-by-default)

`AdaptiveLearningDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative)`
at boot. Null probe / faulting probe / non-authoritative caller ⇒
`NotifyOutcome` is a complete no-op: no record created, no baseline armed,
no write, clients never mature personalities. **Clients are inert by
construction** — the host funnel simply calls and discards the result.

## 4. Data structures

- `LearningRecord` (nested, per agent): `AgentId` (the SAME stable AgentId
  P10/P11/P12 use), `ArmedLevel` (0 = never armed), `CrossingCount`,
  `AdjustCount`, `LastEvalMs`, `LastSeenMs` (-1 = never; hygiene anchor),
  `LastAdjustMs`, `LastAdjustTrait`, `LastAdjustRule`, `UpdateCount`.
- Director state: records dict ≤ `MaxRecords=32` (== agent bound), history
  FIFO ≤ `MaxHistory=16` (expired ids), counters
  (`RecordsCreated/Crossings/Adjustments/Clamped/Refused/Expired/BaselinesArmed`),
  `LastAdjustment` readback.
- Record ids are `LEARN:<agentId>`, reused in log lines and history.

## 5. NotifyOutcome flow (exactly one decision per call)

1. Authority gate (deny-by-default; fault = silent deny).
2. Input validation: P11 id shape (`PersonalityFactory.IsValidAgentId`) +
   P10 static outcome vocabulary (compared, never parsed) → counted
   `Refused` on any violation.
3. Experience readback OUTSIDE the director lock (one-way lock order —
   this layer takes nobody's lock while held): `SnapshotOf` defensive
   copy. Missing record ⇒ refused, never fabricated.
4. Pass 1 under the director lock: hygiene sweep first
   (`SweepExpiredLocked` — records with no outcome past
   `ActiveExpiryMs=30000` decay to history; removals always run, expiry
   lines capped at `MaxPendingLines=4`), then bookkeeping, then the
   1 s evaluation gate (`MinRecheckMs` — outcome storms rate-limited; a
   crossing PERSISTS through rate-limited passes and is decided on a
   later pass), then baseline arm (first readable pass, no adjustment)
   or crossing consumption (one-shot; `ArmedLevel` advances BEFORE the
   write attempt so a failed write cannot re-fire).
   - Registry-full refusal is defensive only (learning cap == experience
     cap == agent cap: 32); deterministic, never a size change.
5. Direction pick (pure fn `PickRule` over the snapshot) — `-1` ⇒ silent
   consumption.
6. Personality read/write OUTSIDE every lock: derive-on-demand
   (`Get`→null⇒`DeriveFor`), `ClampTrait` bounds the delta, trait
   already at the clamp bound ⇒ counted `Clamped`, no write, crossing
   still consumed; `PersonalityFactory.FromValues` builds the matured
   record (explicit source); `SetPersonality` is all-or-nothing —
   identity-integrity/validation refusal ⇒ counted `Refused`, never a
   partial write.
7. Pass 2 under the director lock: finalize record + counters.
8. Emission: deferred lines fired after the final lock release;
   `PersonalityAdjusted LEARN:<id> trait=<T> delta=+1 level=<L> rule=<R>`
   (with the data-until-consumer tag), plus `LearningRecordExpired`
   hygiene lines.

Derive-on-demand note: a first maturation for a never-registered agent
costs two P11 counter events (one assignment + one replacement) — both
visible in P11 diagnostics. A pre-seeded agent costs one replacement.

## 6. Anti-churn structure

One decision per call, one crossing per level, levels bounded 1..10 ⇒
adjustments are structurally rare (max 9 per agent across a whole career
bounded by the 10-level ladder). The 1 s evaluation gate absorbs outcome
storms; the crossing persists through rate-limited passes.

## 7. Log lines (CapBotLog.LEARNING subsystem)

- `PersonalityAdjusted LEARN:<agentId> trait=<T> delta=+1 level=<L> rule=<R>`
- `LearningRecordExpired LEARN:<agentId>`
- `learning LEARN:<agentId> armed=<n> crossings=<n> adjusted=<n> lastTrait=<T> lastRule=<R>` (per-record readback)
- Status: `learningRecords=<n> crossings=<n> adjustments=<n> clamped=<n> armed=<n> refused=<n> expired=<n>` + `lastAdjustment=<...>`

## 8. Additive edits to existing files

| File | Change |
|---|---|
| `CapBot/Core/Crew/CrewAgentRegistry.cs` | `ClearTask`: one additive fail-safe `NotifyOutcome` hook after the P12/P13 hooks (outside the agent lock, own try/catch). |
| `CapBot/Core/Crew/CrewExperience.cs` | ADDITIVE `SnapshotOf` defensive-copy readback (under-lock copy; no torn reads for the one-way lock order; null when absent/invalid). |
| `CapBot/Core/Logging/CapBotLog.cs` | +`LEARNING` const (additive). |
| `CapBot/Mod.cs` | P25 boot block: `LearningLogBridge.Ensure()` + authority seam (`ExecutionClaims.IsAuthoritative`). NO tick driver, NO world seam (event-driven). |
| `CapBot/CapBot.csproj` | +2 Compile entries (`Core\Learning\*`). |
| `tests/run_tests.ps1` | +`AdaptiveLearningDirector.cs` domain + `AdaptiveLearningTests.cs` runner file. |
| `tests/TaskRecoveryTests.cs` | +`AdaptiveLearningTests.Run()` (f24), totals include `AdaptiveLearningTests.LastPassed`. |

## 8b. Additive readback API (CrewExperience.SnapshotOf)

Defensive-copy snapshot readback added additively to
`CrewExperienceRegistry` (the P25 learning layer is the first consumer;
P28 persistence / P29 status are future consumers):

```csharp
public static CrewExperienceRecord SnapshotOf(string agentId)
```

Returns a COPY taken under the registry lock — no torn reads; `null` when
absent or invalid id; never a live reference, never a fabricated record.

## 9. Reproducibility contract gap (documented, deliberate)

A matured personality is **explicit-source** — it no longer rebuilds
identically from agent identity after a registry reset or host change
(`PersonalityFactory.Derive` is a pure function; explicit records are
not). Process-local by design; cross-session persistence of matured
personalities is **P28's assignment** (persistence), not this phase's.
P26 (multiplayer hardening) must treat matured (explicit-source)
personalities as master-authoritative data that does not survive host
migration by construction — same status as crew experience today.

## 10. Performance characteristics

Zero per-frame work: the only entry point is the funnel call (bounded,
rate-limited 1 s per record, ≤32 records, lock-hold bounded to dictionary
operations). No LINQ, no allocations on the no-op paths (authority deny,
input refusal). Experience readback is a bounded 12-field copy.

## 11. Tests

`tests/AdaptiveLearningTests.cs` LEARN01–LEARN10 (≈120 assertions):
baseline arm + end-to-end crossing, direction rules incl. precedence and
silent consumption, deny-by-default (null/faulting/non-authoritative
probe), invalid inputs (id shape/outcome vocabulary/no record),
rate-limit + crossing persistence, one-shot per crossing + clamp-bound
consumption, multi-agent isolation + bounded set + hygiene decay/re-arm,
funnel fail-safety (listener fault + experience fault isolation),
determinism + data-only proof (pipeline untouched: registry/claims/
recovery/claims counters, task state, experience record untouched by a
learning-only pass) + post-reset inert, real Sync funnel end-to-end +
sequential crossings + P11 counter shape.

## 12. Test-design gotchas (for future suites)

- `CrossingCount`/`AdjustmentCount` etc. are cumulative director globals —
  sub-scenarios sharing one `FreshSetup()` scope must account for prior
  crossings (LEARN02a→02b lesson; insert a `FreshSetup()` between
  sub-scenarios that assert globals).
- Hygiene sweep runs BEFORE the refresh in the same pass: a stale record
  whose agent reports an outcome in the same pass is swept AND re-created
  fresh (`ExpiredCount` counts it). Re-arm level = the agent's CURRENT
  experience level (2 completions = 4 xp = level 1), not the pre-sweep
  armed level.
- `CapBotTask` exposes `TaskType` (not `Type`) — `AssignTask` arg.
- The funnel (real Sync path) requires: agent visible in a published
  snapshot → `AssignTask` → advance past the task-vanish grace → `Sync`.