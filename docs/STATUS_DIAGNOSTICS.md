# STATUS DIAGNOSTICS (Phase 29; settings audit added Phase 53)

> **Ownership argument.** Phase 29 wires the read-only user-facing surfaces:
> a single status aggregation point, a chat command, and a settings-menu
> summary. It AUTHORS NOTHING and MUTATES NOTHING — it reads the public
> readbacks built across P2–P28 (the same counters the P18 calm gate and
> the P24 observer read). No new Harmony patches, no tick drivers, no
> WorldTick changes (the 11-class ceiling holds), no config SaveValues.

## 1. StatusHub (`Core/Diagnostics/StatusHub.cs`, pure C#)

`Collect(int nowMs)` gathers bounded diagnostic lines from every P2–P28
status surface in a FIXED deterministic order, capped at `MaxLines=128`
(truncation is deterministic: first lines win, plus one truncation notice
line). Each source is individually fail-safe — a faulting surface
contributes one `StatusFault <name> err=<ExceptionType>` line and never
blocks the others or the caller (the P26 monitor-handler / P27 compat-
action pattern). Zero game/PML/Harmony references (pure C# domain,
IL-verified).

**Aggregated sources (in order):**
1. Header (`CapBot status (vP29) lines<=128`)
2. Pipeline: TaskRegistry (StatusLines(nowMs) + live/history counters),
   recovery tracked count, scheduler grants, claims live + ledger entries,
   executor ticks/attempts/enabled, world snapshot freshness
3. Directors: emergency, crew agents, personalities, experience, memory,
   navigation, missions, economy, combat, captain, decision validator,
   planning, mission work, adjustment, learning, MP monitor
4. Advisors (recommend-only): Ollama, crew advisor
5. Compat manager (P27)
6. Capability registry (nowMs variant)

**Read-only proof (IL-scanned):** zero references to task registration/
creation/queue/start, claim acquisition/recording, scheduler/executor/
recovery ticks, or any clear surface. The only production edit outside
the new files is the executor's additive counters (below).

## 2. Executor counters (additive)

`TaskExecutor` gained two bounded counters + readbacks:
`TickCallCount` (Tick invocations that passed the enabled gate) and
`AttemptCount` (cumulative execution attempts performed). Counted under
the existing executor lock; reset in `ResetForTests`. No behavior change
— Tick returns exactly what it returned before.

## 3. `/capbotstatus` (`StatusCommand.cs`)

ChatCommand (PML CommandRouter) with alias `capbotstatus`. Guards: no
local player ⇒ logged + ignored; non-host ⇒ notification refused (the
report is master-authoritative process-local state; a client's report
would be all-zero and misleading). Success: `Messaging.Echo` per line of
the StatusHub report at `TaskClock.NowMs`. Read-only; whole command in
try/catch (fail-safe; a fault logs and returns, never throws to PML).

**P40 focused-section argument:** `/capbotstatus <section>` (e.g.
`/capbotstatus personalities`) echoes ONLY that section's lines via
`StatusHub.CollectSection`. Motivation: the full report is capped at 128
lines while the chat scrollback shows only the tail, so mid-report
sections (personalities, agents, recovery, …) were unreachable on
screen. Same guards and read-only discipline as the full report;
case-insensitive section names (`StatusHub.KnownSections`); unknown
section ⇒ one bounded error line listing valid sections, no crash;
no-argument behavior unchanged.

## 4. Settings-menu summary (Config.cs)

A read-only "Status (host-side pipeline summary)" block appended to the
existing CapBot settings menu: tasks live/history, claims live, scheduler
grants, executor ticks/attempts, crew agents, personalities, memory
agents, compat actions. Each value rendered through a fault-safe `Readback`
helper ("n/a" on any readback fault — the menu can never break because of
a diagnostics fault). IMGUI layout only; no state, no new SaveValues.

## 5. What Phase 29 deliberately does NOT do

- No new commands beyond the status surface (no gameplay-authoring
  commands; spawning/updates already have theirs).
- No new log subsystems (hub output goes through the consumers' own
  channels — chat echo and menu labels).
- No behavior change in any aggregated system (the executor counters are
  bookkeeping only).
- No per-frame cost: the hub runs only when a consumer calls it.
- No WorldTick/Harmony changes (patch-free phase; 11 classes preserved).

## 6. Tests

`tests/StatusDiagnosticsTests.cs` SD01–SD10 (42 assertions): report
shape/order/health; determinism on identical state; populated-state
boundedness (registry StatusLines are bounded COUNTER lines — populating
32 agents changes values, not line counts); executor counter semantics
(disabled ticks not counted, gate-bounded double tick counted once,
zero attempts without grants); registry/claim reflection; crew surfaces;
compat action naming + installed flag; no-fabrication on empty state
(zero counts stated, no NaN, no faults); read-only proof (registry
counts unchanged by collection); cross-collection structural stability
(ages in TaskRegistry lines are nowMs-derived, so byte-identical lines
are NOT the invariant — shape, header, fault-freedom, and non-aging
counters are).

