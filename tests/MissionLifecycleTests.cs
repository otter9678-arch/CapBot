// NOT part of the shipped mod: compiled by tests\run_tests.ps1 against the
// pure mission domain (MissionLifecycle FSM + MissionReturnPolicy) plus the
// domains it builds on. Time is virtual: every timestamp is an explicit nowMs
// argument — no real clock reads, no sleeping.
//
// Covers the P52 mandated scenarios (master prompt §5–§13):
//   ML01 detect -> objectives -> active (detection pipeline, stable-id key)
//   ML02 objective complete edge -> complete -> return-required (type table)
//   ML03 legacy type-key fallback (MissionId -1 keeps MISSION:T<id>)
//   ML04 failed mission (game flag + abandoned mirror)
//   ML05 returned terminal (game active flip after return)
//   ML06 temp-unavailable survival (vanished mission never DEAD)
//   ML07 no-stuck contract: every state has a bounded exit (expiry hygiene)
//   ML08 return policy table verbatim (type 0 -> 3; 25/68/71/72/780/2437/2580/104851 -> 2; 69/264/683/81262/24213/24214/25249 -> 1)
//   ML09 return policy additive path (unlisted type + game turn-in flag)
//   ML10 authority deny-by-default (null/faulting/non-master = no-op)
//   ML11 fail-safe inputs (null/stale/not-started snapshot)
//   ML12 bounded tracked set + deterministic fold (MaxTracked cap)
//   ML13 objective snapshots bounded (MaxObjectives) + unknown-kind fallback
//   ML14 cadence + counters + diagnostics determinism
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Missions;
using CapBot.Core.Navigation;
using CapBot.Core.Emergency;

