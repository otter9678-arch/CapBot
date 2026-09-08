using System;
using System.Collections.Generic;

namespace CapBot.Core.World
{
    // ---- Phase 6: game-facing world reader ----------------------------------
    //
    // Implements IWorldSource against verified PULSAR registries ONLY
    // (PULSAR_GAMEAI_RESEARCH.md §8/§12, CAPBOT_AUDIT.md whitelist, plus
    // members compile-proven by shipped Patch.cs usage). Zero
    // FindObjectsOfType, zero scene scans: reads AllPlayers / AllShips /
    // AllCombatTargets-equivalent encounter state / AllMissions / the player
    // ship's own cached flight-AI lists, at the ~1 Hz refresh the service
    // throttles to.
    //
    // Read-only by construction: nothing here mutates game state, calls
    // hostility logic (Quality Improver replaces ShouldBeHostileToShip — we
    // read the HostileShips list instead), executes tasks, or issues orders.
    //
    // Non-throwing: each section builder catches its own failures and yields
    // a null section (rendered as "unknown" by the snapshot); partial errors
    // are counted (PartialErrorCount/LastPartialError) — never silent, never
    // fatal. Capture itself is wrapped by WorldStateService as a last resort.
    //
    // Null-safe: every registry/element access is null-checked; missing
    // objects become sentinel values (NaN/-1/null = unknown), never invented
    // state. No gameplay assumptions: hostiles come from the game's own list.
    public sealed class PulsarWorldSource : IWorldSource
    {
        private long m_PartialErrors;
        private string m_LastPartialError;

        // Diagnostics: number of section builds that failed since construction.
        public long PartialErrorCount { get { return System.Threading.Volatile.Read(ref m_PartialErrors); } }
        public string LastPartialError { get { return System.Threading.Volatile.Read(ref m_LastPartialError); } }

