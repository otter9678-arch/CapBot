# Phase 26 — Trait Profile (the trait consumer)

Status: implemented + verified (see "Verification"). Read-only diagnostics
phase: the deterministic consumer half of the personality arc. It reads the
traits P25 matured and emits bounded, human-readable crew-composition
signals — it performs NO writes of its own and drives NO behavior change.

## Design basis (verified constraint trail)

No master-plan document exists in the workspace defining Phase 26 numeric
rules (verified: the only "consumer phase" text in the tree is
AdaptiveLearningDirector's own P25 comment). The design is inferred from the
verified deferral-trail constraints:

1. **Inputs (public readbacks only):** `CrewPersonalityRegistry`
   (`ArchetypeOf` / `AffinityToRole` / `Count`) — the P11 read surface;
   `AdaptiveLearningDirector.GetRecord` (`AdjustCount > 0` ⇒ a matured
   personality) — the P25 readback; roster `CrewAgentRegistry.AgentViews`
   (the P21-sanctioned read surface, deterministic AgentId order); world
   gate via the shared 20 s snapshot standard (P9/P10/P24 shape).
2. **Semantics:** one-shot report per signal per record lifetime (the
   P15/P16/P17/P22/P23/P24 house mirror) — a trait state is an EVENT, not a
   persistent condition; persistence is the record snapshot itself (traits
   refreshed every readable pass).
3. **Signals (per active crew agent, first class only):**
   - `DOMINANT` — the agent's archetype is a named archetype (not
     BALANCED): its dominant-trait profile.
   - `LOWAFFINITY` — the role-affinity score for the agent's CURRENT role
     is below 40: a trait-vs-role mismatch worth surfacing. Never a task,
     never a reassignment (assignment is scheduler-mediated and owned by
     other phases).
   - `MATURED` — P25 has adjusted this agent's traits (`AdjustCount > 0`)
     at first sight: the consumer-visible maturation readback.
   - `NOPERS` — global, session-one-shot: active agents exist while the
     personality registry has never held a record — the diagnostic that
     keeps the consumer honest (nothing fabricated when there is nothing
     to consume).
4. **Traits are consumed, never re-written:** no `SetPersonality`, no
   `DeriveFor`, no task operation, no scheduler/recovery/claims/executor/
   validator call, no other phase's statics mutated. Every write it needs
   already happened (P25) or belongs to another phase (assignment).

## Integration

Tick-driven: one guarded block in the `WorldTick` Postfix (in place, after
the P24 block — it observes P25 writes that have already been applied by the
funnel). **No new Harmony patch class** — the 11-class ceiling is kept.
Deny-by-default authority; no config toggle (the P18–P24
deterministic-director precedent); baseline arm pass is observation-free
(the P22/P24 premise-capture analogue).

Boot wiring (Mod.cs): `TraitProfileLogBridge.Ensure()` +
`TraitProfileDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative)` +
`SetWorldProvider(WorldStateService.Latest)`. Log subsystem:
`CapBotLog.TRAIT` (additive).

## Boundaries (contract)

- Reads ONLY the public readbacks listed above plus the snapshot gate.
- Never throws: every seam fault is a counted, fail-closed pass
  (`UnknownInputPasses` + `LastUncertainReason`).
- No game reads (agents/snapshot arrive through seams), no wall-clock reads
  (`nowMs` is TaskClock semantics), no LINQ, no per-frame work, no LLM in
  any deterministic path (the P21 CrewAdvisor reads the same registry
  independently and is never consumed here).
- Every collection is bounded; same inputs ⇒ same decisions.
- One-way lock order: per-agent readbacks run OUTSIDE the director lock
  (the P24 precedent); the layer takes nobody's lock while holding its
  own. A fault on one agent's readback skips that agent only.
- Deny-by-default authority: with no probe, a faulting probe, or a
  non-authoritative caller, `Evaluate` is a no-op (0 reports, no records).

## Bounded knobs

| Constant | Value | Meaning |
| --- | --- | --- |
| `MinRecheckMs` | 5000 | evaluation cadence (P18/P22/P23/P24 parity) |
| `MaxStaleSnapshotMs` | 20000 | shared snapshot freshness standard |
| `MaxRecords` | 32 | bounded profile records (= agent/personality bound) |
| `MaxHistory` | 16 | bounded expired-record id history |
| `ActiveExpiryMs` | 30000 | agent absent past this ⇒ record decays |
| `MaxPendingLines` | 4 | bounded emission queue per pass (house bound) |
| `ReportBlockMs` | 20000 | anti-churn re-report block per record (P24 parity) |
| `LowAffinityThreshold` | 40 | affinity below this ⇒ LOWAFFINITY signal |
| `TrackIdPrefix` | `TRAIT:` | record ids are `TRAIT:<agentId>`; global `TRAIT:<TOKEN>` |

Global track token: `NOPERS` (never collides with agent ids `AGT:<hex8>`).

## Decision semantics

