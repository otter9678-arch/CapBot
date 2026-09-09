using System;
using System.Collections.Generic;
using System.Globalization;
using CapBot.Core.World;

namespace CapBot.Core.Missions
{
    // ---- P52: the mission lifecycle FSM (master prompt §5) ---------------------
    //
    // Pure deterministic domain: 17 explicit states with a single deterministic
    // transition rule (Apply) that folds one MissionSnapshot + navigation into
    // the bounded state record. NO game types, NO clock reads, NO tasks, NO
    // side effects — the FSM is data; consumers (MissionDirector, diagnostics,
    // /capbotmission) read the tracked records. Every input is a verified
    // snapshot field; unknown sentinels never advance a state.
    //
    // States (the 17 mandated):
    //   MISSION_NONE            no mission tracked (session start / all decayed)
    //   MISSION_DETECTED        mission first seen in a readable missions list
    //   MISSION_TARGET_SELECTED a workable target mission was selected (the
    //                           deterministic first-incomplete predicate)
    //   MISSION_TRAVELING_TO_TARGET nav course aimed at the mission's target
    //   MISSION_TARGET_REACHED  current sector == the objective's target sector
    //   MISSION_ACCEPTING       the game reports turn-in/accept readiness edge
    //   MISSION_ACCEPTED        the game confirms the mission active with id
    //   MISSION_OBJECTIVES_DETECTED per-objective payloads are captured
    //   MISSION_OBJECTIVE_ACTIVE  at least one objective incomplete
    //   MISSION_OBJECTIVE_COMPLETE  an objective completed edge fired
    //   MISSION_NEXT_OBJECTIVE  completed < total (more work remains)
    //   MISSION_COMPLETE        all objectives complete (ended or not)
    //   MISSION_RETURN_REQUIRED return policy says deliver/turn-in
    //   MISSION_RETURNING       return required + nav course aimed at sender
    //   MISSION_RETURNED        game flag: mission no longer active (turned in)
    //   MISSION_FAILED          game flag: mission failed (or abandoned)
    //   MISSION_TEMP_UNAVAILABLE  mission present but unreadable/stale this
    //                           pass (sector transition, warp) — NEVER a
    //                           terminal state (§12: survives, re-detects)
    //
    // No-stuck contract: every non-terminal state has a deterministic exit —
    // vanished/unknown missions fall to TEMP_UNAVAILABLE or decay to NONE via
    // the expiry hygiene (ActiveExpiryMs); nothing ever wedges between states.
    //
    // Identity: records key on the stable MissionId when captured (>= 0,
    // MissionData.MissionID — probe9), else the bounded legacy type key
    // "MISSION:T<typeId>" (the P15 L2 caveat, unchanged for legacy callers).
    public enum MissionLifecycleState
    {
        MissionNone = 0,
        MissionDetected = 1,
        MissionTargetSelected = 2,
        MissionTravelingToTarget = 3,
        MissionTargetReached = 4,
        MissionAccepting = 5,
        MissionAccepted = 6,
        MissionObjectivesDetected = 7,
        MissionObjectiveActive = 8,
        MissionObjectiveComplete = 9,
        MissionNextObjective = 10,
        MissionComplete = 11,
        MissionReturnRequired = 12,
        MissionReturning = 13,
        MissionReturned = 14,
        MissionFailed = 15,
        MissionTempUnavailable = 16,
    }

    public static class MissionLifecycleStateText
    {
        private static readonly string[] Names = new string[]
        {
            "MISSION_NONE",
            "MISSION_DETECTED",
            "MISSION_TARGET_SELECTED",
            "MISSION_TRAVELING_TO_TARGET",
            "MISSION_TARGET_REACHED",
            "MISSION_ACCEPTING",
            "MISSION_ACCEPTED",
            "MISSION_OBJECTIVES_DETECTED",
            "MISSION_OBJECTIVE_ACTIVE",
            "MISSION_OBJECTIVE_COMPLETE",
            "MISSION_NEXT_OBJECTIVE",
            "MISSION_COMPLETE",
            "MISSION_RETURN_REQUIRED",
            "MISSION_RETURNING",
            "MISSION_RETURNED",
            "MISSION_FAILED",
            "MISSION_TEMP_UNAVAILABLE",
        };

