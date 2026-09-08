// Dev-side unit tests for the Phase 10 crew agent registry (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure crew-domain files (CrewAgent, CrewAgentRegistry) plus the
// P2/P4/P5/P6 domain files the registry integrates with. Time is virtual:
// the registry reads the injected nowMs provider — no real clock reads, no
// sleeping. Every Sync after the first advances the virtual clock past the
// registry's 1 s recheck gate (the gate itself is covered by S13's
// no-storm assertions).
//
// Covers the Phase 10 mandated scenarios:
//   S01 stable agent creation
//   S02 duplicate creation prevention
//   S03 bot removal
//   S04 stale player reference
//   S05 captain identification
//   S06 role/class mapping
//   S07 multiple simultaneous crew agents
//   S08 task ownership (assign/clear)
//   S09 scheduler interaction (owner-busy gate is scheduler-owned; the agent
//      holds assignment metadata only, never influences grants)
//   S10 recovery interaction (agent observes recovery's terminal outcome)
//   S11 execution-claim interaction (agent assignment is data only; claims
//      remain deny-by-default and untouched)
//   S12 emergency interaction (agent assignment mirrors emergency-task
//      metadata; the director keeps task ownership)
//   S13 deterministic agent lookup + bounded-cadence sync
//   S14 bounded agent registry
//   S15 join/leave lifecycle
//   S16 captain change
//   S17 client/master authority behavior
//   S18 no cross-agent state contamination
//   S19 no unauthorized execution (agent never creates/executes tasks)
//   S20 no invalid-player data ingestion (fail-safe gates)
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;

namespace CapBot.TaskTests
{
    internal static class CrewAgentTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        // ---- harness ---------------------------------------------------------

        private sealed class VirtualClock { public int NowMs; }
        private static VirtualClock s_Clock;
        private static WorldSnapshot s_Snap;
        private static readonly List<string> s_Lines = new List<string>();

        // Recovery rule-4 probe: owner unavailable (bot gone). Everything else
        // healthy — isolates the owner-unavailability path.
        private sealed class OwnerDownProbe : ITaskWorldProbe
        {
            public bool TargetValid(CapBotTask task) { return true; }
            public bool OwnerAvailable(string ownerActorId) { return false; }
            public bool CapabilityAvailable(CapBotTask task) { return true; }
            public bool WorldInvalidatesTask(CapBotTask task) { return false; }
        }

        // Probe that answers "all healthy" — used to keep recovery inert after
        // a rule-4 isolation step.
        private sealed class HealthyProbe : ITaskWorldProbe
        {
            public bool TargetValid(CapBotTask task) { return true; }
            public bool OwnerAvailable(string ownerActorId) { return true; }
            public bool CapabilityAvailable(CapBotTask task) { return true; }
            public bool WorldInvalidatesTask(CapBotTask task) { return false; }
        }

