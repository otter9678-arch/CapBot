using CapBot.Core.Logging;

namespace CapBot.Core.Captain
{
    // ---- Phase 18: captain deliberation director logging bridge --------------------
    // Attaches CapBotLog (CAPTAIN subsystem, existing const) as the captain
    // director's decision listener at mod boot — same pattern as the other
    // phase bridges. The domain stays pure C#.
    public static class CaptainLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CaptainDirector.SetDecisionListener(line => CapBotLog.Info(CapBotLog.CAPTAIN, line));
            m_Attached = true;
        }
    }
}