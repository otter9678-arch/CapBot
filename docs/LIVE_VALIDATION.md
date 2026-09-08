# Phase 36 — Live Gameplay Validation (2026-09-08)

## Purpose

P1–P35 produced 2,777 unit/integration assertions, a consolidated invariant
audit, and a reflection-verified build — but **no test run inside the actual
game**. P36 separates what is *proven* from what is only *believed*, fixes
the blocking defects found during that separation, and records reproducible
manual procedures for everything that cannot be mechanically verified from
the dev environment.

Verification vocabulary (strictly separated):

- **UNIT TEST VERIFIED** — assertion in the automated suite (`run_tests.ps1`).
- **INTEGRATION VERIFIED** — reflection/IL audit against the built DLL and
  game references (verify scripts), no game process needed.
- **LIVE GAME VERIFIED** — observed inside a running PULSAR process.
- **NOT LIVE-VERIFIED** — requires in-game session (see manual procedure).

## P36 entry state (measured 2026-09-08)

| Item | Value |
| --- | --- |
| HEAD / branch | `aede74c` / `master`, working tree clean |
| Test battery at entry | 2777/2777 PASS (re-run this session, raw FAIL grep = 0) |
| Repo build (`bin/Release`) | 388,608 bytes, SHA256 `f553220a…`, built 15:59 |
| **Deployed DLL in game `Mods/`** | 387,584 bytes, SHA256 `116ca813…`, 15:45 — **STALE vs P35 build** |
| Game Mods folder | CapBot + BetterAI, QualityImprover, MoreBotsAI, ExpandedGalaxy, Progress_Editor, Exotic Components, BotCount, Talents mods, UnlimitedCredits, LoadingScreenSkipper |
| Ollama service | LIVE on 127.0.0.1:11434; models: **qwen3:latest**, gemma4:26b, qwen2.5-coder, qwen, qwen2.5 |
| PULSAR process | Not running; desktop idle (screen capture + OCR at session start) |

**Finding D1 (deployment drift, fixed):** the copy of CapBot.dll in the game
Mods folder predated the final audited P35 build (different size and hash).
Fixed by deploying the P35 build (hash parity re-verified), preserving the
prior file as `CapBot.dll.pre_p36.bak`. Later superseded by the L1 fix build
(current deployed SHA256 `b7862bfc…`).

## Finding L1 — qwen3:latest incompatible with advisor request shape (FIXED)

Discovered by live HTTP probe of the real Ollama service using the advisor's
exact request shape (`BuildRequestJson` equivalent payload, stream:false,
keep_alive 30m, num_predict 48, temperature 0.2):

| Probe | num_predict | Result |
| --- | --- | --- |
| 1 | 48 | `content:""`, 48 tokens in `message.thinking`, `done_reason:"length"` |
| 2 | 256 | identical shape — thinking does not converge at temp 0.2 |
| 3 | 48 + `"think":false` | **`ADVICE: Use fuel capsules to repair hull and prepare for hostiles, prioritize shield reinforcement.`** — `done_reason:"stop"`, 20 tokens, passes the existing validator unchanged |

Root cause: qwen3 is a *thinking* model; its reasoning trace consumes the
completion budget before any answer exists. The advisor's response schema
handling needed **no change** (thinking arrives in a separate
`message.thinking` field; `message.content` stays clean).

Fix (additive, both advisors):

- `OllamaAdvisor.KnownModels` += `qwen3:latest` (index 3); same for
  `CrewAdvisor.KnownModels`.
- New `ThinkingModels` table + `IsThinkingModel` / `IsRequestingModelThinking`;
  request builders append `"think":false` for thinking models only. Legacy
  model requests are byte-identical to P35.
- Tests: OA14a–OA14h (8 new assertions). Suite total **2785/2785**.
- Docs: this file + `OLLAMA_ADVISOR.md` (model table + thinking-model note).

Unit-test side effect: OA12h vocabulary bound 3 → 4.

## Static verification results (P36 re-runs)

