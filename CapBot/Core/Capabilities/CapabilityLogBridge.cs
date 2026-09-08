using System;
using CapBot.Core.Logging;

namespace CapBot.Core.Capabilities
{
    // Bridges capability-registry decisions into the Phase 1 logger. The
    // capabilities domain stays pure (no PML/game references); this is the
    // only file that connects it to CapBotLog, attached once at mod boot
    // (alongside the lifecycle, recovery, scheduler, claims and world
    // bridges). CapBotLog's spam/flood guards bound output on top of the
    // registry's own one-line-per-decision discipline; every line is static
    // vocabulary with bounded ids/owners inline (treated as data — never
    // executed).
    internal static class CapabilityLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            m_Attached = true;
            CapabilityRegistry.SetDecisionListener(delegate (string line)
            {
                CapBotLog.Info(CapBotLog.CAPABILITY, line);
            });
        }
    }
}