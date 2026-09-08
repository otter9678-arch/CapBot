using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Capabilities;

namespace CapBot.Core.Navigation
{
    // ---- Phase 14: navigation recovery director ---------------------------------
    //
    // Bounded, deterministic NAVIGATION RECOVERY director. When the Phase 6
    // world snapshot shows a recoverable navigation degeneracy, the director
    // produces bounded recovery plans as DATA: a plan is either a queued
    // NAV_RECOVERY task routed through the standard pipeline (scheduler P4 ->
    // claims P5 -> capability validation P7 -> executor P8) or a report-only
    // record. The director NEVER executes a capability, never RPCs, never
    // touches the vanilla navigation stack (PLFlightAI/PLBotController/
    // PLStarmap/m_ShipCourseGoals are untouched — the verified course-goal RPC
    // channels are reached only through the already-registered P7 capabilities).
    //
    // Rules (all require current-terrain certainty; unknown data never triggers):
    //   CourseLost   — no course goals, not in warp, current sector known,
    //                  persisted >= CourseLostDwellMs -> ADD_COURSE_GOAL task
    //                  re-affirming the CURRENT sector (vanilla-friendly
    //                  re-arm; no invented destination).
    //   GoalReached  — first course goal equals the current sector, not in
    //                  warp, persisted >= GoalDwellMs -> REMOVE_COURSE_GOAL
    //                  task for that goal (course hygiene; vanilla pathing
    //                  keeps serving stale goals otherwise).
    //   StuckStall   — vanilla bot-controller stuck signature (moved < 1 m
    //                  in 5 s while seeking > 7 s, research §3.4) WITH an
    //                  active course -> REPORT-ONLY plan. Vanilla owns
    //                  physical unsticking (stuck-teleport); the director
    //                  coordinates, never moves the ship.
    //
    // Boundaries (Phase 14 contract):
    //   - Plans are DATA + task creation through TaskRegistry only. No task is
    //     claimed, executed, paused, cancelled, or retried here — the executor
    //     says HOW, the scheduler says WHEN, recovery owns failures, and each
    //     system resumes only its own pauses.
    //   - Deny-by-default authority seam: no probe / faulting probe /
    //     non-master -> no evaluation (clients never produce recovery plans).
    //   - Fail-safe on stale/missing/never-captured/not-started snapshots
    //     (same 20 s window as the P9 director): no decisions on uncertainty.
    //   - All timestamps are explicit nowMs values (TaskClock semantics); no
    //     wall-clock reads, no LINQ, no per-frame work (cadence-gated 5 s,
    //     called from the WorldTick driver after the emergency director).
    //   - Every collection is bounded; every rule input validated (unknown
    //     sentinels NaN/-1/null can never trigger a rule).
    public enum NavRule
    {
        CourseLost = 1,
        GoalReached = 2,
        StuckStall = 3,
    }

    // One bounded recovery-plan record. Created and mutated only by the
    // director (inside its lock). Data only.
    public sealed class NavPlanRecord
    {
        public readonly string PlanId;          // "NAV:<rule>:S<sectorId>"
        public readonly NavRule Rule;
        public readonly int SectorId;           // -1 = rule not sector-scoped (never used today)
        public readonly int FirstSeenMs;        // first observation of the condition

        public long TaskId;                     // 0 = no task ever issued (report-only plans stay 0)
        public int TaskResolvedMs;              // -1 = no resolved task yet (re-arm blocker)
        public int LastSeenMs;                  // last pass where the condition persisted
        public long UpdateCount;

        public NavPlanRecord(string planId, NavRule rule, int sectorId, int nowMs)
        {
            PlanId = planId;
            Rule = rule;
            SectorId = sectorId;
            FirstSeenMs = nowMs;
            TaskId = 0;
            TaskResolvedMs = -1;
            LastSeenMs = nowMs;
        }

        // True while a task this plan created is still live (registered and
        // non-terminal). Report-only plans never have a live task.
        public bool HasLiveTask { get { return TaskId > 0 && TaskResolvedMs < 0; } }
    }

