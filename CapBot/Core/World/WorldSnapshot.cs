using System;
using System.Collections.Generic;

namespace CapBot.Core.World
{
    // ---- Phase 6: game/world state — read-oriented snapshots ----------------
    //
    // Immutable, bounded, value-style snapshots of authoritative game state.
    // Pure C# (System* only): no UnityEngine, no PULSAR/PML types — the game
    // fills these in through the IWorldSource seam (PulsarWorldSource), and
    // every consumer (probe, future directors, validation, planning) reads
    // them. Snapshot construction performs NO gameplay side effects: it never
    // mutates game objects, never executes tasks, never issues orders.
    //
    // Authority marking: every section carries a WorldAuthority value so
    // consumers can distinguish master-derived, synchronized, and locally
    // observed data without inspecting PULSAR internals (research §9).
    //
    // All collection sizes are bounded at construction (never grows with the
    // scene); names are truncated; nothing holds game-object references.
    // Per-bot state lives in per-section snapshot values, never in shared
    // static floats/flags.

    // How a snapshot section's values should be trusted (research §9):
    // MasterDerived — the host computes this server-side; clients must not
    //                 act on it authoritatively.
    // Synchronized — replicated to all peers by the game (safe to read, still
    //                not something a client may author).
    // LocallyObserved — this process's own view of a registry; may lag or
    //                   differ between peers.
    public enum WorldAuthority
    {
        Unknown = 0,
        LocallyObserved = 1,
        Synchronized = 2,
        MasterDerived = 3,
    }

    // One crew member (captain, bots, humans). Health/alive fields are
    // nullable-by-sentinel: NaN fraction / alive=false only when the game
    // actually reports it — unknown stays unknown.
    public sealed class CrewMemberSnapshot
    {
        public const int MaxNameLen = 32;

        public readonly int PlayerId;
        public readonly string Name;
        public readonly bool IsBot;
        public readonly int ClassId;
        public readonly int TeamId;
        public readonly bool AliveKnown;
        public readonly bool Alive;
        public readonly float HealthFraction;      // NaN = unknown
        public readonly string CurrentTLIName;     // interior/room location, may be null
        public readonly bool IsCaptain;
        public readonly int AiPriorityTypeData;    // AIPriority.TypeData of the bot's current main priority, -1 = unknown/not a bot

        public CrewMemberSnapshot(
            int playerId, string name, bool isBot, int classId, int teamId,
            bool aliveKnown, bool alive, float healthFraction,
            string currentTLIName, bool isCaptain, int aiPriorityTypeData)
        {
            PlayerId = playerId;
            Name = Truncate(name, MaxNameLen);
            IsBot = isBot;
            ClassId = classId;
            TeamId = teamId;
            AliveKnown = aliveKnown;
            Alive = alive;
            HealthFraction = healthFraction;
            CurrentTLIName = Truncate(currentTLIName, MaxNameLen);
            IsCaptain = isCaptain;
            AiPriorityTypeData = aiPriorityTypeData;
        }

        internal static string Truncate(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }

    // One ship in the encounter (player ship, hostiles, neutrals, depots,
    // stations, beacons all appear in AllShips).
    public sealed class ShipSnapshot
    {
        public const int MaxNameLen = 40;

        public readonly int ShipId;
        public readonly string Name;
        public readonly bool IsPlayerShip;
        public readonly int TeamId;
        public readonly bool HostileToPlayerShip;  // in PlayerShip.HostileShips (QualityImprover-safe: read the list, never call hostility logic)
        public readonly float HullFraction;        // NaN = unknown
        public readonly float ShieldFraction;      // NaN = unknown
        public readonly bool InWarp;
        public readonly int WarpChargeStage;       // raw EWarpChargeStage value, -1 = unknown
        public readonly int WarpTargetSectorId;    // -1 = none
        public readonly int AlertLevel;            // -1 = unknown
        public readonly float CombatLevel;         // NaN = unknown

