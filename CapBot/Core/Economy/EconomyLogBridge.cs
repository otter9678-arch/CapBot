using CapBot.Core.Logging;

namespace CapBot.Core.Economy
{
    // ---- Phase 16: economy director logging bridge --------------------------------
    // Attaches CapBotLog (ECONOMY subsystem, existing const) as the economy
    // director's decision listener at mod boot — same pattern as the other
    // phase bridges. The domain stays pure C#.
    public static class EconomyLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            EconomyDirector.SetDecisionListener(line => CapBotLog.Info(CapBotLog.ECONOMY, line));
            m_Attached = true;
        }
    }
}