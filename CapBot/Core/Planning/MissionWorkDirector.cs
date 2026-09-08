using System;
using System.Collections.Generic;
using CapBot.Core.World;
using CapBot.Core.Tasks;
using CapBot.Core.Capabilities;
using CapBot.Core.Emergency;
using CapBot.Core.Navigation;
using CapBot.Core.Combat;

namespace CapBot.Core.Planning
{
    // ---- Phase 23: mission work director (dynamic task creation) ------------
    //
    // The authoring stage of the planning arc: P22 established the
    // deterministic planning layer (situation assessment, DATA ONLY) and
    // docs/PLANNING_DIRECTOR.md + CHANGELOG named its MISSIONWORK episode as
    // "the trigger surface P23 will author tasks from". This director is the
    // bounded deterministic CONSUMER of that surface: it authors EXACTLY ONE
    // task family bound to EXACTLY ONE capability — ISSUE_MOVE_ORDER
    // (RegisteredCapabilities.IssueMoveOrder) — when a workable mission
    // exists and the ship is calm.
    //
    // Ownership argument (fresh post-P23 audit, P18 precedent — full census
    // in docs/MISSION_WORK_DIRECTOR.md, all VERIFIED this phase):
    //   - SET_CAPTAIN_ORDER: P9 owns it (EmergencyDetector findings); a second
    //     author would share the 2000 ms registry cooldown with P9
    //     emergencies. NOT safe.
    //   - SET_CAPTAIN_TARGET: explicit covenant — P9 + the legacy captain
    //     tick own authorship (CombatDirector.cs mandate). NOT safe.
    //   - ADD/REMOVE_COURSE_GOAL: P14 owns both; legacy SetNextDestiny
    //     rebuilds the course every ~5 s. NOT safe.
    //   - CLEAR_COURSE_GOALS: zero authors BY DESIGN (legacy calls the RPC
    //     directly at four sites; destructive). NOT safe.
    //   - READ_WORLD_SNAPSHOT: zero authors BY DESIGN (a read contract —
    //     directors read the world seam directly). NOT authored.
    //   - ISSUE_MOVE_ORDER: exactly ONE in-tree author today (P18), transient
    //     20 s-TTL crew effect legacy never reads or writes (misfires
    //     self-heal in <= 20 s), dispatcher branch ready, registry cooldown
    //     1000 ms. THE one safe channel — the same verdict the P18 research
    //     reached, re-verified against the tree as it stands after P18-P22.
    //     P23 shares the 1000 ms cooldown with P18 and authoring at priority
    //     4 (below P18's 8) keeps the two channels serialized, never
    //     contending: the scheduler's owner-busy gate serializes CAPTAIN-
    //     owned tasks, and the aged priority ceiling (4 + MaxAgingBonus 5 = 9)
    //     can never exceed the preemption margin over a CAPTAIN_DELIB
    //     (8 + PreemptMargin 2 = 10) — a MISSION_WORK task can never preempt
    //     a captain-deliberation task; it queues behind it.
    //
    // Trigger surface (deterministic, two-machine-readable conditions):
    //   1. PlanningDirector.GetIntent("PLAN:MISSIONWORK") reports
    //      OpenedReported — the P22 episode formally opened (its own dwell +
    //      calm + capacity gates passed). P23 never authors before the
    //      planning layer opens the episode; if P22 is inert, P23 is inert.
    //   2. The work mission re-derived from the SAME snapshot with the
    //      IDENTICAL P22 predicate (first mission in the bounded scan with
    //      !Ended && !Abandoned && MissionTypeId >= 0 && TotalObjectives > 0
    //      && CompletedObjectives >= 0 && CompletedObjectives < TotalObjectives,
    //      scan bounded by MaxMissionsInScope within WorldSnapshot.MaxMissions).
    //      Mission id/objective counts are not carried in the PlanningIntent
    //      record (only in the opened line text), so re-derivation — the
    //      house precedent of reading the world seam directly (P18) — is the
    //      deterministic single source for WHAT to author.
    //
    // The single authoring rule (deliberate, conservative, provable inputs):
    //   MISSIONWORK — with the P22 episode opened, after a 15 s stability
    //   dwell (mission present and readable for that long), the ship calm
    //   (P18 gate family), the registry under its live cap, and no live
    //   MISSION_WORK task from this record, the director authors one
    //   MISSION_WORK task (owner CAPTAIN, priority 4, 60 s timeout, 1 retry,
    //   Preemptible) bound to ISSUE_MOVE_ORDER with a SECTOR target = the
    //   CURRENT sector: "hold/assemble crew at the current position while
    //   mission work is pending". The mission type id rides as metadata for
    //   diagnostics only (data, never parsed). The effect is the same
    //   transient crew move order P18 authors — a misfire self-heals in
    //   <= 20 s.
    //
    // Anti-churn (P18 discipline, mirrored exactly): per-record budget
    // MaxAuthoringsPerIntent=3 (resets only when the record decays via
    // ActiveExpiryMs hygiene); AuthoringDwellMs=15000 before the first
    // authoring of an episode; AuthoringRequeueBlockMs=20000 re-armed from
    // ReconcileTasks stamping TaskResolvedMs on terminal-or-vanished tasks
    // (Failed stays recovery-owned); register/queue refusals count
    // AuthoringRefused and NEVER retry in-pass.
    //
    // Multiplayer: the director never RPCs. Tasks flow through the P4/P5/P7/
    // P8 pipeline, which enforces master-side execution (deny-by-default
    // authority seam keeps clients silent).
    //
    // Boundaries (Phase 23 contract):
    //   - No new Harmony patch class (permanent ceiling of 11 preserved; the
    //     WorldTick Postfix gains the Evaluate + ReconcileTasks blocks IN
    //     PLACE, after the P22 planning block).
    //   - Authoring ONLY via CapBotTask.Create -> SetMetadata ->
    //     TaskRegistry.Register -> TryQueue. No scheduler/recovery/claims/
    //     validator/lifecycle calls beyond TaskRegistry.Get reads in
    //     ReconcileTasks. The task itself is NEVER touched after authoring
    //     (recovery owns lifecycle).
    //   - No direct game/RPC calls, no scene scans, no FindObjectsOfType, no
    //     LINQ, no per-frame work (cadence-gated 5 s).
    //   - Fail-safe on stale/missing/never-captured/not-started snapshots
    //     (shared 20 s standard); unknown mission sentinels never trigger
    //     (the P22 predicate skips them by construction).
    //   - All timestamps are explicit nowMs values (TaskClock semantics); no
    //     wall-clock reads; every collection bounded.
    //   - Config: NO new SaveValue (no Phase 23 config contract; the P18/P22
    //     deterministic-director precedent — always-on).
    public static class MissionWorkDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 5000;             // decision cadence (P18/P22 parity)
        public const int MaxActiveIntents = 8;            // bounded tracked-intent set
        public const int MaxHistory = 16;                 // bounded resolved-intent history
        public const int ActiveExpiryMs = 30000;          // trigger absent past this => record decays
        public const int MaxStaleSnapshotMs = 20000;      // fail-safe: no decisions on older snapshots
        public const int AuthoringDwellMs = 15000;        // mission presence must persist this long before authoring
        public const int AuthoringRequeueBlockMs = 20000; // re-arm delay after a work task resolves
        public const int WorkTaskTimeoutMs = 60000;       // work tasks self-expire (bounded work)
        public const int WorkPriority = 4;                // below P18's 8 — yields to captain deliberation; aged
                                                          // ceiling 4+5=9 never beats the preemption margin (8+2),
                                                          // and never approaches P14 (20) / P9 (110+)
        public const int MaxAuthoringsPerIntent = 3;      // anti-churn cap per intent record lifetime
        public const int MaxMissionsInScope = 8;          // mission-scan bound (IDENTICAL P22 scan bound)
        public const int MaxPendingLines = 4;             // bounded emission buffer per pass

