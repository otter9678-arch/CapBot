using CapBot.Core.Logging;

namespace CapBot.Core.Tasks
{
    // ---- Phase 26: multiplayer authority monitor logging bridge -----------------
    // Attaches CapBotLog (TASK subsystem — the monitor is task-pipeline
    // infrastructure, not a gameplay director) as the monitor's decision
    // listener at mod boot. The domain itself stays pure C# (CapBotLog-free,
    // the P19 lesson: run_tests.ps1 compiles a narrow file set).
    public static class MPLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            MultiplayerAuthorityMonitor.SetDecisionListener(line => CapBotLog.Info(CapBotLog.TASK, line));
            m_Attached = true;
        }
    }
}