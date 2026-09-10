using System;
using System.Collections.Generic;
using CapBot.Core.Crew;

namespace CapBot.Core.Learning
{
    // ---- Phase 25: adaptive learning -------------------------------------------
    //
    // Bounded, deterministic trait maturation for crew agents. This is the
    // phase the tree's contracts assign the "adjust/adaptive" vocabulary to:
    // it adjusts personality trait VALUES — through the P11 SetPersonality
    // write path, the sole sanctioned personality write channel (P11/P12
    // contracts; docs/CREW_EXPERIENCE.md section 9; docs/CREW_PERSONALITIES.md
    // section 10) — using Level/ExperiencePoints as inputs, exactly as the
    // deferral trail names. Every other layer stays read-only on personality.
    //
    // Design basis note: no master-plan document exists in the workspace
    // defining Phase 25 (verified by the P25 research report — "Phase 25"
    // hits are all deferrals naming inputs + write path, zero numeric rules).
    // The design is INFERRED from those verified constraints:
    //
    //   1. Inputs: CrewExperience Level/ExperiencePoints per agent
    //      (docs/CREW_EXPERIENCE.md:129-131), read through the same ClearTask
    //      funnel that feeds P12 accrual.
    //   2. Write path: CrewPersonalityRegistry.SetPersonality with records
    //      built by PersonalityFactory.FromValues (the explicit-source path
    //      CREW_PERSONALITIES.md says "exists for exactly this").
    //   3. Trigger: a LEVEL CROSSING (the level recorded on the experience
    //      record rises past the level this layer last armed) — naturally
    //      rare, so anti-churn is structural: one adjustment decision per
    //      crossing, one crossing per level, levels are bounded 1..10.
    //   4. Direction: picked deterministically from the agent's OWN outcome
    //      mix at the crossing moment (first match wins):
    //        - completion-dominant  (2*TasksCompleted  >= TotalOutcomes) => +1 Diligence
    //        - adversity-dominant   (2*(TasksFailed+TasksVanished) >= TotalOutcomes) => +1 Adaptability
    //        - otherwise no trait has a justified direction => the crossing is
    //          consumed silently (never fabricate an adjustment).
    //   5. Traits remain DATA until a consumer phase reads them: nothing in
    //      the tree reads trait values today (P25 research report, verified),
    //      so a maturation write changes diagnostics only — the same
    //      data-layer-before-consumer pattern as P15->P18 and P22->P23.
    //
    // Integration is EVENT-DRIVEN off the P10 ClearTask funnel: one additive
    // fail-safe hook after the P12/P13 hooks (CrewAgentRegistry.ClearTask),
    // fired OUTSIDE the agent-registry lock with its own try/catch. NO new
    // Harmony patch class (the 11-class ceiling is untouched), no tick
    // driver, no WorldTick block.
    //
    // Boundaries (Phase 25 contract):
    //   - This layer writes ONLY CrewPersonalityRegistry (via SetPersonality).
    //     It never creates/queues/claims/cancels/executes tasks, never calls
    //     TaskScheduler/TaskRecoveryManager/TaskExecutor/ExecutionClaims/
    //     CapabilityRegistry/DecisionValidator, never touches TaskRegistry,
    //     and never mutates another phase's statics.
    //   - CrewExperience is READ ONLY here (SnapshotOf defensive copy); the
    //     learning layer never accrues, removes, or fabricates experience.
    //   - Deny-by-default authority: with no probe, a faulting probe, or a
    //     non-authoritative caller, NotifyOutcome is a no-op (clients inert).
    //   - No game reads, no wall-clock reads (nowMs is TaskClock semantics
    //     passed down the funnel), no LINQ, no per-frame work, no LLM.
    //   - Every collection is bounded; every input validated (P10 outcome
    //     vocabulary compared, never parsed); same inputs => same decisions.
    //
    // Reproducibility note (documented contract gap, P25 research flag):
    // a maturation-updated personality is explicit-source — it no longer
    // rebuilds identically from the agent identity after a registry reset or
    // host change (PersonalityFactory.Derive is a pure function; explicit
    // records are not). Process-local by design; cross-session persistence
    // of matured personalities is P28's assignment, not this phase's.
    public static class AdaptiveLearningDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 1000;          // evaluation gate per record (P10 sync cadence; storms are rate-limited, crossings persist)
        public const int MaxRecords = 32;              // = CrewAgentRegistry.MaxAgents / MaxPersonalities / experience MaxRecords
        public const int MaxHistory = 16;              // bounded expired-record id history
        public const int ActiveExpiryMs = 30000;       // no outcome for this long => record decays (hygiene; re-arm on next outcome)
        public const int MaxPendingLines = 4;          // bounded emission queue per pass (P18/P22/P23/P24 house bound)
        public const int AdjustmentDelta = 1;          // fixed trait nudge per crossing (bounded monotone drift; ClampTrait bounds the ceiling)
        public const string TrackIdPrefix = "LEARN:";  // record ids are "LEARN:<agentId>"

