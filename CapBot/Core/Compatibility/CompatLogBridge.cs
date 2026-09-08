using CapBot.Core.Logging;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 27: compatibility manager logging bridge -------------------------
    // Attaches CapBotLog (COMPAT subsystem — the compat channel used by
    // MoreBotsCompatPatch) as the manager's decision listener at mod boot.
    // The domain stays pure C# (CapBotLog-free, the P19 lesson).
    public static class CompatLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CompatManager.SetDecisionListener(line => CapBotLog.Info(CapBotLog.COMPAT, line));
            m_Attached = true;
        }
    }
}