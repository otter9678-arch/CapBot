using CapBot.Core.Logging;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 50: Safe-Mode-gate logging bridge ----------------------------------
    // Attaches CapBotLog (COMPAT subsystem) as the gate's audit listener.
    // The domain stays pure C# (P19 lesson).
    public static class SafeModeLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            SafeModeGate.SetAuditListener(line => CapBotLog.Info(CapBotLog.COMPAT, line));
            m_Attached = true;
        }
    }
}