// Dev-side unit tests for the Phase 17 combat director (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure combat domain (CombatDirector) plus the domains it
// builds on. Time is virtual: every timestamp is an explicit nowMs argument —
// no real clock reads, no sleeping.
//
// Covers the Phase 17 mandated scenarios:
//   CS01 engagement episode end-to-end (open -> dwell report -> repeat quiet)
//   CS02 composition change re-arms engagement (fresh dwell)
//   CS03 episode close (hostiles clear -> HostileClearedReport; re-entry re-arms)
//   CS04 combat-level gap label (unfavorable vs -, NaN levels = "-")
//   CS05 warp-combat picture (hostiles while player ship in warp; dwell)
//   CS06 under-fire report (player ship TookDamageRecently while hostiles)
//   CS07 boarder report (InvadersOnboardCount > 0; -1 sentinel silent)
//   CS08 quiet paths (no hostiles = quiet pass, NOT unknown; boarders still run)
//   CS09 fail-safe inputs (null/stale/not-started/future)
//   CS10 authority deny-by-default (null/faulting/non-master = no-op)
//   CS11 vanished report + expiry hygiene + bounded history
//       (fresh-publish-after-advance discipline)
//   CS12 cadence + counters + StatusLines/GetTrack determinism
//   CS13 additive capture regression (ShipSnapshot/ThreatSnapshot Phase 17
//       ctors; original ctors still compile + default the new fields)
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Combat;
using CapBot.Core.Economy;
using CapBot.Core.Missions;
using CapBot.Core.Navigation;
using CapBot.Core.Emergency;

namespace CapBot.TaskTests
{
    internal static class CombatTests
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
            CombatDirector.ResetForTests();
            EconomyDirector.ResetForTests();
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
            CombatDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CombatDirector.SetWorldProvider(delegate { return s_Snap; });
            CombatDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CombatDirector.SetAuthorityProbe(delegate { return true; });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int Eval() { return CombatDirector.Evaluate(s_Clock.NowMs); }

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

        private static ShipSnapshot Ship(int id, string name, bool isPlayer, bool hostile, bool tookDamage)
        {
            // Phase 17 ctor shape (13 args, includes TookDamageRecently).
            return new ShipSnapshot(id, name, isPlayer, isPlayer ? 0 : 1, hostile,
                0.9f, 0.5f, false, 0, -1, 0, 10f, tookDamage);
        }

        // Threats with a specific hostile id list + combat levels + boarders.
        private static ThreatSnapshot Threat(int[] hostileIds, float ourLevel, float targetLevel, int invaders)
        {
            List<int> ids = new List<int>();
            if (hostileIds != null) { foreach (int id in hostileIds) ids.Add(id); }
            return new ThreatSnapshot(ids, 0, 0, 0, -1, targetLevel, ourLevel, invaders);
        }

        private static NavigationSnapshot NavInWarp(bool inWarp)
        {
            return new NavigationSnapshot(5, "Sector 5", 0, inWarp, -1,
                new List<int>(), true, 50f, float.NaN, 0f, false);
        }

        private static CrewMemberSnapshot OneCrew()
        {
            return new CrewMemberSnapshot(1, "bot1", true, 0, 0, true, true, 0.9f, "Bridge", true, 3);
        }

        // Phase 9-ctor snapshot with Phase 17 threat/ship fields + custom nav.
        private static WorldSnapshot Snap(int timeMs, ThreatSnapshot threat, bool playerInWarp)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(Ship(1, "player", true, false, false));
            foreach (int id in threat_hostileIds)
            {
                if (ships.Count >= WorldSnapshot.MaxShips) break;
                ships.Add(Ship(id, "h" + id.ToString(System.Globalization.CultureInfo.InvariantCulture), false, true, false));
            }
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(OneCrew());
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                threat, NavInWarp(playerInWarp),
                new EconomyResource().R(),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // Hostile ids of the CURRENT threat snapshot (so Snap() can build the
        // matching hostile ships). Set by Threat().
        private static int[] threat_hostileIds = new int[0];
        private static ThreatSnapshot Threat2(int[] hostileIds, float ourLevel, float targetLevel, int invaders)
        {
            threat_hostileIds = hostileIds ?? new int[0];
            return Threat(hostileIds, ourLevel, targetLevel, invaders);
        }

