// Dev-side unit tests for the Phase 16 economy director (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure economy domain (EconomyDirector) plus the domains it
// builds on. Time is virtual: every timestamp is an explicit nowMs argument —
// no real clock reads, no sleeping.
//
// Covers the Phase 16 mandated scenarios:
//   ES01 credits tracking end-to-end (open -> low edge -> recovery re-arm)
//   ES02 delta report (large positive + negative; small deltas suppressed)
//   ES03 store-sector report (dwell-gated, once per sector episode)
//   ES04 store exit resets episode (re-entry re-arms)
//   ES05 fuel affordability (low supply + unaffordable -> one report; re-arm)
//   ES06 coolant affordability (same shape as fuel)
//   ES07 affordability quiet paths (unknown prices / unknown fuel sentinel)
//   ES08 warp-toll report (dwell-gated, once per toll episode; sentinels)
//   ES09 fail-safe inputs (null/stale/not-started/future/unknown sentinels)
//   ES10 authority deny-by-default (null/faulting/non-master = no-op)
//   ES11 vanished report + expiry hygiene + bounded history
//   ES12 shop classifier seam (null/faulting = never a shop; deny-by-default)
//   ES13 cadence + counters + diagnostics determinism
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Economy;
using CapBot.Core.Missions;
using CapBot.Core.Navigation;
using CapBot.Core.Emergency;

namespace CapBot.TaskTests
{
    internal static class EconomyTests
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
            EconomyDirector.ResetForTests();
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
            EconomyDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            EconomyDirector.SetWorldProvider(delegate { return s_Snap; });
            EconomyDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            EconomyDirector.SetAuthorityProbe(delegate { return true; });
            // Production-shape classifier (mirrors the Mod.cs wiring): raw
            // visual indication 1 marks a shop-class sector. Scenarios that
            // exercise the seam's deny-by-default override it explicitly.
            EconomyDirector.SetShopSectorClassifier(delegate (int v) { return v == 1; });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int Eval() { return EconomyDirector.Evaluate(s_Clock.NowMs); }

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

        // NavigationSnapshot with a specific sector + visual indication.
        private static NavigationSnapshot NavIn(int sectorId, int visualIndication)
        {
            return new NavigationSnapshot(sectorId, "Sector " + sectorId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                visualIndication, false, -1,
                new List<int>(), true, 50f, float.NaN, 0f, false);
        }

        private static WorldObjectSnapshot Station(int price)
        {
            return new WorldObjectSnapshot("WARP_STATION", -1, "gate", -1, price);
        }

