using System;
using System.Collections.Generic;

namespace CapBot.Core.Crew
{
    // ---- Phase 12: crew experience ---------------------------------------------
    //
    // Bounded, deterministic experience accrual for crew agents. Every task
    // outcome the Phase 10 agent registry resolves (CrewAgentRegistry.ClearTask)
    // accrues fixed points and outcome counters on an experience record keyed
    // by the SAME stable AgentId. Level is a pure function of accrued XP.
    //
    // Boundaries (Phase 12 contract):
    //   - Experience is DATA ONLY. Nothing here reads PULSAR state, stores
    //     game-object references, creates/claims/executes tasks, or influences
    //     the scheduler, recovery, claims, capabilities, executor, or
    //     personalities. Personality trait adjustment is deliberately NOT
    //     part of this phase (it belongs to adaptive learning, Phase 25).
    //   - The accrual hook in CrewAgentRegistry.ClearTask is additive and
    //     fail-safe: it runs OUTSIDE the agent-registry lock and a faulting
    //     experience layer can never affect agent state or task resolution.
    //   - All timestamps are explicit nowMs values (TaskClock semantics); no
    //     wall-clock reads, no LINQ, no per-frame work (accrual happens only
    //     when a task outcome is resolved, which is gated by the 1 s sync).
    //   - Every collection is bounded; every input validated; the outcome
    //     vocabulary is the Phase 10 static set (unknown outcomes refused).

    // Deterministic level math. XP thresholds are cumulative and fixed;
    // LevelForXp is a pure function (bounded scan over 10 levels).
    public static class ExperienceLevels
    {
        public const int MaxLevel = 10;

        // Cumulative XP required to REACH level index+1 (thresholds[0]=0 => level 1).
        // Fixed design constants: 50, 120, 220, 350, 510, 710, 950, 1230, 1550.
        public static readonly int[] Thresholds = new int[MaxLevel]
        {
            0, 50, 120, 220, 350, 510, 710, 950, 1230, 1550,
        };

        // Level for accrued XP, 1..MaxLevel. Negative/zero XP => level 1.
        // XP beyond the last threshold stays at MaxLevel (bounded, never invented).
        public static int LevelForXp(long xp)
        {
            int level = 1;
            for (int i = 1; i < MaxLevel; i++)
            {
                if (xp >= Thresholds[i]) level = i + 1;
            }
            return level;
        }
    }

    // One bounded experience record for exactly one agent. Created only by
    // CrewExperienceRegistry; mutated only inside its lock via Apply.
    public sealed class CrewExperienceRecord
    {
        public readonly string AgentId;        // owner identity (stable agent id)
        public readonly int CreatedTimeMs;     // record creation stamp

        public long TasksCompleted;
        public long TasksCancelled;
        public long TasksExpired;
        public long TasksVanished;
        public long TasksFailed;
        public long TotalOutcomes;
        public long ExperiencePoints;
        public int Level;                      // derived 1..ExperienceLevels.MaxLevel
        public string LastOutcome;             // last resolved outcome (data only)
        public int LastResultMs;               // -1 = none
        public long UpdateCount;

        public CrewExperienceRecord(string agentId, int createdTimeMs)
        {
            AgentId = agentId;
            CreatedTimeMs = createdTimeMs;
            Level = 1;
            LastResultMs = -1;
        }

        // Applies one resolved outcome (callers hold the registry lock).
        // counters/points are pre-validated by the registry.
        public void Apply(string outcome, long points, int nowMs)
        {
            if (outcome == CrewAgentRegistry.OutcomeCompleted) TasksCompleted++;
            else if (outcome == CrewAgentRegistry.OutcomeCancelled) TasksCancelled++;
            else if (outcome == CrewAgentRegistry.OutcomeExpired) TasksExpired++;
            else if (outcome == CrewAgentRegistry.OutcomeVanished) TasksVanished++;
            else if (outcome == CrewAgentRegistry.OutcomeFailed) TasksFailed++;
            TotalOutcomes++;
            ExperiencePoints += points;
            Level = ExperienceLevels.LevelForXp(ExperiencePoints);
            LastOutcome = outcome;
            LastResultMs = nowMs;
            UpdateCount++;
        }

