using System;
using System.Collections.Generic;
using CapBot.Core.World;
using CapBot.Core.Tasks;
using CapBot.Core.Captain;
using CapBot.Core.Planning;

namespace CapBot.Core.Adjustment
{
    // ---- Phase 24: adjustment observer (bounded outcome readback) ----
    //
    // The "self-adjustment / re-planning" phase. The P24 research report
    // (documented in docs/ADJUSTMENT_DIRECTOR.md) found NO master-plan
    // mandate in the workspace — the design basis is INFERRED from the
    // deferral trail: "re-planning (re-targeting)" is contractually reserved
    // to later phases by TASK_RECOVERY.md:95-96 while P3 owns every task
    // lifecycle mutation, and the capability covenant leaves no safe new
    // authoring channel (MISSION_WORK_DIRECTOR.md census: ISSUE_MOVE_ORDER
    // already has exactly two authors, P18 + P23). What P24 CAN do under
    // every verified contract is observe the pipeline's outcomes as DATA and
    // emit bounded, recommend-only signals — the same data-layer-before-
    // consumer pattern the tree already shipped (P15 feeds P18, P22 feeds
    // P23); P25 (adaptive learning), P28 (persistence) and P29 (dashboard)
    // are the named downstream consumers.
    //
    // What this observer IS: a bounded deterministic poll of PUBLIC readbacks
    // (TaskRegistry live snapshot; CaptainDirector/PlanningDirector bounded
    // counters) that emits one decision line per detected condition:
    //
    //   ADJ:CHURN  — a live task is Failed or carries retries (retry policy
    //                engaged); the pipeline is struggling with its own work.
    //   ADJ:STARVE — capacity pressure: registry at the live cap, or a
    //                captain/planning authoring refusal or capacity-gate
    //                block increased since the previous readable pass.
    //   ADJ:DRIFT  — the P22 planning director reported premise drift since
    //                the previous readable pass (premise-carrying task
    //                families may be stale — planning-granularity mirror of
    //                the P19 stale-premise screens, now at meta granularity).
    //
    // Every line is a RECOMMENDATION ONLY. Nothing consumes it in this
    // phase; the observer never acts on its own signals.
    //
    // What this observer is NOT (MUST-NOT boundaries — the strictest in the
    // tree, because it watches the systems everyone else must not touch):
    //   - It NEVER authors, queues, pauses, resumes, cancels, retries,
    //     expires or claims any task; it never calls CapBotTask.Create /
    //     Try* / TaskRegistry.Register. TaskRegistry is read through
    //     LiveSnapshot/LiveCount reads ONLY. P3 (recovery) owns every
    //     lifecycle decision — "every auto-resumer owns exactly its own
    //     pauses" and the observer owns no pauses.
    //   - It never calls TaskScheduler, TaskRecoveryManager, TaskExecutor,
    //     ExecutionClaims (beyond its own deny-by-default authority seam),
    //     CapabilityRegistry, DecisionValidator, or the dispatcher. The
    //     director readbacks it polls (CaptainDirector counters,
    //     PlanningDirector counters) are the same public bounded diagnostics
    //     the tests and the P18 calm gate already read (precedent).
    //   - It never mutates another phase's knobs (BackoffBaseMs /
    //     BackoffMultiplier are documented test-settable configuration — no
    //     component anywhere mutates another phase's statics).
    //   - All listener seams in the tree are single-slot and boot-occupied
    //     by LogBridges, so the observer is POLL-based by construction — it
    //     reads counters on its own cadence, never subscribes.
    //   - No LLM anything; no interpreting line text or metadata as
    //     behavior (EXECUTION_SAFETY security posture). Same inputs =>
    //     same signals (test-verified).
    //   - No game/RPC calls, no scene scans, no FindObjectsOfType, no LINQ,
    //     no per-frame work (cadence-gated 5 s).
    //   - No new Harmony patch class (the WorldTick Postfix gains one
    //     guarded block IN PLACE, after the P23 block; 11-class ceiling
    //     kept).
    //   - Fail-safe on stale/missing/never-captured/not-started/future
    //     snapshots (the shared 20 s standard); the first readable pass
    //     arms the baseline only (deltas need a previous pass).
    //   - Multiplayer: never RPCs; deny-by-default authority seam keeps
    //     clients silent.
    //   - Config: NO new SaveValue (the P18/P22/P23 deterministic-director
    //     precedent — always-on by construction).
    public static class AdjustmentDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 5000;              // house decision cadence (P18/P22/P23 parity)
        public const int MaxStaleSnapshotMs = 20000;       // shared freshness standard
        public const int MaxActiveRecords = 8;             // bounded tracked-record set (3 kinds; defensive cap)
        public const int MaxHistory = 16;                  // bounded resolved-record history
        public const int ActiveExpiryMs = 30000;           // condition absent past this => record decays
        public const int AdjustmentRecheckBlockMs = 20000; // re-arm delay between reports of one record
        public const int ChurnRetryThreshold = 1;          // a task with this many retries is "churning"
        public const int MaxPendingLines = 4;              // bounded emission buffer per pass
        public const int MaxChurnTasksPerLine = 3;         // bounded task-id list in a churn line

