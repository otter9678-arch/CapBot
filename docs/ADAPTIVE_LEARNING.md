# Phase 25 — Adaptive Learning (trait maturation)

Status: implemented + verified (see "Verification"). Data-layer phase; no
behavior change in the game yet — traits remain DATA until a consumer phase
reads them.

## Design basis (verified constraint trail)

No master-plan document exists in the workspace defining Phase 25 numeric
rules. The design is inferred from the verified deferral trail (P25 research
report; every hit names inputs + write path, none name numbers):

1. **Inputs:** `CrewExperience` Level/ExperiencePoints per agent
   (docs/CREW_EXPERIENCE.md), read through the same ClearTask funnel that
   feeds P12 accrual.
2. **Write path:** `CrewPersonalityRegistry.SetPersonality` — the sole
   sanctioned personality write channel (docs/CREW_PERSONALITIES.md §10),
   with records built by `PersonalityFactory.FromValues` (the explicit-source
   path the P11 contract designates for exactly this).
3. **Trigger:** a LEVEL CROSSING — the level on the experience record rises
   past the level this layer last armed. Naturally rare (one per level,
   levels bounded 1..10), so anti-churn is structural.
4. **Direction:** deterministic first-match rule over the agent's OWN
   outcome mix at the crossing moment:
   - completion-dominant (`2*TasksCompleted >= TotalOutcomes`) → `+1 Diligence`
   - adversity-dominant (`2*(TasksFailed+TasksVanished) >= TotalOutcomes`) → `+1 Adaptability`
   - otherwise → no justified direction; the crossing is consumed silently
     (never fabricate an adjustment).
   Completion outranks adversity by precedence.
5. **Consumers:** none today — nothing in the tree reads trait values
   (verified). A maturation write changes diagnostics only; the same
   data-layer-before-consumer pattern as P15→P18 and P22→P23.

## Integration

Event-driven off the **P10 ClearTask funnel**: one additive fail-safe hook
after the P12/P13 hooks in `CrewAgentRegistry.ClearTask`, fired OUTSIDE the
agent-registry lock with its own try/catch. **No new Harmony patch class**
(the 11-class ceiling is untouched), no tick driver, no WorldTick block.

Boot wiring (Mod.cs): `LearningLogBridge.Ensure()` +
`AdaptiveLearningDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative)`.

## Boundaries (contract)

- Writes ONLY `CrewPersonalityRegistry` (via `SetPersonality`). Never
  creates/queues/claims/cancels/executes tasks, never calls
  TaskScheduler/TaskRecoveryManager/TaskExecutor/ExecutionClaims/
  CapabilityRegistry/DecisionValidator, never touches TaskRegistry, never
  mutates another phase's statics.
- `CrewExperience` is READ ONLY here (`SnapshotOf` defensive copy); the
  learning layer never accrues, removes, or fabricates experience.
- Deny-by-default authority: with no probe, a faulting probe, or a
  non-authoritative caller, `NotifyOutcome` is a no-op (clients inert;
  host funnel simply no-ops).
- No game reads, no wall-clock reads (`nowMs` is TaskClock semantics passed
  down the funnel), no LINQ, no per-frame work, no LLM.
- Every collection is bounded; every input validated (P10 outcome
  vocabulary compared, never parsed); same inputs ⇒ same decisions.
- One-way lock order: this layer takes nobody's lock while holding its own;
  personality read/write happens with zero locks held.

## Bounded knobs

| Constant | Value | Meaning |
| --- | --- | --- |
| `MinRecheckMs` | 1000 | evaluation gate per record (crossings persist through it) |
| `MaxRecords` | 32 | bounded learning records (= agent/personality/experience bound) |
| `MaxHistory` | 16 | bounded expired-record id history |
| `ActiveExpiryMs` | 30000 | no outcome for this long ⇒ record decays (re-arms on next outcome) |
| `MaxPendingLines` | 4 | bounded emission queue per pass (house bound) |
| `AdjustmentDelta` | 1 | fixed trait nudge per crossing (ClampTrait bounds the ceiling) |
| `TrackIdPrefix` | `LEARN:` | record ids are `LEARN:<agentId>` |

## Decision semantics

Exactly one crossing decision per `NotifyOutcome` call, consumed under the
director lock (single-mutator) so concurrent callers can never
double-adjust one crossing:

