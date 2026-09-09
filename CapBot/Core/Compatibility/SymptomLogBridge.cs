using CapBot.Core.Logging;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 49: symptom-detector logging bridge --------------------------------
    // Attaches CapBotLog (COMPAT subsystem) as the detectors' audit listener.
    // The domain stays pure C# (P19 lesson).
    public static class SymptomLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            SymptomDetectors.SetAuditListener(line => CapBotLog.Info(CapBotLog.COMPAT, line));
            m_Attached = true;
        }
    }
}