using System;
using System.Collections.Generic;

namespace CapBot.Core.Tasks
{
    // ---- Phase 4: task scheduler (orchestration ONLY) -----------------------
    // Selects and orders EXISTING registered tasks for future execution. This
    // is not a brain and not an executor: it runs no gameplay code, writes no
    // nav fields, touches no vanilla priority/behavior-tree system, and makes
    // no LLM decisions. Its entire output is a deterministic, bounded set of
    // per-tick grants (TaskGrant) that the future capability/executor systems
    // (P7/P8) may consume; nothing is coupled into those systems here.
    //
    // Design contracts honored:
    //  - Phase 2 lifecycle is the single mutator: the scheduler drives state
    //    ONLY through CapBotTask's Try* transitions (Queued->Running via
    //    explicit request, Paused on preemption). No direct State writes.
    //  - Phase 3 recovery owns stuck/failed/timeout handling: the scheduler
    //    never retries, never expires, never fails a task; it simply refuses
    //    to schedule tasks recovery has parked (backoff, capability-pause) by
    //    consulting the same public knobs (state, dependencies, deadline).
    //  - Phase 6 supplies world truth: scheduling is gated on task-level
    //    facts only (state/priority/deps/owner/deadline); target validity
    //    stays the probe's job. The scheduler keeps no world state.
    //  - Research cadence (PULSAR_GAMEAI_RESEARCH.md §11): the Tick driver
    //    (P8, host-side) decides cadence; the scheduler enforces a 1 s
    //    global gate so even a per-frame caller yields ~1 s policy, and a
    //    per-tick grant budget bounds work per pass. No per-frame loop is
    //    created here.
    //  - Master-client authority: the scheduler itself is pure bookkeeping;
    //    the P8 driver must gate Tick on PhotonNetwork.isMasterClient.
    //
    // Bounded by construction: leases ≤ live cap, preemption records ≤ live
    // cap, grants per tick ≤ MaxGrantsPerTick, zero growth without a
    // corresponding registered task. No LINQ in the per-tick path.
    public static class TaskScheduler
    {
        public const int MaxGrantsPerTick = 8;      // work handed out per pass
        public const int LeaseDurationMs = 5000;    // grant exclusivity window
        public const int PreemptMargin = 2;         // incoming must beat running by > this
        public const int PreemptMinRunMs = 3000;    // grace before a running task may be preempted
        public const int PreemptPauseMs = 15000;    // preemption-pause window before re-evaluation
        public const int MaxPreemptionsPerTask = 2; // lifetime cap per task
        public const int AgingGainMs = 30000;       // +1 effective priority per this many ms waiting
        public const int MaxAgingBonus = 5;         // bounded aging (no unbounded priority inflation)
        private const int MinRecheckMs = 1000;      // global re-schedule gate

        private static readonly Dictionary<long, Lease> m_Leases = new Dictionary<long, Lease>();
        private static readonly Dictionary<long, PreemptionRecord> m_Preemptions = new Dictionary<long, PreemptionRecord>();
        private static readonly object m_Lock = new object();
        private static Action<string> m_OnDecision;    // SchedulerLogBridge attaches at boot
        private static int m_LastTickMs = -1;
        private static bool m_Enabled = true;

        private sealed class Lease
        {
            public string Owner;
            public int ExpiresAtMs;
        }

        private sealed class PreemptionRecord
        {
            public int PausedAtMs;
            public int PreemptedByPriority;
            public int Count;              // lifetime preemptions suffered
            public bool ByScheduler;       // true while the scheduler's pause is active (auto-resume eligible); false = dormant lifetime counter only
        }

        // ---- configuration (bounded, test-settable) ------------------------
        public static bool Enabled
        {
            get { lock (m_Lock) return m_Enabled; }
            set { lock (m_Lock) m_Enabled = value; }
        }

