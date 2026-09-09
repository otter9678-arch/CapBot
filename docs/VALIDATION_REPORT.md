# VALIDATION REPORT — CapBot P1–P55 (release record)

> **Record date:** 2026-09-08. This is the §42–44 release-candidate
> verification record for the Alpha 1.2.2 expansion line (phases P1–P55,
> local commits through the P55 close-out). Every number in this file was
> produced by a command in this session — nothing is transcribed from
> memory. Vocabulary: LIVE-PASS = observed in a real game session;
> UNIT-PASS = dev-side pure-C# suite; UNVERIFIED = honestly not observed.

## 1. Artifact identity (reproducibility-proven)

| Item | Value |
|---|---|
| Artifact | `CapBot.dll` (Release, net472) |
| Size | 485,888 bytes |
| SHA-256 | `bdea3bb0a6801b77e42ecd7a3edb9dc94afddedc00f9fe86d8c59b4aa151b825` |
| MD5 | `710d159b24955297bd25b62e7528d580` |
| Rebuilt this session via | `build.ps1 -PulsarManaged "C:\SteamLibrary\steamapps\common\PULSARLostColony\PULSAR_LostColony_Data\Managed"` → `BUILD OK … bytes=485888` |
| Byte-identical to deployed | YES — deployed `Mods\CapBot.dll` SHA-256 matches (both hashes run in this session) |
| Smoke checks | PE header OK; no build-machine path/username embedded (build.ps1 smoke checks, P33 contract) |
| Deployed backup chain | `CapBot.dll.pre_p52.bak` = P51 build (`a87f690a…`), verified before overwrite |
| Git state | master, 54 phase commits, working tree clean, **NOTHING pushed** |

## 2. Test gates (this session)

| Gate | Result |
|---|---|
| Dev test suite (40 suites, 45 domain files) | **`TOTAL passed=3664 failed=0` ×3 consecutive runs** |
| Suite gates | run_tests.ps1 gates on `failed=0`; runner line `RUNNER: TOTAL passed=3664 failed=0` |
| Notable suites | SA01–SA04 settings-audit drift detector (190 checks, f40); ML01–ML14 mission lifecycle (69 checks, f39); SM01–SM12 Safe Mode (f38); SY01–SY12 symptom detectors (f37); CE01–CE17 conflict engine (f35); QE01–QE14 quarantine executor (f36) |

## 3. Live-validation evidence ledger (real sessions, this build lineage)

| Area | Evidence | Verdict |
|---|---|---|
| Boot (P52+P53 build, pid 176952) | `SafeModeGate suspended=no ticks=0/1`; `HarmonyMapAudit patchedMethods=434 owners=486 modsPatching=6 enriched=1`; 0 wiring failures; 0 real exceptions; `OllamaSelfTest=pass` | LIVE-PASS |
| Mission lifecycle FSM (P52) | `MissionOpened MISSION:I104884 objectives=1` + `MISSION:I103209 objectives=3`; 0 "lifecycle evaluate failed" | LIVE-PASS |
| `/capbotmission` on screen | director `missionless=0 dupSuppressed=0 expired=0 stale=0 sameTypeColl=0 returnSignals=0`; tracks `MISSION:I0x 0/4`, `MISSION:I103209 0/3`, `MISSION:I104884 0/1`; lifecycle `tracked=3 history=0 evals=20 transitions=6 returnReq=0` | LIVE-PASS |
| `/capbotsettings` on screen | 10/15 rows screen-verified: 5 LIVE rows wired (`OllamaModel qwen3:latest index 0`, `OllamaPort 11434`, …) + all 6 DEAD rows honest (`CONSUMER=NONE (…)`, `EFFECTIVE=not wired (no consumer)`); header + 5 rows above chat scrollback (screen-unverifiable; pinned by SA tests) | LIVE-PASS (honest partial coverage) |
| §14 agent chain (P54) | log: 7 AgentCreated / 7 Personality / 7 Memory at pids 0–6, all ALIVE; on-screen `agents=7 created=7 alive=7 removed=0 dupCreates=0`, `personalities=7 assigned=7`, `memoryAgents=7 memoryCreated=7`, `experienceRecords=0` (honest zero) | LIVE-PASS (7/7/7 for a 7-player world) |
| Prior-world 8/8/8 (Player-prev.log) | 8th agent AGT:1e70d24c pid=7 spawned via game's own crew path, tracked SPAWNING→ALIVE, 8×(Personality+Memory), later honestly REMOVED | LIVE-PASS (8/8/8 met in that world) |
| Ollama/Qwen advisors | `OllamaAdvice`/`CrewAdvice model=qwen3:latest` flowing; `OllamaModelAvailable=true`; recommend-only IL-proven | LIVE-PASS |
| Emergency suppression (P39) | `EmergencySuppressed COOLANTCRITICAL` no-progress guard holding (was 134-task storm in P38) | LIVE-PASS |
| Crew presence | `CaptainAgentPresence SPAWNING→ALIVE` ×7; presence machine honestly handles TEMP_UNAVAILABLE/REMOVED (prior-session 8th agent) | LIVE-PASS |
| Persistence saves | `CrewDataSaved exp=0 matured=0 mem=0 bytes=9` ×3 (honest empty-blob saves; restore path UNIT-PASS P28) | LIVE (mechanism), values honest |

