# Ollama Advisor (Phase 20)

## Purpose and scope

`OllamaAdvisor` is a **recommend-only** integration with a local Ollama server
(`127.0.0.1`). It observes a snapshot of the game world and, at most once per
5 s, asks the local model for a one-line suggestion (`ADVICE: ...`). The
suggestion is **data**: a bounded log line. It never creates tasks, never
touches capabilities, never calls the decision validator (P19), never issues
orders, and never mutates any task pipeline state
(Lifecycle → Recovery → Scheduler → Claim → Capability → Validator → Executor
→ Outcome → Recovery).

Deny-by-default: the advisor is inert unless the config toggle
`OllamaAdvisorEnabled` is explicitly turned on **and** a transport is wired.
No request exists without both.

## Files

| File | Role |
| --- | --- |
| `CapBot/Core/Ollama/OllamaAdvisor.cs` | Core advisor: gates, cadence, dispatch, single-slot buffer, response consumption, validation, prompt build (~806 lines) |
| `CapBot/Core/Ollama/OllamaHttpTransport.cs` | The only network code: loopback-anchored HttpClient POST to `/api/chat` |
| `CapBot/Core/Ollama/AdvisorLogBridge.cs` | Boot-time attach of the `OLLAMA` log subsystem |
| `CapBot/Core/Logging/CapBotLog.cs` | +`OLLAMA` const (additive) |
| `CapBot/Config.cs` | +`OllamaAdvisorEnabled` (bool, **false**), `OllamaModel` (int index), `OllamaPort` (int, 11434); menu toggle / model cycler / port slider |
| `CapBot/Mod.cs` | Boot wiring: bridge, seams, transport, `ApplyConfig` |
| `CapBot/Patch.cs` | `WorldTick` postfix: guarded `ApplyConfig` + `Evaluate` call |
| `tests/OllamaAdvisorTests.cs` | OA01–OA13 (13 suites, ~80 assertions) |
| `tests/run_tests.ps1`, `tests/TaskRecoveryTests.cs` | Suite registration (19 suites total) |

## Ownership / security argument

This subsystem inverts the audit's **C1** finding (ModUpdater WebClient):

- **Host is hard-anchored** to `127.0.0.1` (const). No host/URL/DNS name is
  ever accepted from config or anywhere else. Only the port is configurable
  (validated 1..65535, invalid → default 11434).
- **HttpClient, never WebClient.** One client per transport lifetime (net472
  best practice), `UseProxy=false`, `AllowAutoRedirect=false`, hard
  `Timeout`, per-call cancellation token.
- **No boot-time network.** Nothing contacts Ollama until the game tick
  reaches an enabled, wired advisor with authority + fresh snapshot.
- **Bounded everywhere:** prompt ≤ 4000 chars, advice ≤ 240 chars, request
  timeout 90 s, 3-model vocabulary, single-flight gate, single-slot response
  buffer.

## Gates (in evaluation order)

`Evaluate(nowMs)`:

1. **Authority** — unset or faulting probe ⇒ no-op (deny-by-default,
   fail-closed).
2. **Enabled + transport** — config off or transport null ⇒ inert.
3. **Consume completed response** — if the worker parked a body
   (`PendingResponseSet`, not the null body), consume it, emit the
   advice/invalid line **immediately** (never held hostage by later gates),
   free the in-flight slot.
4. **Back-off window** — silent block while cooling down.
5. **Cadence gate** — minimum 5000 ms between dispatches (wrap-safe
   unchecked subtraction).
6. **Snapshot fail-safe (fail-open)** — null / never-captured / stale > 20 s /
   future-dated / game-not-started ⇒ uncertain pass, no dispatch, counted in
   `UncertainPasses`.
7. **Single-flight gate** — `InFlightRequestMs >= 0` or
   `Interlocked.CompareExchange` on `s_WorkerRunning` ⇒ already running, no
   dispatch.
8. **Dispatch** — build prompt on the game thread, spawn one background
   worker thread (`CapBot-OllamaAdvisor`) to run the HTTP call; it parks the
   raw body in the single-slot buffer under the advisor lock.

## Threading model

- **Game thread:** gates, snapshot read, prompt build, dispatch bookkeeping,
  response consumption, log emission. All state under one advisor lock.
- **Worker thread (one at a time):** HTTP call only, via the transport seam.
  Never touches game state. Parks the result (`PendingResponseSet` marker —
  a null body is a legitimate parked fault) and releases the slot. The game
  thread consumes on a later tick.