        // Phase 9-ctor snapshot with Phase 16 resource prices + custom nav.
        private static WorldSnapshot Snap(
            int timeMs, int credits, int fuel, float coolant, int fuelPrice, int coolantPrice,
            NavigationSnapshot nav, List<WorldObjectSnapshot> objects)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(Ship());
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(new CrewMemberSnapshot(1, "bot1", true, 0, 0, true, true, 0.9f, "Bridge", true, 3));
            List<WorldObjectSnapshot> objs = objects ?? new List<WorldObjectSnapshot>();
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                QuietThreat(), nav,
                new ResourceSnapshot(credits, null, -1, fuel, coolant, fuelPrice, coolantPrice),
                objs,
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // Baseline readable snapshot: credits 5000, fuel 10, coolant 90%,
        // prices 6000/6000 (above credits so affordability edges are testable),
        // non-shop sector 5, no stations.
        private static WorldSnapshot Baseline(int timeMs)
        {
            return Snap(timeMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), null);
        }

        internal static int Run()
        {
            // ---- ES01: credits tracking end-to-end --------------------------------
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(EconomyDirector.MinRecheckMs);
            Check(Eval() == 1, "ES01 opened reported");
            Check(HasLineContaining("EconomyOpened ECONOMY:CREDITS"), "ES01 opened line");
            Check(EconomyDirector.ActiveRecordCount == 1, "ES01 tracked");
            EconomyDirector.EconomyRecord r1 = EconomyDirector.GetTrack("ECONOMY:CREDITS");
            Check(r1 != null && r1.OpenedReported && r1.Kind == "CREDITS", "ES01 record fields");
            Check(EconomyDirector.OpenedReportCount == 1, "ES01 opened counted");
            // Same state again: quiet.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 0, "ES01 unchanged state quiet");
            // Low edge: credits <= ReserveFloor fires once (delta -4000 suppressed).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 1000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES01 low edge reported");
            Check(HasLineContaining("CreditsLowReport credits=1000 reserve=2500"), "ES01 low line");
            Check(EconomyDirector.LowReportCount == 1, "ES01 low counted");
            // Still low: quiet (episode open).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 900, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES01 still-low quiet");
            Check(EconomyDirector.LowReportCount == 1, "ES01 one report per episode");
            // Recovery above the band re-arms; falling low again reports again.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 4000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES01 recovery quiet (re-arms)");
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 2500, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES01 second low after re-arm");
            Check(EconomyDirector.LowReportCount == 2, "ES01 low counted twice across episodes");

            // ---- ES02: delta report ------------------------------------------------
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            // Small delta: suppressed (bounded bookkeeping only).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5100, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES02 small delta quiet");
            Check(EconomyDirector.DeltaReportCount == 0, "ES02 no delta report");
            // Large positive delta.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 20000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES02 large positive delta reported");
            Check(HasLineContaining("CreditsDeltaReport delta=14900 credits=20000"), "ES02 positive line");
            // Large negative delta.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 3000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES02 large negative delta reported");
            Check(HasLineContaining("CreditsDeltaReport delta=-17000 credits=3000"), "ES02 negative line");
            Check(EconomyDirector.DeltaReportCount == 2, "ES02 delta counted twice");
            // A moderate delta that also crosses the reserve band: the LOW edge
            // fires (delta itself stays suppressed — below the floor).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 100, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES02 moderate delta quiet, low edge fires");
            Check(HasLineContaining("CreditsLowReport credits=100"), "ES02 low edge co-fired");

            // ---- ES03: store-sector report -------------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            // Dwell below the gate: quiet.
            Advance(EconomyDirector.StoreDwellMs - EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Check(Eval() == 0, "ES03 below-dwell quiet");
            // Cross the dwell gate: one report.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Check(Eval() == 1, "ES03 dwell reported");
            Check(HasLineContaining("StoreSectorReport sector=7"), "ES03 store line");
            Check(EconomyDirector.StoreReportCount == 1, "ES03 store counted");
            // Same sector, repeat pass: quiet (episode reported).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Check(Eval() == 0, "ES03 repeat quiet");
            Check(EconomyDirector.StoreReportCount == 1, "ES03 one report per episode");

            // ---- ES04: store exit resets episode ---------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.StoreDwellMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Eval();
            Check(EconomyDirector.StoreReportCount == 1, "ES04 setup reported");
            // Leave the shop sector: episode resets.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(8, -1), null));
            Eval();
            // Re-enter a DIFFERENT shop sector: new episode (first sighting,
            // no report yet), then the dwell gate passes and it reports again.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(9, 1), null));
            Eval();
            Advance(EconomyDirector.StoreDwellMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(9, 1), null));
            Check(Eval() == 1, "ES04 new sector episode reports");
            Check(EconomyDirector.StoreReportCount == 2, "ES04 two store reports");
            // Same-sector re-entry after an exit also re-arms (episode closed
            // on exit; fresh dwell required).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(10, -1), null));
            Eval();
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Eval();
            Advance(EconomyDirector.StoreDwellMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Check(Eval() == 1, "ES04 same-sector re-entry re-arms");
            Check(EconomyDirector.StoreReportCount == 3, "ES04 three store reports");

            // ---- ES05: fuel affordability --------------------------------------------
            // Prices (6000) deliberately above credits (5000) so the
            // affordability edge is reachable; reserve band never interferes.
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Check(Eval() == 1, "ES05 opened (only report on healthy supply)");
            Check(!HasLineContaining("FuelAffordabilityReport"), "ES05 healthy supply quiet");
            // Low supply + cannot afford: one report.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 2, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES05 unaffordable reported");
            Check(HasLineContaining("FuelAffordabilityReport fuel=2 price=6000 credits=5000"), "ES05 line");
            Check(EconomyDirector.FuelAffordabilityReportCount == 1, "ES05 counted");
            // Repeat unaffordable pass: quiet (episode open).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 2, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES05 repeat quiet");
            Check(EconomyDirector.FuelAffordabilityReportCount == 1, "ES05 one per episode");
            // Supply recovers: episode closes.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES05 supply-recovery quiet (re-arms)");
            // Unaffordable again: reports again.
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 2, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES05 second episode after re-arm");
            Check(EconomyDirector.FuelAffordabilityReportCount == 2, "ES05 counted twice");
            // Boundary: credits == price is AFFORDABLE (strict <).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 6000, 2, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES05 credits==price is affordable");

            // ---- ES06: coolant affordability -------------------------------------------
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 20f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES06 unaffordable reported");
            Check(HasLineContaining("CoolantAffordabilityReport price=6000 credits=5000"), "ES06 line");
            Check(EconomyDirector.CoolantAffordabilityReportCount == 1, "ES06 counted");
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 20f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES06 repeat quiet");
            // Healthy coolant: never triggers.
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES06 healthy coolant quiet");
            // NaN coolant sentinel: never triggers.
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, 5000, 10, float.NaN, 6000, 6000, NavIn(5, -1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, float.NaN, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES06 NaN coolant never triggers");

            // ---- ES07: affordability quiet paths ------------------------------------------
            // Prices unknown (-1): affordability rules stay silent (counted as
            // unknown-input passes); credits + store rules still run (visual
            // indication 0 = readable non-shop sector, so ONLY the price branch
            // counts unknowns).
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, 5000, 2, 20f, -1, -1, NavIn(5, 0), null));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 2, 20f, -1, -1, NavIn(5, 0), null));
            Check(Eval() == 0, "ES07 unknown prices silent");
            Check(EconomyDirector.FuelAffordabilityReportCount == 0, "ES07 no fuel report");
            Check(EconomyDirector.CoolantAffordabilityReportCount == 0, "ES07 no coolant report");
            Check(EconomyDirector.UnknownInputPassCount == 2, "ES07 unknown passes counted");
            // Unknown fuel sentinel (-1): fuel rule silent, coolant rule fires.
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, 5000, -1, 20f, 6000, 6000, NavIn(5, -1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Check(Eval() == 2, "ES07 opened + coolant report on first pass");
            Check(CountLinesContaining("FuelAffordabilityReport") == 0, "ES07 unknown fuel silent");
            Check(CountLinesContaining("CoolantAffordabilityReport") == 1, "ES07 coolant fired");
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, -1, 20f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES07 second pass quiet (episodes open)");

            // ---- ES08: warp-toll report ------------------------------------------------------
            FreshSetup();
            List<WorldObjectSnapshot> objs = new List<WorldObjectSnapshot>();
            objs.Add(Station(99999));
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.WarpTollDwellMs - EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Check(Eval() == 0, "ES08 below-dwell quiet");
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Check(Eval() == 1, "ES08 dwell reported");
            Check(HasLineContaining("WarpTollReport price=99999 credits=5000"), "ES08 line");
            Check(EconomyDirector.WarpTollReportCount == 1, "ES08 counted");
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Check(Eval() == 0, "ES08 repeat quiet");
            // Affordable toll (credits >= price): never triggers.
            FreshSetup();
            objs = new List<WorldObjectSnapshot>();
            objs.Add(Station(5000));
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.WarpTollDwellMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Check(Eval() == 0, "ES08 affordable toll quiet");
            // Sentinel prices (<= 0): never trigger.
            FreshSetup();
            objs = new List<WorldObjectSnapshot>();
            objs.Add(Station(0));
            objs.Add(Station(-1));
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.WarpTollDwellMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Check(Eval() == 0, "ES08 sentinel prices never trigger");
            // Cheapest unaffordable toll wins (deterministic pick).
            FreshSetup();
            objs = new List<WorldObjectSnapshot>();
            objs.Add(Station(80000));
            objs.Add(Station(60000));
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.WarpTollDwellMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), objs));
            Check(Eval() == 1 && HasLineContaining("WarpTollReport price=60000"), "ES08 cheapest unaffordable wins");

            // ---- ES09: fail-safe inputs ---------------------------------------------------------
            FreshSetup();
            Publish(null);
            Advance(EconomyDirector.MinRecheckMs);
            Check(Eval() == 0, "ES09 no snapshot = no-op");
            Check(HasLineContaining("no world snapshot"), "ES09 uncertainty recorded");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs - EconomyDirector.MaxStaleSnapshotMs - 1, 5000, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Check(Eval() == 0, "ES09 stale snapshot rejected");
            Check(EconomyDirector.StaleRejectionCount == 1, "ES09 stale counted");
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(EconomyDirector.MinRecheckMs);
            s_Snap = new WorldSnapshot(
                s_Clock.NowMs, false, true, 7, WorldAuthority.MasterDerived,
                new List<ShipSnapshot> { Ship() }, new List<CrewMemberSnapshot>(), new List<MissionSnapshot>(),
                QuietThreat(), NavIn(5, -1),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
            Check(Eval() == 0, "ES09 game-not-started rejected");
            // Unknown-credits sentinel: rules silent, unknown pass counted.
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, -1, 2, 20f, 6000, 6000, NavIn(5, -1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, -1, 2, 20f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 0, "ES09 unknown credits silent");
            Check(EconomyDirector.UnknownInputPassCount == 2, "ES09 unknown passes counted");
            Check(CountLinesContaining("CreditsLowReport") == 0, "ES09 no low report on unknown");
            // Future-dated snapshot rejected.
            FreshSetup();
            s_Snap = new WorldSnapshot(
                s_Clock.NowMs + 60000, true, true, 7, WorldAuthority.MasterDerived,
                new List<ShipSnapshot> { Ship() }, new List<CrewMemberSnapshot>(), new List<MissionSnapshot>(),
                QuietThreat(), NavIn(5, -1),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
            Check(Eval() == 0, "ES09 future snapshot rejected");

            // ---- ES10: authority deny-by-default ------------------------------------------------
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(EconomyDirector.MinRecheckMs);
            EconomyDirector.SetAuthorityProbe(delegate { return false; });
            Check(Eval() == 0, "ES10 non-master = no-op");
            EconomyDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
            Check(Eval() == 0, "ES10 faulting probe = no-op");
            EconomyDirector.SetAuthorityProbe(null);
            Check(Eval() == 0, "ES10 null probe = no-op");
            Check(EconomyDirector.EvaluationCount == 0, "ES10 gated passes not counted");

            // ---- ES11: vanished report + expiry hygiene + bounded history -------------------------
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Check(EconomyDirector.ActiveRecordCount == 1, "ES11 setup tracked");
            // Credits go unreadable: record decays after ActiveExpiryMs
            // (LastSeenMs stops refreshing on unreadable passes).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, -1, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Eval();
            Advance(EconomyDirector.ActiveExpiryMs);
            Publish(Snap(s_Clock.NowMs, -1, 10, 90f, 6000, 6000, NavIn(5, -1), null));
            Check(Eval() == 1, "ES11 vanished reported after expiry");
            Check(HasLineContaining("EconomyVanished ECONOMY:CREDITS"), "ES11 vanished line");
            Check(EconomyDirector.ActiveRecordCount == 0, "ES11 record decayed");
            Check(EconomyDirector.HistoryCount == 1, "ES11 history entry");
            // Credits readable again: re-opens (new episode).
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 1, "ES11 re-opened after recovery");
            Check(EconomyDirector.OpenedReportCount == 2, "ES11 opened counted twice");
            // Bounded history: churn more than MaxHistory entries. Each cycle:
            // readable pass refreshes the record, then an unreadable pass —
            // published FRESH after the ActiveExpiryMs advance (publishing
            // before the advance would make the snapshot >20s stale and the
            // fail-safe gate would reject it before hygiene runs).
            for (int i = 0; i < EconomyDirector.MaxHistory + 4; i++)
            {
                Publish(Baseline(s_Clock.NowMs));
                Advance(EconomyDirector.MinRecheckMs);
                Eval();
                Advance(EconomyDirector.ActiveExpiryMs);
                Publish(Snap(s_Clock.NowMs, -1, 10, 90f, 6000, 6000, NavIn(5, -1), null));
                Eval();
            }
            Check(EconomyDirector.HistoryCount == EconomyDirector.MaxHistory, "ES11 history bounded");

            // ---- ES12: shop classifier seam (deny-by-default) ---------------------------------------
            FreshSetup();
            EconomyDirector.SetShopSectorClassifier(null);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Advance(EconomyDirector.MinRecheckMs);
            Eval();
            Advance(EconomyDirector.StoreDwellMs + EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Check(Eval() == 0, "ES12 null classifier = never a shop");
            EconomyDirector.SetShopSectorClassifier(delegate (int v) { throw new InvalidOperationException("fault"); });
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Check(Eval() == 0, "ES12 faulting classifier = never a shop");
            Check(EconomyDirector.StoreReportCount == 0, "ES12 no store reports");
            // Wired classifier (production shape): reports after dwell.
            EconomyDirector.SetShopSectorClassifier(delegate (int v) { return v == 1; });
            Advance(EconomyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Eval();
            Advance(EconomyDirector.StoreDwellMs);
            Publish(Snap(s_Clock.NowMs, 5000, 10, 90f, 6000, 6000, NavIn(7, 1), null));
            Check(Eval() == 1, "ES12 wired classifier reports after dwell");
            Check(HasLineContaining("StoreSectorReport sector=7"), "ES12 store line fired");

            // ---- ES13: cadence + counters + diagnostics -----------------------------------------------
            FreshSetup();
            Publish(Baseline(s_Clock.NowMs));
            Check(Eval() == 1, "ES13 first eval opens");
            Check(Eval() == 0, "ES13 same-instant re-eval cadence-gated");
            Advance(1);
            Check(Eval() == 0, "ES13 sub-cadence re-eval gated");
            Check(EconomyDirector.EvaluationCount == 1, "ES13 gated passes not counted");
            Advance(EconomyDirector.MinRecheckMs);
            Check(Eval() == 0, "ES13 post-cadence quiet (unchanged state)");
            Check(EconomyDirector.Lines().Count == 1, "ES13 Lines bounded");
            List<string> status = EconomyDirector.StatusLines();
            Check(status.Count == 2 && status[0].IndexOf("economy=") == 0 && status[1].IndexOf("uncertain=") > 0,
                "ES13 StatusLines bounded");
            Check(EconomyDirector.GetTrack(null) == null, "ES13 GetTrack null-safe");
            Check(EconomyDirector.GetTrack("") == null, "ES13 GetTrack empty-safe");

            Console.WriteLine("SUITE EconomyTests passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}