        // ---- Phase 17 additions (combat director inputs) -----------------------
        // Data-only combat-activity flag: did the game itself mark this ship as
        // recently damaged (the shipped Patch.cs:242 "took damage recently"
        // window — Time.time - LastTookDamageTime() < 10f, compile-proven).
        // false covers both "not recently damaged" and "capture unknown" —
        // a report-only consumer treats false as never-trigger (fail-safe).
        public readonly bool TookDamageRecently;

        public ShipSnapshot(
            int shipId, string name, bool isPlayerShip, int teamId,
            bool hostileToPlayerShip, float hullFraction, float shieldFraction,
            bool inWarp, int warpChargeStage, int warpTargetSectorId,
            int alertLevel, float combatLevel)
        {
            ShipId = shipId;
            Name = CrewMemberSnapshot.Truncate(name, MaxNameLen);
            IsPlayerShip = isPlayerShip;
            TeamId = teamId;
            HostileToPlayerShip = hostileToPlayerShip;
            HullFraction = hullFraction;
            ShieldFraction = shieldFraction;
            InWarp = inWarp;
            WarpChargeStage = warpChargeStage;
            WarpTargetSectorId = warpTargetSectorId;
            AlertLevel = alertLevel;
            CombatLevel = combatLevel;
            TookDamageRecently = false;
        }

        // Phase 17 constructor: adds the combat-activity flag without touching
        // any existing caller (P9 additive-ctor pattern).
        public ShipSnapshot(
            int shipId, string name, bool isPlayerShip, int teamId,
            bool hostileToPlayerShip, float hullFraction, float shieldFraction,
            bool inWarp, int warpChargeStage, int warpTargetSectorId,
            int alertLevel, float combatLevel,
            bool tookDamageRecently)
            : this(shipId, name, isPlayerShip, teamId,
                   hostileToPlayerShip, hullFraction, shieldFraction,
                   inWarp, warpChargeStage, warpTargetSectorId,
                   alertLevel, combatLevel)
        {
            TookDamageRecently = tookDamageRecently;
        }
    }

    // One active mission with bounded objective summary. Objective *types*
    // are not readable on PLMissionObjective instances (no ObjType member);
    // completion state + text are what the game actually exposes.
    public sealed class MissionSnapshot
    {
        public const int MaxObjectiveTextLen = 120;

        public readonly int MissionTypeId;
        public readonly bool Ended;
        public readonly bool Abandoned;
        public readonly int TotalObjectives;
        public readonly int CompletedObjectives;
        public readonly string FirstIncompleteObjectiveText; // may be null

        public MissionSnapshot(
            int missionTypeId, bool ended, bool abandoned,
            int totalObjectives, int completedObjectives,
            string firstIncompleteObjectiveText)
        {
            MissionTypeId = missionTypeId;
            Ended = ended;
            Abandoned = abandoned;
            TotalObjectives = totalObjectives;
            CompletedObjectives = completedObjectives;
            FirstIncompleteObjectiveText = CrewMemberSnapshot.Truncate(firstIncompleteObjectiveText, MaxObjectiveTextLen);
        }
    }

    // Threat picture. Deliberately assumption-free about hostility semantics
    // (Quality Improver can replace ShouldBeHostileToShip): the authoritative
    // hostile set is the ship's own HostileShips id list; team counts are raw
    // observations, not hostility claims. Target/combat levels are INFERRED
    // semantics (research §6.6) and are provided as data only.
    public sealed class ThreatSnapshot
    {
        public const int MaxHostileIds = 16;

        public readonly IReadOnlyList<int> KnownHostileShipIds;
        public readonly int Team0ShipCount;
        public readonly int Team1ShipCount;
        public readonly int Team2ShipCount;
        public readonly int PlayerTargetShipId;         // -1 = none
        public readonly float PlayerTargetCombatLevel;  // NaN = unknown
        public readonly float OurCombatLevel;           // NaN = unknown

