using CapBot.AI.Behaviors;
using CapBot.AI.Behaviors.PlanetBehavior;
using UnityEngine;

namespace CapBot.AI
{
    public static class BehaviorProfiles
    {
        public static bool TrySpecialSector(CapBot bot)
        {
            var sector = PLServer.GetCurrentSector();
            if (sector == null) return false;

            switch (sector.VisualIndication)
            {
                case ESectorVisualIndication.TOPSEC:
                    ColonyBehavior.Run(bot);
                    return true;

                case ESectorVisualIndication.LCWBATTLE:
                    WarpGuardianBehavior.Run(bot);
                    return true;

                case ESectorVisualIndication.WASTEDWING:
                    WastedWingBehavior.Run(bot);
                    return true;

                case ESectorVisualIndication.RACING_SECTOR:
                case ESectorVisualIndication.RACING_SECTOR_2:
                case ESectorVisualIndication.RACING_SECTOR_3:
                    RaceBehavior.Run(bot);
                    return true;

                case ESectorVisualIndication.DESERT_HUB:
                    Vector3.Run(bot);
                    return true;
            }

            return false;
        }

        public static bool TryCombat(CapBot bot)
        {
            return CombatBehavior.Run(bot);
        }

        public static bool TryPlanetary(CapBot bot)
        {
            return PlanetBehavior.Run(bot);
        }

        public static bool TryShipManagement(CapBot bot)
        {
            return ShipManagementBehavior.Run(bot);
        }

        public static void Idle(CapBot bot)
        {
            IdleBehavior.Run(bot);
        }
    }
}