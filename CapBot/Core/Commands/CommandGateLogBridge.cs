using CapBot.Core.Logging;

namespace CapBot.Core.Commands
{
    // ---- Phase 41: command-gate log bridge ------------------------------------
    // Mirrors DecisionLogBridge (P19): attaches the gate's bounded diagnostics
    // listener to the CapBotLog COMMAND subsystem at boot. The gate holds no
    // reference to CapBotLog directly — seams stay test-substitutable, matching
    // the house pattern.
    public static class CommandGateLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            CommandGate.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.COMMAND, line);
            });
            m_Attached = true;
        }
    }
}