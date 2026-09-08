using CapBot.Core.Logging;

namespace CapBot.Core.Emergency
{
    // ---- Phase 9: emergency director logging bridge ----------------------------
    // Attaches CapBotLog (EMERGENCY subsystem) as the director's decision
    // listener at mod boot. The domain itself stays pure C# — same pattern as
    // TaskLogBridge/SchedulerLogBridge/ClaimLogBridge/ExecutorLogBridge.
    public static class EmergencyLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            EmergencyDirector.SetDecisionListener(line => CapBotLog.Info(CapBotLog.EMERGENCY, line));
            m_Attached = true;
        }
    }
}