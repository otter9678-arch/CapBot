using CapBot.Core.Logging;

namespace CapBot.Core.Ollama
{
    // ---- Phase 20: Ollama advisor log bridge ---------------------------------
    // Mirrors DecisionLogBridge (P19): attaches the advisor's bounded
    // diagnostics listener to the CapBotLog OLLAMA subsystem at boot. The
    // advisor holds no reference to CapBotLog directly — seams stay
    // test-substitutable (P19 gotcha: the pure domain must compile without
    // CapBotLog.cs in the narrow test file set).
    public static class AdvisorLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            OllamaAdvisor.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.OLLAMA, line);
            });
            m_Attached = true;
        }
    }
}