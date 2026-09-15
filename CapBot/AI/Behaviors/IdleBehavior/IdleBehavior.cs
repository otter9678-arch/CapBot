using UnityEngine;

namespace CapBot.AI
{
    public static class IdleBehavior
    {
        private static float _lastOrder = 0f;
        private static float _lastSitCheck = 0f;

        public static void Run(CapBot bot)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;
            if (ship == null) return;

            float now = Time.time;

            // 1. Set captain order to "At Attention"
            if (PLServer.Instance.CaptainsOrdersID != 1 &&
                now - _lastOrder > 1f)
            {
                _lastOrder = now;
                PLServer.Instance.CaptainSetOrderID(1);
            }

            // 2. Set alert level to green
            ship.AlertLevel = 0;

            // 3. Sit in captain’s chair if idle for 20 seconds
            TrySitInChair(bot, ship, now);

            // 4. Reset bot action timer
            bot.LastActionTime = now;
        }

        // -----------------------------
        // SIT IN CAPTAIN’S CHAIR
        // -----------------------------
        private static void TrySitInChair(CapBot bot, PLShipInfo ship, float now)
        {
            // Only check every 2 seconds
            if (now - _lastSitCheck < 2f)
                return;

            _lastSitCheck = now;

            // Must have a chair
            PLCaptainsChair chair = ship.MyStats.GetShipComponent<PLCaptainsChair>(
                ESlotType.E_COMP_CAPTAINS_CHAIR, false);

            if (chair == null)
                return;

            // Only sit if idle for 20 seconds
            if (now - bot.LastActionTime < 20f)
                return;

            Vector3 chairPos = ship.CaptainsChairPivot.position;

            bot.Player.MyBot.AI_TargetPos = chairPos;
            bot.Player.MyBot.AI_TargetPos_Raw = chairPos;
            bot.Player.MyBot.AI_TargetTLI = ship.MyTLI;

            // Move toward chair
            if ((chairPos - bot.Player.GetPawn().transform.position).sqrMagnitude > 4f)
            {
                bot.Player.MyBot.EnablePathing = true;
            }
            else
            {
                // Sit down
                if (ship.CaptainsChairPlayerID != bot.Player.GetPlayerID())
                {
                    ship.AttemptToSitInCaptainsChair(bot.Player.GetPlayerID());
                }
            }
        }
    }
}