# Changelog

All notable changes to CapBot are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Phase 39 — Autonomy Stabilization / Loop Elimination] — unreleased (built from Alpha 1.2.2 source)

### Fixed
- **CoolantCritical no-op remediation loop (P38 live evidence: 134
  identical re-tasks of `EID:COOLANTCRITICAL:3f5584d3`).** Remediation
  (SET_CAPTAIN_ORDER order=9) succeeded every time but cannot refill
  coolant, so the same-severity condition was re-detected after every
  completion and re-created identical executor work forever. The
  EmergencyDirector now keeps a per-EmergencyId no-progress
  **SuppressionGate**: at most `MaxNoProgressResolutions=3` remediation
  attempts, then the identity is suppressed with a rate-limited
  (`SuppressionNotifyMs=60000`) `EmergencySuppressed` line. ANY severity
  change re-arms the breaker (the world moved); the gate expires only
  after `ActiveExpiryMs` with no re-detections (the condition is gone).
  A live-session defect in the first gate draft (suppressed re-detections
  did not refresh `LastSeenMs`, so the hygiene sweep expired the gate
  mid-suppression and restarted the cycle) was caught by live evidence
  and fixed — the gate stays warm on every re-detection, including
  suppressed ones.
- **NAV ADD/REMOVE course-goal oscillator (P38 live evidence: 122
  zero-effect recovery tasks, 79 ADD + 43 REMOVE, ~125 cycles).**
  Rule 1 (CourseLost) re-affirmed the CURRENT sector as a course goal;
  Rule 2 (GoalReached) instantly "reached" and removed it. CourseLost is
  now REPORT-ONLY: one bounded `NavCourseLostReport` per plan-open (same
  shape as StuckStall); the vanilla starmap owns unprompted routing.
  GoalReached removal stays task-bearing (it removes genuinely stale
  goals) with its dwell/requeue-block timing unchanged.

### Added
- **Bounded "nothing happens" diagnostics (mandate vocabulary).**
  `RecoveryActionType.StalledReport`: a Queued task un-granted for
  `StallReportAfterMs=30000` (~6 missed 5 s scheduler grant cycles)
  emits a rate-limited (`StallReportIntervalMs=60000`) `ACTION_STALLED`
  line through the existing recovery listener — report-only, never a
  lifecycle mutation (recovery must not queue work or pick tasks).
  `/capbotstatus` recovery line now carries `stalledReports=`.
- `/capbotstatus` emergency line now carries `suppressed=` and `gates=`
  (no-progress breaker state); navigation line now carries
  `courseLostReports=`.

### Documented
- `docs/LIVE_VALIDATION.md` P39 verdict: both loops eliminated at cause
  and LIVE-PASS verified in a fresh session (EmergencyTaskCreated frozen
  at exactly 3 then suppressed; NavRecoveryTaskCreated 0; NavCourseLostReport
  1; 0 exceptions; 292 log lines at the 10-minute mark vs 2996 in P38);
  mandate items mapped to existing architecture (replan stability, sector
  reconciliation, capability pre-validation, starvation audit).
- Tests: 2822/2822 (+S27 no-progress breaker lifecycle; N01/N02 rewritten
  for report-only CourseLost and task-bearing GoalReached; N09/N10/N12
  retargeted; +7 ACTION_STALLED assertions).

## [Phase 38 — Live re-validation of the P37 build (docs-only)] — unreleased (built from Alpha 1.2.2 source)

### Verified
- **P37 T1 LIVE-VERIFIED in a real session** (16:36 relaunch on the P37
  build `2d3b2df6…`): `EmergencyNoted` ×60, `no capability bound` churn
  lines ×**0** (previously the dominant noise), capability-backed
  CoolantCritical chains still complete end-to-end (#1–#182; 208 tasks
  registered, 110 emergency resolutions, 2996 CapBot log lines).
- **M-CR1 gateway EXECUTED live**: `/capbot` spawned the captain bot
  (`AgentCreated pid=3 bot role=Captain capt=1`), BotCount crew filled in
  (pid=4–6), MoreBots compat guard took the **present-mod path** live
  (`Safe AI-data prefix installed (replaces MoreBots GetAIDataPatch)`,
  `CompatInstall actions=1 installed=1 skipped=0`), CapBot's custom
  captain-order texts render in-world.
