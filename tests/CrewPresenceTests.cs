// Dev-side unit tests for the P44/P45 presence machine (directive B: MoreBots
// false-DEAD fix). Pure C#, compiled by tests\run_tests.ps1 against the same
// domain bundle as CrewAgentTests. Time is virtual (injected nowMs).
//
// Mandated scenarios (directive B §12 — the presence/lifecycle core):
//   P01  new agent starts SPAWNING
//   P02  live snapshot (pawn present, alive) -> ALIVE
//   P03  first death report -> TEMP_UNAVAILABLE (awaiting confirmation), never DEAD
//   P04  sustained death report (2+ syncs, >= 1 s) -> DEAD
//   P05  transient death artifact recovers -> ALIVE, never DEAD
//   P06  pawn missing/unknown in snapshot -> TEMP_UNAVAILABLE, never DEAD
//   P07  missing-data window then recovery -> ALIVE (no DEAD in between)
//   P08  DEAD does not soften on missing data (absence is not revival)
//   P09  positive alive evidence lifts DEAD (game-reported revival)
//   P10  confirmed removal -> REMOVED presence (distinct from DEAD)
//   P11  deactivate -> TEMP_UNAVAILABLE; reactivation does not fake ALIVE
//   P12  ReconcileCrewAgent idempotent (same logical agent, no duplicates)
//   P13  ReconcileCrewAgent refusals counted (invalid pid / unknown agent)
//   P14  reconcile keeps personality + memory attached (same stable AgentId)
//   P15  memory store survives TEMP_UNAVAILABLE + pawn recreation (8/8/8 core)
//   P16  no duplicate memory agent across rejoin + reconcile
//   P17  presence counters truthful (changes/deaths/tempEvents/reconciles)
//   P18  CaptainAgentPresence transition lines emitted for every transition
//   P19  PresenceForOwner CAPTAIN / BOT:<id> / unknown formats
//   P20  scheduler bounded wait: SPAWNING/TEMP_UNAVAILABLE owner -> deferred
//        (task stays Queued, no lease, deduped line); ALIVE owner -> granted
//   P21  class-0 captain full cycle: spawn -> alive -> temp -> alive, never DEAD
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;

