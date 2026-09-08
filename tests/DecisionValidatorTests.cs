// Phase 19: DecisionValidator domain tests — built incrementally, see bottom.
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Capabilities;
using CapBot.Core.Executor;
using CapBot.Core.Captain;
using CapBot.Core.Validation;

namespace CapBot.TaskTests
{
    internal static class DecisionValidatorTests
    {
        // ANCHOR: BODY

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
            // Reset the validator-under-test FIRST, then the layers it reads.
            DecisionValidator.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 300000 };
            s_Snap = FreshCalm(300000);
            s_Lines.Clear();
            DecisionValidator.SetAuthorityProbe(delegate { return true; });
            DecisionValidator.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            DecisionValidator.SetWorldProvider(delegate { return s_Snap; });
            DecisionValidator.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
        }

        private static void Advance(int ms) { s_Clock.NowMs += ms; }

        // Each Eval clears the listener buffer first, so HasLineContaining
        // after Eval checks exactly the lines of that single pass.
        private static void Eval()
        {
            s_Lines.Clear();
            DecisionValidator.Evaluate(s_Clock.NowMs);
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // ---- snapshot builders -------------------------------------------------

        private static ShipSnapshot PlayerShip()
        {
            return new ShipSnapshot(1, "player", true, 0, false, 1f, 1f, false, 0, -1, 0, 10f, false);
        }

        private static CrewMemberSnapshot Crew(int id, string name)
        {
            return new CrewMemberSnapshot(id, name, true, 1, 0, true, true, 1f, "Bridge", false, -1);
        }

        private static WorldSnapshot FreshCalm(int timeMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(PlayerShip());
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot"));
            crew.Add(Crew(2, "bot1"));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // Sector-changed variant: current sector 9 instead of 5.
        private static WorldSnapshot SectorChangedSnap(int timeMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(PlayerShip());
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot"));
            crew.Add(Crew(2, "bot1"));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(9, "Sector 9", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // In-warp variant: InWarp=true, WarpTargetSectorId=12.
        private static WorldSnapshot WarpSnap(int timeMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(PlayerShip());
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot"));
            crew.Add(Crew(2, "bot1"));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, true, 12,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // Unknown-current-sector variant: CurrentSectorId=-1.
        private static WorldSnapshot UnknownSectorSnap(int timeMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(PlayerShip());
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot"));
            crew.Add(Crew(2, "bot1"));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(-1, null, 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // Not-started variant: GameStarted=false.
        private static WorldSnapshot NotStartedSnap(int timeMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(PlayerShip());
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot"));
            crew.Add(Crew(2, "bot1"));
            return new WorldSnapshot(
                timeMs, false, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // ---- task builder ------------------------------------------------------

        // Mirrors the house authoring shape (P18 CreateDeliberationTaskLocked):
        // CAPTAIN-owned task with CapabilityId + Argument metadata, registered
        // and queued through the real pipeline. Returns the task or null.
        private static CapBotTask MakeQueuedTask(string taskType, string targetKind, string targetId,
            string capability, string argument)
        {
            CapBotTask t = CapBotTask.Create(
                taskType, "CAPTAIN", "test task", 8, 1, 60000, targetKind, targetId, null);
            if (t == null) return null;
            if (!string.IsNullOrEmpty(capability) && !t.SetMetadata("CapabilityId", capability)) return null;
            if (!string.IsNullOrEmpty(argument) && !t.SetMetadata("Argument", argument)) return null;
            if (!TaskRegistry.Register(t)) return null;
            if (!t.TryQueue()) return null;
            return t;
        }

        // ---- Run ---------------------------------------------------------------

        internal static int Run()
        {
            // ---- DV01: valid CAPTAIN_DELIB passes clean -------------------------
            // s_Snap defaults to a fresh calm snapshot in FreshSetup.
            FreshSetup();
            CapBotTask clean1 = MakeQueuedTask("CAPTAIN_DELIB", "SECTOR", "5", RegisteredCapabilities.IssueMoveOrder, "5");
            Check(clean1 != null, "DV01 task created");
            Eval();
            Check(s_Lines.Count == 0, "DV01 no lines for clean task");
            Check(DecisionValidator.GetValidations() == 1, "DV01 validation counted");
            Check(DecisionValidator.GetRejections() == 0, "DV01 no rejections");
            Check(DecisionValidator.GetUncertainPasses() == 0, "DV01 no uncertain passes");

            // ---- DV01b: real P18 authoring path validates clean -----------------
            // Drive the real CaptainDirector authoring (crew divergence →
            // dwell → calm gate → CAPTAIN_DELIB task) and confirm the
            // validator passes it with zero diagnostics.
            FreshSetup();
            List<ShipSnapshot> ships1 = new List<ShipSnapshot>();
            ships1.Add(PlayerShip());
            List<CrewMemberSnapshot> crew1 = new List<CrewMemberSnapshot>();
            crew1.Add(new CrewMemberSnapshot(1, "captain-bot", true, 0, 0, true, true, 1f, "Bridge", true, -1));
            crew1.Add(new CrewMemberSnapshot(2, "bot1", true, 1, 0, true, true, 1f, "Engineering", false, -1));
            s_Snap = new WorldSnapshot(
                s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                ships1, crew1, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
            // Wire CaptainDirector to the same virtual world and drive one full
            // authoring cycle (open intent → dwell → author).
            CaptainDirector.ResetForTests();
            CaptainDirector.SetAuthorityProbe(delegate { return true; });
            CaptainDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CaptainDirector.SetWorldProvider(delegate { return s_Snap; });
            CaptainDirector.SetDecisionListener(delegate (string line) { });
            Advance(CaptainDirector.MinRecheckMs);
            CaptainDirector.Evaluate(s_Clock.NowMs); // open intent
            Advance(CaptainDirector.AuthoringDwellMs);
            s_Snap = CloneSnap(s_Snap); // fresh time, same premise
            CaptainDirector.Evaluate(s_Clock.NowMs); // author
            CapBotTask delib = null;
            List<CapBotTask> live1 = TaskRegistry.LiveSnapshot();
            for (int i = 0; i < live1.Count; i++)
            {
                if (live1[i].TaskType == "CAPTAIN_DELIB" && live1[i].State == TaskState.Queued) { delib = live1[i]; break; }
            }
            Check(delib != null, "DV01b P18 authored a CAPTAIN_DELIB task");
            // Fresh snapshot (authoring consumed the old one's time), then one
            // validator pass: no diagnostics, validations counted.
            Advance(DecisionValidator.MinRecheckMs);
            s_Snap = CloneSnap(s_Snap);
            Eval();
            Check(s_Lines.Count == 0, "DV01b clean pass on P18-authored task");
            Check(DecisionValidator.GetValidations() == 1, "DV01b one validation counted");
            Check(DecisionValidator.GetRejections() == 0, "DV01b no rejections");

            // ---- DV02: ISSUE_MOVE_ORDER wrong target kind -----------------------
            FreshSetup();
            CapBotTask bad1 = MakeQueuedTask("CAPTAIN_DELIB", "SHIP", "5", RegisteredCapabilities.IssueMoveOrder, "5");
            Check(bad1 != null, "DV02 task created");
            Eval();
            Check(HasLineContaining("DecisionRejected #"), "DV02 rejected line");
            Check(HasLineContaining("reason=target kind mismatch"), "DV02 kind reason");
            Check(DecisionValidator.GetRejections() == 1, "DV02 rejection counted");
            Check(DecisionValidator.GetShapeRejections() == 1, "DV02 shape subset counted");
            Check(bad1.State == TaskState.Queued, "DV02 diagnostics only — task untouched");

            // ---- DV03: ISSUE_MOVE_ORDER bad sector id ---------------------------
            FreshSetup();
            CapBotTask bad2 = MakeQueuedTask("CAPTAIN_DELIB", "SECTOR", "notanint", RegisteredCapabilities.IssueMoveOrder, "notanint");
            Check(bad2 != null, "DV03 task created");
            Eval();
            Check(HasLineContaining("reason=order id unknown"), "DV03 id reason");
            Check(DecisionValidator.GetRejections() == 1, "DV03 rejection counted");
            CapBotTask bad2b = MakeQueuedTask("CAPTAIN_DELIB", "SECTOR", "-3", RegisteredCapabilities.IssueMoveOrder, "-3");
            Check(bad2b != null, "DV03b task created");
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(HasLineContaining("reason=order id unknown"), "DV03b negative rejected");
            Check(DecisionValidator.GetRejections() == 3, "DV03b cumulative rejections counted");

            // ---- DV04: SET_CAPTAIN_ORDER vocabulary ------------------------------
            FreshSetup();
            CapBotTask good1 = MakeQueuedTask("EMERGENCY", "ORDER", "9", RegisteredCapabilities.SetCaptainOrder, null);
            Check(good1 != null, "DV04 valid order task created");
            Eval();
            Check(s_Lines.Count == 0, "DV04 valid order id passes clean");
            Check(DecisionValidator.GetValidations() == 1, "DV04 validation counted");
            CapBotTask bad3 = MakeQueuedTask("EMERGENCY", "ORDER", "42", RegisteredCapabilities.SetCaptainOrder, null);
            Check(bad3 != null, "DV04b task created");
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(HasLineContaining("reason=order id unknown"), "DV04b unknown order rejected");
            Check(DecisionValidator.GetRejections() == 1, "DV04b rejection counted");

            // ---- DV05: ADD/REMOVE_COURSE_GOAL shapes -----------------------------
            FreshSetup();
            CapBotTask nav1 = MakeQueuedTask("NAV_RECOVERY", "SECTOR", "5", RegisteredCapabilities.AddCourseGoal, "5");
            Check(nav1 != null, "DV05 nav task created");
            Eval();
            Check(s_Lines.Count == 0, "DV05 valid ADD_COURSE_GOAL passes");
            CapBotTask nav2 = MakeQueuedTask("NAV_RECOVERY", "ORDER", "5", RegisteredCapabilities.AddCourseGoal, "5");
            Check(nav2 != null, "DV05b task created");
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(HasLineContaining("reason=target kind mismatch"), "DV05b wrong kind rejected");
            CapBotTask nav3 = MakeQueuedTask("NAV_RECOVERY", "SECTOR", "7", RegisteredCapabilities.RemoveCourseGoal, "7");
            Check(nav3 != null, "DV05c task created");
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(HasLineContaining("reason=stale premise: sector changed"), "DV05c premise check applies to REMOVE too");

            // ---- DV06: stale premise — sector changed ---------------------------
            FreshSetup();
            s_Snap = SectorChangedSnap(s_Clock.NowMs); // current=9, task claims 5
            CapBotTask stale1 = MakeQueuedTask("NAV_RECOVERY", "SECTOR", "5", RegisteredCapabilities.AddCourseGoal, "5");
            Check(stale1 != null, "DV06 task created");
            Eval();
            Check(HasLineContaining("reason=stale premise: sector changed"), "DV06 sector-change rejected");
            Check(DecisionValidator.GetStalePremiseRejections() == 1, "DV06 stale subset counted");
            Check(stale1.State == TaskState.Queued, "DV06 diagnostics only — task untouched");

            // ---- DV07: stale premise — in warp ----------------------------------
            FreshSetup();
            s_Snap = WarpSnap(s_Clock.NowMs); // current=5 matches claim, but in warp
            CapBotTask stale2 = MakeQueuedTask("CAPTAIN_DELIB", "SECTOR", "5", RegisteredCapabilities.IssueMoveOrder, "5");
            Check(stale2 != null, "DV07 task created");
            Eval();
            Check(HasLineContaining("reason=stale premise: in warp"), "DV07 warp rejected");
            Check(DecisionValidator.GetStalePremiseRejections() == 1, "DV07 stale subset counted");

            // ---- DV08: fail-open on snapshot problems ----------------------------
            FreshSetup();
            s_Snap = null;
            Eval();
            Check(HasLineContaining("DecisionUncertain no snapshot"), "DV08a null snapshot uncertain");
            Check(DecisionValidator.GetRejections() == 0, "DV08a never rejected");
            s_Snap = WorldSnapshot.Empty; // never-captured marker
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(HasLineContaining("DecisionUncertain snapshot never captured"), "DV08b never-captured uncertain");
            s_Snap = FreshCalm(s_Clock.NowMs - DecisionValidator.MaxStaleSnapshotMs - 1);
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(HasLineContaining("DecisionUncertain snapshot stale"), "DV08c stale uncertain");
            s_Snap = FreshCalm(s_Clock.NowMs + 60000);
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(HasLineContaining("DecisionUncertain snapshot from future"), "DV08d future uncertain");
            s_Snap = NotStartedSnap(s_Clock.NowMs);
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(HasLineContaining("DecisionUncertain game not started"), "DV08e not-started uncertain");
            Check(DecisionValidator.GetRejections() == 0, "DV08 all fail-open, zero rejections");
            Check(DecisionValidator.GetUncertainPasses() == 5, "DV08 five uncertain passes counted");

            // ---- DV09: fail-open on uncertain premise data -----------------------
            FreshSetup();
            s_Snap = UnknownSectorSnap(s_Clock.NowMs); // CurrentSectorId=-1
            CapBotTask unk1 = MakeQueuedTask("NAV_RECOVERY", "SECTOR", "5", RegisteredCapabilities.AddCourseGoal, "5");
            Eval();
            Check(s_Lines.Count == 0, "DV09a unknown current sector = silent uncertain pass");
            Check(DecisionValidator.GetUncertainPasses() == 1, "DV09a per-task uncertain counted silently");
            Check(DecisionValidator.GetRejections() == 0, "DV09a never rejected");

            // ---- DV10: authority deny-by-default ---------------------------------
            FreshSetup();
            MakeQueuedTask("CAPTAIN_DELIB", "SECTOR", "5", RegisteredCapabilities.IssueMoveOrder, "5");
            DecisionValidator.SetAuthorityProbe(null);
            Eval();
            Check(DecisionValidator.GetValidations() == 0, "DV10a null probe = deny-by-default");
            DecisionValidator.SetAuthorityProbe(delegate { return false; });
            Eval();
            Check(DecisionValidator.GetValidations() == 0, "DV10b false probe = deny");
            DecisionValidator.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
            Eval();
            Check(DecisionValidator.GetValidations() == 0, "DV10c faulting probe = fail-closed no-op");
            DecisionValidator.SetAuthorityProbe(delegate { return true; });
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(DecisionValidator.GetValidations() == 1, "DV10d authority restored");

            // ---- DV11: cadence gate ----------------------------------------------
            FreshSetup();
            MakeQueuedTask("CAPTAIN_DELIB", "SECTOR", "5", RegisteredCapabilities.IssueMoveOrder, "5");
            Eval();
            Check(DecisionValidator.GetValidations() == 1, "DV11 first eval passes");
            Eval(); // same timestamp — must be cadence-blocked
            Check(DecisionValidator.GetValidations() == 1, "DV11 same-timestamp re-eval cadence-blocked");
            Advance(500);
            Eval();
            Check(DecisionValidator.GetValidations() == 1, "DV11 sub-cadence blocked");
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(DecisionValidator.GetValidations() == 2, "DV11 next cadence window passes");

            // ---- DV12: argument mismatch -----------------------------------------
            FreshSetup();
            CapBotTask arg1 = MakeQueuedTask("CAPTAIN_DELIB", "SECTOR", "5", RegisteredCapabilities.IssueMoveOrder, "9");
            Check(arg1 != null, "DV12 task created");
            Eval();
            Check(HasLineContaining("reason=argument mismatch"), "DV12 mismatch rejected");
            Check(DecisionValidator.GetRejections() == 1, "DV12 rejection counted");
            // DV12b: no Argument metadata — no screen (P14 recipes without Argument)
            FreshSetup();
            CapBotTask arg2 = MakeQueuedTask("NAV_RECOVERY", "SECTOR", "5", RegisteredCapabilities.AddCourseGoal, null);
            Check(arg2 != null, "DV12b task created");
            Eval();
            Check(s_Lines.Count == 0, "DV12b missing argument = clean pass");

            // ---- DV13: EMERGENCY task without CapabilityId skipped ---------------
            // Known-fail-by-contract P9 coordination tasks carry no capability
            // binding; the validator deliberately does not screen them.
            FreshSetup();
            CapBotTask noCap = MakeQueuedTask("EMERGENCY", "ORDER", "", null, null);
            Check(noCap != null, "DV13 task created");
            Eval();
            Check(s_Lines.Count == 0, "DV13 no-capability task skipped silently");
            Check(DecisionValidator.GetValidations() == 0, "DV13 not counted as validated");

            // ---- DV14: counters + readbacks determinism ---------------------------
            FreshSetup();
            List<string> lines0 = DecisionValidator.Lines();
            Check(lines0.Count == 3 && lines0[0] == "validations=0", "DV14 Lines baseline");
            Check(DecisionValidator.StatusLines().Count == 1
                && DecisionValidator.StatusLines()[0].IndexOf("DecisionValidator: validations=0", StringComparison.Ordinal) == 0,
                "DV14 StatusLines format");
            MakeQueuedTask("CAPTAIN_DELIB", "SECTOR", "5", RegisteredCapabilities.IssueMoveOrder, "5");
            Eval();
            List<string> lines1 = DecisionValidator.Lines();
            Check(lines1[0] == "validations=1", "DV14 validations reflected");
            Check(lines1[1].IndexOf("rejections=0", StringComparison.Ordinal) == 0, "DV14 rejections reflected");
            Check(lines1[2] == "uncertain=0", "DV14 uncertain reflected");
            Check(DecisionValidator.GetLastUncertainReason().Length == 0, "DV14 no last-uncertain yet");
            s_Snap = null;
            Advance(DecisionValidator.MinRecheckMs);
            Eval();
            Check(DecisionValidator.GetLastUncertainReason() == "no snapshot", "DV14 last-uncertain recorded");
            // ResetForTests clears state AND seams.
            DecisionValidator.ResetForTests();
            Check(DecisionValidator.GetValidations() == 0, "DV14 reset clears counters");
            Check(DecisionValidator.GetLastUncertainReason().Length == 0, "DV14 reset clears reason");
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // no seams → deny-by-default, no throw
            Check(DecisionValidator.GetValidations() == 0, "DV14 reset nulls seams (deny-by-default)");

            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        // Fresh-time clone helper (same premise, advanced SnapshotTimeMs).
        private static WorldSnapshot CloneSnap(WorldSnapshot s)
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
                s_Clock.NowMs, s.GameStarted, s.IsHost, s.CurrentHubId, s.SessionAuthority,
                ships, crew, missions, s.Threats, s.Navigation, s.Resources, objs,
                s.ThreatAuthority, s.NavigationAuthority, s.ResourceAuthority, s.WorldObjectsAuthority,
                s.PlayerShipFireCount, s.PlayerShipReactorTempFraction);
        }
    }
}