- **`/capbotstatus` LIVE-VERIFIED on screen (OCR)**: task history rows
  (#174–#182 all completed, live age counters), CrewAdvisor
  `sent=26 accepted=14 rejected=1` (matches logged `OllamaAdviceInvalid`),
  Compat counters (`faults=0 gateDeny=0 refused=0`), capability registry
  lines, bounded `truncated at 128 lines`.
- **Captain layer live**: `CaptainIntentOpened CAPTAIN:CREWGATHER`;
  `MoveOrderAuthored` needs a crew-divergence episode (none exists while
  followers are gathered — correct by design); divergent-path M-C1 stays
  manual.
- **Advice layer live on `qwen:latest`**: 26 sent / 14 accepted / 1
  correctly rejected. M-L1 (qwen3:latest) remains a manual step — the
  model cycler is main-menu UI only, not changeable mid-session.

### Documented
- `docs/LIVE_VALIDATION.md`: P38 verdict section; matrix rows upgraded to
  ✔ with live evidence (bot spawn, `/capbot` + `/capbotstatus`, MoreBots
  compat present-mod path, captain intent); M-CR1 corrected — `/capbot`
  takes NO arguments (spawns one captain bot; crew fills from the
  BotCount mod); procedures header now names the P37 deploy hash.
- Non-blocking observations for future tuning: `Locker swap failed`
  TRACE at captain-bot spawn; one `CaptainUncertain game not started`
  line after game start (snapshot race window before
  `SpawnBot.Execute` sets `GameHasStarted=true`).

## [Phase 37 — Tuning (coordination-only emergencies never executed; flood guard freed)] — unreleased (built from Alpha 1.2.2 source)

### Changed
- **Coordination-only emergencies no longer create tasks (P36 finding L3).**
  The two advisory-only emergency types (`NavigationFailure`,
  `ObjectiveCritical` — no capability wired, vanilla owns the response)
  previously ran the full executor path: fail `no capability bound` → one
  retry → cancel, every re-detection cycle (349 events across two live
  sessions). The director now **notes** them instead: `EmergencyNoted …`
  line, `CoordinationOnlyNoted` counter (exposed in `/capbotstatus` via
  `StatusLines`), no task, and no Active record (a record with no task
  could shed a REAL capability-backed emergency out of the bounded
  active set). The state machine still reacts to their Warning severity;
  dedup/escalation semantics for capability-backed emergencies untouched.
- **Flood guard no longer starves real bursts (P36 finding L2 root cause).**
  The global log budget was 24 messages / 10 s with silent drops — live
  emergency bursts exceeded it and hid `ExecutorResult`/recovery outcome
  lines, which is what fabricated the "NAV tasks never terminal" suspicion.
  Budget raised to 96/10 s, and Warning+ lines are exempt from the global
  window entirely (the 8 s per-key dedup still bounds per-frame fault
  storms, so first occurrences of Warning/Error/Critical can never be
  dropped).

### Added
- `EmergencyTests` S26: coordination-only findings create no task, no
  active record, emit `EmergencyNoted`, still move the state machine to
  Monitoring, and re-note (not dedup) on repeat passes. S21 updated to the
  new expectations (7 capability-backed tasks; nothing shed below cap).

### Verified
- Build OK (389,632 bytes); tests **2793/2793 ×3** (2785 + 8 S26
  assertions); reflection gates re-run: StatusHub census 37/0, advisor +
  patch census 0 FAIL, 11 Harmony patch classes, WorldTick IL 877.
- Deployed to game Mods with backup (`CapBot.dll.pre_p37.bak`); SHA256
  parity `2d3b2df6…` (repo == deployed).

## [Phase 36 — Live gameplay validation (first live-session evidence, qwen3 fix)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `docs/LIVE_VALIDATION.md` — the P36 record: verification vocabulary
  (UNIT TEST / INTEGRATION / LIVE GAME / NOT LIVE-VERIFIED), entry state,
  findings ledger, live-session evidence tables with log line counts,
  the per-domain live-validation matrix, and reproducible manual
  procedures M-CR1..M-U1 for everything not mechanically verifiable.

### Fixed
- **L1 — qwen3:latest returned empty advice**: qwen3 is a thinking model;
  its reasoning trace consumed the completion budget inside
  `message.thinking` (`content:""`, `done_reason:"length"` at 48 AND 256
  tokens — reproduced by live HTTP probe of the real Ollama service).
  Both advisors now append `"think":false` for thinking models
  (`OllamaAdvisor.IsRequestingModelThinking`; CrewAdvisor reuses it).
  Probe after fix: valid `ADVICE:` line, `done_reason:"stop"`.
- **D1 — deployed DLL was stale**: game `Mods/CapBot.dll` predated the
  final P35 build (size+hash mismatch). Re-deployed; parity re-verified;
  superseded by the L1 build (SHA256 `b7862bfc…`).

### Investigated (no code change)
- **L2 — "NAV_RECOVERY tasks never reach terminal state"**: DISPROVEN.
  Live logs contain full completion chains (e.g. #177:
  `Dispatched REMOVE_COURSE_GOAL` → `ExecutorResult SUCCESS
  task=Completed`; 21 observed NAV completions, 0 failures). The
  apparent leak was (a) outcome lines labeling capability, not task type,
  and (b) the CapBotLog flood guard (24 msgs/10 s, silent drops) hiding
  lines during emergency bursts. `Expired=0` is correct policy ordering:
  the stuck rule (15 s) precedes the timeout rule (120 s) and tasks
  resolve in seconds. `TaskRegistry.SweepExpired` is confirmed orphaned
  but functionally superseded by the wired `TaskRecoveryManager.Tick`
  — left as a documented helper (no duplicate expiry authority).

### Documented (tuning candidates, not blockers)
- **L3** — advisory-only emergencies (NavigationFailure,
  ObjectiveCritical; `RequiredCapability=""`) cycle fail→retry→cancel
  through the executor (349 `no capability bound` events across two live
  sessions). Bounded and fail-closed by design; P37 should suppress
  executor routing for capability-less findings.
- Flood-guard observability: burst-time line drops make log counts lower
  bounds; P37 may raise the budget / exempt ERROR+ levels.

### Verified (live, two real game sessions, 0 CapBot exceptions)
- Emergency pipeline end-to-end: CoolantCritical → task → grant → claim →
  `Dispatched SET_CAPTAIN_ORDER order=9` → `ExecutorResult SUCCESS` →
  `EmergencyResolved` (multiple instances, both sessions).
- NAV recovery dispatches (ADD/REMOVE_COURSE_GOAL) completing with
  `ExecutorResult SUCCESS task=Completed`.
- Recovery: bounded retry with backoff, cancel on retries exhausted; zero
  `ExecutorInvariant` violations.
- Advice recommend-only live: `OllamaAdvice` + `CrewAdvice` accepted;
  no task/order mutation follows any advice line.
- Compatibility: CapBot alongside BetterAI/QualityImprover/ExpandedGalaxy/
  Progress_Editor/Talents/UnlimitedCredits etc. — no conflicts (one
  third-party Talents-mod NRE documented as upstream).

## [Phase 35 — Final audit (verdict: PASS)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `docs/FINAL_AUDIT.md` — the end-to-end audit record: final verification
  battery results, the 12 master-prompt architecture invariants with
  their final proofs, the complete audit-findings ledger (C1/M1/M2/M4/
  M5/H2/L3 → resolutions), known limitations, and disposition.

### Verified (final battery, this phase)
- Reproducible build from a clean tree: **BUILD OK** (388,608 bytes,
  smoke checks passed — PE header, no machine-path/username embedding).
- Dev test suite ×3 consecutive: **`TOTAL passed=2777 failed=0`**
  (22 suites, 31 domain files).
- Consolidated invariant audit: **40/0** (30-type census, 11 patch
  classes, boot wiring, both advisors recommend-only).
- WorldTick IL order audit: **48/0** (monitor pre-gate, G1 wiring,
  ledger preservation — all intact after P31's Patch.cs edits).
- Git: clean tree, all phases locally committed, **nothing pushed**.

### Disposition
Final phase per plan. No push, no Workshop publishing, no release
upload, no distribution without separate owner authorization.

## [Phase 34 — Documentation (README parity, system overview)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `docs/OVERVIEW.md` — one-page phase-to-system map (P1–P33): the task
  pipeline foundation, crew layer, directors, judgment/advisory layer,
  hardening/platform phases, each linking its contract doc, plus the
  test inventory and remaining-plan status.
- README refresh: Architecture section (the one-way control pipeline +
  deny-by-default authority + advisory-only LLMs + /capbotstatus);
  crew-layer, multiplayer-hardening, and secure-updater feature
  summaries (P25/P26/P28/P30 shipped systems); adaptive-learning bullet
  updated to the P25 trait-maturation contract; commands table gains
  `/capbotstatus`.

### Fixed
- README "Building from source" no longer claims `dotnet build` + a
  hardcoded Steam path + a nonexistent PostBuild step; replaced with the
  P33 parameterized `build.ps1` contract (required `-PulsarManaged`,
  smoke checks, CI pointer). Docs now match shipped behavior.

### Verified
- Docs-only phase: no production changes; test gate unchanged
  (`TOTAL passed=2777 failed=0` ×3, last run P32/P33).
- Cross-checked every README claim against the phase contract docs
  (OVERVIEW.md §P26–P33) — no unbacked claims remain (the P27 M1 fix
  discipline held).

## [Phase 33 — Reproducible build/CI (parameterized build.ps1, smoke checks, GitHub Actions pipeline, no personal paths)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `build.ps1` (repo root) — reproducible machine-agnostic build entry
  point: REQUIRED `-PulsarManaged` parameter (validated: exists +
  Assembly-CSharp/PulsarModLoader/0Harmony present; note the game ships
  no `Pulsar.dll`), MSBuild auto-discovery (VS 2022/18/2019 BuildTools +
  Community) or `-MsBuildPath`, optional `-Clean`, nuget restore with a
  committed-`packages/` fallback, and smoke checks: DLL exists, ≥1 KB,
  MZ header, NO resolved `PulsarManaged` path embedded (Unicode scan),
  no build-machine username embedded. Gate line `BUILD OK dll=… bytes=…
  config=…`; failures exit non-zero with `BUILD FAILED: <reason>`.
  Smoke-validated: BUILD OK, 388 KB artifact, path passed as a PARAMETER
  and never written into any repo file.
- `ci/pipeline.yml` — GitHub Actions-style pipeline (windows-latest):
  checkout → setup-msbuild/nuget → `PULSAR_MANAGED` repo-variable game-
  DLL staging contract (fails if unset; game DLLs are NEVER committed) →
  restore → parameterized build → reproducibility smoke checks → test
  gate (3 consecutive clean `RUNNER: TOTAL passed=N failed=0`) →
  SHA-keyed artifact upload. The reflection verify scripts are
  deliberately NOT in CI (they require the live game install).
- `docs/BUILD.md` — the reproducibility contract: inputs (source +
  provided game DLLs + committed NuGet), invocation, determinism notes
  (`Deterministic=true`, `LangVersion 8.0`, net472), smoke checks,
  build.ps1 behavior, CI contract, and the explicit not-in-phase list
  (no signing/version-automation/release automation, no hardcoded paths).

### Verified
- Build infrastructure only — zero production changes; test gate
  unchanged (`TOTAL passed=2777 failed=0` ×3).
- `BUILD OK` achieved via the parameterized path end-to-end.

## [Phase 32 — Testing/QA (invariant regression suite)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `tests/QaInvariantTests.cs` (QA01–QA10, 45 assertions) + suite
  registration (31 domain files, 22 suites). Zero production changes —
  the phase is a pure test-and-audit pass consolidating the master
  prompt's architecture invariants into repeatable regressions:
  deny-by-default authority across ALL seams; LLM advisory-only
  (ADVICE: vocabulary gate + opaque round-trip); bounded-state census;
  end-to-end pipeline round trip with duplicate protection; recovery
  backoff monotonic/capped; persistence live-wins; cross-system
  determinism (byte-identical StatusHub reports from identical
  timelines); no-fabrication census; updater chain integrity; perf gate
  stamp discipline.
- `docs/QA.md` — the invariant census table (12 master rules → automated
  proofs), test inventory, and known-gaps register.
- `verify_build_p32.ps1` — consolidated reflection audit: full 30-type
  P2–P31 shipped-surface census; 11 patch classes; Mod-ctor wiring
  IL-proven (authority seam + MP monitor + compat manager) with the full
  preload set; BOTH advisors IL-proven recommend-only (zero pipeline-
  mutator references in any method body); command surfaces intact.

### Verified
- Tests: `TOTAL passed=2777 failed=0` ×3 consecutive (31 domain files,
  22 suites; QA suite 45/45).
- Reflection (`verify_build_p32.ps1`): 40/0 (see above).
- Build: unchanged production surface — Release build re-run clean.
- Run-1/2 defects: 3 test-authoring slips (missing using, ledger
  RecordOutcome returns bool, QA02 out-var overwritten). ZERO product
  defects found — all 12 invariants held.

## [Phase 31 — Performance (scene-scan gate, per-frame cost bounded)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Perf/SceneScanGate.cs` — per-key re-scan gate (audit H2):
  `Allow(key, nowMs)` allows when ≥250 ms passed since the key's stamp
  (first call arms; Allow NEVER advances the stamp — the engine owns
  Commit), `Commit(key, nowMs)` records the pass. Bounded MaxKeys=32
  fail-closed; null/empty keys refuse and never allocate; wrap-aware
  deltas; deterministic (no wall-clock reads). IL-verified pure domain.
  Zero allocations on the gated path.
- Handler wiring (Patch.cs, two lines each): the five scripted-sector
  handlers — `AtColony`, `WastedWing`, `AtRaces`, `PlanetExploration`,
  `HighRollers` — now gate their WHOLE body at a 250 ms cadence. These
  ran every frame with 2–5 full-scene `FindObjectsOfType` scans each
  (dozens of scene scans per frame in those sectors); now ≤4
  scans/sec per sector. Behavior preserved: last-set AI targets persist
  in PLBot between gated runs (game-side), movement stays fluid;
  `PlanetExploration`'s halt flag re-evaluates on the next gated pass.
  IL-proven: all five handlers call Allow with their exact key strings.
- Audit M4 re-verified as NOT current: BotEconomy/BotUpgrades per-tick
  allocations already run inside the 4-second slow-tick window
  (executor-gated) — documented in `docs/PERFORMANCE.md` §1.
- `tests/PerfGateTests.cs` (PERF01–PERF08, 63 assertions) + suite
  registration (30 domain files, 21 suites).
- `docs/PERFORMANCE.md` — the P31 contract (findings §1, gate §2,
  not-in-phase §3, tests §4, gotchas §5, verification §6).

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2732 failed=0` ×3 consecutive (suite now 30 domain
  files, 21 suites). Zero product bugs this phase; all 6 run-1..5 fixes
  were test-timeline authoring errors (non-monotonic timestamps crossing
  the 250 ms boundary; the gate stamp moves only on arm/Commit — the
  engine's Allow→Commit pattern is what arms the next window).
- Reflection (`verify_build_p31.ps1`): 24/0 — gate surface/consts
  (IntervalMs=250, MaxKeys=32); all five handlers IL-proven gated with
  their exact keys; gate IL purity; 11 patch classes; prior-phase types
  intact. Verify preloads extended (CrewAILibraryBuild/UnityEngine/Behave
  dlls) with per-method fault-safe body probes.

## [Phase 30 — Secure updater (verification chain, atomic staging)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Update/UpdatePolicy.cs` — pure C# verification policy for the mod
  updater (audit C1): HTTPS-only URL gate with a bounded 5-host
  GitHub-family allowlist (exact case-insensitive host match; userinfo
  spoofing refused; malformed refused); payload shape gate (non-null,
  1KB–32MB, MZ header — JSON/HTML error pages never install); SHA-256
  digest gate (mismatch/malformed digest REFUSES, never bypasses; absent
  digest = documented residual risk, publish digests with version
  files); strict file-name defense (ONLY bare `<name>.dll` accepted —
  path-shaped input refused, never flattened; never throws). IL-verified
  purity: zero PML/game/Harmony/WebClient/File references.
- `ModUpdater.cs` hardening: both fetch URLs gated BEFORE any connection;
  payload verified (shape + digest) BEFORE any write; `[blocked]`
  report lines + `blocked` tally for policy refusals; staged/installed
  target names sanitized; honest user-agent replaces the Chrome spoof
  (L3).
- `docs/SECURE_UPDATER.md` — the P30 contract (findings §1, policy §2,
  wiring §3, not-in-phase §4, tests §5, gotchas §6, verification §7).

### Fixed
- Staged update application is now ATOMIC (audit M5):
  `File.Replace(staged, target, backup)` replaces the delete-then-move
  pair that could leave a mod DLL deleted on a mid-operation failure;
  backup cleanup is best-effort; first-install path stays `File.Move`.

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2669 failed=0` ×3 consecutive (suite now 29 domain
  files, 20 suites; UPD01–UPD10, 55 assertions). Run-1 caught 1 REAL
  product bug before any release: the file-name sanitizer used
  `Path.GetFileName`, which THROWS ArgumentException on some `..`
  traversal shapes under .NET Framework — rewritten as never-throw
  strict refusal. SHA-256 reference vector (SHA-256("abc")) asserted.
- Reflection (`verify_build_p30.ps1`): 34/0 — policy surface/consts;
  UpdateAll IL gates through ALL five policy functions; staged-apply IL
  ORDER proof (File.Replace@147 → File.Delete@154 = backup cleanup only,
  M5); honest UA; policy IL purity (no network/file refs); 11 patch
  classes; prior-phase types intact.

## [Phase 29 — Status diagnostics (StatusHub, /capbotstatus, menu summary)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Diagnostics/StatusHub.cs` — single read-only aggregation point for
  the P2–P28 status surfaces. `Collect(int nowMs)` gathers bounded
  diagnostic lines in a fixed deterministic order (header → pipeline:
  registry/recovery/scheduler/claims/executor/world → directors →
  recommend-only advisors → compat → capability registry), hard-capped
  at MaxLines=128 with deterministic truncation, each source individually
  fail-safe (one faulting surface ⇒ one `StatusFault` line, never blocks
  the others). Authors NOTHING, mutates NOTHING — reads public readbacks
  only; IL-verified pure domain (zero game/PML/Harmony refs) AND
  read-only IL proof (no task registration/creation/queue/start, no
  claim acquire/record, no scheduler/executor/recovery ticks, no clear
  surfaces). No per-frame cost (runs only when called).
- `StatusCommand.cs` — `/capbotstatus` ChatCommand (PML CommandRouter).
  Guards: no local player ⇒ logged + ignored; non-host ⇒ refused (the
  report is master-authoritative process-local state). Success: one
  `Messaging.Echo` per StatusHub line. Read-only; whole command
  fail-safe.
- Executor additive status counters: `TaskExecutor.TickCallCount` (Tick
  calls passing the enabled gate) + `AttemptCount` (cumulative attempts)
  — bookkeeping only, counted under the existing lock, reset in
  ResetForTests, zero behavior change.
- Settings-menu summary (Config.cs): read-only "Status (host-side
  pipeline summary)" block — tasks live/history, claims, grants,
  executor ticks/attempts, crew agents, personalities, memory agents,
  compat actions; each value through a fault-safe Readback helper
  ("n/a" on fault — the menu can never break from a diagnostics fault).
  No new SaveValues.
- `tests/StatusDiagnosticsTests.cs` (SD01–SD10, 42 assertions) + suite
  registration (28 domain files, 19 suites).
- `docs/STATUS_DIAGNOSTICS.md` — the P29 contract (hub §1, executor
  counters §2, command §3, menu §4, not-in-phase §5, tests §6, gotchas
  §7, verification §8).

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2614 failed=0` ×3 consecutive (suite now 28 domain
  files, 19 suites). Run-1/2 findings fixed in-suite (all test-authoring
  bugs): registry StatusLines are bounded COUNTER lines (population
  changes values, not line counts); task history holds only TERMINAL
  tasks; claims deny-by-default requires SetAuthorityPolicy in tests;
  stale report variable after FreshSetup (SD07); nowMs-derived ages make
  byte-identical cross-collection equality the wrong invariant (SD10
  asserts structural shape instead).
- Reflection (`verify_build_p29.ps1`): 37/0 — hub surface/const;
  command derives ChatCommand; executor counters; hub IL purity + IL
  read-only proof (no lifecycle mutators/tick drivers); command IL calls
  Collect + Messaging.Echo; 11 patch classes; prior-phase types intact.

## [Phase 28 — Crew data persistence (insert-only restore, live wins)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Persistence/CrewPersistence.cs` — pure C# serializer for the
  cross-session crew data layers: P12 experience records (full), P13
  memory rows (full), and MATURED (explicit-source) P11 personalities.
  Derived personalities are NOT persisted (reproducible from the stable
  AgentId by design — the P11 derivation is a pure function); archetypes
  are NEVER persisted (re-derived from traits on restore); levels are
  NEVER trusted (recomputed from XP). Versioned format v1 ("CAPB" magic,
  u16 version, bounded sections, u16-length-prefixed null-safe UTF-8
  strings, 64K hard bound); decode is all-or-nothing — any structural
  fault returns null, live state untouched. IL-verified pure domain:
  zero PULSAR/PML/Harmony/game/pipeline references. No Harmony targets,
  no tick driver, no WorldTick change (11-class ceiling untouched).
- `CrewPersistenceAdapter.cs` — `CapBotCrewDataSave : PMLSaveData`
  ("CapBotCrewData", VersionID 1): thin production adapter; PML
  auto-discovers the subclass (the proven CapBotLearningSave pattern —
  no registration call). Fail-safe both ways (faulting Save ⇒ empty
  blob; faulting Load ⇒ never throws, never partial state). Legacy
  "CapBotLearning" XP slot UNTOUCHED. Logs via the existing PERSISTENCE
  subsystem.
- Registry additive export/restore surfaces (live state always wins):
  `CrewExperienceRegistry.ExportAll / RestoreRecord` (insert-only, Level
  recomputed from XP, outcome vocabulary enforced); 
  `CrewPersonalityRegistry.ExportMatured / RestoreMatured` (explicit-
  source rows only; traits validated 0..100; archetype re-derived;
  provenance stays "explicit"); `CrewMemorySystem.ExportAll / RestoreRow
  / RestoreRows` (whole-agent semantics against BATCH-START live state;
  per-row restore routes through the existing Upsert so validation and
  eviction rules stay the single authority).
- Restore semantics: INSERT-ONLY (a live record always wins — never
  overwritten or evicted; memory skips the WHOLE agent when any live
  memory existed at batch start); invalid payloads refused and counted,
  never fabricated (invalid id shape, unknown outcome vocabulary,
  out-of-range traits, negative counters, XP inconsistent with outcome
  counters).
- `tests/PersistenceTests.cs` (PERS01–PERS10, 53 assertions) + suite
  registration (27 domain files, 18 suites).
- `docs/PERSISTENCE.md` — the P28 contract (scope census §1, restore
  semantics §2, format §3, wiring §4, not-in-phase §5, tests §6, gotchas
  §7, verification §8).

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2572 failed=0` ×3 consecutive (suite now 27 domain
  files, 18 suites). Run-1 findings fixed in-suite: memory restore
  batch semantics (whole-agent decision against batch-start state, not
  per-row current state — MP08-style shared-state lesson), 1
  accidentally-dropped `CrewExperienceRegistry.StatusLines` method
  restored, adapter namespace fix (`PulsarModLoader.SaveData`, not the
  root namespace), corrupted-test-file rewrite (interrupted write had
  duplicated a block 233× — rewritten and verified single-pass before
  compiling).
- Reflection (`verify_build_p28.ps1`): 49/0 — serializer surface/consts
  (Magic=0x42504143, FormatVersion=1, MaxAgents=32, MaxMemoriesPerAgent=8,
  MaxEncodedLength=64K); adapter derives PMLSaveData and delegates
  Capture/Encode (Save) and Decode/Restore (Load); registry export/
  restore surfaces; serializer IL purity (zero forbidden refs); no
  Harmony surgery; Harmony patch classes == 11; prior-phase types intact.

## [Phase 27 — Compatibility manager (dispatch-and-audit registry)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Compatibility/CompatManager.cs` — central dispatch-and-audit
  registry for inter-mod compat actions. Actions are
  (name, modNames[], install): bounded (MaxActions=8, MaxModsPerAction=4),
  duplicate-name-safe (idempotent no-op), refusal-counted. Fail-closed
  mod-detection seam (`SetIsLoadedProvider` — boot wires PML `IsModLoaded`
  through try/catch ⇒ false; provider absent/faulting ⇒ nothing installs,
  `CompatGateUnavailable (deny-by-default)`). `InstallAll()` snapshots
  under lock, gates per action, invokes installs OUTSIDE all locks with
  per-action try/catch (one faulting action cannot block the others;
  `CompatActionFaulted` + `FaultCount`); manager-level idempotence across
  calls (production delegates additionally self-guarded). Bounded status
  surface (`StatusLines()` ≤12 lines; the P29 consumption surface) plus
  audited readback properties. Pure C# domain — IL-verified zero
  PulsarModLoader/HarmonyLib/PhotonNetwork/game/pipeline references (PML
  stays behind the provider seam). NO Harmony targets, NO tick driver,
  NO WorldTick change (11-class ceiling untouched).
- `Core/Compatibility/CompatLogBridge.cs` — decision listener onto the
  existing `COMPAT` log subsystem (the channel MoreBotsCompatPatch
  already logs through).
- `Mod.cs` P27 boot block — bridge + fail-closed PML provider seam + the
  single production action registration: `RegisterAction("MoreBots
  class-0 crash guard", ["MoreBots"], MoreBotsCompatPatch.Install)`.
- `Patch.cs` SpawnBot.Execute — dispatches `CompatManager.InstallAll()`
  instead of calling MoreBotsCompatPatch.Install() directly. Install
  TIMING unchanged (at `/capbot` spawn — the class-0 bot can only exist
  from that point; the crashing MoreBots prefix must be treated before
  the bot's first GetAIData frame). MoreBotsCompatPatch implementation,
  Harmony surgery, and self-idempotence guards are UNCHANGED.
- Audit results (documented in `docs/COMPATIBILITY.md` §1): F1 (audit
  M1, docs-truth) — README claims for TalentsModPerformanceImprovement
  ("UI helper replaced with a safe version") and ExpandedGalaxy
  ("boot-time crash guards") had NO code behind them; corrected to what
  is real (detection + settings-menu listing), with an explicit note that
  the earlier claims were incorrect — implementing untestable guards
  against third-party internals would violate the never-invent-APIs rule.
  F2 (audit M2, unchanged) — NonCaptainMenu button-list rebuild clobber
  risk documented as a standing conflict risk (behavior change out of
  scope). Verified sound: MoreBots compat is real and fail-safe;
  BetterAI/QualityImprover have no overlapping patch targets; world reads
  are QualityImprover-safe by construction; Harmony ordering discipline
  (11 all-postfix patch classes + one isolated runtime unpatch/replace,
  zero HarmonyPriority attributes).
- `tests/CompatManagerTests.cs` (CM01–CM10, 50 assertions) + suite
  registration (26 domain files, 17 suites).
- `docs/COMPATIBILITY.md` — the P27 contract (audit §1 with F1/F2, manager
  §2, not-in-phase §3, tests §4, gotchas §5, verification §6).

### Fixed
- README compatibility section corrected to match shipped behavior (M1):
  ExpandedGalaxy and TalentsModPerformanceImprovement reduced to
  detection+listing with explicit correction notes; MoreBots entry
  updated to describe the actual guard mechanism; Credits line for TMPI
  corrected likewise.

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2519 failed=0` ×3 consecutive (suite now 26 domain
  files, 17 suites). Run-1 findings fixed in-suite: 1 compile error
  (missing `LastPassed` property — suite-registration contract) + 1
  test-authoring rewrite (CM10's first draft layered two delegates over a
  shared counter — self-contradictory; rewritten to mirror the REAL
  production shape: one action, self-guarded delegate, same-name
  re-registration no-op).
- Reflection (`verify_build_p27.ps1`): 47/0 — manager type/members/consts
  (MaxActions=8 / MaxModsPerAction=4 / MaxStatusLines=12);
  MoreBotsCompatPatch intact; SpawnBot.Execute dispatches
  `CompatManager.InstallAll`; manager IL purity (zero forbidden refs);
  Mod-ctor wiring IL-probed; Harmony patch classes == 11; CapBotLog.COMPAT
  intact; prior-phase types intact. Verify-script preload fix (game PML
  file is `PulsarModLoader.dll`, not `PML.dll`; `ACTk.Runtime.dll` also
  required) — this upgraded the Mod-ctor probe from the P25/P26 SKIP to a
  real PASS.

## [Phase 26 — Multiplayer hardening (authority-flip monitor + transition hygiene)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/MultiplayerAuthorityMonitor.cs` — pre-gate authority-flip
  observer. Runs EVERY FRAME in the WorldTick Postfix BEFORE the
  host-only gate (on host AND client), watching the same authority seam
  the pipeline uses (`ExecutionClaims.IsAuthoritative` —
  `PhotonNetwork.isMasterClient` with fault→deny). First authoritative
  observation ARMS (never fires — the boot frame must not look like a
  flip); faulting probe ⇒ "unknown", no transition synthesized (counted
  only); true→false (AUTHORITY LOST) fires registered handlers OUTSIDE
  all locks, each in its own try/catch (registration order), emitting
  `MPAuthorityLost handlers=<n>`; false→true (REGAINED) emits
  `MPAuthorityRegained` and fires nothing (volatile state was already
  cleared at loss; the next authoritative pass rebuilds from world
  observation). Handlers bounded (≤16), duplicate-safe, individually
  fail-safe. The monitor owns NO gameplay semantics — it only dispatches
  to registered clear handlers. Steady state: one probe read, zero
  allocation.
- `Core/Tasks/MPLogBridge.cs` — boot attach of the monitor's decision
  listener onto the existing `TASK` log subsystem (additive).
- Authority-loss surfaces (additive, fail-safe):
  `ExecutionClaims.ClearForAuthorityLoss()` — drops claims only,
  **ledger preserved** (IL-verified NOT to touch `ActionLedger`, the P5
  duplicate truth), `ClaimsClearedAuthorityLost claims=<n> (ledger
  preserved)`; `TaskScheduler.ClearForAuthorityLoss()` — drops leases +
  preemption records only, task registry untouched,
  `SchedulerLeasesClearedAuthorityLost leases=<n>`;
  `CrewAgentRegistry.ClearForAuthorityLoss(nowMs)` — mirrors the P10
  TrackAuthority(false) clear (`AgentsClearedAuthorityLost`).
- `Mod.cs` P26 boot block — bridge + authority probe seam + the three
  production clear handlers registered with the monitor.
- `Patch.cs` WorldTick Postfix: `MultiplayerAuthorityMonitor.Observe()`
  pre-gate (every frame, individually guarded) and, in the host block,
  `ExecutionClaims.Tick(nowMs)` right after the executor tick — G1 fix:
  the P5 claim-lease hygiene existed since P5 but had NO production
  caller. Still 11 Harmony patch classes.
- Audit results (documented in `docs/MULTIPLAYER_HARDENING.md` §1):
  G1 claim-hygiene wiring gap (fixed above) and G2 — the P10
  authority-lost agent clear lived BEHIND the host gate, which stops
  running the moment authority is lost (dead-in-game since Phase 10;
  fixed by the pre-gate monitor). Verified sound: host-only gating of
  every tick driver, deny-by-default claims authority, per-director
  authority probes, dispatcher RPC discipline (single-send per logical
  action, no request-RPCs, no local+All duplicates), sticky-success
  duplicate protection, mid-attempt flip recovery path. Volatile-state
  census at authority flips documented (claims/leases/agents dropped;
  ledger/task registry/recovery/directors/experience kept, with
  rationale); passive host-migration behavior documented by construction.
- `tests/MultiplayerHardeningTests.cs` (MP01–MP10, ~78 assertions) +
  suite registration (25 domain files, 16 suites).
- `docs/MULTIPLAYER_HARDENING.md` — the P26 contract (audit §1 with
  findings + census, monitor §2, surfaces §3, driver wiring §4,
  not-in-phase §5, tests §6, gotchas §7, verification §7b).

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2469 failed=0` ×3 consecutive (suite now 25 domain
  files, 16 suites). Run-1 findings fixed in-suite: 1 implementation bug
  (monitor arm-pass stamping `m_HasLast` outside the lock — MP01 caught
  the arm looking like a false→true flip) + 3 test-authoring bugs
  (shared-probe state leakage MP04→MP07, closure display-class identity
  collapsing filler handlers MP08, ledger seeding path MP05 —
  `RecordExecutionResult` without a claim is `StaleCallbackIgnored` by
  design; seed via `ActionLedger.RecordOutcome`).
- Reflection (`verify_build_p26.ps1`): 48/0 — monitor type/members/consts
  (MaxHandlers=16); all three `ClearForAuthorityLoss` surfaces; WorldTick
  IL order probe@33 → observe@47 → dv@84 ⇒ monitor is pre-gate; claims-Tick
  wiring (G1) + all prior driver references intact; monitor IL purity (no
  game types, no pipeline mutators); claims clear provably does not touch
  the ledger; Harmony patch classes == 11; prior-phase types intact.

## [Phase 25 — Adaptive learning (bounded trait maturation)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Learning/AdaptiveLearningDirector.cs` — bounded, deterministic
  trait maturation: the "adjust/adaptive" phase the deferral trail assigns
  to P25 (inputs = CrewExperience Level/XP; write path = the P11
  `SetPersonality` sole-sanctioned channel; trigger = level crossings off
  the P10 ClearTask funnel). One bounded adjustment per level crossing,
  direction picked deterministically from the agent's OWN outcome mix at
  the crossing moment (completion-dominant => +1 Diligence; adversity-
  dominant => +1 Adaptability; neutral mix => crossing consumed silently,
  never fabricated). Baseline armed on the first readable pass (the P22
  premise-capture analogue — no adjustment on the arm pass). EVENT-DRIVEN:
  one additive fail-safe hook in `CrewAgentRegistry.ClearTask` after the
  P12/P13 hooks — NO new Harmony patch class (11-class ceiling untouched),
  no tick driver, no WorldTick block. Deny-by-default authority (null/
  faulting/non-authoritative probe => complete no-op; clients inert).
  Anti-churn structural: one decision per call, one crossing per level,
  levels bounded 1..10 (max 9 adjustments per agent career); 1 s
  evaluation gate absorbs outcome storms with the crossing persisting;
  hygiene decay at `ActiveExpiryMs=30000` with bounded history (≤16);
  bounded record set (≤32 == agent cap); ≤4 pending lines/pass. Clamp
  bound honored: a crossing whose trait sits at the bound is consumed
  with NO write (counted `Clamped`, never fabricated). Traits remain DATA
  until a consumer phase reads them (data-layer-before-consumer, the
  P15→P18 / P22→P23 pattern). Experience is READ-ONLY here
  (`SnapshotOf` defensive copy); the layer never accrues, removes, or
  fabricates experience. No config toggle (P18–P24 precedent).
- `Core/Learning/LearningLogBridge.cs` + `CapBotLog.LEARNING` — boot attach
  of the new `LEARNING` log subsystem (additive).
- `CrewExperienceRegistry.SnapshotOf` additive defensive-copy readback
  (under-lock copy; no torn reads for the one-way lock order; the P28
  persistence / P29 status consumption surface).
- `Mod.cs` P25 boot block (bridge + authority seam only — event-driven,
  no world/now seams needed) and `CapBot.csproj` +2 Compile entries.
- `tests/AdaptiveLearningTests.cs` (LEARN01–LEARN10, ~120 assertions) +
  suite registration (24 domain files, 15 suites).
- `docs/ADAPTIVE_LEARNING.md` — the P25 contract (rule table, NotifyOutcome
  flow, ownership argument, additive edits table, reproducibility contract
  gap — matured personalities are explicit-source and process-local with
  cross-session persistence assigned to P28 — LEARN01–LEARN10 inventory,
  test-design gotchas).

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2396 failed=0` ×3 consecutive (suite now 24 domain
  files, 15 suites). Run-1 findings fixed in-suite: one `task.Type` →
  `task.TaskType` compile fix; 4 assertion fixes all test-authoring bugs
  vs. documented semantics (cumulative CrossingCount across sub-scenarios
  sharing a FreshSetup; hygiene sweep-before-refresh counting; re-arm
  level = current experience level).
- Reflection (`verify_build_p25.ps1`): 76/0 — static class + nested
  `LearningRecord` + bridge; members/properties probed; 7 consts exact;
  IL ownership scans (zero forbidden refs: lifecycle mutators, scheduler/
  recovery/executor/claims/validator/dispatcher, Photon, scene scans,
  capability RPCs; sanctioned surface = SetPersonality write + SnapshotOf
  read + FromValues/IsValidAgentId); `ClearTask` IL references
  `AdaptiveLearningDirector.NotifyOutcome` (funnel hook); WorldTick
  postfix does NOT tick the learning director (event-driven by design);
  Harmony patch classes == 11; prior-phase surfaces intact. Mod ctor
  wiring compile-gated (type not loadable in the reduced-preload stage —
  probe limitation, documented).
- Phase-25 gate: P11 write path honored (sole sanctioned channel); P12
  inputs honored (Level/XP via the same funnel); deny-by-default held;
  zero new Harmony patches; zero new PULSAR APIs; 11-class ceiling held.

## [Phase 24 — Adjustment observer (bounded outcome readback)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Adjustment/AdjustmentDirector.cs` — the "self-adjustment /
  re-planning" phase shipped as DATA (the P24 research report verified the
  design basis: no master-plan mandate exists in the workspace; "re-targeting
  is re-planning, not recovery" is reserved by TASK_RECOVERY.md:95-96 and P3
  owns every task lifecycle mutation, and the capability covenant leaves no
  safe new authoring channel). A bounded deterministic OBSERVER that polls
  PUBLIC readbacks (TaskRegistry live snapshot; CaptainDirector /
  PlanningDirector bounded counters — the same public diagnostics the P18
  calm gate reads) on a 5 s cadence and emits bounded recommend-only
  signals: `ADJ:CHURN` (a live task is Failed or carries retries — the
  pipeline struggling with its own work), `ADJ:STARVE` (registry
  saturated at the live cap, or an authoring-refusal / capacity-gate
  counter DELTA since the previous readable pass), `ADJ:DRIFT` (P22
  premise-drift reports increased — premise-carrying task families may be
  stale, the planning-granularity mirror of the P19 stale-premise screens
  at meta granularity). Every signal line carries
  `(recommend-only; no behavior change in Phase 24)`. Anti-churn: one-shot
  record semantics (first true => report, persisting => silent refresh,
  re-fire => rate-limited by `AdjustmentRecheckBlockMs`=20000 from the
  last report, clear+decay => fresh record re-arms), first readable pass
  arms the baseline only, hygiene decay at `ActiveExpiryMs`=30000, bounded
  tracked set (≤8) + history (≤16) + ≤4 lines/pass + ≤3 task ids per
  churn detail. Poll-based by construction (every listener seam in the
  tree is single-slot and boot-occupied by LogBridges). Reads are
  fail-closed on seam faults; snapshots fail-safe per the shared 20 s
  standard. NEVER mutates a task, authors anything, or touches another
  phase's configuration (BackoffBaseMs/BackoffMultiplier are documented
  test-settable configuration — untouched). Downstream consumers (named
  by the tree): P25 adaptive learning, P28 persistence, P29 dashboard.
  No config toggle (P18/P22/P23 deterministic-director precedent).