        public const string TrackIdPrefix = "ADJ:";        // "ADJ:<kind>"
        public const string TrackChurn = "CHURN";          // TrackId "ADJ:CHURN"
        public const string TrackStarve = "STARVE";        // TrackId "ADJ:STARVE"
        public const string TrackDrift = "DRIFT";          // TrackId "ADJ:DRIFT"

        // Public readback record (mirrors PlanningIntent/MissionWorkIntent:
        // public class with public fields, handed out for bounded reads).
        public sealed class AdjustmentRecord
        {
            public readonly string TrackId;          // "ADJ:<kind>"
            public readonly string Kind;
            public readonly int FirstSeenMs;

            public int LastSeenMs;                   // last readable pass with the condition true
            public long UpdateCount;
            public long ReportCount;                 // one-shot reports this record lifetime
            public int LastReportMs = -1;            // -1 = never reported
            public string LastRecommendation;        // recommend-only detail of the last report
            public bool ConditionSeen;               // condition true on the most recent readable pass

            public AdjustmentRecord(string trackId, string kind, int nowMs)
            {
                TrackId = trackId;
                Kind = kind;
                FirstSeenMs = nowMs;
                LastSeenMs = nowMs;
            }
        }

        private sealed class DirectorState
        {
            public readonly Dictionary<string, AdjustmentRecord> Active =
                new Dictionary<string, AdjustmentRecord>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;

            // baseline for counter deltas (armed on the first readable pass)
            public bool HasPrevPass;
            public long PrevCaptainRefused;
            public long PrevCaptainCapped;
            public long PrevPlanCapacityBlocks;
            public long PrevPlanDrift;

            public long Evaluations;
            public long RecordsTracked;
            public long Reports;
            public long RecheckBlocks;
            public long DuplicatesSuppressed;
            public long RecordsExpired;
            public long PlansExpired;
            public long StaleRejections;
            public long UnknownInputPasses;
            public string LastUncertainReason;
            public string LastRecommendation;
        }

