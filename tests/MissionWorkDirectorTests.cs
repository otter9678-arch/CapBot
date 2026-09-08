// Phase 23: MissionWorkDirector domain tests. NOT part of the shipped mod:
// compiled separately by tests\run_tests.ps1 against the pure planning domain
// (MissionWorkDirector) plus the domains it builds on (P22 PlanningDirector,
// P9/P14/P17 calm-gate readbacks, task registry). Time is virtual: every
// timestamp is an explicit nowMs argument — no real clock reads, no sleeping.
//
// Covers the Phase 23 mandated scenarios:
//   MW01 end-to-end authoring: P22 opens -> dwell -> MISSION_WORK task
//        authored (full task-shape + metadata assertions) + live-task
//        suppression
//   MW02 trigger-surface gate: P22 episode NOT opened => no authoring,
//        ever (record-tracked-but-unopened and record-absent both refuse)
//   MW03 dwell gate: authoring only after AuthoringDwellMs
//   MW04 calm gate: P9 / P14 / P17 / hostiles / warp each block; hostile
//        or warp snapshots from the start keep P22 silent => P23 silent
//   MW05 capacity gate: registry at the live cap blocks; freed => authors
//   MW06 anti-churn: per-record budget cap (3) + requeue block re-arm
//   MW07 P19 screening of the MISSION_WORK family via DecisionValidator
//        (clean pass / stale premise: sector / stale premise: warp /
//        argument mismatch — the additive family edit verified end-to-end)
//   MW08 fail-safe inputs (null/stale/not-started/future) + cadence gate
//   MW09 authority deny-by-default (null/faulting/non-master = no-op)
//   MW10 reconcile (non-terminal/terminal/vanished) + decay + re-open with
//        budget reset + Lines/StatusLines determinism + post-reset inert
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Planning;
using CapBot.Core.Combat;
using CapBot.Core.Missions;
using CapBot.Core.Navigation;
using CapBot.Core.Emergency;
using CapBot.Core.Capabilities;
using CapBot.Core.Validation;