- `Core/Adjustment/AdjustmentLogBridge.cs` + `CapBotLog.ADJUSTMENT` —
  boot attach of the new `ADJUSTMENT` log subsystem (additive).
- `Mod.cs` P24 boot block (bridge + authority/now/world seams) and
  `Patch.cs` WorldTick postfix block (guarded `Evaluate` after the P23
  block; 11 patch classes preserved). `CapBot.csproj` +2 Compile entries.
- `tests/AdjustmentDirectorTests.cs` (ADJ01–ADJ10, ~90 assertions) + suite
  registration (23 domain files, 14 suites).
- `docs/ADJUSTMENT_DIRECTOR.md` — the P24 contract + data-posture
  documentation (signal vocabulary, one-shot record semantics, MUST-NOT
  list, config/multiplayer posture, ADJ01–ADJ10 inventory, verification
  results).

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2275 failed=0` ×3 consecutive (runs 3–5; suite now
  23 domain files, 14 suites).
- Reflection (`verify_build_p24.ps1`): 80/0 — static class + nested
  `AdjustmentRecord` + bridge; 12 public + 4 private members probed; 13
  consts; IL ownership scans (zero forbidden refs: lifecycle mutators,
  scheduler/recovery/executor/claims/validator/dispatcher, Photon,
  scene scans, capability RPCs; reads = TaskRegistry.LiveSnapshot +
  LiveCount only); WorldTick postfix references `AdjustmentDirector.
  Evaluate`; Harmony patch classes == 11; P22/P23 types intact.

## [Phase 23 — Mission work director (dynamic task creation)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Planning/MissionWorkDirector.cs` — the authoring stage of the
  planning arc (P22 established the deterministic planning layer and
  documented its MISSIONWORK episode as "the trigger surface P23 will
  author tasks from"): a bounded deterministic CONSUMER of that surface
  that authors EXACTLY ONE task family bound to EXACTLY ONE capability —
  `MISSION_WORK` tasks (owner CAPTAIN, priority 4, 60 s timeout, 1 retry,
  Preemptible) bound to `ISSUE_MOVE_ORDER` with a SECTOR target = the
  CURRENT sector ("hold crew at the current position while mission work is
  pending"). Trigger surface (deterministic, two conditions): the P22
  episode formally opened (`GetIntent("PLAN:MISSIONWORK").OpenedReported`,
  fail-closed) + the work mission re-derived from the same snapshot with
  the identical P22 predicate and scan bounds (mission id rides as
  metadata, never parsed). Full P18 anti-churn discipline: per-record
  budget `MaxAuthoringsPerIntent`=3 (resets only on hygiene decay),
  `AuthoringDwellMs`=15000, `AuthoringRequeueBlockMs`=20000 re-armed from
  `ReconcileTasks` stamping terminal-or-vanished resolution, live-task
  suppression, calm gate (real P9/P14/P17 readbacks + hostiles/boarders/
  warp, fail-closed) + capacity gate (`LiveCount < MaxLiveTasks`=64 —
  reacts to pressure, never retry-storms; register/queue refusals count
  `AuthoringRefused` and re-arm on the next dwell window). Ownership
  argument re-audited this phase (fresh post-P23 census in
  `docs/MISSION_WORK_DIRECTOR.md`): ISSUE_MOVE_ORDER is the only safe
  channel (transient 20 s-TTL crew effect legacy never reads/writes);
  priority 4 keeps P23 serialized behind P18's priority-8 gather orders
  (aged ceiling 9 never beats the preemption margin over P18's base 8).
  Gate order (P18 house shape): authority deny-by-default ⇒ cadence 5 s ⇒
  snapshot fail-safe ⇒ bounded rules ⇒ ≤4 lines/pass. No config toggle.
- `Core/Planning/MissionWorkLogBridge.cs` + `CapBotLog.MISSIONWORK` — boot
  attach of the new `MISSIONWORK` log subsystem (additive).
- `Mod.cs` P23 boot block (bridge + authority/now/world seams) and
  `Patch.cs` WorldTick postfix block (guarded `Evaluate` +
  `ReconcileTasks` pair after the P22 planning block; 11 patch classes
  preserved; tasks authored this pass are first scheduled/executed on the
  following WorldTick — no same-tick race). `CapBot.csproj` +2 Compile.
- `docs/MISSION_WORK_DIRECTOR.md` — the P23 contract + fresh ownership
  audit artifact (author census per capability, ISSUE_MOVE_ORDER sharing
  justification, MUST-NOT list, gate order, constants, failure semantics,
  MW01–MW10 inventory, verification results).

### Changed
- `Core/Validation/DecisionValidator.cs` — additive: `MISSION_WORK` joins
  the author-premise stale-premise family list (`CAPTAIN_DELIB`,
  `NAV_RECOVERY`, `MISSION_WORK`). Shape screens are capability-keyed and
  already cover ISSUE_MOVE_ORDER tasks family-independently; the argument-
  mismatch screen applies as-is. No behavioral change to the existing
  families.
- `docs/DECISION_VALIDATOR.md` (family list), `docs/CAPABILITIES.md`
  (in-tree authorship note), `docs/PLANNING_DIRECTOR.md` (P23 shipped
  cross-ref) — additive documentation.

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2152 failed=0` ×3 consecutive runs (22 suites,
  MW01–MW10 added; run-1 harness fixes: DV stale-premise screen ordering,
  P22 two-pass re-open).
- Reflection (`verify_build_p23.ps1`): 73/0 — director surface, constants,
  IL ownership scans (zero forbidden refs; authoring path =
  `CapBotTask.Create` → `TaskRegistry.Register` → `TryQueue` +
  `TaskRegistry.Get` reads; PlanningDirector data-only invariant intact),
  DV family strings, `CapBotLog.MISSIONWORK`, patch classes == 11,
  WorldTick postfix IL 708 → 777.

## [Phase 22 — Planning director (deterministic situation assessment)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Planning/PlanningDirector.cs` — the first stage of the planning arc
  the contracts sketched (P6 names `planning` as an intended snapshot
  consumer; P4/P3/P10 explicitly-not lists defer "dynamic planning" to later
  phases): a bounded deterministic CONSUMER of the P6 snapshot that tracks
  planning situations as data and emits bounded decision lines. Two rules:
  **PREMISE_DRIFT** (planning-granularity mirror of the P19 stale-premise
  screens — premise = sector+warp captured at arm; divergence emits
  `PlanningPremiseDrift` and re-arms; warp end is not drift; unknown
  sentinels never arm/fire; anti-churn `DriftRecheckBlockMs`=15000 report
  rate-limit) and the **MISSIONWORK episode** (the trigger surface P23 will
  author tasks from: incomplete mission present + P18 calm-gate family —
  no P9 emergency, no P14 plan, no P17 record, no hostiles/boarders/warp —
  plus registry live-capacity headroom `LiveCount < MaxLiveTasks`=64; opens
  `PlanningIntentOpened PLAN:MISSIONWORK` exactly once after a one-cadence
  dwell; refreshes silent). DATA ONLY: **no task authored in Phase 22** —
  reflection-verified zero lifecycle methods declared on the type. Gate
  order (P18 house shape): authority deny-by-default ⇒ cadence 5 s ⇒
  snapshot fail-safe (null/never-captured/stale>20s/future/not-started ⇒
  `PlanningUncertain`) ⇒ bounded rules ⇒ ≤4 lines/pass. No config toggle
  (P18 deterministic-director precedent). Same-snapshot ⇒ same decisions
  (test-verified).
- `Core/Planning/PlanningLogBridge.cs` — boot attach of the new `PLANNING`
  log subsystem. `CapBotLog.cs` +`PLANNING` const (additive).
- `Mod.cs` boot wiring (bridge + authority/now/world seams — always-on by
  construction), `Patch.cs` WorldTick postfix guarded planning block after
  the P21 advisor block (still 11 Harmony patch classes — ceiling held,
  Postfix extended in place inside its own try/catch).
- `docs/PLANNING_DIRECTOR.md` — full contract incl. the design-basis note
  (no master-plan doc in the workspace; P22 shape [INFERRED] from the
  deferral trail + P18 template + P19 screen vocabulary; re-alignment
  candidate if the external PART 0–68 plan differs).
- `tests/PlanningDirectorTests.cs` — PD01–PD10 (~55 assertions): premise
  capture + sector drift (+reverse drift after the recheck window), warp
  start/end semantics, anti-churn block with premise re-capture, unknown-
  sentinel accounting, episode open/refresh/decay/re-open, real-P9/P14/P17
  calm-gate blocks + hostiles + warp, capacity gate (registry filled to the
  live cap blocks; frees ⇒ opens), fail-safe inputs, cadence, authority
  deny-by-default, Lines/StatusLines/GetIntent determinism, data-only proof
  (registry untouched), post-reset inert.

### Changed
- `CapBot.csproj` — +2 Compile entries (PlanningDirector, PlanningLogBridge).
- `run_tests.ps1` compiles 21 domain files + 12 test suites; suite runner
  sums f1..f21 (21 suites).

### Verified
- Build: MSBuild Release 0 warnings / 0 errors; reflection (`verify_build_p22.ps1`):
  211 types (180 named), PlanningDirector 65 members all probed, nested
  PlanningIntent/PlanningPremise, data-only scan clean, `CapBotLog.PLANNING`,
  11 patch classes, WorldTick postfix IL 673 → 708, `CapBot.Core.Planning`
  namespace present. Tests: `TOTAL passed=1997 failed=0` ×3 consecutive
  (suite 21 files).

## [Phase 21 — Crew advisor (Qwen integration, recommend-only)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Qwen/CrewAdvisor.cs` — the crew-domain completion of the P20 Ollama
  advisor (the Qwen integration): asks the SAME local server (loopback-only,
  shared port/model config) for ONE advisory line about the crew picture,
  built from the P10 crew agent hooks reserved since CREW_AGENTS.md
  (Role/RoleName, LastTaskOutcome, LastKnownTLIName) through the additive
  `CrewAgentRegistry.AgentViews()` readback. Structural mirror of P20:
  authority deny-by-default ⇒ enabled+transport ⇒ consume (lines fire
  immediately) ⇒ back-off ⇒ cadence (**8000 ms** — slower than P20's 5 s;
  crew picture changes slowly) ⇒ snapshot fail-safe (null/never-captured/
  stale>20s/future/not-started ⇒ uncertain) ⇒ single-flight (Interlocked
  CAS) ⇒ dispatch (crew prompt on the game thread ≤4000 chars, dedicated
  worker thread `CapBot-CrewAdvisor` runs HTTP, single-slot buffer
  `PendingResponseSet` marker, game thread consumes). Same shared advisory
  data contract: `OllamaAdvisor.ExtractContent` + `ValidateAdvice` (≥8 chars,
  ≤240, `ADVICE:` prefix, zero control chars), same `keep_alive:"30m"`
  `stream:false` `num_predict:48` `temperature:0.2`. Advice = DATA: one
  bounded log line `CrewAdvice`/`CrewAdviceInvalid`; never assigns tasks
  (no registry handles — only copied AgentViews), never mutates task
  pipeline state, never feeds the deterministic directors. Crew-specific
  MUST-NOT addition: no `AssignTask`/`ClearTask`/`AddCapabilityReference`
  calls. Back-off ladder (3 consecutive ⇒ 120 s); no auto-retry (consume-eval
  opens next cadence window — exactly one follow-up, asserted via the
  race-free `RequestsSent` counter).
- `Core/Qwen/CrewAdvisorLogBridge.cs` — boot attach of the new `QWEN` log
  subsystem. `CapBotLog.cs` +`QWEN` const (additive).
- `CrewAgentRegistry.AgentView`/`AgentViews()` — additive point-in-time
  readback (copied fields, deterministic AgentId order; never live agent
  references). No registry behavior change.
- `Config.cs` — `QwenAdvisorEnabled` (bool, **default false** =
  deny-by-default). Shares the P20 loopback host (hard-anchored), port and
  bounded model vocabulary; only the toggle is independent. Menu: one toggle
  button, no new text inputs.
- `Mod.cs` boot wiring (seams + shared `OllamaHttpTransport` + ApplyConfig),
  `Patch.cs` WorldTick postfix guarded crew-advisor block after the P20
  block (still 11 Harmony patch classes — ceiling held, Postfix extended in
  place inside its own try/catch).
- `docs/CREW_ADVISOR.md` — full contract incl. the design-basis note (no
  master-plan doc exists in the workspace; P21 shape inferred from the P20
  contract + audit constraints + CREW_AGENTS.md reserved hooks).
- `tests/CrewAdvisorTests.cs` — CA01–CA10 (~70 assertions): inert-by-
  construction, dispatch with a REAL P10-synced registry (assignment
  round-trip seeds LastTaskOutcome into the prompt), single-flight, advice
  consumed/validated, newline injection, soft-fault handling, snapshot
  fail-safe, prompt bounds + unknown-sentinel degradation (null-TLI agent ⇒
  `lastLoc=unknown`), readbacks + reset determinism. P20 race lessons
  carried over (`WaitForCall` vs `WaitForPark`; `RequestsSent` for retry
  invariants).

### Changed
- `tests/OllamaAdvisorTests.cs` — `FakeTransport` visibility `private` →
  `internal` (reused unchanged by the CA suite; same assembly).
- `run_tests.ps1` compiles 20 domain files + 11 test suites; suite runner
  `TaskRecoveryTests.cs` sums f1..f20.

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: TOTAL passed=1920 failed=0, three consecutive runs (raw-output
  FAIL grep = 0).
- Reflection (verify_build_p21.ps1): 205 types / 175 named; CrewAdvisor
  surface complete; AgentViews/AgentView present; internal reuse proven;
  Config seam + CapBotLog.QWEN; 11 Harmony patch classes intact;
  WorldTick postfix IL 603 → 673 bytes; namespace CapBot.Core.Qwen present.

## [Phase 20 — Ollama advisor (recommend-only, local)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Ollama/OllamaAdvisor.cs` — a recommend-only advisor against a LOCAL
  Ollama server: at most one request per 5 s cadence (wrap-safe), the world
  premise comes from the P6 snapshot ONLY (same fail-safe as P16–P19:
  null/never-captured/stale>20s/future/not-started ⇒ uncertain, no dispatch),
  prompt built on the game thread ≤ 4000 chars with unknown sentinels
  ("unknown"), request `stream:false` `keep_alive:"30m"` bounded
  `num_predict:48` `temperature:0.2`. Response parked by a dedicated
  background worker thread (single-flight via Interlocked CAS
  `s_WorkerRunning`; HTTP cold-load ~38–40 s measured — sync on the game
  thread is impossible) into a single-slot buffer (`PendingResponseSet` bool
  marker — a null body is a legitimate parked fault), consumed on a later
  game tick where advice is validated (≥8 chars, ≤240 truncated, `ADVICE:`
  prefix case-insensitive, ZERO control chars — newline injection rejected)
  and emitted as ONE bounded log line `OllamaAdvice model=<name> advice=...`
  or `OllamaAdviceInvalid model=<name> reason=...`. Advice is DATA: no task
  creation, no capabilities, no P19 validator calls, no orders —
  recommend-only by construction. Gates in order: authority
  (deny-by-default, fault = no-op) ⇒ enabled+transport ⇒ consume (lines fire
  immediately, never held hostage by gates) ⇒ back-off ⇒ cadence ⇒ snapshot
  fail-safe ⇒ single-flight ⇒ dispatch. Hard faults arm a back-off ladder
  (3 consecutive ⇒ 120 s cooldown); rejected advice never auto-retries (the
  consume-eval legitimately opens the next cadence window — exactly one
  follow-up, no loop). House director pattern (static + DirectorState +
  m_Lock + 5 fail-closed seams + bounded counters + Lines()/StatusLines()/
  HasPendingResponse() readbacks + ResetForTests clears state AND seams).
- `Core/Ollama/OllamaHttpTransport.cs` — the ONLY network code, the inverse
  of audit C1: host HARD-ANCHORED to loopback `127.0.0.1` (no host string,
  URL, or DNS name ever accepted from config or anywhere else; only the port
  is configurable, clamped 1..65535 default 11434). `HttpClient` (never
  WebClient) constructed once per transport lifetime (net472 best practice),
  `UseProxy=false`, `AllowAutoRedirect=false`, hard `Timeout` 90 s + per-call
  cancellation token; ALL failures return null across the seam (transport
  never throws). No boot-time network — nothing contacts Ollama until an
  enabled, wired advisor with authority + fresh snapshot is ticked.
- `Core/Ollama/AdvisorLogBridge.cs` — boot attach of the new `OLLAMA` log
  subsystem (DecisionLogBridge pattern). `CapBotLog.cs` +`OLLAMA` const
  (additive).
- `Config.cs` — `OllamaAdvisorEnabled` (bool, **default false** =
  deny-by-default), `OllamaModel` (int index into bounded `KnownModels` =
  {qwen2.5:latest (default), qwen:latest, qwen2.5-coder:latest} — never a
  free-form string, avoiding the first-string-`SaveValue` pitfall),
  `OllamaPort` (int, 11434). Menu: advisor toggle, model cycler (cycles the
  bounded table, never a text field), port slider (1024–65535).
- `Mod.cs` boot wiring (seams + transport + ApplyConfig), `Patch.cs`
  WorldTick postfix guarded advisor block (still 11 Harmony patch classes —
  ceiling held, Postfix extended in place inside the existing try/catch).
- `docs/OLLAMA_ADVISOR.md` — full contract: gates order, threading model,
  measured latencies, MUST-NOT list, test inventory.
- `tests/OllamaAdvisorTests.cs` — OA01–OA13 (13 suites, ~80 assertions):
  inert-by-construction, dispatch + request body shape (no URL, non-
  streaming, default model), single-flight, advice consumed/validated,
  newline injection rejected, soft-fault handling, snapshot fail-safe, prompt
  bounds + divergence/unknown sentinels, JSON extraction (real 0.33.3 shape,
  escapes, sibling keys), validation bounds, config clamps, readbacks +
  reset determinism. First multi-threaded suite: `WaitForCall` (worker
  entered transport — in-flight observable) vs `WaitForPark` (worker parked)
  are deliberately distinct; the consume-eval legitimately opens the next
  cadence window (consume runs before the cadence gate), so retry-loop
  invariants assert on the game-thread-only `RequestsSent` counter.

### Changed
- `run_tests.ps1` compiles 19 domain files + 10 test suites; suite runner
  `TaskRecoveryTests.cs` sums f1..f19.

### Verified
- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: TOTAL passed=1869 failed=0, three consecutive runs (raw-output
  FAIL grep = 0).
- Reflection (verify_build_p20.ps1): 198 types / 171 named; all 22 probed
  OllamaAdvisor members; KnownModels exact; ITransport.PostChatJson;
  loopback host + /api/chat anchored; 3 Config seams; CapBotLog.OLLAMA;
  11 Harmony patch classes intact; WorldTick postfix IL 533 → 603 bytes;
  namespace CapBot.Core.Ollama present.

## [Phase 19 — Decision validator (pre-dispatch diagnostics screen)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Validation/DecisionValidator.cs` — a diagnostics-only pre-dispatch
  validator: reviews QUEUED tasks that carry a `CapabilityId` metadata
  binding from the WorldTick Postfix IMMEDIATELY BEFORE `TaskScheduler.Tick`
  (screened tasks are still Queued — no race with grants/leases/claims) and
  emits bounded diagnostics when a task's shape contradicts what its
  dispatcher will do with it (dispatcher-only shape gaps the P7 registry
  ladder cannot see: ISSUE_MOVE_ORDER registry TargetReq=None lets a
  non-SECTOR task through to die post-start; SET_CAPTAIN_ORDER order id must
  be in the verified vanilla vocabulary {1,4,6,8,9,10,11,12,13};
  ADD/REMOVE_COURSE_GOAL SECTOR+int≥0) or when the author's world premise has
  gone stale (CAPTAIN_DELIB/NAV_RECOVERY authored FROM a snapshot: claimed
  sector vs current Navigation.CurrentSectorId with positive evidence only,
  InWarp invalidates the sector premise). **Fail-open on uncertainty** (any
  unreadable input — null/stale/future/not-started snapshot, nav missing,
  CurrentSectorId −1, unreadable target — produces an uncertain marker or a
  silent skip, NEVER a rejection; an uncertain validator must never be the
  reason healthy work dies), **fail-closed on action** (holds no task
  records, calls no lifecycle API — P3 recovery owns ALL cancel/fail/pause
  decisions; worst case = one diagnostic line). Deliberate non-duplication:
  never re-runs P7 gates 1-13, P3 rules, P5 claims, or galaxy/encounter
  membership (the dispatcher's job with real game data). Bounded sanity:
  Argument != TargetId on SECTOR tasks → `argument mismatch` (authoring bug
  surfaced, not fixed). House director pattern (static + DirectorState +
  m_Lock, 4 fail-closed seams, deny-by-default authority with fault = no-op,
  cadence 1s matching scheduler MinRecheckMs, snapshot fail-safe gates,
  bounded ≤4 lines/pass fired after scan, bounded counters + Lines()/
  StatusLines() readbacks, ResetForTests clears state AND seams). EMERGENCY
  tasks without a capability binding are skipped silently (known-fail-by-
  contract per Phase 9 §9 — not a validator concern). MaxStaleSnapshotMs=20s
  reuses the P16/P17/P18 director threshold — one shared freshness standard,
  NOT a third. Zero new game reads (everything flows through the P6
  snapshot); no new Harmony patch class (ceiling 11 held; the Evaluate call
  lives in the EXISTING WorldTick Postfix, IL 499 → 533).
- `Core/Validation/DecisionLogBridge.cs` — attaches CapBotLog as the
  validator's decision listener at boot (CaptainLogBridge pattern).
- `CapBotLog.DECISION` subsystem const (additive).
- `docs/DECISION_VALIDATOR.md` — full contract: the ownership argument (what
  it must NOT duplicate and why, with the two evidence-proven gap closures),
  screens, fail semantics, diagnostics vocabulary, gates, data flow,
  multiplayer authority model, performance contract, verified-API table,
  failure modes, tests, scope boundaries.
- `tests/DecisionValidatorTests.cs` — DV01–DV14 (78 assertions) covering the
  clean pass (incl. DV01b: the REAL P18 authoring path produces a task the
  validator passes with zero diagnostics), every dispatcher-only shape
  rejection, both stale-premise rejections (task verified left Queued —
  diagnostics-only), all five fail-open snapshot paths (zero rejections),
  uncertain premise data, authority deny-by-default (null/false/faulting
  probe), cadence (same-timestamp safe), argument-mismatch sanity, no-
  capability EMERGENCY skip, counters/readbacks determinism + ResetForTests.
  Hooked as f18 in TaskRecoveryTests (18 suites).

### Changed
- `Patch.cs` — ONE guarded block added to the WorldTick Postfix between the
  master gate and `TaskScheduler.Tick`: `DecisionValidator.Evaluate(nowMs)`
  in its own try/catch (`DECISION "Decision validator tick failed"`).
- `Mod.cs` — boot block after the P18 seams: `DecisionLogBridge.Ensure()` +
  authority/clock/world seam wiring (deny-by-default until set).
- `CapBot.csproj` — 2 new Compile entries.
- `tests/run_tests.ps1` — DecisionValidator.cs + DecisionValidatorTests.cs
  added (18 test suites).

## [Phase 18 — Captain deliberation director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Captain/CaptainDirector.cs` — the fusion + narrow-authoring layer
  ("Captain Brain 2.0" substrate): a bounded deterministic CONSUMER of the
  P6 snapshot and the P9/P14/P15/P16/P17 director readbacks that tracks
  captain-deliberation situations as data AND authors EXACTLY ONE task family
  bound to EXACTLY ONE capability — `ISSUE_MOVE_ORDER` (the ownership
  argument: zero in-tree authors, dispatcher SECTOR branch ready, transient
  20 s-TTL crew effect that self-heals; SET_CAPTAIN_ORDER / SET_CAPTAIN_TARGET
  / ADD·REMOVE·CLEAR_COURSE_GOALS are all legacy/P9/P14-owned and NOT safe).
  Single `CAPTAIN:CREWGATHER` intent record (bounded set ≤ 8, history ≤ 16,
  divergence absent past 30 s decays the intent with a one-shot
  `CaptainIntentExpired`, decision cadence 5 s). One-shot edge reports:
  `CaptainIntentOpened` (first readable divergence: an alive, alive-known,
  non-captain bot whose readable CurrentTLIName differs ordinally from the
  readable captain location), `MoveOrderAuthored #n` (after
  AuthoringDwellMs = 15 s of persisting divergence AND a pass through the
  fail-closed CALM GATE — P9 state Normal/Monitoring + zero actives, P14
  ActivePlanCount 0, P17 ActiveRecordCount 0, zero snapshot hostiles, no
  boarders (−1 sentinel blocks), not in warp, sector known: any unreadable
  input BLOCKS — the director creates/registers/queues one CAPTAIN_DELIB task
  through the REAL pipeline: owner CAPTAIN, priority 8 (top of the normal
  band — never preempts P14 20 / P9 110+), 60 s timeout, 1 retry,
  Preemptible metadata, CapabilityId=ISSUE_MOVE_ORDER, Argument=<current
  sector>, i.e. "gather crew to the captain's position"),
  `MoveOrderRefused` (register/queue refused — no retry storm), `CaptainUncertain`
  fail-safe lines, `CaptainShed` (defensive house-pattern parity). Crew-read
  fail-safe classification: unknown liveness/TLI = unknown input (never
  triggers), readable death = excluded-not-unknown (P9 owns the health
  ladder), unreadable captain with bots present = rule unknown. ReconcileTasks
  (P14-mirroring): terminal/vanished task stamps TaskResolvedMs; re-authoring
  re-arms after AuthoringRequeueBlockMs = 20 s; anti-churn cap
  MaxAuthoringsPerIntent = 3 per record lifetime (decay resets the budget).
  Live-task duplicate suppression. Counter readbacks + bounded deterministic
  diagnostics (Lines one per intent, StatusLines = 2, GetIntent live-record
  readback). ResetForTests nulls all 4 seams.
- `Core/Captain/CaptainLogBridge.cs` — attaches CapBotLog (CAPTAIN, existing
  const) as the director's decision listener at boot.
- `docs/CAPTAIN_DIRECTOR.md` — full contract: the one-capability ownership
  argument, rules, the calm gate, gates, lifecycle bookkeeping, data flow,
  task shape, authority model, performance, verified-API table, failure
  modes, tests, scope boundaries.
- `tests/CaptainTests.cs` — CT01–CT14 (78 assertions) covering authoring
  end-to-end through the REAL pipeline (full task shape asserted), live-task
  suppression + resolution + requeue + re-arm, dwell gate, calm gate (combat
  record blocks then clears; threats/warp/unknown-sector block), vanished-task
  reconciliation, anti-churn cap + decay budget reset, dead-bot exclusion,
  unknown crew data never triggering, fail-safe inputs, authority
  deny-by-default, cadence + counters + determinism. Hooked as f17
  (seventeen suites).

### Fixed
- **TEST-GATE HONESTY BUG (critical)**: `CombatTests.Run()` and
  `CaptainTests.Run()` returned a hardcoded `return 0;` instead of
  `return s_Failed;` — the TOTAL failed=0 gate LIED (P17's "1640/1640 first
  run" claim was inaccurate: 4 CS checks were failing but masked). Both gates
  now return the real failure count.
- 4 stale P17 CS expectations (tests were wrong, director semantics correct
  per docs/COMBAT_DIRECTOR.md): CS03 `EngagementReportCount` cumulative → 3
  (CS01 + CS02 re-arm + CS03 re-entry dwell); CS06/CS07 engagement-dwell
  co-fires counted → Eval 2/1 (dwell elapsed while boarders re-arm);
  CS11 cleared + vanished co-fire on the close pass → Eval 2.
- CT02/CT09 task-transition bugs: `TryComplete()` from Queued is ILLEGAL
  (Queued→Completed is not in the TaskTransitions table) — `TryStart()` first.
- CT06 same-timestamp cadence block: after `CombatDirector.ResetForTests()`
  the re-eval at the same virtual NowMs was cadence-blocked — Advance
  (MinRecheckMs) before the final eval.
- `CaptainDirector` unknown-input accounting: an unreadable captain WITH
  bots present now counts UnknownInputPasses (CT11 sub-case; sawBot tracking
  in the bot loop).

### Changed
- `CapBot.csproj` — +2 Compile entries (Core/Captain/CaptainDirector.cs,
  Core/Captain/CaptainLogBridge.cs) after the Combat entries.
- `Mod.cs` — Phase 18 boot block after the combat seams: CaptainLogBridge.Ensure();
  CaptainDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
  SetNowMsProvider(TaskClock.NowMs); SetWorldProvider(WorldStateService.Latest).
- `Patch.cs` — WorldTick Postfix gains TWO guarded blocks after the combat
  block: `CaptainDirector.Evaluate(TaskClock.NowMs)` +
  `CaptainDirector.ReconcileTasks(TaskClock.NowMs)` (each in its own
  try/catch → CapBotLog.Error; no new Harmony patch class — ceiling of 11
  preserved; IL 430 → 499 bytes).
- `tests/run_tests.ps1` — +CaptainDirector.cs in the domain compile list;
  +CaptainTests.cs last in the test list.
- `tests/TaskRecoveryTests.cs` — f17 hook + TOTAL line includes
  CaptainTests.LastPassed (17 suites). **TOTAL 1718/1718** (1640 prior + 78
  new). Reflection verify: types 187, named 163, harmony_patch_classes=11,
  CaptainDirector 56-member surface + 17 constants exact, all P6–P17
  surfaces intact.

## [Phase 17 — Combat director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Combat/CombatDirector.cs` — the combat-tracking layer (BOUNDED
  DETERMINISTIC REPORT-ONLY **by mandate**: the P7 catalog DOES contain one
  combat-adjacent capability — SET_CAPTAIN_TARGET, executor-dispatchable —
  but its authorship is owned by the P9 emergency director (DangerousCombat)
  and by the legacy captain tick (ComputeDesiredOrder / BoardEnemy / blind
  jump), so P17 produces the combat-side coordination record and creates NO
  tasks). Single `COMBAT:ENGAGEMENT` episode record (bounded set ≤ 8 =
  MaxActiveCombatRecords with deterministic oldest-shedding — defensive-only
  in this contract, house-pattern parity — history ≤ 16 = MaxHistory, hostile
  set empty past 60 s = ActiveExpiryMs decays the record with a one-shot
  `CombatVanished` report, decision cadence 5 s). One-shot edge reports:
  `CombatOpened` (first pass with ≥ 1 authoritative hostile — immediate, no
  dwell: P9 owns immediate severity; record-creation vocabulary, re-emits
  only after the record decays), `HostileEngagementReport` (after
  EngagementDwellMs = 15 s of a STABLE hostile picture — any composition
  change re-arms with a fresh dwell clock; carries hostile count, our/target
  combat levels (INFERRED semantics, data-only), the gap label mirroring
  P9's INFERRED 1.33 threat-readout const, and the player-ship hull
  fraction), `HostileClearedReport` (hostiles drop to zero: one-shot episode
  close; the tracked record survives — hygiene owns expiry — and re-entry
  re-opens the episode), `WarpEngagementReport` (hostiles present while the
  player ship is in warp — pure data picture, vanilla/legacy own all
  warp/escape behavior), `UnderFireReport` (player ship TookDamageRecently
  while hostiles present — pure data: P9 owns fire/hull SEVERITY), 
  `BoarderReport` (InvadersOnboardCount > 0 — independent of hostiles; data
  only: vanilla repel + legacy order-6 own the response), `CombatUncertain`
  fail-safe lines. Fail-safe gates: authority deny-by-default seam (clients
  never report), cadence, snapshot staleness (>20 s / future / never-captured
  / !GameStarted), unknown sentinels (NaN combat levels → "-" never a
  trigger; InvadersOnboardCount −1 → silent + UnknownInputPasses;
  TookDamageRecently false covers "not damaged" and "capture unknown"),
  zero-hostile passes are legitimate quiet passes (not unknowns). NO tasks,
  no ReconcileTasks, no RPCs, no target authorship, no weapon/fire APIs, no
  severity ladder, no config-slider wiring (audit H4: AIReactionSpeed /
  AIAccuracy / CombatEngageRange / CombatDisengageHealth are DEAD; the
  legacy blind-jump hull floor 0.2f + 60 s cooldown are documented as
  data-only constants). Counter readbacks + bounded deterministic
  diagnostics (Lines one per tracked record, StatusLines = 2).
  ResetForTests.
- `Core/Combat/CombatLogBridge.cs` — attaches CapBotLog (COMBAT, existing
  const) as the director's decision listener at boot (same pattern as the
  other phase bridges).
- `WorldSnapshot.cs` + `PulsarWorldSource.cs` — ADDITIVE P6 capture (P9
  ctor pattern): `ShipSnapshot.TookDamageRecently` (game-owned "took damage
  recently" window — `Time.time - LastTookDamageTime() < 10f`, compile-proven
  shipped Patch.cs:242) and `ThreatSnapshot.InvadersOnboardCount`
  (`playerShip.InvadersOnboard` — DLL reflection-verified public
  System.Int32 property; docs/EMERGENCY.md §165) — per-field try/catch
  RecordPartial, original ctors preserved verbatim (defaults false / −1),
  new ctors chain `: this(...)`.
- `docs/COMBAT_DIRECTOR.md` — full contract: report-only-by-mandate
  rationale (ownership argument), rules, gates, lifecycle bookkeeping, data
  flow, the additive capture table, authority model, audit honesty notes
  (H4 dead combat sliders), performance, verified-API table, failure modes,
  tests, scope boundaries.
- `tests/CombatTests.cs` — 77 assertions CS01–CS13: engagement episode
  end-to-end, composition-change re-arm (fresh dwell), episode close +
  live-record re-entry semantics, combat-level gap labeling
  (unfavorable/sub-threshold/NaN), warp-combat picture, under-fire episodes
  with re-arm, boarder episodes (independent of hostiles, −1 sentinel),
  quiet paths (zero hostiles = legitimate quiet), fail-safe inputs
  (null/stale/not-started/future), authority deny-by-default, vanished +
  expiry hygiene + bounded history (fresh-publish-after-advance discipline),
  cadence + counters + diagnostics determinism, additive-capture ctor
  regression. Hooked as f16 (sixteen suites); run_tests.ps1 compiles the
  combat domain file + test file with the rest. **TOTAL 1640/1640** (1563
  prior + 77 new).

### Changed
- `CapBot.csproj` — +2 Compile entries (Core/Combat/CombatDirector.cs,
  Core/Combat/CombatLogBridge.cs) after the Economy entries.
- `Mod.cs` — Phase 17 boot block after the economy seams: CombatLogBridge
  .Ensure() + authority/now/world seams (deny-by-default; no classifier
  seam needed this phase — hostile membership comes from the authoritative
  HostileShips list, no game enums consumed).
- `Patch.cs` — WorldTick Postfix: ONE new guarded block after the economy
  block (`CombatDirector.Evaluate` ONLY — IL 395 → 430 bytes; still 11
  Harmony patch classes, ceiling preserved; no ReconcileTasks — nothing to
  reconcile).
- `tests/run_tests.ps1` — compiles Core/Combat/CombatDirector.cs +
  tests/CombatTests.cs with the rest; `tests/TaskRecoveryTests.cs` — f16
  hook + sixteen-suite TOTAL/gate.

### Notes
- Zero new unverified APIs: the two additive captures are compile-proven
  (Patch.cs:242 window) or DLL reflection-verified (InvadersOnboard Int32
  property); everything else consumes existing P6 snapshot fields.
- Report-only by mandate (ownership argument — §1 of the contract doc);
  SET_CAPTAIN_TARGET authorship stays P9/legacy-owned; no new combat
  capability registered (P7 catalog stays at 7 built-ins).
- Combat-level semantics remain INFERRED (research §6.6) and data-only,
  mirroring the P9 EmergencyDetector precedent.
- Verification summary: Release build 0/0; tests 1640/1640 (16 suites);
  reflection 182 types / 159 named (P16 was 177/155), CombatDirector 53
  members + CombatRecord 7 members, all 15 constants exact, ShipSnapshot 13
  fields / 2 ctors, ThreatSnapshot 8 fields / 2 ctors, P6–P16 intact,
  harmony_patch_classes=11, WorldTick Postfix IL 430, namespace
  CapBot.Core.Combat present.

## [Phase 16 — Economy director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Economy/EconomyDirector.cs` — the economy-tracking layer (BOUNDED
  DETERMINISTIC REPORT-ONLY, P15 mirror): credits tracking in
  `ECONOMY:CREDITS` records (bounded set ≤ 8 = MaxActiveEconomyRecords with
  deterministic oldest-shedding by LastSeenMs (tie → lowest key order,
  `EconomyShed` line), history ≤ 16 = MaxHistory, credits unreadable past
  60 s = ActiveExpiryMs decays the record with a one-shot `EconomyVanished`
  report, decision cadence 5 s). One-shot edge reports: `EconomyOpened`
  (first readable credits), `CreditsLowReport` (credits ≤ ReserveFloor =
  2500 — the legacy CREDIT_RESERVE documentation, audit H4: the dead
  MinCreditsReserve slider is NOT wired — one report per episode re-armed by
  recovery), `CreditsDeltaReport` (change beyond MaxDeltaReportAbs = 10000;
  direction + magnitude only, NEVER cause inference — the snapshot cannot
  attribute deltas), `StoreSectorReport` (shop-class sector per the
  classifier SEAM — production wires the exact compile-proven
  ESectorVisualIndication list shipped Patch.cs:169-171 uses — + 15 s
  StoreDwellMs, one report per sector episode, exit closes/re-entry
  re-arms), `FuelAffordabilityReport`/`CoolantAffordabilityReport` (supply
  low mirroring the P9 warning thresholds as data constants ≤ 2 capsules /
  ≤ 30% AND captured unit price readable AND credits < price — one report
  per episode; pure data: P9 owns low-supply SEVERITY, legacy HandleShop
  owns all buying), `WarpTollReport` (snapshot WARP_STATION Price >
  readable credits — verified comparison shape of Patch.cs:2609 — 15 s
  dwell, cheapest unaffordable toll wins, Price ≤ 0 sentinels never
  trigger), `EconomyUncertain` fail-safe lines. Fail-safe gates: authority
  deny-by-default seam (clients never report; credits are MasterDerived),
  cadence, snapshot staleness (>20 s / future / never-captured /
  !GameStarted), unknown sentinels (Credits −1 / NaN coolant / −1 fuel /
  −1 prices / −1 sector never trigger — UnknownInputPasses counter),
  deny-by-default shop classifier seam (P10 SetRoleNameResolver pattern).
  NO tasks created (no economy capability exists in the P7 catalog; a task
  without CapabilityId metadata fails at start per the P8 executor
  contract — report-only by API-surface necessity, P15 mirror); no
  ReconcileTasks; no RPCs, no credit mutation (audit M10 patterns
  excluded), no CrewPurchaseLimitsEnabled replication, no PLTradeData
  (referenced nowhere — treated as nonexistent), no ShopRepMultiplier
  consumption (private helper, body never verified — base prices only).
  Counter readbacks + bounded deterministic diagnostics (Lines one per
  tracked record, StatusLines = 2). ResetForTests.
- `Core/Economy/EconomyLogBridge.cs` — attaches CapBotLog (ECONOMY, existing
  const) as the director's decision listener at boot (same pattern as the
  other phase bridges).
- `WorldSnapshot.cs` + `PulsarWorldSource.cs` — ADDITIVE P6 capture (P9
  ctor pattern): `ResourceSnapshot.FuelBasePrice` / `CoolantBasePrice`
  (-1 = unknown) filled from `(int)PLServer.GetFuelBasePrice()` /
  `(int)PLServer.GetCoolantBasePrice()` — both compile-proven in shipped
  Patch.cs HandleShop (lines 2220/2234) — per-field try/catch
  RecordPartial, original 5-arg ResourceSnapshot ctor preserved verbatim
  (defaults −1), new 7-arg ctor chains `: this(...)`.
- `docs/ECONOMY_DIRECTOR.md` — full contract: report-only rationale (API-
  surface argument), rules, gates, lifecycle bookkeeping, data flow, the
  additive capture table, authority model, audit honesty note (H4 dead
  slider), performance, verified-API table, failure modes, tests,
  deliberate scope boundaries.
- `tests/EconomyTests.cs` — 102 assertions covering ES01–ES13: credits
  end-to-end (open → low edge → recovery re-arm), delta reports (large ±,
  small suppressed, moderate-delta-crossing-the-band co-fire), store
  dwell/exit/re-entry episodes, affordability episodes (boundary
  credits==price is affordable), unknown-sentinel quiet paths (prices
  unknown; fuel sentinel with coolant still firing), warp-toll
  dwell/sentinels/cheapest-pick, fail-safe inputs (null/stale/not-started/
  future/unknown-credits), authority deny-by-default, vanished + expiry
  hygiene + bounded history (fresh-publish-after-advance discipline),
  classifier seam deny-by-default (null/faulting = never a shop), cadence +
  counters + diagnostics.

### Changed
- `Patch.cs` — WorldTick Postfix extended IN PLACE: after the mission
  block, `EconomyDirector.Evaluate(nowMs)` in its own try/catch
  (`CapBotLog.ECONOMY`). Postfix IL bytes 360 → 395 (expected change; no new
  patch class — the permanent ceiling of 11 is preserved).
- `CapBot.csproj` — +2 Compile entries (`Core\Economy\EconomyDirector.cs`,
  `Core\Economy\EconomyLogBridge.cs`).
- `Mod.cs` — Phase 16 boot block after the mission seams:
  `EconomyLogBridge.Ensure()` + authority/now/world seams + the
  shop-sector classifier wired to the compile-proven ESectorVisualIndication
  shop-class list.
- `tests/run_tests.ps1` — +1 domain compile entry
  (`Core\Economy\EconomyDirector.cs`) and +1 suite (`EconomyTests.cs`,
  last).
- `tests/TaskRecoveryTests.cs` — TestMain runs fifteen suites (`f15` =
  EconomyTests); TOTAL line updated.

### Notes
- Zero NEW unverified PULSAR APIs: the director reads only previously
  verified P6 snapshot sections plus the two additive price captures whose
  API surface was already compile-proven in shipped HandleShop code.
  `GetFuelBasePrice()`/`GetCoolantBasePrice()` are the first P6-capture uses
  of a whitelist API pair not previously captured (audit line 86).
- Report-only by API-surface necessity (identical shape to Phase 15): no
  economy capability exists in the P7 catalog, and the P8 executor rejects
  tasks with unbound CapabilityIds — an economy task would fail at
  execution by construction. Legacy BotEconomy/BotExtractor/HandleShop
  keep exclusive ownership of every economy action.
- Credit deltas are direction + magnitude only — no cause inference (the
  snapshot cannot attribute credits movement).
- Consumers are later phases (Captain Brain 2.0 planning); Phase 16
  implements no consumer beyond the bounded reports.
- Verified: build 0 warnings/0 errors; tests TOTAL 1563/1563 (fifteen
  suites; EconomyTests 102 assertions ES01–ES13); reflection 177 types/
  155 named, EconomyDirector 60 members + EconomyRecord nested type +
  all 15 constants exact (MinRecheckMs=5000, MaxActiveEconomyRecords=8,
  MaxHistory=16, ActiveExpiryMs=60000, MaxStaleSnapshotMs=20000,
  StoreDwellMs=15000, WarpTollDwellMs=15000, ReserveFloor=2500,
  MaxDeltaReportAbs=10000, FuelLowCapsules=2, CoolantLowPercent=30,
  MaxTextLen=120, TrackIdPrefix=ECONOMY:, TrackCredits=CREDITS,
  TargetKindEconomy=ECONOMY), ResourceSnapshot 2 fields + 2 ctors,
  P6–P15 intact, harmony_patch_classes=11, WorldTick Postfix IL 395 bytes
  (expected in-place growth from 360), new namespace CapBot.Core.Economy.

## [Phase 15 — Mission director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Missions/MissionDirector.cs` — the mission-tracking layer (BOUNDED
  DETERMINISTIC REPORT-ONLY): `MissionTrackRecord` rows (TrackId
  "MISSION:<typeId>", FirstSeen/LastSeen/LastProgress stamps, completed/
  total objective counters, UpdateCount, CompletedReported/AbandonedReported/
  StallReported one-shot flags, LatestObjectiveText bounded DATA carry ≤ 120
  chars — never parsed); `MissionDirector` (tracked set ≤ 8 =
  MaxActiveMissions with deterministic oldest-shedding by LastSeenMs
  (tie → lowest key order, `MissionShed` line), history ≤ 16 = MaxHistory,
  absent/terminal records decay after 60 s = ActiveExpiryMs, live present
  missions never expire, stall dwell 120 s = StallReportMs once per episode
  re-armed by progress, decision cadence 5 s). One-shot transition reports:
  MissionOpened (first sighting), MissionProgress (non-completing
  completed-count increase — one bounded report per edge, a completing
  increase emits MissionCompleted instead), MissionCompleted (all objectives
  done or Ended, incl. terminal-at-first-sighting), MissionAbandoned edge,
  MissionVanished (tracked mission absent from a readable list),
  MissionStallReport (120 s no-progress coordination record for later
  phases — never a task), MissionlessReport (edge-triggered, only after the
  session tracked a mission; pristine zero-mission sessions are quiet).
  Same-type-id instances collapse (first sighting wins, extra instances
  counted in SameTypeIdCollisions — audit L2 caveat now documented and
  counted). Fail-safe gates: authority deny-by-default seam (clients never
  report), cadence, snapshot staleness (>20 s / future / never-captured /
  !GameStarted → MissionUncertain line), unknown sentinels (TotalObjectives
  == 0 never completes or stalls). NO tasks created (the P7 catalog has no
  mission capability and the P8 executor rejects unbound CapabilityIds —
  report-only by API-surface necessity); no ReconcileTasks (nothing to
  reconcile); no RPCs, no dialogue interaction, no objective mutation (legacy
  audit C2 patterns excluded). Counter readbacks + bounded deterministic
  diagnostics (Lines ≤ one per tracked mission, StatusLines = 2). The
  WorldSnapshot ctors normalize a null missions list to empty (Bounded
  contract), so the null-section branch is defensive-only and absence rides
  the bounded MissionVanished path (documented capture-failure semantics).
  ResetForTests.
