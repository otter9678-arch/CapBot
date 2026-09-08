using CapBot.Core.Logging;

namespace CapBot.Core.Validation
{
    // ---- Phase 19: decision validator log bridge ------------------------------
    // Mirrors CaptainLogBridge (P18): attaches the validator's bounded
    // diagnostics listener to the CapBotLog DECISION subsystem at boot.
    // The validator holds no reference to CapBotLog directly — seams stay
    // test-substitutable, matching the P18 house pattern.
    public static class DecisionLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            DecisionValidator.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.DECISION, line);
            });
            m_Attached = true;
        }
    }
}