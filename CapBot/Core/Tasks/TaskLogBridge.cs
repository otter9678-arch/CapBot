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
            TaskRegistry.SetTransitionListener(delegate (CapBotTask task, string label)
            {
                CapBotLog.Info(CapBotLog.TASK, "Task " + label + ": " + task.ToStatusLine(TaskClock.NowMs));
            });
        }
    }
}