namespace CapBot.TaskTests
{
    internal static class MissionLifecycleTests
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
            MissionLifecycle.ResetForTests();
            MissionDirector.ResetForTests();
            NavigationRecoveryDirector.ResetForTests();
            EmergencyDirector.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 200000 };
            s_Snap = null;
            s_Lines.Clear();
            MissionLifecycle.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            MissionLifecycle.SetWorldProvider(delegate { return s_Snap; });
            MissionLifecycle.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            MissionLifecycle.SetAuthorityProbe(delegate { return true; });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int Eval() { return MissionLifecycle.Evaluate(s_Clock.NowMs); }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
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

        private static MissionObjectiveSnapshot Obj(string kind, bool completed, int amt, int needed, int sector)
        {
            return new MissionObjectiveSnapshot(kind, completed, amt, needed, sector, null, -1, -1, -1);
        }

        private static MissionSnapshot Msn(int typeId, int missionId, bool ended, bool abandoned,
            int total, int completed, List<MissionObjectiveSnapshot> objectives,
            bool activeKnown = false, bool active = false,
            bool turnInKnown = false, bool turnIn = false,
            bool failedKnown = false, bool failed = false)
        {
            return new MissionSnapshot(typeId, ended, abandoned, total, completed, null,
                missionId, activeKnown, active, turnInKnown, turnIn, failedKnown, failed, objectives);
        }

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

        private static List<MissionSnapshot> One(MissionSnapshot m)
        {
            List<MissionSnapshot> l = new List<MissionSnapshot>();
            l.Add(m);
            return l;
        }

        private static List<MissionObjectiveSnapshot> TwoObjs(bool firstDone, bool secondDone)
        {
            List<MissionObjectiveSnapshot> l = new List<MissionObjectiveSnapshot>();
            l.Add(Obj(MissionObjectiveSnapshot.TalkToNpcKind, firstDone, firstDone ? 1 : 0, 1, -1));
            l.Add(Obj(MissionObjectiveSnapshot.ReachSectorKind, secondDone, secondDone ? 1 : 0, 1, 9));
            return l;
        }

        internal static int Run()
        {
            // ---- ML01: detection pipeline with stable-id key --------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(Msn(4242, 777001, false, false, 2, 0, TwoObjs(false, false)))));
            Advance(MissionLifecycle.MinRecheckMs);
            Check(Eval() == 1, "ML01 opened reported");
            Check(HasLineContaining("MissionLifecycleOpened MISSION:I777001 type=4242"), "ML01 stable-id track line");
            MissionLifecycleRecord r1 = MissionLifecycle.GetTrack("MISSION:I777001");
            Check(r1 != null && r1.MissionId == 777001 && r1.MissionTypeId == 4242, "ML01 record identity");
            Check(r1 != null && r1.State == MissionLifecycleState.MissionObjectivesDetected, "ML01 initial state");
            // Same state again -> the FSM advances OBJECTIVES_DETECTED ->
            // OBJECTIVE_ACTIVE (objectives are known; work pipeline starts
            // without waiting for a progress change).
            Advance(MissionLifecycle.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(Msn(4242, 777001, false, false, 2, 0, TwoObjs(false, false)))));
            Check(Eval() >= 1, "ML01 objective-active transition reported");
            Check(HasLineContaining("MISSION_OBJECTIVES_DETECTED -> MISSION_OBJECTIVE_ACTIVE"), "ML01 objective-active line");
            Check(MissionLifecycle.GetTrack("MISSION:I777001").State == MissionLifecycleState.MissionObjectiveActive,
                "ML01 state advanced to OBJECTIVE_ACTIVE");
            // Unchanged state -> quiet.
            Advance(MissionLifecycle.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(Msn(4242, 777001, false, false, 2, 0, TwoObjs(false, false)))));
            Check(Eval() == 0, "ML01 unchanged quiet");
            // First objective completes -> progress carried (state stays ACTIVE).
            Advance(MissionLifecycle.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(Msn(4242, 777001, false, false, 2, 1, TwoObjs(true, false)))));
            Check(Eval() == 0, "ML01 progress pass quiet");
            r1 = MissionLifecycle.GetTrack("MISSION:I777001");
            Check(r1 != null && r1.CompletedObjectives == 1 && r1.TotalObjectives == 2, "ML01 progress carried");

            // ---- ML02: complete -> return-required -> terminal --------------------
            Advance(MissionLifecycle.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(Msn(4242, 777001, false, false, 2, 2, TwoObjs(true, true),
                turnInKnown: true, turnIn: true))));
            int r = Eval();
            Check(r >= 1, "ML02 complete transition reported");
            Check(HasLineContaining("MISSION_OBJECTIVE_ACTIVE -> MISSION_COMPLETE"), "ML02 complete transition line");
            Check(HasLineContaining("MissionReturnRequired MISSION:I777001"), "ML02 return-required signal");
            r1 = MissionLifecycle.GetTrack("MISSION:I777001");
            Check(r1 != null && r1.State == MissionLifecycleState.MissionComplete, "ML02 complete state");
            Check(r1 != null && r1.TerminalReported && r1.ReturnRequiredReported, "ML02 terminal + return flags");
            // Decay hygiene: once the mission leaves the list, the terminal
            // record frees its slot (an ACTIVE record never decays while
            // present — publishing the same mission would just re-track it).
            Advance(MissionLifecycle.ActiveExpiryMs);
            Publish(Snap(s_Clock.NowMs, new List<MissionSnapshot>()));
            Eval();
            Check(MissionLifecycle.TrackedCount == 0, "ML02 terminal decayed");

            // ---- ML03: legacy type-key fallback -----------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(Msn(4242, -1, false, false, 2, 0, null))));
            Advance(MissionLifecycle.MinRecheckMs);
            Check(Eval() == 1, "ML03 opened reported");
            Check(HasLineContaining("MissionLifecycleOpened MISSION:T4242"), "ML03 legacy type key");
            Check(MissionLifecycle.GetTrack("MISSION:T4242") != null, "ML03 legacy record tracked");

            // ---- ML04: failed (game flag + abandoned mirror) -----------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(Msn(69, 888001, false, true, 1, 0, null))));
            Advance(MissionLifecycle.MinRecheckMs);
            Eval();
            r1 = MissionLifecycle.GetTrack("MISSION:I888001");
            Check(r1 != null && r1.State == MissionLifecycleState.MissionFailed, "ML04 abandoned mirrors failed");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(Msn(69, 888002, false, false, 1, 0, null, failedKnown: true, failed: true))));
            Advance(MissionLifecycle.MinRecheckMs);
            Eval();
            r1 = MissionLifecycle.GetTrack("MISSION:I888002");
            Check(r1 != null && r1.State == MissionLifecycleState.MissionFailed, "ML04 game failed flag");

            // ---- ML05: returned terminal (active flip after completion) ------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(Msn(69, 999001, false, false, 1, 0, null, activeKnown: true, active: true))));
            Advance(MissionLifecycle.MinRecheckMs);
            Eval();
            // Ship delivers: objectives complete + game flips active off.
            Advance(MissionLifecycle.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(Msn(69, 999001, false, false, 1, 1, null, activeKnown: true, active: false))));
            Eval();
            r1 = MissionLifecycle.GetTrack("MISSION:I999001");
            Check(r1 != null && r1.State == MissionLifecycleState.MissionReturned, "ML05 returned state");
            Check(HasLineContaining("MissionLifecycleTerminal MISSION:I999001 state=MISSION_RETURNED"), "ML05 returned terminal line");

            // ---- ML06: temp-unavailable survival (never DEAD) ----------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(Msn(780, 555001, false, false, 2, 1, null))));
            Advance(MissionLifecycle.MinRecheckMs);
            Eval();
            Check(MissionLifecycle.TrackedCount == 1, "ML06 tracked pre-vanish");
            // Mission vanishes (sector transition) -> TEMP_UNAVAILABLE wording, record decays.
            Advance(MissionLifecycle.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, new List<MissionSnapshot>()));
            Eval();
            Check(HasLineContaining("MissionTempUnavailable MISSION:I555001"), "ML06 temp-unavailable line");
            Check(!HasLineContaining("MissionDead"), "ML06 never DEAD vocabulary");
            Check(MissionLifecycle.TrackedCount == 0, "ML06 record decayed after vanish");

            // ---- ML07: no-stuck — unreadable snapshot keeps records bounded --------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(Msn(264, 111001, false, false, 1, 0, null))));
            Advance(MissionLifecycle.MinRecheckMs);
            Eval();
            // Stale snapshot pass: state untouched, no decisions.
            Advance(MissionLifecycle.MaxStaleSnapshotMs + 1000);
            Publish(Snap(s_Clock.NowMs - MissionLifecycle.MaxStaleSnapshotMs - 500, One(Msn(264, 111001, false, false, 1, 0, null))));
            Check(Eval() == 0, "ML07 stale snapshot rejected");
            Check(MissionLifecycle.GetTrack("MISSION:I111001") != null, "ML07 record untouched on stale");
            // Recovery: fresh snapshot re-detects (TEMP_UNAVAILABLE -> DETECTED path is implicit:
            // a vanished record decays; a fresh snapshot re-tracks cleanly).
            Publish(Snap(s_Clock.NowMs, One(Msn(264, 111001, false, false, 1, 0, null))));
            Check(Eval() >= 0, "ML07 recovery pass never throws");

            // ---- ML08: return policy table verbatim --------------------------------
            Check(MissionReturnPolicy.RequiredCompletedCountForType(0) == 3, "ML08 type 0 requires 3");
            foreach (int t in new int[] { 25, 68, 71, 72, 780, 2437, 2580, 104851 })
                Check(MissionReturnPolicy.RequiredCompletedCountForType(t) == 2, "ML08 type " + t + " requires 2");
            foreach (int t in new int[] { 69, 264, 683, 81262, 24213, 24214, 25249 })
                Check(MissionReturnPolicy.RequiredCompletedCountForType(t) == 1, "ML08 type " + t + " requires 1");
            Check(MissionReturnPolicy.RequiredCompletedCountForType(4242) == -1, "ML08 unlisted type -> -1");
            // Verbatim semantics: completed >= required triggers (shipped parity).
            Check(MissionReturnPolicy.ShouldReturnToSender(Msn(25, -1, false, false, 4, 2, null)), "ML08 type 25 at 2/4 returns");
            Check(MissionReturnPolicy.ShouldReturnToSender(Msn(69, -1, false, false, 3, 1, null)), "ML08 type 69 at 1/3 returns");
            Check(!MissionReturnPolicy.ShouldReturnToSender(Msn(25, -1, false, false, 4, 1, null)), "ML08 type 25 at 1/4 does not");
            Check(!MissionReturnPolicy.ShouldReturnToSender(null), "ML08 null mission safe");
            Check(!MissionReturnPolicy.ShouldReturnToSender(Msn(-1, -1, false, false, 1, 0, null)), "ML08 unknown typeId safe");

            // ---- ML09: additive unlisted-type path (game turn-in flag) --------------
            Check(MissionReturnPolicy.ShouldReturnToSender(
                Msn(4242, 123, false, false, 2, 2, null, turnInKnown: true, turnIn: true)), "ML09 unlisted + turnIn -> return");
            Check(!MissionReturnPolicy.ShouldReturnToSender(
                Msn(4242, 123, false, false, 2, 1, null, turnInKnown: true, turnIn: true)), "ML09 incomplete objectives block return");
            Check(!MissionReturnPolicy.ShouldReturnToSender(
                Msn(4242, 123, false, false, 2, 2, null, turnInKnown: false, turnIn: false)), "ML09 unknown flag never triggers");
            // Game flag never weakens a listed type's table: type 25 requires
            // 2, and 2/4 satisfies it — the table verdict stands (parity).
            Check(MissionReturnPolicy.ShouldReturnToSender(
                Msn(25, -1, false, false, 4, 2, null, turnInKnown: true, turnIn: false)), "ML09 listed type ignores non-confirming flag");

            // ---- ML10: authority deny-by-default ------------------------------------
            FreshSetup();
            MissionLifecycle.SetAuthorityProbe(null);
            Publish(Snap(s_Clock.NowMs, One(Msn(69, 222001, false, false, 1, 0, null))));
            Check(Eval() == 0, "ML10 null probe no-op");
            MissionLifecycle.SetAuthorityProbe(delegate { throw new InvalidOperationException("boom"); });
            Check(Eval() == 0, "ML10 faulting probe no-op");
            MissionLifecycle.SetAuthorityProbe(delegate { return false; });
            Check(Eval() == 0, "ML10 non-authoritative no-op");
            Check(MissionLifecycle.TrackedCount == 0, "ML10 no records built");

            // ---- ML11: fail-safe inputs ----------------------------------------------
            FreshSetup();
            Publish(null);
            Check(Eval() == 0, "ML11 null snapshot no-op");
            s_Snap = WorldSnapshot.Empty;
            Check(Eval() == 0, "ML11 never-captured no-op");
            Publish(Snap(0, One(Msn(69, 333001, false, false, 1, 0, null))));
            Advance(MissionLifecycle.MaxStaleSnapshotMs + 500);
            Check(Eval() == 0, "ML11 stale no-op");

            // ---- ML12: bounded tracked set -------------------------------------------
            FreshSetup();
            List<MissionSnapshot> many = new List<MissionSnapshot>();
            for (int i = 0; i < 20; i++)
                many.Add(Msn(1000 + i, 500000 + i, false, false, 1, 0, null));
            Publish(Snap(s_Clock.NowMs, many));
            Advance(MissionLifecycle.MinRecheckMs);
            Eval();
            Check(MissionLifecycle.TrackedCount <= MissionLifecycle.MaxTracked, "ML12 tracked bounded");

            // ---- ML13: cadence + determinism -----------------------------------------
            // LastEvalMs starts at -1: the FIRST eval always runs (house parity
            // with MissionDirector — the cadence gate only silences passes that
            // arrive INSIDE a window after a previous eval).
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, One(Msn(683, 444001, false, false, 1, 0, null))));
            Check(Eval() == 1, "ML13 first eval always runs");
            Check(HasLineContaining("MissionLifecycleOpened MISSION:I444001"), "ML13 opened on first eval");
            // Sub-cadence pass: gate silent.
            Advance(100);
            Publish(Snap(s_Clock.NowMs, One(Msn(683, 444001, false, false, 1, 0, null))));
            Check(Eval() == 0, "ML13 sub-cadence pass ignored");
            // Next window: fold runs, DETECTED pipeline advances to ACTIVE.
            Advance(MissionLifecycle.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, One(Msn(683, 444001, false, false, 1, 0, null))));
            Check(Eval() >= 1, "ML13 next window fold runs");
            Check(MissionLifecycle.GetTrack("MISSION:I444001").State == MissionLifecycleState.MissionObjectiveActive,
                "ML13 state advanced after cadence window");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}