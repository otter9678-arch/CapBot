using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;

namespace CapBot.Core.Crew
{
    // ---- Phase 10: crew agent registry -----------------------------------------
    //
    // Per-bot state lives HERE — a bounded registry keyed by stable AgentId
    // ("AGT:<hash8>") — never in shared statics/floats/flags. The registry is
    // the single owner and mutator of CrewAgent records; it diffs the crew
    // section of the authoritative Phase 6 world snapshot (WorldStateService.
    // Latest via the world seam) and keeps one agent per crew member.
    //
    // Sync is a bounded-cadence, host-side diff — never per frame, never a
    // scene scan, no FindObjectsOfType, no LINQ:
    //
    //   WorldSnapshot.Crew (authoritative observation, P6)
    //     -> freshness gate (fail-safe on stale/missing snapshots)
    //     -> authority gate (deny-by-default: clients never build agent state)
    //     -> diff against live agents: create / update / deactivate / reactivate
    //     -> per-agent task observation (read-only; TaskRegistry is the source
    //        of truth for task state)
    //
    // The registry NEVER:
    //   - stores a PLPlayer/PLBot/pawn reference (sector transitions invalidate
    //     them; game objects live only in the P6 capture seam),
    //   - creates, queues, claims, or executes tasks (scheduler/claims/
    //     capabilities/executor untouched — task fields are metadata only),
    //   - writes PULSAR world state (agents are read-oriented observations),
    //   - lets clients build authoritative agent state (authority probe).
    //
    // Identity: "AGT:<hash8>" = FNV-1a (ActionIdentity.ComputeStableHash) over
    // a bounded seed ("B|<playerId>" bot / "H|<playerId>" human). Recomputed
    // identically on rejoin — stable for the crew member's lifetime, immune to
    // name/class changes, and never parsed or dispatched on (compared only).
    public static class CrewAgentRegistry
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 1000;          // sync cadence gate (snapshot refresh is 1 Hz; never per frame)
        public const int MaxAgents = 32;               // bounded live map (PULSAR crews are far smaller; MoreBots headroom)
        public const int MaxHistory = 16;              // bounded removed-agent history
        public const int RemovalGraceMs = 15000;       // absent -> Removed after this long unconfirmed
        public const int TaskVanishedGraceMs = 10000;  // task missing from registry this long -> clear assignment
        public const int MaxCapabilityRefs = 8;        // bounded per-agent capability-reference list
        public const int MaxCapabilityIdLen = 32;      // same rule as CapabilityDescriptor ids
        public const int MaxNameLen = CrewMemberSnapshot.MaxNameLen; // 32

        // LastTaskOutcome vocabulary (static; compared/logged, never parsed).
        public const string OutcomeCompleted = "COMPLETED";
        public const string OutcomeCancelled = "CANCELLED";
        public const string OutcomeExpired = "EXPIRED";
        public const string OutcomeFailed = "FAILED";
        public const string OutcomeVanished = "VANISHED";

        private sealed class RegistryState
        {
            public readonly Dictionary<string, CrewAgent> Agents =
                new Dictionary<string, CrewAgent>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastSyncMs = -1;
            public long Syncs;
            public long Created;
            public long Reactivated;
            public long Deactivated;
            public long Removed;
            public long DuplicateCreates;
            public long StaleRejections;
            public long AuthorityChanges;
            public long AssignmentsAccepted;
            public long AssignmentsRefused;
            public long TaskObservations;
            public string LastUncertainReason;
            public bool LastAuthorityKnown;   // false = never evaluated
            public bool LastAuthorityValue;
        }

