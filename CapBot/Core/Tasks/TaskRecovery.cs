using System;
using System.Collections.Generic;

namespace CapBot.Core.Tasks
{
    // ---- Phase 3: task recovery -------------------------------------------
    // Recovery decides what may happen to a task that is Failed, stuck,
    // expired, invalidated, interrupted, or blocked — and nothing else.
    // It is a POLICY layer on top of the Phase 2 lifecycle: every mutation
    // still flows through CapBotTask's idempotent Try* transitions, so any
    // illegal or repeated recovery action is rejected by the lifecycle itself.
    //
    // Research constraints honored (PULSAR_GAMEAI_RESEARCH.md):
    //  - pure C#: the world is observed ONLY through ITaskWorldProbe, so
    //    recovery re-derives intent from current state and never stores or
    //    depends on transient vanilla AI internals (paths, Behave tree state,
    //    host-migration-surviving state — none of which survive migration).
    //  - no new per-frame navigation decision system: the intended driver
    //    cadence is ~1 s (Tick), matching vanilla's 1-1.5 s decision gates.
    //  - no PULSAR/PML APIs are invented or called in this phase.

    public enum RecoveryActionType
    {
        None = 0,
        Retry = 1,    // Failed -> Queued (bounded: MaxRetries + backoff delay)
        Pause = 2,    // Running -> Paused (capability temporarily unavailable)
        Resume = 3,   // Paused -> Running (capability available again)
        Fail = 4,     // -> Failed (stuck or interrupted execution)
        Cancel = 5,   // terminal abandonment (recovery impossible)
        Expire = 6    // terminal (deadline elapsed)
    }

    // Answers recovery questions from CURRENT authoritative world state.
    // The Phase 6 (Game/World State) implementation must be cheap, host-side,
    // non-throwing, and must never serve stale answers across sector changes.
    // Recovery caches no world state itself — every answer is treated as the
    // current truth, which is what makes recovery safe after host migration.
    public interface ITaskWorldProbe
    {
        bool TargetValid(CapBotTask task);          // false: target gone/invalid
        bool OwnerAvailable(string ownerActorId);   // false: owner interrupted/lost
        bool CapabilityAvailable(CapBotTask task);  // false: capability temporarily down
        bool WorldInvalidatesTask(CapBotTask task); // true: premise removed by a world change
    }

    // Neutral probe used when no real probe is attached (Phase 3 ships none —
    // nothing routes gameplay through the task system yet). Makes Tick(null)
    // safe and keeps the manager correct-but-inert until Phase 6.
    public sealed class NullWorldProbe : ITaskWorldProbe
    {
        public static readonly NullWorldProbe Instance = new NullWorldProbe();
        private NullWorldProbe() { }
        public bool TargetValid(CapBotTask task) { return true; }
        public bool OwnerAvailable(string ownerActorId) { return true; }
        public bool CapabilityAvailable(CapBotTask task) { return true; }
        public bool WorldInvalidatesTask(CapBotTask task) { return false; }
    }

    // Per-task recovery bookkeeping. Lives only while the task is live; the
    // manager drops it as soon as the task reaches a terminal state.
    internal sealed class RecoveryRecord
    {
        public float LastProgress;
        public int LastProgressChangeMs;
        public int NextRecheckMs;
        public int LastActionMs = -1;
        public int FailedAtMs = -1;   // recovery timeline anchor for retry backoff
        public int Actions;
        public string LastReason;
        public bool PausedForCapability;
    }

    internal struct RecoveryDecision
    {
        public RecoveryActionType Action;
        public string Reason;
    }

    // Pure decision function: (task, record, probe, now) -> decision.
    // Deterministic: no clock reads, no side effects, first matching rule
    // wins — so at most one recovery action is chosen per task per Tick
    // (idempotent by construction). The manager executes; this only decides.
    internal static class TaskRecoveryPolicy
    {
        private const int StuckThresholdMs = TaskRecoveryManager.StuckThresholdMs;
        private const int PausedAbandonMs = TaskRecoveryManager.PausedAbandonMs;