namespace CapBot.TaskTests
{
    internal static class MissionWorkDirectorTests
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
            // Reset the director-under-test FIRST, then the layers it reads
            // (P22 planning director + P9/P14/P17 readbacks + registry).
            MissionWorkDirector.ResetForTests();
            PlanningDirector.ResetForTests();
            CombatDirector.ResetForTests();
            MissionDirector.ResetForTests();
            NavigationRecoveryDirector.ResetForTests();
            EmergencyDirector.ResetForTests();
            TaskRegistry.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 300000 };
            s_Snap = null;
            s_Lines.Clear();
            MissionWorkDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            MissionWorkDirector.SetWorldProvider(delegate { return s_Snap; });
            MissionWorkDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            MissionWorkDirector.SetAuthorityProbe(delegate { return true; });
            PlanningDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            PlanningDirector.SetWorldProvider(delegate { return s_Snap; });
            PlanningDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            PlanningDirector.SetAuthorityProbe(delegate { return true; });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int EvalPlan() { return PlanningDirector.Evaluate(s_Clock.NowMs); }
        private static int EvalWork() { return MissionWorkDirector.Evaluate(s_Clock.NowMs); }

        // One real-driver tick (mirrors Patch.cs order: planning block BEFORE
        // the mission-work block): advance, publish fresh, plan eval, work
        // eval. Returns the mission-work director's report count.
        private static int TickDriver(int advanceMs)
        {
            Advance(advanceMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            EvalPlan();
            return EvalWork();
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

        private static WorldSnapshot WarpWithMission(int timeMs)
        {
            List<MissionSnapshot> missions = new List<MissionSnapshot>();
            missions.Add(new MissionSnapshot(3, false, false, 4, 1, "fix the reactor"));
            return CalmSnap(timeMs, missions, true, 5);
        }

        private static WorldSnapshot ThreatenedWithMission(int timeMs)
        {
            List<MissionSnapshot> missions = new List<MissionSnapshot>();
            missions.Add(new MissionSnapshot(3, false, false, 4, 1, "fix the reactor"));
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 0.9f, 0.5f, false, 0, -1, 0, 10f, false));
            ships.Add(new ShipSnapshot(9, "hostile9", false, 1, true, 0.8f, 0.4f, false, 0, -1, 0, 10f, false));
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot", true, true, "Bridge"));
            crew.Add(Crew(2, "bot1", true, false, "Bridge"));
            List<int> hostileIds = new List<int>();
            hostileIds.Add(9);
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, missions,
                new ThreatSnapshot(hostileIds, 0, 0, 0, -1, 12f, 10f, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
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

        private static WorldSnapshot CloneWithWarp(WorldSnapshot s, int timeMs, bool inWarp)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            foreach (ShipSnapshot ship in s.Ships) ships.Add(ship);
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            foreach (CrewMemberSnapshot c in s.Crew) crew.Add(c);
            List<MissionSnapshot> missions = new List<MissionSnapshot>();
            foreach (MissionSnapshot m in s.Missions) missions.Add(m);
            List<WorldObjectSnapshot> objs = new List<WorldObjectSnapshot>();
            foreach (WorldObjectSnapshot o in s.WorldObjects) objs.Add(o);
            NavigationSnapshot nav = new NavigationSnapshot(
                s.Navigation != null ? s.Navigation.CurrentSectorId : -1,
                s.Navigation != null ? s.Navigation.CurrentSectorName : null,
                s.Navigation != null ? s.Navigation.SectorVisualIndication : -1,
                inWarp, 7, new List<int>(), true, 50f, float.NaN, 0f, false);
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

        // ---- calm-gate injections (REAL director passes, P18 test pattern) ----

        private static void InjectEmergency()
        {
            EmergencyDirector.ResetForTests();
            EmergencyDirector.SetAuthorityProbe(delegate { return true; });
            EmergencyDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            EmergencyDirector.SetWorldProvider(delegate { return HullCriticalSnap(); });
            EmergencyDirector.Evaluate(s_Clock.NowMs);
        }

        private static WorldSnapshot HullCriticalSnap()
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 0.05f, 0.1f, false, 0, -1, 1, 10f, true));
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot", true, true, "Bridge"));
            crew.Add(Crew(2, "bot1", true, false, "Bridge"));
            return new WorldSnapshot(
                s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        private static void InjectNavPlan()
        {
            NavigationRecoveryDirector.ResetForTests();
            NavigationRecoveryDirector.SetAuthorityProbe(delegate { return true; });
            NavigationRecoveryDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            NavigationRecoveryDirector.SetWorldProvider(delegate { return CourseLostSnap(); });
            NavigationRecoveryDirector.Evaluate(s_Clock.NowMs);
        }

        private static WorldSnapshot CourseLostSnap()
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 1f, false, 0, -1, 0, 10f, false));
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot", true, true, "Bridge"));
            crew.Add(Crew(2, "bot1", true, false, "Bridge"));
            return new WorldSnapshot(
                s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        private static void InjectCombatRecord()
        {
            CombatDirector.ResetForTests();
            CombatDirector.SetAuthorityProbe(delegate { return true; });
            CombatDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CombatDirector.SetWorldProvider(delegate { return HostileSnap(); });
            CombatDirector.Evaluate(s_Clock.NowMs);
        }

        private static WorldSnapshot HostileSnap()
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 0.9f, 0.5f, false, 0, -1, 0, 10f, false));
            ships.Add(new ShipSnapshot(9, "hostile9", false, 1, true, 0.8f, 0.4f, false, 0, -1, 0, 10f, false));
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot", true, true, "Bridge"));
            crew.Add(Crew(2, "bot1", true, false, "Bridge"));
            List<int> hostileIds = new List<int>();
            hostileIds.Add(9);
            return new WorldSnapshot(
                s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(hostileIds, 0, 0, 0, -1, 12f, 10f, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // Opens the P22 MISSIONWORK episode end-to-end (REAL P22 passes: arm
        // pass below dwell, open pass at dwell) then arms the P23 episode
        // (one mission-work pass, below its own dwell). Leaves the harness
        // ready for a TickDriver that satisfies AuthoringDwellMs.
        private static void OpenPlanningEpisode()
        {
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Check(EvalPlan() == 0, "P22 arm pass quiet (below dwell)");
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalPlan() == 1, "P22 episode opened");
            Check(HasLineContaining("PlanningIntentOpened PLAN:MISSIONWORK mission=3 objectives=1/4"), "P22 opened line");
            Check(EvalWork() == 0, "MW arm pass quiet (below dwell)");
        }

        private static CapBotTask FindWorkTask()
        {
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            for (int i = 0; i < live.Count; i++)
            {
                if (live[i].TaskType == MissionWorkDirector.TaskTypeMissionWork) return live[i];
            }
            return null;
        }

        internal static int Run()
        {
            // ---- MW01: end-to-end authoring + task shape + suppression --------
            FreshSetup();
            OpenPlanningEpisode();
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 1, "MW01 task authored at dwell");
            Check(HasLineContaining("MoveOrderAuthored #"), "MW01 authored line");
            Check(HasLineContaining("MWORK:MISSIONWORK sector=5 mission=3 objectives=1/4"), "MW01 authored line detail");
            Check(MissionWorkDirector.AuthoringsIssuedCount == 1, "MW01 authoring counted");
            CapBotTask t = FindWorkTask();
            Check(t != null, "MW01 MISSION_WORK task live in registry");
            Check(t.TaskType == "MISSION_WORK" && t.OwnerActorId == "CAPTAIN", "MW01 type + owner");
            Check(t.Priority == MissionWorkDirector.WorkPriority && t.Priority == 4, "MW01 priority 4 (below P18's 8)");
            Check(t.MaxRetries == 1 && t.TimeoutMs == MissionWorkDirector.WorkTaskTimeoutMs, "MW01 one retry + 60s timeout");
            Check(t.TargetKind == "SECTOR" && t.TargetId == "5", "MW01 SECTOR target = current sector");
            Check(t.Dependencies.Count == 0 && t.State == TaskState.Queued, "MW01 queued, no deps");
            Check(t.GetMetadata("CapabilityId") == RegisteredCapabilities.IssueMoveOrder, "MW01 CapabilityId=ISSUE_MOVE_ORDER");
            Check(t.GetMetadata("Argument") == "5", "MW01 Argument == TargetId (P19 argument screen)");
            Check(t.GetMetadata("Preemptible") == "true", "MW01 Preemptible");
            Check(t.GetMetadata("MissionTypeId") == "3", "MW01 mission id carried as data");
            Check(t.GetMetadata("WorkId") == "MWORK:MISSIONWORK" && t.GetMetadata("WorkKind") == "MISSIONWORK", "MW01 track metadata");
            Check(t.Metadata.Count == 6, "MW01 exactly the six metadata entries");
            MissionWorkDirector.MissionWorkIntent rec = MissionWorkDirector.GetIntent("MWORK:MISSIONWORK");
            Check(rec != null && rec.TaskId == t.TaskId && rec.HasLiveTask, "MW01 record holds live task");
            // Live-task suppression: no re-author while the task is live.
            Check(TickDriver(MissionWorkDirector.MinRecheckMs) == 0, "MW01 live task suppresses re-authoring");
            Check(MissionWorkDirector.DuplicatesSuppressedCount >= 1, "MW01 duplicate suppressed counted");
            Check(FindWorkTask() != null && FindWorkTask().TaskId == t.TaskId, "MW01 same task, no churn");

            // ---- MW02: trigger-surface gate (P22 not opened => no authoring) --
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            EvalPlan(); // P22 arms (below ITS dwell — record tracked, NOT opened)
            EvalWork(); // MW arms (below ITS dwell)
            Advance(MissionWorkDirector.AuthoringDwellMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            // NO plan eval: P22's record exists but OpenedReported is false.
            Check(EvalWork() == 0, "MW02 unopened P22 record refuses authoring");
            Check(MissionWorkDirector.GetIntent("MWORK:MISSIONWORK") != null, "MW02 record still tracked");
            Check(PlanningDirector.OpenedReportCount == 0, "MW02 P22 never opened");
            Check(MissionWorkDirector.AuthoringsIssuedCount == 0 && TaskRegistry.LiveCount == 0, "MW02 nothing authored");
            // Record absent entirely (P22 reset): same refusal.
            PlanningDirector.ResetForTests();
            PlanningDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            PlanningDirector.SetWorldProvider(delegate { return s_Snap; });
            PlanningDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            PlanningDirector.SetAuthorityProbe(delegate { return true; });
            Advance(MissionWorkDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalWork() == 0, "MW02 absent P22 record refuses authoring");
            Check(MissionWorkDirector.AuthoringsIssuedCount == 0 && TaskRegistry.LiveCount == 0, "MW02 still nothing authored");

            // ---- MW03: dwell gate ---------------------------------------------
            FreshSetup();
            OpenPlanningEpisode();
            Advance(10000); // below AuthoringDwellMs (15 s)
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            EvalPlan();
            Check(EvalWork() == 0, "MW03 below-dwell refuses authoring");
            Check(TaskRegistry.LiveCount == 0, "MW03 no task below dwell");
            Check(TickDriver(MissionWorkDirector.MinRecheckMs) == 1, "MW03 authors at dwell");
            Check(TaskRegistry.LiveCount == 1, "MW03 task live");

            // ---- MW04: calm gate — P9 / P14 / P17 / hostiles / warp ------------
            FreshSetup();
            OpenPlanningEpisode();
            InjectEmergency();
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 0, "MW04 P9 emergency blocks authoring");
            Check(MissionWorkDirector.CalmGateBlockCount >= 1, "MW04 P9 block counted");
            Check(MissionWorkDirector.AuthoringsIssuedCount == 0, "MW04 P9: nothing authored");
            FreshSetup();
            OpenPlanningEpisode();
            InjectNavPlan();
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 0, "MW04 P14 plan blocks authoring");
            Check(MissionWorkDirector.CalmGateBlockCount >= 1, "MW04 P14 block counted");
            FreshSetup();
            OpenPlanningEpisode();
            InjectCombatRecord();
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 0, "MW04 P17 record blocks authoring");
            Check(MissionWorkDirector.CalmGateBlockCount >= 1, "MW04 P17 block counted");
            // Hostiles appear AFTER the episode opened (P22 already open).
            FreshSetup();
            OpenPlanningEpisode();
            Publish(ThreatenedWithMission(s_Clock.NowMs));
            Advance(MissionWorkDirector.AuthoringDwellMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            EvalPlan();
            Check(EvalWork() == 0, "MW04 snapshot hostiles block authoring");
            Check(MissionWorkDirector.CalmGateBlockCount >= 1, "MW04 hostiles block counted");
            // Warp starts AFTER the episode opened.
            FreshSetup();
            OpenPlanningEpisode();
            Publish(WarpWithMission(s_Clock.NowMs));
            Advance(MissionWorkDirector.AuthoringDwellMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            EvalPlan();
            Check(EvalWork() == 0, "MW04 warp blocks authoring");
            Check(MissionWorkDirector.CalmGateBlockCount >= 1, "MW04 warp block counted");
            // Hostiles from the START: P22's own calm gate never opens the
            // episode => the P23 trigger surface never arms => silent.
            FreshSetup();
            Publish(ThreatenedWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            EvalPlan();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            EvalPlan();
            Check(PlanningDirector.OpenedReportCount == 0, "MW04 P22 stays silent under hostiles");
            Advance(MissionWorkDirector.AuthoringDwellMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            EvalPlan();
            Check(EvalWork() == 0, "MW04 hostile-from-start: nothing authored end-to-end");
            Check(MissionWorkDirector.AuthoringsIssuedCount == 0 && TaskRegistry.LiveCount == 0, "MW04 hostile-from-start: registry untouched");

            // ---- MW05: capacity gate -------------------------------------------
            FreshSetup();
            OpenPlanningEpisode();
            for (int n = 0; n < TaskRegistry.MaxLiveTasks; n++)
            {
                CapBotTask fill = CapBotTask.Create("MW_FILLER", "HOST", "capacity fill", 1, 0, 0, null, null, null);
                TaskRegistry.Register(fill);
            }
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 0, "MW05 capacity gate blocks authoring");
            Check(MissionWorkDirector.CapacityGateBlockCount >= 1, "MW05 capacity block counted");
            Check(MissionWorkDirector.AuthoringsIssuedCount == 0, "MW05 nothing authored at cap");
            TaskRegistry.ResetForTests();
            Check(TickDriver(MissionWorkDirector.MinRecheckMs) == 1, "MW05 authors after capacity frees");
            Check(TaskRegistry.LiveCount == 1, "MW05 single work task live");

            // ---- MW06: anti-churn — budget cap + requeue re-arm -----------------
            FreshSetup();
            OpenPlanningEpisode();
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 1, "MW06 authoring #1");
            long a1 = MissionWorkDirector.GetIntent("MWORK:MISSIONWORK").TaskId;
            CapBotTask t1 = FindWorkTask();
            Check(t1 != null && t1.TryCancel("test harness"), "MW06 cancel #1 (Queued -> Cancelled)");
            MissionWorkDirector.ReconcileTasks(s_Clock.NowMs);
            MissionWorkDirector.MissionWorkIntent r6 = MissionWorkDirector.GetIntent("MWORK:MISSIONWORK");
            Check(r6.TaskResolvedMs >= 0 && !r6.HasLiveTask, "MW06 reconcile stamped resolution");
            Check(TickDriver(MissionWorkDirector.AuthoringRequeueBlockMs) == 1, "MW06 re-arm authors #2 after requeue block");
            long a2 = MissionWorkDirector.GetIntent("MWORK:MISSIONWORK").TaskId;
            Check(a2 != a1, "MW06 second task is a fresh task");
            FindWorkTask().TryCancel("test harness");
            MissionWorkDirector.ReconcileTasks(s_Clock.NowMs);
            Check(TickDriver(MissionWorkDirector.AuthoringRequeueBlockMs) == 1, "MW06 authoring #3");
            long a3 = MissionWorkDirector.GetIntent("MWORK:MISSIONWORK").TaskId;
            Check(a3 != a2, "MW06 third task is a fresh task");
            FindWorkTask().TryCancel("test harness");
            MissionWorkDirector.ReconcileTasks(s_Clock.NowMs);
            Check(TickDriver(MissionWorkDirector.AuthoringRequeueBlockMs) == 0, "MW06 budget cap blocks authoring #4");
            Check(MissionWorkDirector.AuthoringCappedCount >= 1, "MW06 capped counted");
            Check(MissionWorkDirector.AuthoringsIssuedCount == 3, "MW06 exactly 3 authorings per record");
            Check(FindWorkTask() == null, "MW06 no live MISSION_WORK after cap");

            // ---- MW07: P19 screening of the MISSION_WORK family (DV end-to-end)
            // (a) clean pass at the authoring premise (sector 5, no warp).
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            DecisionValidator.ResetForTests();
            DecisionValidator.SetAuthorityProbe(delegate { return true; });
            DecisionValidator.SetWorldProvider(delegate { return s_Snap; });
            DecisionValidator.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CapBotTask dv = CapBotTask.Create("MISSION_WORK", "CAPTAIN", "dv screen test", 4, 1, 60000, "SECTOR", "5", null);
            dv.SetMetadata("CapabilityId", RegisteredCapabilities.IssueMoveOrder);
            dv.SetMetadata("Argument", "5");
            TaskRegistry.Register(dv);
            Check(dv.TryQueue(), "MW07 dv task queued");
            Advance(DecisionValidator.MinRecheckMs);
            DecisionValidator.Evaluate(s_Clock.NowMs);
            Check(DecisionValidator.GetRejections() == 0, "MW07 clean pass at current sector");
            Check(DecisionValidator.GetValidations() == 1, "MW07 task screened");
            // (b) stale premise: sector changed.
            Advance(1000);
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 8));
            DecisionValidator.Evaluate(s_Clock.NowMs);
            Check(HasLineContaining("DecisionRejected #") && HasLineContaining("type=MISSION_WORK")
                && HasLineContaining("reason=stale premise: sector changed"), "MW07 stale-sector rejection");
            Check(DecisionValidator.GetStalePremiseRejections() == 1, "MW07 stale-sector counted");
            // (c) stale premise: warp started (same sector).
            Advance(1000);
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 5));
            DecisionValidator.Evaluate(s_Clock.NowMs);
            Advance(1000);
            Publish(CloneWithWarp(s_Snap, s_Clock.NowMs, true));
            DecisionValidator.Evaluate(s_Clock.NowMs);
            Check(HasLineContaining("reason=stale premise: in warp"), "MW07 in-warp rejection");
            Check(DecisionValidator.GetStalePremiseRejections() == 2, "MW07 in-warp counted");
            // (d) argument mismatch (family-independent screen). Publish calm
            // (no warp) first: the stale-premise screen runs BEFORE the
            // argument screen and returns the first verdict, so a warp=true
            // snapshot would preempt the argument-mismatch rejection.
            Advance(1000);
            Publish(CloneWithWarp(s_Snap, s_Clock.NowMs, false));
            CapBotTask bad = CapBotTask.Create("MISSION_WORK", "CAPTAIN", "dv arg test", 4, 1, 60000, "SECTOR", "5", null);
            bad.SetMetadata("CapabilityId", RegisteredCapabilities.IssueMoveOrder);
            bad.SetMetadata("Argument", "7");
            TaskRegistry.Register(bad);
            bad.TryQueue();
            DecisionValidator.Evaluate(s_Clock.NowMs);
            Check(HasLineContaining("reason=argument mismatch"), "MW07 argument-mismatch rejection");
            Check(DecisionValidator.GetShapeRejections() == 1, "MW07 shape-class rejection counted");

            // ---- MW08: fail-safe inputs + cadence -------------------------------
            FreshSetup();
            s_Snap = null;
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW08 null snapshot");
            Check(HasLineContaining("MissionWorkUncertain no world snapshot"), "MW08 null line");
            s_Snap = FreshCalm(s_Clock.NowMs - MissionWorkDirector.MaxStaleSnapshotMs - 1);
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW08 stale snapshot");
            Check(MissionWorkDirector.StaleRejectionCount == 1, "MW08 stale counted");
            s_Snap = NotStartedSnap(s_Clock.NowMs);
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW08 not-started snapshot");
            s_Snap = FreshCalm(s_Clock.NowMs + 60000);
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW08 future snapshot");
            Check(MissionWorkDirector.StaleRejectionCount == 2, "MW08 future counted as stale");
            Check(MissionWorkDirector.EvaluationCount == 0, "MW08 fail-safe passes are not full evaluations");
            s_Snap = FreshCalm(s_Clock.NowMs);
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW08 valid quiet pass");
            Check(MissionWorkDirector.EvaluationCount == 1, "MW08 evaluation counted once");
            Check(EvalWork() == 0 && MissionWorkDirector.EvaluationCount == 1, "MW08 cadence gate (immediate second eval)");

            // ---- MW09: authority deny-by-default --------------------------------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            MissionWorkDirector.SetAuthorityProbe((Func<bool>)null);
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW09 null probe no-op");
            MissionWorkDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("probe fault"); });
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW09 faulting probe no-op");
            MissionWorkDirector.SetAuthorityProbe(delegate { return false; });
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW09 non-authoritative no-op");
            Check(MissionWorkDirector.EvaluationCount == 0, "MW09 nothing evaluated");
            MissionWorkDirector.SetAuthorityProbe(delegate { return true; });
            Advance(MissionWorkDirector.MinRecheckMs);
            Check(EvalWork() == 0, "MW09 authoritative evaluates (quiet pass)");
            Check(MissionWorkDirector.EvaluationCount == 1, "MW09 evaluation counted");

            // ---- MW10: reconcile + decay + re-open + determinism + reset --------
            FreshSetup();
            OpenPlanningEpisode();
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 1, "MW10 authored for reconcile");
            MissionWorkDirector.ReconcileTasks(s_Clock.NowMs);
            MissionWorkDirector.MissionWorkIntent r10 = MissionWorkDirector.GetIntent("MWORK:MISSIONWORK");
            Check(r10.TaskResolvedMs < 0 && r10.HasLiveTask, "MW10 live Queued task is not resolved");
            Check(FindWorkTask().TryCancel("test harness"), "MW10 cancel for terminal reconcile");
            MissionWorkDirector.ReconcileTasks(s_Clock.NowMs);
            Check(r10.TaskResolvedMs >= 0 && !r10.HasLiveTask, "MW10 terminal task stamped resolved");
            // Vanish path: registry wiped under a live record.
            FreshSetup();
            OpenPlanningEpisode();
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 1, "MW10 authored for vanish");
            TaskRegistry.ResetForTests();
            MissionWorkDirector.ReconcileTasks(s_Clock.NowMs);
            MissionWorkDirector.MissionWorkIntent rv = MissionWorkDirector.GetIntent("MWORK:MISSIONWORK");
            Check(rv.TaskResolvedMs >= 0 && !rv.HasLiveTask, "MW10 vanished task stamped resolved");
            // Decay: mission gone past ActiveExpiryMs => record expires.
            Advance(MissionWorkDirector.ActiveExpiryMs + MissionWorkDirector.MinRecheckMs);
            Publish(FreshCalm(s_Clock.NowMs));
            EvalPlan();
            Check(EvalWork() >= 1, "MW10 expiry reported");
            Check(HasLineContaining("MissionWorkIntentExpired MWORK:MISSIONWORK"), "MW10 expired line");
            Check(MissionWorkDirector.ActiveIntentCount == 0 && MissionWorkDirector.HistoryCount == 1, "MW10 record decayed to history");
            // Re-open with a FRESH record: the authoring budget resets. Two
            // real P22 passes (P22 PD05 discipline: arm below dwell, open at
            // dwell).
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            EvalPlan();
            EvalWork(); // MW episode re-arms (below its own dwell)
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            EvalPlan();
            Check(PlanningDirector.OpenedReportCount == 2, "MW10 P22 re-opened");
            Check(TickDriver(MissionWorkDirector.AuthoringDwellMs) == 1, "MW10 authors with fresh budget");
            MissionWorkDirector.MissionWorkIntent r10b = MissionWorkDirector.GetIntent("MWORK:MISSIONWORK");
            Check(r10b != null && r10b.AuthoringsIssued == 1 && r10b.FirstSeenMs > r10.FirstSeenMs, "MW10 fresh record, budget reset");
            // Readback shapes.
            List<string> lines = MissionWorkDirector.Lines();
            Check(lines.Count == 1 && lines[0].StartsWith("intent MWORK:MISSIONWORK", StringComparison.Ordinal), "MW10 Lines shape");
            List<string> status = MissionWorkDirector.StatusLines();
            Check(status.Count == 2 && status[0].StartsWith("missionwork=", StringComparison.Ordinal)
                && status[1].StartsWith("capped=", StringComparison.Ordinal), "MW10 StatusLines shape");
            // Same-snapshot determinism: two identical setups => same status.
            FreshSetup();
            OpenPlanningEpisode();
            TickDriver(MissionWorkDirector.AuthoringDwellMs);
            List<string> statusA = MissionWorkDirector.StatusLines();
            FreshSetup();
            OpenPlanningEpisode();
            TickDriver(MissionWorkDirector.AuthoringDwellMs);
            List<string> statusB = MissionWorkDirector.StatusLines();
            Check(statusA.Count == statusB.Count && statusA[0] == statusB[0], "MW10 same-snapshot determinism");
            // Post-reset inert.
            MissionWorkDirector.ResetForTests();
            Advance(MissionWorkDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(EvalWork() == 0, "MW10 post-reset inert (no probe)");
            Check(MissionWorkDirector.ActiveIntentCount == 0 && MissionWorkDirector.GetIntent("MWORK:MISSIONWORK") == null, "MW10 state cleared");

            Console.WriteLine("");
            Console.WriteLine("MissionWorkDirectorTests: passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}