Gate order mirrors the P18/P22/P23/P24 house shape: authority
(deny-by-default) ⇒ cadence ⇒ snapshot fail-safe (missing / stale /
from-the-future / not-started) ⇒ bounded rules. Baseline arm pass silent
(signals start on the SECOND readable pass). Returns the number of NEW
reports this pass; never throws.

Per-agent profile record (one-shot semantics, the P24 record mirror):

- **First seen** ⇒ track + immediate report per the first-class rule:
  `matured` outranks `lowAffinity` outranks `dominant`; a balanced,
  known-affinity, never-matured profile is tracked but quiet.
- **Profile persists** ⇒ silent refresh (`DuplicatesSuppressed`).
- **Signal re-fire** (e.g. maturation appears on a known profile) ⇒
  re-report only after `ReportBlockMs` (anti-churn); inside the block the
  pass is counted as `RecheckBlocks` and silent.
- **Agent absent** ⇒ hygiene decays the record after `ActiveExpiryMs`
  (`TraitProfileExpired`); a re-seen agent arms a FRESH record (budget
  re-armed).
- **Maturity re-read every readable pass** — the trait EVENT is the
  trigger, not the condition.

`NOPERS` is session-global one-shot (never re-reported even if the
condition persists); expired-id history is bounded; the emission queue is
capped; `ArchetypeIdOf` maps the fixed PersonalityArchetypes vocabulary
(Sentinel→1 … Adapter→5, else 0).

## Derive-on-demand interplay (load-bearing)

`CrewPersonalityRegistry.ArchetypeOf/AffinityToRole` call `DeriveFor`,
which derives AND REGISTERS the personality for unregistered agents. The
arm pass performs these readbacks — so the registry is never empty at pass
2 if agents were visible at arm time. `NOPERS` is therefore honest only
for mid-session joiners (agents not yet present during the empty-crew arm
pass). This is verified behavior, not an accident (TRAIT07).

## Config / multiplayer posture

Config toggles: none (the P18–P24 deterministic-director precedent —
toggles exist only for the optional LLM advisors). Inert on clients via
the deny-by-default authority seam (host-side WorldTick only). No
SaveValue reads or writes; no existing config value feeds this layer.

## Test inventory (TRAIT01–TRAIT11)

| Suite | Covers |
| --- | --- |
| TRAIT01 | baseline arm (observation-free) + end-to-end DOMINANT signal (Sentinel→arch 1, Weapons affinity 59, one-shot report counted) |
| TRAIT02 | LOWAFFINITY signal (deterministic Engineer weights below threshold) |
| TRAIT03 | deny-by-default authority (null / faulting / non-authoritative probe ⇒ no-op; nothing evaluated or tracked; authorized ⇒ arm + profile proceed) |
| TRAIT04 | snapshot fail-safes (missing / stale / future / not-started ⇒ 0 with reasons; readable snapshot arms) |
| TRAIT05 | evaluation rate limit (in-gate pass denied, not counted) + profile persistence + hygiene-accurate last-seen refresh |
| TRAIT06 | one-shot per record + anti-churn re-report block + dup-suppression on the refresh pass + re-report after block expiry |
| TRAIT07 | NOPERS global one-shot incl. the derive-on-demand interplay (empty-crew arm ⇒ joiners ⇒ registry populated by readbacks; no second report) |
| TRAIT08 | MATURED signal after a real P25 adjustment (level crossing ⇒ Diligence 51 ⇒ `matured` re-report) + consumer never re-writes traits |
| TRAIT09 | multi-agent isolation (dominant agent signals, quiet agents tracked silent) + deterministic line order + GetRecord null/empty/absent safety |
| TRAIT10 | hygiene decay after REAL registry removal (crew loss ⇒ expiry lines, bounded history) + fresh re-arm (new records, tracked count advances) |
| TRAIT11 | determinism (identical scenario ⇒ identical diagnostics) + data-only proof (seeded trait untouched; no learning write; task pipeline untouched) + post-reset inert |

## Verification (2026-09-17)

- Build: MSBuild Release — 0 warnings / 0 errors.
- Tests: `TOTAL passed=2483 failed=0` (suite now 25 domain files, 16
  suites; +86 P26 assertions over the P25 baseline of 2397).
- Reflection (`verify_build_p26.ps1`): 83/0 — static class + nested
  `TraitProfileRecord` + bridge; 10 members + 12 properties probed; 9
  record fields; 10 consts; IL ownership scans (zero forbidden refs:
  SetPersonality/DeriveFor — the write-path proof — plus task
  authoring/scheduler/recovery/executor/claims/validator/dispatcher,
  Photon, scene scans, capability RPCs; reads = ArchetypeOf,
  AffinityToRole, AdaptiveLearningDirector.GetRecord,
  CrewAgentRegistry.AgentViews); `CapBotLog.TRAIT` const; Mod boot IL
  references (methods AND constructors) `TraitProfileLogBridge.Ensure` +
  `TraitProfileDirector.SetAuthorityProbe`; Harmony patch classes == 11;
  P24/P25 surfaces intact.