        // Diagnostic hook: one line per scheduler decision (grant/refuse/
        // preempt). Fired outside the lock; must never throw.
        public static void SetDecisionListener(Action<string> listener)
        {
            lock (m_Lock) m_OnDecision = listener;
        }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // ---- Public surface (for the future executor, P8) -------------------
        // A grant is a suggestion to start work, not an execution order: the
        // executor must still route the task's intent through verified game
        // surfaces (P7 capability registry), and claim via TryTakeLease.
        public struct TaskGrant
        {
            public long TaskId;
            public string TaskType;
            public string OwnerActorId;
            public int Priority;
            public int ExpiresAtMs;
        }

        // Number of tasks currently granted (bounded by live cap).
        public static int ActiveGrantCount { get { lock (m_Lock) return m_Leases.Count; } }

        // Exclusive claim of a previously granted task. The future executor
        // calls this right before moving the task Queued->Running; double
        // claims (or claims on expired leases) return false. On success the
        // lease is consumed so exactly one executor pass can start the task.
        public static bool TryTakeLease(long taskId, int nowMs)
        {
            Lease l;
            lock (m_Lock)
            {
                if (!m_Leases.TryGetValue(taskId, out l)) return false;
                if (unchecked(nowMs - l.ExpiresAtMs) >= 0)
                {
                    m_Leases.Remove(taskId);
                    return false;
                }
                m_Leases.Remove(taskId);
            }
            Emit("GrantLeaseTaken #" + taskId);
            return true;
        }

        // True while a task holds an unexpired lease (diagnostics/tests).
        public static bool HasLease(long taskId)
        {
            lock (m_Lock)
            {
                Lease l;
                return m_Leases.TryGetValue(taskId, out l);
            }
        }

