using System;
using System.Collections.Generic;

namespace CapBot.Core.Crew
{
    // ---- Phase 13: crew memory --------------------------------------------------
    //
    // Bounded, deterministic episodic memory for crew agents. Each agent keeps a
    // small ring of fact entries (location observations, task outcomes, crew
    // events) keyed by the SAME stable AgentId as the P10/P11/P12 registries.
    // Memory is RECALLABLE DATA for later phases — it is not learning, not
    // personality adjustment, not decision making, and it never influences the
    // scheduler, recovery, claims, capabilities, executor, or PULSAR world state.
    //
    // Boundaries (Phase 13 contract):
    //   - Memory is DATA ONLY. Nothing here reads PULSAR state, stores
    //     game-object references, creates/claims/executes tasks, or influences
    //     any existing pipeline stage. Location memory is written through the
    //     explicit RememberLocation API (the P6 snapshot remains the sole
    //     authoritative world observation — no snapshot-path changes).
    //   - The single Phase 13 funnel wiring is additive and fail-safe:
    //     CrewAgentRegistry.ClearTask calls RememberTaskOutcome OUTSIDE the
    //     agent-registry lock in its own try/catch — a faulting memory layer
    //     can never affect agent state or task resolution (test M10).
    //   - All timestamps are explicit nowMs values (TaskClock semantics); no
    //     wall-clock reads, no LINQ, no per-frame work (writes happen only on
    //     task resolution, gated by the 1 s sync cadence, or on explicit API
    //     calls from later phases).
    //   - Every collection is bounded; every input validated; the task-outcome
    //     vocabulary is the Phase 10 static set (unknown outcomes refused).
    //   - Facts are data rows: fields are compared, never parsed or dispatched on.

    // Bounded static memory-kind vocabulary (compared, never parsed).
    public enum MemoryKind
    {
        Location = 1,
        TaskOutcome = 2,
        CrewEvent = 3,
    }

    // One bounded memory entry for exactly one agent. Created and mutated only
    // by CrewMemorySystem (inside its lock). A fact row: kind + data + stamps.
    public sealed class CrewMemoryEntry
    {
        public readonly string AgentId;      // owner identity (stable agent id)
        public readonly MemoryKind Kind;     // static vocabulary
        public readonly long TaskId;         // TaskOutcome key; 0 = not task-scoped
        public readonly int CreatedTimeMs;   // first observation stamp

        public string Text;                  // data only (location/crew-event payload), null = none
        public string Outcome;               // TaskOutcome payload (static vocabulary), null = none
        public int LastSeenMs;               // last write or recall stamp
        public long UpdateCount;

        public CrewMemoryEntry(string agentId, MemoryKind kind, long taskId, string text, string outcome, int nowMs)
        {
            AgentId = agentId;
            Kind = kind;
            TaskId = taskId;
            Text = text;
            Outcome = outcome;
            CreatedTimeMs = nowMs;
            LastSeenMs = nowMs;
        }

        // The single recall path: stamps the read so eviction can favor
        // recently-used facts (bounded bookkeeping, data only).
        public void Recall(int nowMs)
        {
            LastSeenMs = nowMs;
            UpdateCount++;
        }

