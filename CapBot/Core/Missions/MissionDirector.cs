using System;
using System.Collections.Generic;
using CapBot.Core.World;

namespace CapBot.Core.Missions
{
    // ---- Phase 15: mission director ---------------------------------------------
    //
    // Bounded deterministic MISSION TRACKING layer. Phase 15 is deliberately
    // REPORT-ONLY: the readable mission surface (P6 snapshot missions section —
    // MissionTypeId, Ended, Abandoned, TotalObjectives, CompletedObjectives,
    // FirstIncompleteObjectiveText) carries no objective types, no giver
    // locations, and no rewards, and the Phase 7 capability catalog contains
    // NO mission capability (deliberately excluded in Phase 7), so there is no
    // verified, executor-dispatchable action a mission plan could request.
    // A task without CapabilityId metadata fails at start per the Phase 8
    // executor contract, so the director produces DATA records + bounded
    // diagnostic lines and creates NO tasks. Mission accept/decline dialogue
    // flows, AttemptStartMissionOfTypeID/AttemptForceEndMissionOfTypeID RPCs,
    // and any objective-progress manipulation (legacy audit finding C2) remain
    // out of scope — those need verified capabilities + dispatcher branches in
    // a later phase before any task could ever exist.
    //
    // What the director does (all inputs VERIFIED snapshot fields):
    //   - tracks up to MaxActiveMissions active (non-terminal) missions by
    //     MissionTypeId with deterministic oldest-shedding;
    //   - emits one-shot transition reports: MissionOpened (first sighting),
    //     MissionProgress (completed-count increase), MissionCompleted
    //     (all objectives done or Ended while not abandoned),
    //     MissionAbandoned (Abandoned edge), MissionVanished (tracked mission
    //     disappeared from a readable missions list);
    //   - emits a dwell-gated MissionStallReport (no completed-objective
    //     progress for StallReportMs) ONCE per stall episode — a bounded
    //     coordination record for later phases (Captain Brain 2.0 planning,
    //     Economy Director), never a task;
    //   - emits an edge-triggered MissionlessReport when a readable missions
    //     list goes empty after tracking activity (never spam);
    //   - suppresses every report while the missions list is UNREADABLE
    //     (capture failure or never-captured) — no decisions on uncertainty.
    //
    // Identity caveat (audit L2, documented): the snapshot carries only
    // MissionTypeId, so two concurrent same-type missions are indistinguishable
    // in captured data; tracking keys are "MISSION:<typeId>" and inherit that
    // limitation (SameTypeIdCollisions counter reports the risk). Progress
    // aggregates across same-type instances; transitions fire on the first
    // terminal aggregate edge. Objective *text* is carried as DATA (bounded,
    // never parsed as behavior — house security rule).
    //
    // Boundaries (Phase 15 contract):
    //   - No tasks, no task metadata, no registry writes, no scheduler/claim/
    //     executor interaction, no RPCs, no scene scans, no LINQ, no per-frame
    //     work (cadence-gated 5 s, driven from the WorldTick Postfix after the
    //     navigation blocks).
    //   - Deny-by-default authority seam: no probe / faulting probe /
    //     non-master -> no evaluation (clients never produce mission reports).
    //   - Fail-safe on stale/missing/never-captured/not-started snapshots
    //     (same 20 s window as the P9/P14 directors).
    //   - All timestamps are explicit nowMs values (TaskClock semantics); no
    //     wall-clock reads; every collection bounded.
    public sealed class MissionTrackRecord
    {
        public readonly string TrackId;          // "MISSION:<typeId>"
        public readonly int MissionTypeId;
        public readonly int FirstSeenMs;         // first sighting of the mission

        public int LastSeenMs;                   // last pass where a live mission refreshed the record
        public int LastProgressMs;               // last completed-count increase
        public int CompletedObjectives;          // last observed completed count
        public int TotalObjectives;              // last observed total (0 = unknown)
        public long UpdateCount;
        public bool CompletedReported;           // MissionCompleted fired
        public bool AbandonedReported;           // MissionAbandoned fired
        public bool StallReported;               // MissionStallReport fired this episode
        public string LatestObjectiveText;       // bounded DATA carry (≤ 120 chars), never parsed
        public bool ReturnRequiredReported;      // P52: return signal fired (one per record)