        // Bounded single-line diagnostic (deterministic, data only).
        public string ToLine()
        {
            return "experience " + AgentId
                + " lvl=" + Level.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " xp=" + ExperiencePoints.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " done=" + TasksCompleted.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " cancel=" + TasksCancelled.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " expire=" + TasksExpired.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " vanish=" + TasksVanished.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " fail=" + TasksFailed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // ---- Phase 12: experience registry ------------------------------------------
    //
    // Bounded registry of CrewExperienceRecord keyed by the stable AgentId.
    // The only writer is RecordOutcome — fed by the Phase 10 ClearTask funnel
    // (every task outcome the agent layer resolves accrues exactly once).
    //
    // Bounded: at most MaxRecords (32) records — matching the P10/P11 bounds;
    // deterministic refusal when full. Records are created lazily on the first
    // accrued outcome for a valid agent id; unknown outcome vocabulary is
    // refused (never fabricated).
    public static class CrewExperienceRegistry
    {
        public const int MaxRecords = 32;            // = CrewAgentRegistry.MaxAgents

        // Points per resolved outcome (fixed design constants). COMPLETED is
        // the only strong reward; every other resolution still counts as
        // participation. Unknown outcomes return -1 (refused, never guessed).
        public const int PointsCompleted = 10;
        public const int PointsOther = 2;

        private sealed class RegistryState
        {
            public readonly Dictionary<string, CrewExperienceRecord> Records =
                new Dictionary<string, CrewExperienceRecord>(StringComparer.Ordinal);
            public long RecordsCreated;
            public long Accruals;
            public long Refused;
            public long Restored;          // Phase 28: records inserted from the save blob
            public long RestoreSkipped;    // Phase 28: live record already present (live wins)
            public long RestoreRefused;    // Phase 28: invalid payload / full registry
        }

        private static readonly RegistryState S = new RegistryState();
        private static readonly object m_Lock = new object();
        private static Action<string> m_OnDecision;  // ExperienceLogBridge attaches at boot

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

        // ---- readback (diagnostics/tests) ------------------------------------
        public static int Count { get { lock (m_Lock) return S.Records.Count; } }
        public static long RecordCount { get { lock (m_Lock) return S.RecordsCreated; } }
        public static long AccrualCount { get { lock (m_Lock) return S.Accruals; } }
        public static long RefusedCount { get { lock (m_Lock) return S.Refused; } }

        // Points for an outcome in the Phase 10 static vocabulary; -1 for
        // unknown/empty outcomes (refused, never fabricated).
        public static int PointsForOutcome(string outcome)
        {
            if (outcome == CrewAgentRegistry.OutcomeCompleted) return PointsCompleted;
            if (outcome == CrewAgentRegistry.OutcomeCancelled) return PointsOther;
            if (outcome == CrewAgentRegistry.OutcomeExpired) return PointsOther;
            if (outcome == CrewAgentRegistry.OutcomeVanished) return PointsOther;
            if (outcome == CrewAgentRegistry.OutcomeFailed) return PointsOther;
            return -1;
        }

        // Records one resolved task outcome for an agent (lazy record creation).
        // Unknown agent ids / outcome vocabulary / full registry => refused.
        public static bool RecordOutcome(string agentId, string outcome, int nowMs)
        {
            if (!PersonalityFactory.IsValidAgentId(agentId))
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            int points = PointsForOutcome(outcome);
            if (points < 0)
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            CrewExperienceRecord record;
            lock (m_Lock)
            {
                if (!S.Records.TryGetValue(agentId, out record))
                {
                    if (S.Records.Count >= MaxRecords)
                    {
                        Emit("ExperienceRefused " + agentId + " (registry full)");
                        S.Refused++;
                        return false;
                    }
                    record = new CrewExperienceRecord(agentId, nowMs);
                    S.Records[agentId] = record;
                    S.RecordsCreated++;
                }
                record.Apply(outcome, points, nowMs);
                S.Accruals++;
            }
            Emit("ExperienceRecorded " + agentId + " outcome=" + outcome
                + " points=" + points.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " xp=" + record.ExperiencePoints.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " level=" + record.Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        // Lookup without creation (null when absent or invalid).
        public static CrewExperienceRecord Get(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            lock (m_Lock)
            {
                CrewExperienceRecord r;
                return S.Records.TryGetValue(agentId, out r) ? r : null;
            }
        }

        // Level of an agent's experience record (0 = no record / invalid id).
        public static int LevelOf(string agentId)
        {
            CrewExperienceRecord r = Get(agentId);
            return r == null ? 0 : r.Level;
        }

        // Phase 25: defensive snapshot readback (additive). Returns a COPY of
        // the record taken under the registry lock — no torn reads for
        // consumers that read outside their own lock (the learning layer's
        // one-way lock order). null when absent or invalid id; never a live
        // reference, never a fabricated record.
        public static CrewExperienceRecord SnapshotOf(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            lock (m_Lock)
            {
                CrewExperienceRecord r;
                if (!S.Records.TryGetValue(agentId, out r)) return null;
                CrewExperienceRecord copy = new CrewExperienceRecord(r.AgentId, r.CreatedTimeMs);
                copy.TasksCompleted = r.TasksCompleted;
                copy.TasksCancelled = r.TasksCancelled;
                copy.TasksExpired = r.TasksExpired;
                copy.TasksVanished = r.TasksVanished;
                copy.TasksFailed = r.TasksFailed;
                copy.TotalOutcomes = r.TotalOutcomes;
                copy.ExperiencePoints = r.ExperiencePoints;
                copy.Level = r.Level;
                copy.LastOutcome = r.LastOutcome;
                copy.LastResultMs = r.LastResultMs;
                copy.UpdateCount = r.UpdateCount;
                return copy;
            }
        }

        // Removes an experience record (future-phase lifecycle integration).
        public static bool Remove(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return false;
            lock (m_Lock)
            {
                if (!S.Records.Remove(agentId)) return false;
            }
            Emit("ExperienceRemoved " + agentId);
            return true;
        }

        // One bounded diagnostic line per record (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CrewExperienceRecord> kv in S.Records)
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
                lines.Add("experienceRecords=" + S.Records.Count
                    + " created=" + S.RecordsCreated + " accruals=" + S.Accruals
                    + " refused=" + S.Refused);
            }
            return lines;
        }

        // ---- Phase 28: persistence export/restore (additive) --------------------
        //
        // Export: defensive copy of every record (the persistence layer is a
        // read-only consumer of live state; the P25 SnapshotOf pattern).
        // Restore: INSERT-ONLY — a live record always wins over the saved one
        // (a record created this session is fresher than any save), Level is
        // NEVER trusted (recomputed from XP), and invalid payloads are
        // refused, never fabricated.

        public static List<CrewExperienceRecord> ExportAll()
        {
            List<CrewExperienceRecord> outList = new List<CrewExperienceRecord>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CrewExperienceRecord> kv in S.Records)
                {
                    outList.Add(SnapshotRecordLocked(kv.Value));
                }
            }
            outList.Sort(delegate (CrewExperienceRecord a, CrewExperienceRecord b)
            { return string.CompareOrdinal(a.AgentId, b.AgentId); });
            return outList;
        }

