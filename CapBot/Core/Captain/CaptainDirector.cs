using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Emergency;
using CapBot.Core.Navigation;
using CapBot.Core.Combat;
using CapBot.Core.Capabilities;

namespace CapBot.Core.Captain
{
    // ---- Phase 18: captain deliberation director ("Captain Brain 2.0") ------------
    //
    // The fusion + narrow-authoring layer the docs promised: a bounded
    // deterministic CONSUMER of the P6 snapshot and the P9/P14/P15/P16/P17
    // director readbacks that (a) tracks captain-deliberation situations as
    // data and (b) authors EXACTLY ONE task family bound to EXACTLY ONE
    // capability — ISSUE_MOVE_ORDER (RegisteredCapabilities.IssueMoveOrder).
    //
    // Why ISSUE_MOVE_ORDER is the only authorable surface (ownership argument,
    // Phase 18 research report §"safe task-authorship space", all VERIFIED):
    //   - SET_CAPTAIN_ORDER: legacy commits orders through its own 2s/8s dwell
    //     machine (Patch.cs:246-266) and P9 is the sanctioned override author —
    //     a third author would thrash the order banner and share the 2000 ms
    //     registry cooldown, delaying P9 emergency orders. NOT safe.
    //   - SET_CAPTAIN_TARGET: explicit P17 covenant — authorship owned by P9 +
    //     the legacy captain tick. NOT safe.
    //   - ADD/REMOVE_COURSE_GOAL: P14 owns them (its header reserves the
    //     course-goal channels); legacy SetNextDestiny clears/rebuilds the
    //     course every ~5 s (Patch.cs:415-428). NOT safe.
    //   - CLEAR_COURSE_GOALS: legacy uses the RPC directly at four sites.
    //     Destructive. NOT safe.
    //   - READ_WORLD_SNAPSHOT: a read contract — directors read the world seam
    //     directly; authoring read-tasks is pointless. NOT authored.
    //   - ISSUE_MOVE_ORDER: ZERO in-tree authors, dispatcher branch ready
    //     (PulsarCapabilityDispatcher.DispatchIssueMoveOrder, SECTOR target),
    //     transient 20 s-TTL crew effect legacy never reads or writes,
    //     misfires self-heal in <= 20 s. THE one safe channel.
    //
    // The single authoring rule (deliberate, conservative, provable inputs):
    //   CREWGATHER — a live, non-captain, non-dead bot whose readable current
    //   location (CurrentTLIName) DIVERGES from the readable captain location,
    //   while the ship is calm: no P9 emergency (state Normal/Monitoring AND
    //   zero active emergencies), no P14 active plan, no P17 combat record,
    //   zero snapshot hostiles, no boarders, not in warp, sector known. After
    //   a 15 s stability dwell the director authors a CAPTAIN_DELIB task
    //   (owner CAPTAIN, priority 8, 60 s timeout, 1 retry, Preemptible) bound
    //   to ISSUE_MOVE_ORDER with a SECTOR target = the CURRENT sector, i.e.
    //   "gather crew to the captain's position". Health is carried as data
    //   only (wounded/dead crew are P9 territory — the calm gate keeps the
    //   two layers out of each other's hair); the trigger is LOCATION
    //   divergence, orthogonal to P9's health ladder.
    //
    // Everything else the captain "deliberates" about (orders, targets,
    // course changes, blind jumps, alert levels, shop, comms) stays owned by
    // legacy/P9/P14 and is NOT authored here — the director's remaining
    // output is bounded diagnostic data for later phases.
    //
    // Multiplayer: the coordinator never RPCs. Tasks flow through the P4/P5/
    // P7/P8 pipeline, which already enforces master-side execution and the
    // correct masks (deny-by-default authority seam keeps clients silent).
    //
    // Boundaries (Phase 18 contract):
    //   - No new Harmony patch class (permanent ceiling of 11 preserved; the
    //     WorldTick Postfix gains the Evaluate + ReconcileTasks blocks IN
    //     PLACE, after the combat block).
    //   - No direct game/RPC calls, no scene scans, no FindObjectsOfType, no
    //     LINQ, no per-frame work (cadence-gated 5 s).
    //   - Fail-safe on stale/missing/never-captured/not-started snapshots
    //     (same 20 s window as P9/P14/P15/P16/P17); unknown crew data never
    //     triggers (unknown-input accounting like P16/P17).
    //   - All timestamps are explicit nowMs values (TaskClock semantics); no
    //     wall-clock reads; every collection bounded.
    //   - Config: NO new SaveValue (no Phase 18 config contract was promised;
    //     the dead H4 sliders remain untouched).
    public static class CaptainDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 5000;             // decision cadence (1 Hz snapshot, 5 s decisions)
        public const int MaxActiveIntents = 8;            // bounded tracked-intent set
        public const int MaxHistory = 16;                 // bounded resolved-intent history
        public const int ActiveExpiryMs = 30000;          // divergence cleared past this => intent decays
        public const int MaxStaleSnapshotMs = 20000;      // fail-safe: no decisions on older snapshots
        public const int AuthoringDwellMs = 15000;        // divergence must persist this long before authoring
        public const int AuthoringRequeueBlockMs = 20000; // re-arm delay after a deliberation task resolves
        public const int DeliberationTaskTimeoutMs = 60000; // deliberation tasks self-expire (bounded work)
        public const int DeliberationPriority = 8;        // top of normal band 1..8 — never preempts P14 (20) / P9 (110+)
        public const int MaxAuthoringsPerIntent = 3;      // anti-churn cap per intent record lifetime
        public const int MaxPendingLines = 4;             // bounded emission buffer per pass
        public const int MaxTextLen = 120;                // bounded DATA carry for location names

