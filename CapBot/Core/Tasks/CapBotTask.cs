using System;
using System.Collections.Generic;

namespace CapBot.Core.Tasks
{
    // A single unit of future work. Phase 2 scope: lifecycle bookkeeping ONLY —
    // this class never executes gameplay actions and holds no game-object
    // references (target/reference data is stored as opaque strings so nothing
    // here can crash the game or leak Unity objects). Scheduling and execution
    // arrive in later phases and must treat this type as read-only state.
    public sealed class CapBotTask : IEquatable<CapBotTask>
    {
        // ---- Identity (immutable) -----------------------------------------
        public long TaskId { get; private set; }
        public string TaskType { get; private set; }        // e.g. "NAV_ALIGN", "MISSION_REPORT" — static vocabulary, never parsed
        public string SourceDescription { get; private set; } // why this task exists (creation reason, static text)

        // ---- Ownership -----------------------------------------------------
        public string OwnerActorId { get; private set; }    // e.g. "CAPTAIN", "BOT:<playerId>", "HOST"

        // ---- Scheduling inputs (immutable after creation) ------------------
        public int Priority { get; private set; }           // higher = more urgent
        public int MaxRetries { get; private set; }
        public int TimeoutMs { get; private set; }          // -1 = no deadline
        public IReadOnlyList<long> Dependencies { get; private set; } // TaskIds that must reach Completed first

        // ---- Opaque target/reference data (data, never code) --------------
        public string TargetKind { get; private set; }      // e.g. "SECTOR", "MISSION", "COMPONENT" — free-form tag
        public string TargetId { get; private set; }        // opaque reference (game ids as text; treated as untrusted data)

        // ---- Metadata (bounded; values are data, never executed) ----------
        private readonly Dictionary<string, string> m_Metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Metadata { get { return m_Metadata; } }

        // ---- Timestamps (milliseconds from TaskClock) ----------------------
        public int CreatedTimeMs { get; private set; }
        public int StartedTimeMs { get; private set; }      // -1 until first Running entry
        public int CompletedTimeMs { get; private set; }    // -1 until terminal

        // ---- Lifecycle state ----------------------------------------------
        public TaskState State { get; private set; }
        public int RetryCount { get; private set; }
        public string CancellationReason { get; private set; }
        public string FailureReason { get; private set; }

        // ---- Progress -------------------------------------------------------
        private float m_Progress;
        public float Progress { get { return m_Progress; } }

        // Last transition label, for deterministic status reporting.
        public string LastTransition { get; private set; }

        private CapBotTask() { }

        // Factory: validates invariants at the boundary. Returns null (and
        // calls onRejected) instead of throwing on bad input.
        public static CapBotTask Create(
            string taskType,
            string ownerActorId,
            string sourceDescription,
            int priority,
            int maxRetries,
            int timeoutMs,
            string targetKind,
            string targetId,
            IEnumerable<long> dependencies)
        {
            if (string.IsNullOrEmpty(taskType) || taskType.Length > 64) return null;
            if (string.IsNullOrEmpty(ownerActorId) || ownerActorId.Length > 64) return null;
            if (maxRetries < 0 || maxRetries > 10) return null;

            CapBotTask t = new CapBotTask();
            t.TaskId = TaskIds.Next();
            t.TaskType = taskType;
            t.OwnerActorId = ownerActorId;
            t.SourceDescription = sourceDescription ?? string.Empty;
            t.Priority = priority;
            t.MaxRetries = maxRetries;
            t.TimeoutMs = timeoutMs <= 0 ? -1 : timeoutMs;
            t.TargetKind = targetKind ?? string.Empty;
            t.TargetId = targetId ?? string.Empty;
            t.CreatedTimeMs = TaskClock.NowMs;
            t.StartedTimeMs = -1;
            t.CompletedTimeMs = -1;
            t.State = TaskState.Created;
            t.CancellationReason = null;
            t.FailureReason = null;
            t.LastTransition = "Created";

            List<long> deps = new List<long>();
            if (dependencies != null)
            {
                foreach (long d in dependencies)
                {
                    if (d > 0 && !deps.Contains(d)) deps.Add(d);
                    if (deps.Count >= 16) break; // bounded
                }
            }
            t.Dependencies = deps;
            return t;
        }

        // ---- Metadata -------------------------------------------------------
        public bool SetMetadata(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 64 || m_Metadata.Count >= 32) return false;
            m_Metadata[key] = value ?? string.Empty;
            return true;
        }

        public string GetMetadata(string key)
        {
            string v;
            return m_Metadata.TryGetValue(key, out v) ? v : null;
        }