- `Core/Missions/MissionLogBridge.cs` — attaches CapBotLog (MISSION) as the
  director's decision listener at boot (same pattern as the other phase
  bridges).
- `docs/MISSION_DIRECTOR.md` — full contract: report-only rationale (API-
  surface argument), rules, gates, lifecycle bookkeeping, data flow,
  identity caveat, authority model, performance, verified-API table (zero
  new APIs), failure modes, tests, deliberate scope boundaries.
- `tests/MissionTests.cs` — 79 assertions covering MS01–MS13: tracking
  end-to-end (open → progress → completed, single report per edge, bounded
  DATA carry), transition suppression, abandonment edges,
  terminal-at-first-sighting, stall dwell (once per episode, re-armed by
  progress), missionless edge (pristine-session baseline quiet, never spam),
  capture-failure semantics (ctor normalization → absence path), fail-safe
  inputs (null/stale/not-started), authority deny-by-default (null/faulting/
  non-master), vanished reports + expiry hygiene + bounded history, bounded
  tracked set with deterministic shed-oldest, same-type collision counter,
  cadence + counters + diagnostics.

### Changed
- `Patch.cs` — WorldTick Postfix extended IN PLACE: after the navigation
  blocks, `MissionDirector.Evaluate(nowMs)` in its own try/catch
  (`CapBotLog.MISSION`). Postfix IL bytes 325 → 360 (expected change; no new
  patch class — the permanent ceiling of 11 is preserved).