        // ---- Phase 17 additions (combat director inputs) -----------------------
        // Boarder count on the player ship (PLShipInfoBase.InvadersOnboard —
        // DLL reflection-verified: public property returning System.Int32;
        // game-owned data read, never hostility logic). -1 = unknown; -1 never
        // triggers any report (unknown sentinels never trigger).
        public readonly int InvadersOnboardCount;       // -1 = unknown

        public ThreatSnapshot(
            IReadOnlyList<int> knownHostileShipIds,
            int team0ShipCount, int team1ShipCount, int team2ShipCount,
            int playerTargetShipId, float playerTargetCombatLevel, float ourCombatLevel)
        {
            List<int> ids = new List<int>();
            if (knownHostileShipIds != null)
            {
                foreach (int id in knownHostileShipIds)
                {
                    if (ids.Count >= MaxHostileIds) break;
                    ids.Add(id);
                }
            }
            KnownHostileShipIds = ids;
            Team0ShipCount = team0ShipCount;
            Team1ShipCount = team1ShipCount;
            Team2ShipCount = team2ShipCount;
            PlayerTargetShipId = playerTargetShipId;
            PlayerTargetCombatLevel = playerTargetCombatLevel;
            OurCombatLevel = ourCombatLevel;
            InvadersOnboardCount = -1;
        }

        // Phase 17 constructor: adds the boarder count without touching any
        // existing caller (P9 additive-ctor pattern).
        public ThreatSnapshot(
            IReadOnlyList<int> knownHostileShipIds,
            int team0ShipCount, int team1ShipCount, int team2ShipCount,
            int playerTargetShipId, float playerTargetCombatLevel, float ourCombatLevel,
            int invadersOnboardCount)
            : this(knownHostileShipIds,
                   team0ShipCount, team1ShipCount, team2ShipCount,
                   playerTargetShipId, playerTargetCombatLevel, ourCombatLevel)
        {
            InvadersOnboardCount = invadersOnboardCount < 0 ? -1 : invadersOnboardCount;
        }
    }

    // Sector/course/warp picture for the player ship + vanilla bot-controller
    // movement signals (stuck detection inputs, read straight from the
    // captain bot's PLBotController when present).
    public sealed class NavigationSnapshot
    {
        public const int MaxCourseGoals = 8;
        public const int MaxSectorNameLen = 40;

        public readonly int CurrentSectorId;        // -1 = unknown
        public readonly string CurrentSectorName;   // may be null
        public readonly int SectorVisualIndication; // raw ESectorVisualIndication value, -1 = unknown
        public readonly bool InWarp;
        public readonly int WarpTargetSectorId;     // -1 = none
        public readonly IReadOnlyList<int> CourseGoals; // ship course goal sector ids (bounded)
        public readonly bool HasBotNavigationMetrics;
        public readonly float DistMovedInLast5s;        // NaN = unknown
        public readonly float SuccessRateSeekingTarget; // NaN = unknown
        public readonly float TimeSeekingTargetSec;     // NaN = unknown
        public readonly bool PathRequestInProgress;

        public NavigationSnapshot(
            int currentSectorId, string currentSectorName, int sectorVisualIndication,
            bool inWarp, int warpTargetSectorId, IReadOnlyList<int> courseGoals,
            bool hasBotNavigationMetrics, float distMovedInLast5s,
            float successRateSeekingTarget, float timeSeekingTargetSec,
            bool pathRequestInProgress)
        {
            List<int> goals = new List<int>();
            if (courseGoals != null)
            {
                foreach (int g in courseGoals)
                {
                    if (goals.Count >= MaxCourseGoals) break;
                    goals.Add(g);
                }
            }
            CourseGoals = goals;
            CurrentSectorId = currentSectorId;
            CurrentSectorName = CrewMemberSnapshot.Truncate(currentSectorName, MaxSectorNameLen);
            SectorVisualIndication = sectorVisualIndication;
            InWarp = inWarp;
            WarpTargetSectorId = warpTargetSectorId;
            HasBotNavigationMetrics = hasBotNavigationMetrics;
            DistMovedInLast5s = distMovedInLast5s;
            SuccessRateSeekingTarget = successRateSeekingTarget;
            TimeSeekingTargetSec = timeSeekingTargetSec;
            PathRequestInProgress = pathRequestInProgress;
        }
    }