| Gate | Result |
| --- | --- |
| `run_tests.ps1` (post-fix) | **2785/2785 PASS** (raw FAIL grep = 0) |
| `verify_build_p29.ps1` (status + prior-phase census) | 37/0 |
| `verify_build_p21.ps1` (advisor + patch census) | 0 FAIL; 11 Harmony patch classes; WorldTick IL 877 (P26 audit baseline) |
| Build | `BUILD OK bytes=389120 config=Release` (P33 smoke checks incl. no-path-embedding) |
| Deployment parity | repo `bin/Release` SHA256 == `Mods/CapBot.dll` SHA256 (`b7862bfc…`) |
| LLM contract probe | qwen3:latest answers the advisor prompt with a valid ADVICE line when `think:false` is sent (see L1) |

## Live-session evidence (2026-09-08, two real game sessions)

Two real PULSAR sessions were mined for CapBot log lines (previous session
~35 min, ended 16:10; current session live at time of mining —
`Player-prev.log` 2,718 lines + growing `Player.log` 1,714+ lines). Counts
are `find /c` greps over both files. **Context caveat:** the running game
process loaded the 15:45 deployed DLL (hash `116ca813…`, pre-L1-fix); the
L1 build (`b7862bfc…`) is deployed for the *next* launch. Advice lines in
these sessions therefore used non-thinking `qwen:latest`, which works
without `think:false`.

| Evidence (LIVE GAME VERIFIED) | Player.log | Player-prev.log |
| --- | --- | --- |
| Tasks registered (all types) | 66 | 108 |
| `Queued->Running` transitions | 177 | 288 |
| `Running->Completed` (observed lines; more dropped by flood guard) | 11 | 34 |
| `Running->Failed` | 56 | 85 |
| Capability dispatches (`Dispatched`) | 21 | 44 |
| `ExecutorResult` outcome lines | 9 | 24 |
| Recovery actions applied (Retry+Cancel) | 32 | 64 |
| `Recovery applied Retry` | 18 | 38 |
| `Recovery applied Cancel` | 15 | 26 |
| `Recovery applied Expire / Pause / Resume / stuck` | 0 | 0 |
| `ExecutorInvariant` (invariant violations) | 0 | 0 |
| NAV_RECOVERY tasks completed (observed) | 6 | 15 |
| NAV_RECOVERY tasks failed (observed) | 0 | 0 |

