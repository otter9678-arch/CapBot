using CapBot.Core.Logging;

namespace CapBot.Core.Missions
{
    // ---- Phase 15: mission director logging bridge -------------------------------
    // Attaches CapBotLog (MISSION subsystem) as the mission director's decision
    // listener at mod boot — same pattern as the other phase bridges. The
    // domain stays pure C#.
    public static class MissionLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            MissionDirector.SetDecisionListener(line => CapBotLog.Info(CapBotLog.MISSION, line));
            m_Attached = true;
        }
    }
}