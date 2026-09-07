using System;

namespace CapBot.Core.Tasks
{
    // Task lifecycle states (PART 57 Phase 2). Terminal states: Completed,
    // Cancelled, Expired. Failed is semi-terminal: it may only retry back to
    // Queued, and only up to MaxRetries (enforced by CapBotTask).
    public enum TaskState
    {
        Created = 0,
        Queued = 1,
        Running = 2,
        Paused = 3,
        Completed = 4,
        Failed = 5,
        Cancelled = 6,
        Expired = 7
    }

    // Monotonic task-identity source. Uses an ever-increasing counter; ids are
    // never reused within a session. Thread-safe via Interlocked because the
    // game may call into task code from UI and tick contexts.
    public static class TaskIds
    {
        private static long m_Next = 1;

        public static long Next()
        {
            return System.Threading.Interlocked.Increment(ref m_Next) - 1;
        }

        public static long Current => System.Threading.Interlocked.Read(ref m_Next) - 1;
    }

    // Milliseconds-only clock. Environment.TickCount wraps every ~24.8 days;
    // unchecked int subtraction stays correct for intervals far below the wrap
    // period. ElapsedMs additionally tracks wraps so long sessions can report
    // true absolute ages as a 64-bit total.
    public static class TaskClock
    {
        private static int m_LastRaw;
        private static long m_WrapCount;
        private static readonly object m_WrapLock = new object();

        static TaskClock()
        {
            m_LastRaw = Environment.TickCount;
        }

        public static int NowMs
        {
            get
            {
                int raw = Environment.TickCount;
                lock (m_WrapLock)
                {
                    // TickCount only ever counts up (mod 2^32). A positive->negative
                    // sign flip is therefore exactly one wrap; any other change is
                    // ordinary forward time (unchecked deltas already handle it).
                    if (m_LastRaw > 0 && raw < 0) m_WrapCount++;
                    m_LastRaw = raw;
                }
                return raw;
            }
        }

        // Wrap-aware absolute age: unchecked 32-bit delta plus 2^32 per wrap.
        public static long ElapsedMs(int fromMs)
        {
            int now = NowMs;
            lock (m_WrapLock)
            {
                return unchecked(now - fromMs) + m_WrapCount * 4294967296L;
            }
        }

        // Interval between two stored stamps; correct for spans << 24.8 days.
        public static int DeltaMs(int fromMs, int toMs)
        {
            return unchecked(toMs - fromMs);
        }
    }

    // The single source of truth for legal state transitions. No other code
    // may mutate a task's state except through CapBotTask.ApplyTransition,
    // which consults this table first.
    public static class TaskTransitions
    {
        // Returns true when `from -> to` is legal.
        public static bool CanTransition(TaskState from, TaskState to)
        {
            switch (from)
            {
                case TaskState.Created:
                    return to == TaskState.Queued
                        || to == TaskState.Cancelled
                        || to == TaskState.Expired
                        || to == TaskState.Failed; // rejected before first queue (e.g. validation)
                case TaskState.Queued:
                    return to == TaskState.Running
                        || to == TaskState.Cancelled
                        || to == TaskState.Expired;
                case TaskState.Running:
                    return to == TaskState.Paused
                        || to == TaskState.Completed
                        || to == TaskState.Failed
                        || to == TaskState.Cancelled
                        || to == TaskState.Expired;
                case TaskState.Paused:
                    return to == TaskState.Running
                        || to == TaskState.Cancelled
                        || to == TaskState.Expired;
                case TaskState.Completed: // terminal — no exits
                case TaskState.Cancelled: // terminal — no exits
                case TaskState.Expired:   // terminal — no exits
                    return false;
                case TaskState.Failed:
                    return to == TaskState.Queued  // retry path only
                        || to == TaskState.Cancelled
                        || to == TaskState.Expired;
                default:
                    return false;
            }
        }

        // Human-readable rejection reason; empty string when legal.
        public static string IllegalReason(TaskState from, TaskState to)
        {
            if (CanTransition(from, to)) return string.Empty;
            if (from == TaskState.Completed || from == TaskState.Cancelled || from == TaskState.Expired)
                return "task is terminal (" + from + ") and cannot transition to " + to;
            return "illegal transition " + from + " -> " + to;
        }
    }
}