- `CapBot.csproj` — +2 Compile entries (`Core\Missions\MissionDirector.cs`,
  `Core\Missions\MissionLogBridge.cs`).
- `Mod.cs` — Phase 15 boot block after the navigation seams:
  `MissionLogBridge.Ensure()` + authority/now/world seams
  (`ExecutionClaims.IsAuthoritative` / `TaskClock.NowMs` /
  `WorldStateService.Latest`).
- `tests/run_tests.ps1` — +1 domain compile entry
  (`Core\Missions\MissionDirector.cs`) and +1 suite (`MissionTests.cs`,
  last).
- `tests/TaskRecoveryTests.cs` — TestMain runs fourteen suites (`f14` =
  MissionTests); TOTAL line updated.

### Notes
- Zero NEW PULSAR APIs: the director reads only the P6 snapshot missions
  section (PLServer.AllMissions / PLMissionBase.Objectives /
  PLMissionObjective.IsCompleted/ObjectiveText — all previously verified).
  Mission RPCs, dialogue flows, objective writes, mission rewards/decline
  paths, and PLGlobalMission remain UNVERIFIED territory and are not used.
- Report-only by API-surface necessity: no mission capability exists in the
  P7 catalog, and the P8 executor rejects tasks with unbound CapabilityIds —
  a mission task would fail at execution by construction.
- Identity: snapshot missions carry only MissionTypeId (audit L2) — same-type
  concurrent missions are collapsed and the risk is counted, never silent.
- Consumers are later phases (Captain Brain 2.0 planning, Economy Director);
  Phase 15 implements no consumer beyond the bounded reports.
- Verified: build 0 warnings/0 errors; tests TOTAL 1461/1461 (fourteen
  suites; MissionTests 79 assertions MS01–MS13); reflection 172 types/
  151 named, P15 type surfaces + all 8 constants exact (MinRecheckMs=5000,
  MaxActiveMissions=8, MaxHistory=16, ActiveExpiryMs=60000,
  MaxStaleSnapshotMs=20000, StallReportMs=120000, TrackIdPrefix=MISSION:,
  TargetKindMission=MISSION), P6–P14 intact, harmony_patch_classes=11,
  WorldTick Postfix IL 360 bytes (expected in-place growth from 325), new
  namespace CapBot.Core.Missions.

## [Phase 14 — Navigation recovery] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Navigation/NavigationRecovery.cs` — the navigation recovery director
  (BOUNDED DETERMINISTIC COORDINATION ONLY): `NavRule` enum
  (CourseLost=1/GoalReached=2/StuckStall=3); `NavPlanRecord` plan rows
  (PlanId "NAV:<rule>:S<sectorId>", TaskId 0 = report-only,
  TaskResolvedMs −1 = unresolved, LastSeenMs, UpdateCount, HasLiveTask);
  `NavigationRecoveryDirector` (active plans ≤ 8 = MaxActivePlans with
  oldest-by-LastSeenMs shedding (tie → lowest key order, `NavPlanShed`
  line), history ≤ 16 = MaxHistory, un-refreshed plans expire after
  30 s = ActiveExpiryMs, re-arm blocked 20 s = RequeueBlockMs after task
  resolution, dwell hysteresis CourseLost 10 s / GoalReached 15 s, decision
  cadence 5 s = MinRecheckMs, recovery priority 20 — above normal work
  (1..8 + aging), below emergencies (110+)). Rules: CourseLost (no goals +
  not in warp + known sector, persisted ≥ 10 s → ADD_COURSE_GOAL task
  re-affirming the CURRENT sector — never an invented destination),
  GoalReached (first goal == current sector, persisted ≥ 15 s →
  REMOVE_COURSE_GOAL task), StuckStall (vanilla stuck signature: moved
  < 1 m in 5 s while seeking > 7 s WITH an active course → REPORT-ONLY
  plan + one NavStallReport line; a task without CapabilityId metadata
  fails at start per the P8 executor contract, so stalls are data records,
  never tasks). Task recipe: CAPTAIN-owned NAV_RECOVERY task (maxRetries 1,
  timeout 120 s) + metadata NavId/NavRule/Preemptible="true"/CapabilityId/
  Argument + Register + TryQueue through the standard pipeline.
  Fail-safe gates: authority (deny-by-default seam, fail-closed — clients
  never evaluate), cadence, snapshot staleness (>20 s / future /
  never-captured / !GameStarted → NavRecoveryUncertain), unknown rule
  inputs (NaN metrics / −1 sectors / in-warp / missing section never
  trigger). ReconcileTasks resolves plans whose task went terminal or
  vanished (plan re-arms after the requeue block); the task itself is
  never touched (lifecycle/recovery own it). Counter readbacks + bounded
  deterministic diagnostics (Lines ≤ one per active plan, StatusLines = 2).
  ResetForTests.
- `Core/Navigation/NavigationLogBridge.cs` — attaches CapBotLog
  (NAVIGATION) as the director's decision listener at boot (same pattern
  as EmergencyLogBridge/MemoryLogBridge).
- `docs/NAVIGATION_RECOVERY.md` — full contract: rules + dwell windows,
  plan/task shapes, lifecycle bookkeeping, data flow, integration points,
  authority model, performance, verified-API table (ADD_/REMOVE_COURSE_GOAL
  only — no new game APIs), failure modes, tests, deliberate scope
  boundaries.
- `tests/NavigationTests.cs` — 74 assertions covering N01–N12: CourseLost
  end-to-end (task shape, metadata, target = current sector, queued
  through the standard pipeline, duplicate suppression), dwell +
  requeue-block re-arm, GoalReached + negatives (second goal, in-warp,
  unknown sector), StuckStall report-only (no task ever, report once,
  not repeated), plan lifecycle (expire → history, bounded shed at 8 with
  NavPlanShed), fail-safe inputs (null/stale/not-started/NaN), authority
  deny-by-default (null/faulting/non-master), ReconcileTasks (vanished
  task → resolution), no unauthorized execution (task stays Queued, no
  lease, no claim, TryClaim → RejectedNotAuthoritative), pipeline
  isolation under nav churn, cadence + counters + diagnostics.

### Changed
- `Patch.cs` — WorldTick Postfix extended IN PLACE: after the emergency
  blocks, `NavigationRecoveryDirector.Evaluate(nowMs)` +
  `ReconcileTasks(nowMs)`, each in its own try/catch
  (`CapBotLog.NAVIGATION`). Postfix IL bytes 256 → 325 (expected change;
  no new patch class — the permanent ceiling of 11 is preserved).
- `CapBot.csproj` — +2 Compile entries
  (`Core\Navigation\NavigationRecovery.cs`,
  `Core\Navigation\NavigationLogBridge.cs`).
- `Mod.cs` — Phase 14 boot block after the memory seams:
  `NavigationLogBridge.Ensure()` +
  `NavigationRecoveryDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative)`
  + `SetNowMsProvider(TaskClock.NowMs)` +
  `SetWorldProvider(WorldStateService.Latest)`.
- `tests/run_tests.ps1` — +2 domain compile entries
  (`Core\Navigation\NavigationRecovery.cs`) and +1 suite
  (`NavigationTests.cs`, last).
- `tests/TaskRecoveryTests.cs` — TestMain runs thirteen suites (`f13` =
  NavigationTests); TOTAL line updated.

### Notes
- Zero NEW PULSAR APIs: course-goal mutation rides the already-verified P7
  capability channels (ADD_COURSE_GOAL → PLServer.AddCourseGoal(Int32)
  [PunRPC], REMOVE_COURSE_GOAL → PLServer.RemoveCourseGoal(Int32)
  [PunRPC]); the vanilla navigation stack (PLFlightAI/PLBotController/
  PLStarmap/m_ShipCourseGoals) is untouched.
- StuckStall is report-only by the P8 executor contract (a task without
  CapabilityId metadata → FailStarted): vanilla stuck-teleport owns
  physical unsticking; the director coordinates only.
- Recovery = re-affirming the CURRENT sector; the director never invents
  destinations and never replans warp.
- Consumers are later phases (Captain Brain 2.0 planning, mission
  director); Phase 14 implements no consumer beyond the bounded plan
  records.
- Verified: build 0 warnings/0 errors; tests TOTAL 1382/1382 (thirteen
  suites; NavigationTests 74 assertions N01–N12); reflection 167 types/
  147 named, P14 type surfaces + all 15 constants exact (MinRecheckMs=5000,
  MaxActivePlans=8, MaxHistory=16, RecoveryTaskTimeoutMs=120000,
  CourseLostDwellMs=10000, GoalDwellMs=15000, RequeueBlockMs=20000,
  ActiveExpiryMs=30000, MaxStaleSnapshotMs=20000, RecoveryPriority=20,
  TaskTypeRecovery=NAV_RECOVERY, OwnerCaptain=CAPTAIN,
  TargetKindSector=SECTOR, StuckDistMovedMeters=1,
  StuckTimeSeekingSec=7), NavRule values 1/2/3, P6–P13 intact,
  harmony_patch_classes=11, WorldTick Postfix IL 325 bytes (expected
  in-place growth from 256), new namespace CapBot.Core.Navigation.

