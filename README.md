# CapBot

A PULSAR: Lost Colony mod that adds a bot Captain — and makes **every crew bot smart and autonomous**.

**Author:** otter9678-arch
**Current version:** Alpha 1.2.2 (expansion line P1–P55; verification record in `docs/VALIDATION_REPORT.md`)

## Credits

- **[pokegustavo](https://github.com/pokegustavor)** — original CapBot mod and its core concept; also author of Better AI, Quality Improver, and Exotic Components (compatibility targets)
- **PULSAR-Modders team** — [Pulsar Mod Loader (PML)](https://github.com/PULSAR-Modders/pulsar-mod-loader), the modding framework this mod runs on
- **Mest / TheRealMesteven** — the `.Talents` talent framework (modded talents integrate with it)
- **OnHyex** — TalentsModPerformanceImprovement (detected for settings-menu listing)
- **Leafy Games** — PULSAR: Lost Colony

## Features

### Captain Bot System
- Spawn with `/capbot` (or `/cap`) in chat — host only, in-game
- CapBot takes the captain's chair, announces custom captain orders, and runs the ship: course planning, warp-gate usage, shop runs (fuel/coolant/missiles), repair depots, blind-jump emergencies, and hull repairs

### Smart AI for ALL crew bots (every class)
- **Auto talent ranking** per class with priority orders — pilot flies faster, engineer repairs better, scientist researches smarter, weapons manning turrets better
- **Crew research**: ship-wide talent research, vanilla + modded talents (`.Talents` IDs 64+), cheapest-first, research materials deducted exactly like the vanilla flow
- **Smart item use**: grabs extinguishers for fires, medkits/health items when hurt — a gap-filler over the game's own AI, never fighting it
- **Movement watchdog**: bots never stay stuck on walls (pathing re-seek → push → detour → nav-graph teleport, the game's own unstick trick)
- **Adaptive learning**: each bot keeps persistent combat/economy/craft/nav skill stats that grow with experience (saved via PML); personality traits mature with outcomes (bounded trait maturation, Phase 25 — Diligence rises with completion-dominant records, Adaptability with adversity-dominant ones)

### Economy
- Sells duplicate and worse cargo components (both vs. installed keepers and pure surplus)
- Buys strictly better components — async-RPC-safe: never buys duplicates, respects cargo caps and a credit reserve
- Auto-installs better cargo components into open slots (works with Exotic Components' custom gear)

### Missions & campaigns
- **Mission auto-detection**: accepts/ends missions from any NPC in any sector (not just hardcoded hubs)
- **Objective worker**: picks up mission components (warp coils, shipments...), mission items, walks into mission volumes, reports to mission NPCs — using the same RPCs the vanilla talk/pickup buttons use, so objectives complete exactly as if a player did them
- **Stays on target**: the ship won't warp away mid-objective; it finishes pickup/report/enter objectives first
- **Campaign auto-start**: W.D. weapons demo, Grey Huntsman bounty, High Rollers buy-in — with attempt cooldowns
- Long-range comms: accepts pickup missions from comms hails

### Ship automation
- **Extractor operation** on abandoned ships (best-price salvage selection)
- **Component upgrades** (materials handled, vanilla caps respected) and weapon upgrades
- **Class locker gear**: bots equip phase pistols, guns, repair/fire guns, scanners, armor from their class locker

### Mod updater
- `/updateall` checks every loaded PML mod's `VersionLink` and updates outdated ones (staged as `.update`, applied atomically on next boot)
- Optional always-on check in the settings menu
- **Secure by construction** (Phase 30): HTTPS-only downloads from a bounded GitHub-family allowlist, PE-shape validation, SHA-256 verification when the version file publishes a digest, strict file-name defense — a hostile or tampered update is refused, never installed

### Crew layer (Phases 10–13, 25, 28)
- **Personalities**: every bot gets a deterministic personality (traits 0–100) derived from its identity; personalities MATURE through play (adaptive learning, Phase 25) and the matured values persist across sessions (Phase 28)
- **Experience**: per-bot outcome history (completed/cancelled/expired/vanished/failed) accrues experience points and levels
- **Memory**: per-bot bounded memory rings — remembered locations, task outcomes, crew events — with recall and time-based eviction
- All state is master-authoritative, deterministic, and (matured parts) save-persistent via PML

### Multiplayer hardening (Phase 26)
- Master/client authority flips are observed pre-gate every frame; on authority loss the volatile state (claims, leases, crew agents) is cleared deterministically while the duplicate-protection ledger survives
- Host migration: CapBot state is process-local by design — a migrated-to host rebuilds fresh from world observation with no desync (CapBot issues commands through verified vanilla channels; it holds no authoritative game state)

## Architecture (for modders)

Every gameplay action flows one way: **world snapshot → planning → task
creation → validation → claim → duplicate protection → execution →
completion/failure → recovery → memory/experience/learning**. The master
client owns all execution (deny-by-default authority wired to
`isMasterClient`); clients evaluate nothing. Ollama/Qwen LLM advisors are
**recommend-only** — their output is validated bounded text that a
deterministic system may consider, never execute. Status is inspectable
in-game via `/capbotstatus`. See `docs/OVERVIEW.md` for the full
phase-by-phase map.

## Commands

| Command | Description |
|---|---|
| `/capbot` (or `/cap`) | Spawns the Captain Bot (host only, in-game) |
| `/capbotstatus` | Full task-pipeline + crew-layer status report (host only); `/capbotstatus <section>` echoes one section |
| `/capbotmission` | Mission director + lifecycle FSM report (host only, read-only) |
| `/capbotsettings` | Settings audit table — CONFIGURED/STORED/RUNTIME/CONSUMER/EFFECTIVE per knob (host only, read-only) |
| `/capbotcompat` | Compatibility/conflict-engine report incl. Safe Mode state (host only, read-only) |
| `/capbotollama` | Ollama advisor diagnostics (host only, read-only) |
| `/updateall` | Checks and updates every loaded PML mod |

## Configuration

PML mod settings menu → **CapBot**:

| Setting | Default | Effect |
|---|---|---|
| CaptainBotEnabled | on | Master toggle for autonomy systems |
| SmartAIEnabled | on | Smart item use for all bots |
| MissionAutoDetectEnabled | on | Economy + campaign + mission work |
| AutoAssignCaptain | on | **not wired — /capbotsettings** (no consumer; honest DEAD knob) |
| AIReactionSpeed | 0.1s | **not wired — /capbotsettings** (legacy cadence stays hardcoded) |
| AIAccuracy | 85% | **not wired — /capbotsettings** (no consumer) |
| CombatEngageRange | 50 | **not wired — /capbotsettings** (no verified position read) |
| CombatDisengageHealth | 20% | **not wired — /capbotsettings** (legacy blind-jump flee uses hardcoded 0.2 hull floor) |
| MinCreditsReserve | 500 | **not wired — /capbotsettings** (legacy reserve stays hardcoded 2500) |
| ModUpdaterEnabled | off | Run update check every launch (LIVE) |
| OllamaAdvisorEnabled / QwenAdvisorEnabled | off | Recommend-only LLM advisors (LIVE when on) |
| OllamaModel / OllamaPort | qwen3:latest / 11434 | Advisor model + endpoint (LIVE) |
| VerboseLogging | off | CapBotLog level gate (LIVE) |

Every knob's honest LIVE/DEAD status is queryable in-game via `/capbotsettings`; the truth table is pinned by tests (`SettingsAuditTests`).

Settings persist instantly (saved on every change).

## Compatibility

Tested live against the current game build with:
- **Better AI** — complementary: it drives classes 1–4 station behavior, CapBot handles captain + management
- **Quality Improver** — no overlapping component logic
- **Exotic Components** — install path uses the game's own slot APIs (custom subtypes work)
- **ExpandedGalaxy** — detected and listed in the settings menu (no code-level guards; none were ever shipped — earlier claims of "boot-time crash guards" were incorrect and have been removed)
- **.Talents framework** — modded talents are ranked and researched by bots
- **TalentsModPerformanceImprovement** — detected and listed in the settings menu (no UI-helper replacement is shipped; an earlier claim of one was incorrect and has been removed)
- **MoreBots** — its class-0 array crash is guarded (the crashing prefix is removed at `/capbot` spawn and a safe replacement installed, dispatched via the compatibility manager)

## Installation

**Manual:** drop `CapBot.dll` into `PULSARLostColony\Mods\` (requires [PML](https://github.com/PULSAR-Modders/pulsar-mod-loader/releases))

In game (host): `/capbot`

## Building from source

The build is machine-agnostic — the game's `Managed` DLL folder is a
required parameter, never a hardcoded path (see `docs/BUILD.md`):

```
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 `
    -PulsarManaged "C:\Path\To\PULSAR_LostColony_Data\Managed"
```

- Requires MSBuild (VS BuildTools/Community) and .NET Framework 4.7.2
  targeting; NuGet packages are committed in `packages/`.
- The script validates the game DLLs, builds `CapBot.sln` (Release),
  and smoke-checks the artifact (PE image, no build-machine paths
  embedded). On success it prints `BUILD OK`.
- The dev-side test suite (`tests/run_tests.ps1`, pure C#, no game
  references) runs the 40-suite regression independently of the game.
- CI: `ci/pipeline.yml` builds on any Windows runner with the game DLLs
  staged via the `PULSAR_MANAGED` variable.

## License

Proprietary — maintained by otter9678-arch. Original mod by pokegustavo (credited per the original project's terms).