        public WorldSnapshot Capture(WorldSnapshot previous, int nowMs)
        {
            // Section containers; null section => "unknown" defaults below.
            bool gameStarted = false;
            bool isHost = false;
            int hubId = -1;
            List<ShipSnapshot> ships = null;
            List<CrewMemberSnapshot> crew = null;
            List<MissionSnapshot> missions = null;
            ThreatSnapshot threats = null;
            NavigationSnapshot navigation = null;
            ResourceSnapshot resources = null;
            List<WorldObjectSnapshot> worldObjects = null;

            // The two singletons nearly every section needs.
            PLServer server = null;
            PLEncounterManager encounter = null;
            try { server = PLServer.Instance; } catch (Exception ex) { RecordPartial("PLServer.Instance", ex); }
            try { encounter = PLEncounterManager.Instance; } catch (Exception ex) { RecordPartial("PLEncounterManager.Instance", ex); }

            // ---- session (game started / host / hub) ---------------------------
            // GameHasStarted is a server-side flag (master-derived); isMasterClient
            // is this peer's own role (locally observed). Authority below reflects
            // the weaker of the two so consumers never over-trust the section.
            try
            {
                if (server != null) gameStarted = server.GameHasStarted;
                isHost = PhotonNetwork.isMasterClient;
            }
            catch (Exception ex) { RecordPartial("session", ex); }

            PLShipInfoBase playerShip = null;
            try { if (encounter != null) playerShip = encounter.PlayerShip; } catch (Exception ex) { RecordPartial("PlayerShip", ex); }

            // ---- ships (AllShips registry, player ship first) -------------------
            int team0 = 0, team1 = 0, team2 = 0;
            try
            {
                if (encounter != null && encounter.AllShips != null)
                {
                    ships = new List<ShipSnapshot>(8);
                    if (playerShip != null) ships.Add(BuildShip(playerShip, playerShip, true));
                    int added = ships.Count;
                    foreach (KeyValuePair<int, PLShipInfoBase> kv in encounter.AllShips)
                    {
                        if (added >= WorldSnapshot.MaxShips) break;
                        PLShipInfoBase ship = kv.Value;
                        if (ship == null || ship == playerShip) continue;
                        if (playerShip != null && ship.ShipID == playerShip.ShipID) continue;
                        ships.Add(BuildShip(ship, playerShip, false));
                        added++;
                    }
                }
            }
            catch (Exception ex) { RecordPartial("ships", ex); ships = null; }

            // Team tallies ride the same pass data (threat section consumes them).
            try
            {
                if (encounter != null && encounter.AllShips != null)
                {
                    foreach (KeyValuePair<int, PLShipInfoBase> kv in encounter.AllShips)
                    {
                        PLShipInfoBase ship = kv.Value;
                        if (ship == null) continue;
                        int t = ship.TeamID;
                        if (t == 0) team0++;
                        else if (t == 1) team1++;
                        else if (t == 2) team2++;
                    }
                }
            }
            catch (Exception ex) { RecordPartial("team tallies", ex); }

            // ---- crew (AllPlayers, captain first) --------------------------------
            try
            {
                if (server != null && server.AllPlayers != null)
                {
                    crew = new List<CrewMemberSnapshot>(8);
                    PLPlayer captain = null;
                    foreach (PLPlayer player in server.AllPlayers)
                    {
                        if (player == null) continue;
                        if (player.IsBot && player.TeamID == 0 && player.GetClassID() == 0) { captain = player; break; }
                    }
                    if (captain != null && crew.Count < WorldSnapshot.MaxCrew) crew.Add(BuildCrew(captain, true));
                    foreach (PLPlayer player in server.AllPlayers)
                    {
                        if (crew.Count >= WorldSnapshot.MaxCrew) break;
                        if (player == null || player == captain) continue;
                        crew.Add(BuildCrew(player, false));
                    }
                }
            }
            catch (Exception ex) { RecordPartial("crew", ex); crew = null; }

            // ---- missions (AllMissions) ------------------------------------------
            try
            {
                if (server != null && server.AllMissions != null)
                {
                    missions = new List<MissionSnapshot>(4);
                    foreach (PLMissionBase mission in server.AllMissions)
                    {
                        if (missions.Count >= WorldSnapshot.MaxMissions) break;
                        if (mission == null) continue;
                        int total = 0, completed = 0;
                        string firstIncomplete = null;
                        if (mission.Objectives != null)
                        {
                            foreach (PLMissionObjective objective in mission.Objectives)
                            {
                                if (objective == null) continue;
                                total++;
                                if (objective.IsCompleted) completed++;
                                else if (firstIncomplete == null) firstIncomplete = objective.ObjectiveText;
                            }
                        }
                        missions.Add(new MissionSnapshot(
                            mission.MissionTypeID, mission.Ended, mission.Abandoned,
                            total, completed, firstIncomplete));
                    }
                }
            }
            catch (Exception ex) { RecordPartial("missions", ex); missions = null; }

            // ---- threats (authoritative HostileShips list + target observation) --
            try
            {
                List<int> hostileIds = new List<int>(4);
                if (playerShip != null && playerShip.HostileShips != null)
                {
                    foreach (int id in playerShip.HostileShips)
                    {
                        if (hostileIds.Count >= ThreatSnapshot.MaxHostileIds) break;
                        hostileIds.Add(id);
                    }
                }
                int targetShipId = -1;
                float targetCombatLevel = float.NaN;
                float ourCombatLevel = float.NaN;
                if (playerShip != null)
                {
                    try { ourCombatLevel = playerShip.GetCombatLevel(); } catch (Exception ex) { RecordPartial("our combat level", ex); }
                    PLShipInfoBase target = playerShip.TargetShip;
                    if (target != null)
                    {
                        targetShipId = target.ShipID;
                        try { targetCombatLevel = target.GetCombatLevel(); } catch (Exception ex) { RecordPartial("target combat level", ex); }
                    }
                }

                // Phase 17: boarder count on the player ship (game-owned data;
                // PLShipInfoBase.InvadersOnboard is a public System.Int32
                // property — DLL reflection-verified). Any fault leaves -1 and
                // the boarder rule stays silent.
                int invaders = -1;
                try { if (playerShip != null) invaders = playerShip.InvadersOnboard; }
                catch (Exception ex) { RecordPartial("invaders onboard", ex); }

                threats = new ThreatSnapshot(hostileIds, team0, team1, team2, targetShipId, targetCombatLevel, ourCombatLevel, invaders);
            }
            catch (Exception ex) { RecordPartial("threats", ex); threats = null; }

            // ---- navigation (sector / course goals / warp / bot movement signals) -
            try
            {
                int sectorId = -1;
                string sectorName = null;
                int visualIndication = -1;
                PLSectorInfo currentSector = null;
                try { currentSector = PLServer.GetCurrentSector(); } catch (Exception ex) { RecordPartial("GetCurrentSector", ex); }
                if (currentSector != null)
                {
                    sectorId = currentSector.ID;
                    sectorName = currentSector.Name;
                    visualIndication = (int)currentSector.VisualIndication;
                }

                List<int> courseGoals = new List<int>(4);
                if (server != null && server.m_ShipCourseGoals != null)
                {
                    foreach (int goal in server.m_ShipCourseGoals)
                    {
                        if (courseGoals.Count >= NavigationSnapshot.MaxCourseGoals) break;
                        courseGoals.Add(goal);
                    }
                }

                bool inWarp = false;
                int warpTarget = -1;
                if (playerShip != null)
                {
                    inWarp = playerShip.InWarp;
                    warpTarget = playerShip.WarpTargetID;
                }

                // Vanilla bot-controller movement metrics from the captain bot
                // (stuck-detection inputs; verified fields, read-only).
                bool hasNavMetrics = false;
                float distMoved = float.NaN, successRate = float.NaN, timeSeeking = float.NaN;
                bool pathRequest = false;
                PLPlayer captainBot = null;
                if (crew != null && crew.Count > 0 && crew[0].IsCaptain && server != null && server.AllPlayers != null)
                {
                    foreach (PLPlayer player in server.AllPlayers)
                    {
                        if (player != null && player.IsBot && player.TeamID == 0 && player.GetClassID() == 0) { captainBot = player; break; }
                    }
                }
                if (captainBot != null)
                {
                    PLBot bot = captainBot.MyBot;
                    if (bot != null)
                    {
                        PLBotController controller = bot.MyBotController;
                        if (controller != null)
                        {
                            hasNavMetrics = true;
                            distMoved = controller.DistMovedInLast5s;
                            successRate = controller.successRateSeekingTarget;
                            timeSeeking = controller.timeSeekingTarget;
                            pathRequest = controller.PathRequestInProgress;
                        }
                    }
                }

                navigation = new NavigationSnapshot(
                    sectorId, sectorName, visualIndication, inWarp, warpTarget, courseGoals,
                    hasNavMetrics, distMoved, successRate, timeSeeking, pathRequest);
            }
            catch (Exception ex) { RecordPartial("navigation", ex); navigation = null; }

            // ---- resources / economy ----------------------------------------------
            try
            {
                int credits = -1;
                if (server != null) credits = server.CurrentCrewCredits;

                List<int> research = null;
                int upgradeMats = -1;
                if (server != null)
                {
                    try
                    {
                        CodeStage.AntiCheat.ObscuredTypes.ObscuredInt[] researchArr = server.ResearchMaterials;
                        if (researchArr != null)
                        {
                            research = new List<int>(researchArr.Length);
                            for (int i = 0; i < researchArr.Length && i < ResourceSnapshot.MaxResearchEntries; i++)
                                research.Add(researchArr[i]);
                        }
                    }
                    catch (Exception ex) { RecordPartial("research materials", ex); research = null; }
                    try { upgradeMats = server.CurrentUpgradeMats; } catch (Exception ex) { RecordPartial("upgrade materials", ex); }
                }

                int fuel = -1;
                float coolant = float.NaN;
                if (playerShip != null)
                {
                    try { fuel = playerShip.NumberOfFuelCapsules; } catch (Exception ex) { RecordPartial("fuel capsules", ex); }
                    try { coolant = playerShip.ReactorCoolantLevelPercent; } catch (Exception ex) { RecordPartial("coolant level", ex); }
                }

                // ---- Phase 16 economy inputs (unit prices, fail-safe) -----
                // Effective fuel/coolant unit prices via the server price API
                // surface compile-proven in shipped Patch.cs HandleShop. Any
                // fault leaves -1 (unknown); the economy director's
                // affordability rules stay silent on unknown prices.
                int fuelPrice = -1;
                int coolantPrice = -1;
                if (server != null)
                {
                    try { fuelPrice = (int)server.GetFuelBasePrice(); } catch (Exception ex) { RecordPartial("fuel base price", ex); }
                    try { coolantPrice = (int)server.GetCoolantBasePrice(); } catch (Exception ex) { RecordPartial("coolant base price", ex); }
                }

                resources = new ResourceSnapshot(credits, research, upgradeMats, fuel, coolant, fuelPrice, coolantPrice);
            }
            catch (Exception ex) { RecordPartial("resources", ex); resources = null; }

            // ---- world objects (flight-AI caches + beacons from AllShips) ---------
            try
            {
                worldObjects = new List<WorldObjectSnapshot>(4);
                if (playerShip != null)
                {
                    // Chained member access only (no local of the flight-AI type):
                    // the declared type's base class lives in an unreferenced
                    // assembly, so binding a local would fail compilation — the
                    // same chained pattern shipped Patch.cs uses compiles fine.
                    if (playerShip.MyFlightAI != null)
                    {
                        try
                        {
                            var depots = playerShip.MyFlightAI.cachedRepairDepotList;
                            if (depots != null)
                            {
                                foreach (PLRepairDepot depot in depots)
                                {
                                    if (worldObjects.Count >= WorldSnapshot.MaxWorldObjects) break;
                                    if (depot == null) continue;
                                    worldObjects.Add(new WorldObjectSnapshot("REPAIR_DEPOT", -1, depot.name, -1, -1));
                                }
                            }
                        }
                        catch (Exception ex) { RecordPartial("repair depots", ex); }

                        try
                        {
                            var stations = playerShip.MyFlightAI.cachedWarpStationList;
                            if (stations != null)
                            {
                                foreach (PLWarpStation station in stations)
                                {
                                    if (worldObjects.Count >= WorldSnapshot.MaxWorldObjects) break;
                                    if (station == null) continue;
                                    int price = -1;
                                    try
                                    {
                                        PLSectorInfo sector = PLServer.GetCurrentSector();
                                        if (sector != null) price = station.GetPriceForSectorID(sector.ID);
                                    }
                                    catch (Exception ex) { RecordPartial("warp station price", ex); }
                                    worldObjects.Add(new WorldObjectSnapshot("WARP_STATION", -1, station.name, -1, price));
                                }
                            }
                        }
                        catch (Exception ex) { RecordPartial("warp stations", ex); }
                    }
                }
                if (encounter != null && encounter.AllShips != null)
                {
                    foreach (KeyValuePair<int, PLShipInfoBase> kv in encounter.AllShips)
                    {
                        if (worldObjects.Count >= WorldSnapshot.MaxWorldObjects) break;
                        PLShipInfoBase ship = kv.Value;
                        if (ship == null || ship.ShipTypeID != EShipType.E_BEACON) continue;
                        PLBeaconInfo beacon = ship as PLBeaconInfo;
                        if (beacon == null) continue;
                        worldObjects.Add(new WorldObjectSnapshot("BEACON", ship.ShipID, ship.ShipName, -1, -1));
                    }
                }
            }
            catch (Exception ex) { RecordPartial("world objects", ex); worldObjects = null; }

            // Null sections render as bounded "unknown" defaults; the snapshot
            // itself is always constructible.
            WorldAuthority sessionAuthority = isHost ? WorldAuthority.MasterDerived : WorldAuthority.LocallyObserved;

            // ---- Phase 9 emergency inputs (player ship only, fail-safe) -----
            // Fire count from the game's own verified counter; reactor temp
            // fraction from PLShipStats reactor properties. Any failure leaves
            // the field "unknown" (-1 / NaN) — the emergency detector treats
            // unknown data as no-emergency by contract.
            int fireCount = -1;
            float reactorTempFraction = float.NaN;
            if (playerShip != null)
            {
                try
                {
                    PLShipInfo fullShip = playerShip as PLShipInfo;
                    if (fullShip != null) fireCount = fullShip.CountNonNullFires();
                }
                catch (Exception ex) { RecordPartial("fire count", ex); }
                try
                {
                    PLShipStats stats = playerShip.MyStats;
                    if (stats != null && stats.ReactorTempMax > 0f)
                        reactorTempFraction = stats.ReactorTempCurrent / stats.ReactorTempMax;
                }
                catch (Exception ex) { RecordPartial("reactor temp", ex); }
            }

            return new WorldSnapshot(
                nowMs, gameStarted, isHost, hubId, sessionAuthority,
                ships,
                crew,
                missions,
                threats,
                navigation,
                resources,
                worldObjects,
                WorldAuthority.Synchronized,   // hostile list is server-authored/synced; team tallies are raw counts (documented)
                WorldAuthority.Synchronized,   // sector identity + course goals replicate to all peers
                WorldAuthority.MasterDerived,  // credits/research are host-computed economy values
                WorldAuthority.LocallyObserved,
                fireCount,
                reactorTempFraction);
        }