## 7. Test-design gotchas

- Registry StatusLines are counter lines, not per-record lines (P10–P13
  report one bounded line per agent via Lines(), but StatusLines() is the
  counters line) — don't assert line-count growth from population.
- Task history only holds TERMINAL tasks; a queued live task shows
  `live=1 history=0`.
- Claims are deny-by-default: `SetAuthorityPolicy(→ true)` is required
  before TryClaim in tests (the MP05 discipline) even for readback proofs.
- A prior scenario's report variable is stale after FreshSetup — always
  collect a fresh report for the current scenario (SD07).
- nowMs-derived ages in TaskRegistry/CapabilityRegistry lines make
  byte-identical cross-collection equality the WRONG invariant (SD10).

## 9. Settings audit (Phase 53, §15–19)

The honest per-SaveValue truth table lives in ONE place:
`Core/Diagnostics/SettingsAudit.cs` (pure C#, no game/PML references,
never throws, bounded). 15 rows in fixed Config.cs order, each
classified:

- **LIVE (9):** a compile-verified gameplay consumer exists, and the row
  names it — the 3 autonomy toggles (`Autonomy.OnTick` talents/research/
  inventory, SmartItemUse, economy/campaign/missions), ModUpdaterEnabled
  (Mod boot), VerboseLogging (CapBotLog level gate), OllamaAdvisorEnabled
  + QwenAdvisorEnabled (advisor ApplyConfig → Evaluate gates),
  OllamaModel (advisors request model; P44 owner pin `qwen3:latest` at
  boot), OllamaPort (OllamaAdvisor endpoint, clamped 1..65535).
- **DEAD (6):** the menu shows the knob and the value persists, but
  NOTHING reads it (audit H4, grep-verified): AutoAssignCaptain,
  AIReactionSpeed, AIAccuracy, CombatEngageRange, CombatDisengageHealth,
  MinCreditsReserve. Consumer renders `NONE (…)`; effective renders
  `not wired (no consumer)`.

P53 deliberately does NOT wire dead sliders — silently changing legacy
behavior is exactly what the audit forbids. The legacy constants those
knobs resemble stay documented as DATA in the P16 (EconomyDirector
ReserveFloor 2500) and P17 (CombatDirector LegacyBlindJumpHullFraction
0.2) contracts; wiring is a later-phase decision.

**`/capbotsettings` (SettingsCommand.cs, host-only, read-only):**
renders `SETTING / CONFIGURED / STORED / RUNTIME / CONSUMER / EFFECTIVE`
per row straight from the audit table + live Config values. STORED
mirrors CONFIGURED (SaveValue is the in-memory value loaded at boot; the
single boot-time overwrite — the qwen3 model pin — is reported in the
consumer column instead). Fail-safe readbacks degrade to "n/a", never
invented; OllamaModel shows the pinned model name + index.

**Drift protection:** `tests/SettingsAuditTests.cs` (suite f40, SA01–SA04,
190 checks) pins the table: size 15 = 9 live + 6 dead; every dead knob
stays DEAD with a NONE consumer and not-wired effective; internal
consistency (DEAD never claims wired, non-DEAD never claims not-wired,
consumer text present, unique names); safe lookups. A knob that gains a
consumer without flipping its row FAILS the suite — the audit cannot
silently go stale. Conversely a knob reported LIVE without a consumer
fails the invented-liveness check.

**Menu honesty:** the six dead knobs in Config.cs carry the label suffix
" (not wired — /capbotsettings)" so the UI stops implying they act.

## 10. Verification (P29 record + P53 addition)

- Build: MSBuild Release 0 warnings / 0 errors.
- Tests (P29): `TOTAL passed=2614 failed=0` ×3 consecutive at the time
  (28 domain files, 19 suites; SD suite 42/42 after fixing 5
  test-authoring bugs — counter-not-per-record line counts,
  terminal-only history, missing authority policy, stale report
  variable, age-embedded line instability).
- Reflection (P29, `verify_build_p29.ps1`): 37/0 — hub surface/const;
  command derives ChatCommand with the full member set; executor
  counters present; hub IL purity (zero forbidden refs) + read-only IL
  proof (no lifecycle mutators, no tick drivers); command IL calls
  Collect + Messaging.Echo; 11 patch classes; prior-phase types intact.
- Tests (P53): suite f40 `SettingsAuditTests` SA01–SA04 (190 checks);
  full runner `TOTAL passed=3664 failed=0` ×3 consecutive (40 suites);
  Release build 0 errors, CapBot.dll 485,888 bytes, SHA-256
  bdea3bb0a6801b77e42ecd7a3edb9dc94afddedc00f9fe86d8c59b4aa151b825.