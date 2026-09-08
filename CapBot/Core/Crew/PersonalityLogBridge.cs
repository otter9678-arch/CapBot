using CapBot.Core.Logging;

namespace CapBot.Core.Crew
{
    // ---- Phase 11: personality registry logging bridge --------------------------
    // Attaches CapBotLog (CREW subsystem) as the personality registry's
    // decision listener at mod boot — same pattern as CrewAgentLogBridge.
    // The domain itself stays pure C#.
    public static class PersonalityLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CrewPersonalityRegistry.SetDecisionListener(line => CapBotLog.Info(CapBotLog.CREW, line));
            m_Attached = true;
        }
    }
}