# MULTIPLAYER HARDENING (Phase 26)

> **Ownership argument.** Phase 26 is an audit-and-harden phase: no new
> gameplay authority, no new capability, no new authoring channel. Every
> gameplay mutation still flows through the P5 claims → P7 validation → P8
> executor pipeline with the deny-by-default authority policy wired to
> `PhotonNetwork.isMasterClient` (P8, unchanged). This phase adds only
> transition-time hygiene that the existing authority architecture could
> not reach, plus its automated tests.

## 1. Audit result (evidence-based)

**Verified sound (no change):**
- Host-only tick gating: ALL task-pipeline tick drivers (validator screen,
  scheduler, recovery, executor, claim hygiene, directors) run behind the
  `PhotonNetwork.isMasterClient` gate in the WorldTick Postfix.
- Deny-by-default claims authority: `ExecutionClaims.SetAuthorityPolicy`
  wired to `isMasterClient` with fault→deny; clients can never claim,
  record, or dispatch.
- Per-director authority probes: every director/advisor/observer takes
  `ExecutionClaims.IsAuthoritative` as its authority seam — a client
  process evaluates nothing, authors nothing, syncs nothing.
- Dispatcher RPC discipline: single-send per logical action; no
  request-RPCs (vanilla client→MasterClient pattern untouched); no
  local+`PhotonTargets.All` duplicates; SET_CAPTAIN_TARGET streams without
  an All-RPC.
- Duplicate-execution protection: identity ledger sticky-success survives
  anything process-local.
- Mid-attempt authority flip: the executor's `RejectedNotAuthoritative`
  record path hands the task to recovery (invariant path), never wedges.

**Findings fixed (G1/G2):**
- **G1 — claim-lease hygiene never ran in production.**
  `ExecutionClaims.Tick` (drops expired leases + claims whose task is
  terminal/missing) existed since P5 but had no production caller.
  Wired into the WorldTick host block right after the executor tick.
- **G2 — the P10 authority-lost agent clear was unreachable in-game.**
  `CrewAgentRegistry.TrackAuthority(false)` clears agents on authority
  loss — but it lives BEHIND the host gate, which stops running the
  moment authority is lost. Dead-in-game since Phase 10. Fixed by the
  pre-gate monitor (§2), which invokes a new public
  `CrewAgentRegistry.ClearForAuthorityLoss` handler.

**Volatile-state census at an authority flip (what dies with authority):**

| State | Owner | On authority LOST | Why |
|---|---|---|---|
| Execution claims (leases) | P5 | **dropped** (clear handler) | 5 s bookkeeping for a state no longer owned |
| Sticky-success ledger | P5 | **KEPT** | duplicate-protection is identity truth, not a lease |
| Scheduler leases + preemption records | P4 | **dropped** (clear handler) | 5 s grants; must not suggest execution for foreign state |
| Crew agent records | P10 | **dropped** (clear handler) | volatile authoritative data; deterministic ids make rebuild lossless |
| Task registry (live/history) | P2 | untouched | lifecycle is not authority-volatile; P3 owns it |
| Recovery records | P3 | untouched | budgeted backoff self-expires; next authoritative pass re-evaluates |
| Director records (P14–P24) | each | untouched | all expire on their own cadences (30–60 s hygiene) |
| Crew experience/memory/personality/learning | P11–P13/P25 | untouched | process-local DATA; not authoritative game state |
| Ollama/Qwen advisors | P20/P21 | untouched | recommend-only diagnostics, no authority semantics |

**Passive host-migration behavior (documented, by construction):** the task
registry is process-local, so a host migrating to ANOTHER machine starts
with an empty task set — vanilla PULSAR AI continues uninterrupted (native
AI is untouched by design; CapBot never replaced it). A host migrating
BACK to a process that previously held authority re-arms every director
from fresh world snapshots (first readable pass = baseline arm everywhere,
the P22/P25 pattern) with no stale premise carry-over beyond the bounded
expiry cadences above.

## 2. MultiplayerAuthorityMonitor (new, `Core/Tasks/`)

Pre-gate authority-flip observer. Runs EVERY FRAME in the WorldTick
Postfix BEFORE the host-only gate (on host AND client), watching the same
authority seam the pipeline uses (`ExecutionClaims.IsAuthoritative` —
which is `PhotonNetwork.isMasterClient` with fault→deny).

- Steady state: one probe read, zero allocation.
- First authoritative observation ARMS (never fires) — the boot frame must
  not look like a flip.
- Faulting probe ⇒ "unknown": no transition synthesized (counted only);
  a boot-time fault can never fire handlers (nothing armed).
- true→false (AUTHORITY LOST): records the flip, snapshots registered
  handlers, fires each OUTSIDE all locks with its own try/catch
  (registration order), emits `MPAuthorityLost handlers=<n>`.