## 4. Test matrix (34 items — master-prompt §40)

Statuses: ✔ = live-observed; ○ = unit-proven (dev suite); · = manual procedure documented (LIVE_VALIDATION.md M-*), not live-observed.

| # | Item | Status | Basis |
|---|---|---|---|
| 1 | Task lifecycle create→queue→run→complete | ✔+○ | live chains #121–#182 (P36–P39); f01 |
| 2 | Duplicate-execution protection (sticky ledger) | ✔+○ | live ClaimAccepted/OwnershipReleased chains; f04 |
| 3 | Scheduler grants/leases/preemption | ○ | f03; live grants in every session |
| 4 | Recovery retry/backoff/cancel + stuck | ✔+○ | live `Recovery applied Retry`/`Cancel`; f02 |
| 5 | ACTION_STALLED "nothing happens" diagnostic | ○ | P39 +7 asserts; live never fired (no stalls) |
| 6 | Claims deny-by-default authority | ✔+○ | live host-only execution; f05+MP suite |
| 7 | Authority-flip monitor + volatile clear | ○ | P26 IL + unit; live flip not induced (M-MP1) |
| 8 | World snapshot freshness fail-safe | ✔+○ | live freshness fresh; stale-gate unit tests |
| 9 | Capability 13-gate validation ladder | ✔+○ | live dispatches; f06 |
| 10 | Executor dispatch→record | ✔+○ | live `ExecutorResult SUCCESS`; f07 |
| 11 | Emergency director severity ladder | ✔+○ | live CoolantCritical chains; f08 |
| 12 | Emergency no-progress suppression | ✔+○ | live EmergencySuppressed (P39); S27 |
| 13 | Crew agents 1:1 ids | ✔+○ | live AgentCreated/Personality 1:1 (P54); f09 |
| 14 | Personalities derived+reconciled | ✔+○ | live PersonalityCreated/Reconciled; f10 |
| 15 | Experience accrual | ○ | f11; live honest zero (no taskObs yet) |
| 16 | Memory rings bounded | ○ | f12 |
| 17 | Persistence insert-only restore | ○ | P28; live-save mechanism seen |
| 18 | Navigation recovery authoring | ✔+○ | live REMOVE/ADD_COURSE_GOAL chains #177 |
| 19 | Mission detection/reporting | ✔+○ | live MissionOpened + /capbotmission (P52) |
| 20 | Mission lifecycle FSM 17 states | ✔+○ | live tracked=3 transitions=6; ML suite |
| 21 | Mission ONE return decision | ○ | MissionReturnPolicy verbatim port + tests |
| 22 | Economy report-only | ○ | f15 |
| 23 | Combat report-only | ○ | f16 |
| 24 | Captain deliberation | ✔+○ | live CaptainIntentOpened CREWGATHER |
| 25 | Planning premise-drift | ○ | P22 suite |
| 26 | Mission work authoring | ✔+○ | P23 suite; authoring channel shared w/ P18 |
| 27 | Adjustment observer | ○ | P24 suite; live counters |
| 28 | Adaptive trait maturation | ○ | P25 suite (needs long session live — M-CR2) |
| 29 | Ollama advisor recommend-only | ✔+○ | live OllamaAdvice; IL purity proof |
| 30 | Crew advisor (Qwen) recommend-only | ✔+○ | live CrewAdvice qwen3:latest; IL purity proof |
| 31 | MP hardening pre-gate | ○ | P26 IL + unit; M-MP1 manual |
| 32 | Compat/quarantine/Safe Mode chain | ○+· | CE/QE/SM/SY suites; live boot wiring lines |
| 33 | Settings audit honesty | ✔+○ | /capbotsettings on-screen (10/15) + SA drift suite |
| 34 | Status diagnostics shape | ✔+○ | /capbotstatus full+focused sections live OCR'd |

## 5. Known manual surface (documented, not blocking)

M-C1 divergent move-order authoring, M-C2 emergency SET_CAPTAIN_TARGET,
M-CR2 long-session maturation soak, M-MP1 2-client multiplayer,
M-U1 settings-menu layout, the 5 rows of /capbotsettings above the chat
scrollback window (pinned by SA tests instead), and the P50-era
DisabledUntilCompatibilityTest boot-gate completeness note (§7 wiring
recorded; full A/B lab automation remains future work).

## 6. Release disposition (§42–48)

- **Release blockers: NONE.** All §42 categories re-verified this
  session (reproducible build byte-identical; tests ×3; live evidence
  current for the shipped DLL).
- **Publicly published: NO. Workshop: NO.** Local release package only
  (§47): `Release\Alpha-1.2.2-expansion\` (8 files; CapBot.dll SHA-256
  parity-verified; intentionally not committed — `[Rr]elease/` is
  gitignored).
- Next step after this record: §48 — STOP.