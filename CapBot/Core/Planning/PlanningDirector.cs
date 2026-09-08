using System;
using System.Collections.Generic;
using CapBot.Core.World;
using CapBot.Core.Tasks;
using CapBot.Core.Emergency;
using CapBot.Core.Navigation;
using CapBot.Core.Combat;

namespace CapBot.Core.Planning
{
    // ---- Phase 22: planning director (deterministic situation assessment) ----
    //
    // The first stage of the planning arc the contracts sketched: P6's header
    // names "planning" as an intended snapshot consumer (WORLD_STATE.md), and
    // the P4/P3/P10 explicitly-not lists all defer "dynamic planning" to
    // later phases (TASK_SCHEDULER.md:185, TASK_RECOVERY.md:132,
    // CREW_AGENTS.md:221). No master-plan document exists in the workspace
    // (verified in the Phase 21 research pass; the P22 research report
    // confirmed the same deferral trail), so the P22 shape is INFERRED from
    // those contracts and documented in docs/PLANNING_DIRECTOR.md. P23
    // (dynamic task creation) builds on this layer.
    //
    // What this director IS: a bounded deterministic CONSUMER of the P6 world
    // snapshot that (a) tracks planning-relevant situations as data and
    // (b) emits bounded decision lines. Two rules:
    //
    //   1. PREMISE_DRIFT — the planning-granularity mirror of the P19 stale-
    //      premise screens ("stale premise: sector changed"/"stale premise:
    //      in warp"): a premise snapshot (sector id + InWarp) captured when
    //      the episode arms is compared against the current snapshot every
    //      readable pass; divergence emits PlanningPremiseDrift and re-arms
    //      with the fresh premise. Premises that authored tasks
    //      (CAPTAIN_DELIB, NAV_RECOVERY) rely on are exactly these two fields
    //      — catching their expiry at planning granularity, before the queue
    //      is even screened, is the deterministic planning layer's
    //      contribution. P19 keeps full ownership of its per-queued-task
    //      screens (P22 never calls it).
    //
    //   2. MISSIONWORK episode — the trigger surface P23 will author tasks
    //      from: an incomplete active mission exists (bounded scan, first
    //      MaxMissionsInScope entries), the ship is calm (same gate family as
    //      P18: no P9 emergency, no P14 plan, no P17 combat record, no
    //      hostiles/boarders/warp, known sector) and the tracked task set
    //      has spare live-capacity headroom (P4's MaxLiveTasks cap: registering
    //      beyond it returns null, so the planner must react to pressure, not
    //      retry-storm). Calm + capacity => episode opens (exactly one
    //      PlanningIntentOpened line); any calm/capacity loss keeps the
    //      episode open but silent until conditions return. The episode is
    //      DATA ONLY — no task is authored in Phase 22.
    //
    // What this director is NOT (MUST-NOT boundaries, mirroring P20/P21):
    //   - It NEVER authors, queues, pauses, resumes, cancels, or claims any
    //     task; it never calls CapBotTask.Create / Try* / TaskRegistry.Register.
    //     TaskRegistry is read through the LiveCount counter only.
    //   - It never calls TaskScheduler, TaskRecoveryManager, ExecutionClaims
    //     (beyond its own deny-by-default authority seam), CapabilityRegistry,
    //     DecisionValidator, CaptainDirector, or CrewAgentRegistry assignment
    //     APIs. Readbacks it does consume are the same public counters the
    //     P18 calm gate already reads (precedent).
    //   - No LLM anything (advisors P20/P21 are data-only log lines; planning
    //     logic never consumes them). Same-snapshot => same decisions.
    //   - No game/RPC calls, no scene scans, no FindObjectsOfType, no LINQ,
    //     no per-frame work (cadence-gated 5 s).
    //   - No new Harmony patch class (the WorldTick Postfix gains one guarded
    //     block IN PLACE, after the P21 advisor block; 11-class ceiling kept).
    //   - Fail-safe on stale/missing/never-captured/not-started/future
    //     snapshots (the shared 20 s standard); unknown data never triggers
    //     (unknown-sentinel accounting).
    //   - Multiplayer: never RPCs; deny-by-default authority seam keeps
    //     clients silent.
    public static class PlanningDirector
    {
        // ---- bounds + cadence -----------------------------------------------
        public const int MinRecheckMs = 5000;             // house decision cadence (P18 parity)
        public const int MaxStaleSnapshotMs = 20000;      // shared freshness standard
        public const int MaxActiveSituations = 8;         // bounded tracked-situation set
        public const int MaxHistory = 16;                 // bounded resolved-situation history
        public const int ActiveExpiryMs = 30000;          // trigger absent past this => record decays
        public const int DriftRecheckBlockMs = 15000;     // re-arm delay after a premise-drift report
        public const int MaxMissionsInScope = 8;          // mission-scan bound (snapshot caps Missions at 16)
        public const int MaxPendingLines = 4;             // bounded emission buffer per pass

