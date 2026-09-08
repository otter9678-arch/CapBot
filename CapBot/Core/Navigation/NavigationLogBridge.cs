using CapBot.Core.Logging;

namespace CapBot.Core.Navigation
{
    // ---- Phase 14: navigation recovery logging bridge ----------------------------
    // Attaches CapBotLog (NAVIGATION subsystem) as the navigation recovery
    // director's decision listener at mod boot — same pattern as the other
    // phase bridges. The domain stays pure C#.
    public static class NavigationLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            NavigationRecoveryDirector.SetDecisionListener(line => CapBotLog.Info(CapBotLog.NAVIGATION, line));
            m_Attached = true;
        }
    }
}