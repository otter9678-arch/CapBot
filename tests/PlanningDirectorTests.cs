// Phase 22: PlanningDirector domain tests. NOT part of the shipped mod:
// compiled separately by tests\run_tests.ps1 against the pure planning domain
// (PlanningDirector) plus the domains it builds on. Time is virtual: every
// timestamp is an explicit nowMs argument — no real clock reads, no sleeping.
//
// Covers the Phase 22 mandated scenarios:
//   PD01 premise capture + drift end-to-end (arm -> sector drift -> report)
//   PD02 warp-start drift + warp-end non-drift
//   PD03 drift recheck block (anti-churn) + premise re-capture semantics
//   PD04 unknown sentinels never arm/never fire (unknown-input accounting)
//   PD05 MISSIONWORK episode open (dwell + calm + capacity) + refresh quiet
//   PD06 calm gate: P9 emergency / P14 plan / P17 record / hostiles / warp
//   PD07 capacity gate (registry live cap) blocks the open line
//   PD08 fail-safe inputs (null/stale/not-started/future) + cadence gate
//   PD09 authority deny-by-default (null/faulting/non-master = no-op)
//   PD10 hygiene expiry + record decay + StatusLines/Lines/GetIntent
//        determinism + reset determinism
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Planning;
using CapBot.Core.Combat;
using CapBot.Core.Missions;
using CapBot.Core.Navigation;
using CapBot.Core.Emergency;

