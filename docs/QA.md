# QA / TESTING (Phase 32)

> **Ownership argument.** Phase 32 is a pure test-and-audit phase: zero
> production changes. It consolidates the master prompt's architecture
> invariants into a repeatable regression suite (`tests/QaInvariantTests.cs`)
> plus a consolidated reflection audit (`verify_build_p32.ps1`) that re-checks
> every prior-phase surface in one run.

## 1. Invariant census (master-prompt rules → automated proof)

| # | Master invariant | Automated proof |
|---|---|---|
| 1 | Master/authority owns execution; deny-by-default | QA01 (claims/monitor/crew seams refuse without authority) + verify: Mod ctor IL wires `SetAuthorityPolicy` |
| 2 | LLMs advisory-only; never execute advice | QA02 (ADVICE: vocabulary gate, control-char rejection, bounded length, opaque round-trip) + verify: both advisors' IL has ZERO pipeline-mutator refs |
| 3 | No per-frame expensive operations | QA03 bounds + P31 SceneScanGate (PERF suite) + verify: 11-class ceiling |
| 4 | Duplicate-execution protection | QA04 (sticky ledger: same action id ⇒ `DuplicateExecutionRejected`) |
| 5 | Recovery budget/backoff | QA05 (BackoffDelayMs monotonic non-decreasing over 20 steps, ≤ MaxBackoffMs) |
| 6 | Live state wins over persistence | QA06 (stale save never overwrites a live record) |
| 7 | Determinism | QA07 (identical FreshSetup timelines ⇒ byte-identical StatusHub reports) |
| 8 | No-fabrication | QA08 (invalid ids/traits/blobs/paths/kinds refused across all registries) |
| 9 | Updater verification chain | QA09 (honest payload passes; tampered digest refuses; plain http refuses) |
| 10 | Bounded everything | QA03 census: agents/experience/personalities/memory ≤32, memory-per-agent ≤8, status ≤128 lines, scan keys ≤32, updater payload ≤32MB, compat actions ≤8, persistence blob ≤64KB, advice ≤240 |
| 11 | Ollama/Qwen integrations keep working | QA02 + OllamaAdvisorTests/CrewAdvisorTests (loopback transport seam; single-flight; hard timeouts) |
| 12 | Control pipeline preserved | QA04 end-to-end create→register→queue→start→claim→record + 21-suite regression |

## 2. Test inventory (as of P32)

- 22 pure-domain suites, 31 domain files, 2822 total assertions in the
  runner (all `TOTAL passed=N failed=0` ×3-consecutive gated per phase).
- Per-phase suites: P2 lifecycle, P3 recovery, P4 scheduler, P5 claims,
  P6 world, P7 capabilities, P8 executor, P9 emergency, P10 agents,
  P11 personalities, P12 experience, P13 memory, P15 navigation,
  P16 missions, P17 economy, P18 combat, P19 captain, P22 planning,
  P23 mission work, P24 adjustment, P25 learning, P26 multiplayer,
  P27 compat, P28 persistence, P29 status, P30 updater, P31 perf,
  P32 QA invariants.
- Reflection verify scripts per phase (.qwen/tmp/verify_build_pNN.ps1)
  with the corrected preload set (PulsarModLoader/ACTk/CrewAILibrary/
  UnityEngine/Behave) and per-method fault-safe body probes.

## 3. Known gaps (documented, deliberate)

- No in-game automated harness (the dev-side suite is pure C#; in-game
  behavior is validated by the reflection scripts' shipped-surface
  audits — loading CapBot.dll against the real game DLLs).
- Sync WebClient freeze risk documented in P30 (threading unwarranted
  without a measured problem — P31 rationale carries).
- The scripted-sector handlers' behavior is validated at the gate level
  (PERF suite) and IL level (handler keys); the handlers' sector logic
  itself is gameplay-tuned code guarded by the P1 Postfix try/catch.

## 4. Verification (this phase)

- Tests: `TOTAL passed=2777 failed=0` ×3 consecutive (suite now 31 domain
  files, 22 suites; QA suite 45/45).
- Reflection (`verify_build_p32.ps1`): 40/0 — full 30-type P2–P31 census;
  11 patch classes; Mod-ctor wiring IL-proven (authority seam, MP monitor,
  compat manager) with the full preload set; BOTH advisors IL-proven
  recommend-only (zero pipeline-mutator references in any method body);
  command surfaces intact.
- Run-1/2 defects: 2 test-authoring slips (missing using; ledger
  RecordOutcome returns bool) + 1 stale-variable assertion (QA02 out-var
  overwritten by the oversize case). Zero product defects found — the
  invariants held.