    // Crew-level resources and economy observations.
    public sealed class ResourceSnapshot
    {
        public const int MaxResearchEntries = 8;

        public readonly int Credits;                       // -1 = unknown
        public readonly IReadOnlyList<int> ResearchMaterials; // per-tier counts (bounded)
        public readonly int UpgradeMaterials;              // -1 = unknown
        public readonly int FuelCapsules;                  // -1 = unknown (player ship)
        public readonly float CoolantLevelPercent;         // NaN = unknown (player ship)

        // ---- Phase 16 additions (economy director inputs) ---------------------
        // Effective unit prices in credits (-1 = unknown). Captured from
        // PLServer.GetFuelBasePrice()/GetCoolantBasePrice() — both compile-proven
        // in shipped Patch.cs HandleShop (lines 2220/2234). Fail-safe: any
        // capture fault leaves -1 and the economy director's affordability
        // rules stay silent (unknown sentinels never trigger).
        public readonly int FuelBasePrice;                 // -1 = unknown
        public readonly int CoolantBasePrice;              // -1 = unknown

        public ResourceSnapshot(
            int credits, IReadOnlyList<int> researchMaterials,
            int upgradeMaterials, int fuelCapsules, float coolantLevelPercent)
        {
            List<int> research = new List<int>();
            if (researchMaterials != null)
            {
                foreach (int r in researchMaterials)
                {
                    if (research.Count >= MaxResearchEntries) break;
                    research.Add(r);
                }
            }
            ResearchMaterials = research;
            Credits = credits;
            UpgradeMaterials = upgradeMaterials;
            FuelCapsules = fuelCapsules;
            CoolantLevelPercent = coolantLevelPercent;
            FuelBasePrice = -1;
            CoolantBasePrice = -1;
        }

        // Phase 16 constructor: adds the two economy unit prices without
        // touching any existing caller (P9 additive-ctor pattern).
        public ResourceSnapshot(
            int credits, IReadOnlyList<int> researchMaterials,
            int upgradeMaterials, int fuelCapsules, float coolantLevelPercent,
            int fuelBasePrice, int coolantBasePrice)
            : this(credits, researchMaterials, upgradeMaterials, fuelCapsules, coolantLevelPercent)
        {
            FuelBasePrice = fuelBasePrice < 0 ? -1 : fuelBasePrice;
            CoolantBasePrice = coolantBasePrice < 0 ? -1 : coolantBasePrice;
        }
    }

    // One notable world object (repair depot, warp station, beacon). No
    // static registries exist for these in the game; the source reads the
    // ship's own cached lists (verified via shipped Patch.cs usage).
    public sealed class WorldObjectSnapshot
    {
        public const int MaxNameLen = 40;

        public readonly string Kind;    // "REPAIR_DEPOT" / "WARP_STATION" / "BEACON" (static vocabulary, never parsed)
        public readonly int ObjectId;
        public readonly string Name;
        public readonly int SectorId;   // -1 = unknown
        public readonly int Price;      // -1 = not applicable/unknown

        public WorldObjectSnapshot(string kind, int objectId, string name, int sectorId, int price)
        {
            Kind = kind;
            ObjectId = objectId;
            Name = CrewMemberSnapshot.Truncate(name, MaxNameLen);
            SectorId = sectorId;
            Price = price;
        }
    }

    // Root snapshot: one point-in-time, immutable, fully bounded view.
    public sealed class WorldSnapshot
    {
        public const int MaxShips = 24;
        public const int MaxCrew = 16;
        public const int MaxMissions = 16;
        public const int MaxWorldObjects = 16;