        public MissionTrackRecord(string trackId, int missionTypeId, int nowMs)
        {
            TrackId = trackId;
            MissionTypeId = missionTypeId;
            FirstSeenMs = nowMs;
            LastSeenMs = nowMs;
            LastProgressMs = nowMs;
            CompletedObjectives = 0;
            TotalObjectives = 0;
            UpdateCount = 0;
            CompletedReported = false;
            AbandonedReported = false;
            StallReported = false;
            LatestObjectiveText = null;
        }

        // True once a terminal transition has been reported. Terminal records
        // stop refreshing LastSeenMs and decay via expiry hygiene.
        public bool IsTerminalState { get { return CompletedReported || AbandonedReported; } }

        // Bounded DATA carry of the latest incomplete-objective text (the
        // snapshot already truncates to MaxObjectiveTextLen). Diagnostic only.
        public void CarryObjectiveText(string text)
        {
            if (text == null) { LatestObjectiveText = null; return; }
            LatestObjectiveText = text.Length <= MissionSnapshot.MaxObjectiveTextLen
                ? text
                : text.Substring(0, MissionSnapshot.MaxObjectiveTextLen);
        }
    }

    public static class MissionDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 5000;          // decision cadence (1 Hz snapshot, 5 s decisions)
        public const int MaxActiveMissions = 8;        // bounded tracked-mission set (snapshot holds ≤ 16)
        public const int MaxHistory = 16;              // bounded resolved-mission history
        public const int ActiveExpiryMs = 60000;       // un-refreshed (terminal/vanished) record decays after this
        public const int MaxStaleSnapshotMs = 20000;   // fail-safe: no decisions on older snapshots
        public const int StallReportMs = 120000;       // no-progress window before one stall report

        public const string TrackIdPrefix = "MISSION:";     // "MISSION:<typeId>"
        public const string TargetKindMission = "MISSION";  // diagnostic vocabulary — no capability exists

        private sealed class DirectorState
        {
            public readonly Dictionary<string, MissionTrackRecord> Active =
                new Dictionary<string, MissionTrackRecord>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;
            public bool MissionlessEpisodeOpen;
            public long Evaluations;
            public long MissionsTracked;
            public long ProgressReports;
            public long CompletedReports;
            public long AbandonedReports;
            public long VanishedReports;
            public long StallReports;
            public long MissionlessReports;
            public long DuplicatesSuppressed;
            public long PlansExpired;
            public long StaleRejections;
            public long SameTypeIdCollisions;
            public long ReturnSignals;               // P52: return-policy edge signals
            public string LastUncertainReason;
        }

