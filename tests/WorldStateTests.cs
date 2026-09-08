// Dev-side unit tests for the Phase 6 world-state domain (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure world-domain files (WorldSnapshot, WorldStateService,
// WorldSnapshotProbe) plus the task-domain files the probe implements.
// Time is virtual: WorldStateService takes explicit nowMs, the probe takes
// injectable snapshot/time providers — no real clock reads, no sleeping.
// Covers the Phase 6 mandated scenarios: null/missing objects, no crew,
// multiple bots, missing captain, sector transition, no active mission,
// multiple missions, destroyed targets, invalid/stale refs, host/client
// authority, deterministic construction, bounded collection sizes.
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;

namespace CapBot.TaskTests
{
    internal static class WorldStateTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        // ---- helpers ----------------------------------------------------------

        private static CrewMemberSnapshot Crew(int id, string name, bool isBot, bool isCaptain, bool aliveKnown, bool alive)
        {
            return new CrewMemberSnapshot(id, name, isBot, isBot ? 0 : 2, 0, aliveKnown, alive, 1f, "Bridge", isCaptain, isBot ? 3 : -1);
        }

        private static ShipSnapshot Ship(int id, bool hostile, int team)
        {
            return new ShipSnapshot(id, "ship" + id, false, team, hostile, 0.5f, 0.5f, false, 0, -1, 0, 10f);
        }

        private static WorldSnapshot SnapshotAt(int timeMs, bool isHost, List<ShipSnapshot> ships, List<CrewMemberSnapshot> crew, List<MissionSnapshot> missions)
        {
            return new WorldSnapshot(
                timeMs, true, isHost, 7,
                isHost ? WorldAuthority.MasterDerived : WorldAuthority.LocallyObserved,
                ships, crew, missions,
                new ThreatSnapshot(new List<int> { 9 }, 1, 1, 0, -1, float.NaN, float.NaN),
                new NavigationSnapshot(5, "Sector Five", 3, false, -1, new List<int> { 6 }, false, float.NaN, float.NaN, float.NaN, false),
                new ResourceSnapshot(1000, new List<int> { 1, 2 }, 3, 10, 0.5f),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved);
        }

        private static WorldSnapshotProbe Probe(WorldSnapshot snapshot, int nowMs)
        {
            WorldSnapshot fixedSnap = snapshot;
            return new WorldSnapshotProbe(delegate { return fixedSnap; }, delegate { return nowMs; });
        }

        private static void FreshWorldSetup()
        {
            WorldStateService.ResetForTests();
        }

        private sealed class FakeSource : IWorldSource
        {
            public WorldSnapshot Next;
            public int CaptureCalls;
            public WorldSnapshot Capture(WorldSnapshot previous, int nowMs)
            {
                CaptureCalls++;
                return Next;
            }
        }

        // ---- tests -------------------------------------------------------------

