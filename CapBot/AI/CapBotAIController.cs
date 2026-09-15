using UnityEngine;

namespace CapBot.AI
{
    public static class CapBotAIController
    {
        // Legacy Patch.cs owns the in-game decision loop; this controller only
        // tracks which behavior is active for UI/debug/personality purposes.
        public static void UpdateCaptainAI(PLPlayer botPlayer)
        {
            if (!AIUtils.IsValidCaptain(botPlayer))
                return;

            CaptainBot bot = AIRegistry.Get(botPlayer);
            AIUtils.UpdateBotState(bot);

            PLSectorInfo sector = PLServer.GetCurrentSector();
            string behavior = "ShipManagement";

            if (sector != null)
            {
                switch (sector.VisualIndication)
                {
                    case ESectorVisualIndication.TOPSEC:
                        behavior = "Colony";
                        break;
                    case ESectorVisualIndication.LCWBATTLE:
                        behavior = "WarpGuardian";
                        break;
                    case ESectorVisualIndication.WASTEDWING:
                        behavior = "WastedWing";
                        break;
                    case ESectorVisualIndication.RACING_SECTOR:
                    case ESectorVisualIndication.RACING_SECTOR_2:
                    case ESectorVisualIndication.RACING_SECTOR_3:
                        behavior = "Race";
                        break;
                    case ESectorVisualIndication.DESERT_HUB:
                        behavior = "Burrow";
                        break;
                    default:
                        if (shipHasHostiles(botPlayer.StartingShip))
                        {
                            behavior = "Combat";
                        }
                        else if (sector.MySPI != null && sector.MySPI.HasPlanet)
                        {
                            behavior = "Planet";
                        }
                        break;
                }
            }

            bot.CurrentBehavior = behavior;

            if (botPlayer.StartingShip != null && botPlayer.StartingShip.InWarp)
                LevelingSystem.AddXP(bot, XPEvents.WARP_JUMP / 60);
        }

        private static bool shipHasHostiles(PLShipInfo ship)
        {
            return ship != null &&
                   (ship.HostileShips.Count > 0 ||
                    (ship.TargetShip != null && ship.TargetShip != ship) ||
                    ship.TargetSpaceTarget != null);
        }
    }
}