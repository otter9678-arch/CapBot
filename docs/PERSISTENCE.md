# CREW DATA PERSISTENCE (Phase 28)

> **Ownership argument.** Phase 28 adds cross-session persistence for the
> process-local DATA layers (P12 experience, P13 memory, matured P11
> personalities). The three registries remain the SOLE owners of live state;
> the persistence layer is a read-only exporter and insert-only restorer
> between them and a versioned byte payload. It authors no tasks, mutates no
> pipeline state, has no Harmony targets, no tick driver, and no WorldTick
> block (the 11-class ceiling holds). PML save-slot discovery (the proven
> `CapBotLearningSave` pattern) carries the payload; the legacy XP slot is
> untouched.

## 1. Scope: what is persisted and what is not

| Layer | Owner | Persisted | Why |
|---|---|---|---|
| P12 experience records | CrewExperienceRegistry | **YES** (all records) | accrued XP is non-reproducible outcome history |
| P13 memory rings | CrewMemorySystem | **YES** (all rows) | episodic facts are non-reproducible observations |
| P11 personalities | CrewPersonalityRegistry | **MATURED ONLY** (explicit-source) | derived ones are reproducible from the stable AgentId (P11 FNV-1a is a pure function); persisting them would be redundant and could go stale |
| Director/task/ledger/recovery state | P2–P8, P14–P24 | **NO** | process-local lifecycle/transient by the P26 census; tasks re-derive from world observation each session |
| Legacy Autonomy.Learning XP matrix | Autonomy.Learning | **NO (already persisted)** | own PML slot "CapBotLearning" — untouched, not migrated |
| Config | SaveValue | **NO (already persisted by PML)** | existing mechanism |

**Archetypes are never persisted** — re-derived from traits on restore
(the P11 `Assign` rule is a pure function of the trait spread).
**Levels are never trusted** — recomputed from XP by
`ExperienceLevels.LevelForXp` on restore.

## 2. Restore semantics (live state always wins)

- **Insert-only:** a restore never overwrites or evicts a live record.
  Experience/personality: a live record for the same AgentId ⇒ the saved
  row is SKIPPED (counted `RestoreSkipped`). Memory: whole-agent
  semantics — if the agent had ANY memory at BATCH START, all its saved
  rows are skipped; rows for a previously-absent agent may legitimately
  share one agent (first `Upsert` creates it, later rows update in place —
  same save, not a live/saved conflict).
- **Never fabricated:** invalid payloads are refused and counted
  (`RestoreRefused`), never coerced: invalid AgentId shape, unknown
  outcome vocabulary, out-of-range traits, negative counters, XP
  inconsistent with outcome counters (totalOutcomes < sum of counted
  outcomes).
- **Recomputed, never trusted:** Level from XP; archetype from traits;
  provenance forced to `explicit` for restored personalities.
- **Stamps are data:** saved CreatedTimeMs/LastSeenMs are preserved as
  data rows; UpdateCount is not carried over for memory (live bookkeeping
  semantics restart). No wall-clock reads anywhere.

## 3. Format (v1)

`CAPB` magic (u32) + version (u16, `FormatVersion=1`) + three bounded
sections: experience records (agentId, XP i64, 5×i64 outcome counters,
totalOutcomes i64, lastOutcome string, stamps), matured trait rows
(agentId, 5×i32 traits 0..100, DerivedTimeMs i32), memory rows (agentId,
kind u8 1..3, taskId i64, text/outcome null-safe UTF-8 strings, stamps).
Strings are u16-length-prefixed UTF-8 (0xFFFF = null). Hard bound
`MaxEncodedLength=64K` (sanity; a max registry blob is ~3 KB). Decode is
all-or-nothing: any structural fault (bad magic, future version,
truncation, over-bound counts) ⇒ null payload, live state untouched.

## 4. Production wiring

- `Core/Persistence/CrewPersistence.cs` — pure C# serializer (IL-verified:
  zero PULSAR/PML/Harmony/game/pipeline references).
- `CrewPersistenceAdapter.cs` — `CapBotCrewDataSave : PMLSaveData`
  ("CapBotCrewData", VersionID 1). PML auto-discovers the subclass (no
  registration call, saved/loaded with the save slot). Fail-safe both
  ways: a faulting Save returns an empty blob; a faulting Load never
  throws and never leaves partial state. Logging through the existing
  `CapBotLog.PERSISTENCE` subsystem (no new subsystem).
- Registry additive surfaces: `CrewExperienceRegistry.ExportAll /
  RestoreRecord`, `CrewPersonalityRegistry.ExportMatured / RestoreMatured`,
  `CrewMemorySystem.ExportAll / RestoreRow / RestoreRows`.
- **Boot-order note:** PML loads save slots during mod initialization;
  restored records are present before the first WorldTick (first Sync
  re-derives agents and live state accumulates on top — inserts never
  collide because restore precedes gameplay).

## 5. What Phase 28 deliberately does NOT do

- No persistence for task/recovery/director state (P26 census: process-
  local by design; a fresh session re-derives from world observation).
- No save-format migration of the legacy slot (binary-compatible; the
  legacy `CapBotLearningSave` stays as-is).
- No user-facing save/reset UI (P29 owns command/UI/status surfaces).
- No WorldTick/driver/Harmony changes (event-free, tick-free, patch-free).
- No persistence of Ollama/Qwen advisor state (recommend-only diagnostics;
  nothing worth carrying across sessions).

## 6. Tests

`tests/PersistenceTests.cs` PERS01–PERS10 (53 assertions): full round
trip across all three layers; byte-identical determinism; insert-only
restore (live wins, XP/level untouched); whole-agent memory skip;
derived-personality exclusion + matured round trip with explicit
provenance; archetype re-derivation; forged-level refusal (recomputed
from XP); decode robustness (null/empty/bad-magic/future-version/over-
bound/truncated); payload validation refusals (invalid id, negative XP,
inconsistent totals, out-of-range trait, unknown outcome vocabulary);
bounded payload + counters/readbacks.

## 7. Test-design gotchas

- Whole-agent memory skip must be decided against the BATCH-START live
  state — run-1 caught restore row 2 of the same (previously-absent)
  agent "skipping" because row 1's Upsert had already created the agent.
  Fix: `RestoreRows` snapshots the live agent set once; single-row
  `RestoreRow` delegates to the batch API.
- The interrupted-write file-corruption pattern struck again (233×
  duplicated `stale.*` block) — the rewritten suite is verified
  single-pass before compiling (grep for duplicated statement patterns).
- Magic decimal: 0x42504143 ("CAPB" LE) = **1112555843** (first attempt
  used a wrong decimal and the verify check failed on the const, not the
  product). PowerShell has no `u` suffix — cast `[uint32]`.
- PMLSaveData lives in namespace `PulsarModLoader.SaveData` (not the root
  namespace) — run-1 compile error in the adapter.

## 8. Verification

- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2572 failed=0` ×3 consecutive (suite now 27 domain
  files, 18 suites; PERS suite 53/53 after fixing the restore batch
  semantics + 1 accidentally-dropped StatusLines method + adapter
  namespace).
- Reflection (`verify_build_p28.ps1`): 49/0 — serializer surface/consts
  (Magic/FormatVersion/MaxAgents/MaxMemoriesPerAgent/MaxEncodedLength);
  adapter derives PMLSaveData and delegates Capture/Encode/Decode/Restore;
  registry export/restore surfaces; serializer IL purity; no Harmony
  surgery; 11 patch classes; prior-phase types intact.