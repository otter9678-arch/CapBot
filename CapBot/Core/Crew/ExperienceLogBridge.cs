using CapBot.Core.Logging;

namespace CapBot.Core.Crew
{
    // ---- Phase 12: experience registry logging bridge ---------------------------
    // Attaches CapBotLog (CREW subsystem) as the experience registry's
    // decision listener at mod boot — same pattern as CrewAgentLogBridge and
    // PersonalityLogBridge. The domain itself stays pure C#.
    public static class ExperienceLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CrewExperienceRegistry.SetDecisionListener(line => CapBotLog.Info(CapBotLog.CREW, line));
            m_Attached = true;
        }
    }
}