using CapBot.Dialogue;
using UnityEngine;

namespace CapBot.AI
{
    public static class CapBotAIController
    {
        // Called from the meta-layer ticker each frame.
        public static void PollBots()
        {
            if (PLServer.Instance == null) return;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null) UpdateCaptainAI(p);
            }
        }

        // Legacy Patch.cs owns the in-game decision loop and reports the
        // branch it took via AIRegistry.ReportActivity; this controller only
        // reacts to those transitions for dialogue/personality purposes.
        public static void UpdateCaptainAI(PLPlayer botPlayer)
        {
            if (!AIUtils.IsValidCaptain(botPlayer))
                return;

            CaptainBot bot = AIRegistry.Get(botPlayer);
            AIUtils.UpdateBotState(bot);
            BotSelfManager.Poll(botPlayer);
            BotSelfManager.ManageNearbyPickups(bot);
            BotSelfManager.PollResearch(bot);
        }

        // Called from AIRegistry.ReportActivity when Patch.cs changes branch.
        public static void OnActivityChanged(CaptainBot bot, string activity)
        {
            switch (activity)
            {
                case "Combat":
                case "WarpGuardian":
                    DialogueTriggers.OnCombatStart(bot);
                    break;

                case "Planet":
                case "Colony":
                    DialogueTriggers.OnExplore(bot);
                    break;
            }
        }
    }
}