    public static class NavigationRecoveryDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 5000;          // decision cadence (1 Hz snapshot, 5 s decisions)
        public const int MaxActivePlans = 8;           // bounded active plan set
        public const int MaxHistory = 16;              // bounded resolved-plan history
        public const int RecoveryTaskTimeoutMs = 120000; // recovery tasks self-expire (bounded work)
        public const int CourseLostDwellMs = 10000;    // empty course must persist this long before re-arm
        public const int GoalDwellMs = 15000;          // reached goal must persist this long before removal
        public const int RequeueBlockMs = 20000;       // re-arm delay after a task resolves for its plan
        public const int ActiveExpiryMs = 30000;       // un-reconfirmed plan decays after this
        public const int MaxStaleSnapshotMs = 20000;   // fail-safe: no decisions on older snapshots
        public const int RecoveryPriority = 20;        // above normal work (1..8 + aging), below emergencies (110+)

        public const string TaskTypeRecovery = "NAV_RECOVERY"; // static vocabulary, never parsed
        public const string OwnerCaptain = "CAPTAIN";  // matches the P7 navigation capability owners
        public const string TargetKindSector = "SECTOR";

        // Vanilla stuck thresholds (PULSAR_GAMEAI_RESEARCH §3.4, VERIFIED).
        public const float StuckDistMovedMeters = 1f;
        public const float StuckTimeSeekingSec = 7f;

        private sealed class DirectorState
        {
            public readonly Dictionary<string, NavPlanRecord> Plans =
                new Dictionary<string, NavPlanRecord>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;
            public long Evaluations;
            public long TasksCreated;
            public long ReportsRecorded;
            public long CourseLostReports;
            public long DuplicatesSuppressed;
            public long PlansExpired;
            public long TaskResolutions;
            public long StaleRejections;
            public string LastUncertainReason;
        }

