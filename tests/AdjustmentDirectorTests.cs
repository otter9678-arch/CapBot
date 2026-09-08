// Phase 24: AdjustmentDirector domain tests. NOT part of the shipped mod:
// compiled separately by tests\run_tests.ps1 against the pure adjustment
// domain (AdjustmentDirector) plus the domains it reads (P22
// PlanningDirector counters, task registry). Time is virtual: every
// timestamp is an explicit nowMs argument — no real clock reads, no
// sleeping.
//
// Covers the Phase 24 scenarios:
//   ADJ01 churn end-to-end: failed/retried live task => ADJ:CHURN report
//        (recommend-only detail) + persisting-condition silent refresh
//   ADJ02 churn clear + re-fire: rate-limited re-report (RecheckBlocks)
//        then a genuine re-report after the recheck window
//   ADJ03 starve end-to-end: registry saturation + P22 capacity-gate delta
//        (real P22 pass) => ADJ:STARVE; then counter-delta-only path
//   ADJ04 drift end-to-end: real P22 premise-drift passes => ADJ:DRIFT,
//        with the observer's own recheck-block rate limit verified
//   ADJ05 fail-safe inputs (null/stale/not-started/future) + cadence gate
//   ADJ06 authority deny-by-default (null/faulting/non-master = no-op;
//        baseline never armed without authority)
//   ADJ07 first readable pass arms the baseline only (no signal on pass 1)
//   ADJ08 hygiene: cleared condition decays after ActiveExpiryMs; re-fire
//        after decay arms a fresh record and reports immediately
//   ADJ09 determinism (identical scenario re-run => identical diagnostics)
//        + data-only proof (registry + director counters untouched) +
//        GetRecord null-safety + post-reset inert
//   ADJ10 bounded emission + multi-signal pass (churn+starve+drift)
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Planning;
using CapBot.Core.Adjustment;
using CapBot.Core.Missions;