        public const string TrackIdPrefix = "PLAN:";      // "PLAN:<kind>"
        public const string TrackMissionWork = "MISSIONWORK"; // TrackId "PLAN:MISSIONWORK"
        public const string TrackPremiseDrift = "PREMISE_DRIFT";

        // Public readback record (mirrors CaptainIntent/CombatRecord:
        // public class with public fields, handed out for bounded reads).
        public sealed class PlanningIntent
        {
            public readonly string TrackId;          // "PLAN:<kind>"
            public readonly string Kind;
            public readonly int FirstSeenMs;

            public int LastSeenMs;                   // last pass with a readable trigger
            public long UpdateCount;
            public bool OpenedReported;              // PlanningIntentOpened fired
            public int DriftReports;                 // premise-drift reports this record lifetime

            public PlanningIntent(string trackId, string kind, int nowMs)
            {
                TrackId = trackId;
                Kind = kind;
                FirstSeenMs = nowMs;
                LastSeenMs = nowMs;
            }
        }

        // A captured planning premise: the two snapshot fields every
        // premise-carrying task family (CAPTAIN_DELIB, NAV_RECOVERY) relies on.
        public sealed class PlanningPremise
        {
            public readonly int SectorId;            // -1 = unknown (never armed on unknown)
            public readonly bool InWarp;

            public PlanningPremise(int sectorId, bool inWarp)
            {
                SectorId = sectorId;
                InWarp = inWarp;
            }

            public bool IsUnknown { get { return SectorId < 0; } }
        }

        private sealed class DirectorState
        {
            public readonly Dictionary<string, PlanningIntent> Active =
                new Dictionary<string, PlanningIntent>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;

            // premise-drift episode state
            public bool PremiseOpen;                 // episode armed with a captured premise
            public int PremiseFirstSeenMs = -1;      // when the current premise was captured
            public PlanningPremise Premise;          // the captured premise (null = not armed)
            public int LastDriftReportMs = -1;       // re-arm blocker after a drift report

            // mission-work episode state (single PLAN:MISSIONWORK record)
            public bool EpisodeOpen;
            public int EpisodeFirstSeenMs = -1;

            public long Evaluations;
            public long IntentsTracked;
            public long OpenedReports;
            public long DriftReports;
            public long DriftRecheckBlocks;
            public long CalmGateBlocks;
            public long CapacityGateBlocks;
            public long DuplicatesSuppressed;
            public long IntentsExpired;
            public long PlansExpired;
            public long StaleRejections;
            public long UnknownInputPasses;
            public string LastUncertainReason;
        }