        public const string TrackIdPrefix = "CAPTAIN:";   // "CAPTAIN:<kind>"
        public const string TrackCrewGather = "CREWGATHER"; // TrackId "CAPTAIN:CREWGATHER"
        public const string TargetKindSector = "SECTOR";  // the dispatcher's ISSUE_MOVE_ORDER derivation target
        public const string TaskTypeDeliberation = "CAPTAIN_DELIB"; // static vocabulary, never parsed
        public const string OwnerCaptain = "CAPTAIN";     // matches the P7 capability owners + the P9/P14 task owner

        // Public readback record (GetIntent surface — mirrors
        // CombatRecord/EconomyRecord: a public class with public fields; the
        // director hands out the live record for bounded diagnostic reads).
        public sealed class CaptainIntent
        {
            public readonly string TrackId;          // "CAPTAIN:<kind>"
            public readonly string Kind;
            public readonly int FirstSeenMs;

            public int LastSeenMs;                   // last pass with readable inputs
            public long UpdateCount;
            public bool OpenedReported;              // CaptainIntentOpened fired
            public int AuthoringsIssued;             // deliberation tasks issued for this intent
            public int LastAuthorMs = -1;            // -1 = never authored
            public long TaskId;                      // 0 = no task ever issued
            public int TaskResolvedMs = -1;          // -1 = no resolved task yet (re-arm blocker)

            public CaptainIntent(string trackId, string kind, int nowMs)
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
            public readonly Dictionary<string, CaptainIntent> Active =
                new Dictionary<string, CaptainIntent>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;

            // crew-gather episode state (single CAPTAIN:CREWGATHER record)
            public bool EpisodeOpen;                 // divergence present and readable
            public int EpisodeFirstSeenMs = -1;      // first readable divergence pass of this episode
            public int LastDivergentCount = -1;      // last readable divergent count (-1 unknown)

