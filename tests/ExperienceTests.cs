// Dev-side unit tests for the Phase 12 crew experience layer (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure crew-domain files (CrewExperience) plus the Phase 10/11
// domains it builds on. Time is virtual: every timestamp is an explicit
// nowMs argument — no real clock reads, no sleeping.
//
// Covers the Phase 12 mandated scenarios (master prompt structure):
//   X01 outcome accrual via the real Sync funnel (end-to-end)
//   X02 deterministic level math (thresholds, caps, monotonicity)
//   X03 points vocabulary (COMPLETED=10, others=2, unknown refused)
//   X04 per-outcome counters + last-outcome stamp
//   X05 level-up behavior (crossing thresholds)
//   X06 registry stability + Remove lifecycle
//   X07 no cross-agent contamination
//   X08 bounded registry (refusal at cap, deterministic)
//   X09 no invalid-data ingestion (garbage ids/outcomes refused)
//   X10 experience never influences scheduling/claims/execution
//   X11 accrual is fail-safe (funnel unaffected by experience-layer faults)
//   X12 ClearTask funnel regression (P10 semantics unchanged)
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;

namespace CapBot.TaskTests
{
    internal static class ExperienceTests
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
            CrewExperienceRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
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

        // Full end-to-end flow: publish snapshot, create an agent, assign a
        // task, complete it through the real task lifecycle, then let the
        // registry's Sync observation funnel resolve the outcome.
        private static void SyncUntilResolved(long taskId)
        {
            int guard = 0;
            CrewAgent agent = null;
            while (guard++ < 10)
            {
                Advance(1000);
                CrewAgentRegistry.Sync(s_Clock.NowMs);
                agent = FindAgentByTask(taskId);
                if (agent == null) return;
            }
        }