        private static readonly DirectorState S = new DirectorState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed) -------------------------------------
        private static Func<bool> m_AuthorityProbe;         // null/fault => no-op evaluation
        private static Func<int> m_NowMsProvider;           // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_OnDecision;         // AdjustmentLogBridge attaches at boot

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_AuthorityProbe = probe; }
        public static void SetNowMsProvider(Func<int> provider) { lock (m_Lock) m_NowMsProvider = provider; }
        public static void SetWorldProvider(Func<WorldSnapshot> provider) { lock (m_Lock) m_WorldProvider = provider; }
        public static void SetDecisionListener(Action<string> listener) { lock (m_Lock) m_OnDecision = listener; }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // ---- readback (diagnostics/tests) ----------------------------------------
        public static int ActiveRecordCount { get { lock (m_Lock) return S.Active.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long EvaluationCount { get { lock (m_Lock) return S.Evaluations; } }
        public static long RecordsTrackedCount { get { lock (m_Lock) return S.RecordsTracked; } }
        public static long ReportCount { get { lock (m_Lock) return S.Reports; } }
        public static long RecheckBlockCount { get { lock (m_Lock) return S.RecheckBlocks; } }
        public static long DuplicatesSuppressedCount { get { lock (m_Lock) return S.DuplicatesSuppressed; } }
        public static long RecordsExpiredCount { get { lock (m_Lock) return S.RecordsExpired; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static long UnknownInputPassCount { get { lock (m_Lock) return S.UnknownInputPasses; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        // The last recommendation emitted (recommend-only posture — data for
        // P25/P28/P29; this observer never acts on it). Null = none yet.
        public static string LastRecommendation { get { lock (m_Lock) return S.LastRecommendation; } }

        // Deterministic lookup by record id (null when absent).
        public static AdjustmentRecord GetRecord(string recordId)
        {
            if (string.IsNullOrEmpty(recordId)) return null;
            lock (m_Lock)
            {
                AdjustmentRecord r;
                return S.Active.TryGetValue(recordId, out r) ? r : null;
            }
        }

        // One bounded diagnostic line per tracked record (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, AdjustmentRecord> kv in S.Active)
                {
                    AdjustmentRecord r = kv.Value;
                    lines.Add("record " + r.TrackId
                        + " upd=" + r.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " reports=" + r.ReportCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (r.ConditionSeen ? " active" : ""));
                }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                lines.Add("adjustment=" + S.Active.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tracked=" + S.RecordsTracked
                    + " reports=" + S.Reports
                    + " recheckBlocks=" + S.RecheckBlocks
                    + " dupSuppressed=" + S.DuplicatesSuppressed);
                lines.Add("expired=" + S.RecordsExpired + " stale=" + S.StaleRejections
                    + " unknownInputs=" + S.UnknownInputPasses
                    + " uncertain=" + (S.LastUncertainReason ?? "-"));
                lines.Add("recommendation=" + (S.LastRecommendation ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Gate order mirrors the P18/P22/P23 house shape:
        // authority (deny-by-default) => cadence => snapshot fail-safe =>
        // bounded rules. Returns the number of NEW lines/reports this pass
        // (0 on every failure path). Never throws. Mutates NO task and
        // authors NOTHING.
        public static int Evaluate(int nowMs)
        {
            Func<bool> auth;
            Func<WorldSnapshot> world;
            lock (m_Lock)
            {
                auth = m_AuthorityProbe;
                world = m_WorldProvider;
            }

            // Deny-by-default authority: no probe / faulting probe => no-op.
            if (auth == null) return 0;
            bool isAuth;
            try { isAuth = auth(); } catch (Exception) { return 0; }
            if (!isAuth) return 0;

            lock (m_Lock)
            {
                if (S.LastEvalMs >= 0 && unchecked(nowMs - S.LastEvalMs) < MinRecheckMs) return 0;
                S.LastEvalMs = nowMs;
            }

            // ---- fail-safe snapshot gate (shared 20 s standard) ---------------
            // The observer reads no world state beyond this gate, but the
            // snapshot's freshness is the precondition for interpreting any
            // pipeline data — stale world means stale outcomes.
            WorldSnapshot snapshot = null;
            if (world != null)
            {
                try { snapshot = world(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured)
            {
                MarkUncertain("no world snapshot captured (fail-safe: no adjustment assessment)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no adjustment assessment)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no adjustment assessment)");
                return 0;
            }

            List<string> pending = new List<string>(MaxPendingLines);
            int reports = 0;

            // ---- inputs (bounded public readbacks only). Read OUTSIDE our
            // lock: these are other classes' thread-safe readbacks and none
            // of them ever takes the Adjustment lock (one-way, deadlock-safe
            // — the P23 readback precedent). Any fault => fail-closed
            // uncertain pass (never signal from a half-read pipeline).
            List<CapBotTask> live;
            long captainRefused;
            long captainCapped;
            long planCapacityBlocks;
            long planDrift;
            try
            {
                live = TaskRegistry.LiveSnapshot();
                captainRefused = CaptainDirector.AuthoringRefusedCount;
                captainCapped = CaptainDirector.AuthoringCappedCount;
                planCapacityBlocks = PlanningDirector.CapacityGateBlockCount;
                planDrift = PlanningDirector.DriftReportCount;
            }
            catch (Exception)
            {
                lock (m_Lock) S.UnknownInputPasses++;
                MarkUncertain("a readback seam faulted (fail-closed: no adjustment assessment)");
                return 0;
            }

            lock (m_Lock)
            {

                // First readable pass arms the baseline only — deltas need a
                // previous pass (level-based churn rules could fire on pass
                // one, but arming quietly keeps the very first readable pass
                // observation-free, the P22 premise-capture analogue).
                if (!S.HasPrevPass)
                {
                    S.PrevCaptainRefused = captainRefused;
                    S.PrevCaptainCapped = captainCapped;
                    S.PrevPlanCapacityBlocks = planCapacityBlocks;
                    S.PrevPlanDrift = planDrift;
                    S.HasPrevPass = true;
                    S.Evaluations++;
                    return 0;
                }

                // ---- rule 1: CHURN ------------------------------------------
                // A live task is Failed or carries retries. RetryCount is
                // immutable-per-retry data owned by P3 recovery; the observer
                // reads it and never drives a retry itself.
                bool churn = false;
                List<string> churnParts = new List<string>(MaxChurnTasksPerLine);
                for (int i = 0; i < live.Count; i++)
                {
                    CapBotTask t = live[i];
                    if (t == null) continue;
                    string reason = null;
                    if (t.State == TaskState.Failed) reason = "failed";
                    else if (t.RetryCount >= ChurnRetryThreshold) reason = "retried";
                    if (reason == null) continue;
                    churn = true;
                    if (churnParts.Count < MaxChurnTasksPerLine)
                        churnParts.Add("#" + t.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + reason);
                }

                // ---- rule 2: STARVE -----------------------------------------
                // Capacity pressure: registry saturated, or an authoring
                // refusal / capacity-gate block appeared since the previous
                // readable pass (counter DELTAS — the counters themselves
                // grow forever, only the delta is a signal).
                bool saturated = TaskRegistry.LiveCount >= TaskRegistry.MaxLiveTasks;
                long refusedDelta = captainRefused - S.PrevCaptainRefused;
                long cappedDelta = captainCapped - S.PrevCaptainCapped;
                long planCapDelta = planCapacityBlocks - S.PrevPlanCapacityBlocks;
                bool starve = saturated || refusedDelta > 0 || cappedDelta > 0 || planCapDelta > 0;

                // ---- rule 3: DRIFT ------------------------------------------
                long driftDelta = planDrift - S.PrevPlanDrift;
                bool drift = driftDelta > 0;

                // ---- bounded rules -> one-shot records -----------------------
                if (churn)
                {
                    string detail = JoinIds(churnParts);
                    reports += ReportConditionLocked(TrackChurn, detail, nowMs, pending);
                }
                if (starve)
                {
                    string detail;
                    if (saturated)
                        detail = "capacity=" + TaskRegistry.LiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + "/" + TaskRegistry.MaxLiveTasks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    else
                        detail = (refusedDelta > 0 ? "captainRefused+" : string.Empty)
                            + (cappedDelta > 0 ? "captainCapped+" : string.Empty)
                            + (planCapDelta > 0 ? "planCapacity+" : string.Empty);
                    reports += ReportConditionLocked(TrackStarve, detail, nowMs, pending);
                }
                if (drift)
                {
                    reports += ReportConditionLocked(TrackDrift,
                        "planningDrift+" + driftDelta.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        nowMs, pending);
                }

                // ---- baseline rollover ---------------------------------------
                S.PrevCaptainRefused = captainRefused;
                S.PrevCaptainCapped = captainCapped;
                S.PrevPlanCapacityBlocks = planCapacityBlocks;
                S.PrevPlanDrift = planDrift;

                // ---- seen-flag sweep -----------------------------------------
                // A record whose condition did NOT fire this pass clears its
                // seen-flag, so the next re-fire is a genuine re-report
                // (rate-limited by the recheck block), never a silent
                // "persisting" refresh. Fired records keep seen=true (set in
                // ReportConditionLocked).
                foreach (KeyValuePair<string, AdjustmentRecord> kv in S.Active)
                {
                    string k = kv.Value.Kind;
                    bool fired = (k == TrackChurn && churn)
                        || (k == TrackStarve && starve)
                        || (k == TrackDrift && drift);
                    if (!fired) kv.Value.ConditionSeen = false;
                }

                // ---- hygiene: expire records whose condition stays absent
                // past ActiveExpiryMs (one-shot expired line, P15/P22/P23
                // mirror). A live condition refreshes LastSeenMs every
                // readable pass and never expires while it persists.
                List<string> expired = new List<string>(MaxActiveRecords);
                foreach (KeyValuePair<string, AdjustmentRecord> kv in S.Active)
                {
                    if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs) expired.Add(kv.Key);
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    AdjustmentRecord expiredRec = S.Active[expired[i]];
                    S.Active.Remove(expired[i]);
                    S.HistoryIds.Enqueue(expired[i]);
                    while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                    S.PlansExpired++;
                    S.RecordsExpired++;
                    reports++;
                    pending.Add("AdjustmentRecordExpired " + expiredRec.TrackId);
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count && i < MaxPendingLines; i++) Emit(pending[i]);
            return reports;
        }

        // ---- rules -------------------------------------------------------------------

        // One-shot report semantics (P15/P22/P23 record mirror):
        //   condition first true  => arm the record + emit the report;
        //   condition stays true  => silent refresh (DuplicatesSuppressed);
        //   condition re-fires after a clear, within the record's lifetime
        //                          => re-report only after
        //                          AdjustmentRecheckBlockMs (anti-churn), else
        //                          RecheckBlocks++ and silent;
        //   condition clears      => hygiene decays the record after
        //                          ActiveExpiryMs; re-firing after decay arms a
        //                          FRESH record (budget re-armed).
        private static int ReportConditionLocked(
            string kind, string detail, int nowMs, List<string> pending)
        {
            string trackId = TrackIdPrefix + kind;
            AdjustmentRecord rec;
            bool fresh = !S.Active.TryGetValue(trackId, out rec);
            if (fresh)
            {
                if (S.Active.Count >= MaxActiveRecords)
                {
                    // Bounded tracked set: shed the oldest by LastSeenMs (tie ->
                    // lowest key order). Defensive-only (3 kinds); house-pattern
                    // parity (P17/P18/P22/P23 mirror).
                    string oldest = null;
                    foreach (KeyValuePair<string, AdjustmentRecord> okv in S.Active)
                    {
                        if (oldest == null
                            || okv.Value.LastSeenMs < S.Active[oldest].LastSeenMs
                            || (okv.Value.LastSeenMs == S.Active[oldest].LastSeenMs
                                && string.CompareOrdinal(okv.Key, oldest) < 0))
                        {
                            oldest = okv.Key;
                        }
                    }
                    if (oldest != null)
                    {
                        S.Active.Remove(oldest);
                        S.HistoryIds.Enqueue(oldest);
                        while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                        S.PlansExpired++;
                    }
                }
                rec = new AdjustmentRecord(trackId, kind, nowMs);
                S.Active[trackId] = rec;
                S.RecordsTracked++;
            }

            bool wasSeen = rec.ConditionSeen;
            rec.ConditionSeen = true;
            rec.LastSeenMs = nowMs;
            rec.UpdateCount++;

            if (!fresh && wasSeen)
            {
                // Condition persisted across passes: silent refresh.
                S.DuplicatesSuppressed++;
                return 0;
            }

            // First report of this record lifetime, or a re-fire after a
            // clear. Re-fire is rate-limited by the recheck block.
            if (!fresh && rec.LastReportMs >= 0
                && unchecked(nowMs - rec.LastReportMs) < AdjustmentRecheckBlockMs)
            {
                S.RecheckBlocks++;
                return 0;
            }

            rec.ReportCount++;
            rec.LastReportMs = nowMs;
            rec.LastRecommendation = detail;
            S.Reports++;
            S.LastRecommendation = "ADJ:" + kind + " " + detail;
            pending.Add("AdjustmentSignal " + TrackIdPrefix + kind
                + " " + detail
                + " (recommend-only; no behavior change in Phase 24)");
            return 1;
        }

        private static string JoinIds(List<string> parts)
        {
            if (parts.Count == 0) return "no detail";
            string joined = string.Empty;
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) joined += ",";
                joined += parts[i];
            }
            return joined;
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("AdjustmentUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Active.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.HasPrevPass = false;
                S.PrevCaptainRefused = 0;
                S.PrevCaptainCapped = 0;
                S.PrevPlanCapacityBlocks = 0;
                S.PrevPlanDrift = 0;
                S.Evaluations = 0;
                S.RecordsTracked = 0;
                S.Reports = 0;
                S.RecheckBlocks = 0;
                S.DuplicatesSuppressed = 0;
                S.RecordsExpired = 0;
                S.PlansExpired = 0;
                S.StaleRejections = 0;
                S.UnknownInputPasses = 0;
                S.LastUncertainReason = null;
                S.LastRecommendation = null;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_OnDecision = null;
            }
        }
    }
}