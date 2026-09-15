using UnityEngine;

namespace CapBot.AI
{
    public static class CapBotAIController
    {
        public static void UpdateCaptainAI(PLPlayer botPlayer)
        {
            if (!AIUtils.IsValidCaptain(botPlayer))
                return;

            // Retrieve or create bot instance
            CapBot bot = AIRegistry.Get(botPlayer);

            // Update internal state
            AIUtils.UpdateBotState(bot);

            // High-level decision tree
            if (BehaviorProfiles.TrySpecialSector(bot))
                return;

            if (BehaviorProfiles.TryCombat(bot))
                return;

            if (BehaviorProfiles.TryPlanetary(bot))
                return;

            if (BehaviorProfiles.TryShipManagement(bot))
                return;

            // Default fallback
            BehaviorProfiles.Idle(bot);
        }
    }
}