        // ---- Tick ------------------------------------------------------------
        // One scheduling pass. Deterministic: same snapshot -> same grants.
        // Returns the number of grants issued this pass.
        public static int Tick(int nowMs)
        {
            if (!Enabled) return 0;
            if (m_LastTickMs >= 0 && unchecked(nowMs - m_LastTickMs) < MinRecheckMs) return 0;
            m_LastTickMs = nowMs;

            ExpireStaleLeases(nowMs);
            PrunePreemptionRecords();
            EvaluatePreemptions(nowMs);

            // Candidate pool: Queued tasks only. (Running tasks are already
            // executing; Created tasks belong to their creators; Paused/
            // Failed tasks belong to recovery. Terminal tasks are gone from
            // the registry's live set.)
            List<CapBotTask> candidates = null;
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            foreach (CapBotTask t in live)
            {
                if (t.State != TaskState.Queued) continue;
                if (HasLease(t.TaskId)) continue;                 // duplicate-scheduling guard
                if (candidates == null) candidates = new List<CapBotTask>();
                candidates.Add(t);
            }

            // Deterministic order: effective priority desc (aging-bounded),
            // then FCFS (earlier CreatedTimeMs first), then TaskId asc.
            if (candidates != null) SortCandidates(candidates, nowMs);

            // Owners with Running tasks, computed once per pass. Grants are
            // 5 s bookkeeping, execution is not — an owner mid-execution is
            // busy regardless of lease expiry, and otherwise the pass after
            // a grant would re-flood the same owner. Same-owner queued work
            // resolves through the preemption pass, not this gate.
            List<string> busyOwners = null;
            foreach (CapBotTask t in live)
            {
                if (t.State != TaskState.Running) continue;
                if (!ContainsOwner(busyOwners, t.OwnerActorId))
                {
                    if (busyOwners == null) busyOwners = new List<string>();
                    busyOwners.Add(t.OwnerActorId);
                }
            }

            int granted = 0;
            if (candidates != null)
            {
                foreach (CapBotTask t in candidates)
                {
                    if (granted >= MaxGrantsPerTick) break;

                    // Deadline respect: tasks whose deadline already elapsed
                    // are skipped (recovery's SweepExpired owns expiring them;
                    // the scheduler just never schedules them).
                    if (t.IsTimedOut(nowMs)) { Emit("Refuse #" + t.TaskId + " deadline elapsed"); continue; }

                    // NOTE: no retry-headroom gate — a Queued task always has
                    // a pending attempt. Retry accounting is recovery's job
                    // (it abandons exhausted tasks by cancelling them from
                    // Failed, and those never appear here); a Queued task
                    // with RetryCount == MaxRetries is the legal final-retry
                    // attempt and must stay schedulable (a gate here would
                    // also have refused every MaxRetries=0 task forever).

                    // Dependency gate: every dependency TaskId must resolve
                    // to a Completed task (registry Get covers history).
                    if (!DependenciesSatisfied(t)) { Emit("Refuse #" + t.TaskId + " dependencies unmet"); continue; }

                    // One grant per owner per pass (starvation guard at the
                    // owner level; work stealing across owners is P8 policy).
                    if (OwnerHasGrant(t.OwnerActorId, nowMs) || ContainsOwner(busyOwners, t.OwnerActorId))
                    { Emit("Refuse #" + t.TaskId + " owner busy: " + t.OwnerActorId); continue; }

                    // P45 bounded wait (directive B item 10): an owner whose
                    // agent is SPAWNING/TEMP_UNAVAILABLE must NOT receive a
                    // dispatch — the task stays Queued (wait, not spin). This
                    // is a defer, never a retry: no counters move, no lease is
                    // consumed, one deduped diagnostic per owner+presence.
                    CapBot.Core.Crew.AgentPresenceState ownerPresence = CapBot.Core.Crew.CrewAgentRegistry.PresenceForOwner(t.OwnerActorId);
                    if (ownerPresence == CapBot.Core.Crew.AgentPresenceState.Spawning || ownerPresence == CapBot.Core.Crew.AgentPresenceState.TempUnavailable)
                    {
                        EmitBoundedOwnerWait(t.OwnerActorId, ownerPresence);
                        continue;
                    }

                    Grant(t, nowMs);
                    granted++;
                }
            }

            // Preemption pass (explicitly policy-gated; see TryPreempt). The
            // scheduler-level gates still apply — a task the scheduler would
            // refuse (deadline, dependencies) must never displace running
            // work either, and already-granted candidates are skipped so a
            // grant is never issued twice in one pass. The owner-busy gate
            // is deliberately NOT applied here: a queued candidate blocked
            // by its owner's running task is exactly the case preemption
            // exists for; TryPreempt itself refuses when the owner holds a
            // lease on something OTHER than the victim.
            if (candidates != null && granted < MaxGrantsPerTick)
            {
                foreach (CapBotTask t in candidates)
                {
                    if (granted >= MaxGrantsPerTick) break;
                    if (HasLease(t.TaskId)) continue;
                    if (t.IsTimedOut(nowMs)) continue;
                    if (!DependenciesSatisfied(t)) continue;
                    if (TryPreempt(t, nowMs)) granted++;
                }
            }

            return granted;
        }

        // P45: deduped bounded-wait diagnostic — one line per owner+presence
        // until the state changes (no per-pass log storm).
        private static readonly Dictionary<string, string> m_LastWaitEmit = new Dictionary<string, string>(StringComparer.Ordinal);
        private static void EmitBoundedOwnerWait(string ownerActorId, CapBot.Core.Crew.AgentPresenceState presence)
        {
            string tag = presence.ToString();
            string last;
            lock (m_Lock)
            {
                if (m_LastWaitEmit.TryGetValue(ownerActorId, out last) && last == tag) return;
                m_LastWaitEmit[ownerActorId] = tag;
            }
            Emit("OwnerBoundedWait owner=" + ownerActorId + " presence=" + tag + " action=deferred (task stays Queued)");
        }

