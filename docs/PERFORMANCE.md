# PERFORMANCE (Phase 31)

> **Ownership argument.** Phase 31 removes the audit's one live per-frame
> hot path (H2) without changing any decision: the five scripted-sector
> handlers keep producing identical AI targets, just at a bounded cadence
> instead of every frame. Policy is pure C#; wiring is two lines per
> handler. No new Harmony patches (11-class ceiling), no pipeline changes,
> no allocations added on the gated path.

## 1. Findings addressed (evidence-based)

| Audit | Finding | P31 resolution |
|---|---|---|
| H2 (PERFORMANCE) | Scripted-sector handlers run EVERY FRAME inside the captain Postfix with 2–5 full-scene `FindObjectsOfType` scans each (`AtColony` 2–3/frame, `WastedWing` 4–5/frame, `AtRaces`/`PlanetExploration`/`HighRollers` similar) — dozens of scene scans per frame in those sectors | `SceneScanGate` (§2): each handler gated at a 250 ms cadence (4 scans/sec max per key instead of ~60/sec); 30+ IL-verified gated call sites covered by 5 handler-entry gates |
| M4 (PERFORMANCE) | Per-tick allocations in BotEconomy/BotUpgrades/LINQ | Verified NOT current: those systems run inside the 4-second `LastSlowTick` window in `Autonomy.OnTick` (executor-gated, one pass per 4 s) — the audit predates the executor-gating rework. No change needed; documented. |

**Behavior note (documented, deliberate):** gating the whole handler means
its last-set `AI_TargetPos`/`AI_TargetTLI` persist in `PLBot` between gated
runs (game-side state, untouched by CapBot), so the game's own AI keeps
moving toward the last target while the handler recomputes on its 250 ms
cadence. Movement stays fluid (4 target updates/sec is above human
perception for pathing targets). `PlanetExploration`'s `ShouldHalt` stays
`false` between gated runs — the halt decision re-evaluates on the next
gated pass; the frame-in-between uses the last AI state (same targets as
the halted state would produce).

## 2. SceneScanGate (`Core/Perf/SceneScanGate.cs`, pure C#)

- `Allow(key, nowMs)`: true when ≥`IntervalMs` (250 ms) has passed since
  the key's stamp. First call ARMS and allows. Allow NEVER advances the
  stamp (the engine owns Commit — an un-committed pass re-runs on the
  next frame after the interval, by design: the scan is still pending).
- `Commit(key, nowMs)`: records the scan pass (advances the stamp).
- Bounded `MaxKeys=32` fail-closed (unknown keys over the bound refuse —
  same as every CapBot bound); null/empty keys refuse and never allocate.
- Wrap-aware deltas (unchecked int arithmetic, the TaskClock convention).
- Deterministic: no wall-clock reads; the engine passes `TaskClock.NowMs`.
- IL-verified purity: zero game/PML/Harmony/UnityEngine references.

### Handler wiring (Patch.cs, two lines each)

`AtColony` → key "colony"; `WastedWing` → "wastedwing"; `AtRaces` →
"races"; `PlanetExploration` → "planetexplore"; `HighRollers` →
"highrollers". Entry order: gate check → commit → original body (the
commit runs only when allowed, so a gated-off frame records nothing).

## 3. What Phase 31 deliberately does NOT do

- No async/threading changes (sync WebClient is P30's documented residual;
  threading work is unwarranted without a measured problem).
- No changes to the P14–P28 director cadences (all already gate-bounded
  1–5 s; per-frame cost is one integer compare each — verified in P22–P25).
- No allocation rework in BotEconomy/BotUpgrades (already on the 4 s slow
  tick; the audit's M4 premise no longer holds — see §1).
- No HarmonyPriority/ordering changes; no new patch classes (11 preserved).

## 4. Tests

`tests/PerfGateTests.cs` PERF01–PERF08 (63 assertions): first-allow arms;
within-interval refusal (Allow does not self-advance); Commit advances
the stamp; exact 250 ms boundary semantics (249 refuses / 250 allows);
un-committed interval passing re-allows (engine commits next); wrap-
aware deltas; bounded keys (32 fail-closed, reset, null/empty never
allocate); key isolation across the five handler keys; determinism.

## 5. Test/verify gotchas

- The gate stamp moves ONLY on first-allow (arming) and Commit — a
  boundary Allow at t+250 does NOT re-arm; the engine's Commit does.
  Test timelines must keep timestamps monotonic (a 1300 check after a
  1250 check crosses the interval and re-allows).
- Handler names collide with the `CapBot` parameter in Patch.cs —
  qualified `CapBot.Core...` inside those methods resolves the PARAMETER
  (`PLPlayer.Core`), not the namespace. Bare type names + usings.
- Verify-script preloads now also need `CrewAILibraryBuild.dll`,
  `UnityEngine.dll`, `Behave.Unity.Runtime.dll`, `Behave.Unity.Assets.dll`
  for the captain-patch method bodies (FindObjectsOfType generic
  instantiations pull transitive deps). MethodRefs is fault-safe per
  method (try/catch body read) so one unresolvable body cannot kill the
  probe.

## 6. Verification

- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2732 failed=0` ×3 consecutive (suite now 30 domain
  files, 21 suites; PERF suite 63/63 after fixing 6 test-authoring
  timeline errors — all in the boundary arithmetic, zero product bugs).
- Reflection (`verify_build_p31.ps1`): 24/0 — gate surface/consts; ALL
  five handlers IL-proven to call `SceneScanGate.Allow` with their exact
  key strings; gate IL purity; 11 patch classes; prior-phase types
  intact.