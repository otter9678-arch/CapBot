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
  from PML `GetAllMods`, flags enriched later by the runtime Harmony
  audit — P48 §9: `MarkHarmonyObserved` flips `false→true` only, from
  the live patch map — never name strings), `RecordSymptom` (latest
  wins), `RecordComparison` (latest A/B wins), ≤32 tracked mods.
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
- No runtime Harmony-map enrichment of `usesHarmony` flags yet (landed
  in Phase 48 — see §9 below).
- No A/B automation (experiments stay manual one-variable runs).
- Safe Mode behavioral suspension landed in Phase 50 (see §11 below);
  at P46 time the latch was audit-only. The boot-safety gate
  `DisabledUntilCompatibilityTest` semantics are enforced by the
  Phase 47 boot gate — the `CompatibilityStateRow.BootMustKeepQuarantined`
  rule it enforces is already in the model.

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

- No A/B automation (experiments stay manual one-variable runs).
- No Safe Mode behavioral changes yet (landed in Phase 50 — see §11
  below).
- No symptom detectors wired to live telemetry yet (the engine's
  `RecordSymptom`/`RecordComparison` callers are tests only; live
  exception fingerprinting is a future phase).

## 9. Runtime Harmony-map enrichment (Phase 48, `Patch.cs` + engine)

The conflict engine's `usesHarmony` profile flags — seeded from PML
metadata at boot (§7) — are enriched from the LIVE Harmony patch map at
the first in-ship tick. Static filename speculation is gone: enrichment
reflects what actually patched, observed from the running game.

### Intake API (`ConflictEngine.MarkHarmonyObserved`)

`MarkHarmonyObserved(modName, harmonyOwner, nowMs)` — strictly
conservative: refuses null/empty/unknown mods; records the Harmony owner
id read-only; when `UsesHarmony` is false it flips true (never
un-sets), increments `m_HarmonyEnrichedCount`, and emits a one-time
`CompatibilityHarmonyEnriched mod=<name> owner=<owner|unknown>` audit.
Idempotent (re-observation never double-counts). Readbacks:
`HarmonyOwnerText`, `HarmonyEnrichedCount`; the `/capbotstatus conflicts`
summary carries `harmonyPatching=<count>`. Honesty invariant (CE26):
enrichment alone never strengthens a weak A/B — a mod enriched to
"uses Harmony" without causal evidence still evaluates
`RefusalReason.UsesHarmonyOnly → KeepBoth`, never quarantine.

### Auditor (`Patch.HarmonyMapAudit`, one-shot on first WorldTick)

Runs only in the ship/game scene (verified: WorldTick does NOT fire at
main menu or the Join-a-Crew lobby, so the audit line appears only after
entering a crew game). Builds the owner→mod map from
`ModManager.GetAllMods()` × `mod.HarmonyIdentifier()`, walks
`HarmonyLib.Harmony.GetAllPatchedMethods()` ×
`PatchProcessor.GetPatchInfo(method).Owners` (0Harmony v2.2.2.0 — API
surface reflection-verified, not invented), calls `MarkHarmonyObserved`
once per patching mod, and emits a single summary
`HarmonyMapAudit patchedMethods=<n> owners=<n> modsPatching=<n> enriched=<n>`.
Fully try/catch fail-safe — any fault emits
`Harmony map audit fault (skipped)` once and never retries.

### Live verification (P48 boot, offline crew, in-ship)

`HarmonyMapAudit patchedMethods=435 owners=493 modsPatching=7 enriched=1`
plus `CompatibilityHarmonyEnriched mod=Talents owner=Mest.Talents` —
the TMPI (TalentsModPerformanceImprovement) owner id, matching its
author. 0 wiring failures; the only in-game exception is the known
pre-existing TMPI `PLShipInfoUpdatePatch.TalentsUpdateNeeded` NRE
(not CapBot, unchanged from P45/P46 observations).

### What Phase 48 deliberately does NOT do

- No A/B automation (experiments stay manual one-variable runs).
- No Safe Mode behavioral changes yet (landed in Phase 50 — see §11
  below).
- Enrichment is evidence only: it feeds `CompatibilityHarmonyEnriched`
  audit lines and `harmonyPatching=`, never the quarantine path by
  itself (CE26 pins the refusal ladder).

## 10. Symptom detectors wired to live telemetry (Phase 49, `Mod.cs` + `SymptomDetectors.cs`)