namespace CapBot.TaskTests
{
    internal static class CrewPresenceTests
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
            CrewAgentRegistry.ResetForTests();
            CrewPersonalityRegistry.ResetForTests();
            CrewMemorySystem.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskScheduler.ResetForTests();
            WorldStateService.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 500000 };
            s_Snap = null;
            s_Lines.Clear();
            CrewAgentRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetWorldProvider(delegate { return s_Snap; });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
            TaskScheduler.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetRoleNameResolver(delegate (int classId)
            {
                switch (classId)
                {
                    case 0: return "Captain";
                    case 1: return "Pilot";
                    default: return "Crewman";
                }
            });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int Sync() { return CrewAgentRegistry.Sync(s_Clock.NowMs); }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static int CountLines(string prefix)
        {
            int n = 0;
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].StartsWith(prefix, StringComparison.Ordinal)) n++;
            }
            return n;
        }

        // Crew entry with full life data (pawn present).
        private static CrewMemberSnapshot Member(int id, bool isBot, int classId, bool isCaptain, bool alive, float health)
        {
            return new CrewMemberSnapshot(id, (isBot ? "bot" : "human") + id, isBot, classId, 0,
                true, alive, health, "Bridge", isCaptain, isBot ? 3 : -1);
        }

        // Crew entry with UNREADABLE life data (pawn missing/unknown) — the
        // exact false-DEAD trap MoreBots recreation produces.
        private static CrewMemberSnapshot MemberUnknownLife(int id, bool isBot, int classId, bool isCaptain)
        {
            return new CrewMemberSnapshot(id, (isBot ? "bot" : "human") + id, isBot, classId, 0,
                false, false, float.NaN, "Bridge", isCaptain, isBot ? 3 : -1);
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

        private static string Id(int playerId, bool isBot) { return CrewAgentRegistry.MakeAgentId(playerId, isBot); }

        // ---- the twenty-one scenarios -------------------------------------------

        internal static int Run()
        {
            // P01 — new agent starts SPAWNING
            {
                FreshSetup();
                Publish(Snap(500000, MemberUnknownLife(1, true, 0, true)));
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Spawning, "P01 first-seen agent starts SPAWNING");
                Check(a != null && "created" == a.PresenceReason, "P01 spawning reason = created");
                Check(CrewAgentRegistry.SpawningAgentCount == 1, "P01 spawning counter");
            }

            // P02 — live snapshot -> ALIVE
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 0, true, true, 1f)));
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Alive, "P02 alive snapshot -> ALIVE");
                Check(CrewAgentRegistry.AliveAgentCount == 1, "P02 alive counter");
                Check(a != null && a.DeathEvidence == null, "P02 no death evidence while alive");
            }

            // P03 — first death report is TEMP_UNAVAILABLE, never DEAD
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 0, true, true, 1f)));
                Sync();
                Publish(Snap(501000, Member(1, true, 0, true, false, 0f)));
                Advance(1000);
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.TempUnavailable, "P03 first death report -> TEMP_UNAVAILABLE");
                Check(CrewAgentRegistry.DeadAgentCount == 0, "P03 no DEAD after one report");
                Check(a != null && a.DeathObservedMs == -1, "P03 no death observed yet on record");
            }

            // P04 — sustained death report (2+ syncs spanning >= 1 s) -> DEAD
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 0, true, true, 1f)));
                Sync();
                Publish(Snap(501000, Member(1, true, 0, true, false, 0f)));
                Advance(1000);
                Sync(); // first report at 501000
                Publish(Snap(502000, Member(1, true, 0, true, false, 0f)));
                Advance(1000);
                Sync(); // sustained report at 502000 (>= 1 s span) -> confirm
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Dead, "P04 sustained report -> DEAD");
                Check(CrewAgentRegistry.DeadAgentCount == 1, "P04 dead counter");
                Check(CrewAgentRegistry.DeathConfirmationCount == 1, "P04 death confirmation counter");
                Check(a != null && a.DeathObservedMs == 502000, "P04 death observed stamped");
            }

            // P05 — transient death artifact recovers, never DEAD
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 1, false, true, 1f)));
                Sync();
                Publish(Snap(501000, Member(1, true, 1, false, false, 0.1f)));
                Advance(1000);
                Sync(); // first report -> TEMP_UNAVAILABLE awaiting
                Publish(Snap(502000, Member(1, true, 1, false, true, 1f)));
                Advance(1000);
                Sync(); // recovered before confirmation
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Alive, "P05 transient artifact -> back to ALIVE");
                Check(CrewAgentRegistry.DeadAgentCount == 0, "P05 never DEAD");
                Check(CrewAgentRegistry.DeathConfirmationCount == 0, "P05 zero death confirmations");
            }

            // P06 — pawn missing/unknown in snapshot -> TEMP_UNAVAILABLE, never DEAD
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 2, false, true, 1f)));
                Sync();
                Publish(Snap(501000, MemberUnknownLife(1, true, 2, false)));
                Advance(1000);
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.TempUnavailable, "P06 pawn-missing -> TEMP_UNAVAILABLE");
                Check(CrewAgentRegistry.DeadAgentCount == 0, "P06 pawn-missing is never DEAD");
                // Sustained missing data NEVER escalates to DEAD.
                Publish(Snap(502000, MemberUnknownLife(1, true, 2, false)));
                Advance(30000);
                Sync();
                a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.TempUnavailable, "P06 sustained missing data stays TEMP_UNAVAILABLE");
                Check(CrewAgentRegistry.DeathConfirmationCount == 0, "P06 zero deaths from missing data");
            }

            // P07 — missing-data window then recovery -> ALIVE (no DEAD between)
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 1, false, true, 1f)));
                Sync();
                Publish(Snap(501000, MemberUnknownLife(1, true, 1, false)));
                Advance(1000);
                Sync();
                Publish(Snap(502000, Member(1, true, 1, false, true, 1f)));
                Advance(1000);
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Alive, "P07 recovery -> ALIVE");
                Check(CrewAgentRegistry.DeathConfirmationCount == 0, "P07 no death ever recorded");
            }

            // P08 — DEAD does not soften on missing data
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 0, true, true, 1f)));
                Sync();
                Publish(Snap(501000, Member(1, true, 0, true, false, 0f)));
                Advance(1000);
                Sync();
                Publish(Snap(502000, Member(1, true, 0, true, false, 0f)));
                Advance(1000);
                Sync(); // confirmed DEAD
                Publish(Snap(503000, MemberUnknownLife(1, true, 0, true)));
                Advance(1000);
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Dead, "P08 missing data does not soften DEAD");
                Check(CrewAgentRegistry.DeathConfirmationCount == 1, "P08 still exactly one confirmation");
            }

            // P09 — positive alive evidence lifts DEAD (game-reported revival)
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 0, true, true, 1f)));
                Sync();
                Publish(Snap(501000, Member(1, true, 0, true, false, 0f)));
                Advance(1000);
                Sync();
                Publish(Snap(502000, Member(1, true, 0, true, false, 0f)));
                Advance(1000);
                Sync(); // DEAD
                Publish(Snap(503000, Member(1, true, 0, true, true, 1f)));
                Advance(1000);
                Sync(); // the game itself reports the pawn alive again
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Alive, "P09 game-reported revival lifts DEAD");
            }

            // P10 — confirmed removal -> REMOVED (distinct from DEAD)
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 3, false, true, 1f)));
                Sync();
                Publish(Snap(501000)); // gone from crew
                Advance(1000);
                Sync();
                Publish(Snap(516000)); // grace (15 s) expires
                Advance(15000);
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a == null, "P10 removed record leaves live map");
                Check(CrewAgentRegistry.RemovedAgentCount == 0, "P10 removed agent not in live presence counts");
                Check(CrewAgentRegistry.DeathConfirmationCount == 0, "P10 removal is not a death");
                Check(HasLineContaining("to=REMOVED"), "P10 REMOVED transition logged");
            }

            // P11 — deactivate -> TEMP_UNAVAILABLE; reactivation does not fake ALIVE
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 1, false, true, 1f)));
                Sync();
                Publish(Snap(501000)); // vanished from snapshot
                Advance(1000);
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Lifecycle == CrewAgentLifecycle.Inactive, "P11 absence -> Inactive lifecycle");
                Check(a != null && a.Presence == AgentPresenceState.TempUnavailable, "P11 absence -> TEMP_UNAVAILABLE presence");
                // Reactivate with UNKNOWN life data: record is Active again but
                // presence stays non-ALIVE until positive evidence arrives.
                Publish(Snap(502000, MemberUnknownLife(1, true, 1, false)));
                Advance(1000);
                Sync();
                a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Lifecycle == CrewAgentLifecycle.Active, "P11 reactivation -> Active");
                Check(a != null && a.Presence == AgentPresenceState.TempUnavailable, "P11 reactivation does not fake ALIVE");
                Publish(Snap(503000, Member(1, true, 1, false, true, 1f)));
                Advance(1000);
                Sync();
                a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Alive, "P11 live evidence -> ALIVE");
            }

            // P12 — ReconcileCrewAgent idempotent (same logical agent, no duplicates)
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 1, false, true, 1f)));
                Sync();
                bool r1 = CrewAgentRegistry.ReconcileCrewAgent(1, true, s_Clock.NowMs);
                bool r2 = CrewAgentRegistry.ReconcileCrewAgent(1, true, s_Clock.NowMs);
                bool r3 = CrewAgentRegistry.ReconcileCrewAgent(1, true, s_Clock.NowMs);
                Check(r1 && r2 && r3, "P12 repeated reconciles succeed");
                Check(CrewAgentRegistry.AgentCount == 1, "P12 still exactly one agent");
                Check(CrewAgentRegistry.CreatedCount == 1, "P12 reconcile created no new record");
                Check(CrewAgentRegistry.ReconcileCount == 3, "P12 reconcile counter = 3");
            }

            // P13 — ReconcileCrewAgent refusals counted
            {
                FreshSetup();
                bool r1 = CrewAgentRegistry.ReconcileCrewAgent(-1, true, s_Clock.NowMs);
                bool r2 = CrewAgentRegistry.ReconcileCrewAgent(42, true, s_Clock.NowMs);
                Check(!r1 && !r2, "P13 unknown/invalid identities refused");
                Check(CrewAgentRegistry.ReconcileRefusalCount == 2, "P13 refusals counted");
                Check(HasLineContaining("CaptainAgentReconcileRefused"), "P13 refusal logged");
            }

            // P14 — reconcile keeps personality + memory attached
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 1, false, true, 1f)));
                Sync();
                string a = Id(1, true);
                CrewAgentRegistry.ReconcileCrewAgent(1, true, s_Clock.NowMs);
                Check(CrewPersonalityRegistry.Get(a) != null, "P14 personality attached after reconcile");
                Check(CrewMemorySystem.AgentCount == 1, "P14 memory store attached after reconcile");
            }

            // P15 — memory store survives TEMP_UNAVAILABLE + recreation cycle
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 1, false, true, 1f)));
                Sync();
                string a = Id(1, true);
                CrewMemorySystem.RememberLocation(a, "Bridge", s_Clock.NowMs);
                int before = CrewMemorySystem.MemoryCountOf(a);
                // TEMP_UNAVAILABLE window (pawn missing), then pawn recreation.
                Publish(Snap(501000, MemberUnknownLife(1, true, 1, false)));
                Advance(1000);
                Sync();
                CrewAgentRegistry.ReconcileCrewAgent(1, true, s_Clock.NowMs);
                Publish(Snap(502000, Member(1, true, 1, false, true, 1f)));
                Advance(1000);
                Sync();
                Check(CrewMemorySystem.MemoryCountOf(a) >= before, "P15 memory survives temp-unavailable + recreation");
                Check(CrewMemorySystem.AgentCount == 1, "P15 exactly one memory store");
            }

            // P16 — no duplicate memory agent across rejoin + reconcile
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 1, false, true, 1f)));
                Sync();
                Publish(Snap(501000)); // leave
                Advance(1000);
                Sync();
                Publish(Snap(502000, Member(1, true, 1, false, true, 1f))); // rejoin within grace
                Advance(1000);
                Sync();
                CrewAgentRegistry.ReconcileCrewAgent(1, true, s_Clock.NowMs);
                Check(CrewMemorySystem.AgentCount == 1, "P16 one memory store after rejoin + reconcile");
                Check(CrewAgentRegistry.AgentCount == 1, "P16 one agent record");
                Check(CrewMemorySystem.MemoryReusedCount >= 1, "P16 reuse counted (no silent recreate)");
            }

            // P17 — presence counters truthful
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 1, false, true, 1f)));
                Sync(); // spawn -> alive (1 change)
                Publish(Snap(501000, MemberUnknownLife(1, true, 1, false)));
                Advance(1000);
                Sync(); // temp (2)
                Publish(Snap(502000, Member(1, true, 1, false, true, 1f)));
                Advance(1000);
                Sync(); // alive (3)
                Check(CrewAgentRegistry.PresenceChangeCount == 3, "P17 presence changes = 3");
                Check(CrewAgentRegistry.TempUnavailableCount == 1, "P17 temp-unavailable events = 1");
                Check(CrewAgentRegistry.DeathConfirmationCount == 0, "P17 zero deaths");
                Check(CrewAgentRegistry.AliveAgentCount == 1 && CrewAgentRegistry.TempUnavailableAgentCount == 0, "P17 live counts agree with state");
            }

            // P18 — CaptainAgentPresence transition lines for every transition
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 0, true, true, 1f)));
                Sync();
                Publish(Snap(501000, MemberUnknownLife(1, true, 0, true)));
                Advance(1000);
                Sync();
                Publish(Snap(502000, Member(1, true, 0, true, true, 1f)));
                Advance(1000);
                Sync();
                int lines = CountLines("CaptainAgentPresence");
                Check(lines >= 3, "P18 transition lines emitted (alive/temp/alive)");
                Check(HasLineContaining("from=SPAWNING to=ALIVE"), "P18 spawn->alive line");
                Check(HasLineContaining("to=TEMP_UNAVAILABLE"), "P18 ->temp line");
                Check(HasLineContaining("from=TEMP_UNAVAILABLE to=ALIVE"), "P18 temp->alive line");
            }

            // P19 — PresenceForOwner formats
            {
                FreshSetup();
                Publish(Snap(500000, Member(1, true, 0, true, true, 1f), Member(2, true, 1, false, true, 1f)));
                Sync();
                Check(CrewAgentRegistry.PresenceForOwner("CAPTAIN") == AgentPresenceState.Alive, "P19 CAPTAIN resolves to captain agent presence");
                Check(CrewAgentRegistry.PresenceForOwner("BOT:2") == AgentPresenceState.Alive, "P19 BOT:<id> resolves");
                Check(CrewAgentRegistry.PresenceForOwner("BOT:99") == AgentPresenceState.Unknown, "P19 unknown bot -> Unknown");
                Check(CrewAgentRegistry.PresenceForOwner("HOST") == AgentPresenceState.Unknown, "P19 unknown format -> Unknown");
                Check(CrewAgentRegistry.PresenceForOwner(null) == AgentPresenceState.Unknown, "P19 null -> Unknown");
                Check(CrewAgentRegistry.PresenceForOwner("BOT:x") == AgentPresenceState.Unknown, "P19 unparseable id -> Unknown");
            }

            // P20 — scheduler bounded wait (defer, not retry/spin)
            {
                FreshSetup();
                // First-seen agent with unreadable life data stays SPAWNING.
                Publish(Snap(500000, MemberUnknownLife(1, true, 1, false)));
                Sync();
                CapBotTask t = CapBotTask.Create("TEST", "BOT:1", "presence wait test", 3, 0, 60000, null, null, null);
                TaskRegistry.Register(t);
                t.TryQueue();
                TaskScheduler.Tick(s_Clock.NowMs);
                Check(!TaskScheduler.HasLease(t.TaskId), "P20 SPAWNING owner gets no grant");
                Check(t.State == TaskState.Queued, "P20 task stays Queued (deferred, not failed)");
                Check(HasLineContaining("OwnerBoundedWait owner=BOT:1 presence=Spawning"), "P20 bounded-wait line emitted");
                // Owner becomes ALIVE -> next pass grants normally.
                Publish(Snap(501000, Member(1, true, 1, false, true, 1f)));
                Advance(1000);
                Sync();
                TaskScheduler.Tick(s_Clock.NowMs);
                Check(TaskScheduler.HasLease(t.TaskId), "P20 ALIVE owner granted normally");
                // No bounded-wait spam: the line appears once per owner+state.
                int waitLines = CountLines("OwnerBoundedWait owner=BOT:1");
                Check(waitLines == 1, "P20 wait line deduped (no per-pass storm)");
            }

            // P21 — class-0 captain full cycle never reports false DEAD
            {
                FreshSetup();
                Publish(Snap(500000, MemberUnknownLife(1, true, 0, true)));  // spawn window
                Sync();
                Publish(Snap(501000, Member(1, true, 0, true, true, 1f)));   // pawn live
                Advance(1000);
                Sync();
                Publish(Snap(502000, MemberUnknownLife(1, true, 0, true)));  // sector transition blip
                Advance(1000);
                Sync();
                Publish(Snap(503000, Member(1, true, 0, true, true, 1f)));   // back live
                Advance(1000);
                Sync();
                CrewAgent a = CrewAgentRegistry.GetAgent(Id(1, true));
                Check(a != null && a.Presence == AgentPresenceState.Alive, "P21 captain ALIVE through recreation cycle");
                Check(CrewAgentRegistry.DeathConfirmationCount == 0, "P21 captain never DEAD");
                Check(CrewAgentRegistry.TempUnavailableCount == 1, "P21 exactly one temp event (the blip)");
                Check(CrewAgentRegistry.AgentCount == 1 && CrewMemorySystem.AgentCount == 1, "P21 one agent, one memory store");
            }

            Console.WriteLine("CrewPresenceTests passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}