End-to-end chains observed live (all stages in one task's history):

- **Emergency pipeline:** `CoolantCritical` detection → `EmergencyTaskCreated`
  → Registered→Queued→Granted→Queued→Running → `CapabilityApproved
  SET_CAPTAIN_ORDER` → `ClaimAccepted` → `Dispatched SET_CAPTAIN_ORDER
  order=9` → `OwnershipReleased reason=Succeeded` → `Running->Completed` →
  `ExecutorResult #121 SET_CAPTAIN_ORDER SUCCESS task=Completed` →
  `EmergencyResolved` (Player.log:1196–1207; prev: #16/#19/#63/#68/#73…).
- **NAV recovery dispatches:** `Dispatched REMOVE_COURSE_GOAL` /
  `Dispatched ADD_COURSE_GOAL` with full completion chain (Player.log:1462–1474
  task #177; prev: #25/#95/#100/#105/#110/#115/#142/#147/#152).
- **Advice channels:** `OllamaAdvice` accepted (`qwen:latest`); `CrewAdvice
  model=qwen:latest advice=ADVICE: Deploy TomKing to the Outpost 448.`
  (Player.log:705); no task/order mutation follows any advice line —
  recommend-only holds live.
- **Recovery loops:** `no capability bound` fail → `Failed->Queued` retry
  after 2000 ms backoff → retries exhausted → `Recovery applied Cancel`
  (Player.log:694–696 task #17) — bounded, no storm.
- **Agent registry:** captain/Weapons/Pilot/Scientist/Engineer role tracking
  from the live crew section.
- **Compatibility:** `CompatInstall` decision ran live (MoreBots absent →
  skip path taken) with 0 CapBot exceptions in either session.
- **Stability:** 0 CapBot exceptions/stack traces; third-party
  `TalentsModPerformanceImprovement` NREs present — NOT CapBot (documented,
  upstream bug).

### Finding L2 — "NAV tasks never terminal" (INVESTIGATED — NOT A DEFECT)

The P36 entry suspicion ("44 Running NAV_RECOVERY tasks, zero terminal
outcomes, no ExecutorResult after Dispatched, timeouts never fire") was
**disproven** on direct evidence:

1. Complete NAV chains exist in both sessions (task #177 above; 21 observed
   `NAV_RECOVERY … state=Completed` lines). Executor outcome lines label the
   **capability** (`ExecutorResult #177 REMOVE_COURSE_GOAL …`), not the task
   type — grepping for task-type + outcome together undercounts.
2. "Missing" outcome lines are a **logging artifact**: `CapBotLog` enforces
   a global flood guard (24 messages / 10 s, plus 8 s per-key dedup) with
   silent drops; emergency bursts exceed the window (Player.log:677–706
   shows the burst that swallowed task #13's outcome lines). All executor
   paths emit *some* decision line (`ExecutorResult`/`ExecutorRejected`/
   `ExecutorInvariant`/`ExecutorStaleCallback`); zero `ExecutorInvariant`
   violations occurred, so no path was skipped.
3. `Expired=0` is **correct behavior**, not a leak: for Running tasks,
   recovery's stuck rule (`15 s`, `Recovery applied Fail` candidates) is
   evaluated before the timeout rule (120 s), and observed tasks resolve in
   seconds (task #177 age 2515 ms). The policy ordering makes the stuck
   path dominate long before the deadline; `Recovery applied Fail` lines are
   themselves flood-dropped under bursts (0 observed but 141
   `Running->Failed` transitions observed — recovery-owned and executor
   `no capability bound` fails).
4. `TaskRegistry.SweepExpired` is confirmed orphaned in production (only
   TaskScheduler.cs:192 references it in a comment). It is functionally
   superseded by `TaskRecoveryManager.Tick` (wired at Patch.cs:2894, host
   gate, ≥1 s), whose per-task Decide evaluates `IsTimedOut` every pass.
   Kept as a documented helper; **no code change** — wiring a second expiry
   path would duplicate recovery's authority.

### Finding L3 — advisory-only emergencies cycle fail→retry→cancel (FIXED in P37)

`EmergencyDetector` creates two emergency types with **no wired capability**
(deliberate: "no capability wired: vanilla owns this; coordination-only
decision") — `NavigationFailure` (stuck bot, Warning) and `ObjectiveCritical`
(1 objective left, Warning). These tasks reach Running, fail closed
("no capability bound"), retry once after backoff, then cancel on retries
exhausted (349 `no capability bound` fail events across both sessions —
the dominant `Running->Failed` source). Behavior was bounded (MaxRetries=1)
and correct (deny-by-default), but wasteful. **P37 resolution:** the
director now notes coordination-only findings (`EmergencyNoted`,
`CoordinationOnlyNoted` counter) without creating a task or an Active
record — see `docs/EMERGENCY.md` §5. No executor churn is possible for
capability-less emergencies anymore.

### Observability note — flood guard drops lines under bursts (FIXED in P37)

`CapBotLog`'s 24/10 s global budget with silent drops is the documented
reason some transitions/`Recovery applied Fail` lines are absent from the
logs during emergency cascades. Counts in the table above are therefore
*lower bounds*. **P37 resolution:** budget raised to 96/10 s and Warning+
lines exempt from the global window (per-key dedup retained); first
occurrences of failures can no longer be dropped.

## Live validation matrix

Legend: ✔ = LIVE GAME VERIFIED this phase, ○ = INTEGRATION VERIFIED (static
proof only), · = NOT LIVE-VERIFIED (manual procedure below).

### Captain

| Behavior | Status | Evidence |
| --- | --- | --- |
| Captain order channels (orders 1/4/6/8/9/10/11/12/13 vocabulary) | ○ | P7 `PunRPC`-verified signatures; executor dispatch compile/IL-proven |
| ISSUE_MOVE_ORDER transient crew gather (P18 authoring) | · | manual M-C1 |
| Emergency order overrides (SET_CAPTAIN_ORDER via CoolantCritical) | ✔ | live chain Player.log:1196–1207 (#121) + prev #16–#88: detection→dispatch→`ExecutorResult SUCCESS`→`EmergencyResolved` |
| Emergency SET_CAPTAIN_TARGET | · | not observed live; manual M-C2 |
| Anti-spam dwell / cooldowns | ○ | P7 cooldown gate + OA/cadence tests |
| Movement/course/target control reach the game | ○ + · | course-goal RPCs dispatch+complete live (Navigation row); move orders M-C1, target M-C2 |

### Crew

| Behavior | Status | Evidence |
| --- | --- | --- |
| Bot spawn (10, /capbot), role assignment | · | manual M-CR1 |
| Role behavior execution (P7 dispatch → game RPCs) | · | manual M-CR1 |
| Personality derivation/maturation | ○ + · | P11/P25 unit-proven; in-game maturation needs long session (M-CR2) |
| Experience/memory accrual | ○ + · | P12/P13 unit-proven; in-game see M-CR2 |
| Task assignment/claims/duplicate protection | ✔ + ○ | P4/P5 unit-proven; live `ClaimAccepted`/`OwnershipReleased reason=Succeeded` chains (e.g. #121, #148, #177) |
| Failure recovery (retry/backoff/cancel, stuck detection) | ✔ + ○ | P3 unit-proven; live `Recovery applied Retry` (18+38) and `Cancel` (15+26); stuck-fail path verified by analysis (see L2), flood-dropped in logs |

### Missions

| Behavior | Status | Evidence |
| --- | --- | --- |
| Mission detection/reporting | ○ | P15 unit-proven; capture-failure semantics documented |
| Mission dialogue handling | · (out of scope) | no capability exists by design (P15 report-only mandate) |
| Mission work task authoring (P23) | · | manual M-M1 |

### Economy

| Behavior | Status | Evidence |
| --- | --- | --- |
| Fuel/coolant/missiles/credits tracking + affordability reports | ○ | P16 unit-proven |
| Component transactions/upgrades | · | manual M-E1 (report-only layer; legacy shop automation untouched) |

### Combat

| Behavior | Status | Evidence |
| --- | --- | --- |
| Hostile engagement reports / episode model | ○ | P17 unit-proven |
| Emergency retreat/disengagement | ○ + · | P9 rules unit-proven; in-game see M-C2 |

### Navigation

| Behavior | Status | Evidence |
| --- | --- | --- |
| Course-lost / goal-reached recovery authoring (P14) | ✔ + ○ | unit-proven; live full chains: `Dispatched REMOVE/ADD_COURSE_GOAL` → `ExecutorResult SUCCESS task=Completed` (#177 Player.log:1462–1474; 6+15 observed completions, 0 failures) |
| TLI transitions / interior movement | · | vanilla stack; no CapBot patches on it (P6 read-only) — see M-N1 |
| Stuck detection | ✔ + ○ | P6 PLBotController metrics verified; P14 rule unit-proven; advisory-only emergency fires live (see L3, fixed P37) — see M-N1 |

### Persistence

| Behavior | Status | Evidence |
| --- | --- | --- |
| Save/load round-trip | ○ | P28 unit-proven (insert-only, live-wins) |
| Corrupted/truncated payload | ○ | P28 all-or-nothing decode unit-proven |
| In-game save → restart → restore | · | manual M-P1 |

### Multiplayer

| Behavior | Status | Evidence |
| --- | --- | --- |
| Authority flips / claim-lease hygiene | ○ | P26 unit + IL-proven (pre-gate monitor) |
| Host / client behavior, duplicate-RPC protection | · | manual M-MP1 (needs 2+ clients) |
| Host migration | · | manual M-MP1 (audit-recognized unverified area) |

### Compatibility

| Behavior | Status | Evidence |
| --- | --- | --- |
| MoreBots class-0 crash guard | ○ + · | P27 IL-proven install path; live session confirms install decision ran (MoreBots absent → skip); runtime guard M-X1 |
| BetterAI / QualityImprover coexistence | ✔ + ○ | both mods loaded in both live sessions; CapBot ran 0 exceptions alongside them (coexistence live); deep behavior matrix M-X1 |
| ExpandedGalaxy / Progress_Editor / others in Mods folder | ✔ | loaded in both live sessions alongside CapBot, no conflicts in log |

### Ollama / Qwen

| Behavior | Status | Evidence |
| --- | --- | --- |
| Provider reachable (loopback, /api/chat) | ✔ | live probe this session |
| qwen3:latest response generation + validation | ✔ (post-fix) | probe 3: valid ADVICE line accepted by unmodified validator |
| Non-thinking model in-game advice (`qwen:latest`) | ✔ | live sessions: `OllamaAdvice` accepted; `CrewAdvice model=qwen:latest advice=ADVICE: Deploy TomKing to the Outpost 448.` (Player.log:705) |
| Timeout / fallback behavior | ○ | P20 unit-proven (null body = soft fault, 120 s back-off after 3 hard faults) |
| qwen3:latest in-game advice (L1 fix active in deployed DLL) | · | fix built+deployed (`b7862bfc…`) but the live session predated it; manual M-L1 with the new DLL |
| No unauthorized execution from LLM output | ✔ + ○ | P20/P32 recommend-only IL proofs; live: no task/order mutation follows any advice line |

### UI / Commands

| Behavior | Status | Evidence |
| --- | --- | --- |
| `/capbot`, `/cap`, `/updateall` command registration | ○ | IL/compile-proven; unchanged since release |
| `/capbotstatus` report shape | ○ | P29 unit-proven (deterministic order, per-source fail-safe) |
| Settings menu (toggles/cyclers/sliders) | ○ + · | P29 summary block unit-proven; layout see M-U1 |

## Manual test procedures (NOT LIVE-VERIFIED → reproducible)

All procedures assume: PULSAR launched, local game hosted (`HOST GAME`),
CapBot deployed (hash `b7862bfc…`), Ollama running with qwen3:latest.

**M-CR1 — spawn & core loop (gateway procedure; run first).**
1. Open chat, run `/capbot 10` then `/cap 2`.
2. Expected: 10 bots + captain spawn at ship; `/capbotstatus` reports
   crew size, executor ticks increasing, world freshness fresh.
3. Verify no errors in log (see Log locations).

**M-C1 — captain deliberation authoring.** Continue from M-CR1: order all
bots to a station far from the captain (vanilla command), wait ≥ 35 s
(15 s dwell + cadence). Expected log line `MoveOrderAuthored` then bots
receive a move order; counter `AuthoringsIssued` increments in
`/capbotstatus` detail.

**M-C2 — emergency override.** Damage own hull below 35% or spawn hostiles.
Expected: `EMERGENCY` subsystem lines (severity ladder), captain override
orders; recovery when safe. Deterministic rules must fire without any LLM.

**M-CR2 — personality/experience maturation.** Play a full session with
bot task completions; then `/quit` to menu, relaunch. Expected: matured
personalities and XP restored (P28); log lines from PERS restore path.

**M-CR3 — failure recovery.** Use `/capbotstatus` to watch a task to
failure (e.g. order a bot to an unreachable location). Expected:
`TASK` lifecycle lines: failure → recovery decision (retry or cancel per
policy) → no duplicate execution (claim ledger).

**M-M1 — mission work authoring.** Accept a mission with clear objectives;
keep ship calm. Expected after dwell: `PLAN:MISSIONWORK` opens (P22), then
a `MISSION_WORK` task appears in status; bots execute toward objectives.

**M-E1 — economy observation.** Dock at a store; buy fuel/components.
Expected: `ECONOMY` store-sector + delta reports; no player-visible economy
automation change (report-only).

**M-N1 — navigation recovery.** Issue a course goal to another sector, then
cancel it mid-flight (or let the ship arrive). Expected: `NAVIGATION`
CourseLost/GoalReached plans with authoring; `StuckStall` stays report-only.

**M-P1 — persistence round-trip.** After M-CR2 play, save+quit+reload.
Expected: experience/memory/personality rows restored; `/capbotstatus`
equal to pre-restart shape (modulo live task state).

**M-MP1 — multiplayer.** Host + 1 client (LAN or Steam). Verify: bots spawn
host-side only, client sees bot movement; disconnect/reconnect the client;
migrate host and back. Expected: `MPAuthorityLost`/`MPAuthorityRegained`
lines; no duplicate bots; vanilla AI uninterrupted after migration.

**M-X1 — compatibility coexistence.** With the full Mods folder loaded
(including BetterAI, QualityImprover, MoreBotsAI, ExpandedGalaxy,
Progress_Editor), run M-CR1. Expected: no Harmony patch conflicts in log
(`CapBot` patches = 11 classes), bots from both CapBot and MoreBots behave,
no crash on class-0 bot.

**M-L1 — in-game advisor emission.** Enable `OllamaAdvisorEnabled` +
`QwenAdvisorEnabled` in the CapBot settings menu, set model cycler to
`qwen3:latest`, play calmly ≥ 1 min. Expected: `OllamaAdvice model=qwen3:latest
advice=…` bounded lines in log; no task/order mutations follow from advice.

**M-U1 — settings menu.** Open CapBot settings in options; verify toggles
(Ollama/Qwen), model cycler shows 4 entries (qwen2.5/qwen/qwen2.5-coder/
qwen3), port slider, verbose-logging toggle, summary block.

## Log locations (for all procedures)

- Unity player log:
  `%USERPROFILE%\AppData\LocalLow\Leafy Games, LLC\PULSAR_LostColony\Player.log`
  (previous session kept as `Player-prev.log`).
- CapBot lines use subsystem tags (`CAPBOTLOG` prefix with `TASK`, `CREW`,
  `EMERGENCY`, `NAVIGATION`, `MISSION`, `ECONOMY`, `COMBAT`, `CAPTAIN`,
  `DECISION`, `OLLAMA`, `QWEN`, `PLANNING`, `MISSIONWORK`, `ADJUST`,
  `LEARNING`, `MP`, `COMPAT`, `PERS`, `CAPABILITY`, `UPDATE`, `PERF`).

## P36 verdict

- **Blocking defects found: 2** (D1 stale deployment, L1 qwen3 advisor
  starvation) — **both fixed and re-gated** (build OK; 2785/2785;
  reflection 37/0 + 0 FAIL; deployment parity verified).
- **Live-session execution:** two real game sessions mined (see Live-session
  evidence). Core pipeline verified LIVE end-to-end: emergency detection →
  task → grant → claim → dispatch → complete; NAV recovery dispatches;
  recovery retry/cancel; claim ledger; recommend-only advice. Suspected
  leak L2 investigated and **closed as not-a-defect** (evidence + policy
  analysis; `Expired=0` is correct ordering, `SweepExpired` superseded by
  recovery Tick). New tuning candidates recorded (L3 advisory-only
  emergency churn, flood-guard observability) — neither is a blocker.
- The in-game matrix above separates ✔ (live-observed this phase) from
  ○/· rows. Highest-value next live step remains M-CR1 (gateway) →
  M-C1/M-M1 (authoring paths) → M-L1 (qwen3 advice with the L1 build).
- **P37+ recommendation:** EXECUTED — P37 was the tuning phase: L3
  coordination-only suppression and the flood-guard budget/Warning+
  exemption are both landed and re-gated (2793/2793 ×3, build+deploy
  parity `2d3b2df6…`). The Stuck-detection matrix row above changes
  meaning from P37: the advisory-only emergency fires as `EmergencyNoted`
  (no task, no executor involvement) — M-N1 remains the manual procedure
  for the vanilla-stack behavior itself. Next live session should re-run
  M-CR1 → M-C1 → M-L1 (qwen3 advice with the L1+P37 build) and confirm
  the L3 churn lines (`no capability bound` on EMERGENCY tasks) are gone.