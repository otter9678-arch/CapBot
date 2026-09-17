using CapBot.Core.Logging;

namespace CapBot.Core.Learning
{
    // ---- Phase 26: trait profile consumer log bridge -----------------------------
    // Mirrors AdjustmentLogBridge (P24) / LearningLogBridge (P25): attaches the
    // consumer's bounded diagnostics listener to the CapBotLog TRAIT subsystem
    // at boot. The director holds no reference to CapBotLog directly — seams
    // stay test-substitutable (P19 gotcha: the pure domain must compile without
    // CapBotLog.cs in the narrow test file set).
    public static class TraitProfileLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            TraitProfileDirector.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.TRAIT, line);
            });
            m_Attached = true;
        }
    }
}