        public readonly int SnapshotTimeMs;     // -1 = never captured (Empty)
        public readonly bool GameStarted;
        public readonly bool IsHost;
        public readonly int CurrentHubId;       // -1 = unknown
        public readonly WorldAuthority SessionAuthority;
        public readonly IReadOnlyList<ShipSnapshot> Ships;       // player ship FIRST (ordering contract — consumer-visible data can be bounded)
        public readonly IReadOnlyList<CrewMemberSnapshot> Crew;  // captain FIRST (same contract)
        public readonly IReadOnlyList<MissionSnapshot> Missions;
        public readonly ThreatSnapshot Threats;
        public readonly NavigationSnapshot Navigation;
        public readonly ResourceSnapshot Resources;
        public readonly IReadOnlyList<WorldObjectSnapshot> WorldObjects;
        public readonly WorldAuthority ThreatAuthority;
        public readonly WorldAuthority NavigationAuthority;
        public readonly WorldAuthority ResourceAuthority;
        public readonly WorldAuthority WorldObjectsAuthority;

        // ---- Phase 9 additions (emergency detection inputs) -------------------
        // -1 / NaN = unknown (never triggers a rule — detectors fail safe on
        // unknown data). Carried as plain data; the P6 contract (bounded,
        // immutable, no game-object refs) is unchanged.
        public readonly int PlayerShipFireCount;               // PLShipInfo.CountNonNullFires(), -1 = unknown
        public readonly float PlayerShipReactorTempFraction;   // ReactorTempCurrent/ReactorTempMax, NaN = unknown

        private static readonly WorldSnapshot s_Empty = new WorldSnapshot();
        public static WorldSnapshot Empty { get { return s_Empty; } }
        public bool IsNeverCaptured { get { return SnapshotTimeMs < 0; } }

        private WorldSnapshot()
        {
            SnapshotTimeMs = -1;
            CurrentHubId = -1;
            SessionAuthority = WorldAuthority.Unknown;
            Ships = new ShipSnapshot[0];
            Crew = new CrewMemberSnapshot[0];
            Missions = new MissionSnapshot[0];
            Threats = new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN);
            Navigation = new NavigationSnapshot(-1, null, -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false);
            Resources = new ResourceSnapshot(-1, null, -1, -1, float.NaN);
            WorldObjects = new WorldObjectSnapshot[0];
            ThreatAuthority = WorldAuthority.Unknown;
            NavigationAuthority = WorldAuthority.Unknown;
            ResourceAuthority = WorldAuthority.Unknown;
            WorldObjectsAuthority = WorldAuthority.Unknown;
            PlayerShipFireCount = -1;
            PlayerShipReactorTempFraction = float.NaN;
        }

        // Original constructor — preserved verbatim (all Phase 6–8 callers and
        // tests keep compiling); Phase 9 fields default to "unknown".
        public WorldSnapshot(
            int snapshotTimeMs, bool gameStarted, bool isHost, int currentHubId,
            WorldAuthority sessionAuthority,
            IReadOnlyList<ShipSnapshot> ships,
            IReadOnlyList<CrewMemberSnapshot> crew,
            IReadOnlyList<MissionSnapshot> missions,
            ThreatSnapshot threats,
            NavigationSnapshot navigation,
            ResourceSnapshot resources,
            IReadOnlyList<WorldObjectSnapshot> worldObjects,
            WorldAuthority threatAuthority,
            WorldAuthority navigationAuthority,
            WorldAuthority resourceAuthority,
            WorldAuthority worldObjectsAuthority)
        {
            SnapshotTimeMs = snapshotTimeMs;
            GameStarted = gameStarted;
            IsHost = isHost;
            CurrentHubId = currentHubId;
            SessionAuthority = sessionAuthority;
            Ships = Bounded(ships, MaxShips);
            Crew = Bounded(crew, MaxCrew);
            Missions = Bounded(missions, MaxMissions);
            Threats = threats ?? new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN);
            Navigation = navigation ?? new NavigationSnapshot(-1, null, -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false);
            Resources = resources ?? new ResourceSnapshot(-1, null, -1, -1, float.NaN);
            WorldObjects = Bounded(worldObjects, MaxWorldObjects);
            ThreatAuthority = threatAuthority;
            NavigationAuthority = navigationAuthority;
            ResourceAuthority = resourceAuthority;
            WorldObjectsAuthority = worldObjectsAuthority;
            PlayerShipFireCount = -1;
            PlayerShipReactorTempFraction = float.NaN;
        }