        private static readonly DirectorState S = new DirectorState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed) -------------------------------------
        private static Func<bool> m_AuthorityProbe;         // null/fault => no-op evaluation
        private static Func<int> m_NowMsProvider;           // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_OnDecision;         // PlanningLogBridge attaches at boot

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
        public static int ActiveIntentCount { get { lock (m_Lock) return S.Active.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long EvaluationCount { get { lock (m_Lock) return S.Evaluations; } }
        public static long IntentsTrackedCount { get { lock (m_Lock) return S.IntentsTracked; } }
        public static long OpenedReportCount { get { lock (m_Lock) return S.OpenedReports; } }
        public static long DriftReportCount { get { lock (m_Lock) return S.DriftReports; } }
        public static long DriftRecheckBlockCount { get { lock (m_Lock) return S.DriftRecheckBlocks; } }
        public static long CalmGateBlockCount { get { lock (m_Lock) return S.CalmGateBlocks; } }
        public static long CapacityGateBlockCount { get { lock (m_Lock) return S.CapacityGateBlocks; } }
        public static long DuplicatesSuppressedCount { get { lock (m_Lock) return S.DuplicatesSuppressed; } }
        public static long IntentsExpiredCount { get { lock (m_Lock) return S.IntentsExpired; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static long UnknownInputPassCount { get { lock (m_Lock) return S.UnknownInputPasses; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        // The currently captured planning premise (null = none armed). A copy —
        // the caller can never mutate director state through it.
        public static PlanningPremise CapturedPremise
        {
            get
            {
                lock (m_Lock)
                {
                    return S.Premise == null
                        ? null
                        : new PlanningPremise(S.Premise.SectorId, S.Premise.InWarp);
                }
            }
        }

        // Deterministic lookup by situation id (null when absent).
        public static PlanningIntent GetIntent(string intentId)
        {
            if (string.IsNullOrEmpty(intentId)) return null;
            lock (m_Lock)
            {
                PlanningIntent r;
                return S.Active.TryGetValue(intentId, out r) ? r : null;
            }
        }

        // One bounded diagnostic line per tracked situation (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, PlanningIntent> kv in S.Active)
                {
                    PlanningIntent r = kv.Value;
                    lines.Add("intent " + r.TrackId
                        + " upd=" + r.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (r.OpenedReported ? " opened" : "")
                        + " drift=" + r.DriftReports.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                lines.Add("planning=" + S.Active.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tracked=" + S.IntentsTracked
                    + " opened=" + S.OpenedReports + " drift=" + S.DriftReports
                    + " driftBlocks=" + S.DriftRecheckBlocks
                    + " calmBlocked=" + S.CalmGateBlocks
                    + " capBlocked=" + S.CapacityGateBlocks
                    + " dupSuppressed=" + S.DuplicatesSuppressed);
                lines.Add("expired=" + S.IntentsExpired + " stale=" + S.StaleRejections
                    + " unknownInputs=" + S.UnknownInputPasses
                    + " uncertain=" + (S.LastUncertainReason ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Gate order mirrors CaptainDirector.Evaluate (P18 house shape):
        // authority (deny-by-default) => cadence => snapshot fail-safe =>
        // bounded rules. Returns the number of NEW lines/reports this pass
        // (0 on every failure path). Never throws. Authors NOTHING.
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
            WorldSnapshot snapshot = null;
            if (world != null)
            {
                try { snapshot = world(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured)
            {
                MarkUncertain("no world snapshot captured (fail-safe: no planning assessment)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no planning assessment)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no planning assessment)");
                return 0;
            }

            List<string> pending = new List<string>(MaxPendingLines);
            int reports = 0;

            lock (m_Lock)
            {
                // ---- inputs ------------------------------------------------
                NavigationSnapshot navigation = snapshot.Navigation;
                IReadOnlyList<MissionSnapshot> missions = snapshot.Missions;

                bool inWarp = navigation != null && navigation.InWarp;
                int currentSectorId = navigation != null ? navigation.CurrentSectorId : -1;
                int hostileCount = snapshot.Threats != null ? snapshot.Threats.KnownHostileShipIds.Count : 0;
                int invaders = snapshot.Threats != null ? snapshot.Threats.InvadersOnboardCount : -1;

                // ---- rule 1: PREMISE_DRIFT ----------------------------------
                reports += EvaluatePremiseDrift(
                    currentSectorId, inWarp, nowMs, pending);

                // ---- rule 2: MISSIONWORK episode -----------------------------
                // First incomplete, unended mission (bounded scan). Unknown
                // sentinel data (-1 counts) never triggers.
                MissionSnapshot workMission = null;
                int scanned = 0;
                for (int i = 0; missions != null && i < missions.Count && i < WorldSnapshot.MaxMissions && scanned < MaxMissionsInScope; i++)
                {
                    MissionSnapshot m = missions[i];
                    if (m == null) continue;
                    scanned++;
                    if (m.Ended || m.Abandoned || m.MissionTypeId < 0) continue;
                    if (m.TotalObjectives <= 0 || m.CompletedObjectives < 0
                        || m.CompletedObjectives >= m.TotalObjectives) continue;
                    workMission = m;
                    break;
                }

                if (workMission != null)
                {
                    EnsureIntentTracked(TrackIdPrefix + TrackMissionWork, TrackMissionWork, nowMs);
                    bool capacityAvailable = TaskRegistry.LiveCount < TaskRegistry.MaxLiveTasks;
                    if (!capacityAvailable) S.CapacityGateBlocks++;

                    if (!S.EpisodeOpen)
                    {
                        S.EpisodeOpen = true;
                        S.EpisodeFirstSeenMs = nowMs;
                    }
                    bool dwellMet = unchecked(nowMs - S.EpisodeFirstSeenMs) >= MinRecheckMs;
                    bool calm = IsCalm(inWarp, currentSectorId, hostileCount, invaders);
                    if (!calm) S.CalmGateBlocks++;

                    PlanningIntent workRec = S.Active[TrackIdPrefix + TrackMissionWork];
                    if (dwellMet && calm && capacityAvailable)
                    {
                        workRec.LastSeenMs = nowMs;
                        if (workRec.OpenedReported)
                        {
                            // Live record refresh — pure data bookkeeping; the
                            // episode stays open (P23's authoring window).
                            S.DuplicatesSuppressed++;
                        }
                        else
                        {
                            workRec.OpenedReported = true;
                            S.OpenedReports++;
                            reports++;
                            pending.Add("PlanningIntentOpened " + TrackIdPrefix + TrackMissionWork
                                + " mission=" + workMission.MissionTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + " objectives=" + workMission.CompletedObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + "/" + workMission.TotalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + " capacity=" + TaskRegistry.LiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + "/" + TaskRegistry.MaxLiveTasks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + " (calm; data only — no task authored in Phase 22)");
                        }
                    }
                }
                else
                {
                    // Trigger gone: close the episode. The tracked record
                    // itself survives (hygiene owns expiry); only the episode
                    // state resets.
                    if (S.EpisodeOpen)
                    {
                        S.EpisodeOpen = false;
                        S.EpisodeFirstSeenMs = -1;
                    }
                }

                // ---- hygiene: expire situations whose trigger stays absent
                // past ActiveExpiryMs (one-shot expired report, P15/P16/P17
                // mirror). A LIVE situation refreshes every readable pass and
                // never expires while its trigger remains.
                List<string> expired = new List<string>(MaxActiveSituations);
                foreach (KeyValuePair<string, PlanningIntent> kv in S.Active)
                {
                    if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs) expired.Add(kv.Key);
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    PlanningIntent expiredRec = S.Active[expired[i]];
                    S.Active.Remove(expired[i]);
                    S.HistoryIds.Enqueue(expired[i]);
                    while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                    S.PlansExpired++;
                    S.IntentsExpired++;
                    reports++;
                    pending.Add("PlanningIntentExpired " + expiredRec.TrackId);
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count && i < MaxPendingLines; i++) Emit(pending[i]);
            return reports;
        }

        // ---- rules -------------------------------------------------------------------

        // Premise capture + drift: the episode arms with the CURRENT premise
        // (sector + warp) on the first readable pass; every later readable pass
        // compares against the captured premise. Divergence => drift report +
        // re-arm (the premise is re-captured so the next comparison is fresh).
        // After a drift report, DriftRecheckBlockMs gates further reports
        // (anti-churn); the premise is still re-captured (a second divergence
        // within the window is a legitimate new planning fact — only the
        // REPORT is rate-limited). Warp END is not drift: arriving where the
        // premise expected resolves it; warp START is drift. Unknown sentinels
        // never arm the premise and never fire a comparison.
        private static int EvaluatePremiseDrift(
            int currentSectorId, bool inWarp, int nowMs, List<string> pending)
        {
            if (!S.PremiseOpen)
            {
                if (currentSectorId < 0) return 0; // unknown sector: nothing to arm
                S.PremiseOpen = true;
                S.PremiseFirstSeenMs = nowMs;
                S.Premise = new PlanningPremise(currentSectorId, inWarp);
                return 0;
            }

            // Unknown current data: comparison is uncertain, nothing fires.
            if (currentSectorId < 0) { S.UnknownInputPasses++; return 0; }

            bool drifted = currentSectorId != S.Premise.SectorId
                || (inWarp && !S.Premise.InWarp);

            if (!drifted)
            {
                // No drift: quiet. The drift record is NOT refreshed here —
                // its trigger is the drift EVENT (one-shot report semantics,
                // P15/P16/P17 mirror), not a persistent condition, so the
                // record decays via hygiene ActiveExpiryMs after the last
                // actual report.
                return 0;
            }

            bool blocked = S.LastDriftReportMs >= 0
                && unchecked(nowMs - S.LastDriftReportMs) < DriftRecheckBlockMs;
            if (blocked)
            {
                S.DriftRecheckBlocks++;
                S.Premise = new PlanningPremise(currentSectorId, inWarp);
                S.PremiseFirstSeenMs = nowMs;
                return 0;
            }

            string drift = "sector " + S.Premise.SectorId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "->" + currentSectorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!S.Premise.InWarp && inWarp) drift += " warp=true";

            S.DriftReports++;
            S.LastDriftReportMs = nowMs;
            S.Premise = new PlanningPremise(currentSectorId, inWarp);
            S.PremiseFirstSeenMs = nowMs;

            // Track the drift as a situation record (bounded set).
            EnsureIntentTracked(TrackIdPrefix + TrackPremiseDrift, TrackPremiseDrift, nowMs);
            PlanningIntent rec = S.Active[TrackIdPrefix + TrackPremiseDrift];
            rec.DriftReports++;
            rec.LastSeenMs = nowMs;
            rec.UpdateCount++;

            pending.Add("PlanningPremiseDrift " + TrackIdPrefix + TrackPremiseDrift
                + " " + drift
                + " (premise of queued premise-carrying tasks may be stale)");
            return 1;
        }

        // The P18 calm-gate family, mirrored exactly: no P9 emergency, no P14
        // active plan, no P17 combat record, no snapshot hostiles/boarders,
        // not in warp, sector known. Fail-closed: any faulting readback blocks.
        private static bool IsCalm(bool inWarp, int currentSectorId, int hostileCount, int invaders)
        {
            EmergencyState state;
            int activeCount;
            try
            {
                state = EmergencyDirector.CurrentState;
                activeCount = EmergencyDirector.ActiveCount;
            }
            catch (Exception)
            {
                return false; // seam fault = fail-closed
            }
            bool emergencyCalm = (state == EmergencyState.Normal || state == EmergencyState.Monitoring)
                && activeCount == 0;
            return emergencyCalm
                && NavigationRecoveryDirector.ActivePlanCount == 0
                && CombatDirector.ActiveRecordCount == 0
                && hostileCount == 0 && invaders <= 0 && !inWarp && currentSectorId >= 0;
        }

        private static void EnsureIntentTracked(string trackId, string kind, int nowMs)
        {
            PlanningIntent rec;
            if (S.Active.TryGetValue(trackId, out rec))
            {
                rec.UpdateCount++;
                rec.LastSeenMs = nowMs;
                return;
            }

            if (S.Active.Count >= MaxActiveSituations)
            {
                // Bounded tracked set: shed the oldest by LastSeenMs (tie ->
                // lowest key order). Defensive-only in this contract (two
                // record kinds); kept for house-pattern parity (P17/P18 mirror).
                string oldest = null;
                foreach (KeyValuePair<string, PlanningIntent> okv in S.Active)
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

            rec = new PlanningIntent(trackId, kind, nowMs);
            S.Active[trackId] = rec;
            S.IntentsTracked++;
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("PlanningUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Active.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.PremiseOpen = false;
                S.PremiseFirstSeenMs = -1;
                S.Premise = null;
                S.LastDriftReportMs = -1;
                S.EpisodeOpen = false;
                S.EpisodeFirstSeenMs = -1;
                S.Evaluations = 0;
                S.IntentsTracked = 0;
                S.OpenedReports = 0;
                S.DriftReports = 0;
                S.DriftRecheckBlocks = 0;
                S.CalmGateBlocks = 0;
                S.CapacityGateBlocks = 0;
                S.DuplicatesSuppressed = 0;
                S.IntentsExpired = 0;
                S.PlansExpired = 0;
                S.StaleRejections = 0;
                S.UnknownInputPasses = 0;
                S.LastUncertainReason = null;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_OnDecision = null;
            }
        }
    }
}