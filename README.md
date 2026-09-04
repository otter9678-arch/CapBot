# CapBot

A PULSAR: Lost Colony mod that adds a bot Captain — and makes **every crew bot smart and autonomous**.

**Author:** otter9678-arch
**Current version:** Alpha 1.2.2 ([Download](https://github.com/otter9678-arch/CapBot/releases/latest))

## Credits

- **[pokegustavo](https://github.com/pokegustavor)** — original CapBot mod and its core concept; also author of Better AI, Quality Improver, and Exotic Components (compatibility targets)
- **PULSAR-Modders team** — [Pulsar Mod Loader (PML)](https://github.com/PULSAR-Modders/pulsar-mod-loader), the modding framework this mod runs on
- **Mest / TheRealMesteven** — the `.Talents` talent framework (modded talents integrate with it)
- **OnHyex** — TalentsModPerformanceImprovement (compatibility guards included)
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
- **Adaptive learning**: each bot keeps persistent combat/economy/craft/nav skill stats that grow with experience (saved via PML)

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
- `/updateall` checks every loaded PML mod's `VersionLink` and updates outdated ones (staged as `.update`, applied on next boot)
- Optional always-on check in the settings menu

## Commands

| Command | Description |
|---|---|
| `/capbot` (or `/cap`) | Spawns the Captain Bot (host only, in-game) |
| `/updateall` | Checks and updates every loaded PML mod |

## Configuration

PML mod settings menu → **CapBot**:

| Setting | Default | Effect |
|---|---|---|
| CaptainBotEnabled | on | Master toggle for autonomy systems |
| SmartAIEnabled | on | Smart item use for all bots |
| MissionAutoDetectEnabled | on | Economy + campaign + mission work |
| AutoAssignCaptain | on | Captain assignment behavior |
| AIReactionSpeed | 0.1s | Bot reaction cadence |
| AIAccuracy | 85% | Combat accuracy |
| CombatEngageRange | 50 | Engagement distance |
| CombatDisengageHealth | 20% | Flee at hull threshold |
| MinCreditsReserve | 500 | Credits kept in reserve |
| ModUpdaterEnabled | off | Run update check every launch |

Settings persist instantly (saved on every change).

## Compatibility

Tested live against the current game build with:
- **Better AI** — complementary: it drives classes 1–4 station behavior, CapBot handles captain + management
- **Quality Improver** — no overlapping component logic
- **Exotic Components** — install path uses the game's own slot APIs (custom subtypes work)
- **ExpandedGalaxy** — plus boot-time crash guards for its starter-ship postfixes
- **.Talents framework** — modded talents are ranked and researched by bots
- **TalentsModPerformanceImprovement** — its unguarded UI helper is replaced with a safe version
- **MoreBots** — its class-0 array crash is guarded (skip prefix for captain bots)

## Installation

**Manual:** drop `CapBot.dll` into `PULSARLostColony\Mods\` (requires [PML](https://github.com/PULSAR-Modders/pulsar-mod-loader/releases))

In game (host): `/capbot`

## Building from source

```
dotnet build CapBot\CapBot.csproj -c Release
```

References resolve against the Steam library path in the csproj (`C:\SteamLibrary\steamapps\common\PULSARLostColony\PULSAR_LostColony_Data\Managed`). The PostBuild step copies the DLL into the game's `Mods\` folder.

## License

Proprietary — maintained by otter9678-arch. Original mod by pokegustavo (credited per the original project's terms).