        private static void SortCandidates(List<CapBotTask> list, int nowMs)
        {
            // Insertion sort: pool is ≤ live cap (64), already nearly sorted
            // by TaskId between ticks; stable, allocation-free, deterministic.
            for (int i = 1; i < list.Count; i++)
            {
                CapBotTask key = list[i];
                int j = i - 1;
                while (j >= 0 && Compare(key, list[j], nowMs) > 0)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = key;
            }
        }

        // Positive when a sorts BEFORE b (higher scheduling precedence).
        private static int Compare(CapBotTask a, CapBotTask b, int nowMs)
        {
            int pa = EffectivePriority(a, nowMs);
            int pb = EffectivePriority(b, nowMs);
            if (pa != pb) return pa > pb ? 1 : -1;             // higher effective priority first
            if (a.CreatedTimeMs != b.CreatedTimeMs) return a.CreatedTimeMs < b.CreatedTimeMs ? 1 : -1; // FCFS: earlier first
            return b.TaskId > a.TaskId ? 1 : (b.TaskId < a.TaskId ? -1 : 0); // tie-break: lower id first
        }

        // Bounded aging: +1 effective priority per AgingGainMs waited, capped.
        // Supports priority reordering WITHOUT unbounded inflation: a task
        // that waited 2.5 minutes gains at most +5.
        private static int EffectivePriority(CapBotTask t, int nowMs)
        {
            int waited = unchecked(nowMs - t.CreatedTimeMs);
            if (waited < 0) waited = 0;
            int bonus = waited / AgingGainMs;
            if (bonus > MaxAgingBonus) bonus = MaxAgingBonus;
            return t.Priority + bonus;
        }

        private static bool DependenciesSatisfied(CapBotTask t)
        {
            IReadOnlyList<long> deps = t.Dependencies;
            for (int i = 0; i < deps.Count; i++)
            {
                CapBotTask dep = TaskRegistry.Get(deps[i]);
                if (dep == null || dep.State != TaskState.Completed) return false;
            }
            return true;
        }

        private static bool OwnerHasGrant(string owner, int nowMs)
        {
            lock (m_Lock)
            {
                foreach (Lease l in m_Leases.Values)
                {
                    if (l.Owner == owner && unchecked(nowMs - l.ExpiresAtMs) < 0) return true;
                }
            }
            return false;
        }

