using CapBot.Core.Logging;

namespace CapBot.Core.Planning
{
    // ---- Phase 23: Mission work director log bridge -----------------------------
    // Mirrors PlanningLogBridge (P22) / CaptainLogBridge (P18): attaches the
    // director's bounded diagnostics listener to the CapBotLog MISSIONWORK
    // subsystem at boot. The director holds no reference to CapBotLog
    // directly — seams stay test-substitutable (P19 gotcha: the pure domain
    // must compile without CapBotLog.cs in the narrow test file set).
    public static class MissionWorkLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            MissionWorkDirector.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.MISSIONWORK, line);
            });
            m_Attached = true;
        }
    }
}