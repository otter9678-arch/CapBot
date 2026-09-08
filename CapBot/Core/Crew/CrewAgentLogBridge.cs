using CapBot.Core.Logging;

namespace CapBot.Core.Crew
{
    // ---- Phase 10: crew agent registry logging bridge ---------------------------
    // Attaches CapBotLog (CREW subsystem) as the registry's decision listener
    // at mod boot. The domain itself stays pure C# — same pattern as
    // TaskLogBridge/SchedulerLogBridge/ClaimLogBridge/ExecutorLogBridge/
    // EmergencyLogBridge.
    public static class CrewAgentLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CrewAgentRegistry.SetDecisionListener(line => CapBotLog.Info(CapBotLog.CREW, line));
            m_Attached = true;
        }
    }
}