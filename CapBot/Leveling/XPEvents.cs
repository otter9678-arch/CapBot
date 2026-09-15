using CapBot.AI;
using UnityEngine;

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

        private static PLShipInfo _lastShip;
        private static int _lastMissionsEnded;

        public static void PollGameEvents()
        {
            if (PLServer.Instance == null || !PhotonNetwork.isMasterClient)
            {
                _lastShip = null;
                _lastMissionsEnded = 0;
                return;
            }

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p == null || !p.IsBot || p.TeamID != 0) continue;

                CaptainBot bot = AIRegistry.Get(p);

                // Warp XP: award once per completed warp for the ship's bots.
                PLShipInfo ship = p.StartingShip;
                if (ship != null && ship != _lastShip)
                {
                    if (_lastShip != null)
                        LevelingSystem.AddXP(bot, WARP_JUMP);
                    _lastShip = ship;
                }

                // Mission XP: award when the crew's active mission count drops
                // (a mission ended) while this bot is alive.
                int ended = CountEndedMissions();
                if (ended > _lastMissionsEnded)
                {
                    LevelingSystem.AddXP(bot, COMPLETE_MISSION * (ended - _lastMissionsEnded));
                    _lastMissionsEnded = ended;
                }
                else if (ended < _lastMissionsEnded)
                {
                    _lastMissionsEnded = ended;
                }
            }
        }

        public static void Reset()
        {
            _lastShip = null;
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