        internal static int Run()
        {
            s_Passed = 0;
            s_Failed = 0;

            RunSnapshotTests();
            RunServiceTests();
            RunProbeTests();

            Console.WriteLine("WORLD_STATE passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        private static void RunSnapshotTests()
        {
            // ---- deterministic construction -------------------------------------
            List<ShipSnapshot> ships = new List<ShipSnapshot> { Ship(7, false, 0) };
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot> { Crew(1, "cap", true, true, true, true) };
            WorldSnapshot a = SnapshotAt(1000, true, ships, crew, null);
            WorldSnapshot b = SnapshotAt(1000, true, new List<ShipSnapshot> { Ship(7, false, 0) }, new List<CrewMemberSnapshot> { Crew(1, "cap", true, true, true, true) }, null);
            Check(a.ToSummaryLine() == b.ToSummaryLine(), "same inputs -> identical summary line (deterministic construction)");
            Check(a.ToSummaryLine().StartsWith("WORLD t=1000", StringComparison.Ordinal), "summary line format deterministic");
            Check(a.IsNeverCaptured == false, "captured snapshot IsNeverCaptured=false");

            // ---- null/missing objects: empty + null sections ---------------------
            WorldSnapshot e = WorldSnapshot.Empty;
            Check(e.IsNeverCaptured && e.SnapshotTimeMs == -1, "Empty snapshot: never-captured sentinel");
            Check(e.Ships.Count == 0 && e.Crew.Count == 0 && e.Missions.Count == 0, "Empty snapshot: bounded empty collections");
            Check(e.Navigation.CurrentSectorId == -1 && e.Resources.Credits == -1, "Empty snapshot: -1 unknown sentinels");
            Check(float.IsNaN(e.Navigation.DistMovedInLast5s), "Empty snapshot: NaN unknown sentinel");
            Check(e.ToSummaryLine() == "WORLD empty", "Empty snapshot summary");
            WorldSnapshot nullSections = new WorldSnapshot(1000, false, false, -1, WorldAuthority.Unknown, null, null, null, null, null, null, null,
                WorldAuthority.Unknown, WorldAuthority.Unknown, WorldAuthority.Unknown, WorldAuthority.Unknown);
            Check(nullSections.Ships.Count == 0 && nullSections.Crew.Count == 0, "null sections render as empty collections (null-safe)");
            Check(nullSections.Threats != null && nullSections.Navigation != null && nullSections.Resources != null, "null sections render as unknown defaults");
            Check(float.IsNaN(nullSections.Navigation.DistMovedInLast5s) && nullSections.Navigation.CurrentSectorId == -1, "null navigation -> unknown sentinels");

            // ---- bounded collection sizes ----------------------------------------
            List<ShipSnapshot> manyShips = new List<ShipSnapshot>();
            for (int i = 0; i < 30; i++) manyShips.Add(Ship(100 + i, false, 1));
            WorldSnapshot bounded = SnapshotAt(1000, true, manyShips, crew, null);
            Check(bounded.Ships.Count == WorldSnapshot.MaxShips, "ships bounded to MaxShips=" + WorldSnapshot.MaxShips);

            List<CrewMemberSnapshot> manyCrew = new List<CrewMemberSnapshot>();
            for (int i = 0; i < 20; i++) manyCrew.Add(Crew(10 + i, "p" + i, i % 2 == 0, false, true, true));
            bounded = SnapshotAt(1000, true, null, manyCrew, null);
            Check(bounded.Crew.Count == WorldSnapshot.MaxCrew, "crew bounded to MaxCrew=" + WorldSnapshot.MaxCrew);

            List<MissionSnapshot> manyMissions = new List<MissionSnapshot>();
            for (int i = 0; i < 20; i++) manyMissions.Add(new MissionSnapshot(500 + i, false, false, 1, 0, null));
            bounded = SnapshotAt(1000, true, null, crew, manyMissions);
            Check(bounded.Missions.Count == WorldSnapshot.MaxMissions, "missions bounded to MaxMissions=" + WorldSnapshot.MaxMissions);

            List<int> manyHostiles = new List<int>();
            for (int i = 0; i < 20; i++) manyHostiles.Add(900 + i);
            ThreatSnapshot boundedThreats = new ThreatSnapshot(manyHostiles, 0, 0, 0, -1, float.NaN, float.NaN);
            Check(boundedThreats.KnownHostileShipIds.Count == ThreatSnapshot.MaxHostileIds, "hostile ids bounded to MaxHostileIds");

            List<int> manyGoals = new List<int>();
            for (int i = 0; i < 20; i++) manyGoals.Add(300 + i);
            NavigationSnapshot boundedNav = new NavigationSnapshot(5, null, -1, false, -1, manyGoals, false, float.NaN, float.NaN, float.NaN, false);
            Check(boundedNav.CourseGoals.Count == NavigationSnapshot.MaxCourseGoals, "course goals bounded to MaxCourseGoals");

            List<int> manyResearch = new List<int>();
            for (int i = 0; i < 20; i++) manyResearch.Add(i);
            ResourceSnapshot boundedRes = new ResourceSnapshot(0, manyResearch, 0, 0, 0f);
            Check(boundedRes.ResearchMaterials.Count == ResourceSnapshot.MaxResearchEntries, "research entries bounded to MaxResearchEntries");

            // ---- no crew / missing captain / multiple bots ------------------------
            WorldSnapshot noCrew = SnapshotAt(1000, true, ships, new List<CrewMemberSnapshot>(), null);
            Check(noCrew.Crew.Count == 0, "no crew -> empty crew list");
            WorldSnapshotProbe p = Probe(noCrew, 1500);
            Check(p.OwnerAvailable("CAPTAIN"), "no crew: owner check fails open (empty view is not evidence)");

            // populated crew WITHOUT a captain -> positive absence for CAPTAIN
            List<CrewMemberSnapshot> crewNoCaptain = new List<CrewMemberSnapshot>
            {
                Crew(2, "bot1", true, false, true, true),
                Crew(3, "bot2", true, false, true, true),
                Crew(4, "human", false, false, true, true),
            };
            WorldSnapshot noCaptain = SnapshotAt(1000, true, ships, crewNoCaptain, null);
            p = Probe(noCaptain, 1500);
            Check(!p.OwnerAvailable("CAPTAIN"), "missing captain: populated crew without captain -> CAPTAIN unavailable (positive absence)");
            Check(p.OwnerAvailable("BOT:2"), "multiple bots: BOT:2 present and alive -> available");
            Check(p.OwnerAvailable("BOT:3"), "multiple bots: BOT:3 present and alive -> available");
            Check(!p.OwnerAvailable("BOT:9"), "multiple bots: BOT:9 absent from populated crew -> unavailable");
            Check(p.OwnerAvailable("HOST"), "HOST owner is not answerable from crew data -> fail-open");
            Check(p.OwnerAvailable("GARBAGE"), "unknown owner format -> fail-open");
            Check(p.OwnerAvailable("BOT:xyz"), "unparseable BOT id -> fail-open");
            Check(p.OwnerAvailable(null), "null owner -> fail-open");

            List<CrewMemberSnapshot> crewDeadBot = new List<CrewMemberSnapshot>
            {
                Crew(2, "bot1", true, false, true, false),
                Crew(5, "cap", true, true, true, true),
            };
            WorldSnapshot deadBot = SnapshotAt(1000, true, ships, crewDeadBot, null);
            p = Probe(deadBot, 1500);
            Check(!p.OwnerAvailable("BOT:2"), "dead bot owner with AliveKnown evidence -> unavailable");
            Check(p.OwnerAvailable("CAPTAIN"), "alive captain present -> available");

            List<CrewMemberSnapshot> crewUnknownAlive = new List<CrewMemberSnapshot>
            {
                new CrewMemberSnapshot(2, "bot1", true, 0, 0, false, false, float.NaN, null, false, 3),
            };
            WorldSnapshot unknownAlive = SnapshotAt(1000, true, ships, crewUnknownAlive, null);
            p = Probe(unknownAlive, 1500);
            Check(p.OwnerAvailable("BOT:2"), "alive state unknown (AliveKnown=false) -> fail-open available");

            // ---- missions: none / one / multiple ----------------------------------
            WorldSnapshot noMissions = SnapshotAt(1000, true, ships, crew, new List<MissionSnapshot>());
            Check(noMissions.Missions.Count == 0, "no active mission -> empty missions");
            p = Probe(noMissions, 1500);
            Check(p.TargetValid(TaskForTest("MISSION", "777")), "no missions: MISSION target check fails open (empty view is not evidence)");

            List<MissionSnapshot> missions = new List<MissionSnapshot>
            {
                new MissionSnapshot(601, false, false, 3, 1, "Fix the reactor"),
                new MissionSnapshot(505, false, false, 2, 2, null),
                new MissionSnapshot(700, true, false, 1, 1, null),
            };
            WorldSnapshot multiMissions = SnapshotAt(1000, true, ships, crew, missions);
            Check(multiMissions.Missions.Count == 3, "multiple missions captured");
            Check(multiMissions.Missions[0].FirstIncompleteObjectiveText == "Fix the reactor", "first incomplete objective text picked");
            Check(multiMissions.Missions[1].FirstIncompleteObjectiveText == null, "all objectives complete -> no incomplete text");
            Check(multiMissions.Missions[2].Ended, "ended mission marked Ended");
            p = Probe(multiMissions, 1500);
            Check(p.TargetValid(TaskForTest("MISSION", "505")), "MISSION target present -> valid");
            Check(!p.TargetValid(TaskForTest("MISSION", "999")), "MISSION target absent from populated missions -> invalid");
            Check(p.TargetValid(TaskForTest("MISSION", "abc")), "unparseable mission id -> fail-open (data, not evidence)");

            // ---- destroyed targets / invalid refs ----------------------------------
            WorldSnapshot shipsView = SnapshotAt(1000, true, ships, crew, null); // ships = [id 7 only]
            p = Probe(shipsView, 1500);
            Check(p.TargetValid(TaskForTest("SHIP", "7")), "SHIP target present in captured view -> valid");
            Check(!p.TargetValid(TaskForTest("SHIP", "8")), "SHIP target absent from captured view -> invalid (destroyed/gone)");
            Check(p.TargetValid(TaskForTest("SHIP", "")), "empty target id -> fail-open (nothing to invalidate)");
            Check(p.TargetValid(TaskForTest("SECTOR", "5")), "SECTOR target kind unverifiable from bounded data -> fail-open");
            Check(p.TargetValid(TaskForTest("UNKNOWN_KIND", "1")), "unknown target kind -> fail-open");
            Check(p.TargetValid(null), "null task -> fail-open (nothing to invalidate)");

            // stale snapshot -> fail-open even for absent ids
            p = Probe(shipsView, 1000 + WorldStateService.MaxSnapshotAgeMs + 1);
            Check(p.TargetValid(TaskForTest("SHIP", "8")), "stale snapshot: target check fails open (no destructive actions on stale data)");
            Check(p.OwnerAvailable("CAPTAIN"), "stale snapshot: owner check fails open");
            p = Probe(null, 1500);
            Check(p.TargetValid(TaskForTest("SHIP", "8")), "never-captured snapshot: target check fails open");

            // fresh boundary: exactly MaxSnapshotAgeMs is still fresh
            p = Probe(shipsView, 1000 + WorldStateService.MaxSnapshotAgeMs);
            Check(p.TargetValid(TaskForTest("SHIP", "7")), "snapshot exactly at MaxSnapshotAgeMs boundary still usable");

            // capability + invalidate contracts
            p = Probe(shipsView, 1500);
            Check(p.CapabilityAvailable(TaskForTest("SHIP", "7")), "capability checks defer to P7 -> always available");
            Check(!p.WorldInvalidatesTask(TaskForTest("SHIP", "8")), "no invented premise semantics -> never invalidates");

            // ---- host/client authority marking -------------------------------------
            WorldSnapshot hostSnap = SnapshotAt(1000, true, ships, crew, null);
            Check(hostSnap.IsHost && hostSnap.SessionAuthority == WorldAuthority.MasterDerived, "host peer: session authority MasterDerived");
            WorldSnapshot clientSnap = SnapshotAt(1000, false, ships, crew, null);
            Check(!clientSnap.IsHost && clientSnap.SessionAuthority == WorldAuthority.LocallyObserved, "client peer: session authority LocallyObserved");
            Check(clientSnap.GameStarted == true, "GameStarted flag carried through snapshot");
            Check(clientSnap.CurrentHubId == 7, "hub id carried through snapshot");
        }

        private static CapBotTask TaskForTest(string kind, string id)
        {
            return CapBotTask.Create("PROBE_TEST", "CAPTAIN", "probe test", 5, 0, -1, kind, id, null);
        }

        private static void RunServiceTests()
        {
            // ---- throttle ----------------------------------------------------------
            FreshWorldSetup();
            FakeSource src = new FakeSource();
            src.Next = SnapFor(5, false, 0);
            WorldStateService.SetSource(src);
            WorldStateService.Refresh(1000);
            Check(src.CaptureCalls == 1, "first refresh captures");
            WorldStateService.Refresh(1500);
            Check(src.CaptureCalls == 1, "refresh within MinRefreshIntervalMs throttled");
            WorldStateService.Refresh(2000);
            Check(src.CaptureCalls == 2, "refresh after interval captures again");
            Check(WorldStateService.Latest.Navigation.CurrentSectorId == 5, "latest snapshot cached");

            // ---- null source dormant ------------------------------------------------
            FreshWorldSetup();
            WorldStateService.Refresh(1000);
            Check(WorldStateService.Latest.IsNeverCaptured, "no source: refresh is a no-op (dormant by construction)");

            // ---- freshness -----------------------------------------------------------
            Check(WorldStateService.GetFreshness(1000) == WorldSnapshotFreshness.NeverCaptured, "freshness NeverCaptured before first capture");
            FreshWorldSetup();
            src = new FakeSource();
            src.Next = SnapFor(5, false, 1000);
            WorldStateService.SetSource(src);
            WorldStateService.Refresh(1000);
            Check(WorldStateService.GetFreshness(2000) == WorldSnapshotFreshness.Fresh, "freshness Fresh within MaxSnapshotAgeMs");
            Check(WorldStateService.GetFreshness(1000 + WorldStateService.MaxSnapshotAgeMs) == WorldSnapshotFreshness.Fresh, "freshness Fresh at exact boundary");
            Check(WorldStateService.GetFreshness(1000 + WorldStateService.MaxSnapshotAgeMs + 1) == WorldSnapshotFreshness.Stale, "freshness Stale beyond MaxSnapshotAgeMs");

            // ---- transitions: first capture silent, sector change, warp edges --------
            FreshWorldSetup();
            List<string> transitions = new List<string>();
            WorldStateService.SetTransitionListener(delegate (WorldTransition t) { transitions.Add(t.Kind + ":" + t.FromSectorId + "->" + t.ToSectorId + "@" + t.TimeMs); });
            src = new FakeSource();
            src.Next = SnapFor(5, false, 0);
            WorldStateService.SetSource(src);
            WorldStateService.Refresh(1000);
            Check(transitions.Count == 0, "first capture fires no transitions (nothing to transition from)");
            src.Next = SnapFor(9, false, 0);
            WorldStateService.Refresh(2000);
            Check(transitions.Count == 1 && transitions[0] == "SECTOR_CHANGED:5->9@2000", "sector change detected with from/to/timestamp");
            Check(WorldStateService.HasUnacknowledgedSectorChange(), "sticky sector-change flag set");
            WorldStateService.AcknowledgeSectorChanged();
            Check(!WorldStateService.HasUnacknowledgedSectorChange(), "AcknowledgeSectorChanged clears sticky flag");
            Check(WorldStateService.HasUnacknowledgedSectorChange() == false, "flag stays clear until next change");

            src.Next = SnapFor(9, true, 0);
            WorldStateService.Refresh(3000);
            Check(transitions.Count == 2 && transitions[1] == "WARP_STARTED:9->9@3000", "warp start edge detected");
            src.Next = SnapFor(9, false, 0);
            WorldStateService.Refresh(4000);
            Check(transitions.Count == 3 && transitions[2] == "WARP_ENDED:9->9@4000", "warp end edge detected");
            Check(!WorldStateService.HasUnacknowledgedSectorChange(), "warp edges alone do not set the sector-change flag");

            // unknown sector ids never fabricate transitions
            FreshWorldSetup();
            transitions.Clear();
            WorldStateService.SetTransitionListener(delegate (WorldTransition t) { transitions.Add(t.Kind); });
            src = new FakeSource();
            src.Next = SnapFor(-1, false, 0);
            WorldStateService.SetSource(src);
            WorldStateService.Refresh(1000);
            src.Next = SnapFor(5, false, 0);
            WorldStateService.Refresh(2000);
            Check(transitions.Count == 0, "unknown (-1) last-seen sector never fabricates SECTOR_CHANGED");

            // ---- SetSource resets detection state -------------------------------------
            src.Next = SnapFor(5, false, 0);
            WorldStateService.SetSource(src);
            WorldStateService.Refresh(3000);
            Check(transitions.Count == 0, "SetSource reset: next capture treated as first (no cross-source transitions)");

            // ---- source throw handling -------------------------------------------------
            FreshWorldSetup();
            src = new FakeSource();
            src.Next = SnapFor(5, false, 0);
            WorldStateService.SetSource(src);
            WorldStateService.Refresh(1000);
            Check(WorldStateService.RefreshCount == 1, "refresh counter observed before reset");
            WorldStateService.SetSource(new ThrowingSource());
            WorldStateService.Refresh(2000);
            Check(WorldStateService.ErrorCount == 1 && WorldStateService.LastError != null, "source exception counted, never thrown");
            Check(WorldStateService.Latest.IsNeverCaptured, "failed capture leaves previous snapshot intact");

            // ---- ResetForTests ----------------------------------------------------------
            WorldStateService.ResetForTests();
            Check(WorldStateService.RefreshCount == 0 && WorldStateService.Latest.IsNeverCaptured, "ResetForTests clears cache and counters");
        }

        private static WorldSnapshot SnapFor(int sectorId, bool inWarp, int timeMs)
        {
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                new List<ShipSnapshot>(), new List<CrewMemberSnapshot>(), new List<MissionSnapshot>(),
                null,
                new NavigationSnapshot(sectorId, null, -1, inWarp, -1, null, false, float.NaN, float.NaN, float.NaN, false),
                null,
                null,
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved);
        }

        private sealed class ThrowingSource : IWorldSource
        {
            public WorldSnapshot Capture(WorldSnapshot previous, int nowMs)
            {
                throw new InvalidOperationException("boom");
            }
        }

        private static void RunProbeTests()
        {
            // provider-throwing probe fails open
            WorldSnapshotProbe p = new WorldSnapshotProbe(
                delegate { throw new InvalidOperationException("boom"); },
                delegate { return 0; });
            Check(p.TargetValid(TaskForTest("SHIP", "8")), "provider exception -> target check fails open");
            Check(p.OwnerAvailable("CAPTAIN"), "provider exception -> owner check fails open");
        }
    }
}