using CapBot.Core.Logging;

namespace CapBot.Core.Adjustment
{
    // ---- Phase 24: Adjustment observer log bridge -------------------------------
    // Mirrors MissionWorkLogBridge (P23) / PlanningLogBridge (P22): attaches
    // the observer's bounded diagnostics listener to the CapBotLog ADJUSTMENT
    // subsystem at boot. The observer holds no reference to CapBotLog
    // directly — seams stay test-substitutable (P19 gotcha: the pure domain
    // must compile without CapBotLog.cs in the narrow test file set).
    public static class AdjustmentLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            AdjustmentDirector.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.ADJUSTMENT, line);
            });
            m_Attached = true;
        }
    }
}