        // ---- per-element builders (each null-safe, each bounded) ---------------

        private static ShipSnapshot BuildShip(PLShipInfoBase ship, PLShipInfoBase playerShip, bool isPlayerShip)
        {
            float hullFraction = float.NaN;
            float shieldFraction = float.NaN;
            try
            {
                PLShipStats stats = ship.MyStats;
                if (stats != null)
                {
                    if (stats.HullMax > 0f) hullFraction = stats.HullCurrent / stats.HullMax;
                    if (stats.ShieldsMax > 0f) shieldFraction = stats.ShieldsCurrent / stats.ShieldsMax;
                }
            }
            catch (Exception) { } // fractions stay NaN = unknown

            bool hostile = false;
            try
            {
                hostile = !isPlayerShip && playerShip != null
                    && playerShip.HostileShips != null
                    && playerShip.HostileShips.Contains(ship.ShipID);
            }
            catch (Exception) { }

            int alertLevel = -1;
            try { alertLevel = ship.AlertLevel; } catch (Exception) { }
            float combatLevel = float.NaN;
            try { combatLevel = ship.GetCombatLevel(); } catch (Exception) { }

            // Phase 17: game-owned "took damage recently" flag (the shipped
            // Patch.cs:242 window, compile-proven). Data-only; any fault leaves
            // false and the combat director's under-fire rule stays silent.
            bool tookDamageRecently = false;
            try
            {
                float timeNow = UnityEngine.Time.time;
                tookDamageRecently = timeNow - ship.LastTookDamageTime() < 10f;
            }
            catch (Exception) { }

            return new ShipSnapshot(
                ship.ShipID, ship.ShipName, isPlayerShip, ship.TeamID,
                hostile, hullFraction, shieldFraction,
                ship.InWarp, (int)ship.WarpChargeStage, ship.WarpTargetID,
                alertLevel, combatLevel,
                tookDamageRecently);
        }

