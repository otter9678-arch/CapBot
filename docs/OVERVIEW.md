# CAPBOT OVERVIEW — phase-to-system map (P1–P33)

> One-page index: every phase, what it shipped, and where its contract
> lives. Build state per phase: Release 0-w/0-e; tests gated ×3
> consecutive clean; reflection verify per phase (scripts in the dev
> workspace, not committed). Commits are local-only (never pushed without
> separate owner authorization).

## Foundation (P1–P9): the task pipeline

| Phase | System | Contract | Key invariant |
|---|---|---|---|
| P1 | Audit + guard pass | CAPBOT_AUDIT.md (workspace) | captain tick try/catch (C2) |
| P2 | Task lifecycle | TASK_LIFECYCLE.md | legal-state-machine transitions only |
| P3 | Recovery | TASK_RECOVERY.md | lifecycle mutations owned by recovery; backoff capped 30 s |
| P4 | Scheduler | TASK_SCHEDULER.md | preemption margin; 5 s leases |
| P5 | Execution claims | EXECUTION_SAFETY.md | deny-by-default authority; sticky-success ledger (duplicate protection) |
| P6 | World state | WORLD_STATE.md | fail-closed snapshot freshness |
| P7 | Capability registry | CAPABILITIES.md | whitelisted vanilla RPC channels only |
| P8 | Executor | EXECUTOR.md | claims→dispatch→record; never invents APIs |
| P9 | Emergency director | EMERGENCY.md | bounded signals, fail-closed |

## Crew layer (P10–P13)

| Phase | System | Contract |
|---|---|---|
| P10 | Crew agents (deterministic ids) | CREW_AGENTS.md |
| P11 | Personalities (identity-derived traits) | CREW_PERSONALITIES.md |
| P12 | Experience (outcome history → XP/levels) | CREW_EXPERIENCE.md |
| P13 | Memory rings (bounded recall) | CREW_MEMORY.md |

## Directors (P14–P19): bounded deterministic advisors-on-world-state

| Phase | System | Contract |
|---|---|---|
| P15 | Navigation recovery | NAVIGATION_RECOVERY.md |
| P16 | Missions | MISSION_DIRECTOR.md |
| P17 | Economy | ECONOMY_DIRECTOR.md |
| P18 | Combat | COMBAT_DIRECTOR.md |
| P19 | Captain orders | CAPTAIN_DIRECTOR.md |

## Judgment + advisory layer (P20–P25)

| Phase | System | Contract | Note |
|---|---|---|---|
| P20 | Ollama advisor | OLLAMA_ADVISOR.md | recommend-only; loopback HTTP; single-flight |
| P21 | Crew advisor (Qwen) | CREW_ADVISOR.md | same contract, second model |
| P22 | Planning director | PLANNING_DIRECTOR.md | data-only situation assessment |
| P23 | Mission work director | MISSION_WORK_DIRECTOR.md | dynamic task authoring (one channel, shared with P18) |
| P24 | Adjustment observer | ADJUSTMENT_DIRECTOR.md | recommend-only outcome readback |
| P25 | Adaptive learning | ADAPTIVE_LEARNING.md | event-driven trait maturation (diligence/adaptability) |

## Hardening + platform (P26–P33)

| Phase | System | Contract | Key outcome |
|---|---|---|---|
| P26 | Multiplayer hardening | MULTIPLAYER_HARDENING.md | pre-gate authority-flip monitor; volatile-state census; ledger survives |
| P27 | Compatibility manager | COMPATIBILITY.md | dispatch-and-audit registry; MoreBots preserved; README truth fix |
| P28 | Crew data persistence | PERSISTENCE.md | PML save slot; insert-only restore; matured personalities only |
| P29 | Status diagnostics | STATUS_DIAGNOSTICS.md | StatusHub (128-line cap) + /capbotstatus + menu summary |
| P30 | Secure updater | SECURE_UPDATER.md | HTTPS+allowlist+SHA-256 chain; atomic staging (C1/M5 closed) |
| P31 | Performance | PERFORMANCE.md | scene-scan gate: scripted-sector handlers ≤4 scans/sec (H2 closed) |
| P32 | Testing/QA | QA.md | 12-invariant regression suite; consolidated 40-check reflection audit |
| P33 | Reproducible build/CI | BUILD.md | parameterized build.ps1 + CI pipeline; no personal paths |

## Test inventory

22 pure-C# suites (31 domain files), gated ×3 consecutive clean per
phase; per-phase reflection verify scripts (dev machine, live game
install); invariants census in QA.md.

## Status

Phases 1–33 complete and locally committed (see CHANGELOG for per-phase
hashes). Remaining per plan: P34 documentation (this file + README
refresh), P35 final audit — then STOP (no push/publish/release without
separate owner authorization).