namespace CapBot.TaskTests
{
    internal static class PlanningDirectorTests
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
            // Reset the director-under-test FIRST, then the downstream layers
            // it reads (P9/P14/P17 readbacks + the task registry LiveCount).
            PlanningDirector.ResetForTests();
            CombatDirector.ResetForTests();
            MissionDirector.ResetForTests();
            NavigationRecoveryDirector.ResetForTests();
            EmergencyDirector.ResetForTests();
            TaskRegistry.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 300000 };
            s_Snap = null;
            s_Lines.Clear();
            PlanningDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            PlanningDirector.SetWorldProvider(delegate { return s_Snap; });
            PlanningDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            PlanningDirector.SetAuthorityProbe(delegate { return true; });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int Eval() { return PlanningDirector.Evaluate(s_Clock.NowMs); }

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

        // Same missions but in warp (calm gate must block).
        private static WorldSnapshot WarpWithMission(int timeMs)
        {
            List<MissionSnapshot> missions = new List<MissionSnapshot>();
            missions.Add(new MissionSnapshot(3, false, false, 4, 1, "fix the reactor"));
            return CalmSnap(timeMs, missions, true, 5);
        }

        // Hostiles + mission (calm gate must block).
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

        // Rebuild a snapshot identical to the given one but with a fresh time.
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

        // A snapshot identical to the given one but with a different sector id.
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

        // A snapshot identical to the given one but with warp started (same sector).
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
            // Drive a REAL P9 evaluation with a hull-critical snapshot so
            // EmergencyDirector.ActiveCount > 0 blocks the calm gate.
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
            // Real P14 plan: drive the P14 director over a course-lost snapshot.
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
            // Real P17 record: drive the combat director over a hostile snapshot.
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

        internal static int Run()
        {
            // ---- PD01: premise capture + sector drift end-to-end ------------------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD01 arm pass is quiet (premise captured)");
            PlanningDirector.PlanningPremise p1 = PlanningDirector.CapturedPremise;
            Check(p1 != null && p1.SectorId == 5 && !p1.InWarp && !p1.IsUnknown, "PD01 premise captured (sector 5)");
            // Same premise: quiet.
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD01 same premise quiet");
            // Sector drift: one report.
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 8));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 1, "PD01 drift reported");
            Check(HasLineContaining("PlanningPremiseDrift PLAN:PREMISE_DRIFT sector 5->8"), "PD01 drift line");
            Check(PlanningDirector.DriftReportCount == 1, "PD01 drift counted");
            p1 = PlanningDirector.CapturedPremise;
            Check(p1 != null && p1.SectorId == 8, "PD01 premise re-captured to sector 8");
            PlanningDirector.PlanningIntent dr = PlanningDirector.GetIntent("PLAN:PREMISE_DRIFT");
            Check(dr != null && dr.DriftReports == 1 && dr.Kind == "PREMISE_DRIFT", "PD01 drift record tracked");
            // Back to sector 5 (after the recheck window): another legitimate
            // drift event.
            Advance(PlanningDirector.DriftRecheckBlockMs);
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 5));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 1, "PD01 reverse drift reported");
            Check(PlanningDirector.DriftReportCount == 2, "PD01 second drift counted");

            // ---- PD02: warp-start drift + warp-end non-drift ----------------------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval(); // arm: sector 5, not in warp
            // Warp starts (same sector): drift fires.
            Publish(CloneWithWarp(s_Snap, s_Clock.NowMs, true));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 1, "PD02 warp-start drift");
            Check(HasLineContaining("sector 5->5 warp=true"), "PD02 warp drift line detail");
            // Warp ends, sector unchanged: NOT drift (arrival resolves the premise).
            s_Lines.Clear();
            Publish(CloneWithWarp(s_Snap, s_Clock.NowMs, false));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD02 warp-end is not drift");
            Check(PlanningDirector.DriftReportCount == 1, "PD02 no extra report");

            // ---- PD03: drift recheck block (anti-churn) ---------------------------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval(); // arm: sector 5
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 8));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 1, "PD03 first drift reported");
            // Another drift INSIDE the recheck window: blocked (report rate-
            // limited) but the premise is re-captured.
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 12));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD03 drift inside window blocked");
            Check(PlanningDirector.DriftRecheckBlockCount == 1, "PD03 block counted");
            Check(PlanningDirector.DriftReportCount == 1, "PD03 no second report");
            PlanningDirector.PlanningPremise p3 = PlanningDirector.CapturedPremise;
            Check(p3 != null && p3.SectorId == 12, "PD03 premise re-captured despite block");
            // After the window: the next drift reports again.
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 15));
            Advance(PlanningDirector.DriftRecheckBlockMs);
            Check(Eval() == 1, "PD03 drift after window reports");
            Check(PlanningDirector.DriftReportCount == 2, "PD03 second report counted");

            // ---- PD04: unknown sentinels never arm / never fire -------------------
            FreshSetup();
            Publish(CalmSnap(s_Clock.NowMs, new List<MissionSnapshot>(), false, -1));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD04 unknown sector never arms");
            Check(PlanningDirector.CapturedPremise == null, "PD04 no premise captured");
            // Known arm, then unknown current data: comparison uncertain.
            Publish(FreshCalm(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval(); // arm at sector 5
            Publish(CalmSnap(s_Clock.NowMs, new List<MissionSnapshot>(), false, -1));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD04 unknown current data is uncertain-quiet");
            Check(PlanningDirector.DriftReportCount == 0, "PD04 no drift from unknown");
            Check(PlanningDirector.UnknownInputPassCount >= 1, "PD04 unknown counted");
            // The premise is NOT re-captured to unknown (stale-safe).
            Check(PlanningDirector.CapturedPremise != null
                && PlanningDirector.CapturedPremise.SectorId == 5, "PD04 premise survives unknown pass");

            // ---- PD05: MISSIONWORK episode open + refresh quiet -------------------
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            // First pass: episode arms but dwell (MinRecheckMs) not yet met.
            Check(Eval() == 0, "PD05 below-dwell quiet");
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(Eval() == 1, "PD05 episode opened");
            Check(HasLineContaining("PlanningIntentOpened PLAN:MISSIONWORK mission=3 objectives=1/4"), "PD05 opened line");
            Check(PlanningDirector.OpenedReportCount == 1, "PD05 opened counted");
            PlanningDirector.PlanningIntent mw = PlanningDirector.GetIntent("PLAN:MISSIONWORK");
            Check(mw != null && mw.Kind == "MISSIONWORK" && mw.OpenedReported, "PD05 record fields");
            // Live refresh: quiet (DuplicatesSuppressed).
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD05 live refresh quiet");
            Check(PlanningDirector.DuplicatesSuppressedCount == 1, "PD05 duplicate suppressed");
            // Mission completes: episode closes, record decays later.
            // Publish-after-advance discipline: advance FIRST so the fresh
            // snapshot stays readable at eval time.
            Advance(PlanningDirector.ActiveExpiryMs + PlanningDirector.MinRecheckMs);
            Publish(FreshCalm(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() >= 1, "PD05 expiry reported");
            Check(PlanningDirector.ActiveIntentCount == 0, "PD05 record decayed");
            Check(PlanningDirector.HistoryCount == 1, "PD05 history holds record");
            // Fresh mission: episode re-opens with a fresh record.
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(Eval() == 1, "PD05 fresh episode re-opens");
            Check(PlanningDirector.OpenedReportCount == 2, "PD05 second open counted");

            // ---- PD06: calm gate — P9 / P14 / P17 / hostiles / warp ---------------
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval(); // episode arms below dwell
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            InjectEmergency();
            Check(Eval() == 0, "PD06 P9 emergency blocks the open");
            Check(PlanningDirector.CalmGateBlockCount >= 1, "PD06 P9 block counted");
            Check(PlanningDirector.OpenedReportCount == 0, "PD06 nothing opened under P9");
            // P14 active plan blocks.
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            InjectNavPlan();
            Check(Eval() == 0, "PD06 P14 plan blocks the open");
            Check(PlanningDirector.CalmGateBlockCount >= 1, "PD06 P14 block counted");
            // P17 combat record blocks.
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            InjectCombatRecord();
            Check(Eval() == 0, "PD06 P17 record blocks the open");
            // Snapshot hostiles block.
            FreshSetup();
            Publish(ThreatenedWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(Eval() == 0, "PD06 snapshot hostiles block the open");
            // Warp blocks.
            FreshSetup();
            Publish(WarpWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(Eval() == 0, "PD06 warp blocks the open");
            // Premise drift still works under a blocked calm gate (rule 1 is
            // independent of rule 2).
            Publish(CloneWithSector(s_Snap, s_Clock.NowMs, 9));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 1, "PD06 drift fires while calm-gate blocked");

            // ---- PD07: capacity gate — registry at the live cap -------------------
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval(); // episode arms below dwell
            // Fill the registry to the live cap with inert tasks.
            for (int n = 0; n < TaskRegistry.MaxLiveTasks; n++)
            {
                CapBotTask fill = CapBotTask.Create("PD_FILLER", "HOST", "capacity fill", 1, 0, 0, null, null, null);
                TaskRegistry.Register(fill);
            }
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(Eval() == 0, "PD07 capacity gate blocks the open");
            Check(PlanningDirector.CapacityGateBlockCount >= 1, "PD07 capacity block counted");
            Check(PlanningDirector.OpenedReportCount == 0, "PD07 nothing opened at cap");
            // Free one slot: the episode opens (dwell satisfied by the arm pass
            // + the elapsed advance).
            TaskRegistry.ResetForTests();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(Eval() == 1, "PD07 opens after capacity frees");
            Check(PlanningDirector.OpenedReportCount == 1, "PD07 opened counted");

            // ---- PD08: fail-safe inputs + cadence ---------------------------------
            FreshSetup();
            s_Snap = null;
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD08 null snapshot");
            Check(HasLineContaining("PlanningUncertain no world snapshot"), "PD08 null line");
            s_Snap = FreshCalm(s_Clock.NowMs - PlanningDirector.MaxStaleSnapshotMs - 1);
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD08 stale snapshot");
            Check(PlanningDirector.StaleRejectionCount == 1, "PD08 stale counted");
            s_Snap = NotStartedSnap(s_Clock.NowMs);
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD08 not-started snapshot");
            s_Snap = FreshCalm(s_Clock.NowMs + 60000);
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD08 future snapshot");
            Check(PlanningDirector.StaleRejectionCount == 2, "PD08 future counted as stale");
            // Cadence gate: immediate second eval is a no-op.
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Check(Eval() == 0, "PD08 cadence gate (immediate second eval)");
            Check(PlanningDirector.EvaluationCount == 1, "PD08 evaluation counted once");

            // ---- PD09: authority deny-by-default ----------------------------------
            FreshSetup();
            Publish(FreshCalm(s_Clock.NowMs));
            PlanningDirector.SetAuthorityProbe((Func<bool>)null);
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD09 null probe no-op");
            PlanningDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("probe fault"); });
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD09 faulting probe no-op");
            PlanningDirector.SetAuthorityProbe(delegate { return false; });
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD09 non-authoritative no-op");
            Check(PlanningDirector.EvaluationCount == 0, "PD09 nothing evaluated");
            PlanningDirector.SetAuthorityProbe(delegate { return true; });
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD09 authoritative evaluates (quiet premise pass)");
            Check(PlanningDirector.EvaluationCount == 1, "PD09 evaluation counted");
            Check(PlanningDirector.CapturedPremise != null, "PD09 premise captured under authority");

            // ---- PD10: determinism + data-only guarantee --------------------------
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Eval();
            Check(PlanningDirector.ActiveIntentCount == 1, "PD10 episode tracked");
            List<string> lines = PlanningDirector.Lines();
            Check(lines.Count == 1 && lines[0].StartsWith("intent PLAN:MISSIONWORK", StringComparison.Ordinal), "PD10 Lines shape");
            List<string> status = PlanningDirector.StatusLines();
            Check(status.Count == 2 && status[0].StartsWith("planning=", StringComparison.Ordinal)
                && status[1].Contains("uncertain="), "PD10 StatusLines shape");
            // DATA ONLY: the director never created a single task.
            Check(TaskRegistry.LiveCount == 0, "PD10 no task authored (data-only contract)");
            Check(TaskRegistry.HistoryCount == 0, "PD10 no task history touched");
            // Reset determinism: post-reset the director is fully inert.
            PlanningDirector.ResetForTests();
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Check(Eval() == 0, "PD10 post-reset inert (no probe)");
            Check(PlanningDirector.CapturedPremise == null && PlanningDirector.ActiveIntentCount == 0, "PD10 state cleared");
            // Same-snapshot determinism: two identical setups produce
            // identical status lines.
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Eval();
            List<string> statusA = PlanningDirector.StatusLines();
            FreshSetup();
            Publish(CalmWithMission(s_Clock.NowMs));
            Advance(PlanningDirector.MinRecheckMs);
            Eval();
            Advance(PlanningDirector.MinRecheckMs);
            Publish(CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Eval();
            List<string> statusB = PlanningDirector.StatusLines();
            Check(statusA.Count == statusB.Count && statusA[0] == statusB[0], "PD10 same-snapshot determinism");

            Console.WriteLine("");
            Console.WriteLine("PlanningDirectorTests: passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}