namespace CapBot.TaskTests
{
    internal static class AdjustmentDirectorTests
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
            // Reset the observer FIRST, then the layers it reads.
            AdjustmentDirector.ResetForTests();
            PlanningDirector.ResetForTests();
            MissionDirector.ResetForTests();
            TaskRegistry.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 300000 };
            s_Snap = null;
            s_Lines.Clear();
            AdjustmentDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            AdjustmentDirector.SetWorldProvider(delegate { return s_Snap; });
            AdjustmentDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            AdjustmentDirector.SetAuthorityProbe(delegate { return true; });
            PlanningDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            PlanningDirector.SetWorldProvider(delegate { return s_Snap; });
            PlanningDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            PlanningDirector.SetAuthorityProbe(delegate { return true; });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int EvalAdj() { return AdjustmentDirector.Evaluate(s_Clock.NowMs); }
        private static int EvalPlan() { return PlanningDirector.Evaluate(s_Clock.NowMs); }

        // One real-driver tick (mirrors Patch.cs order: the P22 planning
        // block runs BEFORE the P24 adjustment block): advance, publish
        // fresh, plan eval, adjustment eval.
        private static int TickDriver(int advanceMs)
        {
            Advance(advanceMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            EvalPlan();
            return EvalAdj();
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static CrewMemberSnapshot Crew(int id, string name, bool isBot, bool isCaptain, string tli)
        {
            return new CrewMemberSnapshot(id, name, isBot, isCaptain ? 0 : 1, 0, true, true, 1f, tli, isCaptain, -1);
        }

        // Base calm snapshot: no missions, no hostiles, not in warp, sector 5.
        private static WorldSnapshot CalmSnap(int timeMs, List<MissionSnapshot> missions, bool inWarp, int sectorId)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 1f, false, 0, -1, 0, 10f, false));
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot", true, true, "Bridge"));
            crew.Add(Crew(2, "bot1", true, false, "Bridge"));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, missions,
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(sectorId, "Sector " + sectorId, 0, inWarp, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        private static WorldSnapshot FreshCalm(int timeMs) { return CalmSnap(timeMs, new List<MissionSnapshot>(), false, 5); }

        private static WorldSnapshot CalmWithMission(int timeMs)
        {
            List<MissionSnapshot> missions = new List<MissionSnapshot>();
            missions.Add(new MissionSnapshot(3, false, false, 4, 1, "fix the reactor"));
            return CalmSnap(timeMs, missions, false, 5);
        }

        private static WorldSnapshot CloneWithFreshTime(WorldSnapshot s, int timeMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            foreach (ShipSnapshot ship in s.Ships) ships.Add(ship);
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            foreach (CrewMemberSnapshot c in s.Crew) crew.Add(c);
            List<MissionSnapshot> missions = new List<MissionSnapshot>();
            foreach (MissionSnapshot m in s.Missions) missions.Add(m);
            List<WorldObjectSnapshot> objs = new List<WorldObjectSnapshot>();
            foreach (WorldObjectSnapshot o in s.WorldObjects) objs.Add(o);
            return new WorldSnapshot(
                timeMs, s.GameStarted, s.IsHost, s.CurrentHubId, s.SessionAuthority,
                ships, crew, missions, s.Threats, s.Navigation, s.Resources, objs,
                s.ThreatAuthority, s.NavigationAuthority, s.ResourceAuthority, s.WorldObjectsAuthority,
                s.PlayerShipFireCount, s.PlayerShipReactorTempFraction);
        }

        private static WorldSnapshot CloneWithSector(WorldSnapshot s, int timeMs, int sectorId)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            foreach (ShipSnapshot ship in s.Ships) ships.Add(ship);
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            foreach (CrewMemberSnapshot c in s.Crew) crew.Add(c);
            List<MissionSnapshot> missions = new List<MissionSnapshot>();
            foreach (MissionSnapshot m in s.Missions) missions.Add(m);
            List<WorldObjectSnapshot> objs = new List<WorldObjectSnapshot>();
            foreach (WorldObjectSnapshot o in s.WorldObjects) objs.Add(o);
            NavigationSnapshot nav = new NavigationSnapshot(sectorId, "Sector " + sectorId, 0,
                s.Navigation != null && s.Navigation.InWarp, -1,
                new List<int>(), true, 50f, float.NaN, 0f, false);
            return new WorldSnapshot(
                timeMs, s.GameStarted, s.IsHost, s.CurrentHubId, s.SessionAuthority,
                ships, crew, missions, s.Threats, nav, s.Resources, objs,
                s.ThreatAuthority, s.NavigationAuthority, s.ResourceAuthority, s.WorldObjectsAuthority,
                s.PlayerShipFireCount, s.PlayerShipReactorTempFraction);
        }

        private static WorldSnapshot NotStartedSnap(int nowMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 1f, false, 0, -1, 0, 10f, false));
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot", true, true, "Bridge"));
            crew.Add(Crew(2, "bot1", true, false, "Bridge"));
            return new WorldSnapshot(
                nowMs, false, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // A live churny task: retried once (TryRetry from Failed — the same
        // lifecycle mutators P3 recovery uses; TESTS are allowed, the
        // OBSERVER never is).
        private static CapBotTask MakeChurnyTask()
        {
            CapBotTask t = CapBotTask.Create(
                "ADJ_FILLER", "TEST", "adjustment churn test", 1, 2, 120000,
                "SECTOR", "5", null);
            if (t == null) return null;
            if (!TaskRegistry.Register(t)) return null;
            if (!t.TryQueue()) return null;
            if (!t.TryStart()) return null;
            if (!t.TryFail("synthetic failure")) return null;
            if (!t.TryRetry()) return null; // Failed -> Queued, RetryCount 1
            return t;
        }

        private static CapBotTask MakeFillerTask()
        {
            CapBotTask t = CapBotTask.Create(
                "ADJ_FILLER", "TEST", "adjustment filler", 1, 0, 120000,
                "SECTOR", "5", null);
            if (t == null) return null;
            if (!TaskRegistry.Register(t)) return null;
            if (!t.TryQueue()) return null;
            return t;
        }

        private static void CancelAllLive()
        {
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            for (int i = 0; i < live.Count; i++) live[i].TryCancel("adj test clear");
        }

        private static int CountChurnyLive()
        {
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            int count = 0;
            for (int i = 0; i < live.Count; i++)
            {
                CapBotTask t = live[i];
                if (t.State == TaskState.Failed || t.RetryCount >= AdjustmentDirector.ChurnRetryThreshold) count++;
            }
            return count;
        }

        private static string FindLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return s_Lines[i];
            }
            return null;
        }

        internal static int Run()
        {
            // ---- ADJ01: churn end-to-end + persisting-condition refresh -----
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            Check(EvalAdj() == 0, "ADJ01 baseline pass quiet");
            CapBotTask t1 = MakeChurnyTask();
            Check(t1 != null && t1.RetryCount == 1, "ADJ01 churny task live with 1 retry");
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 1, "ADJ01 churn signal emitted");
            Check(HasLineContaining("AdjustmentSignal ADJ:CHURN #"), "ADJ01 churn line prefix + task id");
            Check(HasLineContaining("retried"), "ADJ01 retried reason in detail");
            Check(HasLineContaining("(recommend-only; no behavior change in Phase 24)"), "ADJ01 recommend-only tag");
            Check(AdjustmentDirector.ReportCount == 1, "ADJ01 report counted");
            AdjustmentDirector.AdjustmentRecord rec = AdjustmentDirector.GetRecord("ADJ:CHURN");
            Check(rec != null && rec.Kind == "CHURN" && rec.ConditionSeen, "ADJ01 record tracked + seen");
            Check(rec.ReportCount == 1 && rec.LastRecommendation != null
                && rec.LastRecommendation.IndexOf("#", StringComparison.Ordinal) >= 0, "ADJ01 record detail");
            Check(AdjustmentDirector.LastRecommendation != null
                && AdjustmentDirector.LastRecommendation.StartsWith("ADJ:CHURN", StringComparison.Ordinal), "ADJ01 LastRecommendation readback");
            // Persisting condition: silent refresh.
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 0, "ADJ01 persisting churn is silent");
            Check(AdjustmentDirector.DuplicatesSuppressedCount >= 1, "ADJ01 duplicate suppressed counted");
            Check(AdjustmentDirector.ReportCount == 1, "ADJ01 no second report while persisting");
            Check(AdjustmentDirector.ActiveRecordCount == 1 && AdjustmentDirector.HistoryCount == 0, "ADJ01 exactly one record, no history yet");
            // Bounded detail: 5 churny tasks => at most 3 ids in the line.
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            EvalAdj(); // baseline
            for (int i = 0; i < 5; i++) MakeChurnyTask();
            Check(CountChurnyLive() == 5, "ADJ01 five churny tasks live");
            TickDriver(AdjustmentDirector.MinRecheckMs);
            string churnLine = FindLineContaining("AdjustmentSignal ADJ:CHURN");
            Check(churnLine != null, "ADJ01 churn line present with 5 tasks");
            int commas = 0;
            for (int i = 0; i < churnLine.Length; i++) { if (churnLine[i] == ',') commas++; }
            Check(commas <= AdjustmentDirector.MaxChurnTasksPerLine - 1, "ADJ01 churn detail bounded to 3 ids");
            Check(churnLine.IndexOf("#:", StringComparison.Ordinal) < 0, "ADJ01 ids well-formed");

            // ---- ADJ02: clear + re-fire rate limit + genuine re-report ------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            EvalAdj(); // baseline
            CapBotTask c1 = MakeChurnyTask();
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 1, "ADJ02 first churn report");
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 0, "ADJ02 persist is silent");
            Check(c1.TryCancel("adj test clear"), "ADJ02 task cancelled (condition clears)");
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 0, "ADJ02 clear pass quiet");
            Check(!AdjustmentDirector.GetRecord("ADJ:CHURN").ConditionSeen, "ADJ02 seen-flag cleared on clear pass");
            // Re-fire within the 20 s recheck block: blocked, silent, but the
            // condition persisted so seen stays true.
            CapBotTask c2 = MakeChurnyTask();
            Check(c2 != null, "ADJ02 re-fire task live");
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 0, "ADJ02 re-fire within window blocked");
            Check(AdjustmentDirector.RecheckBlockCount == 1, "ADJ02 recheck block counted");
            Check(AdjustmentDirector.ReportCount == 1, "ADJ02 no report while blocked");
            Check(AdjustmentDirector.GetRecord("ADJ:CHURN").ConditionSeen, "ADJ02 blocked fire keeps seen flag");
            // Persist while blocked: silent.
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 0, "ADJ02 persisted condition still silent");
            Check(AdjustmentDirector.RecheckBlockCount == 1, "ADJ02 persisting after block does not re-block");
            // Clear, let the recheck window (measured from the last REPORT)
            // elapse, re-fire => genuine re-report.
            CancelAllLive();
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 0, "ADJ02 clear pass 2 quiet");
            Check(TickDriver(AdjustmentDirector.AdjustmentRecheckBlockMs) == 0, "ADJ02 window passes quietly");
            MakeChurnyTask();
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 1, "ADJ02 re-fire past window re-reports");
            Check(AdjustmentDirector.ReportCount == 2, "ADJ02 exactly two churn reports");

            // ---- ADJ03: starve — saturation + P22 capacity delta ------------
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            EvalPlan(); // arm P22 premise (below dwell: no blocks yet)
            Check(EvalAdj() == 0, "ADJ03 baseline pass quiet");
            // Saturation path: fill the registry to the live cap with a REAL
            // P22 pass in the loop (the P22 MISSIONWORK episode stays open
            // and blocks on capacity each pass).
            for (int i = 0; i < TaskRegistry.MaxLiveTasks; i++)
            {
                CapBotTask f = MakeFillerTask();
                if (f == null) break;
            }
            Check(TaskRegistry.LiveCount == TaskRegistry.MaxLiveTasks, "ADJ03 registry saturated at 64");
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 1, "ADJ03 starve signal emitted");
            Check(HasLineContaining("AdjustmentSignal ADJ:STARVE"), "ADJ03 starve line");
            Check(PlanningDirector.CapacityGateBlockCount >= 1, "ADJ03 P22 capacity block really happened (real pass)");
            AdjustmentDirector.AdjustmentRecord sr = AdjustmentDirector.GetRecord("ADJ:STARVE");
            Check(sr != null && sr.LastRecommendation != null
                && sr.LastRecommendation.IndexOf("capacity=64/64", StringComparison.Ordinal) >= 0, "ADJ03 saturated detail");
            // Persisting saturation: silent.
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 0, "ADJ03 persisting saturation silent");
            // Free one slot => condition clears.
            CancelAllLive();
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 0, "ADJ03 freed pass quiet");
            Check(!AdjustmentDirector.GetRecord("ADJ:STARVE").ConditionSeen, "ADJ03 starve seen flag cleared");
            // Counter-delta path (no saturation): a real P22 capacity block at
            // 64 live, then free one slot BEFORE the observer pass => not
            // saturated, pure planCapacity delta.
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            EvalPlan();
            EvalAdj(); // baseline
            for (int i = 0; i < TaskRegistry.MaxLiveTasks; i++)
            {
                CapBotTask f = MakeFillerTask();
                if (f == null) break;
            }
            Check(TaskRegistry.LiveCount == TaskRegistry.MaxLiveTasks, "ADJ03 64 live for P22 block");
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalPlan() == 0, "ADJ03 P22 episode blocked (not opened)");
            Check(PlanningDirector.CapacityGateBlockCount == 1, "ADJ03 P22 block delta = 1");
            List<CapBotTask> liveDelta = TaskRegistry.LiveSnapshot();
            liveDelta[0].TryCancel("adj free delta");
            Check(TaskRegistry.LiveCount == TaskRegistry.MaxLiveTasks - 1, "ADJ03 63 live (not saturated)");
            Check(EvalAdj() == 1, "ADJ03 starve via counter delta");
            AdjustmentDirector.AdjustmentRecord sr2 = AdjustmentDirector.GetRecord("ADJ:STARVE");
            Check(sr2 != null && sr2.LastRecommendation.IndexOf("planCapacity", StringComparison.Ordinal) >= 0, "ADJ03 planCapacity detail");

            // ---- ADJ04: drift — real P22 premise drift ----------------------
            FreshSetup();
            Publish(CloneWithSector(FreshCalm(s_Clock.NowMs), s_Clock.NowMs, 5));
            Check(EvalPlan() == 0, "ADJ04 P22 premise armed (sector 5)");
            Check(EvalAdj() == 0, "ADJ04 baseline pass quiet");
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 7));
            Check(EvalPlan() == 1, "ADJ04 P22 drift #1");
            Check(EvalAdj() == 1, "ADJ04 observer drift signal");
            Check(HasLineContaining("AdjustmentSignal ADJ:DRIFT planningDrift+1"), "ADJ04 drift line + delta");
            // Quiet pass at the re-armed premise (sector 7): clears the
            // observer's seen flag so the next drift is a genuine re-fire.
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 7));
            Check(EvalPlan() == 0, "ADJ04 quiet pass (premise holds)");
            Check(EvalAdj() == 0, "ADJ04 quiet pass quiet");
            // Second P22 drift: P22 reports (its own recheck window has
            // elapsed), but the OBSERVER re-fires within 20 s of its first
            // report => blocked, RecheckBlocks counted.
            Advance(11000);
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 9));
            Check(EvalPlan() == 1, "ADJ04 P22 drift #2");
            Check(EvalAdj() == 0, "ADJ04 observer re-fire inside 20s window blocked");
            Check(AdjustmentDirector.RecheckBlockCount == 1, "ADJ04 recheck block counted");
            Check(AdjustmentDirector.ReportCount == 1, "ADJ04 no second report while blocked");
            // The blocked re-fire set the condition seen; a quiet pass clears
            // it (no drift this pass).
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 9));
            Check(EvalPlan() == 0, "ADJ04 no P22 drift on quiet pass");
            Check(EvalAdj() == 0, "ADJ04 quiet pass 2 quiet");
            // Third P22 drift, past BOTH recheck windows (P22's own 15 s from
            // its drift #2, the observer's 20 s from its report #1):
            // genuine re-report.
            Advance(11000);
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 11));
            Check(EvalPlan() == 1, "ADJ04 P22 drift #3");
            Check(EvalAdj() == 1, "ADJ04 observer re-report past window");
            Check(AdjustmentDirector.ReportCount == 2, "ADJ04 exactly two drift reports");
            AdjustmentDirector.AdjustmentRecord dr = AdjustmentDirector.GetRecord("ADJ:DRIFT");
            Check(dr != null && dr.ReportCount == 2, "ADJ04 record holds both reports");

            // ---- ADJ05: fail-safe inputs + cadence gate ----------------------
            FreshSetup();
            s_Snap = null;
            Check(EvalAdj() == 0, "ADJ05 null snapshot no-op");
            Check(HasLineContaining("AdjustmentUncertain no world snapshot"), "ADJ05 null uncertain line");
            WorldSnapshot stale = FreshCalm(s_Clock.NowMs - AdjustmentDirector.MaxStaleSnapshotMs - 1);
            Publish(stale);
            Advance(AdjustmentDirector.MinRecheckMs);
            Check(EvalAdj() == 0, "ADJ05 stale snapshot no-op");
            Check(HasLineContaining("AdjustmentUncertain world snapshot stale"), "ADJ05 stale uncertain line");
            WorldSnapshot future = FreshCalm(s_Clock.NowMs + 1000);
            Publish(future);
            Advance(AdjustmentDirector.MinRecheckMs);
            Check(EvalAdj() == 0, "ADJ05 future snapshot no-op");
            Publish(NotStartedSnap(s_Clock.NowMs));
            Advance(AdjustmentDirector.MinRecheckMs);
            Check(EvalAdj() == 0, "ADJ05 not-started no-op");
            Check(HasLineContaining("AdjustmentUncertain game not started"), "ADJ05 not-started uncertain line");
            Check(AdjustmentDirector.StaleRejectionCount >= 1, "ADJ05 stale counted");
            // Cadence gate: publish FIRST, then two evaluates within
            // MinRecheckMs => second no-op (first is the baseline arm).
            Publish(FreshCalm(s_Clock.NowMs));
            Check(EvalAdj() == 0, "ADJ05 cadence pass 1 (baseline)");
            Check(EvalAdj() == 0, "ADJ05 cadence pass 2 within window");
            MakeChurnyTask();
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 1, "ADJ05 fires after cadence window");
            // Baseline armed on the first VALID pass; fail-safe passes never
            // armed a bogus baseline (the fire above proves it).

            // ---- ADJ06: authority deny-by-default ---------------------------
            FreshSetup();
            AdjustmentDirector.SetAuthorityProbe(null);
            Publish(FreshCalm(s_Clock.NowMs));
            MakeChurnyTask();
            Check(EvalAdj() == 0, "ADJ06 null probe no-op");
            AdjustmentDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("boom"); });
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 0, "ADJ06 faulting probe no-op");
            AdjustmentDirector.SetAuthorityProbe(delegate { return false; });
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 0, "ADJ06 non-authoritative no-op");
            Check(AdjustmentDirector.ReportCount == 0, "ADJ06 never reported without authority");
            // Baseline was NOT armed: first authorized pass arms only.
            AdjustmentDirector.SetAuthorityProbe(delegate { return true; });
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 0, "ADJ06 first authorized pass arms baseline only");
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 1, "ADJ06 second authorized pass reports churn");

            // ---- ADJ07: baseline arm on first readable pass -----------------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            MakeChurnyTask();
            Check(EvalAdj() == 0, "ADJ07 first readable pass is signal-free");
            Check(AdjustmentDirector.ReportCount == 0 && AdjustmentDirector.ActiveRecordCount == 0, "ADJ07 nothing tracked on pass 1");
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 1, "ADJ07 second pass reports");
            Check(AdjustmentDirector.GetRecord("ADJ:CHURN") != null, "ADJ07 record exists from pass 2");

            // ---- ADJ08: hygiene decay + fresh-record re-arm -----------------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            EvalAdj(); // baseline
            CapBotTask h1 = MakeChurnyTask();
            TickDriver(AdjustmentDirector.MinRecheckMs); // churn report
            Check(AdjustmentDirector.ReportCount == 1, "ADJ08 churn reported");
            Check(h1.TryCancel("adj hygiene clear"), "ADJ08 condition cleared");
            TickDriver(AdjustmentDirector.MinRecheckMs); // clear pass
            Advance(AdjustmentDirector.ActiveExpiryMs + 1000); // past expiry from last seen
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            // The decay pass reports 1 (the AdjustmentRecordExpired one-shot
            // line is itself a report, P15/P22/P23 record semantics).
            Check(EvalAdj() == 1, "ADJ08 decay pass emits expired line");
            Check(AdjustmentDirector.ActiveRecordCount == 0, "ADJ08 record decayed");
            Check(AdjustmentDirector.RecordsExpiredCount == 1, "ADJ08 expiry counted");
            Check(AdjustmentDirector.HistoryCount == 1, "ADJ08 history holds the expired record");
            Check(HasLineContaining("AdjustmentRecordExpired ADJ:CHURN"), "ADJ08 expired line emitted");
            // Re-fire after decay: FRESH record + immediate report (budget
            // re-armed — the record is new, not the rate-limited old one).
            MakeChurnyTask();
            Check(TickDriver(AdjustmentDirector.MinRecheckMs) == 1, "ADJ08 fresh record reports immediately");
            AdjustmentDirector.AdjustmentRecord hr = AdjustmentDirector.GetRecord("ADJ:CHURN");
            Check(hr != null && hr.ReportCount == 1, "ADJ08 fresh record lifetime");

            // ---- ADJ09: determinism + data-only proof -----------------------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            EvalAdj(); // baseline for scenario A
            MakeChurnyTask();
            for (int i = 0; i < 3; i++) MakeFillerTask();
            TickDriver(AdjustmentDirector.MinRecheckMs);
            List<string> linesA = new List<string>(AdjustmentDirector.Lines());
            List<string> statusA = new List<string>(AdjustmentDirector.StatusLines());
            int registryAfterA = TaskRegistry.LiveCount;
            long planDriftAfterA = PlanningDirector.DriftReportCount;
            long planCapAfterA = PlanningDirector.CapacityGateBlockCount;
            // Reset and re-run the IDENTICAL scenario. TaskIds differ across
            // runs, so compare the id-free diagnostics (record Lines +
            // StatusLines); the signal lines carry task ids by design.
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            EvalAdj();
            MakeChurnyTask();
            for (int i = 0; i < 3; i++) MakeFillerTask();
            TickDriver(AdjustmentDirector.MinRecheckMs);
            List<string> linesB = new List<string>(AdjustmentDirector.Lines());
            List<string> statusB = new List<string>(AdjustmentDirector.StatusLines());
            Check(linesA.Count == linesB.Count, "ADJ09 determinism: same record count");
            bool same = linesA.Count == linesB.Count;
            for (int i = 0; same && i < linesA.Count; i++) same = linesA[i] == linesB[i];
            Check(same, "ADJ09 determinism: identical Lines");
            Check(statusA.Count == statusB.Count, "ADJ09 status line count equal");
            Check(statusA[0] == statusB[0], "ADJ09 determinism: identical status summary");
            Check(TaskRegistry.LiveCount == registryAfterA, "ADJ09 data-only: registry size untouched");
            Check(PlanningDirector.DriftReportCount == planDriftAfterA, "ADJ09 data-only: planning drift counter untouched by observer");
            Check(PlanningDirector.CapacityGateBlockCount == planCapAfterA, "ADJ09 data-only: planning capacity counter untouched");
            // GetRecord null-safety.
            Check(AdjustmentDirector.GetRecord(null) == null, "ADJ09 GetRecord(null) null");
            Check(AdjustmentDirector.GetRecord("") == null, "ADJ09 GetRecord(empty) null");
            Check(AdjustmentDirector.GetRecord("ADJ:NOPE") == null, "ADJ09 GetRecord(unknown) null");
            Check(AdjustmentDirector.GetRecord("ADJ:CHURN") != null, "ADJ09 GetRecord(live) returns record");
            // Post-reset inert.
            AdjustmentDirector.ResetForTests();
            AdjustmentDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            AdjustmentDirector.SetWorldProvider(delegate { return s_Snap; });
            AdjustmentDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            AdjustmentDirector.SetAuthorityProbe(delegate { return true; });
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 0, "ADJ09 post-reset inert (baseline arm)");
            Check(AdjustmentDirector.ReportCount == 0, "ADJ09 post-reset no reports");

            // ---- ADJ10: bounded emission + multi-signal pass ----------------
            FreshSetup();
            Publish(CloneWithSector(FreshCalm(s_Clock.NowMs), s_Clock.NowMs, 5));
            Check(EvalPlan() == 0, "ADJ10 P22 premise armed (sector 5)");
            Check(EvalAdj() == 0, "ADJ10 baseline pass quiet");
            // Churn (2 tasks) + saturation + a P22 drift in ONE pass => 3
            // signals, under MaxPendingLines=4.
            MakeChurnyTask();
            MakeChurnyTask();
            for (int i = 0; i < TaskRegistry.MaxLiveTasks - 2; i++)
            {
                CapBotTask f = MakeFillerTask();
                if (f == null) break;
            }
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithSector(CloneWithFreshTime(s_Snap, s_Clock.NowMs), s_Clock.NowMs, 21));
            int before = s_Lines.Count;
            Check(EvalPlan() == 1, "ADJ10 P22 drift fired");
            int reports = EvalAdj();
            Check(reports == 3, "ADJ10 exactly three signals in one pass");
            int emitted = s_Lines.Count - before;
            // 3 signal lines + 1 PlanningPremiseDrift line from the P22 pass.
            Check(emitted == 4, "ADJ10 four lines emitted (3 signals + P22 drift line)");
            Check(AdjustmentDirector.ReportCount == 3, "ADJ10 observer reports = 3");
            Check(HasLineContaining("AdjustmentSignal ADJ:CHURN"), "ADJ10 churn present");
            Check(HasLineContaining("AdjustmentSignal ADJ:STARVE"), "ADJ10 starve present");
            Check(HasLineContaining("AdjustmentSignal ADJ:DRIFT"), "ADJ10 drift present");
            Check(AdjustmentDirector.ActiveRecordCount == 3, "ADJ10 three records tracked");
            // Multi-expire: clear everything, advance past expiry => all
            // records decay in one pass (3 expired one-shot lines).
            CancelAllLive();
            Advance(AdjustmentDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 0, "ADJ10 clear pass quiet");
            Advance(AdjustmentDirector.ActiveExpiryMs + 1000);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalAdj() == 3, "ADJ10 decay pass emits three expired lines");
            Check(AdjustmentDirector.ActiveRecordCount == 0, "ADJ10 all records decayed");
            Check(AdjustmentDirector.RecordsExpiredCount == 3, "ADJ10 three expiries counted");

            Console.WriteLine("AdjustmentDirectorTests: passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}