        private static CrewAgent FindAgentByTask(long taskId)
        {
            // Bounded scan over the bounded registry (tests only).
            foreach (string line in CrewAgentRegistry.Lines())
            {
                string marker = "task=" + taskId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (line.IndexOf(marker, StringComparison.Ordinal) >= 0)
                {
                    // extract pid from "pid=<n>" and bot flag from "|bot"
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
            // ---- X01: outcome accrual via the real Sync funnel ------------------
            FreshSetup();
            string a = Id(11, true);
            // Create the agent through the real snapshot path.
            s_Snap = Snap(s_Clock.NowMs, Member(11, true, 4, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            Check(CrewAgentRegistry.GetAgent(a) != null, "X01 agent created via sync");
            // Assign + run a task to Completed through the real lifecycle.
            CapBotTask t1 = CapBotTask.Create("TEST", "BOT:11", "experience funnel", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(t1);
            t1.TryQueue();
            t1.TryStart();
            t1.TryComplete();
            CrewAgentRegistry.AssignTask(a, t1.TaskId, "TEST", null, s_Clock.NowMs);
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs); // observes terminal -> ClearTask -> accrue
            CrewExperienceRecord r1 = CrewExperienceRegistry.Get(a);
            Check(r1 != null, "X01 experience record created by funnel");
            Check(r1.TasksCompleted == 1, "X01 completion counted");
            Check(r1.ExperiencePoints == CrewExperienceRegistry.PointsCompleted, "X01 completion points accrued");
            Check(r1.Level == 1, "X01 level after first completion");
            Check(r1.LastOutcome == CrewAgentRegistry.OutcomeCompleted, "X01 last outcome recorded");
            Check(r1.LastResultMs == s_Clock.NowMs, "X01 result stamp matches sync time");
            Check(CrewExperienceRegistry.AccrualCount == 1, "X01 accrual counted once");
            Check(HasLineContaining("ExperienceRecorded " + a), "X01 accrual emitted");

            // ---- X02: deterministic level math -----------------------------------
            Check(ExperienceLevels.LevelForXp(0) == 1, "X02 zero xp -> level 1");
            Check(ExperienceLevels.LevelForXp(49) == 1, "X02 below first threshold -> 1");
            Check(ExperienceLevels.LevelForXp(50) == 2, "X02 exactly threshold -> 2");
            Check(ExperienceLevels.LevelForXp(119) == 2, "X02 mid-band -> 2");
            Check(ExperienceLevels.LevelForXp(120) == 3, "X02 second threshold -> 3");
            Check(ExperienceLevels.LevelForXp(1550) == 10, "X02 last threshold -> 10");
            Check(ExperienceLevels.LevelForXp(100000) == 10, "X02 xp beyond cap stays 10");
            Check(ExperienceLevels.LevelForXp(-5) == 1, "X02 negative xp -> level 1");
            bool monotonic = true;
            long prev = 0;
            for (int i = 0; i < ExperienceLevels.Thresholds.Length; i++)
            {
                if (ExperienceLevels.Thresholds[i] < prev) monotonic = false;
                prev = ExperienceLevels.Thresholds[i];
            }
            Check(monotonic, "X02 thresholds monotonic");
            Check(ExperienceLevels.MaxLevel == 10, "X02 max level vocabulary");

            // ---- X03: points vocabulary -------------------------------------------
            Check(CrewExperienceRegistry.PointsForOutcome(CrewAgentRegistry.OutcomeCompleted)
                == CrewExperienceRegistry.PointsCompleted, "X03 completed points");
            Check(CrewExperienceRegistry.PointsForOutcome(CrewAgentRegistry.OutcomeCancelled)
                == CrewExperienceRegistry.PointsOther, "X03 cancelled points");
            Check(CrewExperienceRegistry.PointsForOutcome(CrewAgentRegistry.OutcomeExpired)
                == CrewExperienceRegistry.PointsOther, "X03 expired points");
            Check(CrewExperienceRegistry.PointsForOutcome(CrewAgentRegistry.OutcomeVanished)
                == CrewExperienceRegistry.PointsOther, "X03 vanished points");
            Check(CrewExperienceRegistry.PointsForOutcome(CrewAgentRegistry.OutcomeFailed)
                == CrewExperienceRegistry.PointsOther, "X03 failed points");
            Check(CrewExperienceRegistry.PointsForOutcome("MADE_UP") == -1, "X03 unknown outcome refused");
            Check(CrewExperienceRegistry.PointsForOutcome(null) == -1, "X03 null outcome refused");
            Check(CrewExperienceRegistry.PointsForOutcome("") == -1, "X03 empty outcome refused");
            Check(CrewExperienceRegistry.RecordOutcome(Id(13, true), "MADE_UP", 1000) == false, "X03 unknown outcome not accrued");
            Check(CrewExperienceRegistry.RecordOutcome(Id(13, true), null, 1000) == false, "X03 null outcome not accrued");
            Check(CrewExperienceRegistry.Get(Id(13, true)) == null, "X03 no record created for unknown outcome");
            Check(CrewExperienceRegistry.RefusedCount == 2, "X03 refusals counted");

            // ---- X04: per-outcome counters -----------------------------------------
            FreshSetup();
            string c = Id(21, true);
            CrewExperienceRegistry.RecordOutcome(c, CrewAgentRegistry.OutcomeCompleted, 1000);
            CrewExperienceRegistry.RecordOutcome(c, CrewAgentRegistry.OutcomeCompleted, 1001);
            CrewExperienceRegistry.RecordOutcome(c, CrewAgentRegistry.OutcomeCancelled, 1002);
            CrewExperienceRegistry.RecordOutcome(c, CrewAgentRegistry.OutcomeExpired, 1003);
            CrewExperienceRegistry.RecordOutcome(c, CrewAgentRegistry.OutcomeVanished, 1004);
            CrewExperienceRegistry.RecordOutcome(c, CrewAgentRegistry.OutcomeFailed, 1005);
            CrewExperienceRecord r4 = CrewExperienceRegistry.Get(c);
            Check(r4.TasksCompleted == 2, "X04 completed counter");
            Check(r4.TasksCancelled == 1, "X04 cancelled counter");
            Check(r4.TasksExpired == 1, "X04 expired counter");
            Check(r4.TasksVanished == 1, "X04 vanished counter");
            Check(r4.TasksFailed == 1, "X04 failed counter");
            Check(r4.TotalOutcomes == 6, "X04 total outcomes counter");
            Check(r4.ExperiencePoints == 2 * CrewExperienceRegistry.PointsCompleted + 4 * CrewExperienceRegistry.PointsOther,
                "X04 points sum matches counters");
            Check(r4.LastOutcome == CrewAgentRegistry.OutcomeFailed, "X04 last outcome is most recent");
            Check(r4.Level == ExperienceLevels.LevelForXp(r4.ExperiencePoints), "X04 level consistent with xp");
            Check(r4.UpdateCount == 6, "X04 update count");
            Check(CrewExperienceRegistry.Lines().Count == 1, "X04 Lines bounded");
            Check(CrewExperienceRegistry.StatusLines().Count == 1, "X04 StatusLines bounded");

            // ---- X05: level crossing -------------------------------------------------
            FreshSetup();
            string l = Id(31, true);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1000);
            Check(CrewExperienceRegistry.Get(l).Level == 1, "X05 level 1 at start");
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1001);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1002);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1003);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1004);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1005);
            CrewExperienceRecord r5 = CrewExperienceRegistry.Get(l);
            Check(r5.ExperiencePoints == 60, "X05 five completions = 60 xp");
            Check(r5.Level == 2, "X05 crossed into level 2");
            Check(HasLineContaining("level=2"), "X05 level-up visible in diagnostics");
            // Direct record math: 12 completions = 120 xp -> level 3
            // (thresholds[2] = 120; 110 xp stays level 2).
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1006);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1007);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1008);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1009);
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1010);
            Check(CrewExperienceRegistry.Get(l).ExperiencePoints == 110, "X05 eleven completions = 110 xp");
            Check(CrewExperienceRegistry.Get(l).Level == 2, "X05 110 xp still level 2");
            CrewExperienceRegistry.RecordOutcome(l, CrewAgentRegistry.OutcomeCompleted, 1011);
            Check(CrewExperienceRegistry.Get(l).ExperiencePoints == 120, "X05 twelve completions = 120 xp");
            Check(CrewExperienceRegistry.Get(l).Level == 3, "X05 crossed into level 3");

            // ---- X06: registry stability + Remove lifecycle ----------------------------
            FreshSetup();
            string s = Id(41, true);
            CrewExperienceRegistry.RecordOutcome(s, CrewAgentRegistry.OutcomeCompleted, 1000);
            CrewExperienceRecord first = CrewExperienceRegistry.Get(s);
            CrewExperienceRegistry.RecordOutcome(s, CrewAgentRegistry.OutcomeCompleted, 1001);
            Check(CrewExperienceRegistry.Get(s) == first, "X06 same record object across accruals");
            Check(CrewExperienceRegistry.RecordCount == 1, "X06 record created once");
            Check(CrewExperienceRegistry.Remove(s), "X06 remove accepted");
            Check(CrewExperienceRegistry.Get(s) == null, "X06 removed record absent");
            Check(!CrewExperienceRegistry.Remove(s), "X06 double remove refused");
            Check(HasLineContaining("ExperienceRemoved " + s), "X06 removal emitted");
            // Re-accrual after removal creates a fresh record.
            CrewExperienceRegistry.RecordOutcome(s, CrewAgentRegistry.OutcomeCompleted, 1002);
            Check(CrewExperienceRegistry.Get(s) != null && CrewExperienceRegistry.Get(s) != first,
                "X06 fresh record after removal");
            Check(CrewExperienceRegistry.RecordCount == 2, "X06 second creation counted");
            Check(CrewExperienceRegistry.LevelOf(s) == 1, "X06 LevelOf valid id");
            Check(CrewExperienceRegistry.LevelOf(Id(99, true)) == 0, "X06 LevelOf absent id = 0");

            // ---- X07: no cross-agent contamination --------------------------------------
            FreshSetup();
            string x1 = Id(51, true);
            string x2 = Id(52, true);
            CrewExperienceRegistry.RecordOutcome(x1, CrewAgentRegistry.OutcomeCompleted, 1000);
            CrewExperienceRegistry.RecordOutcome(x1, CrewAgentRegistry.OutcomeCompleted, 1001);
            CrewExperienceRegistry.RecordOutcome(x2, CrewAgentRegistry.OutcomeVanished, 1002);
            CrewExperienceRecord rx1 = CrewExperienceRegistry.Get(x1);
            CrewExperienceRecord rx2 = CrewExperienceRegistry.Get(x2);
            Check(rx1 != rx2, "X07 distinct records");
            Check(rx1.TasksCompleted == 2 && rx1.TasksVanished == 0, "X07 agent 1 isolated");
            Check(rx2.TasksCompleted == 0 && rx2.TasksVanished == 1, "X07 agent 2 isolated");
            Check(rx1.AgentId == x1 && rx2.AgentId == x2, "X07 agent ids correct");
            // Bot vs human same pid are different agents.
            Check(Id(7, true) != Id(7, false), "X07 bot/human ids distinct");
            CrewExperienceRegistry.RecordOutcome(Id(7, true), CrewAgentRegistry.OutcomeCompleted, 1003);
            Check(CrewExperienceRegistry.Get(Id(7, false)) == null, "X07 bot accrual did not touch human id");

            // ---- X08: bounded registry ----------------------------------------------------
            FreshSetup();
            for (int pid = 1; pid <= 32; pid++)
            {
                CrewExperienceRegistry.RecordOutcome(Id(pid, true), CrewAgentRegistry.OutcomeCompleted, 1000);
            }
            Check(CrewExperienceRegistry.Count == 32, "X08 cap reached exactly");
            Check(!CrewExperienceRegistry.RecordOutcome(Id(33, true), CrewAgentRegistry.OutcomeCompleted, 1000),
                "X08 accrual refused at cap");
            Check(CrewExperienceRegistry.RefusedCount >= 1, "X08 refusal counted");
            Check(CrewExperienceRegistry.Count == 32, "X08 size unchanged after refusal");
            Check(CrewExperienceRegistry.Remove(Id(5, true)), "X08 slot freed");
            Check(CrewExperienceRegistry.RecordOutcome(Id(33, true), CrewAgentRegistry.OutcomeCompleted, 1001),
                "X08 accrual succeeds after slot freed");
            Check(CrewExperienceRegistry.Count == 32, "X08 size stays bounded");
            // Existing records keep accruing at cap (only NEW records refused).
            Check(CrewExperienceRegistry.RecordOutcome(Id(1, true), CrewAgentRegistry.OutcomeCompleted, 1002),
                "X08 existing record accrues at cap");
            Check(CrewExperienceRegistry.Get(Id(1, true)).TasksCompleted == 2, "X08 accrual applied at cap");

            // ---- X09: no invalid-data ingestion --------------------------------------------
            FreshSetup();
            Check(!CrewExperienceRegistry.RecordOutcome(null, CrewAgentRegistry.OutcomeCompleted, 1000), "X09 null id refused");
            Check(!CrewExperienceRegistry.RecordOutcome("", CrewAgentRegistry.OutcomeCompleted, 1000), "X09 empty id refused");
            Check(!CrewExperienceRegistry.RecordOutcome("garbage", CrewAgentRegistry.OutcomeCompleted, 1000), "X09 non-AGT id refused");
            Check(!CrewExperienceRegistry.RecordOutcome("AGT:", CrewAgentRegistry.OutcomeCompleted, 1000), "X09 bare prefix refused");
            Check(!CrewExperienceRegistry.RecordOutcome(new string('A', 40), CrewAgentRegistry.OutcomeCompleted, 1000), "X09 overlong id refused");
            Check(CrewExperienceRegistry.Count == 0, "X09 nothing ingested");
            Check(CrewExperienceRegistry.Get(null) == null, "X09 null lookup null");
            Check(CrewExperienceRegistry.Get("") == null, "X09 empty lookup null");
            Check(!CrewExperienceRegistry.Remove(null), "X09 remove refuses null");
            Check(!CrewExperienceRegistry.Remove(""), "X09 remove refuses empty");

            // ---- X10: experience never influences scheduling/claims/execution ---------------
            FreshSetup();
            CapBotTask tA = CapBotTask.Create("TEST", "BOT:60", "experience isolation A", 5, 0, 60000, null, null, null);
            CapBotTask tB2 = CapBotTask.Create("TEST", "CAPTAIN", "experience isolation B", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tA);
            TaskRegistry.Register(tB2);
            tA.TryQueue();
            tB2.TryQueue();
            // Heavy experience churn before/during scheduling.
            for (int i = 0; i < 25; i++)
            {
                CrewExperienceRegistry.RecordOutcome(Id(60, true), CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            }
            CrewExperienceRegistry.RecordOutcome(Id(61, true), CrewAgentRegistry.OutcomeCancelled, s_Clock.NowMs);
            int basePriorityA = tA.Priority;
            int granted1 = TaskScheduler.Tick(s_Clock.NowMs);
            Check(granted1 >= 1, "X10 scheduler grants normally");
            Check(TaskScheduler.HasLease(tA.TaskId), "X10 lease taken for owner A");
            Check(ExecutionClaims.TryClaim(tA.TaskId, "EXECUTE", 0, "BOT:60", "T", s_Clock.NowMs)
                == ClaimResult.Granted, "X10 claim granted for owner A");
            Check(tA.TryStart(), "X10 task A running");
            CrewExperienceRegistry.RecordOutcome(Id(62, true), CrewAgentRegistry.OutcomeFailed, s_Clock.NowMs);
            CrewExperienceRegistry.Remove(Id(61, true));
            Advance(1000);
            CapBotTask tC = CapBotTask.Create("TEST", "BOT:60", "experience isolation C", 9, 0, 60000, null, null, null);
            TaskRegistry.Register(tC);
            tC.TryQueue();
            TaskScheduler.Tick(s_Clock.NowMs);
            Check(!TaskScheduler.HasLease(tC.TaskId), "X10 busy owner still not granted (one grant per owner)");
            Check(TaskScheduler.HasLease(tB2.TaskId), "X10 other owner granted independently");
            Check(tA.Priority == basePriorityA, "X10 priority untouched by experience churn");
            Check(tA.State == TaskState.Running, "X10 task state untouched");
            // Personality records are equally untouched by experience accrual.
            string pA = Id(60, true);
            CrewPersonalityRegistry.DeriveFor(pA, s_Clock.NowMs);
            CrewPersonality before = CrewPersonalityRegistry.Get(pA);
            CrewExperienceRegistry.RecordOutcome(pA, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            Check(CrewPersonalityRegistry.Get(pA) == before, "X10 personality record untouched by accrual");

            // ---- X11: accrual is fail-safe (funnel unaffected by layer faults) ---------------
            FreshSetup();
            string f = Id(71, true);
            s_Snap = Snap(s_Clock.NowMs, Member(71, true, 1, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            Check(CrewAgentRegistry.GetAgent(f) != null, "X11 agent created");
            CapBotTask tF = CapBotTask.Create("TEST", "BOT:71", "fail-safe funnel", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tF);
            tF.TryQueue();
            tF.TryStart();
            tF.TryComplete();
            CrewAgentRegistry.AssignTask(f, tF.TaskId, "TEST", null, s_Clock.NowMs);
            // Force the experience registry to throw on every accrual.
            CrewExperienceRegistry.ResetForTests();
            CrewExperienceRegistry.SetDecisionListener(delegate (string line)
            {
                if (line.StartsWith("ExperienceRecorded", StringComparison.Ordinal)) throw new InvalidOperationException("fault injection");
            });
            Advance(1000);
            int mutations = CrewAgentRegistry.Sync(s_Clock.NowMs);
            CrewAgent agentF = CrewAgentRegistry.GetAgent(f);
            Check(agentF != null, "X11 agent survives experience fault");
            Check(agentF.CurrentTaskId == 0, "X11 task still resolved (funnel unaffected)");
            Check(agentF.LastTaskOutcome == CrewAgentRegistry.OutcomeCompleted, "X11 outcome still recorded on agent");
            // Registry-level: unknown id shape can't throw, but a full cap must
            // also not break the funnel — refuse quietly.
            CrewExperienceRegistry.ResetForTests();
            CrewExperienceRegistry.SetDecisionListener(null);
            for (int pid = 1; pid <= 32; pid++)
            {
                CrewExperienceRegistry.RecordOutcome(Id(1000 + pid, true), CrewAgentRegistry.OutcomeCompleted, 1000);
            }
            CapBotTask tF2 = CapBotTask.Create("TEST", "BOT:71", "fail-safe funnel 2", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tF2);
            tF2.TryQueue();
            tF2.TryStart();
            tF2.TryComplete();
            CrewAgentRegistry.AssignTask(f, tF2.TaskId, "TEST", null, s_Clock.NowMs);
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            agentF = CrewAgentRegistry.GetAgent(f);
            Check(agentF.LastTaskOutcome == CrewAgentRegistry.OutcomeCompleted, "X11 full-registry refusal does not break funnel");

            // ---- X12: ClearTask funnel regression (P10 semantics unchanged) -------------------
            FreshSetup();
            string g = Id(81, true);
            s_Snap = Snap(s_Clock.NowMs, Member(81, true, 2, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            // ClearTask on unknown agent refused.
            Check(!CrewAgentRegistry.ClearTask(Id(99, true), CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "X12 unknown agent refused");
            // ClearTask with no assignment refused.
            Check(!CrewAgentRegistry.ClearTask(g, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "X12 no-assignment refused");
            // Assign + clear path: agent cleared AND experience accrued exactly once.
            CapBotTask tG = CapBotTask.Create("TEST", "BOT:81", "funnel regression", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tG);
            tG.TryQueue();
            tG.TryStart();
            tG.TryComplete();
            CrewAgentRegistry.AssignTask(g, tG.TaskId, "TEST", null, s_Clock.NowMs);
            Check(CrewAgentRegistry.ClearTask(g, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "X12 explicit clear accepted");
            CrewAgent agentG = CrewAgentRegistry.GetAgent(g);
            Check(agentG.CurrentTaskId == 0 && agentG.LastTaskOutcome == CrewAgentRegistry.OutcomeCompleted,
                "X12 agent state cleared");
            Check(CrewExperienceRegistry.Get(g) != null && CrewExperienceRegistry.Get(g).TasksCompleted == 1,
                "X12 experience accrued exactly once");
            // Second clear (no assignment) does NOT double-accrue.
            Check(!CrewAgentRegistry.ClearTask(g, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "X12 double clear refused");
            Check(CrewExperienceRegistry.Get(g).TasksCompleted == 1, "X12 no double accrual");
            // AssignTask on unknown agent refused (P10 behavior intact).
            Check(!CrewAgentRegistry.AssignTask(Id(98, true), 5, "TEST", null, s_Clock.NowMs),
                "X12 unknown-agent assignment refused");

            Console.WriteLine("SUITE ExperienceTests passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}