        // Bounded single-line diagnostic (deterministic, data only).
        public string ToLine()
        {
            return "memory " + AgentId
                + " kind=" + Kind.ToString()
                + " task=" + (TaskId > 0
                    ? TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "-")
                + " text=" + (Text ?? "-")
                + " outcome=" + (Outcome ?? "-")
                + " seen=" + LastSeenMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " upd=" + UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // ---- Phase 13: memory registry -----------------------------------------------
    //
    // Bounded per-agent memory rings keyed by the stable AgentId. Writers:
    //   - RememberLocation (explicit API; future directors/providers call this)
    //   - RememberTaskOutcome (the Phase 13 ClearTask funnel wiring)
    //   - RememberCrewEvent (explicit API)
    // Reads: Recall/RecallAll (stamp the read; never fabricate).
    //
    // Bounded: at most MaxAgents (32) agents — matching the P10/P11/P12 bounds;
    // at most MaxMemoriesPerAgent (8) entries per agent. When a ring is full a
    // NEW distinct fact evicts the oldest entry by LastSeenMs (tie -> lowest
    // insertion index); an EXISTING fact updates in place and never evicts.
    // Deterministic refusal only when the registry itself is full of new agents.
    public static class CrewMemorySystem
    {
        public const int MaxAgents = 32;             // = CrewAgentRegistry.MaxAgents
        public const int MaxMemoriesPerAgent = 8;    // bounded ring per agent
        public const int MaxTextLen = 32;            // same rule as CrewMemberSnapshot names/TLI names

        private sealed class AgentMemory
        {
            public readonly List<CrewMemoryEntry> Entries = new List<CrewMemoryEntry>(MaxMemoriesPerAgent);
            public long Writes;
            public long Recalls;
            public long Dropped;
        }

        private sealed class RegistryState
        {
            public readonly Dictionary<string, AgentMemory> Agents =
                new Dictionary<string, AgentMemory>(StringComparer.Ordinal);
            public long AgentsCreated;
            public long Writes;
            public long Recalls;
            public long Evictions;
            public long Refused;
            public long Restored;          // Phase 28: rows inserted from the save blob
            public long RestoreSkipped;    // Phase 28: whole-agent skips (any live memory wins)
            public long RestoreRefused;    // Phase 28: invalid payload rows
        }

        private static readonly RegistryState S = new RegistryState();
        private static readonly object m_Lock = new object();
        private static Action<string> m_OnDecision;  // MemoryLogBridge attaches at boot

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
        public static int AgentCount { get { lock (m_Lock) return S.Agents.Count; } }
        public static long WriteCount { get { lock (m_Lock) return S.Writes; } }
        public static long RecallCount { get { lock (m_Lock) return S.Recalls; } }
        public static long EvictionCount { get { lock (m_Lock) return S.Evictions; } }
        public static long RefusedCount { get { lock (m_Lock) return S.Refused; } }

        public static int MemoryCountOf(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return 0;
            lock (m_Lock)
            {
                AgentMemory am;
                return S.Agents.TryGetValue(agentId, out am) ? am.Entries.Count : 0;
            }
        }

        // Phase 10 static outcome vocabulary check (compared, never parsed).
        private static bool IsKnownOutcome(string outcome)
        {
            return outcome == CrewAgentRegistry.OutcomeCompleted
                || outcome == CrewAgentRegistry.OutcomeCancelled
                || outcome == CrewAgentRegistry.OutcomeExpired
                || outcome == CrewAgentRegistry.OutcomeVanished
                || outcome == CrewAgentRegistry.OutcomeFailed;
        }

        // Text data rule: null allowed (fact without payload); otherwise the
        // bounded name/TLI length. Content is never interpreted.
        private static bool IsValidText(string text)
        {
            return text == null || text.Length <= MaxTextLen;
        }

        // ---- write paths -------------------------------------------------------

        // Location fact: upsert by (Kind=Location, Text). A repeated sighting of
        // the same location updates LastSeenMs in place (order stable); a new
        // location replaces the same-kind row when present, else evicts the
        // oldest entry when the ring is full. Never fabricates a location.
        public static bool RememberLocation(string agentId, string locationName, int nowMs)
        {
            if (locationName == null || locationName.Length == 0 || locationName.Length > MaxTextLen)
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            return Upsert(agentId, MemoryKind.Location, 0, locationName, null, nowMs);
        }

        // Task-outcome fact: upsert by (Kind=TaskOutcome, TaskId). Two
        // resolutions of the same task id mutate one row (the later outcome
        // wins — deterministic); distinct task ids are distinct rows.
        // The outcome must be in the Phase 10 static vocabulary.
        public static bool RememberTaskOutcome(string agentId, long taskId, string outcome, int nowMs)
        {
            if (taskId <= 0)
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            if (!IsKnownOutcome(outcome))
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            return Upsert(agentId, MemoryKind.TaskOutcome, taskId, null, outcome, nowMs);
        }

        // Crew-event fact: upsert by (Kind=CrewEvent, Text). Text is data only
        // (null = kind-scoped row); never parsed or dispatched on.
        public static bool RememberCrewEvent(string agentId, string eventText, int nowMs)
        {
            if (!IsValidText(eventText) || (eventText != null && eventText.Length == 0))
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            return Upsert(agentId, MemoryKind.CrewEvent, 0, eventText, null, nowMs);
        }

        // Shared bounded upsert (callers pre-validate their own payloads).
        // Listener lines are collected under the lock and fired AFTER it is
        // released — a listener can never run while the registry lock is held.
        private static bool Upsert(string agentId, MemoryKind kind, long taskId, string text, string outcome, int nowMs)
        {
            if (!PersonalityFactory.IsValidAgentId(agentId))
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            List<string> pending = null;
            bool accepted = false;
            lock (m_Lock)
            {
                AgentMemory am;
                if (!S.Agents.TryGetValue(agentId, out am))
                {
                    if (S.Agents.Count >= MaxAgents)
                    {
                        accepted = false;
                        pending = new List<string>(1);
                        pending.Add("MemoryRefused " + agentId + " (registry full)");
                        S.Refused++;
                    }
                    else
                    {
                        am = new AgentMemory();
                        S.Agents[agentId] = am;
                        S.AgentsCreated++;
                    }
                }
                if (pending == null)
                {
                    // Existing fact (same key) updates in place — no eviction.
                    int found = -1;
                    for (int i = 0; i < am.Entries.Count; i++)
                    {
                        CrewMemoryEntry e = am.Entries[i];
                        if (e.Kind != kind) continue;
                        if (kind == MemoryKind.TaskOutcome)
                        {
                            if (e.TaskId != taskId) continue;
                        }
                        else
                        {
                            if (!string.Equals(e.Text, text, StringComparison.Ordinal)) continue;
                        }
                        found = i;
                        break;
                    }
                    if (found >= 0)
                    {
                        CrewMemoryEntry e = am.Entries[found];
                        if (kind != MemoryKind.TaskOutcome) e.Text = text;
                        e.Outcome = outcome;
                        e.LastSeenMs = nowMs;
                        e.UpdateCount++;
                        am.Writes++;
                        S.Writes++;
                        accepted = true;
                        pending = new List<string>(1);
                        pending.Add("MemoryUpdated " + agentId + " kind=" + kind
                            + (kind == MemoryKind.TaskOutcome
                                ? " task=" + taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : " text=" + (text ?? "-"))
                            + " count=" + am.Entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        // New distinct fact: ring full -> evict the oldest
                        // entry by LastSeenMs (tie -> lowest insertion index).
                        // Bounded scan (<= 8 entries).
                        if (am.Entries.Count >= MaxMemoriesPerAgent)
                        {
                            int oldest = 0;
                            for (int i = 1; i < am.Entries.Count; i++)
                            {
                                if (am.Entries[i].LastSeenMs < am.Entries[oldest].LastSeenMs) oldest = i;
                            }
                            CrewMemoryEntry dropped = am.Entries[oldest];
                            am.Entries.RemoveAt(oldest);
                            am.Dropped++;
                            S.Evictions++;
                            pending = new List<string>(2);
                            pending.Add("MemoryEvicted " + agentId + " kind=" + dropped.Kind
                                + (dropped.TaskId > 0
                                    ? " task=" + dropped.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    : " text=" + (dropped.Text ?? "-")));
                        }
                        am.Entries.Add(new CrewMemoryEntry(agentId, kind, taskId, text, outcome, nowMs));
                        am.Writes++;
                        S.Writes++;
                        accepted = true;
                        if (pending == null) pending = new List<string>(1);
                        pending.Add("MemoryStored " + agentId + " kind=" + kind
                            + (kind == MemoryKind.TaskOutcome
                                ? " task=" + taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                : " text=" + (text ?? "-")));
                    }
                }
            }
            if (pending != null)
            {
                for (int i = 0; i < pending.Count; i++) Emit(pending[i]);
            }
            return accepted;
        }

        // ---- read paths ----------------------------------------------------------

        // Recall one fact by key (null when absent/invalid). Recalls stamp the
        // entry (LastSeenMs/UpdateCount) — the single recall path.
        public static CrewMemoryEntry Recall(string agentId, MemoryKind kind, string text)
        {
            if (!PersonalityFactory.IsValidAgentId(agentId)) return null;
            CrewMemoryEntry hit = null;
            lock (m_Lock)
            {
                AgentMemory am;
                if (!S.Agents.TryGetValue(agentId, out am)) return null;
                for (int i = 0; i < am.Entries.Count; i++)
                {
                    CrewMemoryEntry e = am.Entries[i];
                    if (e.Kind != kind) continue;
                    if (!string.Equals(e.Text, text, StringComparison.Ordinal)) continue;
                    hit = e;
                    break;
                }
                if (hit != null)
                {
                    hit.Recall(nowMsOf());
                    am.Recalls++;
                    S.Recalls++;
                }
            }
            return hit;
        }

        // Recall the latest outcome memory for a task id (null when absent).
        public static CrewMemoryEntry RecallTaskOutcome(string agentId, long taskId)
        {
            if (!PersonalityFactory.IsValidAgentId(agentId) || taskId <= 0) return null;
            CrewMemoryEntry hit = null;
            lock (m_Lock)
            {
                AgentMemory am;
                if (!S.Agents.TryGetValue(agentId, out am)) return null;
                for (int i = 0; i < am.Entries.Count; i++)
                {
                    CrewMemoryEntry e = am.Entries[i];
                    if (e.Kind != MemoryKind.TaskOutcome || e.TaskId != taskId) continue;
                    hit = e;
                    break;
                }
                if (hit != null)
                {
                    hit.Recall(nowMsOf());
                    am.Recalls++;
                    S.Recalls++;
                }
            }
            return hit;
        }

        // All entries of one kind for an agent, in insertion order (oldest ->
        // newest). Bounded list (<= 8). Read-only snapshots; recall stamps are
        // not applied here (use Recall for stamping reads).
        public static List<CrewMemoryEntry> RecallAll(string agentId, MemoryKind kind)
        {
            List<CrewMemoryEntry> result = new List<CrewMemoryEntry>();
            if (!PersonalityFactory.IsValidAgentId(agentId)) return result;
            lock (m_Lock)
            {
                AgentMemory am;
                if (!S.Agents.TryGetValue(agentId, out am)) return result;
                for (int i = 0; i < am.Entries.Count; i++)
                {
                    if (am.Entries[i].Kind == kind) result.Add(am.Entries[i]);
                }
            }
            return result;
        }

        // Virtual-clock seam for recall stamps (production: TaskClock.NowMs;
        // unset -> 0 stamps, never a wall-clock read).
        private static Func<int> m_NowMsProvider;
        public static void SetNowMsProvider(Func<int> provider) { lock (m_Lock) m_NowMsProvider = provider; }
        private static int nowMsOf()
        {
            Func<int> p;
            lock (m_Lock) p = m_NowMsProvider;
            if (p == null) return 0;
            try { return p(); } catch (Exception) { return 0; }
        }

        // ---- lifecycle (future-phase integration point) --------------------------

        // Forgets ALL memory for one agent (agent-removal lifecycle hook; the
        // P10 registry removal pass is a later-phase consumer). Deterministic.
        public static bool ForgetAgent(string agentId)
        {
            if (!PersonalityFactory.IsValidAgentId(agentId)) return false;
            int count;
            lock (m_Lock)
            {
                AgentMemory am;
                if (!S.Agents.TryGetValue(agentId, out am)) return false;
                count = am.Entries.Count;
                S.Agents.Remove(agentId);
            }
            Emit("MemoryForgotten " + agentId + " entries=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        // Lookup without creation (null when absent/invalid).
        public static AgentMemoryStats StatsOf(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            lock (m_Lock)
            {
                AgentMemory am;
                if (!S.Agents.TryGetValue(agentId, out am)) return null;
                return new AgentMemoryStats(am.Entries.Count, am.Writes, am.Recalls, am.Dropped);
            }
        }

        public sealed class AgentMemoryStats
        {
            public readonly int EntryCount;
            public readonly long Writes;
            public readonly long Recalls;
            public readonly long Dropped;
            public AgentMemoryStats(int entryCount, long writes, long recalls, long dropped)
            {
                EntryCount = entryCount;
                Writes = writes;
                Recalls = recalls;
                Dropped = dropped;
            }
        }

        // One bounded diagnostic line per entry (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, AgentMemory> kv in S.Agents)
                {
                    for (int i = 0; i < kv.Value.Entries.Count; i++)
                    {
                        lines.Add(kv.Value.Entries[i].ToLine());
                    }
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
                lines.Add("memoryAgents=" + S.Agents.Count
                    + " created=" + S.AgentsCreated + " writes=" + S.Writes
                    + " recalls=" + S.Recalls + " evictions=" + S.Evictions
                    + " refused=" + S.Refused);
            }
            return lines;
        }

        // ---- Phase 28: persistence export/restore (additive) --------------------
        //
        // Export: defensive row copies of every entry (bounded: ≤32 agents ×
        // ≤8 entries). Restore: INSERT-ONLY at the AGENT level — an agent with
        // ANY live memory is skipped entirely (live facts are fresher than
        // any save; a partial overwrite would silently merge stale save rows
        // into a live ring). Per-row restore routes through the private
        // Upsert so validation/eviction rules stay the single authority.

        public struct MemoryRow
        {
            public string AgentId;
            public MemoryKind Kind;
            public long TaskId;
            public string Text;
            public string Outcome;
            public int CreatedTimeMs;
            public int LastSeenMs;
            public long UpdateCount;
        }

        public static List<MemoryRow> ExportAll()
        {
            List<MemoryRow> outList = new List<MemoryRow>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, AgentMemory> kv in S.Agents)
                {
                    for (int i = 0; i < kv.Value.Entries.Count; i++)
                    {
                        CrewMemoryEntry e = kv.Value.Entries[i];
                        MemoryRow row = new MemoryRow();
                        row.AgentId = e.AgentId;
                        row.Kind = e.Kind;
                        row.TaskId = e.TaskId;
                        row.Text = e.Text;
                        row.Outcome = e.Outcome;
                        row.CreatedTimeMs = e.CreatedTimeMs;
                        row.LastSeenMs = e.LastSeenMs;
                        row.UpdateCount = e.UpdateCount;
                        outList.Add(row);
                    }
                }
            }
            outList.Sort(delegate (MemoryRow a, MemoryRow b)
            {
                int c = string.CompareOrdinal(a.AgentId, b.AgentId);
                if (c != 0) return c;
                if (a.Kind != b.Kind) return ((int)a.Kind) - ((int)b.Kind);
                if (a.TaskId != b.TaskId) return a.TaskId < b.TaskId ? -1 : 1;
                return string.CompareOrdinal(a.Text ?? "", b.Text ?? "");
            });
            return outList;
        }

        // Restores one saved memory row. Returns true when inserted; false
        // when the owning agent has ANY live memory (whole-agent skip — live
        // wins), or the row is refused (invalid id/kind/vocabulary/bounds).
        // The saved LastSeenMs is preserved as the entry's stamp (data row);
        // UpdateCount is not carried over (live bookkeeping semantics restart;
        // persisted recall counts would be stale by definition).
        public static bool RestoreRow(string agentId, MemoryKind kind, long taskId,
            string text, string outcome, int createdTimeMs, int lastSeenMs, long updateCount)
        {
            MemoryRow row = new MemoryRow();
            row.AgentId = agentId;
            row.Kind = kind;
            row.TaskId = taskId;
            row.Text = text;
            row.Outcome = outcome;
            row.CreatedTimeMs = createdTimeMs;
            row.LastSeenMs = lastSeenMs;
            row.UpdateCount = updateCount;
            List<MemoryRow> one = new List<MemoryRow>(1);
            one.Add(row);
            return RestoreRows(one) == 1;
        }

        // Batch restore (the P28 restore path). Whole-agent semantics are
        // decided against the BATCH-START live state: an agent that had ANY
        // memory before this call is skipped entirely (all its rows), while
        // rows restored for a previously-absent agent may legitimately share
        // one agent (the first Upsert creates it; later rows update in place
        // — same save, not a live/saved conflict).
        public static int RestoreRows(List<MemoryRow> rows)
        {
            if (rows == null) return 0;
            HashSet<string> liveAtStart;
            lock (m_Lock)
            {
                liveAtStart = new HashSet<string>(S.Agents.Keys, StringComparer.Ordinal);
            }
            int inserted = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                MemoryRow row = rows[i];
                if (!PersonalityFactory.IsValidAgentId(row.AgentId))
                {
                    lock (m_Lock) { S.RestoreRefused++; }
                    continue;
                }
                if (row.Kind != MemoryKind.Location && row.Kind != MemoryKind.TaskOutcome && row.Kind != MemoryKind.CrewEvent)
                {
                    lock (m_Lock) { S.RestoreRefused++; }
                    continue;
                }
                if (row.Kind == MemoryKind.TaskOutcome)
                {
                    if (row.TaskId <= 0 || !IsKnownOutcome(row.Outcome))
                    {
                        lock (m_Lock) { S.RestoreRefused++; }
                        continue;
                    }
                }
                else
                {
                    if (row.TaskId != 0) { lock (m_Lock) { S.RestoreRefused++; } continue; }
                    if (row.Kind == MemoryKind.Location)
                    {
                        if (row.Text == null || row.Text.Length == 0 || row.Text.Length > MaxTextLen)
                        {
                            lock (m_Lock) { S.RestoreRefused++; }
                            continue;
                        }
                    }
                    else if (!IsValidText(row.Text) || (row.Text != null && row.Text.Length == 0))
                    {
                        lock (m_Lock) { S.RestoreRefused++; }
                        continue;
                    }
                }
                if (row.LastSeenMs < 0 || row.UpdateCount < 0)
                {
                    lock (m_Lock) { S.RestoreRefused++; }
                    continue;
                }
                if (liveAtStart.Contains(row.AgentId))
                {
                    lock (m_Lock) { S.RestoreSkipped++; } // whole-agent skip — live wins
                    continue;
                }
                // Outside the lock: through Upsert (single validation/eviction
                // authority). Upsert re-validates the agent id and payload.
                if (Upsert(row.AgentId, row.Kind, row.TaskId, row.Text, row.Outcome, row.LastSeenMs))
                {
                    lock (m_Lock) { S.Restored++; }
                    inserted++;
                }
            }
            return inserted;
        }

        public static long RestoredCount { get { lock (m_Lock) return S.Restored; } }
        public static long RestoreSkippedCount { get { lock (m_Lock) return S.RestoreSkipped; } }
        public static long RestoreRefusedCount { get { lock (m_Lock) return S.RestoreRefused; } }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Agents.Clear();
                S.AgentsCreated = 0;
                S.Writes = 0;
                S.Recalls = 0;
                S.Evictions = 0;
                S.Refused = 0;
                S.Restored = 0;
                S.RestoreSkipped = 0;
                S.RestoreRefused = 0;
                m_OnDecision = null;
                m_NowMsProvider = null;
            }
        }
    }
}