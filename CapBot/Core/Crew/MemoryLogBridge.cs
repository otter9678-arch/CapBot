using CapBot.Core.Logging;

namespace CapBot.Core.Crew
{
    // ---- Phase 13: memory system logging bridge ---------------------------------
    // Attaches CapBotLog (CREW subsystem) as the memory system's decision
    // listener at mod boot — same pattern as CrewAgentLogBridge,
    // PersonalityLogBridge and ExperienceLogBridge. The domain stays pure C#.
    public static class MemoryLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CrewMemorySystem.SetDecisionListener(line => CapBotLog.Info(CapBotLog.CREW, line));
            m_Attached = true;
        }
    }
}