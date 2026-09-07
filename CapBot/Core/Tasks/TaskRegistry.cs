using System;
using System.Collections.Generic;

namespace CapBot.Core.Tasks
{
    // Bounded, pure-C# registry of live tasks. Phase 2 infrastructure: no game
    // calls, no scheduling, no execution. Future phases (scheduler, recovery,
    // directors) look tasks up here and drive transitions through CapBotTask.
    //
    // Bounded by design:
    //   - MaxLiveTasks: creating beyond the cap returns null (oldest live tasks
    //     are NOT evicted — callers decide how to react; silent eviction would
    //     hide lost work).
    //   - Terminal tasks move to a ring of the last MaxCompletedHistory entries;
    //     older history is dropped, never unbounded.
    public static class TaskRegistry
    {
        public const int MaxLiveTasks = 64;
        public const int MaxCompletedHistory = 128;

        private static readonly Dictionary<long, CapBotTask> m_Live = new Dictionary<long, CapBotTask>();
        private static readonly Queue<CapBotTask> m_History = new Queue<CapBotTask>();
        private static Action<CapBotTask, string> m_OnTransition; // log bridge (Phase 1) attaches here
        private static readonly object m_Lock = new object();

        public static int LiveCount { get { lock (m_Lock) return m_Live.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return m_History.Count; } }

        // Registers a task created via CapBotTask.Create. Null-safe: Create
        // already validated; this only tracks.
        public static bool Register(CapBotTask task)
        {
            if (task == null) return false;
            CapBotTask recorded = null;
            lock (m_Lock)
            {
                if (m_Live.Count >= MaxLiveTasks) return false;
                if (m_Live.ContainsKey(task.TaskId)) return false;
                m_Live[task.TaskId] = task;
                recorded = task;
            }
            // Listener fires outside the lock so a logging callback can never
            // deadlock against registry bookkeeping.
            FireListener(recorded, "Registered");
            return true;
        }

        public static CapBotTask Get(long taskId)
        {
            lock (m_Lock)
            {
                CapBotTask t;
                if (m_Live.TryGetValue(taskId, out t)) return t;
            }
            return FindInHistory(taskId);
        }

        private static CapBotTask FindInHistory(long taskId)
        {
            lock (m_Lock)
            {
                foreach (CapBotTask t in m_History)
                {
                    if (t.TaskId == taskId) return t;
                }
            }
            return null;
        }

        // Records a completed transition. Called automatically from
        // CapBotTask.ApplyTransition (the only state mutator), so registry
        // buckets can never diverge from task state. Listener fires outside
        // the lock.
        public static void RecordTransition(CapBotTask task, string transitionLabel)
        {
            if (task == null) return;
            lock (m_Lock)
            {
                if (task.IsTerminal && m_Live.Remove(task.TaskId))
                {
                    m_History.Enqueue(task);
                    while (m_History.Count > MaxCompletedHistory) m_History.Dequeue();
                }
            }
            FireListener(task, transitionLabel);
        }

        private static void FireListener(CapBotTask task, string label)
        {
            Action<CapBotTask, string> listener;
            lock (m_Lock) listener = m_OnTransition;
            if (listener != null) listener(task, label);
        }

        // Expire-and-sweep helper for later phases: expires every live task
        // whose deadline elapsed. Returns the number expired. TryExpire's
        // transition is auto-recorded by ApplyTransition.
        public static int SweepExpired(int nowMs)
        {
            List<CapBotTask> expired = null;
            lock (m_Lock)
            {
                foreach (CapBotTask t in m_Live.Values)
                {
                    if (t.IsTimedOut(nowMs))
                    {
                        if (expired == null) expired = new List<CapBotTask>();
                        expired.Add(t);
                    }
                }
            }
            if (expired == null) return 0;
            int count = 0;
            foreach (CapBotTask t in expired)
            {
                if (t.TryExpire()) count++;
            }
            return count;
        }

        // Point-in-time snapshot of all live tasks (bounded: ≤ MaxLiveTasks).
        // Scheduler Phase 4 reads this instead of enumerating the dictionary
        // itself, so registry internals stay private. Callers must treat the
        // returned list as read-only; tasks themselves are immutable outside
        // their own ApplyTransition mutators.
        public static List<CapBotTask> LiveSnapshot()
        {
            lock (m_Lock)
            {
                return new List<CapBotTask>(m_Live.Values);
            }
        }

        // Deterministic snapshot for status reporting (Phase 29 will reuse).
        // Sorted by TaskId ascending. Bounded by live+history caps.
        public static List<string> StatusLines(int nowMs)
        {
            List<string> lines = new List<string>();
            List<CapBotTask> all = new List<CapBotTask>();
            lock (m_Lock)
            {
                foreach (CapBotTask t in m_Live.Values) all.Add(t);
                foreach (CapBotTask t in m_History) all.Add(t);
            }
            all.Sort(delegate (CapBotTask a, CapBotTask b) { return a.TaskId.CompareTo(b.TaskId); });
            foreach (CapBotTask t in all) lines.Add(t.ToStatusLine(nowMs));
            return lines;
        }

        // Attach the Phase 1 logging bridge. Pass null to detach. The callback
        // must never throw (CapBotLog's own Emit is self-guarded).
        public static void SetTransitionListener(Action<CapBotTask, string> listener)
        {
            lock (m_Lock) m_OnTransition = listener;
        }

        // Test/dev isolation only — wipes all state. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Live.Clear();
                m_History.Clear();
                m_OnTransition = null;
            }
        }
    }
}