        private static void FreshSetup()
        {
            CrewAgentRegistry.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 200000 };
            s_Snap = null;
            s_Lines.Clear();
            CrewAgentRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetWorldProvider(delegate { return s_Snap; });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
            CrewAgentRegistry.SetRoleNameResolver(delegate(int classId)
            {
                switch (classId)
                {
                    case 0: return "Captain";
                    case 1: return "Pilot";
                    case 2: return "Scientist";
                    case 3: return "Weapons";
                    case 4: return "Engineer";
                    default: return "Crewman";
                }
            });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int Sync() { return CrewAgentRegistry.Sync(s_Clock.NowMs); }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static int CountLines(string prefix)
        {
            int n = 0;
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].StartsWith(prefix, StringComparison.Ordinal)) n++;
            }
            return n;
        }

        // ---- snapshot builders ------------------------------------------------

        private static CrewMemberSnapshot Member(int id, bool isBot, int classId, bool isCaptain, bool alive, float health)
        {
            return new CrewMemberSnapshot(id, (isBot ? "bot" : "human") + id, isBot, classId, 0,
                true, alive, health, "Bridge", isCaptain, isBot ? 3 : -1);
        }

        private static WorldSnapshot Snap(int timeMs, params CrewMemberSnapshot[] crew)
        {
            List<CrewMemberSnapshot> crewList = new List<CrewMemberSnapshot>(crew);
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 0.5f, false, 0, -1, 0, 10f));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crewList, new List<MissionSnapshot>(),
                new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN),
                new NavigationSnapshot(5, "Sector Five", -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false),
                new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        private static CapBotTask NewRunning(string owner, int priority)
        {
            CapBotTask t = CapBotTask.Create("TEST", owner, "crew agent test task", priority, 0, 60000, null, null, null);
            if (t == null) return null;
            if (!TaskRegistry.Register(t)) return null;
            t.TryQueue();
            t.TryStart();
            return t;
        }

        private static string Id(int playerId, bool isBot) { return CrewAgentRegistry.MakeAgentId(playerId, isBot); }

        // ---- the twenty scenarios -----------------------------------------------

        internal static int Run()
        {
            // S01 — stable agent creation
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 0, true, true, 1f)));
                Sync();
                string a = Id(1, true);
                CrewAgent agent = CrewAgentRegistry.GetAgent(a);
                Check(agent != null, "S01 agent created");
                Check(agent != null && agent.PlayerId == 1 && agent.IsBot, "S01 identity fields copied");
                Check(agent != null && agent.Lifecycle == CrewAgentLifecycle.Active, "S01 lifecycle active");
                Check(agent != null && agent.CreatedTimeMs == 200000 && agent.LastSyncTimeMs == 200000, "S01 timestamps stamped");
                Check(Id(1, true) == Id(1, true) && Id(1, true) != Id(2, true), "S01 deterministic identity");
                Check(Id(1, true) != Id(1, false), "S01 bot/human seeds differ");
                Check(agent != null && agent.AgentId.StartsWith("AGT:", StringComparison.Ordinal) && agent.AgentId.Length == 12, "S01 AGT hash8 shape");
            }

            // S02 — duplicate creation prevention
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 0, true, true, 1f)));
                Sync();
                Publish(Snap(201000, Member(1, true, 0, true, true, 0.9f)));
                Advance(1000);
                Sync();
                Check(CrewAgentRegistry.AgentCount == 1, "S02 one record after repeated sync");
                Check(CrewAgentRegistry.CreatedCount == 1, "S02 created count stayed 1");
                Check(CrewAgentRegistry.DuplicateCreateCount == 0, "S02 no duplicate-create counter drift");
                CrewAgent agent = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(agent != null && agent.LastSyncTimeMs == 201000, "S02 same record updated");
            }

            // S03 — bot removal
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 3, false, true, 1f)));
                Sync();
                Publish(Snap(201000)); // crew gone (bot destroyed)
                Advance(1000);
                Sync();
                CrewAgent agent = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(agent != null && agent.Lifecycle == CrewAgentLifecycle.Inactive, "S03 absent -> Inactive");
                Check(agent != null && agent.AbsentSinceMs == 201000, "S03 absence timestamped");
                Publish(Snap(220000));
                Advance(19000); // now 220000, absent for 19s >= 15s grace
                Sync();
                Check(CrewAgentRegistry.GetAgent(Id(1, true)) == null, "S03 grace expiry removes record");
                Check(CrewAgentRegistry.HistoryCount == 1, "S03 history recorded");
                Check(CrewAgentRegistry.RemovedCount == 1, "S03 removed counter");
            }

            // S04 — stale player reference
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 3, false, true, 1f)));
                Sync();
                // Stale world data: a crew entry for a player that never
                // re-appears, then a snapshot that no longer contains it — the
                // agent must deactivate and (grace-expired) be removed without
                // null-reference propagation or ghost resurrection.
                Publish(Snap(202000));
                Advance(2000);
                Sync();
                CrewAgent agent = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(agent != null && agent.Lifecycle == CrewAgentLifecycle.Inactive, "S04 stale ref deactivates agent");
                Publish(Snap(217000));
                Advance(15000);
                Sync();
                Check(CrewAgentRegistry.GetAgent(Id(1, true)) == null, "S04 stale ref removed after grace");
                // Reappearance after removal creates a FRESH record (same id).
                Publish(Snap(218000, Member(1, true, 3, false, true, 1f)));
                Advance(1000);
                Sync();
                CrewAgent reborn = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(reborn != null && reborn.CreatedTimeMs == 218000, "S04 rejoin after removal = fresh record");
            }

            // S05 — captain identification (authoritative snapshot flag only)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 0, true, true, 1f), Member(2, true, 1, false, true, 1f)));
                Sync();
                CrewAgent cap = CrewAgentRegistry.GetAgent(Id(1, true));
                CrewAgent pilot = CrewAgentRegistry.GetAgent(Id(2, true));
                Check(cap != null && cap.IsCaptain, "S05 captain flag on class-0 member");
                Check(pilot != null && !pilot.IsCaptain, "S05 non-captain flag clear");
                Check(cap != null && cap.Role == CrewRole.Captain, "S05 captain role mapped");
                Check(HasLineContaining("AgentCaptainFlag"), "S05 captain flag change logged");
            }

            // S06 — role/class mapping (verified class ids only)
            {
                FreshSetup();
                Publish(Snap(200000,
                    Member(1, true, 0, true, true, 1f),
                    Member(2, true, 1, false, true, 1f),
                    Member(3, true, 2, false, true, 1f),
                    Member(4, true, 3, false, true, 1f),
                    Member(5, true, 4, false, true, 1f),
                    Member(6, true, 7, false, true, 1f)));
                Sync();
                Check(CrewAgentRegistry.GetAgent(Id(1, true)).Role == CrewRole.Captain, "S06 class 0 -> Captain");
                Check(CrewAgentRegistry.GetAgent(Id(2, true)).Role == CrewRole.Pilot, "S06 class 1 -> Pilot");
                Check(CrewAgentRegistry.GetAgent(Id(3, true)).Role == CrewRole.Scientist, "S06 class 2 -> Scientist");
                Check(CrewAgentRegistry.GetAgent(Id(4, true)).Role == CrewRole.Weapons, "S06 class 3 -> Weapons");
                Check(CrewAgentRegistry.GetAgent(Id(5, true)).Role == CrewRole.Engineer, "S06 class 4 -> Engineer");
                Check(CrewAgentRegistry.GetAgent(Id(6, true)).Role == CrewRole.Other, "S06 unknown class -> Other (never invented)");
                Check(CrewAgentRegistry.GetAgent(Id(6, true)).RoleName == "Crewman", "S06 role name resolved via verified channel only");
            }

            // S07 — multiple simultaneous crew agents (per-bot state isolation)
            {
                FreshSetup();
                Publish(Snap(200000,
                    Member(1, true, 0, true, true, 1f),
                    Member(2, true, 1, false, true, 1f),
                    Member(3, true, 2, false, true, 1f),
                    Member(4, true, 3, false, true, 1f),
                    Member(5, true, 4, false, true, 1f)));
                Sync();
                Check(CrewAgentRegistry.AgentCount == 5, "S07 five agents registered");
                Check(CrewAgentRegistry.ActiveAgentCount == 5, "S07 five active");
                Check(CrewAgentRegistry.GetAgent(Id(1, true)).Role == CrewRole.Captain
                    && CrewAgentRegistry.GetAgent(Id(2, true)).Role == CrewRole.Pilot
                    && CrewAgentRegistry.GetAgent(Id(3, true)).Role == CrewRole.Scientist, "S07 per-agent roles distinct");
                // Class flip on one member only changes that agent.
                Publish(Snap(201000,
                    Member(1, true, 0, true, true, 1f),
                    Member(2, true, 2, false, true, 1f),
                    Member(3, true, 2, false, true, 1f),
                    Member(4, true, 3, false, true, 1f),
                    Member(5, true, 4, false, true, 1f)));
                Advance(1000);
                Sync();
                Check(CrewAgentRegistry.GetAgent(Id(2, true)).Role == CrewRole.Scientist, "S07 one agent's role updated");
                Check(CrewAgentRegistry.GetAgent(Id(3, true)).Role == CrewRole.Scientist, "S07 neighbor agent unaffected");
            }

            // S08 — task ownership (assignment metadata + observation)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 1, false, true, 1f)));
                Sync();
                string a = Id(1, true);
                long taskId = NewRunning("BOT:1", 3).TaskId;
                Check(CrewAgentRegistry.AssignTask(a, taskId, "TEST", null, s_Clock.NowMs), "S08 assignment accepted");
                CrewAgent agent = CrewAgentRegistry.GetAgent(a);
                Check(agent != null && agent.CurrentTaskId == taskId, "S08 CurrentTaskId recorded");
                Check(agent != null && agent.CurrentTaskAssignedMs == s_Clock.NowMs, "S08 assignment timestamp");
                // Terminal observation clears the assignment.
                s_Clock.NowMs += 1000;
                CapBotTask assigned = TaskRegistry.Get(taskId);
                assigned.TryComplete();
                Advance(2000);
                Sync();
                agent = CrewAgentRegistry.GetAgent(a);
                Check(agent != null && agent.CurrentTaskId == 0, "S08 terminal task cleared");
                Check(agent != null && OutcomeCompleted == agent.LastTaskOutcome, "S08 outcome recorded");
                Check(HasLineContaining("AgentTaskResolved"), "S08 resolution logged");
                // ClearTask on an agent with no task is a no-op false.
                Check(!CrewAgentRegistry.ClearTask(a, OutcomeCompleted, s_Clock.NowMs), "S08 double clear refused");
            }

            // S09 — scheduler interaction (assignment is data only)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 1, false, true, 1f)));
                Sync();
                string a = Id(1, true);
                CapBotTask t = NewRunning("BOT:1", 3);
                CrewAgentRegistry.AssignTask(a, t.TaskId, "TEST", null, s_Clock.NowMs);
                // One-grant-per-owner is scheduler-owned: a second queued task
                // for the same owner is NOT granted while the first runs —
                // regardless of agent state.
                CapBotTask second = CapBotTask.Create("SCHED_TYPE", "BOT:1", "scheduler test", 5, 2, -1, null, null, null);
                TaskRegistry.Register(second);
                second.TryQueue();
                TaskScheduler.SetDecisionListener(delegate { });
                TaskScheduler.Tick(s_Clock.NowMs);
                Check(!TaskScheduler.HasLease(second.TaskId), "S09 owner-busy gate holds (one grant per owner)");
                // A different owner's queued task IS granted — agent state is
                // irrelevant to the scheduler.
                CapBotTask other = CapBotTask.Create("SCHED_TYPE", "BOT:2", "scheduler test", 4, 2, -1, null, null, null);
                TaskRegistry.Register(other);
                other.TryQueue();
                Advance(1000);
                TaskScheduler.Tick(s_Clock.NowMs);
                Check(TaskScheduler.HasLease(other.TaskId), "S09 other owner granted normally");
                Check(CrewAgentRegistry.GetAgent(a).CurrentTaskId == t.TaskId, "S09 agent assignment untouched by scheduler passes");
                // Clearing the agent's assignment does NOT release leases.
                CrewAgentRegistry.ClearTask(a, OutcomeCompleted, s_Clock.NowMs);
                Check(TaskScheduler.HasLease(other.TaskId), "S09 scheduler state untouched by agent clear");
            }

            // S10 — recovery interaction (agent observes, recovery owns tasks)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 1, false, true, 1f)));
                Sync();
                string a = Id(1, true);
                CapBotTask t = NewRunning("BOT:1", 3);
                CrewAgentRegistry.AssignTask(a, t.TaskId, "TEST", null, s_Clock.NowMs);
                // Recovery rule 4: owner unavailable + Running -> FAIL.
                TaskRecoveryManager.Probe = new OwnerDownProbe();
                TaskRecoveryManager.Track(t);
                TaskRecoveryManager.Tick(t.CreatedTimeMs + 5000);
                Check(t.State == TaskState.Failed, "S10 recovery failed the owner-less task");
                // A Failed task is NOT terminal (recovery owns the retry
                // decision): the agent keeps the assignment.
                Advance(2000);
                Sync();
                Check(CrewAgentRegistry.GetAgent(a).CurrentTaskId == t.TaskId, "S10 failed-but-retryable task stays assigned");
                // Exhausted retries (MaxRetries=0): recovery cancels — terminal
                // (past the 2 s post-fail backoff window).
                TaskRecoveryManager.Tick(t.CreatedTimeMs + 8000);
                Check(t.State == TaskState.Cancelled, "S10 recovery cancelled the exhausted task");
                // Agent observes the terminal task on its next sync.
                TaskRecoveryManager.Probe = new HealthyProbe();
                Advance(10000);
                Sync();
                CrewAgent agent = CrewAgentRegistry.GetAgent(a);
                Check(agent != null && agent.CurrentTaskId == 0, "S10 agent observed terminal outcome");
                Check(agent != null && OutcomeCancelled == agent.LastTaskOutcome, "S10 CANCELLED outcome recorded");
            }

            // S11 — execution-claim interaction (deny-by-default untouched)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 1, false, true, 1f)));
                Sync();
                string a = Id(1, true);
                CapBotTask t = NewRunning("BOT:1", 3);
                CrewAgentRegistry.AssignTask(a, t.TaskId, "TEST", null, s_Clock.NowMs);
                // Claims stay deny-by-default (no authority policy wired here).
                ClaimResult r = ExecutionClaims.TryClaim(t.TaskId, "SET_CAPTAIN_ORDER", 0, "BOT:1", null, s_Clock.NowMs);
                Check(r == ClaimResult.RejectedNotAuthoritative, "S11 claims deny-by-default preserved");
                // Agent assignment must not have created any claim.
                Check(ExecutionClaims.LiveClaimCount == 0, "S11 no claim created by assignment");
                CrewAgentRegistry.ClearTask(a, OutcomeCancelled, s_Clock.NowMs);
                Check(ExecutionClaims.LiveClaimCount == 0, "S11 clear touched no claims");
            }

            // S12 — emergency interaction (director owns tasks, agent mirrors)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 1, false, true, 1f)));
                Sync();
                string a = Id(1, true);
                // The emergency director would create an EMERGENCY task owned
                // CAPTAIN; the agent's task surface is metadata-only, so an
                // assignment of such a task is accepted as data and the
                // director's ownership is untouched.
                CapBotTask em = CapBotTask.Create("EMERGENCY", "CAPTAIN", "emergency: test", 110, 1, 120000, "ORDER", null, null);
                TaskRegistry.Register(em);
                em.TryQueue();
                Check(CrewAgentRegistry.AssignTask(a, em.TaskId, "EMERGENCY", "SET_CAPTAIN_ORDER", s_Clock.NowMs), "S12 emergency task assignment accepted as data");
                CrewAgent agent = CrewAgentRegistry.GetAgent(a);
                Check(agent != null && agent.CurrentTaskCapabilityId == "SET_CAPTAIN_ORDER", "S12 capability metadata copied");
                Check(em.OwnerActorId == "CAPTAIN", "S12 director keeps ownership");
                Check(HasLineContaining("AgentTaskAssigned"), "S12 assignment logged");
            }

            // S13 — deterministic lookup + bounded-cadence sync (no storms)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 1, false, true, 1f)));
                Sync();
                Sync(); // same clock — gate must refuse
                Check(CrewAgentRegistry.SyncCount == 1, "S13 recheck gate refused same-tick sync");
                Advance(500);
                Sync();
                Check(CrewAgentRegistry.SyncCount == 1, "S13 gate refused sub-gate sync");
                Advance(600); // now 1500ms past
                Sync();
                Check(CrewAgentRegistry.SyncCount == 2, "S13 gate allowed post-gate sync");
                Check(CrewAgentRegistry.GetAgent(Id(1, true)) == CrewAgentRegistry.FindByPlayerId(1), "S13 by-id and by-player lookups agree");
                Check(CrewAgentRegistry.GetAgent(null) == null && CrewAgentRegistry.GetAgent("") == null, "S13 null/empty id lookup safe");
                Check(CrewAgentRegistry.FindByPlayerId(999) == null, "S13 unknown player lookup null");
            }

            // S14 — bounded agent registry
            {
                FreshSetup();
                CrewMemberSnapshot[] crew = new CrewMemberSnapshot[16];
                for (int i = 0; i < 16; i++) crew[i] = Member(i + 1, true, 1, false, true, 1f);
                Publish(Snap(200000, crew));
                Sync();
                Check(CrewAgentRegistry.AgentCount == 16, "S14 full 16-crew registered");
                // Snapshot >16 crew truncates at the WorldSnapshot boundary;
                // the registry stays bounded either way.
                List<CrewMemberSnapshot> big = new List<CrewMemberSnapshot>();
                for (int i = 0; i < 40; i++) big.Add(Member(i + 100, true, 1, false, true, 1f));
                List<ShipSnapshot> ships = new List<ShipSnapshot>();
                ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 0.5f, false, 0, -1, 0, 10f));
                Advance(1000);
                WorldSnapshot over = new WorldSnapshot(
                    s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                    ships, big, new List<MissionSnapshot>(),
                    new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN),
                    new NavigationSnapshot(5, "Sector Five", -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false),
                    new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                    new List<WorldObjectSnapshot>(),
                    WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                    -1, float.NaN);
                Publish(over);
                Sync();
                Check(CrewAgentRegistry.AgentCount <= CrewAgentRegistry.MaxAgents, "S14 registry bounded at MaxAgents");
                Check(CrewAgentRegistry.AgentCount == 16 + 16, "S14 snapshot truncation bounded additions");
                // The old agents are Inactive now; after grace they free slots.
                Advance(CrewAgentRegistry.RemovalGraceMs);
                Publish(Snap(s_Clock.NowMs));
                Sync();
                Check(CrewAgentRegistry.AgentCount <= CrewAgentRegistry.MaxAgents, "S14 removal freed slots within bound");
            }

            // S15 — join/leave lifecycle (multiple cycles)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 1, false, true, 1f)));
                Sync();
                // leave
                Publish(Snap(201000));
                Advance(1000);
                Sync();
                Check(CrewAgentRegistry.GetAgent(Id(1, true)).Lifecycle == CrewAgentLifecycle.Inactive, "S15 leave -> Inactive");
                // rejoin within grace: deactivation happened at 201000, the
                // rejoin sync at 202000 is within the 15 s grace window.
                Publish(Snap(202000, Member(1, true, 1, false, true, 1f)));
                Advance(1000);
                Sync();
                CrewAgent agent = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(agent != null && agent.Lifecycle == CrewAgentLifecycle.Active, "S15 rejoin within grace -> Active");
                Check(CrewAgentRegistry.CreatedCount == 1, "S15 no second record created");
                Check(CrewAgentRegistry.ReactivatedCount == 1, "S15 reactivation counted");
                // leave again; removal grace expires only after 15 s absent
                Publish(Snap(203000));
                Advance(1000);
                Sync();
                Check(CrewAgentRegistry.GetAgent(Id(1, true)).Lifecycle == CrewAgentLifecycle.Inactive, "S15 second leave -> Inactive");
                Publish(Snap(226000));
                Advance(23000);
                Sync();
                Check(CrewAgentRegistry.GetAgent(Id(1, true)) == null, "S15 long absence removed");
                Publish(Snap(227000, Member(1, true, 1, false, true, 1f)));
                Advance(1000);
                Sync();
                Check(CrewAgentRegistry.GetAgent(Id(1, true)) != null, "S15 rejoin after removal");
                Check(CrewAgentRegistry.CreatedCount == 2, "S15 second lifecycle created one more record");
            }

            // S16 — captain change
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 0, true, true, 1f), Member(2, true, 1, false, true, 1f)));
                Sync();
                // Captain flag moves from agent 1 to agent 2 (e.g. human takes
                // the chair or the captain bot is replaced).
                Publish(Snap(202000, Member(1, true, 0, false, true, 1f), Member(2, true, 1, true, true, 1f)));
                Advance(2000);
                Sync();
                Check(!CrewAgentRegistry.GetAgent(Id(1, true)).IsCaptain, "S16 old captain flag cleared");
                Check(CrewAgentRegistry.GetAgent(Id(2, true)).IsCaptain, "S16 new captain flag set");
                int flagLines = CountLines("AgentCaptainFlag");
                Check(flagLines >= 2, "S16 both flag changes logged");
                // Exactly one captain agent exists at any time.
                int captains = 0;
                foreach (string line in CrewAgentRegistry.Lines())
                {
                    if (line.Contains("|capt=1")) captains++;
                }
                Check(captains == 1, "S16 exactly one captain agent");
            }

            // S17 — client/master authority behavior
            {
                FreshSetup();
                CrewAgentRegistry.SetAuthorityProbe(delegate { return false; });
                Publish(Snap(200000, Member(1, true, 0, true, true, 1f)));
                Sync();
                Check(CrewAgentRegistry.AgentCount == 0, "S17 non-authoritative sync creates nothing");
                Check(CrewAgentRegistry.SyncCount == 0, "S17 non-authoritative sync is a no-op");
                // Switch to master: sync proceeds.
                CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
                Sync();
                Check(CrewAgentRegistry.AgentCount == 1, "S17 master sync builds agents");
                // Lose authority mid-flight: agents cleared (no stale client state).
                CrewAgentRegistry.SetAuthorityProbe(delegate { return false; });
                Advance(1000);
                Sync();
                Check(CrewAgentRegistry.AgentCount == 0, "S17 authority loss clears agents");
                Check(HasLineContaining("AgentsClearedAuthorityLost"), "S17 authority-loss logged");
            }

            // S18 — no cross-agent state contamination
            {
                FreshSetup();
                Publish(Snap(200000,
                    Member(1, true, 1, false, true, 1f),
                    Member(2, true, 2, false, true, 1f)));
                Sync();
                string a1 = Id(1, true);
                string a2 = Id(2, true);
                long t1 = NewRunning("BOT:1", 3).TaskId;
                long t2 = NewRunning("BOT:2", 4).TaskId;
                CrewAgentRegistry.AssignTask(a1, t1, "TEST", null, s_Clock.NowMs);
                CrewAgentRegistry.AssignTask(a2, t2, "TEST", null, s_Clock.NowMs);
                CrewAgent agent1 = CrewAgentRegistry.GetAgent(a1);
                CrewAgent agent2 = CrewAgentRegistry.GetAgent(a2);
                Check(agent1.CurrentTaskId == t1 && agent2.CurrentTaskId == t2, "S18 assignments isolated per agent");
                Check(agent1.CurrentTaskId != agent2.CurrentTaskId, "S18 no shared task id");
                // Resolve agent 2's task; agent 1 must be untouched.
                s_Clock.NowMs += 1000;
                TaskRegistry.Get(t2).TryComplete();
                Advance(2000);
                Sync();
                agent1 = CrewAgentRegistry.GetAgent(a1);
                agent2 = CrewAgentRegistry.GetAgent(a2);
                Check(agent1.CurrentTaskId == t1, "S18 agent 1 assignment survives neighbor resolution");
                Check(agent2.CurrentTaskId == 0 && OutcomeCompleted == agent2.LastTaskOutcome, "S18 agent 2 resolved independently");
                Check(agent1.LastTaskOutcome == null, "S18 agent 1 has no phantom outcome");
                // Role mutation of agent 1 must not leak into agent 2.
                Publish(Snap(s_Clock.NowMs, Member(1, true, 4, false, true, 1f), Member(2, true, 2, false, true, 1f)));
                Advance(1000);
                Sync();
                Check(CrewAgentRegistry.GetAgent(a1).Role == CrewRole.Engineer, "S18 agent 1 role changed");
                Check(CrewAgentRegistry.GetAgent(a2).Role == CrewRole.Scientist, "S18 agent 2 role unchanged");
            }

            // S19 — no unauthorized execution (agent never creates/executes)
            {
                FreshSetup();
                Publish(Snap(200000, Member(1, true, 1, false, true, 1f)));
                Sync();
                string a = Id(1, true);
                int registryCount = TaskRegistry.LiveCount;
                Sync();
                Advance(1000);
                Sync();
                Check(TaskRegistry.LiveCount == registryCount, "S19 sync creates no tasks");
                Check(CrewAgentRegistry.GetAgent(a).CurrentTaskId == 0, "S19 no task auto-assigned");
                // AssignTask with garbage is refused and creates nothing.
                Check(!CrewAgentRegistry.AssignTask(a, -5, "TEST", null, s_Clock.NowMs), "S19 negative task id refused");
                Check(!CrewAgentRegistry.AssignTask("AGT:doesnotexist", 1, "TEST", null, s_Clock.NowMs), "S19 unknown agent refused");
                Check(!CrewAgentRegistry.AssignTask(a, 1, "TEST", "BAD-ID!", s_Clock.NowMs), "S19 malformed capability id refused");
                Check(TaskRegistry.LiveCount == registryCount, "S19 refused assignments created no tasks");
                // AddCapabilityReference is data-only and bounded.
                Check(CrewAgentRegistry.AddCapabilityReference(a, "READ_WORLD_SNAPSHOT"), "S19 capability ref accepted");
                Check(!CrewAgentRegistry.AddCapabilityReference(a, "BAD ID WITH SPACES"), "S19 malformed capability ref refused");
                Check(CrewAgentRegistry.GetAgent(a).CapabilityReferences.Count == 1, "S19 capability ref recorded");
            }

            // S20 — fail-safe gates (invalid player data never ingested)
            {
                FreshSetup();
                // No snapshot at all.
                Sync();
                Check(CrewAgentRegistry.AgentCount == 0, "S20 no snapshot -> no agents");
                Check(CrewAgentRegistry.LastUncertainReason != null, "S20 uncertainty recorded");
                // Never-captured snapshot.
                Publish(WorldSnapshot.Empty);
                Advance(1000);
                Sync();
                Check(CrewAgentRegistry.AgentCount == 0, "S20 empty snapshot -> no agents");
                // Stale snapshot.
                Publish(Snap(200000, Member(1, true, 0, true, true, 1f)));
                Advance(30000); // 20s stale window exceeded
                Sync();
                Check(CrewAgentRegistry.AgentCount == 0, "S20 stale snapshot -> no agents");
                Check(CrewAgentRegistry.StaleRejectionCount >= 1, "S20 stale rejection counted");
                // Not-started game.
                List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
                crew.Add(Member(1, true, 0, true, true, 1f));
                List<ShipSnapshot> ships = new List<ShipSnapshot>();
                ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 0.5f, false, 0, -1, 0, 10f));
                WorldSnapshot notStarted = new WorldSnapshot(
                    s_Clock.NowMs, false, true, 7, WorldAuthority.MasterDerived,
                    ships, crew, new List<MissionSnapshot>(),
                    new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN),
                    new NavigationSnapshot(5, "Sector Five", -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false),
                    new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                    new List<WorldObjectSnapshot>(),
                    WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                    -1, float.NaN);
                Advance(1000);
                Publish(notStarted);
                Sync();
                Check(CrewAgentRegistry.AgentCount == 0, "S20 not-started game -> no agents");
                // Valid snapshot: null crew entries skipped safely.
                List<CrewMemberSnapshot> withNull = new List<CrewMemberSnapshot>();
                withNull.Add(null);
                withNull.Add(Member(1, true, 0, true, true, 1f));
                WorldSnapshot nullCrew = new WorldSnapshot(
                    s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                    ships, withNull, new List<MissionSnapshot>(),
                    new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN),
                    new NavigationSnapshot(5, "Sector Five", -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false),
                    new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                    new List<WorldObjectSnapshot>(),
                    WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                    -1, float.NaN);
                Advance(1000);
                Publish(nullCrew);
                Sync();
                Check(CrewAgentRegistry.AgentCount == 1, "S20 null crew entry skipped, valid entry ingested");
            }

            Console.WriteLine("CrewAgentTests passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        private const string OutcomeCompleted = "COMPLETED";
        private const string OutcomeFailed = "FAILED";
        private const string OutcomeCancelled = "CANCELLED";
    }
}