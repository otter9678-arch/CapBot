using System;
using CapBot.Core.Logging;

namespace CapBot.Core.World
{
    // Bridges world-state observations into the Phase 1 logger. The world
    // domain stays pure (no PML/game references); this is the only file that
    // connects it to CapBotLog, attached once at mod boot. CapBotLog's
    // spam/flood guards bound output; snapshot summaries and transitions are
    // static vocabulary + numbers — data, never executed.
    internal static class WorldLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            m_Attached = true;
            WorldStateService.SetTransitionListener(delegate (WorldTransition t)
            {
                // One line per detected transition (sector changes / warp
                // edges are rare, vanilla-cadence events — no spam risk).
                CapBotLog.Info(CapBotLog.TASK, "World transition " + t.ToString());
            });
        }
    }
}