- false→true (REGAINED): records the flip, emits `MPAuthorityRegained`,
  fires nothing (volatile state was already cleared at loss; the next
  authoritative pass rebuilds from world observation).
- Handlers: bounded (≤16), duplicate-safe registration, each individually
  fail-safe — one faulting handler cannot block the others or the caller.
- The monitor itself owns NO gameplay semantics: it never mutates pipeline
  state, it only dispatches to the registered clear handlers.

### Registered production handlers (Mod.cs boot)

1. `ExecutionClaims.ClearForAuthorityLoss()` — drops claims; **ledger
   preserved** (`ClaimsClearedAuthorityLost claims=<n> (ledger preserved)`).
2. `TaskScheduler.ClearForAuthorityLoss()` — drops leases + preemption
   records; task registry untouched (`SchedulerLeasesClearedAuthorityLost
   leases=<n>`).
3. `CrewAgentRegistry.ClearForAuthorityLoss(nowMs)` — same semantics as
   the P10 TrackAuthority(false) clear (`AgentsClearedAuthorityLost`).

Clients are unaffected by design: they never hold authoritative state.

## 3. Authority-loss surfaces (additive, fail-safe)

- `ExecutionClaims.ClearForAuthorityLoss()` — claims only. IL-verified to
  NOT touch `ActionLedger` (P5 duplicate truth).
- `TaskScheduler.ClearForAuthorityLoss()` — leases + preemption records
  only. P3 lifecycle never touched.
- `CrewAgentRegistry.ClearForAuthorityLoss(nowMs)` — mirrors the
  TrackAuthority(false) clear (agents + sync stamp; authority bookkeeping
  updated so the next Sync re-syncs cleanly).

## 4. Driver wiring (Patch.cs WorldTick — still 11 Harmony patch classes)

1. `MultiplayerAuthorityMonitor.Observe()` — pre-gate, every frame,
   individually guarded.
2. Host block: `ExecutionClaims.Tick(nowMs)` added right after
   `TaskExecutor.Tick` (G1 fix), individually guarded.

## 5. What Phase 26 deliberately does NOT do

- No RPC additions/changes of any kind (no new Photon calls; the
  dispatcher's verified single-send table is untouched).
- No cross-machine state transfer: CapBot state is process-local by
  design (P28 will govern any persistence); host migration to another
  machine = fresh deterministic rebuild, never a desync (CapBot holds no
  authoritative game state to desync — it only issues commands through
  verified vanilla channels).
- No reconnect/host-migration UI, no RPC correctness changes to vanilla
  (PLCommand pipeline stays off-limits per the P3 research boundary).
- No behavior change for single-player (isMasterClient is always true
  there; the monitor arms once and stays quiet).

## 6. Tests

`tests/MultiplayerHardeningTests.cs` MP01–MP10 (~78 assertions):
deny-by-default + arm semantics; true→false fires handlers / false→true
fires none; steady-state quiet; handler fail-safety (fault isolation +
caller fail-safety); claims clear with LEDGER PRESERVED; scheduler clear
leaves registry/lifecycle untouched; crew clear + rebuild-after-regain
via the real Sync path; bounded/duplicate-safe registration + reset;
claims Tick hygiene + takeover after expiry; determinism + readbacks.

## 7. Test-design gotchas

- Monitor state (`m_HasLast`) must be set under the same lock that reads
  it — MP01 caught an arm-pass looking like a false→true flip.
- Shared `static bool` probe across scenarios: a prior scenario can leave
  it false (MP04 → MP07 lesson); set it explicitly at scenario start.
- C# closures capturing the same variable share one display class ⇒
  `Delegate.Equals` collapses them — seed a per-iteration variable for
  distinct delegates (MP08).
- IL order checks: the `get_isMasterClient` call is the PROBE (before
  Observe); the GATE is the branch after Observe — compare Observe's
  offset against the first host-block driver (DecisionValidator), not
  against the probe.
- `RecordExecutionResult` without a matching claim is
  `StaleCallbackIgnored` and never seeds the ledger — seed via
  `ActionLedger.RecordOutcome` (public, sticky rules) instead.

## 7b. Verification

- Tests: `TOTAL passed=2469 failed=0` ×3 consecutive (suite now 25 domain
  files, 16 suites).
- Reflection (`verify_build_p26.ps1`): 48/0 — monitor type/members/consts;
  all three `ClearForAuthorityLoss` surfaces; WorldTick IL order
  (probe@33 → observe@47 → dv@84 ⇒ pre-gate) + claims-Tick wiring + all
  prior driver references intact; monitor IL purity (no game types, no
  pipeline mutators); claims clear provably does not touch the ledger;
  Harmony patch classes == 11; prior-phase types intact.
- Run-1 defects found and fixed: 1 implementation bug (monitor arm-pass
  stamping `m_HasLast` outside the lock) + 3 test-authoring bugs (probe
  state leakage, closure identity, ledger seeding path).