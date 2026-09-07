using System;
using System.Collections.Generic;

namespace CapBot.Core.Tasks
{
    // ---- Phase 3: recovery executor ----------------------------------------
    // Advances recovery for every live task. Deterministic state machine per
    // task (TaskRecoveryPolicy), bounded memory, log-bridged outcomes.
    //
    // NOT a scheduler: this code never queues new work, never picks which
    // task should run, and never executes gameplay actions. It only reacts to
    // tasks that are Failed / stuck / expired / invalidated / interrupted /
    // blocked — driving them through CapBotTask's own idempotent transitions.
    // Intended cadence ~1 s (vanilla decision-gate), never per frame.
    public static class TaskRecoveryManager
    {
        public const int StuckThresholdMs = 15000;   // no observable progress -> stuck
        public const int PausedAbandonMs = 60000;    // capability-pause ceiling before abandon
        public const int MaxRecoveryActions = 12;    // lifetime budget per task (non-terminal)
        public const int MinRecheckMs = 1000;        // a task is never re-examined faster
        public const int MaxBackoffMs = 30000;       // retry delay ceiling
        private const int MaxTracked = TaskRegistry.MaxLiveTasks; // ≤ live cap by definition

        private static readonly Dictionary<long, RecoveryRecord> m_Records =
            new Dictionary<long, RecoveryRecord>();
        private static readonly object m_Lock = new object();
        private static ITaskWorldProbe m_Probe = NullWorldProbe.Instance;
        private static Action<CapBotTask, RecoveryActionType, string, bool> m_OnAction;
        private static int m_LastTickMs = -1;
        private static bool m_Enabled = true;

        // ---- configuration (bounded, test-settable) -----------------------
        public static int BackoffBaseMs = 2000;
        public static double BackoffMultiplier = 2.0;

        public static bool Enabled
        {
            get { lock (m_Lock) return m_Enabled; }
            set { lock (m_Lock) m_Enabled = value; }
        }

        public static ITaskWorldProbe Probe
        {
            get { lock (m_Lock) return m_Probe; }
            set
            {
                lock (m_Lock) m_Probe = value ?? NullWorldProbe.Instance;
            }
        }

        // Diagnostic/logging hook (RecoveryLogBridge attaches at boot; tests
        // may attach counters). Fired outside the lock, never under it.
        public static void SetActionListener(Action<CapBotTask, RecoveryActionType, string, bool> listener)
        {
            lock (m_Lock) m_OnAction = listener;
        }

        // Bounded exponential backoff: base * multiplier^retryCount, capped.
        public static int BackoffDelayMs(int retryCount)
        {
            if (retryCount <= 0) return BackoffBaseMs;
            double d = BackoffBaseMs;
            for (int i = 0; i < retryCount && d < MaxBackoffMs; i++) d *= BackoffMultiplier;
            if (d > MaxBackoffMs) d = MaxBackoffMs;
            return (int)d;
        }

        // ---- Tick ----------------------------------------------------------
        // One pass over live tasks. Safe to call at any cadence; per-task work
        // is recheck-gated (≥1 s) so per-frame callers still yield ~1 s policy.
        // Returns the number of recovery actions executed.
        public static int Tick(int nowMs)
        {
            if (!Enabled) return 0;
            if (m_LastTickMs >= 0 && unchecked(nowMs - m_LastTickMs) < MinRecheckMs) return 0;
            m_LastTickMs = nowMs;

            // Snapshot the due tasks under the lock, act outside it (same
            // pattern as TaskRegistry's listener: no callbacks under lock).
            List<KeyValuePair<long, RecoveryRecord>> due = null;
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, RecoveryRecord> kv in m_Records)
                {
                    RecoveryRecord r = kv.Value;
                    if (r.NextRecheckMs > nowMs) continue;
                    r.NextRecheckMs = nowMs + MinRecheckMs;
                    if (due == null) due = new List<KeyValuePair<long, RecoveryRecord>>();
                    due.Add(kv);
                }
            }

