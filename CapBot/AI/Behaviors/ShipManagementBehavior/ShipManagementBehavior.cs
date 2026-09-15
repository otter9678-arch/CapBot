using UnityEngine;

namespace CapBot.AI
{
    public static class ShipManagementBehavior
    {
        private static float _lastOrder = 0f;
        private static float _lastAction = 0f;
        private static float _lastBlindJump = 0f;

        public static bool Run(CapBot bot)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;
            if (ship == null) return false;

            float now = Time.time;

            // 1. Warp skip if ready
            TryWarpSkip(ship);

            // 2. Repair depot logic
            if (TryRepairDepot(bot, ship, now))
                return true;

            // 3. Warp gate logic
            if (TryWarpGate(bot, ship, now))
                return true;

            // 4. Intruder alert
            if (TryIntruderAlert(bot, ship, now))
                return true;

            // 5. Boarding logic
            if (TryBoarding(bot, ship, now))
                return true;

            // 6. Kill target logic
            if (TryKillTarget(bot, ship, now))
                return true;

            // 7. Planet mission logic
            if (TryPlanetMission(bot, ship, now))
                return true;

            // 8. Align ship logic
            if (TryAlign(bot, ship, now))
                return true;

            // 9. Idle logic
            TryIdle(bot, ship, now);

            // 10. Emergency blind jump
            TryEmergencyBlindJump(bot, ship, now);

            return true;
        }

        // -----------------------------
        // WARP SKIP
        // -----------------------------
        private static void TryWarpSkip(PLShipInfo ship)
        {
            if (ship.InWarp &&
                PLServer.Instance.AllPlayersLoaded() &&
                (ship.MyShieldGenerator == null ||
                 ship.MyStats.ShieldsCurrent / ship.MyStats.ShieldsMax > 0.99f))
            {
                PLInGameUI.Instance.WarpSkipButtonClicked();
            }
        }

        // -----------------------------
        // REPAIR DEPOT
        // -----------------------------
        private static bool TryRepairDepot(CapBot bot, PLShipInfo ship, float now)
        {
            if (ship.MyFlightAI.cachedRepairDepotList.Count == 0)
                return false;

            if (ship.MyStats.HullCurrent / ship.MyStats.HullMax >= 0.99f)
                return false;

            // Set repair order
            if (PLServer.Instance.CaptainsOrdersID != 9 &&
                now - _lastOrder > 1f)
            {
                _lastOrder = now;
                PLServer.Instance.CaptainSetOrderID(9);
            }

            ship.AlertLevel = 0;

            PLRepairDepot depot = ship.MyFlightAI.cachedRepairDepotList[0];

            if (depot.TargetShip == ship &&
                !ship.ShieldIsActive &&
                now - _lastAction > 1f)
            {
                int amount = 0;
                int price = 0;
                PLRepairDepot.GetAutoPurchaseInfo(ship, out amount, out price, 2);

                PLServer.Instance.ServerRepairHull(ship.ShipID, amount, price);

                depot.photonView.RPC("OnRepairTargetShip", PhotonTargets.All,
                    new object[] { ship.ShipID });

                _lastAction = now;
            }

            return true;
        }

        // -----------------------------
        // WARP GATE
        // -----------------------------
        private static bool TryWarpGate(CapBot bot, PLShipInfo ship, float now)
        {
            if (ship.MyFlightAI.cachedWarpStationList.Count == 0)
                return false;

            PLWarpStation station = ship.MyFlightAI.cachedWarpStationList[0];

            if (!station.IsAligned)
                return false;

            if (PLServer.Instance.CaptainsOrdersID != 8 &&
                now - _lastOrder > 1f)
            {
                _lastOrder = now;
                PLServer.Instance.CaptainSetOrderID(8);
            }

            ship.AlertLevel = 0;
            return true;
        }

        // -----------------------------
        // INTRUDER ALERT
        // -----------------------------
        private static bool TryIntruderAlert(CapBot bot, PLShipInfo ship, float now)
        {
            bool hasIntruders = false;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p.TeamID != 0 &&
                    p.MyCurrentTLI == ship.MyTLI)
                {
                    hasIntruders = true;
                    break;
                }
            }

            if (!hasIntruders)
                return false;

            if (PLServer.Instance.CaptainsOrdersID != 6 &&
                now - _lastOrder > 1f)
            {
                _lastOrder = now;
                PLServer.Instance.CaptainSetOrderID(6);
            }

            ship.AlertLevel = 2;
            return true;
        }

        // -----------------------------
        // BOARDING LOGIC
        // -----------------------------
        private static bool TryBoarding(CapBot bot, PLShipInfo ship, float now)
        {
            PLShipInfoBase target = ship.TargetShip;

            if (target == null ||
                target == ship ||
                target.TeamID == 0)
                return false;

            bool canBoard =
                !target.IsQuantumShieldActive ||
                bot.Player.MyCurrentTLI == target.MyTLI;

            if (!canBoard)
                return false;

            if (PLServer.Instance.CaptainsOrdersID != 6 &&
                now - _lastOrder > 1f)
            {
                _lastOrder = now;
                PLServer.Instance.CaptainSetOrderID(6);
            }

            ship.AlertLevel = 2;
            return true;
        }

        // -----------------------------
        // KILL TARGET LOGIC
        // -----------------------------
        private static bool TryKillTarget(CapBot bot, PLShipInfo ship, float now)
        {
            if (ship.TargetShip == null &&
                ship.TargetSpaceTarget == null)
                return false;

            if (ship.TargetShip != null &&
                ship.TargetShip.IsAbandoned())
                return false;

            if (PLServer.Instance.CaptainsOrdersID != 4 &&
                now - _lastOrder > 1f)
            {
                _lastOrder = now;
                PLServer.Instance.CaptainSetOrderID(4);
            }

            ship.AlertLevel = 2;
            return true;
        }

        // -----------------------------
        // PLANET MISSION LOGIC
        // -----------------------------
        private static bool TryPlanetMission(CapBot bot, PLShipInfo ship, float now)
        {
            PLSectorInfo sector = PLServer.GetCurrentSector();

            if (sector == null ||
                !sector.MySPI.HasPlanet)
                return false;

            bool hasMission = HasActiveMissionInSector();

            if (!hasMission)
                return false;

            if (PLServer.Instance.CaptainsOrdersID != 13 &&
                now - _lastOrder > 1f)
            {
                _lastOrder = now;
                PLServer.Instance.CaptainSetOrderID(13);
            }

            return true;
        }

        private static bool HasActiveMissionInSector()
        {
            foreach (PLMissionBase mission in PLServer.Instance.AllMissions)
            {
                if (mission != null &&
                    !mission.Ended &&
                    mission.TargetSectorID == PLServer.GetCurrentSector().ID)
                {
                    return true;
                }
            }
            return false;
        }

        // -----------------------------
        // ALIGN SHIP LOGIC
        // -----------------------------
        private static bool TryAlign(CapBot bot, PLShipInfo ship, float now)
        {
            if (PLStarmap.Instance.CurrentShipPath.Count == 0)
                return false;

            bool needsAlign =