        // Phase 9 constructor: adds the two emergency detection inputs (fire
        // count, reactor temp fraction) without touching any existing caller.
        public WorldSnapshot(
            int snapshotTimeMs, bool gameStarted, bool isHost, int currentHubId,
            WorldAuthority sessionAuthority,
            IReadOnlyList<ShipSnapshot> ships,
            IReadOnlyList<CrewMemberSnapshot> crew,
            IReadOnlyList<MissionSnapshot> missions,
            ThreatSnapshot threats,
            NavigationSnapshot navigation,
            ResourceSnapshot resources,
            IReadOnlyList<WorldObjectSnapshot> worldObjects,
            WorldAuthority threatAuthority,
            WorldAuthority navigationAuthority,
            WorldAuthority resourceAuthority,
            WorldAuthority worldObjectsAuthority,
            int playerShipFireCount,
            float playerShipReactorTempFraction)
            : this(snapshotTimeMs, gameStarted, isHost, currentHubId, sessionAuthority,
                   ships, crew, missions, threats, navigation, resources, worldObjects,
                   threatAuthority, navigationAuthority, resourceAuthority, worldObjectsAuthority)
        {
            PlayerShipFireCount = playerShipFireCount < 0 ? -1 : playerShipFireCount;
            PlayerShipReactorTempFraction = playerShipReactorTempFraction;
        }

        private static IReadOnlyList<T> Bounded<T>(IReadOnlyList<T> source, int max)
        {
            if (source == null) return new T[0];
            if (source.Count <= max) return source;
            List<T> bounded = new List<T>(max);
            for (int i = 0; i < max; i++) bounded.Add(source[i]);
            return bounded;
        }

        // Deterministic single-line summary for logging (no allocation-heavy
        // formatting beyond the string build itself; one line per refresh max).
        public string ToSummaryLine()
        {
            if (IsNeverCaptured) return "WORLD empty";
            return "WORLD t=" + SnapshotTimeMs
                + " host=" + (IsHost ? "1" : "0")
                + " started=" + (GameStarted ? "1" : "0")
                + " sector=" + Navigation.CurrentSectorId
                + " warp=" + (Navigation.InWarp ? "1" : "0")
                + " ships=" + Ships.Count
                + " crew=" + Crew.Count
                + " missions=" + Missions.Count
                + " hostiles=" + Threats.KnownHostileShipIds.Count
                + " credits=" + Resources.Credits
                + " objects=" + WorldObjects.Count;
        }
    }

    // A detected world transition (fired by WorldStateService outside its
    // lock). Kind is static vocabulary: "SECTOR_CHANGED", "WARP_STARTED",
    // "WARP_ENDED".
    public sealed class WorldTransition
    {
        public readonly string Kind;
        public readonly int FromSectorId;   // context; -1 = none
        public readonly int ToSectorId;     // context; -1 = none
        public readonly int TimeMs;

        public WorldTransition(string kind, int fromSectorId, int toSectorId, int timeMs)
        {
            Kind = kind;
            FromSectorId = fromSectorId;
            ToSectorId = toSectorId;
            TimeMs = timeMs;
        }

        public override string ToString()
        {
            return Kind + " from=" + FromSectorId + " to=" + ToSectorId;
        }
    }
}