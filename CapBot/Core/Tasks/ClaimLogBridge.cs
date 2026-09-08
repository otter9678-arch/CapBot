using System;
using CapBot.Core.Logging;

namespace CapBot.Core.Tasks
{
    // Bridges execution-claim decisions into the Phase 1 logger. The claims
    // domain stays pure (no PML/game references); this is the only file
    // connecting the two, and it attaches itself once at mod boot (alongside
    // the lifecycle, recovery and scheduler bridges). CapBotLog's spam/flood
    // guards bound output on top of the domain's own per-claim rejection
    // throttle; all lines are static-keyed with bounded task ids/owners inline
    // (treated as data — never executed).
    internal static class ClaimLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            m_Attached = true;
            ExecutionClaims.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.TASK, line);
            });
        }
    }
}