        public static RecoveryDecision Decide(CapBotTask task, RecoveryRecord rec, ITaskWorldProbe probe, int nowMs)
        {
            // 1) Deadline elapsed while unfinished -> hard expire. A deadline
            //    is a constraint, not a suggestion; expired tasks are never
            //    retried (TimeoutMs is immutable, so a retry could not honor it).
            if (task.IsTimedOut(nowMs))
                return Dec(RecoveryActionType.Expire, "deadline elapsed (timeoutMs=" + task.TimeoutMs + ")");

            // 2) A world change removed the task's premise -> abandon. The
            //    task cannot be re-derived toward its old goal.
            if (probe.WorldInvalidatesTask(task))
                return Dec(RecoveryActionType.Cancel, "world state invalidated the task premise");

            // 3) Target lost/invalid -> abandon. Re-deriving the task toward
            //    a NEW target is re-planning (later phases), not recovery.
            if (!probe.TargetValid(task))
                return Dec(RecoveryActionType.Cancel, "target invalid or lost");

            // 4) Owner unavailable (interrupted execution / ownership loss).
            if (!probe.OwnerAvailable(task.OwnerActorId))
            {
                if (task.State == TaskState.Running)
                    return Dec(RecoveryActionType.Fail, "owner unavailable: " + task.OwnerActorId);
                if (task.State == TaskState.Paused)
                    return Dec(RecoveryActionType.Cancel, "owner unavailable while paused: " + task.OwnerActorId);
                // Created/Queued: no execution to interrupt. Failed: retry
                // policy below requeues; reassignment is the executor's job.
            }

            // 5) Capability temporarily unavailable -> pause instead of fail;
            //    the work is still valid, only the means are missing.
            if (!probe.CapabilityAvailable(task))
            {
                if (task.State == TaskState.Running)
                    return Dec(RecoveryActionType.Pause, "capability temporarily unavailable");
                if (task.State == TaskState.Paused && rec.PausedForCapability
                    && rec.LastActionMs >= 0 && unchecked(nowMs - rec.LastActionMs) > PausedAbandonMs)
                    return Dec(RecoveryActionType.Cancel, "capability unavailable too long (> " + PausedAbandonMs + "ms)");
                // Queued/Created: not executing, nothing to pause.
            }
            else if (task.State == TaskState.Paused && rec.PausedForCapability)
            {
                // 6) A recovery-initiated pause ends as soon as the capability
                //    returns. Externally-initiated pauses (flag unset) belong
                //    to whoever paused them and are not auto-resumed.
                return Dec(RecoveryActionType.Resume, "capability available again");
            }

            // 7) Stuck: Running with no observable progress change for longer
            //    than the threshold. Progress observation is passive (the
            //    manager compares task.Progress each Tick), so stuck detection
            //    needs no executor integration.
            if (task.State == TaskState.Running && unchecked(nowMs - rec.LastProgressChangeMs) > StuckThresholdMs)
                return Dec(RecoveryActionType.Fail, "stuck: no progress for " + StuckThresholdMs + "ms");

            // 8) Failed-task recovery: bounded, backoff-gated retry, or
            //    abandonment once retries are exhausted.
            if (task.State == TaskState.Failed)
            {
                if (task.RetryCount >= task.MaxRetries)
                    return Dec(RecoveryActionType.Cancel,
                        "retries exhausted (" + task.RetryCount + "/" + task.MaxRetries + "): " + (task.FailureReason ?? "unspecified"));
                int delay = TaskRecoveryManager.BackoffDelayMs(task.RetryCount);
                // Backoff anchor is the recovery record's own timeline (the
                // moment the manager observed the latest failure), not the
                // wall-clock terminal stamp: Tick callers may drive recovery
                // with their own virtual/offset time, and mixing anchors
                // would make the delay meaningless. Externally-failed tasks
                // anchor at first observation.
                int sinceFail = rec.FailedAtMs < 0 ? int.MaxValue : unchecked(nowMs - rec.FailedAtMs);
                if (sinceFail < delay) return NoDec(); // waiting out the backoff
                return Dec(RecoveryActionType.Retry,
                    "retry " + (task.RetryCount + 1) + "/" + task.MaxRetries + " after backoff " + delay + "ms; last failure: " + (task.FailureReason ?? "unspecified"));
            }

            return NoDec();
        }

        private static RecoveryDecision Dec(RecoveryActionType action, string reason)
        {
            RecoveryDecision d; d.Action = action; d.Reason = reason; return d;
        }

        private static RecoveryDecision NoDec()
        {
            RecoveryDecision d; d.Action = RecoveryActionType.None; d.Reason = null; return d;
        }
    }
}