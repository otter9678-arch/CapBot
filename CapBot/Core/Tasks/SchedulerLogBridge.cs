using System;
using CapBot.Core.Logging;

namespace CapBot.Core.Tasks
{
    // Bridges scheduler decisions into the Phase 1 logger. The scheduler stays
    // pure (no PML/game references); this is the only file connecting the two,
    // and it attaches itself once at mod boot (alongside the lifecycle and
    // recovery bridges). CapBotLog's spam/flood guards bound output; decision
    // lines are static-keyed with variable detail inline (task ids/types are
    // bounded text, treated as data — never executed).
    internal static class SchedulerLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            m_Attached = true;
            TaskScheduler.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.TASK, line);
            });
        }
    }
}