            public long Evaluations;
            public long IntentsTracked;
            public long OpenedReports;
            public long AuthoringsIssued;
            public long CalmGateBlocks;
            public long DuplicatesSuppressed;
            public long AuthoringCapped;
            public long AuthoringRefused;
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
        private static Action<string> m_OnDecision;         // CaptainLogBridge attaches at boot

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
        public static long AuthoringsIssuedCount { get { lock (m_Lock) return S.AuthoringsIssued; } }
        public static long CalmGateBlockCount { get { lock (m_Lock) return S.CalmGateBlocks; } }
        public static long DuplicatesSuppressedCount { get { lock (m_Lock) return S.DuplicatesSuppressed; } }
        public static long AuthoringCappedCount { get { lock (m_Lock) return S.AuthoringCapped; } }
        public static long AuthoringRefusedCount { get { lock (m_Lock) return S.AuthoringRefused; } }
        public static long IntentsExpiredCount { get { lock (m_Lock) return S.IntentsExpired; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static long UnknownInputPassCount { get { lock (m_Lock) return S.UnknownInputPasses; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        // Deterministic lookup by intent id (null when absent).
        public static CaptainIntent GetIntent(string intentId)
        {
            if (string.IsNullOrEmpty(intentId)) return null;
            lock (m_Lock)
            {
                CaptainIntent r;
                return S.Active.TryGetValue(intentId, out r) ? r : null;
            }
        }

        // One bounded diagnostic line per tracked intent (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CaptainIntent> kv in S.Active)
                {
                    CaptainIntent r = kv.Value;
                    lines.Add("intent " + r.TrackId
                        + " upd=" + r.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (r.OpenedReported ? " opened" : "")
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
                lines.Add("captain=" + S.Active.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tracked=" + S.IntentsTracked
                    + " opened=" + S.OpenedReports + " authored=" + S.AuthoringsIssued
                    + " calmBlocked=" + S.CalmGateBlocks + " dupSuppressed=" + S.DuplicatesSuppressed);
                lines.Add("capped=" + S.AuthoringCapped + " refused=" + S.AuthoringRefused
                    + " expired=" + S.IntentsExpired + " stale=" + S.StaleRejections
                    + " unknownInputs=" + S.UnknownInputPasses
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
                MarkUncertain("no world snapshot captured (fail-safe: no captain deliberation)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no captain deliberation)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no captain deliberation)");
                return 0;
            }

            List<string> pending = new List<string>(MaxPendingLines);
            int reports = 0;

            lock (m_Lock)
            {
                // ---- inputs ------------------------------------------------
                IReadOnlyList<CrewMemberSnapshot> crew = snapshot.Crew;
                NavigationSnapshot navigation = snapshot.Navigation;

                // Crew-gather rule inputs. Unknown sentinels never trigger:
                //   - no readable captain location => the whole rule is unknown;
                //   - a bot with unknown liveness/TLI is excluded (fail-safe)
                //     and counted as an unknown input;
                //   - dead bots are readable-but-excluded (gathering corpses
                //     is not a deliberation outcome).
                string captainTli = null;
                bool captainReadable = false;
                for (int i = 0; crew != null && i < crew.Count && i < WorldSnapshot.MaxCrew; i++)
                {
                    CrewMemberSnapshot c = crew[i];
                    if (c == null || !c.IsCaptain) continue;
                    captainReadable = c.CurrentTLIName != null && c.CurrentTLIName.Length > 0;
                    if (captainReadable) captainTli = c.CurrentTLIName;
                    break;
                }

                int divergent = 0;
                bool anyUnknown = false;
                bool sawBot = false;
                for (int i = 0; crew != null && i < crew.Count && i < WorldSnapshot.MaxCrew; i++)
                {
                    CrewMemberSnapshot c = crew[i];
                    if (c == null || !c.IsBot || c.IsCaptain) continue;
                    sawBot = true;
                    bool aliveReadable = c.AliveKnown;
                    bool alive = c.Alive;
                    bool tliReadable = c.CurrentTLIName != null && c.CurrentTLIName.Length > 0 && c.CurrentTLIName.Length <= MaxTextLen;
                    if (!aliveReadable || !alive)
                    {
                        // Unknown liveness (capture fault) => unknown input.
                        // Readable death => excluded, not unknown (P9 owns dead/
                        // wounded crew through its health ladder).
                        if (!aliveReadable) anyUnknown = true;
                        continue;
                    }
                    if (!tliReadable)
                    {
                        // An alive bot with no readable location is unknown data.
                        anyUnknown = true;
                        continue;
                    }
                    if (captainReadable && captainTli != null
                        && !string.Equals(c.CurrentTLIName, captainTli, System.StringComparison.Ordinal))
                    {
                        divergent++;
                    }
                }

                int hostileCount = snapshot.Threats != null ? snapshot.Threats.KnownHostileShipIds.Count : 0;
                int invaders = snapshot.Threats != null ? snapshot.Threats.InvadersOnboardCount : -1;
                bool inWarp = navigation != null && navigation.InWarp;
                int currentSectorId = navigation != null ? navigation.CurrentSectorId : -1;

                bool divergentReadable = captainReadable && divergent > 0;

                // ---- crew-gather episode ----------------------------------------
                if (divergentReadable)
                {
                    EnsureIntentTracked(nowMs, pending, ref reports);
                    if (!S.EpisodeOpen)
                    {
                        // Fresh deliberation episode: the authoring dwell clock
                        // starts on the first readable divergence pass.
                        S.EpisodeOpen = true;
                        S.EpisodeFirstSeenMs = nowMs;
                        S.LastDivergentCount = divergent;
                    }
                    EvaluateCrewGather(
                        divergent, inWarp, currentSectorId,
                        hostileCount, invaders, nowMs, pending, ref reports);
                }
                else
                {
                    // No readable divergence: close any open episode. The
                    // tracked record itself survives (hygiene owns expiry);
                    // only the episode state resets (authoring budget persists
                    // with the record — same discipline as P17's live-record
                    // re-entry semantics).
                    if (S.EpisodeOpen)
                    {
                        S.EpisodeOpen = false;
                        S.EpisodeFirstSeenMs = -1;
                        S.LastDivergentCount = -1;
                    }
                    if (anyUnknown || (!captainReadable && sawBot)) S.UnknownInputPasses++;
                }

                // ---- hygiene: expire the intent when divergence stays absent
                // past ActiveExpiryMs (one-shot expired report, P15/P16/P17
                // mirror). A LIVE intent refreshes every readable pass and
                // never expires while divergence remains.
                if (!divergentReadable)
                {
                    List<string> expired = new List<string>(MaxActiveIntents);
                    foreach (KeyValuePair<string, CaptainIntent> kv in S.Active)
                    {
                        if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs) expired.Add(kv.Key);
                    }
                    for (int i = 0; i < expired.Count; i++)
                    {
                        CaptainIntent expiredRec = S.Active[expired[i]];
                        S.Active.Remove(expired[i]);
                        S.HistoryIds.Enqueue(expired[i]);
                        while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                        S.PlansExpired++;
                        S.IntentsExpired++;
                        reports++;
                        pending.Add("CaptainIntentExpired " + expiredRec.TrackId);
                    }
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count; i++) Emit(pending[i]);
            return reports;
        }

        // ---- rules -------------------------------------------------------------------

        private static void EnsureIntentTracked(int nowMs, List<string> pending, ref int reports)
        {
            string trackId = TrackIdPrefix + TrackCrewGather;
            CaptainIntent rec;
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
                // record kind); kept for house-pattern parity (P17 mirror).
                string oldest = null;
                foreach (KeyValuePair<string, CaptainIntent> okv in S.Active)
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
                    pending.Add("CaptainShed " + oldest + " (tracked set full)");
                }
            }

            rec = new CaptainIntent(trackId, TrackCrewGather, nowMs);
            S.Active[trackId] = rec;
            S.IntentsTracked++;
            S.OpenedReports++;
            reports++;
            pending.Add("CaptainIntentOpened " + trackId);
        }

        // Crew-gather authoring: one CAPTAIN_DELIB task (ISSUE_MOVE_ORDER,
        // SECTOR target = the current sector) once divergence has persisted
        // AuthoringDwellMs AND the ship is calm. Re-armed after
        // AuthoringRequeueBlockMs from the task's resolution; capped at
        // MaxAuthoringsPerIntent per record lifetime (anti-churn).
        private static void EvaluateCrewGather(
            int divergent, bool inWarp, int currentSectorId,
            int hostileCount, int invaders, int nowMs,
            List<string> pending, ref int reports)
        {
            CaptainIntent rec = S.Active[TrackIdPrefix + TrackCrewGather];

            if (rec.HasLiveTask)
            {
                S.DuplicatesSuppressed++;
                return;
            }

            if (rec.AuthoringsIssued >= MaxAuthoringsPerIntent)
            {
                // Anti-churn cap: the situation keeps being tracked (data),
                // but this record never authors again. A decayed record
                // (hygiene) resets the budget with a fresh episode.
                S.AuthoringCapped++;
                return;
            }

            bool dwellMet = S.EpisodeFirstSeenMs >= 0
                && unchecked(nowMs - S.EpisodeFirstSeenMs) >= AuthoringDwellMs;
            bool requeueMet = rec.TaskResolvedMs < 0
                || unchecked(nowMs - rec.TaskResolvedMs) >= AuthoringRequeueBlockMs;
            if (!dwellMet || !requeueMet) return;

            // ---- the calm gate ---------------------------------------------------
            // Deliberation only authoring when NOTHING else is in flight:
            // emergencies (P9 state + active records), recovery plans (P14),
            // combat (P17 records + snapshot hostiles/boarders), warp. The
            // gate is fail-closed: any unreadable input blocks authoring
            // (unknown data never triggers — the detector contract).
            bool calm = IsEmergencyCalm() && NavigationRecoveryDirector.ActivePlanCount == 0
                && CombatDirector.ActiveRecordCount == 0
                && hostileCount == 0 && invaders <= 0 && !inWarp && currentSectorId >= 0;
            if (!calm)
            {
                S.CalmGateBlocks++;
                return;
            }

            string sectorText = currentSectorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            long taskId = CreateDeliberationTaskLocked(rec, currentSectorId, divergent, nowMs, pending);
            if (taskId > 0) reports++;
        }

        // P9 calm probe: the emergency system must be entirely quiescent —
        // state Normal/Monitoring AND zero active emergencies. Lock-nested
        // reads (Captain lock -> Emergency lock; one-way, deadlock-safe).
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

        // Creates + registers + queues one deliberation task (callers hold
        // m_Lock). Deterministic shape: CAPTAIN-owned CAPTAIN_DELIB task with
        // the P7 ISSUE_MOVE_ORDER capability bound via metadata. Returns the
        // task id (0 = refused — register/queue failures fail safe, no retry
        // storm: the intent stays open and the next dwell window re-arms).
        private static long CreateDeliberationTaskLocked(
            CaptainIntent rec, int sectorId, int divergent, int nowMs, List<string> pending)
        {
            string sectorText = sectorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CapBotTask task = CapBotTask.Create(
                TaskTypeDeliberation,
                OwnerCaptain,
                "captain deliberation: crew not with captain; gather to current sector",
                DeliberationPriority,
                1,                       // one retry — deliberation may retry once; never a storm
                DeliberationTaskTimeoutMs,
                TargetKindSector,
                sectorText,
                null);
            if (task == null) return 0;

            if (!task.SetMetadata("DelibId", rec.TrackId)) return 0;
            if (!task.SetMetadata("DelibKind", rec.Kind)) return 0;
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
                + " divergent=" + divergent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " (gather crew to the captain's position; transient 20s-TTL crew order)");
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
                foreach (KeyValuePair<string, CaptainIntent> kv in S.Active)
                {
                    CaptainIntent r = kv.Value;
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
            Emit("CaptainUncertain " + reason);
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
                S.LastDivergentCount = -1;
                S.Evaluations = 0;
                S.IntentsTracked = 0;
                S.OpenedReports = 0;
                S.AuthoringsIssued = 0;
                S.CalmGateBlocks = 0;
                S.DuplicatesSuppressed = 0;
                S.AuthoringCapped = 0;
                S.AuthoringRefused = 0;
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