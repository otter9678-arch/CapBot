using CapBot.Core.Logging;

namespace CapBot.Core.Qwen
{
    // ---- Phase 21: Crew advisor log bridge ------------------------------------
    // Mirrors AdvisorLogBridge (P20): attaches the advisor's bounded
    // diagnostics listener to the CapBotLog QWEN subsystem at boot. The
    // advisor holds no reference to CapBotLog directly — seams stay
    // test-substitutable (P19 gotcha: the pure domain must compile without
    // CapBotLog.cs in the narrow test file set).
    public static class CrewAdvisorLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CrewAdvisor.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.QWEN, line);
            });
            m_Attached = true;
        }
    }
}