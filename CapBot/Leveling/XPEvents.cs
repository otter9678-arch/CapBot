using System.Collections.Generic;

namespace CapBot.AI
{
    public static class XPEvents
    {
        public const int KILL_ENEMY = 25;
        public const int REPAIR_SYSTEM = 10;
        public const int SCAN_OBJECT = 15;
        public const int COMPLETE_MISSION = 100;
        public const int WARP_JUMP = 5;
        public const int BOARDING_ACTION = 20;
        public const int USE_PROGRAM = 10;
        public const int PILOT_MANEUVER = 8;

        private static readonly Dictionary<PLShipInfo, bool> ShipInWarp = new Dictionary<PLShipInfo, bool>();
        private static int _lastMissionsEnded;

        public static void PollGameEvents()
        {
            if (PLServer.Instance == null || !PhotonNetwork.isMasterClient)
            {
                Reset();
                return;
            }

            // Computed once per frame so every crew bot receives the same award;
            // a rising count means missions completed since the last poll.
            int ended = CountEndedMissions();
            int missionDelta = ended > _lastMissionsEnded ? ended - _lastMissionsEnded : 0;
            _lastMissionsEnded = ended;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p == null || !p.IsBot || p.TeamID != 0) continue;

                if (p.StartingShip != null && ShipCompletedWarp(p.StartingShip))
                    LevelingSystem.AddXP(AIRegistry.Get(p), WARP_JUMP);

                if (missionDelta > 0)
                    LevelingSystem.AddXP(AIRegistry.Get(p), COMPLETE_MISSION * missionDelta);
            }
        }

        // A warp counts once when the ship leaves warp state; the first sighting
        // of a ship only establishes the baseline so pre-existing warps don't award.
        private static bool ShipCompletedWarp(PLShipInfo ship)
        {
            bool inWarp = ship.InWarp;
            bool wasTracked = ShipInWarp.TryGetValue(ship, out bool wasInWarp);
            ShipInWarp[ship] = inWarp;
            return wasTracked && wasInWarp && !inWarp;
        }

        public static void Reset()
        {
            ShipInWarp.Clear();
            _lastMissionsEnded = 0;
        }

        private static int CountEndedMissions()
        {
            int count = 0;
            foreach (PLMissionBase m in PLServer.Instance.AllMissions)
            {
                if (m != null && m.Ended) count++;
            }
            return count;
        }
    }
}