The engine's `RecordSymptom` intake now has its production caller: the
Unity log stream is subscribed once at mod init and every non-noise log
event flows through a pure-C# windowing domain that counts exception
fingerprints per attributed mod. At the storm threshold (20 events /
60 s) the detector records an `ExceptionStorm` symptom into the engine
and triggers an `Evaluate` — but a recorded symptom alone can only ever
classify PROBABLE / OBSERVE: the quarantine path is unreachable from
detectors (the A/B legs stay manual; the directive's honesty ladder
applies downstream unchanged).

### Wiring (Mod.cs, fail-safe try/catch)

- `SymptomLogBridge.Ensure()` attaches the audit listener (same seam
  pattern as ConflictLogBridge).
- Production resolver: loaded PML mods' assembly names matched
  OrdinalIgnoreCase against the stack text; protected-list assemblies
  are skipped (a protected mod's exceptions never enter the symptom
  path); unresolvable events increment the unattributed counter and are
  never invented into evidence; a resolver fault fails closed to
  unattributed.
- `UnityEngine.Application.logMessageReceived += (c, s, t) =>
  OnLog(c, s, (int)t, TaskClock.NowMs)` — 3-arg LogCallback,
  reflection-verified against UnityEngine.CoreModule (no LogEventArgs
  type exists in the game's Managed dir).
- Success line `SymptomDetectors wired (log pipeline live, exception
  fingerprinting on)`; any fault emits
  `SymptomDetectors wiring failed (fail-safe: no live symptom feed)`.

### Honesty rules (`SymptomDetectors.OnLog`)

- LogType Warning(2)/Log(3) are noise and never counted (verified
  mapping: Error=0, Assert=1, Warning=2, Log=3, Exception=4).
- CapBot's own `[CapBot:` log lines are skipped (never self-attribute).
- Fingerprint = exception header + first `at` frame, ≤200 chars,
  per-mod key bounded to 64 windows (overflow counted as dropped).
- Storm latch: one `RecordSymptom` per 20/60 000 ms crossing; window
  expiry resets the count (SY05 semantics). The detector never
  classifies — the engine's ladder stays the only authority, and
  symptom-without-A/B is capped at PROBABLE/OBSERVE.
- The whole intake body is try/catch swallowed: the detector must never
  become the crash source.

### Live verification (P49 boot, offline crew, in-ship)

`SymptomDetectors wired (log pipeline live, exception fingerprinting on)`
present; 0 wiring failures; 0 StatusFault. The known pre-existing TMPI
NRE (`TalentsModPerformanceImprovement.PLShipInfoUpdatePatch.
TalentsUpdateNeeded` via `PLShipInfo.Update_Patch3`; 2 occurrences this
session, below the storm threshold) was correctly attributed to mod
`Talents`: /capbotstatus conflicts showed
`SymptomDetectors: tracked=1 storms=0 unattributed=1 dropped=0
threshold=20/60000ms` with window row
`SymptomDetectors: Talents|NullReferenceException|
PLShipInfoUpdatePatch.TalentsUpdateNeeded count=1` — later count=0
after 60 s window expiry (SY05 verified live). unattributed=1 is the
PML ExceptionWarningPatch re-emitted raw exception line (no
attributable mod stack); nothing invented, nothing escalated — no
`SymptomDetected`/`CompatibilityDecision` lines fired because the
threshold was never reached.

### What Phase 49 deliberately does NOT do

- No A/B automation (experiments stay manual one-variable runs).
- No Safe Mode behavioral changes yet (landed in Phase 50 — see §11
  below).
- Detectors are evidence-only: a storm records a symptom and triggers
  evaluation, but quarantine stays structurally unreachable from the
  detector path (Class D + Confirmed requires the manual A/B legs).
- Attribution is stack-text matching only: a mod whose exceptions carry
  no matching assembly name in the stack text is counted unattributed,
  never guessed.

## 11. Safe Mode behavioral suspension (Phase 50, `Core/Compatibility/`)

The P46 engine latch finally has its production reader. `SafeModeGate`
is a pure-C# static domain (no Unity/PML/Harmony references — the P19
narrow-compile lesson) whose single `Tick(nowMs)` method answers one
question for three behavioral call sites: is the engine's Safe Mode
latched this frame? When it is, CapBot's own behavior freezes for the
rest of the session while ALL evidence collection stays live — the
directive's honesty ladder applies to the observers, never to the
observed.

### Gate contract