## [Phase 13 — Crew memory] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Crew/CrewMemory.cs` — the memory layer (BOUNDED RECALLABLE DATA
  ONLY): `CrewMemoryEntry` fact rows (Kind = Location/TaskOutcome/CrewEvent,
  Text/Outcome payloads ≤32 chars, CreatedTimeMs/LastSeenMs stamps,
  UpdateCount) in per-agent rings; `CrewMemorySystem` (≤32 = MaxAgents
  agents, ≤8 = MaxMemoriesPerAgent entries per agent, deterministic upsert
  semantics — same key updates in place, new distinct fact evicts the
  oldest entry by LastSeenMs with tie → lowest insertion index; bounded
  registry refusal for NEW agents only; recall paths
  `Recall`/`RecallTaskOutcome`/`RecallAll` that stamp reads via the
  `SetNowMsProvider` clock seam (production: TaskClock.NowMs);
  `ForgetAgent` lifecycle hook; `StatsOf` + bounded diagnostics; listener
  lines collected under the lock and fired AFTER release; ResetForTests).
  No game references, no tick driver, no world reads, no
  scheduler/claims/executor/personality/experience influence.
- `Core/Crew/MemoryLogBridge.cs` — attaches CapBotLog (CREW) as the memory
  system's decision listener at boot (same pattern as
  CrewAgentLogBridge/PersonalityLogBridge/ExperienceLogBridge).
- `docs/CREW_MEMORY.md` — full contract: entry shape, write paths, upsert +
  eviction semantics, recall paths, registry rules, authority, performance,
  verified-API table (none used), security, future integration points,
  failure modes, tests.
- `tests/MemoryTests.cs` — 170 assertions covering the Phase 13 scenarios
  (M01–M12): end-to-end outcome memory through the real Sync funnel,
  location upsert + read stamping, crew-event text as DATA, bounded ring
  (8/agent, cross-kind oldest-by-LastSeenMs eviction, updates never evict),
  bounded registry (32-agent cap, refusal, slot freeing, existing-agent
  writes at cap), invalid-input refusal, no cross-agent contamination,
  scheduler/claims/priority/personality/experience isolation under churn,
  recall stamping via the clock seam + recall-favored eviction, fail-safe
  funnel (throwing listener and full-registry refusal leave agent state and
  task resolution untouched), ForgetAgent lifecycle + no resurrection, and
  upsert determinism (keys, kinds, later-outcome wins, stats).

### Changed
- `Core/Crew/CrewAgentRegistry.cs` — ClearTask extended additively: after
  the agent lock is released (next to the Phase 12 experience hook), a
  fail-safe memory write (`CrewMemorySystem.RememberTaskOutcome`) runs in
  its own try/catch — a faulting memory layer can never affect agent state
  or task resolution. Agent state shape and P10 semantics unchanged.
- `CapBot.csproj` — +2 Compile entries (`Core\Crew\CrewMemory.cs`,
  `Core\Crew\MemoryLogBridge.cs`).
- `Mod.cs` — Phase 13 boot block: `MemoryLogBridge.Ensure()` +
  `CrewMemorySystem.SetNowMsProvider(TaskClock.NowMs)`.
- `tests/run_tests.ps1` — +1 domain compile entry (`CrewMemory.cs`) and
  +1 suite (`MemoryTests.cs`).
- `tests/TaskRecoveryTests.cs` — TestMain runs twelve suites (`f12` =
  MemoryTests); TOTAL line updated.

### Notes
- Zero PULSAR APIs used (pure C# over the Phase 10 ClearTask funnel +
  explicit APIs); no Harmony patch change (still 11 patch classes; WorldTick
  Postfix 256 IL bytes unchanged).
- The P6 snapshot remains the only authoritative world observation —
  location memory is an explicit-API cache, no snapshot-path changes.
- Consumers are later phases (role preferences, planning, Captain Brain
  2.0, navigation recovery); Phase 13 implements no decision consumer.
- Verified: build 0 warnings/0 errors; tests TOTAL 1308/1308 (twelve
  suites; MemoryTests 170 assertions M01–M12); reflection 161 types/142
  named, P13 type surfaces + constants (32/8/32) exact, P6–P12 intact.

## [Phase 12 — Crew experience] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Crew/CrewExperience.cs` — the experience layer (DATA ONLY): bounded
  `CrewExperienceRecord` per agent (per-outcome counters, ExperiencePoints,
  deterministic Level 1..10 via fixed cumulative thresholds 0/50/120/220/350/
  510/710/950/1230/1550, LastOutcome/LastResultMs stamps, UpdateCount);
  `CrewExperienceRegistry` (≤32 = MaxRecords, lazy record creation on first
  accrued outcome, existing records keep accruing at cap, Remove lifecycle
  hook, LevelOf/Get lookups that never fabricate, bounded diagnostics,
  listener outside the lock, ResetForTests); points vocabulary = Phase 10
  static outcomes (COMPLETED=10, others=2, unknown refused −1). No game
  references, no tick driver, no world reads, no scheduler/claims/executor/
  personality influence.
- `Core/Crew/ExperienceLogBridge.cs` — attaches CapBotLog (CREW) as the
  experience registry's decision listener at boot (same pattern as
  CrewAgentLogBridge/PersonalityLogBridge).
- `docs/CREW_EXPERIENCE.md` — full contract: funnel, points/levels, registry
  rules, authority, performance, verified-API table (none used), security,
  future integration points, failure modes, tests.
- `tests/ExperienceTests.cs` — 108 assertions covering the Phase 12
  scenarios (X01–X12): end-to-end accrual through the real Sync funnel,
  deterministic level math and level crossing, points vocabulary with
  unknown-outcome refusal, per-outcome counters, registry stability +
  remove lifecycle, no cross-agent contamination, bounded registry (cap
  refusal, accrual-at-cap, slot freeing), invalid-input refusal,
  scheduler/claims/priority/personality isolation under churn, fail-safe
  funnel (throwing listener and full-registry refusal leave agent state and
  task resolution untouched), and ClearTask funnel regression with
  exactly-once accrual.

### Changed
- `Core/Crew/CrewAgentRegistry.cs` — ClearTask extended additively: after
  the agent lock is released, a fail-safe experience accrual
  (`CrewExperienceRegistry.RecordOutcome`) runs in try/catch — a faulting
  experience layer can never affect agent state or task resolution; accrual
  happens only when the clear actually happened (exactly-once). Agent state
  shape and P10 semantics unchanged.
- `CapBot.csproj` — +2 Compile entries (`Core\Crew\CrewExperience.cs`,
  `Core\Crew\ExperienceLogBridge.cs`).
- `Mod.cs` — Phase 12 boot block: `ExperienceLogBridge.Ensure()` only.
- `tests/run_tests.ps1` — compiles the experience domain file and
  `tests\ExperienceTests.cs` (eleven suites).
- `tests/TaskRecoveryTests.cs` — TestMain runs `ExperienceTests.Run()` as
  f11; TOTAL aggregates eleven suites.

