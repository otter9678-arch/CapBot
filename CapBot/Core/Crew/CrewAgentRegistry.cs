using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Persistence;

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
            // P44 (directive 2): false-DEAD presence-machine counters.
            public long PresenceChanges;       // any presence transition
            public long DeathConfirmations;    // strong-evidence DEAD only
            public long TempUnavailables;      // observed-absent transitions
            public long Reconciles;            // ReconcileCrewAgent executions
            public long ReconcileRefusals;     // unknown identity / bad agentId refusals
            public long LastReconcileMs = -1;  // reconcile cadence anchor
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
        // P44 (directive 2): presence-machine readbacks (truthful counters).
        public static long PresenceChangeCount { get { lock (m_Lock) return S.PresenceChanges; } }
        public static long DeathConfirmationCount { get { lock (m_Lock) return S.DeathConfirmations; } }
        public static long TempUnavailableCount { get { lock (m_Lock) return S.TempUnavailables; } }
        public static long ReconcileCount { get { lock (m_Lock) return S.Reconciles; } }
        public static long ReconcileRefusalCount { get { lock (m_Lock) return S.ReconcileRefusals; } }
        public static long AliveAgentCount
        {
            get { lock (m_Lock) return CountPresence(AgentPresenceState.Alive); }
        }
        public static long TempUnavailableAgentCount
        {
            get { lock (m_Lock) return CountPresence(AgentPresenceState.TempUnavailable); }
        }
        public static long DeadAgentCount
        {
            get { lock (m_Lock) return CountPresence(AgentPresenceState.Dead); }
        }
        public static long RemovedAgentCount
        {
            get { lock (m_Lock) return CountPresence(AgentPresenceState.Removed); }
        }
        public static long SpawningAgentCount
        {
            get { lock (m_Lock) return CountPresence(AgentPresenceState.Spawning); }
        }

        private static long CountPresence(AgentPresenceState presence)
        {
            long n = 0;
            foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
            {
                if (kv.Value.Presence == presence) n++;
            }
            return n;
        }

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
                        + "|life=" + a.Lifecycle + "|presence=" + AgentPresence.Text(a.Presence)
                        + "|capt=" + (a.IsCaptain ? "1" : "0")
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
                lines.Add("presence spawn=" + SpawningAgentCount + " alive=" + AliveAgentCount
                    + " temp=" + TempUnavailableAgentCount + " dead=" + DeadAgentCount
                    + " removed=" + RemovedAgentCount
                    + " changes=" + S.PresenceChanges + " deaths=" + S.DeathConfirmations
                    + " tempEvents=" + S.TempUnavailables
                    + " reconcile=" + S.Reconciles + " reconcileRefused=" + S.ReconcileRefusals);
            }
            return lines;
        }

        // Phase 21 (additive readback): bounded point-in-time view of the
        // registry for advisory consumers (CrewAdvisor). Copies the fields an
        // advisor may read — never the live CrewAgent references (the
        // registry mediates all mutation; advisors hold no registry handles).
        public sealed class AgentView
        {
            public string AgentId;
            public int PlayerId;
            public bool IsBot;
            public bool IsCaptain;
            public CrewRole Role;
            public string RoleName;
            public string Name;
            public string LastKnownTLIName;
            public string LastTaskOutcome;
            public int LastTaskResultMs;
            public CrewAgentLifecycle Lifecycle;
        }

        public static List<AgentView> AgentViews()
        {
            List<AgentView> views = new List<AgentView>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                {
                    CrewAgent a = kv.Value;
                    AgentView v = new AgentView();
                    v.AgentId = a.AgentId;
                    v.PlayerId = a.PlayerId;
                    v.IsBot = a.IsBot;
                    v.IsCaptain = a.IsCaptain;
                    v.Role = a.Role;
                    v.RoleName = a.RoleName;
                    v.Name = a.Name;
                    v.LastKnownTLIName = a.LastKnownTLIName;
                    v.LastTaskOutcome = a.LastTaskOutcome;
                    v.LastTaskResultMs = a.LastTaskResultMs;
                    v.Lifecycle = a.Lifecycle;
                    views.Add(v);
                }
            }
            views.Sort(delegate (AgentView x, AgentView y)
            {
                return string.CompareOrdinal(x.AgentId, y.AgentId);
            });
            return views;
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
            List<string> removedThisPass = new List<string>();
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
                    removedThisPass.Add(id);
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

            // P40: personality reconciliation — AFTER S.Syncs++ (the sync is
            // complete), OUTSIDE the agent lock, fail-safe; bounded to at most
            // one pass of 32 ensure-derives per 1 s sync cadence.
            ReconcilePersonalities(removedThisPass, nowMs);

            // P44: memory reconciliation — same position/discipline as the
            // P40 personality reconcile: retention pass (confirmed removals)
            // then the idempotent ensure pass over every live agent.
            s_LastSyncNowMs = nowMs;
            ReconcileMemories(removedThisPass);
            return mutations;
        }

        // ---- P40: personality lifecycle reconciliation (fail-safe, additive) ---
        //
        // Population contract (mandate items 4/5/6): every LIVE (Active or
        // Inactive — still a crew member) agent carries exactly one
        // deterministic personality, keyed by the stable AgentId, created
        // lazily HERE (never at agent creation), never duplicated by repeated
        // AgentCreated events (EnsureFor is create-if-absent).
        //
        // Removal contract: a Removed agent is no longer a crew member — its
        // DERIVED record is removed so /capbotstatus counts the live crew
        // exactly. Matured/explicit/neutral records are NEVER removed here:
        // they are adaptive-learning state / persisted state / hand-authored
        // state, owned by their own phases (derivation is pure anyway — the
        // same agent re-derives identically on rejoin).
        //
        // Fail-safe discipline (same as the P12/P13/P25 hooks): the listener
        // runs OUTSIDE the agent lock, every personality call is in its own
        // try/catch, Sync's return value and agent state are never affected
        // by a personality fault. The registry-restore probe keeps the
        // reconcile inert while a save blob is being applied (live state must
        // win over re-derivation).
        private static void EnsurePersonality(string agentId, int nowMs)
        {
            try { CrewPersonalityRegistry.EnsureFor(agentId, nowMs); }
            catch (Exception) { }
        }

        private static void ReconcilePersonalities(List<string> removedAgentIds, int nowMs)
        {
            try
            {
                if (CrewPersistence.IsRestoring) return;
                // Removal pass FIRST: a removed agent's derived record must go
                // even when every remaining agent already has a record (the
                // ensure pass below early-returns in that steady state).
                List<string> toRemove = null;
                for (int i = 0; i < removedAgentIds.Count; i++)
                {
                    string agentId = removedAgentIds[i];
                    CrewPersonality p = CrewPersonalityRegistry.Get(agentId);
                    if (p == null) continue;
                    if (!string.Equals(p.Source, PersonalityFactory.SourceDerived, StringComparison.Ordinal))
                        continue;
                    try { CrewPersonalityRegistry.Remove(agentId); } catch (Exception) { }
                    if (toRemove == null) toRemove = new List<string>(4);
                    toRemove.Add(agentId);
                }
                if (toRemove != null)
                    Emit("PersonalityReconciled removed=" + toRemove.Count
                        + " derived records for removed agents");
                List<string> toEnsure = null;
                lock (m_Lock)
                {
                    foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                    {
                        CrewAgent a = kv.Value;
                        if (a.Lifecycle == CrewAgentLifecycle.Removed) continue;
                        if (CrewPersonalityRegistry.Get(a.AgentId) != null) continue;
                        if (toEnsure == null) toEnsure = new List<string>(4);
                        toEnsure.Add(a.AgentId);
                    }
                }
                if (toEnsure == null) return;
                for (int i = 0; i < toEnsure.Count; i++)
                    EnsurePersonality(toEnsure[i], nowMs);
                if (toEnsure.Count > 0)
                    Emit("PersonalityReconciled agents=" + toEnsure.Count
                        + " (sync-tail ensure: every live agent carries one record)");
            }
            catch (Exception) { }
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
            UpdatePresenceFromSnapshot(agent, c, nowMs);

            if (agent.TeamId != c.TeamId) { agent.TeamId = c.TeamId; agent.LastChangeReason = "team"; }
            if (agent.ClassId != c.ClassId)
            {
                agent.ClassId = c.ClassId;
                agent.Role = CrewRoles.FromClassId(c.ClassId);
                agent.RoleName = ResolveRoleName(c.ClassId);
                agent.LastChangeReason = "class changed";
                Emit("AgentRoleChanged " + agent.AgentId + " pid=" + agent.PlayerId
                    + " role=" + agent.Role + " name=" + (agent.RoleName ?? "-"));
                // P40: role-change reconcile — the record's identity is the
                // stable AgentId (unchanged by role), traits stay, the
                // archetype is a pure function of the trait spread (also
                // unchanged) and the role-affinity weights are consulted
                // per-lookup, so nothing is rewritten. The bounded line makes
                // the no-op observable in live logs (reconcile evidence).
                Emit("PersonalityReconciled " + agent.AgentId
                    + " (role change; identity/traits/archetype unchanged; role-affinity read per-lookup)");
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

        // ---- P44 (directive 2): PRESENCE state machine ----------------------------
        //
        // Orthogonal to Lifecycle: Lifecycle answers "in the crew snapshot /
        // retained in the registry"; Presence answers "is this crew member's
        // game avatar verifiably alive". The false-DEAD rule (owner mandate):
        // missing/unknown/stale data is TEMP_UNAVAILABLE, NEVER DEAD — only
        // CanConfirmAgentDeath strong evidence transitions to DEAD, and only
        // a confirmed removal (removal-grace expiry / RemoveAgent) transitions
        // to REMOVED. Every transition emits one bounded diagnostic line.
        private static void UpdatePresenceFromSnapshot(CrewAgent agent, CrewMemberSnapshot c, int nowMs)
        {
            // REMOVED never softens (the record is leaving the live map).
            if (agent.Presence == AgentPresenceState.Removed) return;

            if (c.AliveKnown)
            {
                if (c.Alive)
                {
                    // Positive alive evidence lifts even a DEAD verdict (game-
                    // reported revival); absence never does.
                    TransitionPresence(agent, AgentPresenceState.Alive, "snapshot: pawn present, game reports alive", nowMs);
                    return;
                }
                // AliveKnown && !Alive: the game itself reports the pawn dead.
                if (CanConfirmAgentDeath(agent, "game reports pawn dead", nowMs))
                {
                    TransitionPresence(agent, AgentPresenceState.Dead, "game reports pawn dead", nowMs);
                    return;
                }
                // Gate refused confirmation — record the first observation and
                // hold in TEMP_UNAVAILABLE (never a silent DEAD).
                TransitionPresence(agent, AgentPresenceState.TempUnavailable, "game reports pawn dead (awaiting confirmation)", nowMs);
                return;
            }

            // AliveKnown == false: pawn missing/unreadable at capture time —
            // the exact false-DEAD trap (spawn delay, sector transition, pawn
            // recreation, MoreBots recreation). TEMP_UNAVAILABLE, never DEAD.
            // A DEAD verdict also never softens on missing data. A still-
            // SPAWNING agent (creation seen, avatar never yet confirmed)
            // stays SPAWNING — temp-unavailable is reserved for data lost
            // AFTER the avatar was observed.
            if (agent.Presence == AgentPresenceState.Dead) return;
            if (agent.Presence == AgentPresenceState.Spawning) return;
            TransitionPresence(agent, AgentPresenceState.TempUnavailable, "pawn missing/unknown in snapshot", nowMs);
        }

        // Strong-evidence death gate (mandate: never display DEAD without it).
        // Evidence tiers:
        //   1. ALREADY-CONFIRMED death observation on this record
        //      (DeathObservedMs >= 0): confirmed once, stays DEAD.
        //   2. Repeated: the same death report observed on 2+ consecutive
        //      syncs spanning >= 1 s — a transient capture artifact cannot
        //      satisfy this.
        private static bool CanConfirmAgentDeath(CrewAgent agent, string evidence, int nowMs)
        {
            if (agent.DeathObservedMs >= 0) return true; // previously confirmed
            if (agent.PresenceReason != null
                && agent.PresenceReason.IndexOf("awaiting confirmation", StringComparison.Ordinal) >= 0)
            {
                // Second consecutive death report: confirm only if it spans at
                // least one sync interval (>= 1 s) — transient artifacts reset
                // on the very next ALIVE snapshot and never re-reach here.
                if (agent.PresenceSinceMs >= 0 && unchecked(nowMs - agent.PresenceSinceMs) >= MinRecheckMs)
                {
                    agent.DeathObservedMs = nowMs;
                    return true;
                }
            }
            return false;
        }

        // Single transition funnel: sets all presence fields, bumps counters,
        // emits the bounded CaptainAgentPresence diagnostic line.
        private static void TransitionPresence(CrewAgent agent, AgentPresenceState next, string reason, int nowMs)
        {
            if (agent.Presence == next && string.Equals(agent.PresenceReason, reason, StringComparison.Ordinal)) return;
            string old = AgentPresence.Text(agent.Presence);
            bool changed = agent.Presence != next;
            agent.Presence = next;
            agent.PresenceReason = reason;
            agent.PresenceSinceMs = nowMs;
            if (changed)
            {
                S.PresenceChanges++;
                if (next == AgentPresenceState.Dead) S.DeathConfirmations++;
                if (next == AgentPresenceState.TempUnavailable) S.TempUnavailables++;
            }
            Emit("CaptainAgentPresence id=" + agent.AgentId + " pid=" + agent.PlayerId
                + " from=" + old + " to=" + AgentPresence.Text(next)
                + " reason=" + reason + " t=" + nowMs);
        }

        // Idempotent MoreBots-player reconciliation (directive B item 6): one
        // logical agent per stable PlayerId. Reconnects a recreated pawn/PLBot/
        // AIData to the EXISTING record — personality, memory, task state all
        // preserved (identity is the stable AgentId; nothing is rebuilt).
        // Repeated calls produce the same logical agent; refusals are counted,
        // never thrown.
        public static bool ReconcileCrewAgent(int playerId, bool isBot, int nowMs)
        {
            if (unchecked((uint)playerId) > 0x7FFFFFFFu || playerId < 0)
            {
                lock (m_Lock) S.ReconcileRefusals++;
                Emit("CaptainAgentReconcileRefused pid=" + playerId + " (invalid identity)");
                return false;
            }
            string agentId = MakeAgentId(playerId, isBot);
            lock (m_Lock)
            {
                CrewAgent a;
                if (!S.Agents.TryGetValue(agentId, out a))
                {
                    S.ReconcileRefusals++;
                    Emit("CaptainAgentReconcileRefused pid=" + playerId + " bot=" + (isBot ? "1" : "0") + " (unknown agent)");
                    return false;
                }
                S.Reconciles++;
                S.LastReconcileMs = nowMs;
                // Reconnect semantics: any terminal/temporary verdict left by
                // stale data is lifted the next time a live snapshot confirms
                // the player; the reconcile itself only stamps cadence and
                // re-marks the record live — the NEXT sync's presence pass
                // (UpdatePresenceFromSnapshot) is the only ALIVE authority.
                a.LastSyncTimeMs = nowMs;
                a.AbsentSinceMs = -1;
            }
            // Identity/personality/memory/task metadata all hang off the SAME
            // stable AgentId — the reconnect is automatically complete. The
            // idempotent ensure keeps them present if a transient failure had
            // skipped their creation (mirrors CreateAgent's hooks).
            EnsurePersonality(agentId, nowMs);
            EnsureMemoryAgent(agentId, nowMs, "reconcile");
            Emit("CaptainAgentReconciled id=" + agentId + " pid=" + playerId
                + " bot=" + (isBot ? "1" : "0") + " t=" + nowMs);
            return true;
        }

        private static void DeactivateAgent(CrewAgent agent, int nowMs, string reason)
        {
            agent.Lifecycle = CrewAgentLifecycle.Inactive;
            agent.AbsentSinceMs = nowMs;
            agent.LastChangeReason = reason;
            agent.UpdateCount++;
            S.Deactivated++;
            // P44: observed absence is a TEMP_UNAVAILABLE signal, never death.
            // (Lifecycle Inactive + presence retained = grace-window crew member.)
            if (agent.Presence != AgentPresenceState.Dead && agent.Presence != AgentPresenceState.Removed)
                TransitionPresence(agent, AgentPresenceState.TempUnavailable, "observed absent: " + reason, nowMs);
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
            // P44: a CONFIRMED removal is REMOVED (distinct from DEAD); never
            // put a temporarily-unavailable agent on this path (the caller
            // only reaches here after the removal grace expired).
            if (removed.Presence != AgentPresenceState.Dead)
                TransitionPresence(removed, AgentPresenceState.Removed, "confirmed removal: " + reason, nowMs);
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

        // Phase 26: public authority-loss surface for the MultiplayerAuthority
        // Monitor (the WorldTick host gate makes TrackAuthority(false)
        // unreachable in production once authority is lost — the monitor is
        // the pre-gate observer that reaches this). Same semantics as the
        // TrackAuthority(false) clear: agents are volatile authoritative
        // state; identity is deterministic so the next authoritative sync
        // rebuilds everything. Returns the number cleared. Never throws.
        public static int ClearForAuthorityLoss(int nowMs)
        {
            try
            {
                int cleared;
                lock (m_Lock)
                {
                    cleared = S.Agents.Count;
                    if (cleared > 0)
                    {
                        S.Agents.Clear();
                        S.LastSyncMs = -1;
                        S.AuthorityChanges++;
                        S.LastAuthorityKnown = true;
                        S.LastAuthorityValue = false;
                    }
                }
                if (cleared > 0)
                {
                    Emit("AgentsClearedAuthorityLost count=" + cleared + " t=" + nowMs);
                }
                return cleared;
            }
            catch (Exception) { return 0; }
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
            // P40: personality hook — OUTSIDE the registry lock (the P11 lock
            // is taken inside), fail-safe, no influence on the creation
            // result. Every created agent leaves its creation sync with a
            // personality record (idempotent: existing record wins).
            EnsurePersonality(agentId, nowMs);
            // P44: memory hook — same discipline, same lock-free position.
            // Every created agent leaves its creation sync with exactly one
            // memory store keyed by the SAME stable AgentId (idempotent:
            // existing store wins; failure emits MemoryInitFailed and never
            // fakes the counter).
            EnsureMemoryAgent(agentId, nowMs, "agentCreated");
            return true;
        }

        // ---- P44: memory lifecycle reconciliation (fail-safe, additive) ---
        //
        // The memory twin of the P40 personality reconcile (same position in
        // Sync, same fail-safe discipline): every LIVE (Active or Inactive —
        // still a crew member) agent carries exactly one memory store keyed
        // by the stable AgentId; a REMOVED agent's store is dropped so
        // /capbotstatus counts the live crew exactly. MEMORY IS RETAINED
        // through every temporary lifecycle state — Inactive (absent pawn),
        // SPAWNING, TEMP_UNAVAILABLE, sector transitions, MoreBots pawn
        // recreation — only a CONFIRMED removal (removal grace expired)
        // reaches the retention pass.
        //
        // Fail-safe: outside the agent lock, every memory call in its own
        // try/catch, Sync's return value and agent state never affected by
        // a memory fault. Inert while a save blob is being applied (the
        // restore is insert-only; live state wins — same probe the
        // personality reconcile uses).
        private static void EnsureMemoryAgent(string agentId, int nowMs, string reason)
        {
            try { CrewMemorySystem.EnsureMemoryAgent(agentId, nowMs, reason); }
            catch (Exception) { }
        }

        private static void ReconcileMemories(List<string> removedAgentIds)
        {
            try
            {
                if (CrewPersistence.IsRestoring) return;
                // Retention pass FIRST: a removed agent's memory store must
                // go even when every remaining agent already has one (the
                // ensure pass below early-returns in that steady state).
                if (removedAgentIds != null && removedAgentIds.Count > 0)
                {
                    int removed = CrewMemorySystem.ReconcileRetention(removedAgentIds);
                    if (removed > 0)
                        Emit("MemoryReconciled removed=" + removed
                            + " store(s) for confirmed-removed agents");
                }
                List<string> toEnsure = null;
                lock (m_Lock)
                {
                    foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                    {
                        CrewAgent a = kv.Value;
                        if (a.Lifecycle == CrewAgentLifecycle.Removed) continue;
                        if (toEnsure == null) toEnsure = new List<string>(4);
                        toEnsure.Add(a.AgentId);
                    }
                }
                if (toEnsure == null) return;
                for (int i = 0; i < toEnsure.Count; i++)
                {
                    // Cheap idempotent re-check before the ensure call (the
                    // ensure itself is also idempotent — MemoryReused).
                    EnsureMemoryAgent(toEnsure[i], s_LastSyncNowMs, "reconcile");
                }
            }
            catch (Exception) { }
        }

        // Last Sync nowMs (reconcile runs after the sync completes; the
        // timestamps only feed diagnostics).
        private static int s_LastSyncNowMs = -1;

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
            long taskId = 0;
            lock (m_Lock)
            {
                CrewAgent a;
                if (!S.Agents.TryGetValue(agentId, out a)) { cleared = false; }
                else if (a.CurrentTaskId <= 0) { cleared = false; }
                else
                {
                    taskId = a.CurrentTaskId;
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
                // Phase 13: memory write — additive and fail-safe, same
                // discipline: outside the agent-registry lock, own try/catch;
                // a faulting memory layer can never affect agent state or
                // task resolution.
                try { CrewMemorySystem.RememberTaskOutcome(agentId, taskId, outcome, nowMs); }
                catch (Exception) { }
                // Phase 25: adaptive learning — additive and fail-safe, same
                // discipline: outside the agent-registry lock, own try/catch;
                // a faulting learning layer can never affect agent state or
                // task resolution. The director is deny-by-default (its own
                // authority gate) and consumes the SAME resolved outcome the
                // P12 accrual just processed.
                try { CapBot.Core.Learning.AdaptiveLearningDirector.NotifyOutcome(agentId, outcome, nowMs); }
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

        // ---- executor-facing owner-presence readback (P45 bounded wait) --------
        //
        // Answers "is this task owner's agent currently dispatchable" for the
        // scheduler's bounded-wait gate. Owner vocabulary: "CAPTAIN" (the
        // bot crew member carrying the captain flag) and "BOT:<playerId>".
        // Unknown formats/unknown agents answer Unknown — never a block.
        public static AgentPresenceState PresenceForOwner(string ownerActorId)
        {
            if (string.IsNullOrEmpty(ownerActorId)) return AgentPresenceState.Unknown;
            lock (m_Lock)
            {
                if (ownerActorId == "CAPTAIN")
                {
                    foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                    {
                        CrewAgent a = kv.Value;
                        if (a.IsCaptain && a.IsBot) return a.Presence;
                    }
                    return AgentPresenceState.Unknown;
                }
                if (ownerActorId.Length > 4 && ownerActorId.StartsWith("BOT:", StringComparison.Ordinal))
                {
                    int botId;
                    if (!int.TryParse(ownerActorId.Substring(4),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out botId))
                        return AgentPresenceState.Unknown;
                    foreach (KeyValuePair<string, CrewAgent> kv in S.Agents)
                    {
                        CrewAgent a = kv.Value;
                        if (a.IsBot && a.PlayerId == botId) return a.Presence;
                    }
                }
                return AgentPresenceState.Unknown;
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
                S.PresenceChanges = 0;
                S.DeathConfirmations = 0;
                S.TempUnavailables = 0;
                S.Reconciles = 0;
                S.ReconcileRefusals = 0;
                S.LastReconcileMs = -1;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_RoleNameResolver = null;
                m_OnDecision = null;
            }
        }
    }
}