        public const string TrackIdPrefix = "MWORK:";     // "MWORK:<kind>"
        public const string TrackMissionWork = "MISSIONWORK"; // TrackId "MWORK:MISSIONWORK"
        public const string TargetKindSector = "SECTOR";  // the dispatcher's ISSUE_MOVE_ORDER derivation target
        public const string TaskTypeMissionWork = "MISSION_WORK"; // static vocabulary, never parsed
        public const string OwnerCaptain = "CAPTAIN";     // matches the P7 capability owners + the P9/P14/P18 task owner

        // Public readback record (GetIntent surface — mirrors CaptainIntent/
        // PlanningIntent: public class with public fields; the director hands
        // out the live record for bounded diagnostic reads).
        public sealed class MissionWorkIntent
        {
            public readonly string TrackId;          // "MWORK:<kind>"
            public readonly string Kind;
            public readonly int FirstSeenMs;

            public int LastSeenMs;                   // last pass with a readable work mission
            public long UpdateCount;
            public int AuthoringsIssued;             // work tasks issued for this record
            public int LastAuthorMs = -1;            // -1 = never authored
            public long TaskId;                      // 0 = no task ever issued
            public int TaskResolvedMs = -1;          // -1 = no resolved task yet (re-arm blocker)

            public MissionWorkIntent(string trackId, string kind, int nowMs)
            {
                TrackId = trackId;
                Kind = kind;
                FirstSeenMs = nowMs;
                LastSeenMs = nowMs;
            }

