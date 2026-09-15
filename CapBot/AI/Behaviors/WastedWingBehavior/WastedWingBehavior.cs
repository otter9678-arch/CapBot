using UnityEngine;

namespace CapBot.AI
{
    public static class WastedWingBehavior
    {
        private static Vector3 _reactorPos = new Vector3(0, 0, 0);
        private static bool _reactorLocated = false;

        public static void Run(CapBot bot)
        {
            PLPlayer player = bot.Player;
            PLPawn pawn = player.GetPawn();
            PLBot ai = player.MyBot;

            if (pawn == null)
                return;

            // Find reactor once
            if (!_reactorLocated)
                LocateReactor();

            // If reactor not found, fallback to combat
            if (!_reactorLocated)
            {
                CombatBehavior.Run(bot);
                return;
            }

            // If enemies present, fight them
            if (EnemiesNearby(pawn))
            {
                CombatBehavior.Run(bot);
                return;
            }

            // Move toward reactor
            MoveToReactor(bot, pawn, ai);

            // If close enough, interact with reactor
            TryActivateReactor(bot, pawn);
        }

        // -----------------------------
        // FIND THE REACTOR
        // -----------------------------
        private static void LocateReactor()
        {
            foreach (PLReactor reactor in Object.FindObjectsOfType<PLReactor>())
            {
                if (reactor.name.Contains("WastedWing"))
                {
                    _reactorPos = reactor.transform.position;
                    _reactorLocated = true;
                    return;
                }
            }
        }

        // -----------------------------
        // MOVE TOWARD THE REACTOR