        private static readonly RegistryState S = new RegistryState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed) -------------------------------------
        private static Func<bool> m_AuthorityProbe;        // null/fault => no-op sync
        private static Func<int> m_NowMsProvider;          // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Func<int, string> m_RoleNameResolver; // production: PLPlayer.GetClassNameFromID (static, public)
        private static Action<string> m_OnDecision;        // CrewAgentLogBridge attaches at boot

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_AuthorityProbe = probe; }
        public static void SetNowMsProvider(Func<int> provider) { lock (m_Lock) m_NowMsProvider = provider; }
        public static void SetWorldProvider(Func<WorldSnapshot> provider) { lock (m_Lock) m_WorldProvider = provider; }
        public static void SetRoleNameResolver(Func<int, string> resolver) { lock (m_Lock) m_RoleNameResolver = resolver; }
        public static void SetDecisionListener(Action<string> listener) { lock (m_Lock) m_OnDecision = listener; }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // ---- identity ------------------------------------------------------------
        public static string MakeAgentId(int playerId, bool isBot)
        {
            uint h = ActionIdentity.ComputeStableHash((isBot ? "B|" : "H|") + playerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return "AGT:" + h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---- readback (diagnostics/tests) ----------------------------------------
        public static int AgentCount { get { lock (m_Lock) return S.Agents.Count; } }
        public static int ActiveAgentCount { get { lock (m_Lock) return CountLifecycle(CrewAgentLifecycle.Active); } }
        public static int InactiveAgentCount { get { lock (m_Lock) return CountLifecycle(CrewAgentLifecycle.Inactive); } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long SyncCount { get { lock (m_Lock) return S.Syncs; } }
        public static long CreatedCount { get { lock (m_Lock) return S.Created; } }
        public static long ReactivatedCount { get { lock (m_Lock) return S.Reactivated; } }
        public static long DeactivatedCount { get { lock (m_Lock) return S.Deactivated; } }
        public static long RemovedCount { get { lock (m_Lock) return S.Removed; } }
        public static long DuplicateCreateCount { get { lock (m_Lock) return S.DuplicateCreates; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static long AssignmentAcceptedCount { get { lock (m_Lock) return S.AssignmentsAccepted; } }
        public static long AssignmentRefusedCount { get { lock (m_Lock) return S.AssignmentsRefused; } }
        public static long TaskObservationCount { get { lock (m_Lock) return S.TaskObservations; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        private static int CountLifecycle(CrewAgentLifecycle lifecycle)
        {
            int n = 0;
            foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
            {
                if (kv.Value.Lifecycle == lifecycle) n++;
            }
            return n;
        }

        // Deterministic lookup by stable id (null when absent).
        public static CrewAgent GetAgent(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            lock (m_Lock)
            {
                CrewAgent a;
                return S.Agents.TryGetValue(agentId, out a) ? a : null;
            }
        }

        // Deterministic lookup by PULSAR identity (null when absent).
        public static CrewAgent FindByPlayerId(int playerId)
        {
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                {
                    if (kv.Value.PlayerId == playerId) return kv.Value;
                }
            }
            return null;
        }

        // One bounded diagnostic line per agent (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                {
                    CrewAgent a = kv.Value;
                    lines.Add(a.AgentId + "|pid=" + a.PlayerId + (a.IsBot ? "|bot" : "|human")
                        + "|role=" + a.Role + "|class=" + a.ClassId
                        + "|life=" + a.Lifecycle + "|capt=" + (a.IsCaptain ? "1" : "0")
                        + "|task=" + (a.CurrentTaskId > 0 ? a.CurrentTaskId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")
                        + "|upd=" + a.UpdateCount);
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
                lines.Add("agents=" + S.Agents.Count + " history=" + S.HistoryIds.Count
                    + " syncs=" + S.Syncs + " created=" + S.Created
                    + " reactivated=" + S.Reactivated + " deactivated=" + S.Deactivated
                    + " removed=" + S.Removed + " dupCreates=" + S.DuplicateCreates);
                lines.Add("stale=" + S.StaleRejections + " assigned=" + S.AssignmentsAccepted
                    + " assignRefused=" + S.AssignmentsRefused + " taskObs=" + S.TaskObservations
                    + " authChanges=" + S.AuthorityChanges);
            }
            return lines;
        }

        // ---- the sync pass -----------------------------------------------------------
        //
        // Pulls the authoritative snapshot through the world seam (production:
        // WorldStateService.Latest) so the caller stays clock-only. Returns the
        // number of live registry mutations this pass (0 on the quiet path AND
        // on every failure path). Never throws.
        public static int Sync(int nowMs)
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
            TrackAuthority(isAuth, nowMs);
            if (!isAuth) return 0;

            lock (m_Lock)
            {
                if (S.LastSyncMs >= 0 && unchecked(nowMs - S.LastSyncMs) < MinRecheckMs) return 0;
                S.LastSyncMs = nowMs;
            }

            // ---- fail-safe snapshot gate ------------------------------------
            WorldSnapshot snapshot = null;
            if (world != null)
            {
                try { snapshot = world(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured)
            {
                MarkUncertain("no world snapshot captured (fail-safe: no agent sync)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no agent sync)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no agent sync)");
                return 0;
            }

            int mutations = 0;

            // ---- crew diff ---------------------------------------------------
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            if (snapshot.Crew != null)
            {
                for (int i = 0; i < snapshot.Crew.Count; i++)
                {
                    CrewMemberSnapshot c = snapshot.Crew[i];
                    if (c == null) continue;
                    crew.Add(c);
                }
            }

            List<KeyValuePair<string, CrewMemberSnapshot>> creations = new List<KeyValuePair<string, CrewMemberSnapshot>>();
            lock (m_Lock)
            {
                for (int i = 0; i < crew.Count; i++)
                {
                    CrewMemberSnapshot c = crew[i];
                    string agentId = MakeAgentId(c.PlayerId, c.IsBot);
                    CrewAgent agent;
                    if (!S.Agents.TryGetValue(agentId, out agent) || agent == null)
                    {
                        creations.Add(new KeyValuePair<string, CrewMemberSnapshot>(agentId, c));
                        continue;
                    }
                    UpdateAgentFromSnapshot(agent, c, nowMs);
                }

                // Present agents not seen in this snapshot become Inactive;
                // Active agents seen again reactivate (grace-covered absence).
                foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                {
                    CrewAgent a = kv.Value;
                    bool present = false;
                    for (int i = 0; i < crew.Count; i++)
                    {
                        if (crew[i].PlayerId == a.PlayerId && crew[i].IsBot == a.IsBot)
                        {
                            present = true;
                            break;
                        }
                    }
                    if (a.Lifecycle == CrewAgentLifecycle.Active && !present)
                    {
                        DeactivateAgent(a, nowMs, "absent from crew snapshot");
                        mutations++;
                    }
                    else if (a.Lifecycle == CrewAgentLifecycle.Inactive && present)
                    {
                        ReactivateAgent(a, nowMs, "present again in crew snapshot");
                        mutations++;
                    }
                }
            }

            // ---- removal pass FIRST (grace expiry frees bounded slots) ------
            List<string> expired = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                {
                    CrewAgent a = kv.Value;
                    if (a.Lifecycle != CrewAgentLifecycle.Inactive) continue;
                    if (a.AbsentSinceMs < 0) continue;
                    if (unchecked(nowMs - a.AbsentSinceMs) >= RemovalGraceMs) expired.Add(kv.Key);
                }
                foreach (string id in expired)
                {
                    RemoveAgent(id, nowMs, "removal grace expired");
                    mutations++;
                }
            }

            // ---- creation pass (slots already freed this pass) ---------------
            foreach (KeyValuePair<string, CrewMemberSnapshot> kv in creations)
            {
                if (CreateAgent(kv.Value, nowMs)) mutations++;
            }

            // ---- per-agent task observation ----------------------------------
            List<KeyValuePair<string, string>> outcomes = new List<KeyValuePair<string, string>>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                {
                    CrewAgent a = kv.Value;
                    if (a.CurrentTaskId <= 0) continue;
                    CapBotTask t;
                    try { t = TaskRegistry.Get(a.CurrentTaskId); }
                    catch (Exception) { t = null; }
                    if (t == null)
                    {
                        if (a.CurrentTaskAssignedMs >= 0
                            && unchecked(nowMs - a.CurrentTaskAssignedMs) >= TaskVanishedGraceMs)
                            outcomes.Add(new KeyValuePair<string, string>(kv.Key, OutcomeVanished));
                        continue;
                    }
                    if (!t.IsTerminal) continue;
                    outcomes.Add(new KeyValuePair<string, string>(kv.Key, OutcomeFromState(t.State)));
                }
                if (outcomes.Count > 0) S.TaskObservations += outcomes.Count;
            }
            foreach (KeyValuePair<string, string> kv in outcomes)
            {
                ClearTask(kv.Key, kv.Value, nowMs);
            }

            lock (m_Lock) S.Syncs++;
            return mutations;
        }

        // Sentinel: snapshots older than this never touch agents (fail-safe).
        public const int MaxStaleSnapshotMs = 20000; // same fail-safe window as the P9 director

        private static string OutcomeFromState(TaskState state)
        {
            switch (state)
            {
                case TaskState.Completed: return OutcomeCompleted;
                case TaskState.Cancelled: return OutcomeCancelled;
                case TaskState.Expired: return OutcomeExpired;
                case TaskState.Failed: return OutcomeFailed;
                default: return OutcomeVanished;
            }
        }

        // ---- agent mutation (callers hold m_Lock) ---------------------------------
        private static void UpdateAgentFromSnapshot(CrewAgent agent, CrewMemberSnapshot c, int nowMs)
        {
            agent.UpdateCount++;
            agent.LastSyncTimeMs = nowMs;
            agent.AbsentSinceMs = -1;

            if (agent.TeamId != c.TeamId) { agent.TeamId = c.TeamId; agent.LastChangeReason = "team"; }
            if (agent.ClassId != c.ClassId)
            {
                agent.ClassId = c.ClassId;
                agent.Role = CrewRoles.FromClassId(c.ClassId);
                agent.RoleName = ResolveRoleName(c.ClassId);
                agent.LastChangeReason = "class changed";
                Emit("AgentRoleChanged " + agent.AgentId + " pid=" + agent.PlayerId
                    + " role=" + agent.Role + " name=" + (agent.RoleName ?? "-"));
            }
            if (agent.Name != c.Name) { agent.Name = c.Name; agent.LastChangeReason = "name"; }
            if (agent.LastKnownTLIName != c.CurrentTLIName) { agent.LastKnownTLIName = c.CurrentTLIName; agent.LastChangeReason = "location"; }
            if (agent.IsCaptain != c.IsCaptain)
            {
                agent.IsCaptain = c.IsCaptain;
                agent.LastChangeReason = c.IsCaptain ? "captain flag set" : "captain flag cleared";
                Emit("AgentCaptainFlag " + agent.AgentId + " pid=" + agent.PlayerId
                    + " captain=" + (c.IsCaptain ? "1" : "0"));
            }
        }

        private static string ResolveRoleName(int classId)
        {
            Func<int, string> resolver;
            lock (m_Lock) resolver = m_RoleNameResolver;
            if (resolver == null) return null;
            try { return resolver(classId); }
            catch (Exception) { return null; }
        }

        private static void DeactivateAgent(CrewAgent agent, int nowMs, string reason)
        {
            agent.Lifecycle = CrewAgentLifecycle.Inactive;
            agent.AbsentSinceMs = nowMs;
            agent.LastChangeReason = reason;
            agent.UpdateCount++;
            S.Deactivated++;
            Emit("AgentDeactivated " + agent.AgentId + " pid=" + agent.PlayerId + " (" + reason + ")");
        }

        private static void ReactivateAgent(CrewAgent agent, int nowMs, string reason)
        {
            agent.Lifecycle = CrewAgentLifecycle.Active;
            agent.AbsentSinceMs = -1;
            agent.LastChangeReason = reason;
            agent.UpdateCount++;
            S.Reactivated++;
            Emit("AgentReactivated " + agent.AgentId + " pid=" + agent.PlayerId + " (" + reason + ")");
        }

        private static void RemoveAgent(string agentId, int nowMs, string reason)
        {
            CrewAgent removed;
            if (!S.Agents.TryGetValue(agentId, out removed)) return;
            S.Agents.Remove(agentId);
            S.HistoryIds.Enqueue(agentId);
            while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
            S.Removed++;
            removed.Lifecycle = CrewAgentLifecycle.Removed;
            removed.LastChangeReason = reason;
            Emit("AgentRemoved " + agentId + " pid=" + removed.PlayerId + " (" + reason + ")");
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("AgentSyncUncertain " + reason);
        }

        private static void TrackAuthority(bool isAuth, int nowMs)
        {
            lock (m_Lock)
            {
                if (S.LastAuthorityKnown && S.LastAuthorityValue == isAuth) return;
                S.LastAuthorityKnown = true;
                S.LastAuthorityValue = isAuth;
                S.AuthorityChanges++;
            }
            if (!isAuth)
            {
                // Authority LOST: clients must not independently keep
                // authoritative agent state — clear it (agents are rebuilt by
                // the next authoritative sync; identity is deterministic so
                // nothing user-visible is lost).
                int cleared;
                lock (m_Lock)
                {
                    cleared = S.Agents.Count;
                    S.Agents.Clear();
                    S.LastSyncMs = -1;
                }
                if (cleared > 0)
                {
                    Emit("AgentsClearedAuthorityLost count=" + cleared + " t=" + nowMs);
                }
            }
        }

        // ---- agent creation ------------------------------------------------------------
        private static bool CreateAgent(CrewMemberSnapshot c, int nowMs)
        {
            string agentId = MakeAgentId(c.PlayerId, c.IsBot);
            lock (m_Lock)
            {
                if (S.Agents.ContainsKey(agentId))
                {
                    // Duplicate creation is prevented, never a second record.
                    S.DuplicateCreates++;
                    UpdateAgentFromSnapshot(S.Agents[agentId], c, nowMs);
                    return false;
                }
                if (S.Agents.Count >= MaxAgents)
                {
                    // Registry full and every slot is a live crew member (the
                    // removal pass already ran; bounded by MaxAgents).
                    Emit("AgentCreateRefused " + agentId + " (registry full)");
                    return false;
                }
                CrewAgent agent = new CrewAgent(agentId, c.PlayerId, c.IsBot, nowMs);
                S.Agents[agentId] = agent;
                S.Created++;
                UpdateAgentFromSnapshot(agent, c, nowMs);
                Emit("AgentCreated " + agentId + " pid=" + c.PlayerId + (c.IsBot ? " bot" : " human")
                    + " role=" + agent.Role + " capt=" + (c.IsCaptain ? "1" : "0"));
            }
            return true;
        }

        // ---- task-assignment surface (assignment metadata only) --------------------------
        //
        // Accepts scheduler-produced task references as data. Does NOT create
        // tasks, queue them, claim them, or influence scheduling in any way —
        // P4 owns grants, P5 owns claims, P7/P8 own execution.
        public static bool AssignTask(string agentId, long taskId, string taskType, string capabilityId, int nowMs)
        {
            if (string.IsNullOrEmpty(agentId) || taskId <= 0) { Refused(); return false; }
            if (!string.IsNullOrEmpty(capabilityId)
                && (capabilityId.Length > MaxCapabilityIdLen || !IsValidCapabilityId(capabilityId)))
            { Refused(); return false; }
            lock (m_Lock)
            {
                CrewAgent a;
                if (!S.Agents.TryGetValue(agentId, out a)) { S.AssignmentsRefused++; return false; }
                a.CurrentTaskId = taskId;
                a.CurrentTaskType = taskType;
                a.CurrentTaskCapabilityId = capabilityId;
                a.CurrentTaskAssignedMs = nowMs;
                a.LastChangeReason = "task assigned";
                a.UpdateCount++;
                S.AssignmentsAccepted++;
                Emit("AgentTaskAssigned " + agentId + " task=" + taskId
                    + (string.IsNullOrEmpty(taskType) ? "" : " type=" + taskType)
                    + (string.IsNullOrEmpty(capabilityId) ? "" : " cap=" + capabilityId));
                return true;
            }
        }

        public static bool ClearTask(string agentId, string outcome, int nowMs)
        {
            if (string.IsNullOrEmpty(agentId)) return false;
            bool cleared;
            lock (m_Lock)
            {
                CrewAgent a;
                if (!S.Agents.TryGetValue(agentId, out a)) { cleared = false; }
                else if (a.CurrentTaskId <= 0) { cleared = false; }
                else
                {
                    long taskId = a.CurrentTaskId;
                    a.CurrentTaskId = 0;
                    a.CurrentTaskType = null;
                    a.CurrentTaskCapabilityId = null;
                    a.CurrentTaskAssignedMs = -1;
                    a.LastTaskOutcome = outcome;
                    a.LastTaskResultMs = nowMs;
                    a.LastChangeReason = "task " + (outcome ?? "cleared");
                    a.UpdateCount++;
                    Emit("AgentTaskResolved " + agentId + " task=" + taskId + " outcome=" + (outcome ?? "UNSPECIFIED"));
                    cleared = true;
                }
            }
            if (cleared)
            {
                // Phase 12: experience accrual — additive and fail-safe, fired
                // OUTSIDE the agent-registry lock so a faulting experience
                // listener can never affect agent state or task resolution.
                try { CrewExperienceRegistry.RecordOutcome(agentId, outcome, nowMs); }
                catch (Exception) { }
            }
            return cleared;
        }

        private static void Refused()
        {
            lock (m_Lock) S.AssignmentsRefused++;
        }

        private static bool IsValidCapabilityId(string id)
        {
            for (int i = 0; i < id.Length; i++)
            {
                char ch = id[i];
                bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_';
                if (!ok) return false;
            }
            return true;
        }

        // Registers a bounded, static-vocabulary capability reference on an
        // agent (future-phase integration point; data only, never dispatched).
        public static bool AddCapabilityReference(string agentId, string capabilityId)
        {
            if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(capabilityId)) return false;
            if (capabilityId.Length > MaxCapabilityIdLen || !IsValidCapabilityId(capabilityId)) return false;
            lock (m_Lock)
            {
                CrewAgent a;
                if (!S.Agents.TryGetValue(agentId, out a)) return false;
                if (a.CapabilityReferences.Count >= MaxCapabilityRefs) return false;
                if (a.CapabilityReferences.Contains(capabilityId)) return true;
                a.CapabilityReferences.Add(capabilityId);
                a.LastChangeReason = "capability reference added";
                a.UpdateCount++;
                return true;
            }
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Agents.Clear();
                S.HistoryIds.Clear();
                S.LastSyncMs = -1;
                S.Syncs = 0;
                S.Created = 0;
                S.Reactivated = 0;
                S.Deactivated = 0;
                S.Removed = 0;
                S.DuplicateCreates = 0;
                S.StaleRejections = 0;
                S.AuthorityChanges = 0;
                S.AssignmentsAccepted = 0;
                S.AssignmentsRefused = 0;
                S.TaskObservations = 0;
                S.LastUncertainReason = null;
                S.LastAuthorityKnown = false;
                S.LastAuthorityValue = false;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_RoleNameResolver = null;
                m_OnDecision = null;
            }
        }
    }
}