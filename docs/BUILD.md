# BUILD / REPRODUCIBILITY (Phase 33)

> **Ownership argument.** Phase 33 makes the build reproducible and
> machine-agnostic without changing a single line of shipped code: the
> csproj was already parameterized (`$(PulsarManaged)`, PART 48) and
> deterministic (`Deterministic=true`). This phase adds the parameterized
> build entry point, the CI pipeline, the smoke checks, and the contract
> documentation. No Harmony/pipeline changes.

## 1. Reproducibility contract

**Inputs (and nothing else):** repository source at a commit + the game's
`Managed` DLL folder (provided, never committed) + NuGet packages
(OpenSesame Roslyn toolset 4.0.1, committed in `packages/`, restorable
from packages.config).

**Invocation (no personal paths anywhere):**

```
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 `
    -PulsarManaged "C:\Path\To\PULSAR_LostColony_Data\Managed" `
    [-Configuration Release] [-Clean]
```

- `-PulsarManaged` is REQUIRED (validated to exist and contain
  `Assembly-CSharp.dll`/`Pulsar.dll`/`PulsarModLoader.dll`).
- MSBuild is auto-discovered (VS 2022/18/2019 BuildTools, Community) or
  passed explicitly via `-MsBuildPath`.
- The csproj's own default `PulsarManaged` fallback is a LOCAL convenience
  only; the contract is that scripts and CI always pass it explicitly.

**Determinism:** `<Deterministic>true</Deterministic>` + fixed
`LangVersion 8.0` + `net472`. Same source commit + same game DLLs ⇒ the
same DLL modulo the compiler's embedded timestamps (pdbonly PDB path
strings are the known variance; the DLL itself is deterministic for
reference-identical inputs).

## 2. Smoke checks (both build.ps1 and CI)

1. Output DLL exists, ≥1 KB, MZ header (a real PE image).
2. **No machine leak:** the DLL must NOT contain the resolved
   `PulsarManaged` path (Unicode scan) — a reference HintPath must never
   bake the build machine's path into the artifact.
3. No build-machine username embedded.
4. Test gate: the pure-C# suite (`tests/run_tests.ps1`) passes
   `TOTAL passed=N failed=0` three consecutive times in CI.

## 3. build.ps1 behavior

Validate inputs → locate MSBuild → optional `-Clean` → nuget restore
(falls back to requiring the committed `packages/` when nuget is absent)
→ build with `/p:PulsarManaged=<given>` → smoke checks → gate line
`BUILD OK dll=<path> bytes=<n> config=<c>`. Any failure exits non-zero
with a `BUILD FAILED: <reason>` line. Exit code 0 is the machine-readable
"BUILD OK" gate.

## 4. CI (`ci/pipeline.yml`)

GitHub Actions-style workflow, windows-latest:

1. checkout → setup-msbuild → setup-nuget
2. **Game-DLL staging contract:** the runner supplies the game's Managed
   folder via the `PULSAR_MANAGED` repository variable (staged from a
   private cache by the CI operator). The pipeline FAILS if unset/empty —
   the game DLLs are never committed to the repo.
3. restore → build (parameterized) → reproducibility smoke checks
4. test gate: the dev suite runs 3× and every run must print
   `RUNNER: TOTAL passed=N failed=0`
5. artifact upload of `CapBot.dll` keyed by the commit SHA

**Deliberately NOT in CI:** the reflection verify scripts
(`verify_build_pNN.ps1`) require the live game install and are
dev-machine QA artifacts; CI stops at the pure-domain gate + smoke
checks. The 11-patch-class / IL-order audits run locally per phase as
before.

## 5. What Phase 33 deliberately does NOT do

- No production code changes (build infrastructure only).
- No signing/strong-naming (PML mods are unsigned; adding one changes
  loader behavior — out of scope without an ecosystem change).
- No version-bump automation (AssemblyInfo versioning stays manual).
- No push/Workshop/release automation (P33 stops at local build+test;
  distribution requires separate owner authorization per the master
  prompt).
- No hardcoded game paths anywhere (the audit's original sin stays
  fixed: the csproj fallback is overridable and no script defaults to a
  personal path).

## 6. Verification (this phase)

- build.ps1 smoke-validated locally: parameterized build (real game path
  passed as a PARAMETER, never written into any repo file) → BUILD OK,
  smoke checks pass (no path/username embedding in the artifact).
- Test gate: `TOTAL passed=2777 failed=0` ×3 consecutive (unchanged —
  P33 adds no production code).
- The pipeline file is syntactically reviewed against the GitHub Actions
  schema (windows-latest, setup-msbuild/nuget, artifact upload); actual
  CI execution requires a GitHub remote — the local build.ps1 IS the
  verified reproducible path today.