### Notes
- Zero PULSAR APIs used (pure C# over the Phase 10 funnel).
- No new Harmony patch and no change to the WorldTick postfix (verified
  byte-identical, 256 IL bytes; 11 patch classes unchanged).
- No in-game behavior change beyond the data accrual itself: experience is
  read by nothing yet; scheduling, claims, priorities, task state, and
  personality records are proven unchanged under experience churn (test X10).
- Phase 25 (adaptive learning) owns trait adjustment; `SetPersonality`
  remains the only personality write path. Phase 28 (persistence) may
  serialize the bounded counters.

## [Phase 11 — Crew personalities] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Crew/CrewPersonality.cs` — the personality layer (DATA ONLY): five
  fixed traits (`Discipline/Boldness/Sociability/Diligence/Adaptability`,
  ints 0..100, closed vocabulary), immutable `CrewPersonality` records keyed
  by the Phase 10 stable AgentId, `PersonalityFactory` with three sources —
  identity-derived (deterministic FNV-1a over "P|<agentId>|<TRAIT-SALT>",
  byte-scaled to 0..100: same crew member always gets the same personality
  across rejoins/class changes/rounds, no randomness, no wall clock),
  explicit clamped values (future-phase hook), and neutral all-50;
  `PersonalityArchetypes` static vocabulary (SENTINEL/VANGUARD/COORDINATOR/
  TECHNICIAN/ADAPTER/BALANCED, dominant ≥70, ties → first trait in enum
  order); `RoleAffinity` deterministic 0..100 per-role scores from fixed
  weight tables summing to 100 (Unknown/Other uniform; null ⇒ −1; tables
  returned as defensive copies); `CrewPersonalityRegistry` bounded ≤32
  (= MaxAgents) with identity-integrity writes (record.AgentId must equal
  key), derive-or-existing `DeriveFor` (never silent replacement), explicit
  replace counted, Remove lifecycle hook, derive-on-demand affinity/archetype
  conveniences, bounded diagnostics, listener fired outside the lock,
  ResetForTests. No game references, no tick driver, no world reads, no
  task/claim/scheduler/executor influence.
- `Core/Crew/PersonalityLogBridge.cs` — attaches CapBotLog (CREW) as the
  registry's decision listener at boot (same pattern as CrewAgentLogBridge).
- `docs/CREW_PERSONALITIES.md` — full contract: what a personality is/is not,
  deterministic derivation, archetypes, role affinity, registry rules,
  authority/multiplayer, performance, verified-API table (none used),
  security, future integration points, failure modes, tests.
- `tests/PersonalityTests.cs` — 116 assertions covering the Phase 11
  scenarios (P01–P12): deterministic identity-derived personalities and
  distinctness across agents, archetype tokens (incl. threshold tie-first),
  role-differentiated affinity with bounded scores and immutable weight
  tables, clamping and invalid-input refusal, explicit assign/replace
  counting, neutral + remove lifecycle, registry stability across time,
  no cross-agent contamination (bot/human distinctness), scheduler/claims
  isolation under personality churn, no cross-round drift after reset,
  bounded registry (cap refusal, replacement-at-cap, slot freeing), and
  no invalid-data ingestion (forged archetypes, stolen records).

### Changed
- `CapBot.csproj` — +2 Compile entries (`Core\Crew\CrewPersonality.cs`,
  `Core\Crew\PersonalityLogBridge.cs`).
- `Mod.cs` — Phase 11 boot block: `PersonalityLogBridge.Ensure()` only
  (the layer is inert data; consumers are later phases).
- `tests/run_tests.ps1` — compiles the personality domain file and
  `tests\PersonalityTests.cs` (ten suites; TOTAL gate unchanged).
- `tests/TaskRecoveryTests.cs` — TestMain runs `PersonalityTests.Run()` as
  f10; TOTAL aggregates ten suites.

### Notes
- Zero PULSAR APIs used by the personality layer (pure C# over the P10 agent
  identity). The research doc's PLAIIO/AIData profile channel was deliberately
  NOT used — it would touch the vanilla AI brain-swap surface and vanilla
  save files (out of scope for an additive bounded data layer).
- No new Harmony patch and no change to the WorldTick postfix (verified
  byte-identical, 256 IL bytes; 11 patch classes unchanged).
- No in-game behavior change: personalities are derived on demand and read
  by nothing yet; scheduler grants, owner-busy gating, claims, and task
  priorities are proven unchanged under personality churn (test P09).
- Phase 12 (experience) is NOT implemented here: traits are static derived
  values; `SetPersonality` exists only as the future write path.

## [Phase 10 — Crew agents] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Crew/CrewAgent.cs` — the agent model: bounded data record for one
  crew member (bot or human) with stable identity (`AgentId` = "AGT:<hash8>"
  via the shared FNV-1a `ActionIdentity.ComputeStableHash` over seed
  "B|<playerId>"/"H|<playerId>" — deterministic across rejoins, immune to
  name/class changes), PULSAR identity reference (`PlayerId`),
  role model (`ClassId` raw + bounded `CrewRole` vocabulary
  Captain/Pilot/Scientist/Weapons/Engineer mapped from the verified class
  ids 0–4, unknown→Other, absent→Unknown; `RoleName` resolved data-only
  through the verified static channel `PLPlayer.GetClassNameFromID`),
  captain flag, lifecycle (Active/Inactive/Removed + Created/LastSync/
  AbsentSince stamps), world association (LastKnownTLIName cached
  observation), task relationship (CurrentTaskId/Type/CapabilityId/
  AssignedMs + LastTaskOutcome/ResultMs — assignment metadata and read-only
  observation only), bounded capability-reference list (≤8, data only,
  future-phase integration point) and bounded diagnostics. No
  game-object references, no PLPlayer duplication, no wall-clock reads.
- `Core/Crew/CrewAgentRegistry.cs` — the per-bot state registry keyed by
  stable AgentId (never shared statics): bounded-cadence sync (1 s gate,
  host-only in the shared WorldTick postfix, individually guarded) diffs
  the crew section of the authoritative P6 snapshot — create / update /
  deactivate (absent) / reactivate / remove (15 s grace) / bounded
  history (≤16); agents ≤32, capability refs ≤8; deny-by-default
  authority seam (no probe / faulting probe ⇒ no-op; authority loss
  CLEARS the live map so clients never keep stale authoritative state);
  fail-safe gates (null/never-captured/stale >20 s/future-dated/
  !GameStarted snapshots → no-op with uncertainty logged; null crew
  entries skipped); deterministic lookups (GetAgent/FindByPlayerId);
  task-assignment surface (AssignTask/ClearTask — metadata only, never
  creates/claims/executes; terminal outcomes observed read-only via
  TaskRegistry.Get, Failed-retryable stays assigned, vanished cleared
  after 10 s grace); role-name resolver seam; bounded status/agent
  lines; ResetForTests.
- `Core/Crew/CrewAgentLogBridge.cs` — boots the registry's decision
  listener onto CapBotLog (CREW subsystem) at mod construction.
- `docs/CREW_AGENTS.md` — the full crew-agent contract: identity,
  lifecycle, role model, world-state relationship, task relationship,
  authority/multiplayer behavior, performance bounds, verified-API table,
  future personality/memory integration points, failure modes, tests.
- `tests/CrewAgentTests.cs` — 108 assertions covering all 20 mandated
  scenarios (stable creation, duplicate prevention, bot removal, stale
  reference, captain identification, role mapping, multi-agent isolation,
  task ownership, scheduler/recovery/claims/emergency interaction,
  invalid-player handling, deterministic lookup, bounded registry,
  join/leave lifecycle, captain change, client/master authority, no
  cross-agent contamination, no unauthorized execution, fail-safe gates).

### Changed
- `CapBot.csproj` — three Compile entries for the Crew domain.
- `Mod.cs` — Phase 10 boot block: crew logging bridge + delegate-wired
  seams (authority probe = ExecutionClaims.IsAuthoritative, clock =
  TaskClock.NowMs, world = WorldStateService.Latest, role-name resolver =
  PLPlayer.GetClassNameFromID, fail-safe wrapped). Registry stays INERT
  until the tick driver calls Sync host-side.
- `Patch.cs` — the shared `WorldTick` postfix (PLController.Update)
  extended IN PLACE (still 11 Harmony patch classes): host-only,
  exception-guarded `CrewAgentRegistry.Sync(TaskClock.NowMs)` call after
  the P9 emergency calls; header comment documents Phase 10.
- `tests/run_tests.ps1` — compiles the two Crew domain files and the
  ninth suite; header updated.
- `tests/TaskRecoveryTests.cs` — TestMain runs f9 = CrewAgentTests;
  TOTAL/return gate covers nine suites.

### Notes
- No PULSAR API outside the verified set is touched: the registry's own
  code path consumes WorldSnapshot data only; the role-name resolver is
  the verified static public `PLPlayer.GetClassNameFromID(Int32)`.
- PLPlayer priority management, PLBot behavior trees, PLBotController
  movement, PLFlightAI, RPC patterns, MoreBotsCompatPatch, BotAppearanceFix
  cosmetics and the PML save format are untouched. No competing movement
  AI, no per-frame AI-target manipulation.
- Phase 11+ work (personality, memory, learning, Mission/Economy/Combat
  directors, Captain Brain 2.0, LLM) is NOT implemented.

## [Phase 9 — Emergency director] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Emergency/EmergencyState.cs` — the deterministic override-layer
  vocabulary: `EmergencySeverity` (None/Warning/Elevated/Severe/Critical),
  `EmergencyType` (CriticalHull, CriticalCrewHealth, Fire, ReactorCritical,
  DangerousCombat, ImminentDeath reserved, NavigationFailure, FuelCritical,
  CoolantCritical, ObjectiveCritical; WarpFailure deliberately NOT detected —
  no verified failing-vs-charging rule), the six-state `EmergencyState`
  machine with the legal-transition table (`EmergencyStates.CanTransition`
  + `IllegalReason`; Normal→Monitoring→Warning→Emergency→Critical→Recovery→
  Normal, Recovery the only re-entry to Normal), `EmergencyPrecedence`
  (mandated 9-class order: crew survival > ship survival > catastrophe >
  combat > navigation > mission > economy > maintenance; priority =
  100 + class*10 + severity bump — normal work tops out at 13 with aging,
  so any emergency outranks all normal tasks), the bounded immutable
  `EmergencyDecision` (reason ≤ 200 / target ≤ 64, all 15 mandated fields),
  `ActiveEmergency` (dedup record with LastSeenMs/escalating severity) and
  `EmergencyIdentity` ("EID:<TYPE>:<hash8>" via the shared FNV-1a
  `ActionIdentity.ComputeStableHash` — stable across re-evaluations).
- `Core/Emergency/EmergencyDetector.cs` — the pure rule engine over the P6
  snapshot (zero game access, zero clock reads, zero LINQ, quiet path
  allocation-free). Nine rules, thresholds as public consts: CriticalHull
  (hull ≤ .25/.35/.50), CriticalCrewHealth (worst alive bot ≤ .25/.35/.50),
  Fire (CountNonNullFires ≥ 3 Severe / ≥ 1 Warning), ReactorCritical
  (temp ≥ 95%/90% of max), DangerousCombat (≥1 authoritative hostile;
  Severe if ≥3 hostiles or combat-level gap ×1.33 — INFERRED, data-only),
  NavigationFailure (moved <1 m in 5 s while seeking >7 s — vanilla stuck
  trigger, coordination-only), FuelCritical (capsules ≤ 1/2),
  CoolantCritical (≤ 15%/30%), ObjectiveCritical (exactly 1 objective left,
  coordination-only). Fail-safe on every unknown input (NaN fractions,
  -1 counts/ids, missing sections, dead/unknown crew never trigger).
  Realizations are existing registered capabilities only (orders 9/6/1,
  SET_CAPTAIN_TARGET) — validated again by the registry + executor before
  any action.
- `Core/Emergency/EmergencyDirector.cs` — the deterministic director
  (pure C#, System-only): one evaluation per MinRecheckMs (5 s — no per-frame
  loop), fail-safe world gates (null / never-captured / stale >20 s /
  future-dated / !GameStarted → `EmergencyUncertain` logged, nothing
  created), deny-by-default authority seam (no probe or faulting probe ⇒
  no-op), dedup against bounded ACTIVE records (re-detection refreshes
  LastSeenMs + escalates severity only — never a second task), bounded
  shedding (active ≤ 8 sheds oldest, history ≤ 16, ActiveExpiryMs 30 s,
  TaskRequeueBlockMs 20 s), emergency task creation through the P2 lifecycle
  ONLY (type EMERGENCY, owner CAPTAIN, priority from EmergencyPrecedence,
  maxRetries 1, timeout 120 s, metadata EmergencyId/EmergencyType/
  Preemptible="true"/CapabilityId/Argument; preemption is REQUESTED through
  the P4 scheduler's own policy-gated path — the director never pauses,
  fails or cancels anything), hysteresis-gated state machine
  (StateDwellMs 5 s, RecoveryHoldMs 10 s, illegal transitions counted and
  never applied), ReconcileTasks resolves records whose emergency task
  reached a terminal state (task itself left to lifecycle/recovery),
  StatusLines diagnostics, ResetForTests. Pluggable fail-closed seams:
  authority probe, nowMs provider, world provider, decision listener.
- `Core/Emergency/EmergencyLogBridge.cs` — boots the director's decision
  listener into `CapBotLog` (new EMERGENCY subsystem const).
- `docs/EMERGENCY.md` — the Phase 9 contract document (principle, pipeline,
  state machine, precedence, rules table, identity/dedup, preemption
  contract, verified-API table, world dependencies, multiplayer model,
  performance, unsupported types, failure modes, tests).

### Changed
- `Core/World/WorldSnapshot.cs` — additive Phase 9 extension: new readonly
  fields `PlayerShipFireCount` (int, -1 = unknown) and
  `PlayerShipReactorTempFraction` (float, NaN = unknown); original 16-arg
  constructor preserved verbatim (both fields default to unknown); new 18-arg
  constructor chains via `: this(...)`. All Phase 6–8 callers/tests compile
  unchanged.
- `Core/World/PulsarWorldSource.cs` — Capture() fills the two new fields from
  VERIFIED public APIs (`PLShipInfo.CountNonNullFires()`,
  `PLShipStats.ReactorTempCurrent/ReactorTempMax`), each try/catch-guarded
  into `RecordPartial` (partial-failure bookkeeping, -1/NaN on fault).
- `Mod.cs` — Phase 9 boot block: EmergencyLogBridge.Ensure +
  director seams wired (authority probe = ExecutionClaims.IsAuthoritative,
  nowMs = TaskClock, world = WorldStateService.Latest). Deny-by-default;
  the director stays INERT until the tick driver calls Evaluate host-side.
- `Patch.cs` — WorldTick Postfix (host-only) now also drives the emergency
  director: two individually exception-guarded calls,
  `EmergencyDirector.Evaluate(TaskClock.NowMs)` (5 s internal gate) and
  `EmergencyDirector.ReconcileTasks(...)`, after the executor tick. The
  director never executes anything — tasks still route through P4/P5/P7/P8.
- `CapBotLog.cs` — added the `EMERGENCY` subsystem const.
- `CapBot.csproj` — +4 Compile entries (EmergencyState, EmergencyDetector,
  EmergencyDirector, EmergencyLogBridge).
- `tests/run_tests.ps1` — compiles the three emergency domain files + the
  new test suite (f1–f8).
- `tests/TaskRecoveryTests.cs` — TestMain runs eight suites; TOTAL ×8.

### Tests
- `tests/EmergencyTests.cs` — 141 checks covering all 25 mandated Phase 9
  scenarios (S1–S5 legal state chains with dwell hysteresis, S6 illegal
  transition table, S7–S10 precedence classes/severity bumps, S11 duplicate
  detection, S12 identity determinism, S13 stale/future-dated world,
  S14 invalid targets rejected by the registry, S15 authority rejection +
  deny-by-default, S16 capability rejection executor-side, S17 execution-claim
  rejection on emergency tasks, S18 preemption through the scheduler's own
  policy path, S19 preempted task stays recoverable (auto-resume + cancel
  flow), S20 no-storm (20 passes → 1 task; 5 persisting emergencies → 5
  tasks + 55 dedups; bounded active set), S21 bounded shedding/history,
  S22 Quality-Improver-safe hostility (authoritative list only), S23
  master-only (client produces nothing), S24 repeated evaluation without
  duplicate actions, S25 fail-safe on missing/faulting state) plus per-rule
  detection coverage (R1–R8) and fail-safe inputs. Full suite: 806/806 pass
  (665 prior + 141 new).

## [Phase 8 — Task executor] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Executor/ExecutionResult.cs` — the execution result contract:
  `ExecutionOutcome` (Success / FailureRetryable / FailurePermanent /
  Rejected / Unavailable / Cancelled), the bounded immutable
  `ExecutionResult` (reason ≤ 200, ≤ 8 diagnostic metadata entries, key
  ≤ 32 / value ≤ 128, `WithMeta` derived copies), and `ICapabilityDispatcher`
  — the ONLY pathway from an approved task to a gameplay action. No attempt
  ever throws across the seam; everything is data, never executed.
- `Core/Executor/TaskExecutor.cs` — the static executor engine (pure C#):
  per-attempt pipeline resolve live task → consume scheduler grant
  (`TryTakeLease`, exactly-one-executor-pass) → `TryStart` through the P2
  contract → build `CapabilityRequest` from task fields + "CapabilityId"/
  "Argument" metadata (untrusted; registry-validated) → P7 gate-ladder
  validation → P5 `TryClaim` (attemptEpoch = RetryCount — each recovery
  retry is a new action identity) → dispatch EXACTLY ONE registered
  capability → `RecordExecutionResult` (sticky success, duplicate/stale
  ignored) → lifecycle resolution (Success → TryComplete; failure/rejection
  → TryFail handing to P3 recovery; NO retry logic — recovery owns retry,
  backoff, abandon). Validate-before-claim ordering documented (the P7
  claim-probe seam makes claim-then-validate self-conflict; all gates still
  run before any action and the claim remains the last gate). Tick gate
  250 ms, `MaxAttemptsPerTick = 4`, no dispatcher ⇒ `Unavailable`
  (fail-closed), dispatcher faults wrapped as FailureRetryable, invariant
  violations logged and left to recovery. Every refusal resolves the task
  through the lifecycle — nothing wedges. `SetDispatcher`,
  `SetDecisionListener`, `Enabled`, `ResetForTests`.
- `Core/Executor/PulsarCapabilityDispatcher.cs` — the game-facing
  dispatcher: static, code-reviewed branches on CapabilityId calling
  VERIFIED PULSAR APIs only (signatures verified by direct reflection this
  session; call shapes copied from compile-proven shipped sites).
  `SET_CAPTAIN_ORDER` → `PLServer.CaptainSetOrderID(Int32)` direct call
  (order validated against the static vocabulary {1,4,6,8,9,10,11,12,13}
  from shipped `ComputeDesiredOrder`); `ISSUE_MOVE_ORDER` →
  `pawn.photonView.RPC("IssueMoveOrder", PhotonTargets.All, sector.Position)`
  (impl is private in Assembly-CSharp — reachable only via its [PunRPC]
  route; Vector3 derived only from `PLSectorInfo.Position`, never parsed
  from untrusted text); `SET_CAPTAIN_TARGET` → direct
  `PLShipInfoBase.Captain_SetTargetShip(Int32)` (target syncs via stream —
  no PhotonTargets.All duplicate pattern); course-goal channels via the
  exact shipped `PLServer.Instance.photonView.RPC(..., PhotonTargets.All,
  ...)` shapes with sector existence verified against the galaxy table;
  `READ_WORLD_SNAPSHOT` → pure `WorldStateService.Latest` read. A
  registered capability WITHOUT a branch is refused (`Rejected`) —
  registration never makes a capability executable. No reflection dispatch,
  no method-name lookup, no runtime compilation, no interpretation of task
  metadata/chat/mission text as commands.
- `Core/Executor/ExecutorLogBridge.cs` — boots the executor decision
  listener into `CapBotLog` (TASK subsystem): accepted/rejected,
  capability/authority/precondition failures, duplicate execution, stale
  callbacks, claim releases, invariant violations.
- `docs/EXECUTOR.md` — the Phase 8 contract document (flow, ordering note,
  capability→API table with verification basis, tick driver, logging,
  tests).
- `tests/ExecutionTests.cs` — 98 assertions covering all 20 mandated
  scenarios (successful execution, unknown capability, disabled capability,
  invalid task, invalid owner, invalid target, failed precondition, wrong
  authority, missing claim, duplicate claim, duplicate execution request,
  stale callback, cancelled task, completed task, recovery-owned task,
  retryable failure, permanent failure, scheduler grant requirement,
  deterministic execution identity, multiplayer authority gating) plus the
  tick driver. **Suite total now 665/665** (97 + 57 + 89 + 106 + 80 + 138
  + 98).

### Changed
- `Patch.cs` — the Phase 6 `WorldTick` postfix (11th Harmony patch,
  PLController.Update) extended IN PLACE (no new patch) to also drive, host-
  side only (`PhotonNetwork.isMasterClient`, fail-closed try/catch — the
  shipped authority gate), `TaskScheduler.Tick` + `TaskRecoveryManager.Tick`
  + `TaskExecutor.Tick`. Each call individually exception-guarded; all
  subsystems self-throttle (1 s scheduler/recovery, 250 ms executor), so
  the per-frame anchor yields vanilla decision cadence. INERT until tasks
  exist.
- `Mod.cs` — Phase 8 boot block: `ExecutorLogBridge.Ensure()`,
  `TaskExecutor.SetDispatcher(new PulsarCapabilityDispatcher())`,
  `ExecutionClaims.SetAuthorityPolicy` wired to `PhotonNetwork.
  isMasterClient` (fail-closed: any fault denies authority; clients never
  execute — vanilla's request→master pattern untouched).
- `CapBot.csproj` — four `Core\Executor\` Compile entries.
- `tests/run_tests.ps1` / `tests/TaskRecoveryTests.cs` (TestMain) — added
  the ExecutionResult/TaskExecutor domain files and the f7 ExecutionTests
  suite to the harness.

## [Phase 7 — Safe task capability registry] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Capabilities/CapabilityDescriptor.cs` — the capability contract
  vocabulary: `CapabilityAuthority` (MasterOnly/ClientRequest/ClientOnly/
  ReadOnly — MasterOnly is the default for gameplay), `CapabilityDanger`,
  `CapabilityReversibility`, `CapabilityValidation` (15 deterministic
  outcomes), `TargetRequirement` (None/SectorId/ShipId/MissionId/
  BoundedToken), untrusted `CapabilityRequest` holder, and the immutable
  `CapabilityDescriptor` (bounded lists ≤ 8, bounded static text, pure
  `Precondition`/`TargetValidator` delegates, `VerifiedApi` documentation
  text, deterministic `ToContractLine`). Capabilities are data contracts,
  never executable instructions.
- `Core/Capabilities/CapabilityRegistry.cs` — bounded (≤ 32) static
  allowlist with duplicate-safe registration (ids validated to the same
  `[A-Za-z0-9_]` ≤ 32 vocabulary as Phase 5 `actionKind`, so a CapabilityId
  IS a valid actionKind), cheap exact-match lookup (Ordinal dictionary, no
  scanning/LINQ), enable/disable, and the deterministic validation gate
  ladder (malformed → disabled → actor → authority → target → precondition
  → task mismatch → ownership → cooldown → P5 claim conflict → P6 world
  freshness; first failure wins). Ownership gate resolves the LIVE task
  (identity + non-terminal + owner match + request consistency); cooldowns
  stamp only on approval; claim conflict builds the real P5 action identity
  and treats seam faults as conflict (fail-closed). Pluggable seams
  (authority probe / clock / world provider / claim probe) — all
  fail-closed when unwired or faulting; the registry holds no world or
  claim state. `StatusLines` + `ResetForTests`.
- `Core/Capabilities/RegisteredCapabilities.cs` — the Phase 7 catalog: 7
  built-ins (`SET_CAPTAIN_ORDER`, `ISSUE_MOVE_ORDER`, `SET_CAPTAIN_TARGET`,
  `ADD_COURSE_GOAL`, `REMOVE_COURSE_GOAL`, `CLEAR_COURSE_GOALS`,
  `READ_WORLD_SNAPSHOT`), all documenting PunRPC-verified vanilla channels
  (`PLServer.CaptainSetOrderID(Int32)`, `PLPlayer.IssueMoveOrder(Vector3)`,
  `PLShipInfoBase.Captain_SetTargetShip(Int32)`,
  `PLServer.AddCourseGoal/RemoveCourseGoal(Int32)/ClearCourseGoals()`),
  `CAPTAIN`-owner-restricted, `MasterOnly`, cooldowns 1000–5000 ms guarding
  vanilla cadence, plus the read-only Phase 6 snapshot contract.
  `RegisterBuiltIns()` (duplicate-safe) + `AttachProductionSeams()` (wires
  authority→`ExecutionClaims.IsAuthoritative`, clock→`TaskClock.NowMs`,
  world→`WorldStateService.Latest`, claim probe→`GetClaim.Active ||
  Ledger.Observe==Succeeded`). Deliberately excluded: `Captain_SetAutoMode`
  (empty body), `Captain_NameShip`, `SkipWarp/SkipWarpAt` (unrequested),
  and all speculative combat/mission/economy/build capabilities.
- `Core/Capabilities/CapabilityLogBridge.cs` — attaches Phase 1 `CapBotLog`
  (new CAPABILITY subsystem tag) as the registry decision listener at mod
  boot; the domain contains zero logging calls.
- `Core/Logging/CapBotLog.cs` (modified) — added the `CAPABILITY` subsystem
  tag (additive; OLLAMA remains reserved).
- `CapBot.csproj` (modified) — compile entries for the four new files.
- `Mod.cs` (modified) — boot wiring: `CapabilityLogBridge.Ensure()`,
  `RegisteredCapabilities.RegisterBuiltIns()`,
  `RegisteredCapabilities.AttachProductionSeams()`.
- `docs/CAPABILITIES.md` — full contract: security boundary (contracts not
  executable instructions — no C# generation/runtime compilation/DLL
  loading/shell execution/reflection invocation/LLM-text-as-commands),
  descriptor table, 13-gate validation ladder table, authority matrix
  (fail-closed defaults), seam table, the 7-capability catalog with
  verified APIs, task-system integration contracts (P2/P3/P4/P5/P6),
  compatibility posture (Better AI/MoreBots/Quality Improver — no hostility
  assumptions), performance, logging examples, explicit not-in-phase list.
- `tests/CapabilityTests.cs` — 138 dev-side assertions (not shipped)
  covering all 14 mandated scenarios: registration (+ id vocabulary
  boundaries), duplicate registration, bounded registry cap, unknown
  rejection, deterministic lookup (instance-stable Get, sorted bounded id
  list), happy-path approval, malformed request/task (incl. request TaskId
  ≤ 0 and null request owner — request data validated as untrusted),
  disabled capability, actor allowlist (+ wrong-case owner), authority
  rejection (deny-by-default + faulting probe fail-closed), invalid targets
  (kind/non-integer/negative/empty/over-length token/punctuation), declared
  preconditions + target validators (+ faulting validator fail-closed),
  task-type mismatch, ownership mismatch (not-registered, wrong owner,
  spoofed task id, cancelled task still in registry history), P5 claim
  integration (CapabilityId as actionKind, ledger-Succeeded duplicate
  rejection, unexpired-claim conflict, expired-lease clearance, faulting
  claim probe), P6 world integration (missing seam → RejectedWorldStateMissing,
  never-captured, fresh, stale → RejectedWorldStateStale, boundary,
  faulting provider), catalog metadata integrity (all 7 capabilities'
  authority/target/cooldown/VerifiedApi/owner assertions, bounded fields,
  ToContractLine, StatusLines, enable/disable round-trip). Harness
  `run_tests.ps1` + TestMain wired for six suites. Combined TOTAL:
  **passed=567 failed=0** (97 lifecycle + 57 recovery + 89 scheduler +
  106 claims + 80 world + 138 capability).

### Notes
- Contract layer only: the registry validates and approves — it never
  executes. No PULSAR API is called anywhere in this layer (`VerifiedApi`
  fields are static documentation), no executor exists to consume an
  `Approved` outcome, and no gameplay can route through the registry yet.
  The authority seam is doubly fail-closed (registry gate +
  claims deny-by-default) until P8 wires `PhotonNetwork.isMasterClient`.
- No Harmony patches added (still 11), no RPC changes, no vanilla AI
  behavior changes; Better AI/MoreBots/Quality Improver compatibility
  unaffected (no hostility assumptions encoded). No new PULSAR/PML API
  usage — pure C# domain + P2–P6 integration.
- Sighted but deliberately unregistered during API verification:
  `PLServer.Captain_SetAutoMode(Bool)` (empty body — no observable effect),
  `PLServer.Captain_NameShip(String)`, `PLServer.SkipWarp/SkipWarpAt`.
- Not implemented (later phases): executor (P8), directors (P9/P15–P17),
  Captain Brain 2.0, Decision Validator, Ollama/Qwen, dynamic task
  generation, persistence, UI, updater security, performance refactoring.

## [Phase 6 — Game/World State observation layer] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/World/WorldSnapshot.cs` — immutable, bounded, value-style snapshots:
  `WorldSnapshot` root (session/game-started/host/hub, ships, crew, missions,
  threats, navigation, resources, world objects, per-section
  `WorldAuthority`), section types (`ShipSnapshot`, `CrewMemberSnapshot`,
  `MissionSnapshot`, `ThreatSnapshot`, `NavigationSnapshot`,
  `ResourceSnapshot`, `WorldObjectSnapshot`), `WorldTransition`
  (`SECTOR_CHANGED`/`WARP_STARTED`/`WARP_ENDED`). All collections bounded at
  construction (ships ≤ 24, crew ≤ 16, missions ≤ 16, world objects ≤ 16,
  hostiles ≤ 16, course goals ≤ 8, research ≤ 8); names truncated; NaN/-1/
  null = unknown sentinels; no game-object references held. Player ship is
  `Ships[0]`, captain is `Crew[0]` (ordering contract).
  `WorldSnapshot.Empty` = never-captured placeholder.
- `Core/World/WorldStateService.cs` — pure C# cache/transition detector:
  throttled `Refresh(nowMs)` (1 s default = vanilla decision cadence),
  transition detection from last-seen sector/warp values (survives
  `SetSource` resets; listener fires OUTSIDE the lock, never on first
  capture, ≤ 4 per refresh), sticky
  `HasUnacknowledgedSectorChange()`/`AcknowledgeSectorChanged()` for
  slow-cadence consumers, freshness (`GetFreshness`, 10 s `MaxSnapshotAgeMs`),
  diagnostics (`RefreshCount`/`ErrorCount`/`LastError`), `ResetForTests`.
  Pluggable `IWorldSource` seam; dormant by construction until a source is
  set AND a tick caller refreshes.
- `Core/World/WorldSnapshotProbe.cs` — recovery's real `ITaskWorldProbe`
  (Phase 3 deliverable) answering from the latest snapshot. **Fail-open on
  uncertainty**: never-captured/stale/empty-view snapshots never drive
  destructive recovery actions; positive evidence only (SHIP/MISSION target
  presence, crew membership for `CAPTAIN`/`BOT:<id>` owners with
  `AliveKnown` death evidence; populated-crew absence = positive).
  `CapabilityAvailable` defers to P7; `WorldInvalidatesTask` stays false (no
  invented premise semantics). Injectable snapshot/time providers for
  deterministic tests.
- `Core/World/PulsarWorldSource.cs` — game-facing `IWorldSource` reading
  verified registries ONLY (`PLServer.Instance` GameHasStarted/AllPlayers/
  AllMissions/CurrentCrewCredits/ResearchMaterials/CurrentUpgradeMats/
  m_ShipCourseGoals/GetCurrentSector, `PLEncounterManager.Instance` AllShips/
  PlayerShip, PLShipInfoBase MyStats/HostileShips/TargetShip/GetCombatLevel/
  AlertLevel/InWarp/WarpChargeStage/WarpTargetID/MyFlightAI caches,
  PLPlayer GetPlayerName/GetPlayerID/IsBot/GetClassID/TeamID/GetPawn/
  MyCurrentTLI/ActiveMainPriority, PLBotController stuck metrics via
  PLPlayer.MyBot, PLMissionBase objectives, MyFlightAI.cachedRepairDepotList/
  cachedWarpStationList, PLBeaconInfo beacons). Zero FindObjectsOfType, zero
  scene scans. Non-throwing by section (`PartialErrorCount`/
  `LastPartialError` diagnostics); hostiles read from the game's own
  `HostileShips` list (Quality Improver-safe: never calls hostility logic).
- `Core/World/WorldLogBridge.cs` — attaches Phase 1 `CapBotLog` (TASK) as
  the transition listener at mod boot; the world domain contains zero
  logging calls.
- `CapBot.csproj` (modified) — compile entries for the five new files +
  `PilotAIBuild.dll` reference (transitive base-class assembly of the
  flight-AI type).
- `Mod.cs` (modified) — boot wiring: `WorldLogBridge.Ensure()`,
  `WorldStateService.SetSource(new PulsarWorldSource())`,
  `TaskRecoveryManager.Probe = new WorldSnapshotProbe()`.
- `Patch.cs` (modified) — `WorldTick` Harmony postfix on `PLController.Update`:
  the only new game hook; calls the read-only throttled refresh, exception-
  guarded so it can never alter controller behavior.
- `docs/WORLD_STATE.md` — full contract: data flow, snapshot model (bounds,
  sentinels, authority marks, ordering contracts), service semantics
  (throttle, transitions, sticky flag, freshness), source read-only/non-
  throwing posture, fail-open probe decision table, multiplayer/host-
  migration constraints, performance, security posture, not-in-phase list.
- `tests/WorldStateTests.cs` — 80 dev-side assertions (not shipped) covering
  the mandated scenarios: null/missing objects (Empty + null sections), no
  crew, multiple bots, missing captain (positive absence), sector
  transition (incl. never-fabricated from unknown ids), no active mission,
  multiple missions, destroyed targets (SHIP/MISSION positive-absence),
  invalid/stale refs (stale = fail-open, boundary exactness), host/client
  authority marking, deterministic construction (identical summary lines),
  bounded collection sizes (all seven bounds). Service: throttle, dormant
  null-source, freshness, transitions (first-capture silence, warp edges,
  SetSource reset), source-throw containment, reset. Harness
  `run_tests.ps1` + TestMain wired for five suites. Combined TOTAL:
  **passed=429 failed=0** (97 lifecycle + 57 recovery + 89 scheduler +
  106 claims + 80 world).

### Notes
- Observation layer only: reads authoritative game state into bounded
  immutable snapshots. It executes nothing, mutates nothing, issues no
  orders, and adds no RPCs. The refresh tick is read-only; the probe is
  attached but recovery still has no tick driver, so no gameplay routes
  through world state yet — existing behavior is unchanged.
- Hostility semantics are deliberately assumption-free (Quality Improver can
  replace `ShouldBeHostileToShip`): the authoritative hostile set is the
  game's own `HostileShips` id list; team counts are raw observations.
  Combat-level semantics INFERRED per research §6.6, carried as data only.
- Mission objective *types* are not readable on `PLMissionObjective`
  instances (no `ObjType` member): snapshots carry completion counts + first
  incomplete objective text instead.
- `PLWarpStation`/`PLRepairDepot` have no static registries; world objects
  read the player ship's own flight-AI cached lists (shipped-code-proven).
- Not implemented (later phases): capability registry, executor, directors,
  Captain Brain 2.0, Decision Validator, LLM integration, dynamic task
  generation, persistence, UI, secure updater, performance refactoring.

## [Phase 5 — Duplicate execution protection (claims/leases/idempotency)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/ActionIdentity.cs` — deterministic, data-only action identity
  for future executors: `ActionIdentity.MakeActionId(taskId, actionKind,
  attemptEpoch, targetKey)` → `"<taskId>:<kind>:<epoch>:<hash8>"`, hashed
  with session-stable FNV-1a 32-bit (never `string.GetHashCode`, which is
  not stable). `actionKind` is validated to a bounded static ASCII
  vocabulary (`[A-Za-z0-9_]`, ≤ 32 chars); the opaque `targetKey` is hashed,
  never embedded. `ActionOutcome` enum + `ActionLedger`: bounded (256-entry
  FIFO) idempotency memory — sticky `Succeeded` (never downgraded),
  `Failed→Succeeded` upgrade allowed, duplicate/repeated outcomes are
  no-ops. Identity is compared or logged, never parsed or dispatched on.
- `Core/Tasks/ExecutionClaims.cs` — single-owner execution claims with
  bounded 5 s leases (`ClaimLeaseDurationMs`), one claim per live task
  (≤ 64 = registry cap), attempt epoch default = task `RetryCount`
  (retried task = new logical action; duplicate request = same action).
  `TryClaim` is a deterministic gate ladder (invalid args → not
  authoritative → task missing → terminal → recovery-owned Failed/Paused →
  owned-by-other → duplicate-active → expired-takeover → ledger
  already-succeeded → Granted); `GrantedTakeover` makes stale-lease recovery
  explicit and logged (`LeaseExpired` + `OwnershipReleased`), so stale owners
  never retain ownership. Idempotency two-sided: claim side refuses actions
  already `Succeeded` in the ledger (`DuplicateExecutionRejected`); result
  side (`RecordExecutionResult`) records the first result, releases the
  claim, and ignores duplicate/stale callbacks (`DuplicateIgnored` /
  `StaleCallbackIgnored`). `ReleaseClaim` verifies the owner — mismatch is
  logged `InvariantViolation` and refused. `Tick` hygiene drops
  expired/missing/terminal-task claims. **Deny-by-default authority seam**
  `SetAuthorityPolicy(Func<bool>)`: with no policy, nothing can claim or
  record (fail-closed); Phase 8 wires it to `PhotonNetwork.isMasterClient`.
  Per-claim 1 s rejection-log throttle; no RPCs, no Photon targets, no
  process-external state; bounded memory throughout; no LINQ.
- `Core/Tasks/ClaimLogBridge.cs` — attaches the Phase 1 `CapBotLog` (TASK
  subsystem) as the claims decision listener at mod boot; the domain
  contains zero logging calls.
- `CapBot.csproj` (modified) — compile entries for the three new files.
- `Mod.cs` (modified) — boot wiring: `ClaimLogBridge.Ensure()` next to the
  lifecycle/recovery/scheduler bridges.
- `docs/EXECUTION_SAFETY.md` — full contract: claim model (record shape,
  identity format + FNV-1a rationale, attempt epochs, lease + takeover
  semantics), deterministic claim-rules table (orders 0–10), the two-sided
  idempotency guard (claim side + result side, sticky success, upgrade rule,
  explicit release + invariant logging), Tick hygiene, authority model
  (deny-by-default seam, P8 wiring to `isMasterClient`, process-local
  bookkeeping, no RPCs, host-migration-safe), scheduler interaction (grant
  vs claim separate lifetimes), recovery interaction (Failed/Paused refuse
  claims; claims persist through capability-pause; failure results release;
  fresh epoch after retry; no retry loops), logging examples, security
  posture, explicit not-in-phase list.
- `tests/ExecutionClaimTests.cs` — 106 dev-side assertions (not shipped)
  covering all 15 required scenarios: deterministic identity, deny-by-default
  authority gating, duplicate/same-owner/different-owner claims, lease
  expiry + stale takeover, duplicate completion/failure, stale callbacks,
  release ownership invariants, task cancelled/completed while claimed,
  recovery interaction (owner-down fail → release → recovery-owned refusal →
  fresh-epoch re-claim; capability-pause persistence), scheduler pass
  repeated twice (re-grant vs duplicate-execution refusal), bounded ledger
  eviction + live-claim cleanup, `MakeDefaultActionId` epoch determinism,
  null-reason release, status snapshot. Harness `run_tests.ps1` + TestMain
  wired for four suites. Combined TOTAL: **passed=349 failed=0** (97
  lifecycle + 57 recovery + 89 scheduler + 106 claims).

### Notes
- Protection layer only: claims/leases/idempotency for future executors
  (P7/P8). It executes nothing, holds no world state, and is deny-by-default
  inert until the authority policy is wired (P8 → `isMasterClient`).
  No gameplay routes through it; the scheduler and recovery behavior are
  unchanged. No new PULSAR/PML/Photon API usage (pure System* domain), no
  RPC/Harmony/vanilla-AI changes, no MoreBots-compat or save-format impact.
- Not implemented (later phases): capability registry, executor, world
  state, directors, Captain Brain 2.0, Decision Validator, Ollama/Qwen,
  dynamic task generation, persistence, UI, updater security, performance.

## [Phase 4 — Task scheduler (orchestration-only)] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/TaskScheduler.cs` — deterministic grant/lease scheduler over the
  Phase 2 registry's `Queued` pool (the queue IS the registry — no parallel
  queue structure). One pass (`Tick(nowMs)`): 1 s global re-check gate
  (vanilla decision cadence), lease-expiry hygiene, preemption-record prune +
  auto-resume of scheduler-initiated pauses, deterministic candidate ordering
  (effective priority desc with bounded aging +1/30 s capped +5 → FCFS →
  TaskId; insertion sort, no LINQ in the tick path), gated grant loop
  (deadline-elapsed refused; dependencies must resolve to `Completed`;
  one grant per owner — leases AND Running tasks both count as busy;
  `MaxGrantsPerTick` = 8, `LeaseDurationMs` = 5000), and a policy-gated
  preemption pass (explicit `Preemptible=="true"` metadata opt-in,
  `PreemptMargin` > 2, ≥ 3 s min runtime, < 2 lifetime preemptions — tally
  survives auto-resume as a dormant record —, owner not leased elsewhere;
  victim paused through the lifecycle-enforced `TryPause`). Scheduler state
  is two bounded dictionaries (leases, preemption records, both ≤ live cap,
  dropped when the task leaves the live registry). Grants are suggestions,
  not execution: the P8 executor claims via `TryTakeLease` (consumes the
  lease; double claims fail). `Enabled` switch, decision-listener hook,
  `ActiveGrantCount`, `HasLease`, `SchedulerStatusLines` diagnostics. The
  scheduler never retries, expires, fails, or resumes recovery-paused tasks —
  refusal-only interaction with Phase 3 recovery, and it resumes ONLY its own
  preemption-pauses.
- `Core/Tasks/SchedulerLogBridge.cs` — attaches the Phase 1 `CapBotLog`
  (TASK subsystem) as the scheduler's decision listener at mod boot; the
  scheduler itself contains zero logging calls.
- `Core/Tasks/TaskRegistry.cs` (modified) — added `LiveSnapshot()`: bounded
  point-in-time list of live tasks so scheduler/recovery passes never touch
  registry internals (additive, no behavior change).
- `Mod.cs` (modified) — boot wiring: `SchedulerLogBridge.Ensure()` next to
  the lifecycle/recovery bridges.
- `CapBot.csproj` (modified) — compile entries for the two new files.
- `docs/TASK_SCHEDULER.md` — full contract: queue-is-registry model, pass
  description, gates table (incl. why there is deliberately no
  retry-headroom gate), deterministic ordering, preemption policy (all
  conditions + record lifecycle), no-starvation properties, recovery-state
  interaction, multiplayer/authority constraints for the future P8 driver,
  logging, security posture, explicit not-in-scope list.
- `tests/TaskSchedulerTests.cs` — 89 dev-side assertions (not shipped):
  deterministic ordering (equal-priority FCFS/TaskId, priority precedence,
  bounded aging), dependency gating (Completed/history/unresolvable/expired
  deps; scheduler never expires), terminal-task invisibility, deadline
  refusal without expiry, recovery interaction (backoff-pending and
  final-retry attempts grantable; scheduler never touches recovery pauses),
  owner gates (lease + Running busy), bounded behavior (8 grants/pass, 1 s
  gate, lease cap), lease model (claim seam, double-claim rejection,
  expiry/re-grant), and the full preemption path (pause, auto-resume,
  re-preemption, lifetime cap, dormant tally, foreign-pause non-interference,
  margin/min-run/opt-in/cross-owner/victim-selection gates). Combined TOTAL:
  **passed=243 failed=0** (97 lifecycle + 57 recovery + 89 scheduler).

### Notes
- Orchestration only: the scheduler selects/orders existing registered tasks
  and issues bounded grants; it executes no gameplay code, writes no nav
  fields, touches no vanilla priority/behavior-tree system, makes no LLM
  decisions, and holds no world/transient vanilla state (records reference
  tasks by id + string owners). `Tick` is not wired to any game loop this
  phase — the scheduler is dormant by construction; the future P8 driver must
  additionally gate on `PhotonNetwork.isMasterClient`.
- Existing gameplay untouched: no changes to Patch.cs, Autonomy.cs, any
  Harmony patch, RPC pattern, or PML save format. No new PULSAR/PML API usage.

## [Phase 3 — Task recovery foundation] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/TaskRecovery.cs` — recovery policy: `RecoveryActionType`
  (None/Retry/Pause/Resume/Fail/Cancel/Expire), `ITaskWorldProbe` (the single
  seam through which recovery observes current authoritative world state),
  `NullWorldProbe` (correct-but-inert default until Phase 6 supplies a real
  probe), and `TaskRecoveryPolicy.Decide` — a pure, deterministic decision
  function with fixed rule precedence (timeout → world-invalidated →
  target-invalid → owner-loss → capability → stuck → retry/abandon).
- `Core/Tasks/TaskRecoveryManager.cs` — per-task recovery bookkeeping (bounded:
  one record per registered task, ≤ 64, dropped on terminal), 1 s recheck gate
  per task, bounded exponential retry backoff (2 s base, ×2, 30 s cap),
  lifetime recovery budget (12 non-terminal actions → forced terminal abandon),
  capability-pause ceiling (60 s), pluggable action-listener hook (fired
  outside the manager's lock).
- `Core/Tasks/RecoveryLogBridge.cs` — attaches the Phase 1 `CapBotLog` (TASK
  subsystem) as the manager's action listener at mod boot; recovery outcomes
  log as `Recovery applied/rejected <Action> on <task status line> (reason)`.
- `Core/Tasks/TaskLogBridge.cs` (modified) — the single registry listener now
  also feeds `TaskRecoveryManager.Track` on task registration (records exist
  only for registry-tracked tasks).
- `docs/TASK_RECOVERY.md` — full contract documentation: recovery state
  machine, rule precedence table, retry/backoff/budget semantics, stale-world
  handling (research constraints: no reliance on transient vanilla AI state,
  host-migration-safe), cadence rules, explicit not-in-scope list.
- `tests/TaskRecoveryTests.cs` — 57 dev-side assertions (not shipped):
  backoff curve, every recovery rule incl. precedence, retry exhaustion,
  capability pause/resume/abandon, external-pause non-interference, stuck
  detection with progress-refresh, budget backstop, recheck gate, disabled
  manager, null-probe safety, record lifecycle, status snapshot. Combined with
  the Phase 2 suite: **TOTAL passed=154 failed=0**.

### Notes
- Policy layer only: recovery never creates, queues, selects, or executes
  gameplay work — every mutation flows through Phase 2's idempotent lifecycle
  transitions, and the manager is inert until Phase 6 provides a real
  `ITaskWorldProbe` and a tick driver. No Harmony/RPC/gameplay behavior
  touched; no new PULSAR/PML API usage.
- Research constraints honored (PULSAR_GAMEAI_RESEARCH.md): recovery caches
  no world state, holds no Unity/path/Behave references, re-derives decisions
  from current probe answers only (host-migration-safe by construction); ~1 s
  decision cadence matching vanilla's decision gates; hostility semantics are
  probe-owned (QualityImprover-safe).

## [Phase 2 — Task lifecycle infrastructure] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Tasks/TaskState.cs` — `TaskState` enum (Created/Queued/Running/Paused/
  Completed/Failed/Cancelled/Expired), `TaskIds` monotonic identity counter,
  `TaskClock` wrap-safe millisecond clock, and `TaskTransitions` — the single
  legal-transition table (terminal states have no outgoing transitions).
- `Core/Tasks/CapBotTask.cs` — the task model: immutable identity/owner/priority/
  retries/timeout/dependencies/target data; guarded, idempotent transitions
  (`TryQueue/TryStart/TryPause/TryResume/TryComplete/TryFail/TryCancel/
  TryExpire/TryRetry`); bounded metadata; deterministic `ToStatusLine` reporting.
  Pure C# (System-only) — no Unity/PULSAR/PML references, holds no game objects.
- `Core/Tasks/TaskRegistry.cs` — bounded registry (≤ 64 live tasks, registration
  fails at the cap — no eviction; ≤ 128 history entries, ring drop) that mirrors
  task state automatically and exposes a transition-listener hook plus
  deterministic `StatusLines` reporting.
- `Core/Tasks/TaskLogBridge.cs` — attaches the Phase 1 `CapBotLog` (TASK
  subsystem) as the registry's transition listener at mod boot; the only file
  connecting the task domain to logging, keeping the domain pure/testable.
- `docs/TASK_LIFECYCLE.md` — full contract documentation: states, complete
  transition table, 10 invariants, ownership/cancellation/failure/retry
  semantics, dependency representation, lifecycle logging, explicit
  not-in-scope list for later phases.
- `tests/TaskLifecycleTests.cs` + `tests/run_tests.ps1` — dev-side unit tests
  (not shipped in the mod): 97 assertions covering validation, every legal/
  illegal transition, idempotence, retry/exhaustion semantics, expiry sweep,
  registry bounds (live cap, history ring), identity/equality, metadata caps
  and deterministic status reporting. Result: **97 passed / 0 failed**.
- Boot wiring: `Mod()` constructor calls `TaskLogBridge.Ensure()`.

### Notes
- Infrastructure only: no gameplay routes through the task system yet; the
  existing captain AI, Harmony patches, RPC patterns and PML save format are
  untouched. Scheduler (P4), recovery (P3), duplicate-execution protection (P5),
  directors and Captain Brain 2.0 (P15–P18) are explicitly out of scope and
  must build on the contracts documented in `docs/TASK_LIFECYCLE.md`.
- No new PULSAR/PML API usage — the domain invented none and calls nothing
  game-facing.

## [Phase 1 — Logging & error hardening] — unreleased (built from Alpha 1.2.2 source)

### Added
- `Core/Logging/CapBotLog.cs` — central leveled logger (`Trace`…`Critical`) with
  subsystem tags (CORE, CAPTAIN, CREW, MISSION, NAVIGATION, COMBAT, ECONOMY,
  RESEARCH, PERSISTENCE, NETWORK, COMPAT, UPDATER, UI). Backs onto PML's
  `Logger.Info` only — the one PML logging API verified by reflection.
- Spam/flood guards in the logger: at most one line per message key per 8 s,
  global 24 messages / 10 s flood window, 256-key cap, wrap-safe
  `Environment.TickCount` deltas. The logger itself can never throw.
- `VerboseLogging` persistent setting + "Verbose Logging" button in
  Mod Settings → CapBot. `Trace`/`Debug` lines are only emitted when it is on.
- Outer guard around the whole captain tick (`Patch.Postfix` → `PostfixCore`)
  so a failure in any scripted-sector handler can no longer break the patched
  `PLPlayer.UpdateAIPriorities` (audit finding C2).
- Per-handler guards for AtColony, WarpGuardianBattle, WastedWing, HandleShop,
  GetMissionFromHub, PlanetExploration (orders 12/13), BoardEnemy, HandleComms,
  AtWDWeapons, Burrow, AtRaces, HighRollers and SetNextDestiny — each failure is
  logged (subsystem-tagged warning) and the tick section is skipped.

### Fixed
- Null-dereference crashes in scripted sectors (audit finding C2):
  - `AtRaces`: race start screen not spawned yet → handler now exits cleanly.
  - `AtWDWeapons`: `PLBurrowArena` not spawned yet → clean exit; mission 59682
    objective 1 only marked when the mission exists and has ≥ 2 objectives
    (`Objectives` is a `List<>`, verified by reflection).
  - `BoardEnemy`: target ship cleared between check and handler → clean exit.
- All 32 silent `catch { }` blocks (audit finding H1) now log through CapBotLog
  with static message keys and the exception type/message, instead of vanishing.
- `/updateall` no longer null-refs when run before the local player exists
  (audit finding M7); command failures are logged.
- Mod updater: staged-apply failures, per-mod check failures and boot-time
  auto-update failures are all logged (they were previously invisible).

### Changed
- All remaining direct `Logger.Info("[CapBot] ...")` calls are routed through
  CapBotLog with subsystem tags.
- Build: game-assembly references now resolve through the `$(PulsarManaged)`
  MSBuild property (default `C:\SteamLibrary\steamapps\common\PULSARLostColony\PULSAR_LostColony_Data\Managed`)
  instead of hardcoded relative paths to a non-existent `D:\SteamLibrary`
  (audit finding H3). Override with `msbuild /p:PulsarManaged=<path>`.
- Machine-specific PostBuildEvent XCOPY copy step removed from the csproj.
- Compiler toolset dependency (OpenSesame.Net.Compilers.Toolset 4.0.1, supplies
  the compiler that allows compiling against internal game members) is restored
  via `nuget restore`; `IgnoresAccessChecksToAttribute` resolves from 0Harmony
  exactly as in the original build.
- `LangVersion` pinned to 8.0 to match the toolchain.

### Notes
- No gameplay/AI behavior changed: this phase only adds observability, guards
  the same code paths, and fixes crash paths that could never have worked
  (the three null-deref handlers). RPC patterns, walkthrough coordinates,
  PML save format, NonCaptainMenu, executor election and anti-spam logic are
  untouched (audit §8 do-not-touch list).