        // ---- Progress -------------------------------------------------------
        public void SetProgress(float value)
        {
            if (value < 0f) value = 0f;
            else if (value > 1f) value = 1f;
            m_Progress = value;
        }

        // ---- Transitions (all idempotent; return false on illegal/no-op) ---
        public bool TryQueue()
        {
            return ApplyTransition(TaskState.Queued, null, null);
        }

        public bool TryStart()
        {
            if (ApplyTransition(TaskState.Running, null, null))
            {
                if (StartedTimeMs < 0) StartedTimeMs = TaskClock.NowMs;
                return true;
            }
            return false;
        }

        public bool TryPause()
        {
            return ApplyTransition(TaskState.Paused, null, null);
        }

        public bool TryResume()
        {
            return ApplyTransition(TaskState.Running, null, null);
        }

        // Idempotent completion: completing twice returns false and leaves state
        // (and CompletedTimeMs) untouched.
        public bool TryComplete()
        {
            return ApplyTransition(TaskState.Completed, null, null);
        }

        public bool TryFail(string reason)
        {
            return ApplyTransition(TaskState.Failed, Truncate(reason), null);
        }

        public bool TryCancel(string reason)
        {
            return ApplyTransition(TaskState.Cancelled, null, Truncate(reason));
        }

        public bool TryExpire()
        {
            return ApplyTransition(TaskState.Expired, null, "timeout");
        }

        // Retries are only legal from Failed, only up to MaxRetries, and move
        // the task back to Queued with counters intact. The failure reason is
        // kept (it documents the last failure); the terminal timestamp is
        // cleared because the task is live again. Never resets StartedTimeMs.
        public bool TryRetry()
        {
            if (State != TaskState.Failed) return false;
            if (RetryCount >= MaxRetries) return false;
            if (ApplyTransition(TaskState.Queued, null, null))
            {
                RetryCount++;
                CompletedTimeMs = -1;
                return true;
            }
            return false;
        }

        // Single mutation chokepoint: checks legality, stamps terminal times,
        // stores terminal reasons, records the transition for status reporting,
        // and notifies the registry so its buckets can never diverge from task
        // state (TaskRegistry is pure C# — safe to reference from here).
        private bool ApplyTransition(TaskState to, string failureReason, string cancelReason)
        {
            TaskState from = State;
            if (!TaskTransitions.CanTransition(from, to)) return false;

            State = to;
            LastTransition = from + "->" + to;
            if (failureReason != null) FailureReason = failureReason;
            if (cancelReason != null) CancellationReason = cancelReason;
            if (to == TaskState.Completed || to == TaskState.Failed || to == TaskState.Cancelled || to == TaskState.Expired)
            {
                if (CompletedTimeMs < 0) CompletedTimeMs = TaskClock.NowMs;
            }
            if (to == TaskState.Running && StartedTimeMs < 0) StartedTimeMs = TaskClock.NowMs;
            TaskRegistry.RecordTransition(this, LastTransition);
            return true;
        }

        public bool IsTerminal
        {
            get { return State == TaskState.Completed || State == TaskState.Cancelled || State == TaskState.Expired; }
        }

        // True when a deadline was set and has elapsed while unfinished.
        public bool IsTimedOut(int nowMs)
        {
            return TimeoutMs > 0 && !IsTerminal && TaskClock.DeltaMs(CreatedTimeMs, nowMs) > TimeoutMs;
        }

        // ---- Identity / equality: TaskId is the sole identity --------------
        public bool Equals(CapBotTask other) { return other != null && TaskId == other.TaskId; }
        public override bool Equals(object obj) { return Equals(obj as CapBotTask); }
        public override int GetHashCode() { return TaskId.GetHashCode(); }
        public override string ToString() { return "#" + TaskId + " " + TaskType + " [" + State + "]"; }

        // Deterministic single-line status for logging / future dashboards.
        public string ToStatusLine(int nowMs)
        {
            string reason = string.Empty;
            if (State == TaskState.Cancelled) reason = " cancel=" + (CancellationReason ?? "unspecified");
            else if (State == TaskState.Failed) reason = " fail=" + (FailureReason ?? "unspecified");
            string progress = State == TaskState.Completed ? "1" : m_Progress.ToString("0.00");
            string retries = RetryCount > 0 ? " retry=" + RetryCount + "/" + MaxRetries : string.Empty;
            return "#" + TaskId + " " + TaskType + " owner=" + OwnerActorId + " pri=" + Priority
                + " state=" + State + " progress=" + progress + retries + reason
                + " age=" + TaskClock.DeltaMs(CreatedTimeMs, nowMs) + "ms";
        }

        private static string Truncate(string s)
        {
            if (s == null) return null;
            return s.Length <= 200 ? s : s.Substring(0, 200);
        }
    }
}