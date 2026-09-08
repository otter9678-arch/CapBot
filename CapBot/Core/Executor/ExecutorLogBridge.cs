using System;
using CapBot.Core.Logging;

namespace CapBot.Core.Executor
{
    // Bridges executor decisions into the Phase 1 logger. The executor domain
    // stays pure (no PML/game references); this is the only file connecting
    // the two, and it attaches itself once at mod boot (alongside the
    // lifecycle, recovery, scheduler and claims bridges). CapBotLog's
    // spam/flood guards bound output on top of the executor's own per-tick
    // gate; all lines are static-keyed with bounded task ids/owners inline
    // (treated as data — never executed).
    internal static class ExecutorLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            m_Attached = true;
            TaskExecutor.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.TASK, line);
            });
        }
    }
}