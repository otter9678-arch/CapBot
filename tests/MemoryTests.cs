// Dev-side unit tests for the Phase 13 crew memory layer (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure crew-domain files (CrewMemory) plus the Phase 10/11/12
// domains it builds on. Time is virtual: every timestamp is an explicit
// nowMs argument — no real clock reads, no sleeping.
//
// Covers the Phase 13 mandated scenarios:
//   M01 memory write/recall of task outcome through the real funnel
//   M02 location memory upsert + recall + read stamping
//   M03 crew-event memory + arbitrary text as DATA (never dispatched)
//   M04 bounded ring (8 entries/agent; oldest-by-LastSeenMs eviction)
//   M05 bounded registry (32 agents; deterministic refusal + slot freeing)
//   M06 no invalid-data ingestion (ids/outcomes/taskIds/text refused)
//   M07 no cross-agent contamination
//   M08 memory never influences scheduling/claims/execution/personality
//   M09 recall stamping (LastSeenMs/UpdateCount via the clock seam)
//   M10 fail-safe funnel (faulting memory layer never breaks ClearTask)
//   M11 ForgetAgent lifecycle + re-remember after forget
//   M12 upsert determinism (same key updates in place; distinct keys rows)
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;

namespace CapBot.TaskTests
{
    internal static class MemoryTests
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

        private static void FreshSetup()
        {
            CrewMemorySystem.ResetForTests();
            CrewExperienceRegistry.ResetForTests();
            CrewPersonalityRegistry.ResetForTests();
            CrewAgentRegistry.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            s_Clock = new VirtualClock { NowMs = 600000 };
            s_Snap = null;
            s_Lines.Clear();
            CrewMemorySystem.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewMemorySystem.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetWorldProvider(delegate { return s_Snap; });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
        }

        private static void Advance(int ms) { s_Clock.NowMs += ms; }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

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

        private static string Id(int pid, bool isBot)
        {
            return CrewAgentRegistry.MakeAgentId(pid, isBot);
        }

        private static CrewAgent FindAgentByTask(long taskId)
        {
            foreach (string line in CrewAgentRegistry.Lines())
            {
                string marker = "task=" + taskId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (line.IndexOf(marker, StringComparison.Ordinal) >= 0)
                {
                    int p = line.IndexOf("pid=", StringComparison.Ordinal);
                    if (p < 0) return null;
                    int start = p + 4;
                    int end = start;
                    while (end < line.Length && line[end] >= '0' && line[end] <= '9') end++;
                    int pid = int.Parse(line.Substring(start, end - start), System.Globalization.CultureInfo.InvariantCulture);
                    bool bot = line.IndexOf("|bot", StringComparison.Ordinal) >= 0;
                    return CrewAgentRegistry.GetAgent(Id(pid, bot));
                }
            }
            return null;
        }

