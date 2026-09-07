# Changelog

All notable changes to CapBot are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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