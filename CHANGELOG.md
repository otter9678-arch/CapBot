# Changelog

All notable changes to CapBot are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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