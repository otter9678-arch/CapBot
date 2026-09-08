// Dev-side unit tests for the Phase 15 mission director (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure mission domain (MissionDirector) plus the domains it
// builds on. Time is virtual: every timestamp is an explicit nowMs argument —
// no real clock reads, no sleeping.
//
// Covers the Phase 15 mandated scenarios:
//   MS01 mission tracking end-to-end (open -> progress -> completed; DATA carry)
//   MS02 transition suppression (no duplicate reports once terminal)
//   MS03 abandonment edge
//   MS04 terminal-at-first-sighting (ended/abandoned before ever seen)
//   MS05 stall dwell report (once per episode, re-armed on progress)
//   MS06 missionless edge report (readable empty list; never spam)
//   MS07 unreadable missions section = uncertainty (records untouched)
//   MS08 fail-safe inputs (null/stale/not-started snapshot)
//   MS09 authority deny-by-default (null/faulting/non-master = no-op)
//   MS10 vanished mission report + expiry hygiene + bounded history
//   MS11 bounded tracked set + shed-oldest (8-cap)
//   MS12 same-type-id collision counter (audit L2 documented caveat)
//   MS13 cadence + counters + diagnostics determinism
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Missions;
using CapBot.Core.Navigation;
using CapBot.Core.Emergency;