        private static CrewExperienceRecord SnapshotRecordLocked(CrewExperienceRecord r)
        {
            CrewExperienceRecord copy = new CrewExperienceRecord(r.AgentId, r.CreatedTimeMs);
            copy.TasksCompleted = r.TasksCompleted;
            copy.TasksCancelled = r.TasksCancelled;
            copy.TasksExpired = r.TasksExpired;
            copy.TasksVanished = r.TasksVanished;
            copy.TasksFailed = r.TasksFailed;
            copy.TotalOutcomes = r.TotalOutcomes;
            copy.ExperiencePoints = r.ExperiencePoints;
            copy.Level = r.Level;
            copy.LastOutcome = r.LastOutcome;
            copy.LastResultMs = r.LastResultMs;
            copy.UpdateCount = r.UpdateCount;
            return copy;
        }

        // Restores one record. Returns true when inserted; false when skipped
        // (a live record already exists — live state always wins, counted
        // RestoreSkipped) or refused (invalid id/outcome/bounds or full
        // registry, counted RestoreRefused). Level is recomputed from XP.
        public static bool RestoreRecord(string agentId, long experiencePoints,
            long tasksCompleted, long tasksCancelled, long tasksExpired,
            long tasksVanished, long tasksFailed, long totalOutcomes,
            string lastOutcome, int createdTimeMs, int lastResultMs, long updateCount)
        {
            if (!PersonalityFactory.IsValidAgentId(agentId))
            {
                lock (m_Lock) { S.RestoreRefused++; }
                return false;
            }
            // Outcome vocabulary: null/empty last outcome is a "no outcome yet"
            // row; anything else must be in the Phase 10 closed vocabulary.
            if (lastOutcome != null && lastOutcome.Length > 0
                && lastOutcome != CrewAgentRegistry.OutcomeCompleted
                && lastOutcome != CrewAgentRegistry.OutcomeCancelled
                && lastOutcome != CrewAgentRegistry.OutcomeExpired
                && lastOutcome != CrewAgentRegistry.OutcomeVanished
                && lastOutcome != CrewAgentRegistry.OutcomeFailed)
            {
                lock (m_Lock) S.RestoreRefused++;
                return false;
            }
            if (experiencePoints < 0 || tasksCompleted < 0 || tasksCancelled < 0
                || tasksExpired < 0 || tasksVanished < 0 || tasksFailed < 0
                || totalOutcomes < tasksCompleted + tasksCancelled + tasksExpired
                    + tasksVanished + tasksFailed
                || createdTimeMs < 0 || lastResultMs < -1 || updateCount < 0)
            {
                lock (m_Lock) S.RestoreRefused++;
                return false;
            }
            CrewExperienceRecord record = new CrewExperienceRecord(agentId, createdTimeMs);
            record.TasksCompleted = tasksCompleted;
            record.TasksCancelled = tasksCancelled;
            record.TasksExpired = tasksExpired;
            record.TasksVanished = tasksVanished;
            record.TasksFailed = tasksFailed;
            record.TotalOutcomes = totalOutcomes;
            record.ExperiencePoints = experiencePoints;
            record.Level = ExperienceLevels.LevelForXp(experiencePoints);   // never trust a persisted level
            record.LastOutcome = lastOutcome;
            record.LastResultMs = lastResultMs;
            record.UpdateCount = updateCount;
            lock (m_Lock)
            {
                if (S.Records.ContainsKey(agentId))
                {
                    S.RestoreSkipped++;
                    return false; // live state wins — never overwrite
                }
                if (S.Records.Count >= MaxRecords)
                {
                    S.RestoreRefused++;
                    return false;
                }
                S.Records[agentId] = record;
                S.Restored++;
            }
            Emit("ExperienceRestored " + agentId
                + " xp=" + experiencePoints.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " level=" + record.Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        public static long RestoredCount { get { lock (m_Lock) return S.Restored; } }
        public static long RestoreSkippedCount { get { lock (m_Lock) return S.RestoreSkipped; } }
        public static long RestoreRefusedCount { get { lock (m_Lock) return S.RestoreRefused; } }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Records.Clear();
                S.RecordsCreated = 0;
                S.Accruals = 0;
                S.Refused = 0;
                S.Restored = 0;
                S.RestoreSkipped = 0;
                S.RestoreRefused = 0;
                m_OnDecision = null;
            }
        }
    }
}