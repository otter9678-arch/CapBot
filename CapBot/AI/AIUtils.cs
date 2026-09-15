using UnityEngine;

namespace CapBot.AI
{
    public static class AIUtils
    {
        public static bool IsValidCaptain(PLPlayer p)
        {
            return p != null &&
                   p.IsBot &&
                   p.TeamID == 0 &&
                   p.GetClassID() == 0 &&
                   p.StartingShip != null;
        }

        public static void UpdateBotState(CaptainBot bot) => bot.LastActionTime = Time.time;
    }
}