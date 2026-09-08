using CapBot.Core.Logging;

namespace CapBot.Core.Combat
{
    // ---- Phase 17: combat director logging bridge ---------------------------------
    // Attaches CapBotLog (COMBAT subsystem, existing const) as the combat
    // director's decision listener at mod boot — same pattern as the other
    // phase bridges. The domain stays pure C#.
    public static class CombatLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CombatDirector.SetDecisionListener(line => CapBotLog.Info(CapBotLog.COMBAT, line));
            m_Attached = true;
        }
    }
}