        internal static int Run()
        {
            // ---- M01: task-outcome memory through the real funnel -----------------
            FreshSetup();
            string a = Id(11, true);
            s_Snap = Snap(s_Clock.NowMs, Member(11, true, 4, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            Check(CrewAgentRegistry.GetAgent(a) != null, "M01 agent created via sync");
            CapBotTask t1 = CapBotTask.Create("TEST", "BOT:11", "memory funnel", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(t1);
            t1.TryQueue();
            t1.TryStart();
            t1.TryComplete();
            CrewAgentRegistry.AssignTask(a, t1.TaskId, "TEST", null, s_Clock.NowMs);
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs); // observes terminal -> ClearTask -> remember
            CrewMemoryEntry m1 = CrewMemorySystem.RecallTaskOutcome(a, t1.TaskId);
            Check(m1 != null, "M01 outcome memory created by funnel");
            Check(m1.Outcome == CrewAgentRegistry.OutcomeCompleted, "M01 outcome recorded");
            Check(m1.Kind == MemoryKind.TaskOutcome, "M01 kind is TaskOutcome");
            Check(m1.TaskId == t1.TaskId, "M01 task id is the key");
            Check(CrewMemorySystem.MemoryCountOf(a) == 1, "M01 exactly one entry");
            Check(CrewMemorySystem.WriteCount == 1, "M01 write counted once");
            Check(HasLineContaining("MemoryStored " + a), "M01 store emitted");
            // Unknown agent -> no memory row recalled.
            string absent = Id(77, true);
            Check(CrewMemorySystem.RecallTaskOutcome(absent, t1.TaskId) == null, "M01 unknown agent recalls null");

            // ---- M02: location memory upsert + read stamping ------------------------
            FreshSetup();
            string l = Id(21, true);
            Check(CrewMemorySystem.RememberLocation(l, "Bridge", 1000), "M02 first location accepted");
            Check(CrewMemorySystem.RememberLocation(l, "Reactor", 1001), "M02 second location accepted");
            Check(CrewMemorySystem.RememberLocation(l, "Bridge", 1002), "M02 repeat location accepted");
            Check(CrewMemorySystem.MemoryCountOf(l) == 2, "M02 repeat upserted (no dup row)");
            // Inspect write stamps via RecallAll (read-only; recall not used yet).
            List<CrewMemoryEntry> locations = CrewMemorySystem.RecallAll(l, MemoryKind.Location);
            Check(locations.Count == 2, "M02 recall all returns both rows");
            Check(locations[0].Text == "Bridge" && locations[1].Text == "Reactor", "M02 insertion order preserved");
            CrewMemoryEntry bridge = locations[0];
            Check(bridge.LastSeenMs == 1002, "M02 bridge last-seen updated by repeat write");
            Check(bridge.UpdateCount == 1, "M02 bridge updated once (write path only)");
            // Explicit recall stamps the read (clock seam active in harness).
            CrewMemoryEntry bridgeRecall = CrewMemorySystem.Recall(l, MemoryKind.Location, "Bridge");
            Check(bridgeRecall == bridge, "M02 recall returns same entry");
            Check(bridgeRecall.LastSeenMs == s_Clock.NowMs, "M02 recall stamps LastSeenMs from clock");
            Check(bridgeRecall.UpdateCount == 2, "M02 recall increments update count");
            Check(CrewMemorySystem.Recall(l, MemoryKind.Location, "Engineering") == null, "M02 absent location null");
            Check(HasLineContaining("MemoryUpdated " + l), "M02 upsert emitted");

            // ---- M03: crew-event memory (text is DATA only) -------------------------
            FreshSetup();
            string e = Id(31, true);
            Check(CrewMemorySystem.RememberCrewEvent(e, "joined crew", 1000), "M03 event accepted");
            Check(CrewMemorySystem.RememberCrewEvent(e, "promoted", 1001), "M03 second event accepted");
            CrewMemoryEntry ev = CrewMemorySystem.Recall(e, MemoryKind.CrewEvent, "joined crew");
            Check(ev != null && ev.Text == "joined crew", "M03 event recalled");
            Check(CrewMemorySystem.MemoryCountOf(e) == 2, "M03 two rows");
            // A repeated identical event upserts in place.
            CrewMemorySystem.RememberCrewEvent(e, "joined crew", 1002);
            Check(CrewMemorySystem.MemoryCountOf(e) == 2, "M03 repeat event upserted");
            // Text is never parsed or dispatched on — kind vocabulary is fixed.
            Check(CrewMemorySystem.Recall(e, MemoryKind.CrewEvent, "MADE_UP_TEXT") == null, "M03 unknown event null");

            // ---- M04: bounded ring (8 per agent, oldest-by-LastSeenMs eviction) -----
            FreshSetup();
            string r = Id(41, true);
            for (int i = 1; i <= 8; i++)
            {
                Check(CrewMemorySystem.RememberLocation(r, "Loc" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 1000 + i),
                    "M04 location " + i + " accepted");
            }
            Check(CrewMemorySystem.MemoryCountOf(r) == 8, "M04 ring full at 8");
            Check(CrewMemorySystem.EvictionCount == 0, "M04 no eviction before overflow");
            // Oldest is Loc1 (seen at 1001). Revisit Loc3 (updates LastSeenMs)...
            CrewMemorySystem.RememberLocation(r, "Loc3", 1100);
            Check(CrewMemorySystem.MemoryCountOf(r) == 8, "M04 revisit keeps 8");
            // ...then a NEW location evicts the oldest-seen (Loc1, 1001), not Loc3 (1100).
            Check(CrewMemorySystem.RememberLocation(r, "Loc9", 1200), "M04 overflow write accepted");
            Check(CrewMemorySystem.MemoryCountOf(r) == 8, "M04 ring stays bounded at 8");
            Check(CrewMemorySystem.Recall(r, MemoryKind.Location, "Loc1") == null, "M04 oldest evicted");
            Check(CrewMemorySystem.Recall(r, MemoryKind.Location, "Loc3") != null, "M04 recently-revisited survivor kept");
            Check(CrewMemorySystem.Recall(r, MemoryKind.Location, "Loc9") != null, "M04 newest stored");
            Check(CrewMemorySystem.EvictionCount == 1, "M04 eviction counted once");
            Check(HasLineContaining("MemoryEvicted " + r), "M04 eviction emitted");
            // Kinds coexist in ONE bounded ring: the 8-entry cap is per agent,
            // and a NEW distinct fact evicts the OLDEST entry overall
            // (cross-kind oldest-by-LastSeenMs).
            CrewMemorySystem.RememberCrewEvent(r, "event", 1201);
            Check(CrewMemorySystem.MemoryCountOf(r) == 8, "M04 event stored, ring stays 8");
            Check(CrewMemorySystem.Recall(r, MemoryKind.Location, "Loc2") == null, "M04 oldest entry evicted (cross-kind)");
            Check(CrewMemorySystem.Recall(r, MemoryKind.CrewEvent, "event") != null, "M04 event row present");
            CrewMemorySystem.RememberTaskOutcome(r, 5, CrewAgentRegistry.OutcomeCompleted, 1202);
            Check(CrewMemorySystem.MemoryCountOf(r) == 8, "M04 task stored, ring stays 8");
            Check(CrewMemorySystem.RecallTaskOutcome(r, 5) != null, "M04 task row present");
            Check(CrewMemorySystem.EvictionCount == 3, "M04 evictions counted (Loc1, Loc2, Loc4)");
            // A further distinct location evicts the next-oldest entry
            // (Loc5 seen at 1005), never the fresh event/task rows.
            Check(CrewMemorySystem.RememberLocation(r, "Loc10", 1300), "M04 second overflow accepted");
            Check(CrewMemorySystem.MemoryCountOf(r) == 8, "M04 ring stays bounded");
            Check(CrewMemorySystem.Recall(r, MemoryKind.Location, "Loc5") == null, "M04 oldest location evicted");
            Check(CrewMemorySystem.Recall(r, MemoryKind.CrewEvent, "event") != null, "M04 event row untouched");
            Check(CrewMemorySystem.RecallTaskOutcome(r, 5) != null, "M04 task row untouched");
            // Existing-fact updates never evict.
            CrewMemorySystem.RememberLocation(r, "Loc10", 1301);
            Check(CrewMemorySystem.EvictionCount == 4, "M04 update does not evict");

            // ---- M05: bounded registry (32 agents, deterministic refusal) -----------
            FreshSetup();
            for (int pid = 1; pid <= 32; pid++)
            {
                Check(CrewMemorySystem.RememberLocation(Id(pid, true), "Home", 1000), "M05 agent " + pid + " stored");
            }
            Check(CrewMemorySystem.AgentCount == 32, "M05 cap reached exactly");
            Check(!CrewMemorySystem.RememberLocation(Id(33, true), "Home", 1000), "M05 new agent refused at cap");
            Check(CrewMemorySystem.RefusedCount >= 1, "M05 refusal counted");
            Check(HasLineContaining("MemoryRefused " + Id(33, true)), "M05 refusal emitted");
            // A write to an EXISTING agent still works at cap.
            Check(CrewMemorySystem.RememberLocation(Id(1, true), "Work", 1001), "M05 existing agent writes at cap");
            // Freeing a slot enables a new agent.
            Check(CrewMemorySystem.ForgetAgent(Id(5, true)), "M05 slot freed");
            Check(CrewMemorySystem.RememberLocation(Id(33, true), "Home", 1002), "M05 new agent accepted after free");

            // ---- M06: no invalid-data ingestion --------------------------------------
            FreshSetup();
            Check(!CrewMemorySystem.RememberLocation(null, "Bridge", 1000), "M06 null id refused");
            Check(!CrewMemorySystem.RememberLocation("", "Bridge", 1000), "M06 empty id refused");
            Check(!CrewMemorySystem.RememberLocation("garbage", "Bridge", 1000), "M06 non-AGT id refused");
            Check(!CrewMemorySystem.RememberLocation(Id(1, true), null, 1000), "M06 null location refused");
            Check(!CrewMemorySystem.RememberLocation(Id(1, true), "", 1000), "M06 empty location refused");
            Check(!CrewMemorySystem.RememberLocation(Id(1, true), new string('L', 40), 1000), "M06 overlong location refused");
            Check(!CrewMemorySystem.RememberTaskOutcome(Id(1, true), 0, CrewAgentRegistry.OutcomeCompleted, 1000), "M06 zero taskId refused");
            Check(!CrewMemorySystem.RememberTaskOutcome(Id(1, true), -5, CrewAgentRegistry.OutcomeCompleted, 1000), "M06 negative taskId refused");
            Check(!CrewMemorySystem.RememberTaskOutcome(Id(1, true), 5, "MADE_UP", 1000), "M06 unknown outcome refused");
            Check(!CrewMemorySystem.RememberTaskOutcome(Id(1, true), 5, null, 1000), "M06 null outcome refused");
            Check(!CrewMemorySystem.RememberCrewEvent(Id(1, true), "", 1000), "M06 empty event refused");
            Check(!CrewMemorySystem.RememberCrewEvent(Id(1, true), new string('E', 40), 1000), "M06 overlong event refused");
            Check(CrewMemorySystem.AgentCount == 0, "M06 nothing ingested");
            Check(CrewMemorySystem.Recall(null, MemoryKind.Location, "Bridge") == null, "M06 null id recall null");
            Check(CrewMemorySystem.RecallTaskOutcome(null, 5) == null, "M06 null id task recall null");
            Check(CrewMemorySystem.RecallTaskOutcome(Id(1, true), 0) == null, "M06 zero taskId recall null");
            Check(CrewMemorySystem.MemoryCountOf(null) == 0, "M06 null id count 0");
            Check(!CrewMemorySystem.ForgetAgent(null), "M06 forget refuses null id");
            Check(!CrewMemorySystem.ForgetAgent("garbage"), "M06 forget refuses garbage id");

            // ---- M07: no cross-agent contamination ------------------------------------
            FreshSetup();
            string x1 = Id(51, true);
            string x2 = Id(52, true);
            CrewMemorySystem.RememberLocation(x1, "Bridge", 1000);
            CrewMemorySystem.RememberTaskOutcome(x1, 9, CrewAgentRegistry.OutcomeCompleted, 1001);
            CrewMemorySystem.RememberCrewEvent(x2, "joined", 1002);
            Check(CrewMemorySystem.MemoryCountOf(x1) == 2, "M07 agent 1 has 2 rows");
            Check(CrewMemorySystem.MemoryCountOf(x2) == 1, "M07 agent 2 has 1 row");
            Check(CrewMemorySystem.Recall(x2, MemoryKind.Location, "Bridge") == null, "M07 agent 2 does not see agent 1 location");
            Check(CrewMemorySystem.RecallTaskOutcome(x2, 9) == null, "M07 agent 2 does not see agent 1 task");
            Check(CrewMemorySystem.Recall(x1, MemoryKind.CrewEvent, "joined") == null, "M07 agent 1 does not see agent 2 event");
            // Bot vs human same pid are different agents.
            Check(Id(7, true) != Id(7, false), "M07 bot/human ids distinct");
            CrewMemorySystem.RememberLocation(Id(7, true), "Bridge", 1003);
            Check(CrewMemorySystem.MemoryCountOf(Id(7, false)) == 0, "M07 bot write did not touch human id");

            // ---- M08: memory never influences scheduling/claims/execution --------------
            FreshSetup();
            CapBotTask tA = CapBotTask.Create("TEST", "BOT:60", "memory isolation A", 5, 0, 60000, null, null, null);
            CapBotTask tB2 = CapBotTask.Create("TEST", "CAPTAIN", "memory isolation B", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tA);
            TaskRegistry.Register(tB2);
            tA.TryQueue();
            tB2.TryQueue();
            // Heavy memory churn before/during scheduling.
            for (int i = 0; i < 25; i++)
            {
                CrewMemorySystem.RememberLocation(Id(60, true), "Loc" + (i % 4).ToString(System.Globalization.CultureInfo.InvariantCulture), s_Clock.NowMs);
            }
            CrewMemorySystem.RememberTaskOutcome(Id(60, true), 12345, CrewAgentRegistry.OutcomeFailed, s_Clock.NowMs);
            CrewMemorySystem.RememberCrewEvent(Id(62, true), "event under churn", s_Clock.NowMs);
            CrewMemorySystem.ForgetAgent(Id(62, true));
            int basePriorityA = tA.Priority;
            int granted1 = TaskScheduler.Tick(s_Clock.NowMs);
            Check(granted1 >= 1, "M08 scheduler grants normally");
            Check(TaskScheduler.HasLease(tA.TaskId), "M08 lease taken for owner A");
            Check(ExecutionClaims.TryClaim(tA.TaskId, "EXECUTE", 0, "BOT:60", "T", s_Clock.NowMs)
                == ClaimResult.Granted, "M08 claim granted for owner A");
            Check(tA.TryStart(), "M08 task A running");
            Advance(1000);
            CapBotTask tC = CapBotTask.Create("TEST", "BOT:60", "memory isolation C", 9, 0, 60000, null, null, null);
            TaskRegistry.Register(tC);
            tC.TryQueue();
            TaskScheduler.Tick(s_Clock.NowMs);
            Check(!TaskScheduler.HasLease(tC.TaskId), "M08 busy owner still not granted (one grant per owner)");
            Check(TaskScheduler.HasLease(tB2.TaskId), "M08 other owner granted independently");
            Check(tA.Priority == basePriorityA, "M08 priority untouched by memory churn");
            Check(tA.State == TaskState.Running, "M08 task state untouched");
            // Personality + experience records are equally untouched by memory writes.
            string pA = Id(60, true);
            CrewPersonalityRegistry.DeriveFor(pA, s_Clock.NowMs);
            CrewPersonality before = CrewPersonalityRegistry.Get(pA);
            CrewMemorySystem.RememberLocation(pA, "Bridge", s_Clock.NowMs);
            Check(CrewPersonalityRegistry.Get(pA) == before, "M08 personality record untouched by memory");
            Check(CrewExperienceRegistry.Get(pA) == null, "M08 experience record untouched by memory");

            // ---- M09: recall stamping via the clock seam --------------------------------
            FreshSetup();
            string q = Id(71, true);
            CrewMemorySystem.RememberLocation(q, "Bridge", 1000);
            int stampBefore = s_Clock.NowMs;
            CrewMemoryEntry q1 = CrewMemorySystem.Recall(q, MemoryKind.Location, "Bridge");
            Check(q1 != null, "M09 recall finds entry");
            Check(q1.LastSeenMs == stampBefore && q1.UpdateCount == 1, "M09 recall stamps from clock seam");
            Check(CrewMemorySystem.RecallCount == 1, "M09 recall counted");
            Advance(5000);
            CrewMemoryEntry q2 = CrewMemorySystem.Recall(q, MemoryKind.Location, "Bridge");
            Check(q2 == q1, "M09 same entry object on recall");
            Check(q2.LastSeenMs == s_Clock.NowMs, "M09 second recall re-stamps");
            Check(q2.UpdateCount == 2, "M09 update count increments");
            Check(CrewMemorySystem.RecallCount == 2, "M09 recall counted twice");
            // Eviction favors recalled facts: fill the ring, then overflow —
            // the recalled Bridge (fresh LastSeenMs) survives, the stale rows go.
            for (int i = 1; i <= 7; i++)
            {
                CrewMemorySystem.RememberLocation(q, "S" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 2000 + i);
            }
            Check(CrewMemorySystem.MemoryCountOf(q) == 8, "M09 ring full");
            Check(CrewMemorySystem.RememberLocation(q, "NEW", 3000), "M09 overflow accepted");
            Check(CrewMemorySystem.Recall(q, MemoryKind.Location, "Bridge") != null, "M09 recalled fact survives eviction");
            Check(CrewMemorySystem.Recall(q, MemoryKind.Location, "S1") == null, "M09 stale fact evicted");

            // ---- M10: fail-safe funnel (faulting memory layer never breaks ClearTask) ----
            FreshSetup();
            string f = Id(81, true);
            s_Snap = Snap(s_Clock.NowMs, Member(81, true, 1, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            Check(CrewAgentRegistry.GetAgent(f) != null, "M10 agent created");
            CapBotTask tF = CapBotTask.Create("TEST", "BOT:81", "fail-safe funnel", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tF);
            tF.TryQueue();
            tF.TryStart();
            tF.TryComplete();
            CrewAgentRegistry.AssignTask(f, tF.TaskId, "TEST", null, s_Clock.NowMs);
            // Force the memory system to throw on every task-outcome write.
            CrewMemorySystem.ResetForTests();
            CrewMemorySystem.SetDecisionListener(delegate (string line)
            {
                if (line.StartsWith("MemoryStored", StringComparison.Ordinal)) throw new InvalidOperationException("fault injection");
            });
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            CrewAgent agentF = CrewAgentRegistry.GetAgent(f);
            Check(agentF != null, "M10 agent survives memory fault");
            Check(agentF.CurrentTaskId == 0, "M10 task still resolved (funnel unaffected)");
            Check(agentF.LastTaskOutcome == CrewAgentRegistry.OutcomeCompleted, "M10 outcome still recorded on agent");
            // Registry-level: full cap must also not break the funnel.
            CrewMemorySystem.ResetForTests();
            CrewMemorySystem.SetDecisionListener(null);
            for (int pid = 1; pid <= 32; pid++)
            {
                CrewMemorySystem.RememberLocation(Id(2000 + pid, true), "Home", 1000);
            }
            CapBotTask tF2 = CapBotTask.Create("TEST", "BOT:81", "fail-safe funnel 2", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tF2);
            tF2.TryQueue();
            tF2.TryStart();
            tF2.TryComplete();
            CrewAgentRegistry.AssignTask(f, tF2.TaskId, "TEST", null, s_Clock.NowMs);
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            agentF = CrewAgentRegistry.GetAgent(f);
            Check(agentF.LastTaskOutcome == CrewAgentRegistry.OutcomeCompleted, "M10 full-registry refusal does not break funnel");

            // ---- M11: ForgetAgent lifecycle + re-remember after forget -------------------
            FreshSetup();
            string g = Id(91, true);
            CrewMemorySystem.RememberLocation(g, "Bridge", 1000);
            CrewMemorySystem.RememberTaskOutcome(g, 3, CrewAgentRegistry.OutcomeCompleted, 1001);
            CrewMemorySystem.RememberCrewEvent(g, "joined", 1002);
            Check(CrewMemorySystem.MemoryCountOf(g) == 3, "M11 three rows before forget");
            Check(CrewMemorySystem.ForgetAgent(g), "M11 forget accepted");
            Check(CrewMemorySystem.MemoryCountOf(g) == 0, "M11 all rows gone");
            Check(CrewMemorySystem.AgentCount == 0, "M11 agent slot freed");
            Check(!CrewMemorySystem.ForgetAgent(g), "M11 double forget refused");
            Check(HasLineContaining("MemoryForgotten " + g), "M11 forget emitted");
            // Re-remember after forget creates a fresh agent memory.
            CrewMemorySystem.RememberLocation(g, "Reactor", 1003);
            Check(CrewMemorySystem.MemoryCountOf(g) == 1, "M11 fresh memory after forget");
            Check(CrewMemorySystem.Recall(g, MemoryKind.Location, "Bridge") == null, "M11 old facts not resurrected");

            // ---- M12: upsert determinism ---------------------------------------------------
            FreshSetup();
            string u = Id(101, true);
            CrewMemorySystem.RememberTaskOutcome(u, 42, CrewAgentRegistry.OutcomeFailed, 1000);
            CrewMemorySystem.RememberTaskOutcome(u, 42, CrewAgentRegistry.OutcomeCompleted, 1001);
            Check(CrewMemorySystem.MemoryCountOf(u) == 1, "M12 same taskId mutates one row");
            CrewMemorySystem.RememberTaskOutcome(u, 43, CrewAgentRegistry.OutcomeCancelled, 1002);
            Check(CrewMemorySystem.MemoryCountOf(u) == 2, "M12 distinct taskIds are distinct rows");
            // Same location repeated never creates a duplicate row.
            CrewMemorySystem.RememberLocation(u, "Bridge", 1003);
            CrewMemorySystem.RememberLocation(u, "Bridge", 1004);
            Check(CrewMemorySystem.MemoryCountOf(u) == 3, "M12 location upsert keeps one row");
            // Same event text repeated never creates a duplicate row.
            CrewMemorySystem.RememberCrewEvent(u, "salvaged", 1005);
            CrewMemorySystem.RememberCrewEvent(u, "salvaged", 1006);
            Check(CrewMemorySystem.MemoryCountOf(u) == 4, "M12 event upsert keeps one row");
            // Insertion order is stable across mixed kinds; later outcome wins
            // with the later write stamp (inspected via read-only RecallAll).
            List<CrewMemoryEntry> allTasks = CrewMemorySystem.RecallAll(u, MemoryKind.TaskOutcome);
            Check(allTasks.Count == 2 && allTasks[0].TaskId == 42 && allTasks[1].TaskId == 43,
                "M12 task rows in insertion order");
            Check(allTasks[0].Outcome == CrewAgentRegistry.OutcomeCompleted && allTasks[0].LastSeenMs == 1001,
                "M12 later outcome wins with later stamp");
            Check(CrewMemorySystem.RecallTaskOutcome(u, 42) != null, "M12 task recall by id present");
            // Cross-kind key collision (same text, different kind) stays distinct.
            CrewMemorySystem.RememberLocation(u, "salvaged", 1007);
            Check(CrewMemorySystem.MemoryCountOf(u) == 5, "M12 same text across kinds is distinct rows");
            Check(CrewMemorySystem.Recall(u, MemoryKind.CrewEvent, "salvaged") != null, "M12 event row untouched");
            Check(CrewMemorySystem.Recall(u, MemoryKind.Location, "salvaged") != null, "M12 location row present");
            // Stats surface is bounded + deterministic (8 accepted writes so far).
            CrewMemorySystem.AgentMemoryStats st = CrewMemorySystem.StatsOf(u);
            Check(st != null && st.EntryCount == 5 && st.Writes == 8, "M12 stats entry/write counts");
            Check(CrewMemorySystem.Lines().Count == 5, "M12 Lines bounded");
            Check(CrewMemorySystem.StatusLines().Count == 1, "M12 StatusLines bounded");

            Console.WriteLine("SUITE MemoryTests passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}