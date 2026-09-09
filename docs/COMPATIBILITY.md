# COMPATIBILITY MANAGER (Phase 27)

> **Ownership argument.** Phase 27 centralizes dispatch and audit for
> inter-mod compatibility actions. It adds NO new compat behavior: the only
> real inter-mod guard in the tree (MoreBots class-0 crash fix) keeps its
> implementation, its Harmony surgery, and its idempotence guards in
> Patch.cs. The manager owns registration, mod-detection gating, fail-safe
> dispatch, and the audited status surface — nothing else. It authors no
> tasks, mutates no pipeline state, touches no Harmony targets, and has no
> tick driver (no WorldTick change; the 11-class Harmony ceiling holds).

## 1. Audit result (Phase 27 discovery, evidence-based)

**Real compat code in the tree (pre-existing, preserved):**
- `MoreBotsCompatPatch` (Patch.cs, runtime): MoreBots' `GetAIDataPatch`
  prefix indexes `ClassData[classID - 1]` for every team-0 bot; class 0
  (CapBot) produces index -1 and throws IndexOutOfRangeException every
  frame. At `/capbot` spawn CapBot REMOVES the crashing prefix (own
  Harmony id `pokegustavo.CapBot.compat`) and installs a guarded
  re-implementation (same behavior for classes 1–4, class 0 falls through
  to vanilla + CapBot's own postfix). Reflection into MoreBots internals
  is version-drift fragile by nature but real and fail-safe (any
  reflection fault falls through to vanilla).
- `Config.BetterAILoaded / ExpandedGalaxyLoaded / ExoticComponentsLoaded /
  TalentsModLoaded` — PML `IsModLoaded` detection, used by the settings
  menu "Detected mods" line. Detection only, by design.
- README "Better AI — complementary" / "Quality Improver — no overlapping
  component logic" — verified sound: CapBot never patches the methods
  those mods patch, and world reads are QualityImprover-safe by
  construction (read the HostileShips LIST, never call hostility logic).

**Findings fixed (this phase):**
- **F1 (audit M1, docs-truth)** — README claimed "TalentsModPerformanceImprovement — its unguarded UI helper is replaced with a safe version" and "ExpandedGalaxy — plus boot-time crash guards for its starter-ship
  postfixes" with NO code behind either claim (grep-verified). Implementing
  guards against third-party internals we do not ship and cannot test
  would violate the never-invent-APIs rule. Resolution: the README claims
  are corrected to what is real (detection + listing); the compat surface
  for future guards is the CompatManager (below). Docs must match
  behavior.
- **F2 (audit M2, unchanged, documented)** — `NonCaptainMenu.AddBots`
  rebuilds the player-overview button list; another mod patching the same
  method prefix-style would be clobbered. Quality Improver does not patch
  it today. Standing conflict risk, not fixed here (behavior change out of
  P27 scope).

**Harmony ordering discipline (verified, unchanged):** 11 patch classes,
all Postfix except the single runtime MoreBots unpatch+prefix swap (own
Harmony id, isolated from the main `pokegustavo.CapBot` id). No
HarmonyPriority attributes anywhere in the tree; ordering risk is
inherent to postfix-on-postfix stacking and mitigated by the MoreBots
replacement being the ONLY prefix on `PLPlayer.GetAIData` after install.

## 2. CompatManager (new, `Core/Compatibility/`)

Static, bounded, fail-closed dispatch-and-audit registry.

- **Actions:** `RegisterAction(name, modNames[], install)` — duplicate
  name ⇒ idempotent no-op (returns true, registers nothing). Bounded
  `MaxActions=8`, `MaxModsPerAction=4`. Refusals (null/empty name, null
  mods, empty list, null install, null alias, over-bound) counted, never
  stored.
- **Gate:** `SetIsLoadedProvider(Func<string,bool>)` — boot wires PML
  `ModManager.Instance.IsModLoaded` through try/catch ⇒ false. ANY
  registered alias loaded ⇒ action eligible. Provider absent or faulting
  ⇒ deny-by-default: nothing installs (counted `GateDenyCount`, line
  `CompatGateUnavailable (deny-by-default)`).
- **Install:** `InstallAll()` — snapshot under lock, gate per action
  (provider read OUTSIDE locks), install invoked OUTSIDE all locks with
  per-action try/catch: one faulting action cannot block the others or
  the caller (`CompatActionFaulted name=…`, `FaultCount`). Already-
  installed actions are skipped (manager-level idempotence; production
  delegates are additionally self-guarded — the MoreBotsCompatPatch
  `_installed/_triedInstall` pattern). Emits `CompatInstalled name=…`
  per install and one summary `CompatInstall actions=N installed=N
  skipped=N`.
- **Status:** `StatusLines()` — bounded ≤12 lines (counters + one line
  per action). The P29 consumption surface. Readback properties:
  ActionCount / InstallCalls / InstalledActions / SkippedActions /
  FaultCount / GateDenyCount / RefusedRegistrations / LastSummary.
- **Purity:** pure C# domain — IL-verified zero references to
  PulsarModLoader / HarmonyLib / PhotonNetwork / PLPlayer / PLServer /
  AccessTools / any pipeline type. PML stays behind the provider seam
  (Mod.cs).

### Boot wiring (Mod.cs, additive)

1. `CompatLogBridge.Ensure()` — decision listener onto `CapBotLog.COMPAT`
   (the subsystem MoreBotsCompatPatch already logs through).
2. `SetIsLoadedProvider` — try/catch-wrapped PML `IsModLoaded` (any fault
   ⇒ false ⇒ deny).
3. `RegisterAction("MoreBots class-0 crash guard", ["MoreBots"],
   MoreBotsCompatPatch.Install)` — the single production action.

### Install timing (owned by the existing call site — unchanged)

`SpawnBot.Execute` now calls `CompatManager.InstallAll()` instead of
`MoreBotsCompatPatch.Install()` directly. Timing is deliberately the same
moment as before: `/capbot` spawn. Rationale: the class-0 bot can only
come to exist from that point, and MoreBots' crashing prefix must be
treated before the new bot's first `GetAIData` frame. A boot-time install
would also race PML's own mod-list finalization for no benefit.

## 3. What Phase 27 deliberately does NOT do

- No new compat guards (TMPI/ExpandedGalaxy guards stay unimplemented —
  see F1; adding untestable reflection surgery against third-party
  internals is worse than an honest README).
- No changes to MoreBotsCompatPatch behavior or its Harmony targets.
- No config toggles (compat hygiene is always-on like the P18–P24
  deterministic directors; there is nothing user-tunable about "don't
  crash when MoreBots is loaded").
- No NonCaptainMenu button-merge work (F2 stays documented; a merge is a
  behavior change for existing users, owned by a future phase with its
  own test coverage).
- No WorldTick/driver changes (event-free, tick-free, patch-free phase).

## 4. Tests

`tests/CompatManagerTests.cs` CM01–CM10 (50 assertions): deny-by-default
(no provider / faulting provider); gate semantics (any-alias-loaded ⇒
install, none ⇒ skip); manager idempotence across InstallAll calls;
per-action fail-safety; bounded + duplicate-safe + refusal counting;
status lines/readbacks; determinism; registration-order install order;
gate-recovery accounting; MoreBots production-shape mirror (self-guarded
delegate + manager idempotence + same-name re-registration no-op).

## 5. Test-design gotchas

- The suite exposes `LastPassed` like every other suite — run 1 compile
  failure (CS0117) was a missing `LastPassed`, not a domain bug.
- CM02 skip accounting: an already-installed action takes the
  `alreadyInstalled` continue path, which is NOT a gate skip — only the
  genuinely gated-off action increments `SkippedActions`.
- CM10 mirrors the REAL production shape (one action, self-guarded
  delegate, same-name re-registration no-op). An earlier draft tried to
  "layer" two delegates over one shared counter — self-contradictory by
  construction; don't mimic semantics you can't state cleanly.
- Verify-script preloads: the game's PML file is `PulsarModLoader.dll`
  (NOT `PML.dll`) and `ACTk.Runtime.dll` is required for SpawnBot
  bodies. A wrong name + `Test-Path` guard silently skips the DLL and
  the failure surfaces later as a null type or an unresolved body.

## 6. Verification

- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2519 failed=0` ×3 consecutive (suite now 26 domain
  files, 17 suites; CM suite 50/50 after fixing 1 missing `LastPassed`
  compile error + rewriting CM10 to the faithful production shape).
- Reflection (`verify_build_p27.ps1`): 47/0 — manager type/members/consts;
  MoreBotsCompatPatch intact; SpawnBot.Execute dispatches
  `CompatManager.InstallAll`; manager IL purity (zero forbidden refs);
  Mod ctor wiring IL-probed (PulsarModLoader + ACTk preloads fixed — the
  probe that had to SKIP in P25/P26 now passes); Harmony patch
  classes == 11; CapBotLog.COMPAT intact; prior-phase types intact.

## 7. Conflict engine (Phase 46, `Core/Compatibility/`)

Deterministic classification + quarantine state machine for the standing
mod-conflict directive. The engine DECIDES; the production layer (future
phase) executes any physical quarantine and reports outcomes back. LLM
input is a recommendation only and can never reach the state machine.

- **Model (`ConflictModel.cs`):** `ConflictClass` A (safe overlap →
  KEEP_BOTH) / B (manageable → COMPATIBILITY_FIX) / C (feature conflict →
  DISABLE_FEATURE) / D (mod conflict → quarantine-eligible); `ConflictConfidence`
  UNVERIFIED < PROBABLE < CONFIRMED; 12 `SymptomKind`s; 8 `RefusalReason`s
  encoding the directive's never-remove-for list (shared dependency, Harmony
  usage, touching PLPlayer/PLBot, filename similarity, static speculation,
  protected mod, insufficient evidence, safe isolation available);
  `ConflictRules.Classify` = the authoritative ladder + shared audit
  vocabulary (`ClassText/ConfidenceText/ActionText/RefusalText`).
- **Causality contract:** removal-removes-failure (A/B: symptom with mod,
  absent without) is necessary; reintroduction-reproduces is required for
  CONFIRMED. Without the reintroduction leg the verdict is PROBABLE and the
  action is OBSERVE — even for overwhelming counts (the MoreBots honesty
  invariant, test CE17, mirrors the real archived A/B: 21,807 IndexOOB but
  no reintroduction test ⇒ observe, never quarantine).
- **Quarantine path:** only `Class D + CONFIRMED` reaches
  `Remediation.Quarantine`. State machine: `QuarantineRecommended →
  Quarantined` (executor confirms the physical move) `→ RestoredForRetest →
  CompatibleAfterRetest | QuarantineAgain`. Loop protection: the 3rd
  `QUARANTINE_AGAIN` event latches **Safe Mode** (idempotent, reasoned).
- **Protected mods (`ProtectedModList.cs`):** game/runtime/infra
  assemblies + CapBot itself + Quality Improver (master-prompt rule) are
  structurally unquarantinable — refusal=PROTECTED_MOD wins over ANY
  symptom or A/B evidence (test CE21 end-to-end).
- **Evidence intake (bounded):** `SetModProfile` (one boot-time snapshot
  from PML `GetAllMods`, flags enriched later by the runtime Harmony audit —
  never name strings), `RecordSymptom` (latest wins), `RecordComparison`
  (latest A/B wins), ≤32 tracked mods.
- **Audit:** every `Evaluate` emits a rate-limited
  `CompatibilityDecision mod= class= confidence= action= [refusal=] reason=`
  line — re-emitted only when the verdict text changes or 60s elapsed.
  Also `CompatibilityQuarantined` / `CompatibilityRetestRestored` /
  `CompatibilitySafeMode enabled` lines, and
  `ReportDuplicateCapBot` → `action=STOP_DUPLICATE_EXECUTION` audit (the
  caller must stop duplicate CapBot execution; duplicate CapBot plugins
  must never run concurrently).
- **Status:** `CompatStatus(mod)` vocabulary Loaded / Compatible /
  Conflict / Quarantined (+ reason); `StatusLines()` ≤14 lines. Exposed as
  `/capbotstatus conflicts` section and `/capbotcompat [mod]` chat command
  (read-only echo of engine verdicts; host-only).
- **Record layer (P46.1, `QuarantineRecord.cs`):** the data contracts the
  executor will persist — `QuarantineRecord` (hand-rolled bounded JSON;
  evidence attached verbatim with proper control-char escaping, never
  paraphrased; `CanRecord` factory refuses anything below CONFIRMED
  Class D), `CompatibilityStateRow` (+ `BootMustKeepQuarantined`:
  a mod left in Quarantined/QuarantineAgain stays quarantined across
  reboot — boot NEVER auto-restores; the DisabledUntilCompatibilityTest
  semantics), and a bounded 256-entry `CompatibilityAuditTrail` with
  drop counting. No IO in this layer (pure shapes; the executor writes).
- **State-listener seam (`ConflictEngine.SetStateListener`):** fires on
  every quarantine-state TRANSITION (never on Evaluate recommendations;
  idempotent re-confirm does not re-fire; cleared by `ResetForTests`) —
  the hook the physical executor consumes to move the DLL and write
  `conflict.json` / `compatibility-state.json`.
- **Purity:** pure C# domain — zero PULSAR/PML/Harmony/file-IO references
  (the P19 lesson); the engine performs NO file moves itself.

### Boot wiring (Mod.cs, additive)

1. `ConflictLogBridge.Ensure()` — decision listener onto `CapBotLog.COMPAT`.
2. Inventory feed — one `GetAllMods()` snapshot at boot:
   `ConflictEngine.SetModProfile(name, IsProtected(name), false, …)`;
   whole feed try/catch-wrapped (fault ⇒ empty registry, fail-safe).

### What Phase 46 deliberately does NOT do

- No physical quarantine executor at P46 time (file moves + conflict.json
  writing landed in Phase 47 — see §8 below; the state machine, records,
  and state-listener seam were ready and testable at P46).
- No runtime Harmony-map enrichment of `usesHarmony` flags yet.
- No A/B automation (experiments stay manual one-variable runs).
- No Safe Mode behavioral changes yet (latch + audit only; the boot-safety
  gate `DisabledUntilCompatibilityTest` semantics are enforced by the
  Phase 47 boot gate — the `CompatibilityStateRow.BootMustKeepQuarantined`
  rule it enforces is already in the model).

### Tests

`tests/ConflictEngineTests.cs` CE01–CE25 (171 assertions): refusal ladder;
protected-mod precedence; symptom-without-A/B ⇒ observe; non-implicating
A/B ⇒ keep-both; partial causality ⇒ PROBABLE/OBSERVE; full causality ⇒
CONFIRMED/quarantine state machine incl. idempotent re-confirm; Class C
never quarantines; symptom→class mapping table; rate limiting; loop
protection + safe mode; restore/retest clean; invalid transitions;
duplicate-CapBot audit; bounded status surface; determinism; audit-line
vocabulary; MoreBots honesty mirror; tracking bounds; reset; status
edges; protected-list membership; state-listener transition events
(CE22); record factory + verbatim JSON escaping (CE23); boot-gate rule
(CE24); audit-trail bounds + drop counting (CE25). Suite total after
P46.1: 3249/0.

## 8. Quarantine executor (Phase 47, `Core/Compatibility/`)

The production layer P46 deferred: the ONLY compatibility component
performing file IO. `QuarantineExecutor.cs` is static, thread-safe
(single lock), and wired exclusively in `Mod.cs` behind try/catch
fail-safe seams — one faulting seam never blocks the mod from loading;
an unwired executor refuses ALL operations (fail-closed).

### Production seams (Mod.cs)

- `SetModsDirProvider` → `PulsarModLoader.ModManager.GetModsDir()`
  (STATIC call — reflection-verified against PML 0.12.3.31; there is no
  instance accessor). Any fault ⇒ executor refuses, nothing quarantines.
- `SetIsProtectedProvider` → `ProtectedModList.IsProtected` (second
  gate, defense-in-depth: the engine refuses protected mods first).
- `SetFileHashProvider` → SHA-256 hex; file opened with
  `FileShare.ReadWrite` because PML keeps no locks (mod DLLs are
  UNLOCKED while the game runs — verified live; PML releases handles
  after load).
- Boot audit: one-time `QuarantineExecutor wired modsDir=…` line
  emitted AFTER the boot gate (P47.1 ordering fix — the first P47 boot
  logged an empty value because the line ran before any provider call
  resolved and stamped `m_LastModsDir`).

### Operations

- **Quarantine(modName, modAssembly):** refusal ladder first (identity:
  empty name / non-.dll / filename-hostile chars; protected mod;
  missing assembly; unwired provider) — every refusal audited
  `CompatibilityQuarantineFailed` with a reason. Success path:
  atomic `File.Move` to
  `<modsDir>\CapBot_Quarantine\<mod>\<timestampMs>\<assembly>.dll`
  (bytes never rewritten), then `conflict.json` written next to the
  moved DLL = `QuarantineRecord` JSON with an appended
  `"assemblySha256"` line. If the record write fails the DLL is moved
  BACK (quarantine is atomic — never a half-quarantined copy). Runtime
  moves take effect next boot (PML loads mod DLLs once at boot).
- **WriteState(rows) / ReadState():** `compatibility-state.json` in
  `CapBot_Quarantine\` — temp file + `File.Replace` (atomic-ish; a torn
  file must never disable boot safety). Hand-rolled bounded parser
  (≤64 rows, control-char-safe). Corrupt or missing ledger ⇒ empty
  list = boot fail-open (a mod with no ledger entry is not quarantined;
  its DLL is present and boots — the correct fail-open for "unknown").
- **RestoreForRetest(modName, modAssembly, quarantineDir):** moves the
  DLL back for a compatibility retest; refuses to overwrite an existing
  destination and refuses when the quarantined source is missing.
- **ProductionModsDir:** readback of the last live-resolved mods dir
  (stamped by `ReadState`/`WriteState` after validation; empty until
  first resolution).

### Boot gate (Mod.cs, P47)

`ReadState()` → `ConflictEngine.SeedQuarantineState(...)` per row —
BEFORE any engine evaluation. Seeding is ONLY accepted from engine
state None (live evidence always wins), refuses unknown state text
before any record is created, and never fires the state listener
(no ledger rewrite loop at boot). Boot NEVER auto-restores: a mod the
ledger says is Quarantined/QuarantineAgain stays quarantined
(`DisabledUntilCompatibilityTest` semantics — the DLL was physically
moved; only an explicit `/capbotcompat` retest flow restores).
Seeded rows are audited: `ConflictEngine boot gate seeded N ledger row(s): …`.
State-listener → ledger rebuild: every state transition rewrites the
ledger from `TrackedModNames() × QuarantineStateText()`, skipping
`""`/`None`/`QuarantineRecommended` (states that survive reboot
meaningfully only).

### Tests (QE01–QE14, 52 assertions, `tests/QuarantineExecutorTests.cs`)

Refusal ladder + audit coverage (QE01–QE04); atomic quarantine incl.
bytes-preserved + JSON shape + SHA (QE05); write-fault rollback
(QE06 — DLL restored, no half-quarantined copy); ledger roundtrip +
verbatim escaping + bounded parse + corrupt/missing fail-open (QE07–QE09);
restore-for-retest incl. refusals (QE10); ledger seed accepted/refused
(live-wins, unknown-text refusal creates no record) (QE11–QE13); and
the full-causality E2E (QE14): record symptom → A/B comparison
(true/false/true) → CONFIRMED Class D → executor moves the DLL →
engine re-marks Quarantined → state-listener (wired exactly as Mod.cs)
rebuilds the ledger → simulated reboot (`ResetForTests` + re-seed) →
`CompatStatus` reports Quarantined. Suite total after P47: 3301/0
(verified twice pre-deploy; boot re-verified live with 0 wiring
failures / 0 exceptions).

### What Phase 47 deliberately does NOT do

- No runtime Harmony-map enrichment of `usesHarmony` flags yet.
- No A/B automation (experiments stay manual one-variable runs).
- No Safe Mode behavioral changes yet (latch + audit only).
- No symptom detectors wired to live telemetry yet (the engine's
  `RecordSymptom`/`RecordComparison` callers are tests only; live
  exception fingerprinting is a future phase).