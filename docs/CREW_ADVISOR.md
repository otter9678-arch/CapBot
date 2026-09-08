# Crew Advisor (Phase 21)

## Purpose and scope

`CrewAdvisor` (namespace `CapBot.Core.Qwen`) is the **crew-domain completion
of the Phase 20 Ollama advisor** — the Qwen integration. It asks the SAME
local Ollama server (loopback `127.0.0.1`, shared port/model config) for ONE
advisory line about the crew picture, built from the CrewAgent hooks reserved
since Phase 10 (`docs/CREW_AGENTS.md` "later phases" integration points:
`Role`/`RoleName`, `LastTaskOutcome`, `LastKnownTLIName`) through the additive
`CrewAgentRegistry.AgentViews()` readback.

The advice is **data**: one bounded log line (`CrewAdvice model=... advice=...`
or `CrewAdviceInvalid ...`). It never assigns tasks (registry assignment APIs
untouched), never mutates any task pipeline state, never feeds the
deterministic directors (P9 emergency / P14 captain / P18 combat), and never
reaches a capability or the executor. Deterministic rules always override the
advisor.

**Design basis note:** no master-plan document exists in the workspace
defining Phase 21 (verified by workspace-wide search). The design is inferred
from the P20 advisor contract + audit constraints + the CREW_AGENTS.md
reserved hooks, and documented as such. If the external master plan (PART
0–68) mandates a different shape for P21, this phase is the candidate for
re-alignment.

## Files

| File | Role |
| --- | --- |
| `CapBot/Core/Qwen/CrewAdvisor.cs` | Crew advisor core: gates, cadence, dispatch, single-slot buffer, consumption, crew prompt build (~520 lines) |
| `CapBot/Core/Qwen/CrewAdvisorLogBridge.cs` | Boot attach of the `QWEN` log subsystem |
| `CapBot/Core/Logging/CapBotLog.cs` | +`QWEN` const (additive) |
| `CapBot/Core/Crew/CrewAgentRegistry.cs` | +`AgentView`/`AgentViews()` additive point-in-time readback (no behavior change to the registry) |
| `CapBot/Config.cs` | +`QwenAdvisorEnabled` (bool, **false**); menu toggle |
| `CapBot/Mod.cs` | Boot wiring: bridge, seams, transport (shared `OllamaHttpTransport`), `ApplyConfig` |
| `CapBot/Patch.cs` | `WorldTick` postfix: guarded `ApplyConfig` + `Evaluate` block after the P20 block |
| `tests/CrewAdvisorTests.cs` | CA01–CA10 (~70 assertions) |
| `tests/OllamaAdvisorTests.cs` | `FakeTransport` visibility `private` → `internal` (reused by the CA suite) |
| `tests/run_tests.ps1`, `tests/TaskRecoveryTests.cs` | Suite registration (20 suites total) |

## Inheritance of contract from P20

CrewAdvisor is a structural mirror of `OllamaAdvisor`:

- **Same gate order** in `Evaluate(nowMs)`: authority (deny-by-default,
  fault ⇒ no-op) ⇒ enabled+transport ⇒ consume completed response (lines fire
  immediately, never held hostage) ⇒ back-off (silent) ⇒ cadence (8000 ms —
  slower than P20's 5 s because the crew picture changes slowly; wrap-safe
  unchecked subtraction) ⇒ snapshot fail-safe (null/never-captured/stale>20s/
  future/not-started ⇒ uncertain, no dispatch) ⇒ single-flight
  (`InFlightRequestMs >= 0` + `Interlocked.CompareExchange`) ⇒ dispatch.
- **Same threading model:** prompt built on the game thread (pure registry
  readbacks), dedicated background worker thread (`CapBot-CrewAdvisor`) runs
  HTTP via the SAME `OllamaAdvisor.ITransport` seam and the SAME
  `OllamaHttpTransport` (loopback-anchored, never throws), parks the raw body
  in a single-slot buffer (`PendingResponseSet` bool marker — null body is a
  legitimate parked fault), game thread consumes on a later tick.
