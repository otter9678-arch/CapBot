using CapBot.Core.Logging;

namespace CapBot.Core.Tasks
{
    // Bridges task-domain transitions into the Phase 1 logger. The task domain
    // stays pure (no PML/game references); this is the only file that connects
    // the two, and it attaches itself once at mod boot.
    internal static class TaskLogBridge
    {
        private static bool m_Attached;

        public static void Ensure()
        {
            if (m_Attached) return;
            m_Attached = true;
            // One registry listener total: logs every transition AND feeds the
            // Phase 3 recovery manager's per-task bookkeeping (Track on
            // registration; terminal tasks are dropped by the manager itself).
            TaskRegistry.SetTransitionListener(delegate (CapBotTask task, string label)
            {
                if (label == "Registered") TaskRecoveryManager.Track(task);
                CapBotLog.Info(CapBotLog.TASK, "Task " + label + ": " + task.ToStatusLine(TaskClock.NowMs));
            });
        }
    }
}