        // Baseline readable snapshot: 1 hostile (id 9), no boarders, no warp,
        // combat levels 10/12 (no unfavorable gap).
        private static WorldSnapshot Baseline(int timeMs)
        {
            return Snap(timeMs, Threat2(new int[] { 9 }, 10f, 12f, 0), false);
        }

        internal static int Run()
        {
            // ---- CS01: engagement episode end-to-end ------------------------------
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 1, "CS01 opened reported");
            Check(HasLineContaining("CombatOpened COMBAT:ENGAGEMENT"), "CS01 opened line");
            Check(CombatDirector.ActiveRecordCount == 1, "CS01 tracked");
            CombatDirector.CombatRecord r1 = CombatDirector.GetTrack("COMBAT:ENGAGEMENT");
            Check(r1 != null && r1.OpenedReported && r1.Kind == "ENGAGEMENT", "CS01 record fields");
            Check(CombatDirector.OpenedReportCount == 1, "CS01 opened counted");
            // Dwell below the gate: quiet (opened already fired; engagement not yet).
            Advance(CombatDirector.EngagementDwellMs - CombatDirector.MinRecheckMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 0, "CS01 below-dwell quiet");
            // Cross the dwell gate: engagement report.
            Advance(CombatDirector.MinRecheckMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 1, "CS01 dwell reported");
            Check(HasLineContaining("HostileEngagementReport hostiles=1 ourLevel=10 targetLevel=12 gap=- hull=0.9"), "CS01 engagement line");
            Check(CombatDirector.EngagementReportCount == 1, "CS01 engagement counted");
            // Same stable picture again: quiet (episode reported).
            Advance(CombatDirector.MinRecheckMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 0, "CS01 stable repeat quiet");