- **Same shared advisory data contract:** response content extracted via
  `OllamaAdvisor.ExtractContent` (hand-rolled, escape-aware) and validated by
  `OllamaAdvisor.ValidateAdvice` (≥8 chars, ≤240 truncated, `ADVICE:` prefix
  case-insensitive, zero control chars). Same `keep_alive:"30m"`,
  `stream:false`, `num_predict:48`, `temperature:0.2` options.
- **Same failure semantics:** soft faults (rejected advice) never escalate;
  hard faults arm a back-off ladder (3 consecutive ⇒ 120 s cooldown); no
  auto-retry (the consume-eval legitimately opens the next cadence window —
  exactly one follow-up).
- **Same MUST-NOT boundary** as `docs/OLLAMA_ADVISOR.md`, plus one
  crew-specific rule: MUST NOT call `CrewAgentRegistry.AssignTask`/
  `ClearTask`/`AddCapabilityReference` — the advisor holds no registry
  handles (only the copied `AgentView` structs).

## What the prompt contains

Crew roster picture from `AgentViews()` (deterministic AgentId order, first
`MaxAgentsInPrompt`=12): per-agent `[bot|human] [captain] role=<role|unknown>
roleName=<name|unknown> name=<name|unknown> lastLoc=<TLI|unknown>
lastOutcome=<COMPLETED/...|unknown>` + aggregate counts (`active= bots=
humans= captains= withLastOutcome= withLastLocation= withRoleName=`). All
variable strings pass `EscapeJson` + 16-char truncation; unknown data
degrades to `unknown` sentinels, never a crash. Total prompt ≤ 4000 chars.

## Config

| SaveValue | Type | Default | Meaning |
| --- | --- | --- | --- |
| `QwenAdvisorEnabled` | bool | **false** | crew-advisor master switch (deny-by-default) |

Shared with P20 (same local server): `OllamaPort` (loopback port),
`OllamaModel` (index into the same bounded `KnownModels` vocabulary). Menu
adds one toggle button; no new text inputs.

## Test inventory (CA01–CA10)

| Suite | Covers |
| --- | --- |
| CA01 | inert by construction: config off / transport unset / authority null / faulting probe |
| CA02 | dispatch with a REAL P10-synced crew registry (assignment round-trip seeds LastTaskOutcome); body shape; crew hooks reach prompt; cadence gate |
| CA03 | single-flight; slot freed on consume; consume-eval opens next window |
| CA04 | successful advice consumed + logged; data-only (no registry handles by design) |
| CA05 | malformed content rejected; exactly one follow-up via `RequestsSent` (race-free anti-loop invariant) |
| CA06 | newline injection rejected; injected content never stored |
| CA07 | transport fault (null body) = soft rejection, no back-off |
| CA08 | snapshot fail-safe: null / stale / not-started ⇒ no dispatch, uncertain counted |
| CA09 | prompt bounds + roster sentinel degradation (null-TLI agent ⇒ `lastLoc=unknown`, `lastOutcome=unknown`) |
| CA10 | readbacks, status format, reset determinism, post-reset inert |

Race lessons from P20 carried over: `WaitForCall` (worker entered transport —
in-flight observable) vs `WaitForPark` (worker parked) are distinct; retry
invariants assert on the game-thread-only `RequestsSent`, never `CallCount`.

## Verification (2026-09-08)

- Build: MSBuild Release, 0 warnings / 0 errors.
- Tests: `TOTAL passed=1920 failed=0` ×3 consecutive runs (raw-output grep,
  zero FAIL lines; suite now 20 files).
- Reflection (`verify_build_p21.ps1`): 205 types (175 named); CrewAdvisor
  static class, all 13 probed members; `CrewAdvisorLogBridge`;
  `AgentViews`+`AgentView` present; internal `ExtractContent`/`ValidateAdvice`
  reachable; `Config.QwenAdvisorEnabled`; `CapBotLog.QWEN`; 11 Harmony patch
  classes intact; WorldTick postfix IL 603 → 673 bytes; namespace
  `CapBot.Core.Qwen` present.