namespace CapBot.TaskTests
{
    internal static class MissionTests
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
            MissionDirector.ResetForTests();
            NavigationRecoveryDirector.ResetForTests();
            EmergencyDirector.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 100000 };
            s_Snap = null;
            s_Lines.Clear();
            MissionDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            MissionDirector.SetWorldProvider(delegate { return s_Snap; });
            MissionDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            MissionDirector.SetAuthorityProbe(delegate { return true; });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int Eval() { return MissionDirector.Evaluate(s_Clock.NowMs); }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static int CountLinesContaining(string prefix)
        {
            int n = 0;
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(prefix, StringComparison.Ordinal) == 0) n++;
            }
            return n;
        }

        private static ShipSnapshot Ship()
        {
            return new ShipSnapshot(1, "player", true, 0, false, 1f, 0.5f, false, 0, -1, 0, 10f);
        }

        private static ThreatSnapshot QuietThreat()
        {
            return new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN);
        }

        private static NavigationSnapshot Nav()
        {
            return new NavigationSnapshot(5, "Sector 5", -1, false, -1,
                new List<int>(), true, 50f, float.NaN, 0f, false);
        }

        private static MissionSnapshot Msn(int typeId, bool ended, bool abandoned, int total, int completed, string text)
        {
            return new MissionSnapshot(typeId, ended, abandoned, total, completed, text);
        }

        // Phase 9-ctor snapshot: session = MasterDerived, nav authority Synchronized.
        private static WorldSnapshot Snap(int timeMs, List<MissionSnapshot> missions)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(Ship());
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(new CrewMemberSnapshot(1, "bot1", true, 0, 0, true, true, 0.9f, "Bridge", true, 3));
            List<MissionSnapshot> missionsOrEmpty = missions ?? new List<MissionSnapshot>();
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, missionsOrEmpty,
                QuietThreat(), Nav(),
                new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        private static List<MissionSnapshot> One(int typeId, bool ended, bool abandoned, int total, int completed, string text)
        {
            List<MissionSnapshot> l = new List<MissionSnapshot>();
            l.Add(Msn(typeId, ended, abandoned, total, completed, text));
            return l;
        }

        internal static int Run()
        {
            // ---- MS01: tracking end-to-end ---------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(4242, false, false, 3, 0, "reach the station")));
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 1, "MS01 open reported");
            Check(HasLineContaining("MissionOpened MISSION:4242 objectives=3"), "MS01 opened line");
            Check(MissionDirector.ActiveMissionCount == 1, "MS01 tracked");
            MissionTrackRecord r1 = MissionDirector.GetTrack("MISSION:4242");
            Check(r1 != null && r1.CompletedObjectives == 0 && r1.TotalObjectives == 3, "MS01 record fields");
            Check(r1 != null && r1.LatestObjectiveText == "reach the station", "MS01 text carried as DATA");
            // Same state again: quiet (no new reports).
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(4242, false, false, 3, 0, "reach the station")));
            Check(Eval() == 0, "MS01 unchanged state quiet");
            // Progress edge to 2/3.
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(4242, false, false, 3, 2, null)));
            Check(Eval() == 1, "MS01 progress reported");
            Check(HasLineContaining("MissionProgress MISSION:4242 done=2/3"), "MS01 progress line");
            r1 = MissionDirector.GetTrack("MISSION:4242");
            Check(r1 != null && r1.CompletedObjectives == 2 && r1.LastProgressMs == s_Clock.NowMs, "MS01 progress stamped");
            // Completion edge (one report per edge — completion suppresses the
            // progress report for the same transition).
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(4242, false, false, 3, 3, null)));
            Check(Eval() == 1, "MS01 completed reported (single report per edge)");
            Check(HasLineContaining("MissionCompleted MISSION:4242 done=3/3"), "MS01 completed line");
            r1 = MissionDirector.GetTrack("MISSION:4242");
            Check(r1 != null && r1.IsTerminalState, "MS01 record terminal");
            Check(MissionDirector.CompletedReportCount == 1, "MS01 completed counted");

            // ---- MS02: transition suppression -------------------------------------
            // Terminal record stays in the set but never re-reports.
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(4242, false, false, 3, 3, null)));
            Check(Eval() == 0, "MS02 terminal state quiet");
            Check(MissionDirector.CompletedReportCount == 1, "MS02 exactly one completion report");
            // Ended flag with the same completed count: still quiet (already terminal).
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(4242, true, false, 3, 3, null)));
            Check(Eval() == 0, "MS02 ended-after-completed quiet");

            // ---- MS03: abandonment edge --------------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(77, false, false, 2, 0, null)));
            Advance(MissionDirector.MinRecheckMs);
            Eval();
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(77, false, true, 2, 0, null)));
            Check(Eval() == 1, "MS03 abandoned reported");
            Check(HasLineContaining("MissionAbandoned MISSION:77"), "MS03 abandoned line");
            Check(MissionDirector.AbandonedReportCount == 1, "MS03 abandoned counted");
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(77, false, true, 2, 0, null)));
            Check(Eval() == 0, "MS03 abandoned quiet after edge");
            Check(MissionDirector.AbandonedReportCount == 1, "MS03 exactly one abandon report");

            // ---- MS04: terminal at first sighting -----------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(99, true, false, 2, 2, null)));
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 1, "MS04 ended-at-first-sighting reported as completed");
            Check(HasLineContaining("(terminal at first sighting)"), "MS04 terminal-first-sighting marker");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(98, false, true, 2, 0, null)));
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 1, "MS04 abandoned-at-first-sighting reported");
            Check(MissionDirector.MissionsTrackedCount == 1, "MS04 still tracked (record kept)");

            // ---- MS05: stall dwell report --------------------------------------------
            // NOTE: cadence-gating means each eval must be a separate pass;
            // expiry hygiene never expires a LIVE PRESENT mission (design
            // contract), so the long dwell advances are safe here.
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(55, false, false, 4, 1, "kill the raiders")));
            Advance(MissionDirector.MinRecheckMs);
            Eval();
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(55, false, false, 4, 1, "kill the raiders")));
            Eval();
            // No progress for the window: exactly ONE stall report.
            Advance(MissionDirector.StallReportMs);
            Publish(Snap(s_Clock.NowMs, One(55, false, false, 4, 1, "kill the raiders")));
            Check(Eval() == 1, "MS05 stall reported after dwell");
            Check(HasLineContaining("MissionStallReport MISSION:55"), "MS05 stall line");
            Check(MissionDirector.StallReportCount == 1, "MS05 stall counted once");
            // Repeat pass in the same episode: quiet (duplicates suppressed).
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(55, false, false, 4, 1, "kill the raiders")));
            Check(Eval() == 0, "MS05 repeat stall quiet");
            Check(MissionDirector.StallReportCount == 1, "MS05 still one stall report");
            Check(MissionDirector.DuplicatesSuppressedCount == 1, "MS05 duplicate counted");
            // Progress re-arms the episode; the next stall reports again.
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(55, false, false, 4, 2, null)));
            Check(Eval() == 1, "MS05 progress re-arms episode");
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(55, false, false, 4, 2, null)));
            Eval();
            Advance(MissionDirector.StallReportMs);
            Publish(Snap(s_Clock.NowMs, One(55, false, false, 4, 2, null)));
            Check(Eval() == 1, "MS05 second stall after re-arm");
            Check(MissionDirector.StallReportCount == 2, "MS05 stall counted twice across episodes");

            // ---- MS06: missionless edge report ----------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, new List<MissionSnapshot>()));
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 0, "MS06 never-seen-activity quiet (fresh session baseline)");
            Check(MissionDirector.MissionlessReportCount == 0, "MS06 no report on pristine session");
            // After tracking activity, an empty readable list reports once.
            Publish(Snap(s_Clock.NowMs, One(31, false, false, 2, 0, null)));
            Advance(MissionDirector.MinRecheckMs);
            Eval();
            Publish(Snap(s_Clock.NowMs, new List<MissionSnapshot>()));
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 1, "MS06 missionless edge reported");
            Check(HasLineContaining("MissionlessReport"), "MS06 missionless line");
            Check(MissionDirector.MissionlessReportCount == 1, "MS06 missionless counted once");
            // Repeat missionless pass: quiet (edge-triggered).
            Advance(MissionDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, new List<MissionSnapshot>()));
            Check(Eval() == 0, "MS06 repeat missionless quiet");
            Check(MissionDirector.MissionlessReportCount == 1, "MS06 still one report");

            // ---- MS07: capture-failure semantics ----------------------------------------
            // The WorldSnapshot ctors NORMALIZE a null missions list to an
            // empty array (Bounded contract), so a capture failure on the
            // missions section is indistinguishable from an empty readable
            // list downstream. Documented contract: the mission the director
            // already tracked vanishes from the readable list and gets the
            // one-shot MissionVanished report (absence handling), not an
            // uncertainty path. The null-section branch in the director is
            // defensive-only and unreachable.
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(20, false, false, 2, 0, null)));
            Advance(MissionDirector.MinRecheckMs);
            Eval();
            Check(MissionDirector.ActiveMissionCount == 1, "MS07 setup tracked");
            s_Snap = new WorldSnapshot(
                s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                new List<ShipSnapshot> { Ship() }, new List<CrewMemberSnapshot>(), null,
                QuietThreat(), Nav(),
                new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 1, "MS07 null section normalizes to empty (absence path)");
            Check(HasLineContaining("MissionVanished MISSION:20"), "MS07 tracked mission vanishes once");
            Check(MissionDirector.ActiveMissionCount == 0, "MS07 record expired via absence");

            // ---- MS08: fail-safe inputs -------------------------------------------------
            FreshSetup();
            Publish(null);
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 0, "MS08 no snapshot = no-op");
            Check(HasLineContaining("no world snapshot"), "MS08 uncertainty recorded");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs - MissionDirector.MaxStaleSnapshotMs - 1, One(20, false, false, 2, 0, null)));
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 0, "MS08 stale snapshot rejected");
            Check(MissionDirector.StaleRejectionCount == 1, "MS08 stale counted");
            FreshSetup();
            WorldSnapshot notStarted = Snap(s_Clock.NowMs, One(20, false, false, 2, 0, null));
            Publish(notStarted);
            Advance(MissionDirector.MinRecheckMs);
            s_Snap = new WorldSnapshot(
                s_Clock.NowMs, false, true, 7, WorldAuthority.MasterDerived,
                new List<ShipSnapshot> { Ship() }, new List<CrewMemberSnapshot>(), new List<MissionSnapshot>(),
                QuietThreat(), Nav(),
                new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
            Check(Eval() == 0, "MS08 game-not-started rejected");

            // ---- MS09: authority deny-by-default ----------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(20, false, false, 2, 0, null)));
            Advance(MissionDirector.MinRecheckMs);
            MissionDirector.SetAuthorityProbe(delegate { return false; });
            Check(Eval() == 0, "MS09 non-master = no-op");
            MissionDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
            Check(Eval() == 0, "MS09 faulting probe = no-op");
            MissionDirector.SetAuthorityProbe(null);
            Check(Eval() == 0, "MS09 null probe = no-op");
            Check(MissionDirector.EvaluationCount == 0, "MS09 gated passes not counted");

            // ---- MS10: vanished report + expiry hygiene + bounded history ---------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(64, false, false, 2, 0, null)));
            Advance(MissionDirector.MinRecheckMs);
            Eval();
            Check(MissionDirector.ActiveMissionCount == 1, "MS10 setup tracked");
            // Mission vanishes from a readable list (ended on the game side but
            // absent here): vanishing while live reports once.
            Publish(Snap(s_Clock.NowMs, new List<MissionSnapshot>()));
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 1, "MS10 vanished reported");
            Check(HasLineContaining("MissionVanished MISSION:64"), "MS10 vanished line");
            Check(MissionDirector.VanishedReportCount == 1, "MS10 vanished counted");
            Check(MissionDirector.ActiveMissionCount == 0, "MS10 record expired immediately (absent from list)");
            Check(MissionDirector.HistoryCount == 1, "MS10 history entry");
            // Bounded history: churn more than MaxHistory entries.
            for (int i = 0; i < MissionDirector.MaxHistory + 4; i++)
            {
                Publish(Snap(s_Clock.NowMs, One(500 + i, false, false, 1, 0, null)));
                Advance(MissionDirector.MinRecheckMs);
                Eval();
                Publish(Snap(s_Clock.NowMs, new List<MissionSnapshot>()));
                Advance(MissionDirector.MinRecheckMs);
                Eval();
            }
            Check(MissionDirector.HistoryCount == MissionDirector.MaxHistory, "MS10 history bounded");

            // ---- MS11: bounded tracked set + shed-oldest ---------------------------------
            // The list grows cumulatively (all missions stay present) so the
            // tracked set genuinely fills; a one-mission-per-pass loop would
            // expire each previous mission via absence and never reach the cap.
            FreshSetup();
            for (int i = 0; i < MissionDirector.MaxActiveMissions + 2; i++)
            {
                List<MissionSnapshot> batch = new List<MissionSnapshot>(i + 1);
                for (int j = 0; j <= i; j++) batch.Add(Msn(600 + j, false, false, 1, 0, null));
                Publish(Snap(s_Clock.NowMs, batch));
                Advance(MissionDirector.MinRecheckMs);
                Eval();
            }
            Check(MissionDirector.ActiveMissionCount == MissionDirector.MaxActiveMissions,
                "MS11 tracked set stays bounded");
            Check(HasLineContaining("MissionShed"), "MS11 shedding observed");
            // Deterministic shed: the OLDEST (600) left the set, newest retained.
            Check(MissionDirector.GetTrack("MISSION:600") == null, "MS11 oldest shed");
            Check(MissionDirector.GetTrack("MISSION:" + (600 + MissionDirector.MaxActiveMissions + 1)) != null,
                "MS11 newest retained");

            // ---- MS12: same-type-id collision counter -------------------------------------
            FreshSetup();
            List<MissionSnapshot> twins = new List<MissionSnapshot>();
            twins.Add(Msn(800, false, false, 2, 0, null));
            twins.Add(Msn(800, false, false, 3, 1, null));
            Publish(Snap(s_Clock.NowMs, twins));
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 1, "MS12 one open per type id");
            Check(MissionDirector.ActiveMissionCount == 1, "MS12 same-type instances collapsed");
            Check(MissionDirector.SameTypeIdCollisionCount == 1, "MS12 collision counted");
            MissionTrackRecord r12 = MissionDirector.GetTrack("MISSION:800");
            Check(r12 != null && r12.TotalObjectives == 2, "MS12 first sighting wins");
            Check(MissionDirector.Lines().Count == 1, "MS12 diagnostics bounded to tracked set");

            // ---- MS13: cadence + counters + diagnostics ------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(21, false, false, 2, 0, null)));
            Check(Eval() == 1, "MS13 first eval opens");
            Check(Eval() == 0, "MS13 same-instant re-eval cadence-gated");
            Advance(1);
            Check(Eval() == 0, "MS13 sub-cadence re-eval gated");
            Check(MissionDirector.EvaluationCount == 1, "MS13 gated passes not counted");
            Advance(MissionDirector.MinRecheckMs);
            Check(Eval() == 0, "MS13 post-cadence quiet (unchanged state)");
            Check(MissionDirector.Lines().Count == 1, "MS13 Lines bounded");
            List<string> status = MissionDirector.StatusLines();
            Check(status.Count == 2 && status[0].IndexOf("missions=") == 0 && status[1].IndexOf("uncertain=") > 0,
                "MS13 StatusLines bounded");
            Check(MissionDirector.GetTrack(null) == null, "MS13 GetTrack null-safe");
            Check(MissionDirector.GetTrack("") == null, "MS13 GetTrack empty-safe");

            Console.WriteLine("SUITE MissionTests passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}