        // One bounded learning record per agent (keyed by the SAME stable
        // AgentId the P10/P11/P12 registries use). Created only by
        // NotifyOutcome; mutated only under the director lock.
        public sealed class LearningRecord
        {
            public readonly string AgentId;
            public int ArmedLevel;          // level this layer last armed (0 = never armed; baseline arm is observation-free)
            public long CrossingCount;      // level crossings consumed for this agent
            public long AdjustCount;        // trait writes actually performed
            public int LastEvalMs;          // -1 = never evaluated
            public int LastSeenMs;          // -1 = never (hygiene anchor)
            public int LastAdjustMs;        // -1 = never adjusted
            public string LastAdjustTrait;  // null = never adjusted
            public string LastAdjustRule;   // null = never adjusted
            public long UpdateCount;

            public LearningRecord(string agentId)
            {
                AgentId = agentId;
                LastEvalMs = -1;
                LastSeenMs = -1;
                LastAdjustMs = -1;
            }

            public string ToLine()
            {
                return "learning " + TrackIdPrefix + AgentId
                    + " armed=" + ArmedLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " crossings=" + CrossingCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " adjusted=" + AdjustCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " lastTrait=" + (LastAdjustTrait ?? "-")
                    + " lastRule=" + (LastAdjustRule ?? "-");
            }
        }

        private sealed class LearningState
        {
            public readonly Dictionary<string, LearningRecord> Records =
                new Dictionary<string, LearningRecord>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public long RecordsCreated;
            public long Crossings;
            public long Adjustments;
            public long Clamped;        // crossing consumed but the trait sat at the clamp bound (no write possible)
            public long Refused;
            public long Expired;
            public long BaselinesArmed;
            public string LastAdjustment;
        }

