# SECURE UPDATER (Phase 30)

> **Ownership argument.** Phase 30 hardens the existing `/updateall` engine
> (audit C1, M5, L3) without changing what it does: it still checks every
> loaded mod's VersionLink and updates newer DLLs. What changes is that
> every download decision now routes through a verification policy, the
> staged apply is atomic, and the failure modes refuse closed. No new
> Harmony patches, no tick drivers, no pipeline changes (11-class ceiling
> holds). The policy layer is pure C#; the engine keeps transport.

## 1. Findings addressed (evidence-based)

| Audit | Finding | P30 resolution |
|---|---|---|
| C1 (SECURITY) | Arbitrary DLL install: no HTTPS enforcement, no domain allowlist, no SHA-256, no signature check | Full verification chain (§2): HTTPS-only + bounded GitHub-family allowlist + payload shape + SHA-256 digest when published + strict file-name defense |
| M5 (ROBUSTNESS) | `File.Delete(target)` then `File.Move(staged)` — non-atomic; a failure between leaves the mod DLL deleted | `File.Replace(staged, target, backup)` (atomic swap with rollback target) + best-effort backup cleanup; first-install path stays `File.Move` |
| L3 (hygiene) | Chrome user-agent spoof | Honest `CapBot-Updater/1.0 (PULSAR: Lost Colony PML mod updater)` |

**Residual risk (documented, not silently accepted):** when a publisher's
version JSON carries NO `Sha256` field, install proceeds on scheme+shape
alone — an HTTPS/allowlist/MITM-resistant but content-unverified install.
A garbage or mismatched digest REFUSES (never bypasses); the discipline
for publishers is "publish the digest with the version file". CapBot's
own release process should publish `Sha256` alongside `DownloadLink`.

## 2. UpdatePolicy (`Core/Update/UpdatePolicy.cs`, pure C#)

- **URL gate** `CheckDownloadUrl(url)`: HTTPS-only (http/ftp/file refused),
  host must be an exact case-insensitive match against a bounded 5-host
  GitHub-family allowlist (`github.com`, `www.github.com`,
  `raw.githubusercontent.com`, `api.github.com`,
  `objects.githubusercontent.com`), userinfo (`user:pass@host`) refused,
  malformed input refused. Enum verdict + `VerdictName` for report lines.
- **Payload gate** `ValidateDllBytes(bytes)`: non-null, ≥1 KB, ≤32 MB, MZ
  header (0x4D 0x5A). A JSON error page, HTML login page, or truncated
  response never installs. Refusal reasons are short fixed tokens
  (`null/too-small/too-large/not-mz`).
- **Digest gate**: `ComputeSha256` (lowercase 64-hex, reference-vector
  tested); `VerifySha256(bytes, expected)` — no digest published ⇒ true
  (documented residual); malformed/short/garbage digest ⇒ false; mismatch
  ⇒ false; case-insensitive match. `TryGetShaFromVersionJson` extends the
  engine's tolerant scrape with a case-insensitive `Sha256` field.
- **File-name defense**: `SanitizeDllFileName` accepts ONLY a bare
  `<name>.dll` (≤96 chars, no separators/colon/`..`/reserved chars).
  Path-shaped input is REFUSED, not flattened — suspicious input is never
  normalized into acceptance. Never throws (the first draft used
  `Path.GetFileName`, which throws ArgumentException on some `..` shapes
  on .NET Framework — UPD07 caught it).
- **Purity** (IL-verified): zero PML/game/Harmony/WebClient/File
  references; the policy layer never touches the network or the file
  system.

## 3. Engine wiring (ModUpdater.cs)

Per mod, in order: version-file URL gate → version JSON fetch →
parse → download-link gate → version compare → download → shape gate →
digest gate → file-name gate → write (or stage). Refusals emit
`[blocked] <mod>: <reason>` report lines and count in a new `blocked`
tally (`Updated: …, blocked: N, failed: N`). Both gates run BEFORE any
connection/write. Staged names use the sanitized bare name. Staged apply
is atomic (§1). No change to PML handling (PML's own update path was
already manual-only and is unchanged).

## 4. What Phase 30 deliberately does NOT do

- No signature verification (PML ecosystem has no signing infra; SHA-256
  via HTTPS+allowlist is the practical bound — noted for a future phase
  if the ecosystem adds signatures).
- No async/background downloads (sync WebClient on the command thread is
  the existing UX; threading is P31's concern if measured to matter).
- No user-confirmation UI change (`/updateall` remains the explicit
  operator action; the boot-time `ModUpdaterEnabled` path now benefits
  from the same verification chain automatically).
- No changes to version-compare semantics (CompareVersions untouched).

## 5. Tests

`tests/UpdatePolicyTests.cs` UPD01–UPD10 (55 assertions): URL gate
(https/allowlist/scheme/userinfo/suffix-spoof/malformed); payload shape
(null/empty/too-small/too-large/JSON/HTML/MZ); SHA-256 determinism +
format + different-payload divergence; verify rules (match/case/
mismatch/malformed/short/no-digest/null); digest extraction
(present/absent/null/case); strict file-name defense (traversal, nested,
drive, extension, reserved, length); chain composition (honest payload
passes all gates; tampered payload shape-passes but digest-refuses;
error page refused at shape); SHA-256("abc") reference vector; bounds
constants.

## 6. Test/verify gotchas

- `Path.GetFileName` THROWS on some `..` traversal shapes under .NET
  Framework ("Illegal characters in path") — the sanitizer is manual
  parsing, no Path APIs on untrusted input.
- Strict-refusal over flatten: the first draft flattened
  `..\evil.dll` → `evil.dll` (safe but permissive); the security-phase
  semantics is REFUSE path-shaped input (a publisher shipping a path in
  `FileName` is a red flag).
- The engine's remaining `File.Delete` deletes the `.old` BACKUP after
  `File.Replace` — the M5 IL proof is ORDER (Replace precedes Delete),
  not absence of Delete.
- PS gotcha: `if (-not $x -ge 0)` parses as `(if (-not $x) -ge 0)` —
  initialize sentinels directly, never re-assign via that idiom.

## 7. Verification

- Build: MSBuild Release 0 warnings / 0 errors.
- Tests: `TOTAL passed=2669 failed=0` ×3 consecutive (suite now 29 domain
  files, 20 suites; UPD suite 55/55). Run-1 findings: 1 real product bug
  (sanitizer threw on traversal input — caught by UPD07 before any
  release; fixed to never-throw strict refusal) + 2 test-authoring
  slips (VerifySha256 arg types, stale variable name).
- Reflection (`verify_build_p30.ps1`): 34/0 — policy surface/consts
  (MaxDllBytes/MinDllBytes/5-host allowlist); UpdateAll IL gates through
  ALL five policy functions; staged apply IL order Replace@147 →
  Delete@154 (M5 atomic swap + backup cleanup); honest UA; policy IL
  purity (no PML/game/network/file refs); 11 patch classes; prior-phase
  types intact.