        private static readonly DirectorState S = new DirectorState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed) -------------------------------------
        private static Func<bool> m_AuthorityProbe;         // null/fault => no-op evaluation
        private static Func<int> m_NowMsProvider;           // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_OnDecision;         // NavigationLogBridge attaches at boot

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
        public static int ActivePlanCount { get { lock (m_Lock) return S.Plans.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long EvaluationCount { get { lock (m_Lock) return S.Evaluations; } }
        public static long TasksCreatedCount { get { lock (m_Lock) return S.TasksCreated; } }
        public static long ReportsRecordedCount { get { lock (m_Lock) return S.ReportsRecorded; } }
        public static long CourseLostReportCount { get { lock (m_Lock) return S.CourseLostReports; } }
        public static long DuplicatesSuppressedCount { get { lock (m_Lock) return S.DuplicatesSuppressed; } }
        public static long PlansExpiredCount { get { lock (m_Lock) return S.PlansExpired; } }
        public static long TaskResolutionCount { get { lock (m_Lock) return S.TaskResolutions; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        // Deterministic lookup by plan id (null when absent).
        public static NavPlanRecord GetPlan(string planId)
        {
            if (string.IsNullOrEmpty(planId)) return null;
            lock (m_Lock)
            {
                NavPlanRecord p;
                return S.Plans.TryGetValue(planId, out p) ? p : null;
            }
        }

        // One bounded diagnostic line per active plan (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, NavPlanRecord> kv in S.Plans)
                {
                    NavPlanRecord p = kv.Value;
                    lines.Add("plan " + p.PlanId
                        + " rule=" + p.Rule
                        + " sector=" + p.SectorId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " task=" + (p.TaskId > 0
                            ? p.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : "-")
                        + " resolved=" + (p.TaskResolvedMs >= 0
                            ? p.TaskResolvedMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : "-")
                        + " upd=" + p.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                lines.Add("navPlans=" + S.Plans.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tasks=" + S.TasksCreated
                    + " reports=" + S.ReportsRecorded + " courseLostReports=" + S.CourseLostReports
                    + " dupSuppressed=" + S.DuplicatesSuppressed
                    + " expired=" + S.PlansExpired + " resolutions=" + S.TaskResolutions);
                lines.Add("stale=" + S.StaleRejections
                    + " uncertain=" + (S.LastUncertainReason ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Pulls the authoritative snapshot through the world seam (production:
        // WorldStateService.Latest) so the caller stays clock-only. Returns the
        // number of NEW recovery tasks created this pass (0 on the quiet path AND
        // on every failure path). Never throws.
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

            // ---- fail-safe snapshot gate ------------------------------------
            WorldSnapshot snapshot = null;
            if (world != null)
            {
                try { snapshot = world(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured)
            {
                MarkUncertain("no world snapshot captured (fail-safe: no navigation recovery)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no navigation recovery)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no navigation recovery)");
                return 0;
            }

            // ---- rule inputs (unknown data can never trigger a rule) --------
            NavigationSnapshot nav = snapshot.Navigation;
            if (nav == null)
            {
                MarkUncertain("navigation section missing (fail-safe: no navigation recovery)");
                return 0;
            }
            int courseCount = nav.CourseGoals != null ? nav.CourseGoals.Count : 0;
            bool inWarp = nav.InWarp;
            int currentSectorId = nav.CurrentSectorId;

            List<string> pending = new List<string>(2);
            int created = 0;

            lock (m_Lock)
            {
                // ---- hygiene: expire plans whose condition stopped persisting ----
                List<string> expired = new List<string>(MaxActivePlans);
                foreach (KeyValuePair<string, NavPlanRecord> kv in S.Plans)
                {
                    if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs) expired.Add(kv.Key);
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    NavPlanRecord expiredPlan = S.Plans[expired[i]];
                    S.Plans.Remove(expired[i]);
                    S.HistoryIds.Enqueue(expired[i]);
                    while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                    S.PlansExpired++;
                    pending.Add("NavPlanExpired " + expired[i]);
                }

                // ---- Rule 1: CourseLost ------------------------------------------
                // No course goals, not in warp, current sector known, persisted.
                //
                // P39 ROOT-CAUSE FIX (live evidence: tasks #4..#256 alternated
                // ADD_COURSE_GOAL sector=0 / REMOVE_COURSE_GOAL sector=0 for a
                // whole session): re-affirming the CURRENT sector as a course
                // goal cannot change the condition this rule detects — the
                // goal-count stays 0 vs 1 only until Rule 2 (GoalReached,
                // firstGoal == currentSector) removes it again. The two rules
                // formed an ADD/REMOVE oscillator with zero net world effect
                // (~125 task cycles live). CourseLost is therefore REPORT-ONLY:
                // a plan record + one bounded diagnostic (same shape as
                // StuckStall). Only GoalReached removal of genuinely stale
                // goals (first goal != current sector... impossible here by
                // rule; i.e. goals for OTHER sectors) still tasks.
                if (courseCount == 0 && !inWarp && currentSectorId >= 0)
                {
                    string planId = "NAV:CourseLost:S" + currentSectorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    NavPlanRecord plan = GetOrRefreshPlanLocked(planId, NavRule.CourseLost, currentSectorId, nowMs);
                    plan.UpdateCount++;
                    if (plan.TaskId == 0 && plan.UpdateCount == 1)
                    {
                        // First observation: bounded report line (per-plan once;
                        // re-report only after the plan expires and re-opens).
                        S.ReportsRecorded++;
                        S.CourseLostReports++;
                        pending.Add("NavCourseLostReport " + planId
                            + " (report-only: re-affirming the current sector as a goal is a no-op —"
                            + " it is immediately 'reached' and removed; vanilla starmap owns unprompted routing)");
                    }
                    // Re-observations (UpdateCount > 1) are silent: the plan
                    // record already carries the condition, and re-reporting
                    // would flood the log exactly like the old task loop did.
                }

                // ---- Rule 2: GoalReached ------------------------------------------
                // First course goal equals the current sector, not in warp,
                // persisted (hysteresis: transient equality never removes goals).
                if (courseCount > 0 && !inWarp && currentSectorId >= 0)
                {
                    int firstGoal = nav.CourseGoals[0];
                    if (firstGoal >= 0 && firstGoal == currentSectorId)
                    {
                        string planId = "NAV:GoalReached:S" + firstGoal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        NavPlanRecord plan = GetOrRefreshPlanLocked(planId, NavRule.GoalReached, firstGoal, nowMs);
                        if (!plan.HasLiveTask
                            && unchecked(nowMs - plan.FirstSeenMs) >= GoalDwellMs
                            && (plan.TaskResolvedMs < 0 || unchecked(nowMs - plan.TaskResolvedMs) >= RequeueBlockMs))
                        {
                            long taskId = CreateRecoveryTaskLocked(plan, RegisteredCapabilities.RemoveCourseGoal,
                                firstGoal, "course goal reached; remove stale goal",
                                nowMs, pending);
                            if (taskId > 0) created++;
                        }
                        else if (plan.HasLiveTask)
                        {
                            S.DuplicatesSuppressed++;
                        }
                    }
                }

                // ---- Rule 3: StuckStall (REPORT-ONLY) ------------------------------
                // Vanilla stuck signature WITH an active course. No task: the
                // vanilla stuck-teleport owns physical unsticking; this
                // coordinates (bounded record + diagnostics) only.
                if (courseCount > 0 && !inWarp
                    && nav.HasBotNavigationMetrics
                    && !float.IsNaN(nav.DistMovedInLast5s) && !float.IsNaN(nav.TimeSeekingTargetSec)
                    && nav.DistMovedInLast5s < StuckDistMovedMeters
                    && nav.TimeSeekingTargetSec > StuckTimeSeekingSec)
                {
                    string planId = "NAV:Stuck:S" + (currentSectorId >= 0
                        ? currentSectorId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "unknown");
                    NavPlanRecord plan = GetOrRefreshPlanLocked(planId, NavRule.StuckStall, currentSectorId, nowMs);
                    plan.UpdateCount++;
                    if (plan.TaskId == 0 && plan.UpdateCount == 1)
                    {
                        // First observation of this stall: bounded report line.
                        S.ReportsRecorded++;
                        pending.Add("NavStallReport " + planId
                            + " moved=" + nav.DistMovedInLast5s.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " seeking=" + nav.TimeSeekingTargetSec.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " (report-only; vanilla owns unsticking)");
                    }
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count; i++) Emit(pending[i]);
            return created;
        }

        // ---- reconcile pass -----------------------------------------------------------
        //
        // Resolves active plans whose task reached a terminal state or vanished
        // from the registry. Bounded, snapshot-free; called by the driver after
        // Evaluate. The task itself is NEVER touched (recovery owns lifecycle) —
        // the plan only records the resolution and re-arms after RequeueBlockMs.
        public static void ReconcileTasks(int nowMs)
        {
            List<KeyValuePair<string, string>> resolutions = new List<KeyValuePair<string, string>>(MaxActivePlans);
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, NavPlanRecord> kv in S.Plans)
                {
                    NavPlanRecord p = kv.Value;
                    if (p.TaskId <= 0 || p.TaskResolvedMs >= 0) continue;
                    CapBotTask t;
                    try { t = TaskRegistry.Get(p.TaskId); }
                    catch (Exception) { t = null; }
                    if (t == null)
                    {
                        p.TaskResolvedMs = nowMs;
                        p.UpdateCount++;
                        S.TaskResolutions++;
                        resolutions.Add(new KeyValuePair<string, string>(kv.Key, "vanished"));
                    }
                    else if (t.IsTerminal)
                    {
                        p.TaskResolvedMs = nowMs;
                        p.UpdateCount++;
                        S.TaskResolutions++;
                        resolutions.Add(new KeyValuePair<string, string>(kv.Key, t.State.ToString()));
                    }
                }
            }
            for (int i = 0; i < resolutions.Count; i++)
            {
                Emit("NavPlanTaskResolved " + resolutions[i].Key + " (" + resolutions[i].Value + ")");
            }
        }

        // ---- plan bookkeeping (callers hold m_Lock) -----------------------------------
        private static NavPlanRecord GetOrRefreshPlanLocked(string planId, NavRule rule, int sectorId, int nowMs)
        {
            NavPlanRecord plan;
            if (!S.Plans.TryGetValue(planId, out plan))
            {
                if (S.Plans.Count >= MaxActivePlans)
                {
                    // Bounded active set: shed the oldest plan by LastSeenMs
                    // (tie -> lowest key order). Bounded scan (<= 8).
                    string oldest = null;
                    foreach (KeyValuePair<string, NavPlanRecord> kv in S.Plans)
                    {
                        if (oldest == null
                            || kv.Value.LastSeenMs < S.Plans[oldest].LastSeenMs
                            || (kv.Value.LastSeenMs == S.Plans[oldest].LastSeenMs
                                && string.CompareOrdinal(kv.Key, oldest) < 0))
                        {
                            oldest = kv.Key;
                        }
                    }
                    if (oldest != null)
                    {
                        S.Plans.Remove(oldest);
                        S.HistoryIds.Enqueue(oldest);
                        while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                        S.PlansExpired++;
                        Emit("NavPlanShed " + oldest + " (active set full)");
                    }
                }
                plan = new NavPlanRecord(planId, rule, sectorId, nowMs);
                S.Plans[planId] = plan;
                Emit("NavPlanOpened " + planId);
            }
            plan.LastSeenMs = nowMs;
            return plan;
        }

        // Creates + registers + queues one recovery task (callers hold m_Lock).
        // Deterministic shape: CAPTAIN-owned NAV_RECOVERY task with the P7
        // navigation capability bound via metadata. Returns the task id
        // (0 = refused — register/queue failures fail safe, no retry storm:
        // the plan stays open and the next dwell window re-arms).
        private static long CreateRecoveryTaskLocked(NavPlanRecord plan, string capabilityId, int sectorId, string reason, int nowMs, List<string> pending)
        {
            string sectorText = sectorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CapBotTask task = CapBotTask.Create(
                TaskTypeRecovery,
                OwnerCaptain,
                "navigation recovery: " + reason,
                RecoveryPriority,
                1,                       // one retry — recovery may retry once; never a storm
                RecoveryTaskTimeoutMs,
                TargetKindSector,
                sectorText,
                null);
            if (task == null) return 0;

            if (!task.SetMetadata("NavId", plan.PlanId)) return 0;
            if (!task.SetMetadata("NavRule", plan.Rule.ToString())) return 0;
            if (!task.SetMetadata("Preemptible", "true")) return 0;
            if (!task.SetMetadata("CapabilityId", capabilityId)) return 0;
            if (!task.SetMetadata("Argument", sectorText)) return 0;

            if (!TaskRegistry.Register(task)) return 0;
            if (!task.TryQueue()) return task.TaskId; // registered but queue refused — lifecycle logged it

            plan.TaskId = task.TaskId;
            plan.UpdateCount++;
            S.TasksCreated++;
            pending.Add("NavRecoveryTaskCreated #" + task.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + plan.PlanId + " cap=" + capabilityId + " sector=" + sectorText);
            return task.TaskId;
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("NavRecoveryUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Plans.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.Evaluations = 0;
                S.TasksCreated = 0;
                S.ReportsRecorded = 0;
                S.CourseLostReports = 0;
                S.DuplicatesSuppressed = 0;
                S.PlansExpired = 0;
                S.TaskResolutions = 0;
                S.StaleRejections = 0;
                S.LastUncertainReason = null;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_OnDecision = null;
            }
        }
    }
}