using System;
using System.Collections.Generic;
using CapBot.Core.Crew;
using CapBot.Core.World;

namespace CapBot.Core.Learning
{
    // ---- Phase 26: trait profile director (the trait consumer) ------------------
    //
    // The consumer phase the deferral trail names: P25 matures personality
    // traits through the sole sanctioned write path, and this director READS
    // them — deterministically, as DATA — turning trait values into bounded,
    // human-readable crew-composition signals. It is the deterministic read
    // half of the personality arc; the P21 CrewAdvisor (LLM path) reads the
    // same registry independently and is never consumed by this layer.
    //
    // Design basis note: no master-plan document exists in the workspace
    // defining Phase 26 numerically (verified: the only "consumer phase" text
    // in the tree is AdaptiveLearningDirector's own P25 comment). The design
    // is INFERRED from the verified deferral-trail constraints:
    //
    //   1. Inputs: the P11 public readback surface — CrewPersonalityRegistry
    //      (ArchetypeOf / AffinityToRole / personality traits) and the P25
    //      AdaptiveLearningDirector.GetRecord readback (AdjustCount > 0 =>
    //      a matured personality exists). Roster: CrewAgentRegistry.AgentViews
    //      (the P21-sanctioned read surface; deterministic AgentId order).
    //      World gate: the shared 20 s snapshot standard (P9/P10/P24 shape).
    //   2. Semantics: one-shot report per signal per record lifetime
    //      (P15/P16/P17/P22/P23/P24 house mirror) — a trait change is an
    //      EVENT, not a persistent condition; persistence is handled by the
    //      record snapshot itself (traits refreshed every readable pass).
    //   3. Rules (per active crew agent, first class only):
    //        - DOMINANT   — archetype is a named archetype (not BALANCED):
    //                       "this agent's dominant-trait profile";
    //        - LOWAFFINITY — RoleAffinity score for the agent's CURRENT role
    //                       is below the threshold (40): a trait-vs-role
    //                       mismatch worth surfacing (never a task, never a
    //                       reassignment — assignment is scheduler-mediated
    //                       and owned by other phases);
    //        - MATURED    — the P25 learning layer has adjusted this
    //                       agent's traits (AdjustCount > 0) at first sight:
    //                       the consumer-visible maturation readback;
    //        - NOPERS     — global, one record: active agents exist while the
    //                       personality registry has never held a record —
    //                       the diagnostic that keeps the consumer honest.
    //   4. Traits are consumed but never re-written: this layer performs NO
    //      SetPersonality, NO DeriveFor, NO task operation, NO scheduler/
    //      recovery/claims/executor/validator call, and never mutates another
    //      phase's statics. Every write it needs already happened (P25) or
    //      belongs to another phase (assignment).
    //
    // Tick driver: the WorldTick Postfix gains one guarded block IN PLACE
    // (after the P24 block — it observes P25 writes that have already been
    // applied by the funnel), keeping the 11-class Harmony ceiling. Deny-by-
    // default authority; no config toggle (the P18-P24 deterministic-director
    // precedent); baseline arm pass is observation-free (the P22/P24
    // premise-capture analogue).
    //
    // Boundaries (Phase 26 contract):
    //   - Reads ONLY public readbacks: CrewAgentRegistry.AgentViews(),
    //     CrewPersonalityRegistry.ArchetypeOf/AffinityToRole/Count,
    //     AdaptiveLearningDirector.GetRecord, and the world snapshot gate.
    //   - Never throws: every seam fault is a counted, fail-closed pass.
    //   - No game reads (agents/snapshot arrive through seams), no wall-clock
    //     reads (nowMs is TaskClock semantics), no LINQ, no per-frame work,
    //     no LLM in any deterministic path (the P21 advisor reads the same
    //     registry independently and is never consumed here).
    //   - Every collection is bounded; same inputs => same decisions.
    public static class TraitProfileDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 5000;           // house decision cadence (P18/P22/P23/P24 parity)
        public const int MaxStaleSnapshotMs = 20000;    // shared freshness standard
        public const int MaxRecords = 32;               // = CrewAgentRegistry.MaxAgents / MaxPersonalities
        public const int MaxHistory = 16;               // bounded expired-record id history
        public const int ActiveExpiryMs = 30000;        // agent absent past this => record decays
        public const int MaxPendingLines = 4;           // bounded emission queue per pass (house bound)
        public const int ReportBlockMs = 20000;         // anti-churn re-report block per record (P24 parity)
        public const int LowAffinityThreshold = 40;     // affinity below this on a named role => LOWAFFINITY signal
        public const string TrackIdPrefix = "TRAIT:";   // record ids are "TRAIT:<agentId>"; global "TRAIT:<TOKEN>"