            public bool HasLiveTask { get { return TaskId > 0 && TaskResolvedMs < 0; } }
        }

        private sealed class DirectorState
        {
            public readonly Dictionary<string, MissionWorkIntent> Active =
                new Dictionary<string, MissionWorkIntent>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;

            // mission-work episode state (single MWORK:MISSIONWORK record)
            public bool EpisodeOpen;                 // work mission present and readable
            public int EpisodeFirstSeenMs = -1;      // first readable mission pass of this episode

            public long Evaluations;
            public long IntentsTracked;
            public long AuthoringsIssued;
            public long CalmGateBlocks;
            public long CapacityGateBlocks;
            public long DuplicatesSuppressed;
            public long AuthoringCapped;
            public long AuthoringRefused;
            public long IntentsExpired;
            public long PlansExpired;
            public long StaleRejections;
            public string LastUncertainReason;
        }

        private static readonly DirectorState S = new DirectorState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed) -------------------------------------
        private static Func<bool> m_AuthorityProbe;         // null/fault => no-op evaluation
        private static Func<int> m_NowMsProvider;           // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_OnDecision;         // MissionWorkLogBridge attaches at boot

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
        public static long AuthoringsIssuedCount { get { lock (m_Lock) return S.AuthoringsIssued; } }
        public static long CalmGateBlockCount { get { lock (m_Lock) return S.CalmGateBlocks; } }
        public static long CapacityGateBlockCount { get { lock (m_Lock) return S.CapacityGateBlocks; } }
        public static long DuplicatesSuppressedCount { get { lock (m_Lock) return S.DuplicatesSuppressed; } }
        public static long AuthoringCappedCount { get { lock (m_Lock) return S.AuthoringCapped; } }
        public static long AuthoringRefusedCount { get { lock (m_Lock) return S.AuthoringRefused; } }
        public static long IntentsExpiredCount { get { lock (m_Lock) return S.IntentsExpired; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        // Deterministic lookup by intent id (null when absent).
        public static MissionWorkIntent GetIntent(string intentId)
        {
            if (string.IsNullOrEmpty(intentId)) return null;
            lock (m_Lock)
            {
                MissionWorkIntent r;
                return S.Active.TryGetValue(intentId, out r) ? r : null;
            }
        }

        // One bounded diagnostic line per tracked intent (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, MissionWorkIntent> kv in S.Active)
                {
                    MissionWorkIntent r = kv.Value;
                    lines.Add("intent " + r.TrackId
                        + " upd=" + r.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " authored=" + r.AuthoringsIssued.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " task=" + (r.TaskId > 0
                            ? r.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : "-")
                        + " resolved=" + (r.TaskResolvedMs >= 0
                            ? r.TaskResolvedMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : "-"));
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
                lines.Add("missionwork=" + S.Active.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tracked=" + S.IntentsTracked
                    + " authored=" + S.AuthoringsIssued
                    + " calmBlocked=" + S.CalmGateBlocks + " capBlocked=" + S.CapacityGateBlocks
                    + " dupSuppressed=" + S.DuplicatesSuppressed);
                lines.Add("capped=" + S.AuthoringCapped + " refused=" + S.AuthoringRefused
                    + " expired=" + S.IntentsExpired + " stale=" + S.StaleRejections
                    + " uncertain=" + (S.LastUncertainReason ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Pulls the authoritative snapshot through the world seam (production:
        // WorldStateService.Latest) so the caller stays clock-only. Returns the
        // number of NEW lines/reports this pass (0 on the quiet path AND on
        // every failure path). Never throws. Authors ONLY ISSUE_MOVE_ORDER
        // tasks (ownership argument in the header).
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
                MarkUncertain("no world snapshot captured (fail-safe: no mission work)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no mission work)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no mission work)");
                return 0;
            }

            List<string> pending = new List<string>(MaxPendingLines);
            int reports = 0;

            lock (m_Lock)
            {
                // ---- inputs ------------------------------------------------
                IReadOnlyList<MissionSnapshot> missions = snapshot.Missions;
                NavigationSnapshot navigation = snapshot.Navigation;

                bool inWarp = navigation != null && navigation.InWarp;
                int currentSectorId = navigation != null ? navigation.CurrentSectorId : -1;
                int hostileCount = snapshot.Threats != null ? snapshot.Threats.KnownHostileShipIds.Count : 0;
                int invaders = snapshot.Threats != null ? snapshot.Threats.InvadersOnboardCount : -1;

                // Work-mission re-derivation: the IDENTICAL P22 predicate and
                // scan bounds (first incomplete, unended, known-type mission in
                // the bounded scan). Unknown sentinels never trigger: the
                // predicate skips them by construction.
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

                // ---- mission-work episode + authoring -----------------------
                if (workMission != null)
                {
                    EnsureIntentTracked(nowMs);
                    if (!S.EpisodeOpen)
                    {
                        // Fresh authoring episode: the dwell clock starts on the
                        // first readable mission pass.
                        S.EpisodeOpen = true;
                        S.EpisodeFirstSeenMs = nowMs;
                    }
                    reports += EvaluateMissionWork(
                        workMission.MissionTypeId, workMission.CompletedObjectives, workMission.TotalObjectives,
                        inWarp, currentSectorId, hostileCount, invaders, nowMs, pending);
                }
                else
                {
                    // No readable work mission: close any open episode. The
                    // tracked record itself survives (hygiene owns expiry);
                    // only the episode state resets (authoring budget persists
                    // with the record — P18 discipline).
                    if (S.EpisodeOpen)
                    {
                        S.EpisodeOpen = false;
                        S.EpisodeFirstSeenMs = -1;
                    }
                }

                // ---- hygiene: expire intents whose trigger stays absent past
                // ActiveExpiryMs (one-shot expired report, P15/P16/P17/P18
                // mirror). A LIVE intent refreshes every readable pass and
                // never expires while its trigger remains.
                List<string> expired = new List<string>(MaxActiveIntents);
                foreach (KeyValuePair<string, MissionWorkIntent> kv in S.Active)
                {
                    if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs) expired.Add(kv.Key);
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    MissionWorkIntent expiredRec = S.Active[expired[i]];
                    S.Active.Remove(expired[i]);
                    S.HistoryIds.Enqueue(expired[i]);
                    while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                    S.PlansExpired++;
                    S.IntentsExpired++;
                    reports++;
                    pending.Add("MissionWorkIntentExpired " + expiredRec.TrackId);
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count && i < MaxPendingLines; i++) Emit(pending[i]);
            return reports;
        }

        // ---- rules -------------------------------------------------------------------

        private static void EnsureIntentTracked(int nowMs)
        {
            string trackId = TrackIdPrefix + TrackMissionWork;
            MissionWorkIntent rec;
            if (S.Active.TryGetValue(trackId, out rec))
            {
                rec.UpdateCount++;
                rec.LastSeenMs = nowMs;
                return;
            }

            if (S.Active.Count >= MaxActiveIntents)
            {
                // Bounded tracked set: shed the oldest by LastSeenMs (tie ->
                // lowest key order). Defensive-only in this contract (a single
                // record kind); kept for house-pattern parity (P17/P18/P22
                // mirror).
                string oldest = null;
                foreach (KeyValuePair<string, MissionWorkIntent> okv in S.Active)
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

            rec = new MissionWorkIntent(trackId, TrackMissionWork, nowMs);
            S.Active[trackId] = rec;
            S.IntentsTracked++;
        }

        // Mission-work authoring: one MISSION_WORK task (ISSUE_MOVE_ORDER,
        // SECTOR target = the current sector) once the mission has persisted
        // AuthoringDwellMs, the P22 episode has formally opened, the ship is
        // calm, and the registry has live-capacity headroom. Re-armed after
        // AuthoringRequeueBlockMs from the task's resolution; capped at
        // MaxAuthoringsPerIntent per record lifetime (anti-churn).
        private static int EvaluateMissionWork(
            int missionTypeId, int completedObjectives, int totalObjectives,
            bool inWarp, int currentSectorId, int hostileCount, int invaders, int nowMs,
            List<string> pending)
        {
            MissionWorkIntent rec = S.Active[TrackIdPrefix + TrackMissionWork];

            // Trigger surface gate: the P22 episode must have formally opened
            // (its own dwell + calm + capacity gates passed). Fail-closed: a
            // faulting planning readback counts as not-opened.
            if (!PlanningEpisodeOpened()) return 0;

            if (rec.HasLiveTask)
            {
                S.DuplicatesSuppressed++;
                return 0;
            }

            if (rec.AuthoringsIssued >= MaxAuthoringsPerIntent)
            {
                // Anti-churn cap: the situation keeps being tracked (data),
                // but this record never authors again. A decayed record
                // (hygiene) resets the budget with a fresh episode.
                S.AuthoringCapped++;
                return 0;
            }

            bool dwellMet = S.EpisodeFirstSeenMs >= 0
                && unchecked(nowMs - S.EpisodeFirstSeenMs) >= AuthoringDwellMs;
            bool requeueMet = rec.TaskResolvedMs < 0
                || unchecked(nowMs - rec.TaskResolvedMs) >= AuthoringRequeueBlockMs;
            if (!dwellMet || !requeueMet) return 0;

            // ---- the calm gate ---------------------------------------------------
            // Work authoring only when NOTHING else is in flight: emergencies
            // (P9 state + active records), recovery plans (P14), combat (P17
            // records + snapshot hostiles/boarders), warp. The gate is fail-
            // closed: any unreadable input blocks authoring (unknown data
            // never triggers — the detector contract).
            bool calm = IsEmergencyCalm() && NavigationRecoveryDirector.ActivePlanCount == 0
                && CombatDirector.ActiveRecordCount == 0
                && hostileCount == 0 && invaders <= 0 && !inWarp && currentSectorId >= 0;
            if (!calm)
            {
                S.CalmGateBlocks++;
                return 0;
            }

            // ---- the capacity gate ------------------------------------------------
            // Author only with live-capacity headroom: registering beyond the
            // cap returns false with no eviction, so the director reacts to
            // pressure (counts the block) instead of retry-storming.
            if (TaskRegistry.LiveCount >= TaskRegistry.MaxLiveTasks)
            {
                S.CapacityGateBlocks++;
                return 0;
            }

            long taskId = CreateWorkTaskLocked(
                rec, currentSectorId, missionTypeId, completedObjectives, totalObjectives, nowMs, pending);
            if (taskId > 0) return 1;
            return 0;
        }

        // P9 calm probe: the emergency system must be entirely quiescent —
        // state Normal/Monitoring AND zero active emergencies. Lock-nested
        // reads (MissionWork lock -> Emergency lock; one-way, deadlock-safe).
        private static bool IsEmergencyCalm()
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
            return (state == EmergencyState.Normal || state == EmergencyState.Monitoring)
                && activeCount == 0;
        }

        // P22 trigger-surface probe: the PLAN:MISSIONWORK episode must be
        // open AND formally opened (PlanningIntentOpened fired). Lock-nested
        // read (MissionWork lock -> Planning lock; one-way, deadlock-safe —
        // Planning never takes the MissionWork lock). Fail-closed.
        private static bool PlanningEpisodeOpened()
        {
            try
            {
                PlanningDirector.PlanningIntent mw = PlanningDirector.GetIntent(
                    PlanningDirector.TrackIdPrefix + PlanningDirector.TrackMissionWork);
                return mw != null && mw.OpenedReported;
            }
            catch (Exception)
            {
                return false; // seam fault = fail-closed
            }
        }

        // Creates + registers + queues one work task (callers hold m_Lock).
        // Deterministic shape: CAPTAIN-owned MISSION_WORK task with the P7
        // ISSUE_MOVE_ORDER capability bound via metadata. Returns the task id
        // (0 = refused — register/queue failures fail safe, no retry storm:
        // the intent stays open and the next dwell window re-arms).
        private static long CreateWorkTaskLocked(
            MissionWorkIntent rec, int sectorId, int missionTypeId,
            int completedObjectives, int totalObjectives, int nowMs, List<string> pending)
        {
            string sectorText = sectorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string missionText = missionTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string objectivesText = completedObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + totalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CapBotTask task = CapBotTask.Create(
                TaskTypeMissionWork,
                OwnerCaptain,
                "mission work: assist active mission; hold crew in current sector",
                WorkPriority,
                1,                       // one retry — work may retry once; never a storm
                WorkTaskTimeoutMs,
                TargetKindSector,
                sectorText,
                null);
            if (task == null) return 0;

            if (!task.SetMetadata("WorkId", rec.TrackId)) return 0;
            if (!task.SetMetadata("WorkKind", rec.Kind)) return 0;
            if (!task.SetMetadata("MissionTypeId", missionText)) return 0;
            if (!task.SetMetadata("Preemptible", "true")) return 0;
            if (!task.SetMetadata("CapabilityId", RegisteredCapabilities.IssueMoveOrder)) return 0;
            if (!task.SetMetadata("Argument", sectorText)) return 0;

            if (!TaskRegistry.Register(task))
            {
                S.AuthoringRefused++;
                return 0;
            }
            if (!task.TryQueue())
            {
                // Registered but queue refused — lifecycle logged it; the task
                // will expire on its own deadline (bounded). Count as refused
                // (no authoring effect this window).
                S.AuthoringRefused++;
                rec.TaskId = task.TaskId;
                rec.TaskResolvedMs = nowMs; // treat as resolved: requeue discipline applies
                rec.AuthoringsIssued++;
                rec.LastAuthorMs = nowMs;
                pending.Add("MoveOrderRefused #" + task.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " " + rec.TrackId + " sector=" + sectorText);
                return task.TaskId;
            }

            rec.TaskId = task.TaskId;
            rec.AuthoringsIssued++;
            rec.LastAuthorMs = nowMs;
            rec.UpdateCount++;
            S.AuthoringsIssued++;
            pending.Add("MoveOrderAuthored #" + task.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + rec.TrackId + " sector=" + sectorText
                + " mission=" + missionText + " objectives=" + objectivesText
                + " (assist active mission; transient 20s-TTL crew order)");
            return task.TaskId;
        }

        // ---- reconcile pass -----------------------------------------------------------
        //
        // Resolves active intents whose task reached a terminal state or vanished
        // from the registry. Bounded, snapshot-free; called by the driver after
        // Evaluate. The task itself is NEVER touched (recovery owns lifecycle) —
        // the intent only records the resolution and re-arms after
        // AuthoringRequeueBlockMs.
        public static void ReconcileTasks(int nowMs)
        {
            List<KeyValuePair<string, string>> resolutions = new List<KeyValuePair<string, string>>(MaxActiveIntents);
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, MissionWorkIntent> kv in S.Active)
                {
                    MissionWorkIntent r = kv.Value;
                    if (r.TaskId <= 0 || r.TaskResolvedMs >= 0) continue;
                    CapBotTask t;
                    try { t = TaskRegistry.Get(r.TaskId); }
                    catch (Exception) { t = null; }
                    if (t == null)
                    {
                        r.TaskResolvedMs = nowMs;
                        r.UpdateCount++;
                        resolutions.Add(new KeyValuePair<string, string>(kv.Key, "vanished"));
                    }
                    else if (t.IsTerminal)
                    {
                        r.TaskResolvedMs = nowMs;
                        r.UpdateCount++;
                        resolutions.Add(new KeyValuePair<string, string>(kv.Key, t.State.ToString()));
                    }
                }
            }
            // Resolutions are deterministic bookkeeping (no separate counter
            // line: the intent's Lines() readback carries resolved= stamps).
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("MissionWorkUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Active.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.EpisodeOpen = false;
                S.EpisodeFirstSeenMs = -1;
                S.Evaluations = 0;
                S.IntentsTracked = 0;
                S.AuthoringsIssued = 0;
                S.CalmGateBlocks = 0;
                S.CapacityGateBlocks = 0;
                S.DuplicatesSuppressed = 0;
                S.AuthoringCapped = 0;
                S.AuthoringRefused = 0;
                S.IntentsExpired = 0;
                S.PlansExpired = 0;
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