        private static CrewMemberSnapshot BuildCrew(PLPlayer player, bool isCaptain)
        {
            string name = null;
            try { name = player.GetPlayerName(false); } catch (Exception) { }

            bool aliveKnown = false;
            bool alive = false;
            float healthFraction = float.NaN;
            string tliName = null;
            int aiPriorityData = -1;
            try
            {
                PLPawn pawn = player.GetPawn();
                if (pawn != null)
                {
                    aliveKnown = true;
                    alive = !pawn.IsDead;
                    if (pawn.MaxHealth > 0f) healthFraction = pawn.Health / pawn.MaxHealth;
                }
            }
            catch (Exception) { }
            try
            {
                PLTeleportationLocationInstance tli = player.MyCurrentTLI;
                if (tli != null) tliName = tli.TeleporterLocationName;
            }
            catch (Exception) { }
            if (player.IsBot)
            {
                try
                {
                    AIPriority priority = player.ActiveMainPriority;
                    if (priority != null) aiPriorityData = priority.TypeData;
                }
                catch (Exception) { }
            }

            return new CrewMemberSnapshot(
                player.GetPlayerID(), name, player.IsBot, player.GetClassID(), player.TeamID,
                aliveKnown, alive, healthFraction, tliName, isCaptain, aiPriorityData);
        }

        private void RecordPartial(string section, Exception ex)
        {
            System.Threading.Interlocked.Increment(ref m_PartialErrors);
            System.Threading.Volatile.Write(ref m_LastPartialError, section + ": " + ex.GetType().Name);
        }
    }
}