        private static readonly LearningState S = new LearningState();
        private static readonly object m_Lock = new object();
        private static Func<bool> m_AuthorityProbe;    // null/fault => no-op (deny-by-default)
        private static Action<string> m_OnDecision;    // LearningLogBridge attaches at boot

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_AuthorityProbe = probe; }
        public static void SetDecisionListener(Action<string> listener) { lock (m_Lock) m_OnDecision = listener; }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        private static void EmitAll(List<string> lines)
        {
            if (lines == null) return;
            for (int i = 0; i < lines.Count; i++) Emit(lines[i]);
        }

        // ---- readback (diagnostics/tests) ------------------------------------
        public static int RecordCount { get { lock (m_Lock) return S.Records.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long CrossingCount { get { lock (m_Lock) return S.Crossings; } }
        public static long AdjustmentCount { get { lock (m_Lock) return S.Adjustments; } }
        public static long ClampedCount { get { lock (m_Lock) return S.Clamped; } }
        public static long RefusedCount { get { lock (m_Lock) return S.Refused; } }
        public static long ExpiredCount { get { lock (m_Lock) return S.Expired; } }
        public static long BaselineArmedCount { get { lock (m_Lock) return S.BaselinesArmed; } }
        public static string LastAdjustment { get { lock (m_Lock) return S.LastAdjustment; } }

        // Deterministic lookup (null when absent or invalid). Returns the live
        // record reference (house readback shape).
        public static LearningRecord GetRecord(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            lock (m_Lock)
            {
                LearningRecord r;
                return S.Records.TryGetValue(agentId, out r) ? r : null;
            }
        }

        // Wrap-safe delta (TaskClock ints; unchecked subtraction, house shape).
        private static int UncheckedDelta(int prev, int now)
        {
            unchecked { return now - prev; }
        }

        // Deterministic direction picker over the agent's OWN outcome mix at
        // the crossing moment. Returns the PersonalityTrait index (0..4) or
        // -1 when no rule justifies a direction (never fabricated). First
        // match wins; both rules can only match together when cancelled/
        // expired outcomes dilute TotalOutcomes — completion outranks
        // adversity by precedence.
        private static int PickRule(CrewExperienceRecord exp)
        {
            if (exp.TotalOutcomes <= 0) return -1;
            if (exp.TasksCompleted * 2 >= exp.TotalOutcomes)
                return (int)PersonalityTrait.Diligence;                      // rule "completion"
            if ((exp.TasksFailed + exp.TasksVanished) * 2 >= exp.TotalOutcomes)
                return (int)PersonalityTrait.Adaptability;                   // rule "adversity"
            return -1;
        }

        private static string TraitName(int traitIndex)
        {
            switch (traitIndex)
            {
                case (int)PersonalityTrait.Discipline: return "Discipline";
                case (int)PersonalityTrait.Boldness: return "Boldness";
                case (int)PersonalityTrait.Sociability: return "Sociability";
                case (int)PersonalityTrait.Diligence: return "Diligence";
                case (int)PersonalityTrait.Adaptability: return "Adaptability";
                default: return "-";
            }
        }

        // Hygiene: decay records with no outcome past ActiveExpiryMs. Removals
        // always run; the expired-line queue is capped at MaxPendingLines (the
        // house emission bound). Callers hold m_Lock; lines are emitted by the
        // caller AFTER the lock is released.
        private static void SweepExpiredLocked(int nowMs, List<string> pending)
        {
            List<string> expired = null;
            foreach (KeyValuePair<string, LearningRecord> kv in S.Records)
            {
                LearningRecord r = kv.Value;
                if (r.LastSeenMs < 0) continue;
                if (UncheckedDelta(r.LastSeenMs, nowMs) <= ActiveExpiryMs) continue;
                if (expired == null) expired = new List<string>();
                expired.Add(kv.Key);
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                S.Records.Remove(expired[i]);
                S.Expired++;
                S.HistoryIds.Enqueue(TrackIdPrefix + expired[i]);
                while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                if (pending != null && pending.Count < MaxPendingLines)
                    pending.Add("LearningRecordExpired " + TrackIdPrefix + expired[i]);
            }
        }

        // Called by the P10 ClearTask funnel after P12 accrual (and usable
        // directly by tests). Exactly one crossing decision per call; the
        // crossing is consumed under the director lock (single-mutator) so a
        // concurrent caller can never double-adjust one crossing. Never
        // throws to the caller: every failure path is a counted refusal, and
        // the funnel wraps this call in its own fail-safe try/catch.
        public static bool NotifyOutcome(string agentId, string outcome, int nowMs)
        {
            // Authority gate: deny-by-default. A null, faulting, or
            // non-authoritative probe silences the layer entirely (clients
            // never mature personalities; the host funnel simply no-ops).
            Func<bool> auth;
            lock (m_Lock) auth = m_AuthorityProbe;
            if (auth == null) return false;
            bool authoritative;
            try { authoritative = auth(); }
            catch (Exception) { return false; }
            if (!authoritative) return false;

            // Input validation: P11 id shape + the P10 static outcome
            // vocabulary (compared, never parsed). Unknown => refused.
            if (!PersonalityFactory.IsValidAgentId(agentId))
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            if (outcome != CrewAgentRegistry.OutcomeCompleted
                && outcome != CrewAgentRegistry.OutcomeCancelled
                && outcome != CrewAgentRegistry.OutcomeExpired
                && outcome != CrewAgentRegistry.OutcomeVanished
                && outcome != CrewAgentRegistry.OutcomeFailed)
            {
                lock (m_Lock) S.Refused++;
                return false;
            }

            // Experience readback OUTSIDE the director lock (one-way lock
            // order: this layer takes nobody's lock while held). A missing
            // record means the P12 accrual has not run for this agent —
            // refused, never fabricated. SnapshotOf returns a defensive copy
            // taken under the experience registry's lock (no torn reads).
            CrewExperienceRecord exp = CrewExperienceRegistry.SnapshotOf(agentId);
            if (exp == null)
            {
                lock (m_Lock) S.Refused++;
                return false;
            }

            List<string> pending = new List<string>();
            int traitIndex = -1;
            string ruleName = null;
            int crossedLevel = -1;
            bool accepted = false;

            // ---- pass 1 (under lock): bookkeeping + crossing decision ------
            lock (m_Lock)
            {
                SweepExpiredLocked(nowMs, pending);

                LearningRecord rec;
                if (!S.Records.TryGetValue(agentId, out rec))
                {
                    if (S.Records.Count >= MaxRecords)
                    {
                        // The record bound equals the agent bound, so this is
                        // defensive only; deterministic refusal, never a size
                        // change, never a live-record shed.
                        S.Refused++;
                        accepted = false;
                    }
                    else
                    {
                        rec = new LearningRecord(agentId);
                        S.Records[agentId] = rec;
                        S.RecordsCreated++;
                    }
                }

                if (accepted || S.Records.ContainsKey(agentId))
                {
                    rec = S.Records[agentId];
                    rec.LastSeenMs = nowMs;
                    rec.UpdateCount++;
                    if (rec.LastEvalMs >= 0 && UncheckedDelta(rec.LastEvalMs, nowMs) < MinRecheckMs)
                    {
                        // Evaluation rate-limited (outcome storms): the
                        // crossing, if any, PERSISTS — ArmedLevel still lags
                        // the experience level — and is decided on a later
                        // pass. LastSeenMs was refreshed, so hygiene stays
                        // accurate.
                        accepted = true;
                    }
                    else
                    {
                        rec.LastEvalMs = nowMs;
                        if (rec.ArmedLevel == 0)
                        {
                            // First readable pass for this agent: arm the
                            // baseline ONLY (the P22 premise-capture
                            // analogue) — no adjustment on the arm pass.
                            rec.ArmedLevel = exp.Level;
                            S.BaselinesArmed++;
                            accepted = true;
                        }
                        else if (exp.Level <= rec.ArmedLevel)
                        {
                            accepted = true;   // no crossing
                        }
                        else
                        {
                            // Level crossing: consumed HERE, exactly once,
                            // regardless of the rule outcome (one-shot).
                            S.Crossings++;
                            rec.CrossingCount++;
                            crossedLevel = exp.Level;
                            rec.ArmedLevel = exp.Level;
                            traitIndex = PickRule(exp);
                            if (traitIndex >= 0)
                            {
                                ruleName = traitIndex == (int)PersonalityTrait.Diligence
                                    ? "completion" : "adversity";
                            }
                            accepted = true;
                        }
                    }
                }
            }
            // lock released — emissions deferred to the single exit below.

            if (crossedLevel < 0 || traitIndex < 0)
            {
                // No crossing, an arm pass, or a crossing with no justified
                // direction (neutral outcome mix): consumed silently — never
                // fabricate an adjustment.
                EmitAll(pending);
                return accepted;
            }

            // ---- personality read/write OUTSIDE every lock -----------------
            // One-way lock order: this layer holds nothing when it takes the
            // P11 registry lock. Derive-on-demand matches the registry's own
            // convenience convention (AffinityToRole/ArchetypeOf): a never-
            // registered agent derives first (counted by P11 as an
            // assignment) and is then replaced by the matured record
            // (counted as a replacement) — two P11 counter events per first
            // maturation, both visible in P11 diagnostics.
            CrewPersonality current = CrewPersonalityRegistry.Get(agentId);
            if (current == null) current = CrewPersonalityRegistry.DeriveFor(agentId, nowMs);
            if (current == null)
            {
                // Registry at cap and agent absent: fail-safe refusal.
                lock (m_Lock) S.Refused++;
                EmitAll(pending);
                return true;
            }

            int[] values = new int[5]
            {
                current.Get(PersonalityTrait.Discipline),
                current.Get(PersonalityTrait.Boldness),
                current.Get(PersonalityTrait.Sociability),
                current.Get(PersonalityTrait.Diligence),
                current.Get(PersonalityTrait.Adaptability),
            };
            int target = CrewPersonality.ClampTrait(values[traitIndex] + AdjustmentDelta);
            if (target == values[traitIndex])
            {
                // Trait already at the clamp bound: the delta is a no-op —
                // no write, no line, crossing still consumed.
                lock (m_Lock) S.Clamped++;
                EmitAll(pending);
                return true;
            }
            values[traitIndex] = target;
            CrewPersonality updated = PersonalityFactory.FromValues(
                agentId, values[0], values[1], values[2], values[3], values[4], nowMs);
            if (updated == null || !CrewPersonalityRegistry.SetPersonality(agentId, updated, nowMs))
            {
                // Identity integrity / validation refused the write:
                // fail-safe refusal (never a partial write — SetPersonality
                // is all-or-nothing).
                lock (m_Lock) S.Refused++;
                EmitAll(pending);
                return true;
            }

            string line = "PersonalityAdjusted " + TrackIdPrefix + agentId
                + " trait=" + TraitName(traitIndex)
                + " delta=+" + AdjustmentDelta.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " level=" + crossedLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " rule=" + ruleName
                + " (adaptive learning; traits are data until a consumer phase reads them)";

            // ---- pass 2 (under lock): finalize record + counters ----------
            lock (m_Lock)
            {
                S.Adjustments++;
                S.LastAdjustment = TrackIdPrefix + agentId
                    + " trait=" + TraitName(traitIndex)
                    + " delta=+" + AdjustmentDelta.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " level=" + crossedLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " rule=" + ruleName;
                LearningRecord rec;
                if (S.Records.TryGetValue(agentId, out rec))
                {
                    rec.AdjustCount++;
                    rec.LastAdjustMs = nowMs;
                    rec.LastAdjustTrait = TraitName(traitIndex);
                    rec.LastAdjustRule = ruleName;
                }
            }

            EmitAll(pending);
            Emit(line);
            return true;
        }

        // One bounded diagnostic line per record (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, LearningRecord> kv in S.Records)
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
                lines.Add("learningRecords=" + S.Records.Count
                    + " crossings=" + S.Crossings.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " adjustments=" + S.Adjustments.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " clamped=" + S.Clamped.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " armed=" + S.BaselinesArmed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " refused=" + S.Refused.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " expired=" + S.Expired.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("lastAdjustment=" + (S.LastAdjustment ?? "-"));
            }
            return lines;
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Records.Clear();
                S.HistoryIds.Clear();
                S.RecordsCreated = 0;
                S.Crossings = 0;
                S.Adjustments = 0;
                S.Clamped = 0;
                S.Refused = 0;
                S.Expired = 0;
                S.BaselinesArmed = 0;
                S.LastAdjustment = null;
                m_AuthorityProbe = null;
                m_OnDecision = null;
            }
        }
    }
}