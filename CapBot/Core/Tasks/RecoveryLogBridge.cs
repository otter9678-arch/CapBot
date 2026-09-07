using System;
using CapBot.Core.Logging;

namespace CapBot.Core.Tasks
{
    // Bridges recovery outcomes into the Phase 1 logger. The recovery domain
    // itself stays pure (no PML/game references); this is the only file that
    // connects the two, and it attaches itself once at mod boot (alongside
    // the Phase 2 TaskLogBridge). CapBotLog's spam/flood guards bound output;
    // reasons are bounded text (CapBotTask truncates to 200 chars) and are
    // treated as data, never executed.
    internal static class RecoveryLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            m_Attached = true;
            TaskRecoveryManager.SetActionListener(delegate (CapBotTask task, RecoveryActionType action, string reason, bool acted)
            {
                string line = "Recovery " + (acted ? "applied" : "rejected") + " " + action
                    + " on " + task.ToStatusLine(TaskClock.NowMs);
                if (!string.IsNullOrEmpty(reason)) line += " (" + reason + ")";
                CapBotLog.Info(CapBotLog.TASK, line);
            });
        }
    }
}