Why async parking: measured Ollama cold-load is ~38–40 s (probed
2026-09-08), far beyond any tolerable game-frame block; warm prompts answer
in ~0.1–0.2 s. The single-slot buffer + slot-free-on-consume shape means at
most one response is ever in flight or pending.

## Recommendation-only boundary (MUST-NOT list)

The advisor MUST NOT:

- create, pause, resume, cancel, or claim any task;
- call into `TaskScheduler`, `TaskRecoveryManager`, `ExecutionClaims`,
  `CapabilityRegistry`, or `DecisionValidator`;
- issue captain orders or move bots;
- read or write game state off the game thread;
- contact any non-loopback address;
- run more than one HTTP request concurrently;
- auto-retry failures (a rejected/failed request waits for the next natural
  cadence window; hard faults arm a 120 s back-off after 3 consecutive
  failures).

Advice is emitted as one bounded log line:
`OllamaAdvice model=<name> advice=<text>` (or
`OllamaAdviceInvalid model=<name> reason=...`). Nothing else consumes it.

## Config

| SaveValue | Type | Default | Meaning |
| --- | --- | --- | --- |
| `OllamaAdvisorEnabled` | bool | **false** | master switch (deny-by-default) |
| `OllamaModel` | int | 0 | index into bounded `KnownModels` |
| `OllamaPort` | int | 11434 | loopback port, clamped 1..65535 |

`KnownModels` = `qwen2.5:latest` (default), `qwen:latest`,
`qwen2.5-coder:latest`. Model is an index (never a free-form string — avoids
the first-string-`SaveValue` pitfall).

## Response contract (verified against Ollama 0.33.3, 2026-09-08)

`POST http://127.0.0.1:<port>/api/chat`, body:
`{"model":..., "messages":[{system,user}], "stream":false,
"keep_alive":"30m", "options":{"num_predict":48,"temperature":0.2}}`.
Response is a single JSON object; content extracted via bounded hand-rolled
scraping (`ExtractContent`) — no Newtonsoft dependency. Advice validated:
non-empty, ≥ 8 chars, ≤ 240 (truncated), `ADVICE:` prefix
(case-insensitive), zero control characters (newline injection rejected).

## Test inventory (OA01–OA13)

| Suite | Covers |
| --- | --- |
| OA01 | inert by construction: config off / transport unset / authority null / faulting probe |
| OA02 | enabled + calm snapshot dispatches one request; body has no URL, non-streaming, default model |
| OA03 | single-flight: no concurrent dispatch; slot freed on consume; consume-eval opens next cadence window |
| OA04 | successful advice consumed + logged |
| OA05 | malformed/non-ADVICE content rejected; newline injection rejected; exactly one follow-up, no retry loop |
| OA06 | transport fault (null body) = soft failure, no back-off from soft rejects |
| OA07 | snapshot fail-safe: null / stale / not-started ⇒ no dispatch, uncertain counted |
| OA08 | prompt bounded, system-first, ADVICE instruction, keep_alive, calm crew picture |
| OA09 | divergent / unknown crew data reaches prompt with unknown sentinels |
| OA10 | JSON extraction: real 0.33.3 shape, escapes, null/empty/garbage, sibling keys |
| OA11 | advice validation bounds (prefix, case, length, control chars) |
| OA12 | config clamps (port, model index, ApplyConfig) |
| OA13 | readbacks, status format, reset determinism, post-reset inert |

Test-harness note: the OA suite is the first multi-threaded suite —
`WaitForCall` (worker entered transport, in-flight observable) vs
`WaitForPark` (worker parked response) are deliberately distinct helpers;
assertions on transport state must wait for the correct one, and the
consume-eval legitimately opens the next cadence window (consume runs before
the cadence gate).

## Verification (2026-09-08)

- Build: MSBuild Release, 0 warnings / 0 errors.
- Tests: `TOTAL passed=1869 failed=0` ×3 consecutive runs (raw-output grep,
  zero FAIL lines).
- Reflection (`verify_build_p20.ps1`): 198 types (171 named); all 22 probed
  advisor members present; `KnownModels` exact; `ITransport.PostChatJson`;
  loopback host + `/api/chat`; 3 config seams; `CapBotLog.OLLAMA`; 11 Harmony
  patch classes intact; WorldTick postfix IL 533 → 603 bytes; namespace
  `CapBot.Core.Ollama` present; 1 `IgnoresAccessChecksTo` attribute.