        private static readonly DirectorState S = new DirectorState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed) -------------------------------------
        private static Func<bool> m_AuthorityProbe;         // null/fault => no-op evaluation
        private static Func<int> m_NowMsProvider;           // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_OnDecision;         // MissionLogBridge attaches at boot

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
        public static int ActiveMissionCount { get { lock (m_Lock) return S.Active.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long EvaluationCount { get { lock (m_Lock) return S.Evaluations; } }
        public static long MissionsTrackedCount { get { lock (m_Lock) return S.MissionsTracked; } }
        public static long ProgressReportCount { get { lock (m_Lock) return S.ProgressReports; } }
        public static long CompletedReportCount { get { lock (m_Lock) return S.CompletedReports; } }
        public static long AbandonedReportCount { get { lock (m_Lock) return S.AbandonedReports; } }
        public static long VanishedReportCount { get { lock (m_Lock) return S.VanishedReports; } }
        public static long StallReportCount { get { lock (m_Lock) return S.StallReports; } }
        public static long MissionlessReportCount { get { lock (m_Lock) return S.MissionlessReports; } }
        public static long DuplicatesSuppressedCount { get { lock (m_Lock) return S.DuplicatesSuppressed; } }
        public static long PlansExpiredCount { get { lock (m_Lock) return S.PlansExpired; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static long SameTypeIdCollisionCount { get { lock (m_Lock) return S.SameTypeIdCollisions; } }
        public static long ReturnSignalCount { get { lock (m_Lock) return S.ReturnSignals; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        // Deterministic lookup by track id (null when absent).
        public static MissionTrackRecord GetTrack(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            lock (m_Lock)
            {
                MissionTrackRecord r;
                return S.Active.TryGetValue(trackId, out r) ? r : null;
            }
        }

        // One bounded diagnostic line per tracked mission (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, MissionTrackRecord> kv in S.Active)
                {
                    MissionTrackRecord r = kv.Value;
                    lines.Add("track " + r.TrackId
                        + " done=" + r.CompletedObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "/" + r.TotalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " upd=" + r.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (r.CompletedReported ? " completed" : "")
                        + (r.AbandonedReported ? " abandoned" : ""));
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
                lines.Add("missions=" + S.Active.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tracked=" + S.MissionsTracked
                    + " progress=" + S.ProgressReports + " completed=" + S.CompletedReports
                    + " abandoned=" + S.AbandonedReports + " vanished=" + S.VanishedReports
                    + " stalls=" + S.StallReports);
                lines.Add("missionless=" + S.MissionlessReports
                    + " dupSuppressed=" + S.DuplicatesSuppressed + " expired=" + S.PlansExpired
                    + " stale=" + S.StaleRejections + " sameTypeColl=" + S.SameTypeIdCollisions
                    + " returnSignals=" + S.ReturnSignals
                    + " uncertain=" + (S.LastUncertainReason ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Pulls the authoritative snapshot through the world seam (production:
        // WorldStateService.Latest) so the caller stays clock-only. Returns the
        // number of NEW transition reports this pass (0 on the quiet path AND on
        // every failure path). Never throws. Creates NO tasks (report-only phase).
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
                MarkUncertain("no world snapshot captured (fail-safe: no mission tracking)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no mission tracking)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no mission tracking)");
                return 0;
            }
            if (snapshot.Missions == null)
            {
                // DEFENSIVE ONLY: the WorldSnapshot constructors normalize a
                // null missions list to an empty array (Bounded contract), so
                // this branch is unreachable today. Kept as a fail-safe mirror
                // of the P14 navigation-section check in case the snapshot
                // contract ever grows a missions-authority mark.
                MarkUncertain("missions section missing (defensive; no mission tracking this pass)");
                lock (m_Lock) S.Evaluations++;
                return 0;
            }

            List<string> pending = new List<string>(4);
            int reports = 0;

            lock (m_Lock)
            {
                // ---- present map: stable id -> snapshot (dedupe same-instance) --
                // P52: key on the stable per-instance MissionId when captured
                // (probe9: MissionData.MissionID — closes the audit L2 same-type
                // ambiguity for instances that carry identity); legacy missions
                // (MissionId -1) keep the EXACT P15 key "MISSION:<typeId>" so
                // every existing consumer/test key keeps working. Same-type
                // instances with identity now track separately (the collision
                // counter stays meaningful for legacy-keyed missions only).
                Dictionary<string, MissionSnapshot> present =
                    new Dictionary<string, MissionSnapshot>(StringComparer.Ordinal);
                int duplicateInstances = 0;
                for (int i = 0; i < snapshot.Missions.Count; i++)
                {
                    MissionSnapshot m = snapshot.Missions[i];
                    if (m == null) continue;
                    string key = m.MissionId >= 0
                        ? MissionLifecycle.TrackIdInstancePrefix + m.MissionId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : TrackIdPrefix + m.MissionTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!present.ContainsKey(key))
                    {
                        present[key] = m;
                    }
                    else
                    {
                        duplicateInstances++;
                    }
                }
                if (duplicateInstances > 0) S.SameTypeIdCollisions += duplicateInstances;

                // ---- hygiene: expire absent or terminal-decayed records ----------
                // A LIVE PRESENT mission never expires (its record refreshes
                // every pass); only records whose mission vanished from the
                // list, or terminal records that stopped refreshing and aged
                // past ActiveExpiryMs, decay to free tracked slots.
                List<string> expired = new List<string>(MaxActiveMissions);
                foreach (KeyValuePair<string, MissionTrackRecord> kv in S.Active)
                {
                    MissionTrackRecord rec = kv.Value;
                    bool absent = !present.ContainsKey(kv.Key);
                    bool terminalDecayed = rec.IsTerminalState
                        && unchecked(nowMs - rec.LastSeenMs) >= ActiveExpiryMs;
                    if (absent || terminalDecayed)
                    {
                        expired.Add(kv.Key);
                    }
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    MissionTrackRecord expiredRec = S.Active[expired[i]];
                    S.Active.Remove(expired[i]);
                    S.HistoryIds.Enqueue(expired[i]);
                    while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                    S.PlansExpired++;
                    bool vanished = !present.ContainsKey(expired[i]);
                    if (vanished && !expiredRec.IsTerminalState)
                    {
                        // Vanished while still live: bounded one-shot report.
                        S.VanishedReports++;
                        reports++;
                        pending.Add("MissionVanished " + expiredRec.TrackId
                            + " done=" + expiredRec.CompletedObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + "/" + expiredRec.TotalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        pending.Add("MissionExpired " + expiredRec.TrackId + " (record decayed)");
                    }
                }

                // ---- per-mission rules --------------------------------------------
                if (present.Count == 0)
                {
                    // Readable and missionless: edge-triggered report — only
                    // after this session actually tracked a mission (a pristine
                    // session with zero missions is the NORMAL state, not an
                    // event worth reporting).
                    if (S.Active.Count == 0 && S.MissionsTracked > 0 && !S.MissionlessEpisodeOpen)
                    {
                        S.MissionlessEpisodeOpen = true;
                        S.MissionlessReports++;
                        pending.Add("MissionlessReport (no active missions; readable list)");
                    }
                }
                else
                {
                    S.MissionlessEpisodeOpen = false;
                    foreach (KeyValuePair<string, MissionSnapshot> kv in present)
                    {
                        MissionSnapshot m = kv.Value;
                        string trackId = kv.Key;
                        MissionTrackRecord rec;
                        bool opened = false;
                        if (!S.Active.TryGetValue(trackId, out rec))
                        {
                            if (S.Active.Count >= MaxActiveMissions)
                            {
                                // Bounded tracked set: shed the oldest by LastSeenMs
                                // (tie -> lowest key order). Bounded scan (<= 8).
                                string oldest = null;
                                foreach (KeyValuePair<string, MissionTrackRecord> okv in S.Active)
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
                                    pending.Add("MissionShed " + oldest + " (tracked set full)");
                                }
                            }
                            rec = new MissionTrackRecord(trackId, m.MissionTypeId, nowMs);
                            S.Active[trackId] = rec;
                            S.MissionsTracked++;
                            opened = true;
                        }

                        rec.UpdateCount++;

                        // Live records refresh; terminal records stop refreshing
                        // (they decay via expiry hygiene to free tracked slots).
                        if (!rec.IsTerminalState) rec.LastSeenMs = nowMs;

                        if (opened)
                        {
                            rec.CompletedObjectives = m.CompletedObjectives;
                            rec.TotalObjectives = m.TotalObjectives;
                            rec.CarryObjectiveText(m.FirstIncompleteObjectiveText);
                            // Terminal-at-first-sighting missions (ended/abandoned
                            // before ever seen) are recorded, not reported as opens.
                            if (m.Ended)
                            {
                                rec.CompletedReported = true;
                                S.CompletedReports++;
                                reports++;
                                pending.Add("MissionCompleted " + trackId
                                    + " done=" + m.CompletedObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + "/" + m.TotalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + " (terminal at first sighting)");
                            }
                            else if (m.Abandoned)
                            {
                                rec.AbandonedReported = true;
                                S.AbandonedReports++;
                                reports++;
                                pending.Add("MissionAbandoned " + trackId);
                            }
                            else
                            {
                                reports++;
                                pending.Add("MissionOpened " + trackId
                                    + " objectives=" + m.TotalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            }
                            continue;
                        }

                        // Progress edge: completed-count increase. When the
                        // increase COMPLETES the mission, the terminal block
                        // below emits MissionCompleted instead (one bounded
                        // report per edge, never two).
                        if (m.CompletedObjectives > rec.CompletedObjectives)
                        {
                            bool completesNow = m.TotalObjectives > 0
                                && m.CompletedObjectives >= m.TotalObjectives;
                            rec.CompletedObjectives = m.CompletedObjectives;
                            rec.LastProgressMs = nowMs;
                            rec.StallReported = false;
                            if (!completesNow)
                            {
                                S.ProgressReports++;
                                reports++;
                                pending.Add("MissionProgress " + trackId
                                    + " done=" + m.CompletedObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + "/" + m.TotalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            }
                        }
                        // Total-objective change (objective list grew): data refresh.
                        if (m.TotalObjectives != rec.TotalObjectives)
                        {
                            rec.TotalObjectives = m.TotalObjectives;
                        }
                        // Carry the bounded objective text as DATA (never parsed).
                        rec.CarryObjectiveText(m.FirstIncompleteObjectiveText);

                        // P52 return policy (ONE authoritative decision, §9):
                        // edge-triggered data signal when the mission reaches its
                        // return point (legacy type table + additive game flag).
                        if (!rec.ReturnRequiredReported
                            && !rec.IsTerminalState
                            && MissionReturnPolicy.ShouldReturnToSender(m))
                        {
                            rec.ReturnRequiredReported = true;
                            S.ReturnSignals++;
                            reports++;
                            pending.Add(new MissionReturnPolicy.MissionReturnSignal(
                                MissionReturnPolicy.MissionReturnSignal.KindReturnRequired,
                                m.MissionTypeId, m.MissionId,
                                m.CompletedObjectives, m.TotalObjectives, nowMs).ToString());
                        }

                        // Terminal transitions (report once per record).
                        if (!rec.CompletedReported && !rec.AbandonedReported)
                        {
                            bool allDone = m.TotalObjectives > 0 && m.CompletedObjectives >= m.TotalObjectives;
                            if (allDone || m.Ended)
                            {
                                rec.CompletedReported = true;
                                S.CompletedReports++;
                                reports++;
                                pending.Add("MissionCompleted " + trackId
                                    + " done=" + m.CompletedObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + "/" + m.TotalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            }
                            else if (m.Abandoned)
                            {
                                rec.AbandonedReported = true;
                                S.AbandonedReports++;
                                reports++;
                                pending.Add("MissionAbandoned " + trackId);
                            }
                            else if (m.TotalObjectives > 0
                                && unchecked(nowMs - rec.LastProgressMs) >= StallReportMs
                                && !rec.StallReported)
                            {
                                // Stall: no completed-objective progress for the
                                // window. One report per episode (re-armed on the
                                // next progress edge). Report-only coordination
                                // record for later phases — never a task.
                                rec.StallReported = true;
                                S.StallReports++;
                                reports++;
                                pending.Add("MissionStallReport " + trackId
                                    + " done=" + rec.CompletedObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + "/" + rec.TotalObjectives.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + " noProgressForMs=" + (nowMs - rec.LastProgressMs).ToString(System.Globalization.CultureInfo.InvariantCulture));
                            }
                            else if (rec.StallReported)
                            {
                                // Repeat passes during the same stall episode are
                                // quiet duplicates (bounded bookkeeping only).
                                S.DuplicatesSuppressed++;
                            }
                        }
                    }
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count; i++) Emit(pending[i]);
            return reports;
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("MissionUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Active.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.MissionlessEpisodeOpen = false;
                S.Evaluations = 0;
                S.MissionsTracked = 0;
                S.ProgressReports = 0;
                S.CompletedReports = 0;
                S.AbandonedReports = 0;
                S.VanishedReports = 0;
                S.StallReports = 0;
                S.MissionlessReports = 0;
                S.DuplicatesSuppressed = 0;
                S.PlansExpired = 0;
                S.StaleRejections = 0;
                S.SameTypeIdCollisions = 0;
                S.ReturnSignals = 0;
                S.LastUncertainReason = null;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_OnDecision = null;
            }
        }
    }
}