        public static string Text(MissionLifecycleState state)
        {
            int i = (int)state;
            if (i < 0 || i >= 17) return "MISSION_UNKNOWN";
            return Names[i];
        }
    }

    // One tracked mission's lifecycle record (bounded, mutable bookkeeping —
    // the same shape as MissionTrackRecord/PlanningIntent).
    public sealed class MissionLifecycleRecord
    {
        public readonly string TrackId;          // "MISSION:I<id>" or "MISSION:T<typeId>"
        public readonly int MissionTypeId;
        public readonly int MissionId;           // -1 = legacy identity
        public readonly int FirstSeenMs;

        public MissionLifecycleState State;
        public int LastSeenMs;
        public int StateSinceMs;                 // when the current state was entered
        public int CompletedObjectives;
        public int TotalObjectives;
        public long UpdateCount;
        public bool TerminalReported;            // returned/failed/complete reported
        public bool ReturnRequiredReported;

        public MissionLifecycleRecord(string trackId, int missionTypeId, int missionId, int nowMs)
        {
            TrackId = trackId;
            MissionTypeId = missionTypeId;
            MissionId = missionId;
            FirstSeenMs = nowMs;
            State = MissionLifecycleState.MissionDetected;
            StateSinceMs = nowMs;
            LastSeenMs = nowMs;
            CompletedObjectives = 0;
            TotalObjectives = 0;
            UpdateCount = 0;
            TerminalReported = false;
            ReturnRequiredReported = false;
        }
    }

    public static class MissionLifecycle
    {
        public const int MinRecheckMs = 5000;     // decision cadence (house 1 Hz snapshot, 5 s decisions)
        public const int MaxTracked = 12;         // bounded tracked set
        public const int MaxHistory = 16;
        public const int ActiveExpiryMs = 60000;  // absent/temp-unavailable record decays after this
        public const int MaxStaleSnapshotMs = 20000;

        public const string TrackIdInstancePrefix = "MISSION:I";
        public const string TrackIdTypePrefix = "MISSION:T";

        public static string TrackIdFor(int missionId, int missionTypeId)
        {
            if (missionId >= 0)
                return TrackIdInstancePrefix + missionId.ToString(CultureInfo.InvariantCulture);
            return TrackIdTypePrefix + missionTypeId.ToString(CultureInfo.InvariantCulture);
        }

        private sealed class LifecycleState
        {
            public readonly Dictionary<string, MissionLifecycleRecord> Active =
                new Dictionary<string, MissionLifecycleRecord>(32);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;
            public long Evaluations;
            public long MissionsTracked;
            public long Transitions;
            public long ReturnRequiredReports;
            public long TerminalReports;
            public long TempUnavailableReports;
            public long StaleRejections;
            public long PlansExpired;
            public string LastUncertainReason;
        }

        private static readonly LifecycleState S = new LifecycleState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed; production wires TaskClock etc.) --
        private static Func<bool> m_AuthorityProbe;
        private static Func<int> m_NowMsProvider;
        private static Func<WorldSnapshot> m_WorldProvider;
        private static Action<string> m_OnDecision;

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