            int executed = 0;
            if (due == null) return 0;
            foreach (KeyValuePair<long, RecoveryRecord> kv in due)
            {
                CapBotTask task = TaskRegistry.Get(kv.Key);
                RecoveryRecord rec = kv.Value;
                if (task == null || task.IsTerminal)
                {
                    Forget(kv.Key);
                    continue;
                }

                // Passive progress tracking (no executor integration needed):
                // any observable change refreshes the stuck clock.
                if (task.State == TaskState.Running)
                {
                    if (task.Progress != rec.LastProgress)
                    {
                        rec.LastProgress = task.Progress;
                        rec.LastProgressChangeMs = nowMs;
                    }
                    if (rec.LastProgressChangeMs < 0) rec.LastProgressChangeMs = nowMs;
                }
                else if (task.State == TaskState.Failed && rec.FailedAtMs < 0)
                {
                    // First observation of a failure (whether recovery- or
                    // externally-initiated): anchor the retry backoff here.
                    rec.FailedAtMs = nowMs;
                }

                RecoveryDecision d = TaskRecoveryPolicy.Decide(task, rec, Probe, nowMs);
                if (d.Action == RecoveryActionType.None)
                {
                    rec.LastReason = null;
                    continue;
                }
                if (Execute(task, d, nowMs)) executed++;
            }
            return executed;
        }

        // Executes one policy decision through the lifecycle's own mutators.
        // Every branch is already idempotent; the lifetime budget is the
        // backstop against ping-pong (retry<->fail oscillation).
        private static bool Execute(CapBotTask task, RecoveryDecision d, int nowMs)
        {
            RecoveryRecord rec;
            lock (m_Lock)
            {
                if (!m_Records.TryGetValue(task.TaskId, out rec)) return false; // dropped mid-tick
                if (rec.Actions >= MaxRecoveryActions && d.Action != RecoveryActionType.Cancel)
                {
                    // Recovery budget exhausted -> abandon once, terminally.
                    d.Action = RecoveryActionType.Cancel;
                    d.Reason = "recovery budget exhausted (" + rec.Actions + " actions): " + d.Reason;
                }
                if (rec.LastActionMs >= 0 && unchecked(nowMs - rec.LastActionMs) < MinRecheckMs) return false;
                rec.LastActionMs = nowMs;
            }

            bool acted;
            switch (d.Action)
            {
                case RecoveryActionType.Retry: acted = task.TryRetry(); break;
                case RecoveryActionType.Pause:
                    acted = task.TryPause();
                    if (acted) rec.PausedForCapability = true;
                    break;
                case RecoveryActionType.Resume:
                    acted = task.TryResume();
                    if (acted) rec.PausedForCapability = false;
                    break;
                case RecoveryActionType.Fail: acted = task.TryFail(d.Reason); break;
                case RecoveryActionType.Cancel: acted = task.TryCancel(d.Reason); break;
                case RecoveryActionType.Expire: acted = task.TryExpire(); break;
                default: acted = false; break;
            }

            lock (m_Lock)
            {
                if (acted)
                {
                    rec.Actions++;
                    if (d.Action == RecoveryActionType.Fail) rec.FailedAtMs = nowMs;
                    else if (d.Action == RecoveryActionType.Retry) rec.FailedAtMs = -1; // next failure re-anchors
                }
                rec.LastReason = acted ? d.Reason : null;
                if (task.IsTerminal) m_Records.Remove(task.TaskId);
            }
            // Recovery outcomes reach the Phase 1 logger through the pluggable
            // hook that RecoveryLogBridge attaches at boot — the manager itself
            // stays pure C# (testable without the game).
            Action<CapBotTask, RecoveryActionType, string, bool> listener;
            lock (m_Lock) listener = m_OnAction;
            if (listener != null) listener(task, d.Action, d.Reason, acted);
            return acted;
        }

        // Attach bookkeeping when a task enters the registry. Bounded: records
        // only exist for registered tasks and are dropped on terminal state.
        public static void Track(CapBotTask task)
        {
            if (task == null) return;
            lock (m_Lock)
            {
                if (m_Records.Count >= MaxTracked) return; // cannot exceed live cap
                RecoveryRecord r;
                if (m_Records.TryGetValue(task.TaskId, out r)) return;
                r = new RecoveryRecord();
                r.LastProgress = task.Progress;
                r.LastProgressChangeMs = TaskClock.NowMs;
                r.NextRecheckMs = TaskClock.NowMs;
                m_Records[task.TaskId] = r;
            }
        }

        public static void Forget(long taskId)
        {
            lock (m_Lock) m_Records.Remove(taskId);
        }

        public static bool IsTracked(long taskId)
        {
            lock (m_Lock) return m_Records.ContainsKey(taskId);
        }

        public static int TrackedCount { get { lock (m_Lock) return m_Records.Count; } }

        // Diagnostic snapshot (bounded). "id|state|actions|lastReason"
        public static List<string> RecoveryStatusLines(int nowMs)
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, RecoveryRecord> kv in m_Records)
                {
                    CapBotTask t = TaskRegistry.Get(kv.Key);
                    string state = t == null ? "?" : t.State.ToString();
                    string reason = kv.Value.LastReason == null ? "-" : kv.Value.LastReason;
                    lines.Add(kv.Key + "|" + state + "|actions=" + kv.Value.Actions + "|" + reason);
                }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Records.Clear();
                m_Probe = NullWorldProbe.Instance;
                m_Enabled = true;
                m_LastTickMs = -1;
            }
            BackoffBaseMs = 2000;
            BackoffMultiplier = 2.0;
        }
    }
}