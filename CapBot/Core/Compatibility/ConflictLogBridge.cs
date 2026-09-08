using CapBot.Core.Logging;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 46: conflict engine logging bridge --------------------------------
    // Attaches CapBotLog (COMPAT subsystem) as the engine's decision listener at
    // mod boot. The domain stays pure C# (CapBotLog-free, the P19 lesson).
    public static class ConflictLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            ConflictEngine.SetDecisionListener(line => CapBotLog.Info(CapBotLog.COMPAT, line));
            m_Attached = true;
        }
    }
}