- **Baseline arm:** first readable pass for an agent arms
  `ArmedLevel = current level`, observation-free (the P22 premise-capture
  analogue). No adjustment on the arm pass.
- **No crossing** (`Level <= ArmedLevel`): accepted, silent.
- **Crossing:** consumed exactly once regardless of rule outcome
  (one-shot). Direction by the rules above; a crossing with no justified
  direction is consumed silently (never fabricated).
- **Clamp bound:** a crossing whose trait sits at the clamp bound performs
  NO write (counted as `Clamped`, never fabricated).
- **Rate limit:** passes inside `MinRecheckMs` refresh hygiene only; the
  crossing, if any, PERSISTS and is decided on a later pass.
- **Hygiene:** records with no outcome past `ActiveExpiryMs` are swept;
  removals always run; expired-id history bounded; the emission queue is
  capped. A swept-then-refreshed agent re-arms at the CURRENT level with no
  bogus adjustment.

## Reproducibility note (documented contract gap)

A matured personality is explicit-source: it no longer rebuilds identically
from the agent identity after a registry reset or host change
(`PersonalityFactory.Derive` is a pure function; explicit records are not).
Process-local by design; cross-session persistence of matured
personalities is P28's assignment, not this phase's.

## Config / multiplayer posture

Config toggles: none (the P18–P24 deterministic-director precedent —
toggles exist only for the optional LLM advisors). Inert on clients via the
deny-by-default authority seam (host-side funnel only). No SaveValue reads
or writes; no existing config value feeds this layer.

## Test inventory (LEARN01–LEARN10)

| Suite | Covers |
| --- | --- |
| LEARN01 | baseline arm (observation-free) + end-to-end crossing adjustment (completion rule, Diligence 50→51, explicit-source, P11 replacement line) |
| LEARN02 | direction rules: no-justification (silent consume), adversity rule (+1 Adaptability), completion precedence over adversity |
| LEARN03 | deny-by-default authority (null / faulting / non-authoritative probe ⇒ no-op; baseline never armed without authority) |
| LEARN04 | invalid inputs refused (id shape incl. null/empty/bare-prefix/overlong; outcome vocabulary incl. MADE_UP/null/empty; missing experience record ⇒ refused, never fabricated) + identity integrity of matured records |
| LEARN05 | evaluation rate limit + crossing persistence through it + hygiene-accurate last-seen refresh |
| LEARN06 | one-shot per crossing (same level never re-crosses) + clamp-bound consumption (no fabricated write, no readback) |
| LEARN07 | multi-agent isolation + bounded set (cap 32, deterministic refusal at cap) + hygiene decay/re-arm (sweep-then-refresh re-arms at current level; fresh record after decay is baseline-only) |
| LEARN08 | funnel fail-safety (learning listener fault propagates to direct callers but the funnel still resolves task state; experience-emit fault isolates the same way; Apply precedes Emit) |
| LEARN09 | determinism (identical scenario re-run ⇒ identical diagnostics) + data-only proof (registry size, task state, claims, recovery, experience record untouched) + post-reset inert |
| LEARN10 | real Sync funnel end-to-end + sequential crossings (level 2 then 3, Diligence 50→52) + P11 counter shape (replacement counted, no extra assignment) |

## Verification (2026-09-10)

- Build: MSBuild Release — 0 warnings / 0 errors.
- Tests: `TOTAL passed=2397 failed=0` (suite now 24 domain files, 15
  suites; +122 P25 assertions over the P24 baseline of 2275).
- Reflection (`verify_build_p25.ps1`): 69/0 — static class + nested
  `LearningRecord` + bridge; 11 members + 9 properties probed; 10 record
  fields; 7 consts; IL ownership scans (zero forbidden refs: lifecycle
  mutators, scheduler/recovery/executor/claims/validator/dispatcher,
  Photon, scene scans, capability RPCs; reads =
  CrewExperienceRegistry.SnapshotOf, writes =
  CrewPersonalityRegistry.SetPersonality only);
  `CrewAgentRegistry.ClearTask` IL references
  `AdaptiveLearningDirector.NotifyOutcome`; `CapBotLog.LEARNING` const;
  Harmony patch classes == 11; P24 surfaces intact.