- **Provider seam:** `SetSafeModeProvider(() =>
  ConflictEngine.SafeMode)`, `SetSafeModeReasonProvider(() =>
  ConflictEngine.SafeModeReason)` — wired in Mod.cs behind a fail-safe
  try/catch. Unwired provider → the gate observes not-latched (nothing
  invented); a FAULTING provider → latched=true → suspend (fail-
  closed); an internal gate fault → `Tick` returns true (never
  silently re-enable behavior while the engine may be latched).
- **Session-sticky suspension:** once latched this session, the
  behavioral suspension stays until a future phase's explicit un-latch
  flow. This mirrors the engine itself — its latch is idempotent and
  has no auto-clear; mirroring that here is the honest behavior.
- **Edge counting:** a rising edge (clean session latches mid-run)
  increments `SuspensionCount` and captures the engine's reason; a
  gate whose FIRST observation is already latched counts NO edge (the
  sticky latch itself is the state, and the engine's idempotent
  re-confirm must never re-count).
- **Evidence surface:** first observation emits a positive wiring-
  evidence status line (`SafeModeGate suspended=no ticks=…/…`); while
  suspended the gate re-emits at most every 60 s; status is bounded
  (4 lines). Readbacks: Suspended / Reason / SuspensionCount /
  TicksTotal / TicksSuspended.

### The three behavioral gates (Patch.cs, each fail-closed)

1. **WorldTick host pipeline** — placed AFTER the evidence blocks
   (WorldStateService.Refresh, HarmonyMapAudit.RunOnce,
   MultiplayerAuthorityMonitor.Observe stay live every frame) and
   BEFORE the `if (!isMaster) return;` driver: a latched Safe Mode
   freezes the scheduler / executor / directors this frame.
2. **PostfixCore legacy feature tick** — placed AFTER the default-AI
   data fill (bots keep a sane brain) and BEFORE `Autonomy.OnTick` AND
   the captain-bot block: talents, economy, research, missions,
   watchdog, smart item use, orders, shop, course planning all stop;
   bots fall back to vanilla AI.
3. **SpawnBot.Execute** — after the `capisbot` duplicate check: NEW
   spawns are refused with a PML notification. A pure refusal: no
   state mutation, no `/capbotstatus` change.

Evidence collection is deliberately NOT gated anywhere: world refresh,
Harmony audit, authority observation, and symptom detectors all keep
running under Safe Mode — the mod suspends its BEHAVIOR, never its
EVIDENCE.

### Wiring (Mod.cs, fail-safe try/catch)

`SafeModeLogBridge.Ensure()` routes gate lines to the `[CapBot:COMPAT]`
log (SymptomLogBridge seam pattern). Providers read
`ConflictEngine.SafeMode` / `ConflictEngine.SafeModeReason` directly.
Any wiring fault emits `SafeModeGate wiring failed (fail-safe: gate
observes not-latched)`; the absence of that fault line plus the gate's
own first status line are the wiring evidence (no invented success
line).

### Status surface

`/capbotstatus conflicts` emits `SafeModeGate.StatusLines()` between
the engine's lines and the detectors':
`SafeModeGate: suspended=<yes|no> [reason=…] edges=<n> ticks=<s/t>
lastAtMs=<ms|none>` plus the coverage line
`SafeModeGate: covers hostTickPipeline, legacyFeatureTick, capbotSpawn;
evidence collection NOT gated`.

### Tests

`tests/SafeModeGateTests.cs` SM01–SM12 (37 checks): unwired-inert;
latch-suspends; sticky un-latch; edge reason capture; provider-fault
fail-closed; reason-provider fault survival; first-observation-latched
counts no edge; first-emit status line; 60 s re-emit interval; status
shape + reset; engine-integration E2E via a real ConflictEngine latch
(the CE10 recipe: SetModProfile → RecordSymptom + RecordComparison →
Evaluate → MarkQuarantined → restore/retest ×3 → Safe Mode latched with
"repeated re-confirmed conflicts"). Suite total after P50: 3386/0.

### What Phase 50 deliberately does NOT do

- No A/B automation (experiments stay manual one-variable runs).
- No explicit un-latch flow yet (the engine's latch is idempotent with
  no auto-clear; the gate mirrors it session-sticky — an owner-facing
  unlock command is a future phase).
- No auto-restore of quarantined mods at boot (standing rule; the
  Phase 47 boot gate still enforces
  `DisabledUntilCompatibilityTest`).
- No gating of evidence collection, ever: the engine latch itself, the
  detectors, the Harmony audit, and the authority monitor all stay live
  under suspension.
- The suspension is behavioral only: quarantined files stay quarantined
  (never modified), and no third-party binary is touched.