        // True when the owner appears in the per-pass busy list (has a task
        // currently Running). Running is reachable only through Queued ->
        // grant -> executor start, so this cannot permanently wedge anything:
        // completion/failure/pause all clear it.
        private static bool ContainsOwner(List<string> list, string owner)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == owner) return true;
            }
            return false;
        }

        // Owner-grant check with an exclusion: true when the owner holds an
        // unexpired lease on any task other than `exceptTaskId`. Used by the
        // preemption pass, whose own victim lease is the exception.
        private static bool OwnerHasGrant(string owner, long exceptTaskId, int nowMs)
        {
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, Lease> kv in m_Leases)
                {
                    if (kv.Key == exceptTaskId) continue;
                    if (kv.Value.Owner == owner && unchecked(nowMs - kv.Value.ExpiresAtMs) < 0) return true;
                }
            }
            return false;
        }

        private static void Grant(CapBotTask t, int nowMs)
        {
            lock (m_Lock) m_Leases[t.TaskId] = new Lease { Owner = t.OwnerActorId, ExpiresAtMs = nowMs + LeaseDurationMs };
            Emit("Granted #" + t.TaskId + " " + t.TaskType + " owner=" + t.OwnerActorId
                + " pri=" + t.Priority + " effPri=" + EffectivePriority(t, nowMs) + " leaseMs=" + LeaseDurationMs);
        }

        private static void ExpireStaleLeases(int nowMs)
        {
            List<long> stale = null;
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, Lease> kv in m_Leases)
                {
                    if (unchecked(nowMs - kv.Value.ExpiresAtMs) >= 0)
                    {
                        if (stale == null) stale = new List<long>();
                        stale.Add(kv.Key);
                    }
                }
                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++) m_Leases.Remove(stale[i]);
                }
            }
            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++) Emit("LeaseExpired #" + stale[i]);
            }
        }

        // Drops preemption records whose task no longer exists in the live
        // registry so the bounded dictionary holds nothing without a live
        // task (Get covers live set only — a task still in history was
        // already terminal, and terminal victims never resume). Records for
        // tasks still live but non-Paused stay: their Count is the LIFETIME
        // preemption tally (a task preempted twice that later completed
        // must not have its history erased by being retried/started again).
        private static void PrunePreemptionRecords()
        {
            List<long> dead = null;
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, PreemptionRecord> kv in m_Preemptions)
                {
                    bool liveHere = false;
                    List<CapBotTask> live = TaskRegistry.LiveSnapshot();
                    for (int i = 0; i < live.Count; i++)
                    {
                        if (live[i].TaskId == kv.Key) { liveHere = true; break; }
                    }
                    if (!liveHere)
                    {
                        if (dead == null) dead = new List<long>();
                        dead.Add(kv.Key);
                    }
                }
                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++) m_Preemptions.Remove(dead[i]);
                }
            }
        }

        // ---- Preemption (explicit policy only) -------------------------------
        // A Queued task may displace a Running task ONLY when the running
        // task is explicitly marked preemptible via metadata ("Preemptible"
        // = "true"), the queued task's effective priority exceeds the
        // running task's by more than PreemptMargin, the running task has
        // had at least PreemptMinRunMs to do useful work, and it has not
        // already been preempted MaxPreemptionsPerTask times. The displaced
        // task is Paused (its only legal exit besides completion/failure);
        // only the scheduler's own preemption-pauses are auto-resumed here.
        // Additionally the candidate's owner must not hold a lease on a
        // DIFFERENT task — preemption exists to resolve a candidate vs. its
        // own owner's running task, not to queue-jump behind other grants.
        private static bool TryPreempt(CapBotTask candidate, int nowMs)
        {
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            CapBotTask victim = null;
            for (int i = 0; i < live.Count; i++)
            {
                CapBotTask t = live[i];
                if (t.State != TaskState.Running) continue;
                if (t.OwnerActorId != candidate.OwnerActorId) continue; // same-owner displacement only
                if (t.GetMetadata("Preemptible") != "true") continue;   // explicit policy gate
                PreemptionRecord rec;
                lock (m_Lock)
                {
                    if (m_Preemptions.TryGetValue(t.TaskId, out rec) && rec.Count >= MaxPreemptionsPerTask) continue;
                }
                if (EffectivePriority(candidate, nowMs) <= EffectivePriority(t, nowMs) + PreemptMargin) continue;
                if (t.StartedTimeMs < 0 || unchecked(nowMs - t.StartedTimeMs) < PreemptMinRunMs) continue;
                if (victim == null || EffectivePriority(t, nowMs) < EffectivePriority(victim, nowMs)) victim = t;
            }
            if (victim == null) return false;
            // The owner must hold no lease on a DIFFERENT task: preemption
            // resolves a candidate against its own owner's running work and
            // never queue-jumps behind other grants. Other Running tasks of
            // the same owner are allowed — the lowest effective-priority
            // victim is chosen among them.
            if (OwnerHasGrant(candidate.OwnerActorId, victim.TaskId, nowMs)) return false;

            if (!victim.TryPause()) { Emit("PreemptRefused #" + victim.TaskId + " lifecycle rejected pause"); return false; }
            lock (m_Lock)
            {
                PreemptionRecord rec;
                if (!m_Preemptions.TryGetValue(victim.TaskId, out rec))
                {
                    rec = new PreemptionRecord();
                    m_Preemptions[victim.TaskId] = rec;
                }
                rec.PausedAtMs = nowMs;
                rec.PreemptedByPriority = EffectivePriority(candidate, nowMs);
                rec.Count++;
                rec.ByScheduler = true;
            }
            Emit("Preempted #" + victim.TaskId + " (runPri=" + EffectivePriority(victim, nowMs) + ") by #" + candidate.TaskId
                + " (queuePri=" + EffectivePriority(candidate, nowMs) + ", margin>" + PreemptMargin + ")");
            Grant(candidate, nowMs);
            return true;
        }

        // Scheduler-initiated preemption pauses auto-resume when the
        // preemption window elapsed and the displayer priority is no longer
        // dominant. Externally-initiated pauses are never touched (Phase 3
        // contract: recovery resumes only capability-pauses; the scheduler
        // resumes only its own preemption-pauses).
        private static void EvaluatePreemptions(int nowMs)
        {
            List<KeyValuePair<long, PreemptionRecord>> due = null;
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, PreemptionRecord> kv in m_Preemptions)
                {
                    if (!kv.Value.ByScheduler) continue;
                    if (unchecked(nowMs - kv.Value.PausedAtMs) < PreemptPauseMs) continue;
                    if (due == null) due = new List<KeyValuePair<long, PreemptionRecord>>();
                    due.Add(kv);
                }
            }
            if (due == null) return;
            foreach (KeyValuePair<long, PreemptionRecord> kv in due)
            {
                CapBotTask t = TaskRegistry.Get(kv.Key);
                if (t == null || t.State != TaskState.Paused)
                {
                    lock (m_Lock) m_Preemptions.Remove(kv.Key);
                    continue;
                }
                if (t.TryResume())
                {
                    lock (m_Lock)
                    {
                        // Demote to a dormant record: the Count survives as
                        // the task's lifetime preemption tally, but this
                        // pause is no longer the scheduler's to resume —
                        // keeping ByScheduler=true would let a later pass
                        // auto-resume a pause some other system took in
                        // between (it must own only ITS OWN pauses).
                        kv.Value.ByScheduler = false;
                    }
                    Emit("PreemptResume #" + t.TaskId);
                }
            }
        }

        // Diagnostic snapshot (bounded): "id|leaseOwner|expiresInMs" /
        // "id|preempted xN". Sorted ordinal.
        public static List<string> SchedulerStatusLines(int nowMs)
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, Lease> kv in m_Leases)
                {
                    int remain = unchecked(kv.Value.ExpiresAtMs - nowMs);
                    lines.Add(kv.Key + "|lease|" + kv.Value.Owner + "|" + remain + "ms");
                }
                foreach (KeyValuePair<long, PreemptionRecord> kv in m_Preemptions)
                {
                    lines.Add(kv.Key + "|preempted x" + kv.Value.Count);
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
                m_Leases.Clear();
                m_Preemptions.Clear();
                m_LastWaitEmit.Clear();
                m_OnDecision = null;
                m_LastTickMs = -1;
                m_Enabled = true;
            }
        }

        // Phase 26: authority-loss surface for the MultiplayerAuthorityMonitor.
        // Leases and preemption records are volatile scheduler bookkeeping
        // (grants are 5 s bookkeeping — P4 contract); on authority loss the
        // process must not keep suggesting execution for a state it no longer
        // owns. The task registry itself is untouched (lifecycle is not
        // authority-volatile: P3 owns it). Returns the number of leases
        // dropped. Never throws.
        public static int ClearForAuthorityLoss()
        {
            try
            {
                int dropped;
                lock (m_Lock)
                {
                    dropped = m_Leases.Count;
                    m_Leases.Clear();
                    m_Preemptions.Clear();
                }
                if (dropped > 0)
                {
                    lock (m_Lock) m_LastWaitEmit.Clear();
                    Emit("SchedulerLeasesClearedAuthorityLost leases=" + dropped);
                }
                return dropped;
            }
            catch (Exception) { return 0; }
        }
    }
}