            // ---- CS02: composition change re-arms ---------------------------------
            // Second hostile joins: re-arms the engagement report (fresh dwell).
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9, 11 }, 10f, 12f, 0), false));
            Check(Eval() == 0, "CS02 composition change below-dwell quiet");
            Advance(CombatDirector.EngagementDwellMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9, 11 }, 10f, 12f, 0), false));
            Check(Eval() == 1, "CS02 re-armed engagement reports after fresh dwell");
            Check(HasLineContaining("HostileEngagementReport hostiles=2"), "CS02 two hostiles line");
            Check(CombatDirector.EngagementReportCount == 2, "CS02 engagement counted twice");

            // ---- CS03: episode close + re-entry ---------------------------------------------
            // Hostiles clear: one-shot cleared report.
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(null, 10f, 12f, 0), false));
            Check(Eval() == 1, "CS03 cleared reported");
            Check(HasLineContaining("HostileClearedReport"), "CS03 cleared line");
            Check(CombatDirector.ClearedReportCount == 1, "CS03 cleared counted");
            Check(CombatDirector.ActiveRecordCount == 1, "CS03 record persists (hygiene owns expiry)");
            // Re-entry while the record is still live: episode re-opens quietly
            // (CombatOpened is record-creation vocabulary — it re-emits only
            // after the record decays; the stable-picture dwell still restarts).
            Advance(CombatDirector.MinRecheckMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 0, "CS03 re-entry into live record quiet");
            Check(CountLinesContaining("CombatOpened") == 1, "CS03 opened emitted once per record");
            // The re-entry episode still dwells into a fresh engagement report.
            Advance(CombatDirector.EngagementDwellMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 1, "CS03 re-entry episode dwells into engagement report");
            Check(CombatDirector.EngagementReportCount == 2, "CS03 engagement counted twice");

            // ---- CS04: combat-level gap label (data only) ---------------------------
            FreshSetup();
            // Outmatched: target > ours * 1.33.
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 14f, 0), false));
            Advance(CombatDirector.MinRecheckMs);
            Eval();
            Advance(CombatDirector.EngagementDwellMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 14f, 0), false));
            Check(Eval() == 1 && HasLineContaining("gap=unfavorable"), "CS04 unfavorable gap labeled");
            // Gap below the threshold: "-".
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(CombatDirector.MinRecheckMs);
            Eval();
            Advance(CombatDirector.EngagementDwellMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 1 && HasLineContaining("gap=-"), "CS04 sub-threshold gap dash");
            // NaN levels: never trigger the label, report as "-".
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, float.NaN, float.NaN, 0), false));
            Advance(CombatDirector.MinRecheckMs);
            Eval();
            Advance(CombatDirector.EngagementDwellMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, float.NaN, float.NaN, 0), false));
            Check(Eval() == 1 && HasLineContaining("ourLevel=- targetLevel=- gap=-"), "CS04 NaN levels dash");

            // ---- CS05: warp-combat picture --------------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 0), true));
            Advance(CombatDirector.MinRecheckMs);
            Eval();
            Advance(CombatDirector.EngagementDwellMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 0), true));
            Check(Eval() == 2, "CS05 engagement + warp picture co-fire");
            Check(HasLineContaining("WarpEngagementReport hostiles=1"), "CS05 warp line");
            Check(CombatDirector.WarpPictureReportCount == 1, "CS05 warp counted");
            // Repeat: quiet (both reported).
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 0), true));
            Check(Eval() == 0, "CS05 repeat quiet");
            // Warp exits: episode stays open (hostiles remain), no cleared report.
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 0), false));
            Check(Eval() == 0, "CS05 warp exit quiet (episode open)");

            // ---- CS06: under-fire report -------------------------------------------------
            // Player ship took damage recently while hostiles present.
            FreshSetup();
            {
                ThreatSnapshot t = Threat2(new int[] { 9 }, 10f, 12f, 0);
                List<ShipSnapshot> ships = new List<ShipSnapshot>();
                ships.Add(new ShipSnapshot(1, "player", true, 0, false, 0.9f, 0.5f, false, 0, -1, 0, 10f, true));
                ships.Add(Ship(9, "h9", false, true, false));
                List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
                crew.Add(OneCrew());
                s_Snap = new WorldSnapshot(
                    s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                    ships, crew, new List<MissionSnapshot>(),
                    t, NavInWarp(false),
                    new EconomyResource().R(),
                    new List<WorldObjectSnapshot>(),
                    WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                    -1, float.NaN);
            }
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 2, "CS06 opened + under-fire co-fire");
            Check(HasLineContaining("UnderFireReport"), "CS06 under-fire line");
            Check(CombatDirector.UnderFireReportCount == 1, "CS06 under-fire counted");
            // Repeat (condition persists): quiet.
            Advance(CombatDirector.MinRecheckMs);
            Publish(s_Snap = CloneWithFreshTime(s_Snap, s_Clock.NowMs));
            Check(Eval() == 0, "CS06 repeat quiet");
            // Condition clears (took damage flag false): re-arms; firing again reports again.
            {
                ThreatSnapshot t = Threat2(new int[] { 9 }, 10f, 12f, 0);
                List<ShipSnapshot> ships = new List<ShipSnapshot>();
                ships.Add(new ShipSnapshot(1, "player", true, 0, false, 0.9f, 0.5f, false, 0, -1, 0, 10f, false));
                ships.Add(Ship(9, "h9", false, true, false));
                List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
                crew.Add(OneCrew());
                s_Snap = new WorldSnapshot(
                    s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                    ships, crew, new List<MissionSnapshot>(),
                    t, NavInWarp(false),
                    new EconomyResource().R(),
                    new List<WorldObjectSnapshot>(),
                    WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                    -1, float.NaN);
            }
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS06 condition-cleared quiet (re-arms)");
            {
                ThreatSnapshot t = Threat2(new int[] { 9 }, 10f, 12f, 0);
                List<ShipSnapshot> ships = new List<ShipSnapshot>();
                ships.Add(new ShipSnapshot(1, "player", true, 0, false, 0.9f, 0.5f, false, 0, -1, 0, 10f, true));
                ships.Add(Ship(9, "h9", false, true, false));
                List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
                crew.Add(OneCrew());
                s_Snap = new WorldSnapshot(
                    s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                    ships, crew, new List<MissionSnapshot>(),
                    t, NavInWarp(false),
                    new EconomyResource().R(),
                    new List<WorldObjectSnapshot>(),
                    WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                    -1, float.NaN);
            }
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 1, "CS06 second episode after re-arm");
            Check(CombatDirector.UnderFireReportCount == 2, "CS06 under-fire counted twice");

            // ---- CS07: boarder report -------------------------------------------------------
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(CombatDirector.MinRecheckMs);
            Eval();
            // Boarders arrive (with hostiles present): one report.
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 2), false));
            Check(Eval() == 1, "CS07 boarders reported");
            Check(HasLineContaining("BoarderReport boarders=2"), "CS07 boarders line");
            Check(CombatDirector.BoarderReportCount == 1, "CS07 boarders counted");
            // Repeat: quiet.
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 2), false));
            Check(Eval() == 0, "CS07 repeat quiet");
            // Boarders clear: re-arms.
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 0), false));
            Check(Eval() == 0, "CS07 boarders-cleared quiet (re-arms)");
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 1), false));
            Check(Eval() == 1, "CS07 second episode after re-arm");
            Check(CombatDirector.BoarderReportCount == 2, "CS07 counted twice");
            // Unknown sentinel (-1): never triggers, counted as unknown input.
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, -1), false));
            Advance(CombatDirector.MinRecheckMs);
            Eval();
            Advance(CombatDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, -1), false));
            Check(Eval() == 0, "CS07 unknown boarders silent");
            Check(CombatDirector.UnknownInputPassCount == 2, "CS07 unknown passes counted");
            // Boarders WITHOUT hostiles: still reports (rule is independent).
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, Threat2(null, 10f, 12f, 3), false));
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 1, "CS07 boarders without hostiles fire");
            Check(HasLineContaining("BoarderReport boarders=3"), "CS07 independent line");

            // ---- CS08: quiet paths (no hostiles = legitimate quiet) ----------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, Threat2(null, float.NaN, float.NaN, 0), false));
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS08 no hostiles = quiet (not unknown)");
            Check(CombatDirector.UnknownInputPassCount == 0, "CS08 no unknown accounting");
            Check(CombatDirector.ActiveRecordCount == 0, "CS08 nothing tracked");

            // ---- CS09: fail-safe inputs -------------------------------------------------------
            FreshSetup();
            Publish(null);
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS09 no snapshot = no-op");
            Check(HasLineContaining("no world snapshot"), "CS09 uncertainty recorded");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs - CombatDirector.MaxStaleSnapshotMs - 1, Threat2(new int[] { 9 }, 10f, 12f, 0), false));
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS09 stale snapshot rejected");
            Check(CombatDirector.StaleRejectionCount == 1, "CS09 stale counted");
            FreshSetup();
            {
                ThreatSnapshot t = Threat2(new int[] { 9 }, 10f, 12f, 0);
                List<ShipSnapshot> ships = new List<ShipSnapshot>();
                ships.Add(Ship(1, "player", true, false, false));
                ships.Add(Ship(9, "h9", false, true, false));
                List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
                crew.Add(OneCrew());
                s_Snap = new WorldSnapshot(
                    s_Clock.NowMs, false, true, 7, WorldAuthority.MasterDerived,
                    ships, crew, new List<MissionSnapshot>(),
                    t, NavInWarp(false),
                    new EconomyResource().R(),
                    new List<WorldObjectSnapshot>(),
                    WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                    -1, float.NaN);
            }
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS09 game-not-started rejected");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs + CombatDirector.MaxStaleSnapshotMs + 1, Threat2(new int[] { 9 }, 10f, 12f, 0), false));
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS09 future snapshot rejected");

            // ---- CS10: authority deny-by-default --------------------------------------------
            CombatDirector.ResetForTests();
            s_Lines.Clear();
            CombatDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CombatDirector.SetWorldProvider(delegate { return s_Snap; });
            CombatDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CombatDirector.SetAuthorityProbe((Func<bool>)null);
            Publish(Snap(s_Clock.NowMs, Threat2(new int[] { 9 }, 10f, 12f, 0), false));
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS10 null probe no-op");
            CombatDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("probe fault"); });
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS10 faulting probe no-op");
            CombatDirector.SetAuthorityProbe(delegate { return false; });
            Advance(CombatDirector.MinRecheckMs);
            Check(Eval() == 0, "CS10 non-authoritative no-op");
            Check(CombatDirector.EvaluationCount == 0, "CS10 no evaluations ran");
            CombatDirector.SetAuthorityProbe(delegate { return true; });

            // ---- CS11: vanished + expiry hygiene + bounded history ---------------------------
            // Hostiles present, then unreadable (hostile section empty) past
            // ActiveExpiryMs -> record decays into bounded history. The
            // publish-after-advance discipline keeps the snapshot FRESH at
            // eval time (recurring stale-gate lesson).
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(CombatDirector.MinRecheckMs);
            Eval();
            Check(CombatDirector.ActiveRecordCount == 1, "CS11 setup tracked");
            // Hostiles clear; advance past expiry BEFORE publishing fresh.
            Advance(CombatDirector.ActiveExpiryMs);
            Publish(Snap(s_Clock.NowMs, Threat2(null, 10f, 12f, 0), false));
            Check(Eval() == 1, "CS11 cleared reported on the close pass");
            Check(CombatDirector.VanishedReportCount == 1, "CS11 vanished reported");
            Check(CombatDirector.ActiveRecordCount == 0, "CS11 record decayed to history");
            Check(CombatDirector.HistoryCount == 1, "CS11 history holds the record");
            // Re-open after expiry: a fresh episode (new record).
            Advance(CombatDirector.MinRecheckMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 1, "CS11 re-open reports");
            Check(CombatDirector.RecordsTrackedCount == 2, "CS11 second record tracked");
            // Bounded history (<= MaxHistory): churn episodes with fresh
            // publishes AFTER each advance.
            for (int i = 0; i < 25; i++)
            {
                Advance(CombatDirector.ActiveExpiryMs);
                Publish(Snap(s_Clock.NowMs, Threat2(null, 10f, 12f, 0), false));
                Eval();
                Advance(CombatDirector.MinRecheckMs);
                Publish(Baseline(s_Clock.NowMs));
                Eval();
            }
            Check(CombatDirector.HistoryCount <= CombatDirector.MaxHistory, "CS11 history bounded");
            Check(CombatDirector.HistoryCount == CombatDirector.MaxHistory, "CS11 history saturated at cap");

            // ---- CS12: cadence + counters + diagnostics ---------------------------------------
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(CombatDirector.MinRecheckMs);
            Eval();
            int evalsAfterFirst = (int)CombatDirector.EvaluationCount;
            // Sub-cadence re-eval: no-op, not counted.
            s_Clock.NowMs += CombatDirector.MinRecheckMs - 1;
            Check(Eval() == 0, "CS12 sub-cadence no-op");
            Check((int)CombatDirector.EvaluationCount == evalsAfterFirst, "CS12 sub-cadence not counted");
            Advance(CombatDirector.MinRecheckMs);
            Publish(Baseline(s_Clock.NowMs));
            Eval();
            Check(CombatDirector.EvaluationCount == evalsAfterFirst + 1, "CS12 cadence pass counted");
            List<string> status = CombatDirector.StatusLines();
            Check(status.Count == 2 && status[0].StartsWith("combat=", StringComparison.Ordinal)
                && status[1].Contains("uncertain="), "CS12 status lines shape");
            List<string> lines = CombatDirector.Lines();
            Check(lines.Count == 1 && lines[0].Contains("track COMBAT:ENGAGEMENT"), "CS12 lines determinism");
            Check(CombatDirector.GetTrack(null) == null, "CS12 GetTrack null-safe");

            // ---- CS13: additive capture regression --------------------------------------------
            // Phase 17 ctors exist and the ORIGINAL ctors still default the new
            // fields (P9/P16 additive-ctor pattern preserved verbatim).
            ShipSnapshot legacyShip = new ShipSnapshot(1, "s", false, 1, true, 1f, 1f, false, 0, -1, 0, 5f);
            Check(!legacyShip.TookDamageRecently, "CS13 legacy ShipSnapshot ctor defaults flag false");
            ShipSnapshot p17Ship = new ShipSnapshot(2, "s2", true, 0, false, 1f, 1f, false, 0, -1, 0, 5f, true);
            Check(p17Ship.TookDamageRecently, "CS13 Phase 17 ShipSnapshot ctor carries flag");
            ThreatSnapshot legacyThreat = new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN);
            Check(legacyThreat.InvadersOnboardCount == -1, "CS13 legacy ThreatSnapshot ctor defaults boarders -1");
            ThreatSnapshot p17Threat = new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN, 4);
            Check(p17Threat.InvadersOnboardCount == 4, "CS13 Phase 17 ThreatSnapshot ctor carries boarders");
            ThreatSnapshot negativeBoarders = new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN, -5);
            Check(negativeBoarders.InvadersOnboardCount == -1, "CS13 negative boarders normalize to -1");

            return 0;
        }

        // Rebuild a snapshot identical to the given one but with a fresh time
        // (publish-after-advance discipline helper).
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

        // Minimal readable ResourceSnapshot (Phase 16 7-arg ctor; economy
        // fields are irrelevant to combat tests but must be readable so the
        // combat rules run in a fully readable snapshot).
        private sealed class EconomyResource
        {
            public ResourceSnapshot R()
            {
                return new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000);
            }
        }
    }
}