        // Global track tokens (never an agentId — agent ids are "AGT:<hex8>",
        // so these cannot collide).
        public const string TrackNoPersonality = "NOPERS";

        // One bounded trait profile record per active crew agent (keyed by the
        // SAME stable AgentId the P10/P11/P25 layers use). Created only by
        // Evaluate; mutated only under the director lock.
        public sealed class TraitProfileRecord
        {
            public readonly string AgentId;
            public int ArchetypeId;         // 0 = none/balanced, 1..5 = first-class archetype index
            public int AffinityScore;       // last computed role-affinity score (-1 = unknown role)
            public long UpdateCount;
            public int LastSeenMs;          // -1 = never
            public long ReportCount;        // one-shot reports this record lifetime
            public int LastReportMs;        // -1 = never reported
            public bool Matured;            // P25 AdjustCount > 0 at last readback

            public TraitProfileRecord(string agentId, int nowMs)
            {
                AgentId = agentId;
                LastSeenMs = nowMs;
                LastReportMs = -1;
            }

            public string ToLine()
            {
                return "traitProfile " + TrackIdPrefix + AgentId
                    + " arch=" + ArchetypeId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " aff=" + AffinityScore.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " matured=" + (Matured ? "1" : "0")
                    + " reports=" + ReportCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // Archetype ids (static, deterministic; mirror PersonalityArchetypes
        // vocabulary in fixed enum order).
        internal static int ArchetypeIdOf(string archetype)
        {
            if (archetype == PersonalityArchetypes.Sentinel) return 1;
            if (archetype == PersonalityArchetypes.Vanguard) return 2;
            if (archetype == PersonalityArchetypes.Coordinator) return 3;
            if (archetype == PersonalityArchetypes.Technician) return 4;
            if (archetype == PersonalityArchetypes.Adapter) return 5;
            return 0; // BALANCED / null / unknown
        }

        private sealed class DirectorState
        {
            public readonly Dictionary<string, TraitProfileRecord> Records =
                new Dictionary<string, TraitProfileRecord>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;
            public bool HasPrevPass;        // baseline arm pass (P22/P24 analogue)
            public bool NoPersReported;     // NOPERS is session-global one-shot
            public long Evaluations;
            public long RecordsTracked;
            public long Reports;
            public long RecheckBlocks;
            public long DuplicatesSuppressed;
            public long RecordsExpired;
            public long StaleRejections;
            public long UnknownInputPasses;
            public string LastUncertainReason;
            public string LastReport;
        }

        private static readonly DirectorState S = new DirectorState();
        private static readonly object m_Lock = new object();
        private static Func<bool> m_AuthorityProbe;         // null/fault => no-op evaluation
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_OnDecision;         // TraitProfileLogBridge attaches at boot

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_AuthorityProbe = probe; }
        public static void SetWorldProvider(Func<WorldSnapshot> provider) { lock (m_Lock) m_WorldProvider = provider; }
        public static void SetDecisionListener(Action<string> listener) { lock (m_Lock) m_OnDecision = listener; }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // ---- readback (diagnostics/tests) ------------------------------------
        public static int RecordCount { get { lock (m_Lock) return S.Records.Count; } }
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
        public static string LastReport { get { lock (m_Lock) return S.LastReport; } }

        // Deterministic lookup (null when absent or invalid). Returns the live
        // record reference (house readback shape).
        public static TraitProfileRecord GetRecord(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            lock (m_Lock)
            {
                TraitProfileRecord r;
                return S.Records.TryGetValue(agentId, out r) ? r : null;
            }
        }