        // ---- readback --------------------------------------------------------------
        public static int TrackedCount { get { lock (m_Lock) return S.Active.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long EvaluationCount { get { lock (m_Lock) return S.Evaluations; } }
        public static long MissionsTrackedCount { get { lock (m_Lock) return S.MissionsTracked; } }
        public static long TransitionCount { get { lock (m_Lock) return S.Transitions; } }
        public static long ReturnRequiredReportCount { get { lock (m_Lock) return S.ReturnRequiredReports; } }
        public static long TerminalReportCount { get { lock (m_Lock) return S.TerminalReports; } }
        public static long TempUnavailableReportCount { get { lock (m_Lock) return S.TempUnavailableReports; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        public static MissionLifecycleRecord GetTrack(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            lock (m_Lock)
            {
                MissionLifecycleRecord r;
                return S.Active.TryGetValue(trackId, out r) ? r : null;
            }
        }

        // One bounded line per tracked record (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, MissionLifecycleRecord> kv in S.Active)
                {
                    MissionLifecycleRecord r = kv.Value;
                    lines.Add("lifecycle " + r.TrackId
                        + " state=" + MissionLifecycleStateText.Text(r.State)
                        + " done=" + r.CompletedObjectives.ToString(CultureInfo.InvariantCulture)
                        + "/" + r.TotalObjectives.ToString(CultureInfo.InvariantCulture)
                        + " upd=" + r.UpdateCount.ToString(CultureInfo.InvariantCulture));
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
                lines.Add("lifecycle tracked=" + S.Active.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tracked=" + S.MissionsTracked
                    + " transitions=" + S.Transitions
                    + " returnReq=" + S.ReturnRequiredReports
                    + " terminal=" + S.TerminalReports
                    + " tempUnavail=" + S.TempUnavailableReports);
                lines.Add("stale=" + S.StaleRejections + " expired=" + S.PlansExpired
                    + " uncertain=" + (S.LastUncertainReason ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass -----------------------------------------------------
        //
        // One deterministic pass: fold the snapshot's missions into the tracked
        // set. Returns the number of NEW transition lines this pass. Never throws.
        public static int Evaluate(int nowMs)
        {
            Func<bool> auth;
            Func<WorldSnapshot> world;
            lock (m_Lock)
            {
                auth = m_AuthorityProbe;
                world = m_WorldProvider;
            }

            // Deny-by-default authority (house contract: clients never advance
            // mission state).
            if (auth == null) return 0;
            bool isAuth;
            try { isAuth = auth(); } catch (Exception) { return 0; }
            if (!isAuth) return 0;

            lock (m_Lock)
            {
                if (S.LastEvalMs >= 0 && unchecked(nowMs - S.LastEvalMs) < MinRecheckMs) return 0;
                S.LastEvalMs = nowMs;
            }

            WorldSnapshot snapshot = null;
            if (world != null)
            {
                try { snapshot = world(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured)
            {
                MarkUncertain("no world snapshot captured (fail-safe: no lifecycle tracking)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no lifecycle tracking)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no lifecycle tracking)");
                return 0;
            }

            List<string> pending = new List<string>(8);
            int reports = 0;

            lock (m_Lock)
            {
                IReadOnlyList<MissionSnapshot> missions = snapshot.Missions;
                Dictionary<string, MissionSnapshot> present =
                    new Dictionary<string, MissionSnapshot>(missions != null ? missions.Count : 0, StringComparer.Ordinal);
                if (missions != null)
                {
                    for (int i = 0; i < missions.Count && i < WorldSnapshot.MaxMissions; i++)
                    {
                        MissionSnapshot m = missions[i];
                        if (m == null) continue;
                        if (m.MissionTypeId < 0 && m.MissionId < 0) continue;
                        string trackId = TrackIdFor(m.MissionId, m.MissionTypeId);
                        if (!present.ContainsKey(trackId)) present[trackId] = m;
                    }
                }

                // ---- hygiene: expire records that stopped refreshing ----------
                List<string> expired = new List<string>(MaxTracked);
                foreach (KeyValuePair<string, MissionLifecycleRecord> kv in S.Active)
                {
                    bool absent = !present.ContainsKey(kv.Key);
                    bool terminalDecayed = kv.Value.TerminalReported
                        && unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs;
                    if (absent || terminalDecayed)
                    {
                        // §12: a vanished mission that was NOT terminal decays
                        // through TEMP_UNAVAILABLE wording — never DEAD.
                        if (absent && !kv.Value.TerminalReported)
                        {
                            S.TempUnavailableReports++;
                            reports++;
                            pending.Add("MissionTempUnavailable " + kv.Key
                                + " (vanished; survives via TEMP_UNAVAILABLE, never DEAD)");
                        }
                        expired.Add(kv.Key);
                    }
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    S.Active.Remove(expired[i]);
                    S.HistoryIds.Enqueue(expired[i]);
                    while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                    S.PlansExpired++;
                }

                // ---- fold present missions through the transition rule --------
                foreach (KeyValuePair<string, MissionSnapshot> kv in present)
                {
                    if (S.Active.Count >= MaxTracked && !S.Active.ContainsKey(kv.Key)) break; // bounded
                    int newLines = Apply(kv.Key, kv.Value, nowMs, pending);
                    reports += newLines;
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count && i < MaxPendingLines; i++) Emit(pending[i]);
            return reports;
        }

        private const int MaxPendingLines = 6;

        // The deterministic transition rule. Fold one snapshot mission into its
        // record (created on first sighting). Bounded; never throws.
        private static int Apply(string trackId, MissionSnapshot m, int nowMs, List<string> pending)
        {
            MissionLifecycleRecord rec;
            bool opened = false;
            if (!S.Active.TryGetValue(trackId, out rec))
            {
                if (S.Active.Count >= MaxTracked) return 0; // bounded set (hygiene frees)
                rec = new MissionLifecycleRecord(trackId, m.MissionTypeId, m.MissionId, nowMs);
                S.Active[trackId] = rec;
                S.MissionsTracked++;
                opened = true;
            }

            rec.UpdateCount++;
            if (!rec.TerminalReported) rec.LastSeenMs = nowMs;
            rec.TotalObjectives = m.TotalObjectives;
            rec.CompletedObjectives = m.CompletedObjectives;

            MissionLifecycleState before = rec.State;
            MissionLifecycleState next = rec.State;

            // Terminal observation (§6: the game's own flags are authoritative;
            // the bounded Ended/Abandoned flags mirror them snapshot-side).
            bool failed = (m.GameFailedKnown && m.GameFailed) || m.Abandoned;
            bool allDone = m.TotalObjectives > 0 && m.CompletedObjectives >= m.TotalObjectives;
            bool ended = m.Ended || (m.GameActiveKnown && !m.GameActive && allDone);
            bool returned = m.GameActiveKnown && !m.GameActive && allDone && !failed;

            if (failed)
            {
                next = MissionLifecycleState.MissionFailed;
            }
            else if (returned)
            {
                next = MissionLifecycleState.MissionReturned;
            }
            else if (allDone || m.Ended)
            {
                next = MissionLifecycleState.MissionComplete;
                bool returnRequired = MissionReturnPolicy.ShouldReturnToSender(m);
                if (returnRequired && !rec.ReturnRequiredReported)
                {
                    rec.ReturnRequiredReported = true;
                    S.ReturnRequiredReports++;
                    pending.Add("MissionReturnRequired " + trackId
                        + " done=" + m.CompletedObjectives.ToString(CultureInfo.InvariantCulture)
                        + "/" + m.TotalObjectives.ToString(CultureInfo.InvariantCulture)
                        + " (deliver/turn-in pending)");
                }
            }
            else if (m.GameReadyTurnInKnown && m.GameReadyTurnIn)
            {
                // Accept/turn-in readiness edge before completion (some mission
                // flows accept the crew at the giver mid-objectives).
                next = MissionLifecycleState.MissionAccepting;
            }
            else if (opened)
            {
                next = MissionLifecycleState.MissionObjectivesDetected;
            }
            else if (rec.State == MissionLifecycleState.MissionDetected
                || rec.State == MissionLifecycleState.MissionObjectivesDetected)
            {
                // Detection -> work pipeline once objectives are known.
                next = m.TotalObjectives > 0
                    ? MissionLifecycleState.MissionObjectiveActive
                    : MissionLifecycleState.MissionDetected;
            }

            int lines = 0;
            if (opened)
            {
                S.Transitions++;
                lines++;
                pending.Add("MissionLifecycleOpened " + trackId
                    + " type=" + m.MissionTypeId.ToString(CultureInfo.InvariantCulture)
                    + " state=" + MissionLifecycleStateText.Text(next));
            }
            else if (next != before)
            {
                S.Transitions++;
                lines++;
                pending.Add("MissionLifecycle " + trackId
                    + " " + MissionLifecycleStateText.Text(before) + " -> " + MissionLifecycleStateText.Text(next));
            }

            rec.State = next;
            if (next != before || opened) rec.StateSinceMs = nowMs;

            // Terminal edge (once per record).
            if ((next == MissionLifecycleState.MissionComplete
                || next == MissionLifecycleState.MissionReturned
                || next == MissionLifecycleState.MissionFailed)
                && !rec.TerminalReported)
            {
                rec.TerminalReported = true;
                S.TerminalReports++;
                lines++;
                pending.Add("MissionLifecycleTerminal " + trackId
                    + " state=" + MissionLifecycleStateText.Text(next));
            }

            return lines;
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("MissionLifecycleUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Active.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.Evaluations = 0;
                S.MissionsTracked = 0;
                S.Transitions = 0;
                S.ReturnRequiredReports = 0;
                S.TerminalReports = 0;
                S.TempUnavailableReports = 0;
                S.StaleRejections = 0;
                S.PlansExpired = 0;
                S.LastUncertainReason = null;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_OnDecision = null;
            }
        }
    }
}