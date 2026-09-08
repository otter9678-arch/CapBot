using CapBot.Core.Logging;

namespace CapBot.Core.Planning
{
    // ---- Phase 22: Planning director log bridge --------------------------------
    // Mirrors CaptainLogBridge (P18): attaches the director's bounded
    // diagnostics listener to the CapBotLog PLANNING subsystem at boot. The
    // director holds no reference to CapBotLog directly — seams stay
    // test-substitutable (P19 gotcha: the pure domain must compile without
    // CapBotLog.cs in the narrow test file set).
    public static class PlanningLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            PlanningDirector.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.PLANNING, line);
            });
            m_Attached = true;
        }
    }
}