        // One bounded diagnostic line per record (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, TraitProfileRecord> kv in S.Records)
                {
                    lines.Add(kv.Value.ToLine());
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
                lines.Add("traitProfiles=" + S.Records.Count
                    + " tracked=" + S.RecordsTracked.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " reports=" + S.Reports.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " recheckBlocks=" + S.RecheckBlocks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " dupSuppressed=" + S.DuplicatesSuppressed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " expired=" + S.RecordsExpired.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " staleRejected=" + S.StaleRejections.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " unknownInputPasses=" + S.UnknownInputPasses.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("lastReport=" + (S.LastReport ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Gate order mirrors the P18/P22/P23/P24 house shape:
        // authority (deny-by-default) => cadence => snapshot fail-safe =>
        // bounded rules. Returns the number of NEW lines/reports this pass
        // (0 on every failure path). Never throws. Authors NOTHING, mutates
        // NO task, writes NO trait.
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
            // Trait data is session-scoped and only meaningful against a live
            // world; the snapshot freshness is the precondition.
            WorldSnapshot snapshot = null;
            if (world != null)
            {
                try { snapshot = world(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured)
            {
                MarkUncertain("no world snapshot captured (fail-safe: no trait assessment)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no trait assessment)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no trait assessment)");
                return 0;
            }

            List<string> pending = new List<string>(MaxPendingLines);
            int reports = 0;

            // ---- inputs (bounded public readbacks only). Read OUTSIDE our
            // lock: these are other classes' thread-safe readbacks and none
            // of them ever takes the trait-profile lock (one-way lock order,
            // the P24 readback precedent). Any fault => fail-closed pass.
            List<CrewAgentRegistry.AgentView> agents;
            int personalityCount;
            try
            {
                agents = CrewAgentRegistry.AgentViews();
                personalityCount = CrewPersonalityRegistry.Count;
            }
            catch (Exception)
            {
                lock (m_Lock) S.UnknownInputPasses++;
                MarkUncertain("a readback seam faulted (fail-closed: no trait assessment)");
                return 0;
            }

            // Per-agent personality/affinity/learning readbacks also stay
            // OUTSIDE the director lock (same one-way order; bounded agent
            // count). A fault on one agent skips that agent only (counted
            // under our lock at pass end — never a torn counter).
            List<string> agentIds = new List<string>(MaxRecords);
            List<int> archetypeIds = new List<int>(MaxRecords);
            List<int> affinities = new List<int>(MaxRecords);
            List<bool> matured = new List<bool>(MaxRecords);
            int agentFaults = 0;
            for (int i = 0; i < agents.Count && agentIds.Count < MaxRecords; i++)
            {
                CrewAgentRegistry.AgentView a = agents[i];
                if (a == null || a.AgentId == null || a.Lifecycle != CrewAgentLifecycle.Active)
                    continue;
                try
                {
                    int archId = ArchetypeIdOf(CrewPersonalityRegistry.ArchetypeOf(a.AgentId, nowMs));
                    int aff = a.Role == CrewRole.Captain || a.Role == CrewRole.Pilot
                        || a.Role == CrewRole.Scientist || a.Role == CrewRole.Weapons
                        || a.Role == CrewRole.Engineer
                        ? CrewPersonalityRegistry.AffinityToRole(a.AgentId, a.Role, nowMs)
                        : -1;
                    AdaptiveLearningDirector.LearningRecord lr = AdaptiveLearningDirector.GetRecord(a.AgentId);
                    bool maturedNow = lr != null && lr.AdjustCount > 0;
                    agentIds.Add(a.AgentId);
                    archetypeIds.Add(archId);
                    affinities.Add(aff);
                    matured.Add(maturedNow);
                }
                catch (Exception)
                {
                    agentFaults++;
                    // Skip this agent only; the rest of the pass stays sound.
                }
            }

            lock (m_Lock)
            {
                // Baseline arm pass (P22/P24 analogue): the first readable
                // pass arms quietly — deltas/records need a world; signals
                // start on the SECOND readable pass.
                if (!S.HasPrevPass)
                {
                    S.HasPrevPass = true;
                    S.Evaluations++;
                    return 0;
                }

                // ---- global rule 1: NOPERS ----------------------------------
                // Active agents but the personality registry has NEVER held a
                // record: the consumer has nothing to consume — surface it
                // once (session-global one-shot; never fabricated data).
                if (agentIds.Count > 0 && personalityCount == 0 && !S.NoPersReported)
                {
                    S.NoPersReported = true;
                    S.Reports++;
                    reports++;
                    S.LastReport = TrackIdPrefix + TrackNoPersonality;
                    if (pending.Count < MaxPendingLines)
                        pending.Add("TraitSignal " + TrackIdPrefix + TrackNoPersonality
                            + " activeCrew=" + agentIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " personalities=0"
                            + " (no personality data to consume; nothing fabricated)");
                }

                // ---- per-agent records + one-shot signals -------------------
                for (int i = 0; i < agentIds.Count; i++)
                {
                    reports += ProfileAgentLocked(
                        agentIds[i], archetypeIds[i], affinities[i], matured[i],
                        nowMs, pending);
                }

                // ---- hygiene: expire profiles whose agent stays absent past
                // ActiveExpiryMs (P15/P16/P17/P24 mirror).
                List<string> expired = new List<string>(MaxRecords);
                foreach (KeyValuePair<string, TraitProfileRecord> kv in S.Records)
                {
                    if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs) expired.Add(kv.Key);
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    TraitProfileRecord expiredRec = S.Records[expired[i]];
                    S.Records.Remove(expired[i]);
                    S.HistoryIds.Enqueue(expiredRec.AgentId);
                    while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                    S.RecordsExpired++;
                    reports++;
                    if (pending.Count < MaxPendingLines)
                        pending.Add("TraitProfileExpired " + TrackIdPrefix + expiredRec.AgentId);
                }

                S.Evaluations++;
            }
            // lock released — emissions deferred to the single exit below.

            for (int i = 0; i < pending.Count && i < MaxPendingLines; i++) Emit(pending[i]);
            return reports;
        }

        // Per-agent one-shot semantics (P24 record mirror):
        //   agent first seen          => track + report its profile immediately;
        //   profile persists          => silent refresh (DuplicatesSuppressed);
        //   signal re-fires after a clear, within the record's lifetime
        //                             => re-report only after ReportBlockMs
        //                                (anti-churn), else RecheckBlocks++ and
        //                                silent;
        //   agent absent              => hygiene decays the record after
        //                                ActiveExpiryMs; re-seen arms a FRESH
        //                                record (budget re-armed).
        // Maturity is re-read every readable pass; a maturation that appears
        // on a KNOWN profile re-fires the MATURED signal under the same
        // block rules (the trait EVENT is the trigger, not the condition).
        private static int ProfileAgentLocked(
            string agentId, int archetypeId, int affinity, bool matured,
            int nowMs, List<string> pending)
        {
            TraitProfileRecord rec;
            bool fresh = !S.Records.TryGetValue(agentId, out rec);
            if (fresh)
            {
                if (S.Records.Count >= MaxRecords)
                {
                    // The record bound equals the agent bound, so this is
                    // defensive only; deterministic refusal, never a size
                    // change, never a live-record shed.
                    return 0;
                }
                rec = new TraitProfileRecord(agentId, nowMs);
                S.Records[agentId] = rec;
                S.RecordsTracked++;
            }

            bool wasSeen = rec.LastSeenMs >= 0;
            rec.LastSeenMs = nowMs;
            rec.UpdateCount++;

            // First report of this record lifetime, or a re-fire after a
            // clear. Re-fire is rate-limited by the report block.
            if (!fresh && rec.LastReportMs >= 0
                && unchecked(nowMs - rec.LastReportMs) < ReportBlockMs)
            {
                S.RecheckBlocks++;
                return 0;
            }
            if (!fresh && wasSeen)
            {
                S.DuplicatesSuppressed++;
                // Refresh the snapshot before deciding to re-report.
            }

            rec.ArchetypeId = archetypeId;
            rec.AffinityScore = affinity;
            rec.Matured = matured;

            string detail = null;
            if (matured) detail = "matured";
            else if (affinity >= 0 && affinity < LowAffinityThreshold) detail = "lowAffinity aff=" + affinity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            else if (archetypeId > 0) detail = "dominant arch=" + archetypeId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (detail == null)
            {
                // Balanced, known-affinity, never-matured profile: tracked,
                // quiet.
                return 0;
            }

            rec.ReportCount++;
            rec.LastReportMs = nowMs;
            S.Reports++;
            int reports = 1;
            S.LastReport = TrackIdPrefix + agentId + " " + detail;
            if (pending.Count < MaxPendingLines)
                pending.Add("TraitSignal " + TrackIdPrefix + agentId + " " + detail
                    + " (read-only trait consumer; traits are data)");
            return reports;
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("TraitUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Records.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.HasPrevPass = false;
                S.NoPersReported = false;
                S.Evaluations = 0;
                S.RecordsTracked = 0;
                S.Reports = 0;
                S.RecheckBlocks = 0;
                S.DuplicatesSuppressed = 0;
                S.RecordsExpired = 0;
                S.StaleRejections = 0;
                S.UnknownInputPasses = 0;
                S.LastUncertainReason = null;
                S.LastReport = null;
                m_AuthorityProbe = null;
                m_WorldProvider = null;
                m_OnDecision = null;
            }
        }
    }
}