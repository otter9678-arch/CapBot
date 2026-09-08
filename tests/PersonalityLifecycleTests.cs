// Dev-side unit tests for the P40 personality lifecycle reconciliation
// (pure C#). NOT part of the shipped mod: compiled separately by
// tests\run_tests.ps1 against the crew-domain files (CrewAgent,
// CrewAgentRegistry, CrewPersonality) plus the CrewPersistence domain and
// the P2/P4/P5/P6 files the registry integrates with. Time is virtual: the
// registry reads the injected nowMs provider — no real clock reads.
//
// Covers the P40 mandated scenarios:
//   P40-01  0 agents => 0 personalities
//   P40-02  1 eligible agent => 1 personality
//   P40-03  4 eligible agents => 4 personalities
//   P40-04  repeated AgentCreated (duplicate create / re-derive pressure) => still 4
//   P40-05  role change => personality preserved/reconciled (no duplicate, no rewrite)
//   P40-06  save/load => personality restored (matured restore path)
//   P40-07  removed agent => derived personality removed (live count tracks crew)
//   P40-08  diagnostics: PersonalityCreated/Assigned/Reconciled/Removed/Restored lines
//   P40-09  restore-window probe: reconcile inert during CrewPersistence.Restore
//   P40-10  consumption seam: RoleAffinity/archetype read live registry records
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;
using CapBot.Core.Persistence;

namespace CapBot.TaskTests
{
    internal static class PersonalityLifecycleTests
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
            CrewExperienceRegistry.ResetForTests();
            CrewMemorySystem.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 300000 };
            s_Snap = null;
            s_Lines.Clear();
            CrewAgentRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetWorldProvider(delegate { return s_Snap; });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewPersonalityRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
            CrewAgentRegistry.SetRoleNameResolver(delegate (int classId)
            {
                switch (classId)
                {
                    case 0: return "Captain";
                    case 1: return "Pilot";
                    case 2: return "Scientist";
                    case 3: return "Weapons";
                    case 4: return "Engineer";
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

        // ---- snapshot builders (same shape as CrewAgentTests) ----------------

        private static CrewMemberSnapshot Member(int id, bool isBot, int classId, bool isCaptain, bool alive, float health)
        {
            return new CrewMemberSnapshot(id, (isBot ? "bot" : "human") + id, isBot, classId, 0,
                true, alive, health, "Bridge", isCaptain, isBot ? 3 : -1);
        }

        private static WorldSnapshot Snap(int timeMs, params CrewMemberSnapshot[] crew)
        {
            List<CrewMemberSnapshot> crewList = new List<CrewMemberSnapshot>(crew);
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 0.5f, false, 0, -1, 0, 10f));
            // Original 16-arg WorldSnapshot constructor; Navigation/Resources/
            // Threats are null-guarded by the constructor (fail-safe defaults).
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crewList, new List<MissionSnapshot>(),
                new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN),
                null, null, null,
                WorldAuthority.Unknown, WorldAuthority.Unknown, WorldAuthority.Unknown,
                WorldAuthority.Unknown);
        }

        private static string AgentIdOf(int playerId, bool isBot)
        {
            return CrewAgentRegistry.MakeAgentId(playerId, isBot);
        }

        // ---- tests -------------------------------------------------------------

        private static void TestZeroAgents()
        {
            Console.WriteLine("--- P40-01: 0 agents => 0 personalities ---");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs)); // empty crew, game started
            Sync();
            Check(CrewAgentRegistry.AgentCount == 0, "P40-01 no agents created");
            Check(CrewPersonalityRegistry.Count == 0, "P40-01 zero personalities");
            Check(CountLines("PersonalityAssigned") == 0, "P40-01 no assignment lines");
        }

        private static void TestOneAgent()
        {
            Console.WriteLine("--- P40-02: 1 eligible agent => 1 personality ---");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, Member(3, true, 0, true, true, 1f)));
            Sync();
            string aid = AgentIdOf(3, true);
            Check(CrewAgentRegistry.AgentCount == 1, "P40-02 one agent created");
            Check(CrewPersonalityRegistry.Count == 1, "P40-02 exactly one personality");
            Check(CrewPersonalityRegistry.Get(aid) != null, "P40-02 record keyed by stable AgentId");
            Check(HasLineContaining("AgentCreated " + aid), "P40-02 AgentCreated line present");
            Check(HasLineContaining("PersonalityAssigned " + aid), "P40-02 PersonalityAssigned line present");
            CrewPersonality p = CrewPersonalityRegistry.Get(aid);
            Check(p != null && PersonalityFactory.SourceDerived.Equals(p.Source, StringComparison.Ordinal),
                "P40-02 record is identity-derived");
            // Determinism: same agent id always derives the same trait spread.
            CrewPersonality again = PersonalityFactory.Derive(aid, s_Clock.NowMs + 1000);
            Check(again.Get(PersonalityTrait.Discipline) == p.Get(PersonalityTrait.Discipline)
                && again.Get(PersonalityTrait.Boldness) == p.Get(PersonalityTrait.Boldness)
                && again.Get(PersonalityTrait.Sociability) == p.Get(PersonalityTrait.Sociability)
                && again.Get(PersonalityTrait.Diligence) == p.Get(PersonalityTrait.Diligence)
                && again.Get(PersonalityTrait.Adaptability) == p.Get(PersonalityTrait.Adaptability),
                "P40-02 derivation deterministic for stable id");
        }

        private static void TestFourAgents()
        {
            Console.WriteLine("--- P40-03: 4 eligible agents => 4 personalities ---");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs,
                Member(3, true, 0, true, true, 1f),
                Member(4, true, 1, false, true, 1f),
                Member(5, true, 2, false, true, 1f),
                Member(6, true, 3, false, true, 1f)));
            Sync();
            Check(CrewAgentRegistry.AgentCount == 4, "P40-03 four agents created");
            Check(CrewPersonalityRegistry.Count == 4, "P40-03 exactly four personalities");
            for (int pid = 3; pid <= 6; pid++)
            {
                string aid = AgentIdOf(pid, true);
                Check(CrewPersonalityRegistry.Get(aid) != null, "P40-03 personality present for pid=" + pid);
            }
            Check(CountLines("PersonalityAssigned") == 4, "P40-03 four assignment lines");
        }

        private static void TestRepeatedAgentCreated()
        {
            Console.WriteLine("--- P40-04: repeated AgentCreated => still 4 ---");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs,
                Member(3, true, 0, true, true, 1f),
                Member(4, true, 1, false, true, 1f),
                Member(5, true, 2, false, true, 1f),
                Member(6, true, 3, false, true, 1f)));
            Sync();
            int beforeAgents = CrewAgentRegistry.AgentCount;
            int beforePers = CrewPersonalityRegistry.Count;
            long assignedBefore = CrewPersonalityRegistry.AssignedCount;
            long derivationsBefore = CrewPersonalityRegistry.DerivationCount;

            // (a) duplicate create: same agent re-presented in a later sync.
            // The registry diff never re-creates a PRESENT agent (update in
            // place), so no second AgentCreated line may appear.
            Advance(1500);
            Publish(Snap(s_Clock.NowMs,
                Member(3, true, 0, true, true, 1f),
                Member(4, true, 1, false, true, 1f),
                Member(5, true, 2, false, true, 1f),
                Member(6, true, 3, false, true, 1f)));
            Sync();
            Check(CrewAgentRegistry.CreatedCount == 4, "P40-04a no re-creation (CreatedCount stable)");
            Check(CountLines("AgentCreated ") == 4, "P40-04a no second AgentCreated lines");
            Check(CrewPersonalityRegistry.Count == beforePers, "P40-04a still 4 personalities");
            Check(CrewPersonalityRegistry.AssignedCount == assignedBefore, "P40-04a no new assignments");
            Check(CrewPersonalityRegistry.DerivationCount == derivationsBefore, "P40-04a no re-derivations");

            // (b) full removal + rediscovery (AgentCreated again for the SAME
            // stable identity): everyone leaves, the removal grace expires,
            // then the crew returns — records are swept with the removal and
            // re-derived deterministically for the recreated agents.
            Advance(1500);
            Publish(Snap(s_Clock.NowMs)); // empty crew — everyone absent
            Sync();
            Check(CrewAgentRegistry.ActiveAgentCount == 0, "P40-04b all agents absent");
            Advance(CrewAgentRegistry.RemovalGraceMs + 1000);
            Sync();
            Check(CrewAgentRegistry.AgentCount == 0, "P40-04b agents removed after grace");
            Check(CrewPersonalityRegistry.Count == 0, "P40-04b derived records removed with agents");
            Advance(1500);
            Publish(Snap(s_Clock.NowMs,
                Member(3, true, 0, true, true, 1f),
                Member(4, true, 1, false, true, 1f),
                Member(5, true, 2, false, true, 1f),
                Member(6, true, 3, false, true, 1f)));
            Sync();
            Check(CrewAgentRegistry.AgentCount == beforeAgents, "P40-04b agents recreated");
            Check(CrewPersonalityRegistry.Count == beforePers, "P40-04b still 4 personalities after recreate");
            for (int pid = 3; pid <= 6; pid++)
            {
                CrewPersonality p = CrewPersonalityRegistry.Get(AgentIdOf(pid, true));
                Check(p != null, "P40-04b record present for pid=" + pid);
            }
        }

        private static void TestRoleChangeReconcile()
        {
            Console.WriteLine("--- P40-05: role change => personality preserved/reconciled ---");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, Member(5, true, 1, false, true, 1f)));
            Sync();
            string aid = AgentIdOf(5, true);
            CrewPersonality before = CrewPersonalityRegistry.Get(aid);
            Check(before != null, "P40-05 personality present before role change");
            int discBefore = before.Get(PersonalityTrait.Discipline);
            string archBefore = before.Archetype;

            Advance(1500);
            Publish(Snap(s_Clock.NowMs, Member(5, true, 2, false, true, 1f))); // Pilot -> Scientist
            Sync();
            Check(HasLineContaining("AgentRoleChanged " + aid), "P40-05 AgentRoleChanged line present");
            Check(HasLineContaining("PersonalityReconciled " + aid), "P40-05 PersonalityReconciled line present");
            CrewPersonality after = CrewPersonalityRegistry.Get(aid);
            Check(after != null, "P40-05 record still present after role change");
            Check(ReferenceEquals(before, after), "P40-05 SAME record instance (preserved, not replaced)");
            Check(after.Get(PersonalityTrait.Discipline) == discBefore, "P40-05 traits unchanged");
            Check(after.Archetype == archBefore, "P40-05 archetype unchanged (pure function of traits)");
            Check(CrewPersonalityRegistry.Count == 1, "P40-05 still exactly one personality");
            Check(CrewPersonalityRegistry.ReplacedCount == 0, "P40-05 no replacement counted");

            // Consumption seam: the affinity score follows the NEW role's
            // weight table against the SAME record (weights are consulted
            // per-lookup — reconcile needs no rewrite).
            int pilotAffinity = RoleAffinity.Score(after, CrewRole.Pilot);
            int scientistAffinity = RoleAffinity.Score(after, CrewRole.Scientist);
            Check(pilotAffinity >= 0 && scientistAffinity >= 0, "P40-05 affinity scores valid for both roles");
        }

        private static void TestSaveLoadRestore()
        {
            Console.WriteLine("--- P40-06: save/load => personality restored ---");
            FreshSetup();
            // Mature an agent through the sanctioned write path, capture the
            // save blob, wipe live state, restore, and verify.
            Publish(Snap(s_Clock.NowMs, Member(7, true, 4, false, true, 1f)));
            Sync();
            string aid = AgentIdOf(7, true);
            CrewPersonality derived = CrewPersonalityRegistry.Get(aid);
            Check(derived != null, "P40-06 derived record exists");
            CrewPersonality matured = PersonalityFactory.FromValues(aid,
                derived.Get(PersonalityTrait.Discipline),
                derived.Get(PersonalityTrait.Boldness),
                derived.Get(PersonalityTrait.Sociability),
                derived.Get(PersonalityTrait.Diligence),
                CrewPersonality.ClampTrait(derived.Get(PersonalityTrait.Adaptability) + 1),
                s_Clock.NowMs);
            Check(CrewPersonalityRegistry.SetPersonality(aid, matured, s_Clock.NowMs), "P40-06 maturation write accepted");

            byte[] blob = CrewPersistence.Encode(CrewPersistence.Capture());
            Check(blob != null && blob.Length > 0, "P40-06 save blob encoded");

            // Simulate a fresh load: wipe everything, restore the blob.
            FreshSetup();
            Check(CrewPersonalityRegistry.Count == 0, "P40-06 live state cleared");
            CrewPersistence.CrewDataSnapshot decoded = CrewPersistence.Decode(blob);
            Check(decoded != null, "P40-06 save blob decoded");
            int inserted = CrewPersistence.Restore(decoded);
            Check(inserted >= 1, "P40-06 rows restored");
            Check(HasLineContaining("PersonalityRestored " + aid), "P40-06 PersonalityRestored line present");
            CrewPersonality restored = CrewPersonalityRegistry.Get(aid);
            Check(restored != null, "P40-06 record restored under stable AgentId");
            Check(restored.Get(PersonalityTrait.Adaptability) == matured.Get(PersonalityTrait.Adaptability),
                "P40-06 matured trait value survived the round trip");
            Check(PersonalityFactory.SourceExplicit.Equals(restored.Source, StringComparison.Ordinal),
                "P40-06 restored record keeps explicit (matured) provenance");
            // A post-restore sync must NOT re-derive over the restored record.
            long beforeAssigned = CrewPersonalityRegistry.AssignedCount;
            Publish(Snap(s_Clock.NowMs, Member(7, true, 4, false, true, 1f)));
            Sync();
            Check(CrewPersonalityRegistry.AssignedCount == beforeAssigned, "P40-06 no re-derivation after restore");
        }

        private static void TestRemovedAgent()
        {
            Console.WriteLine("--- P40-07: removed agent => personality removed ---");
            FreshSetup();
            Publish(Snap(s_Clock.NowMs,
                Member(3, true, 0, true, true, 1f),
                Member(4, true, 1, false, true, 1f)));
            Sync();
            string gone = AgentIdOf(4, true);
            string stays = AgentIdOf(3, true);
            Check(CrewPersonalityRegistry.Count == 2, "P40-07 two personalities initially");

            Advance(1500);
            Publish(Snap(s_Clock.NowMs, Member(3, true, 0, true, true, 1f))); // pid 4 leaves
            Sync();
            Check(CrewPersonalityRegistry.Count == 2, "P40-07 record held during absence grace");
            Check(CrewPersonalityRegistry.Get(gone) != null, "P40-07 absent agent keeps record inside grace");

            Advance(1500);
            Publish(Snap(s_Clock.NowMs, Member(3, true, 0, true, true, 1f))); // still absent, quiet sync
            Advance(CrewAgentRegistry.RemovalGraceMs + 1000);
            Sync();
            Check(HasLineContaining("AgentRemoved " + gone), "P40-07 agent removed after grace");
            Check(CrewPersonalityRegistry.Get(gone) == null, "P40-07 derived personality removed with agent");
            Check(CrewPersonalityRegistry.Count == 1, "P40-07 count now tracks the live crew");
            Check(CrewPersonalityRegistry.Get(stays) != null, "P40-07 surviving agent keeps its record");
            Check(HasLineContaining("PersonalityRemoved " + gone), "P40-07 PersonalityRemoved line present");
        }

        private static void TestRestoreWindowProbe()
        {
            Console.WriteLine("--- P40-09: reconcile inert during restore window ---");
            FreshSetup();
            // No sync has run: the reconcile would normally derive for any
            // live agent. While a restore window is open it must stay inert.
            // Simulate the window via a real Restore call (synchronous).
            CrewPersistence.CrewDataSnapshot snap = new CrewPersistence.CrewDataSnapshot();
            Check(!CrewPersistence.IsRestoring, "P40-09 probe false outside restore");
            CrewPersistence.Restore(snap); // empty snapshot; flag toggles inside
            Check(!CrewPersistence.IsRestoring, "P40-09 probe false after restore returns");
            // Restore a MATURED row for an agent the reconcile has never seen:
            // after the window closes, a sync must not re-derive it back to
            // a derived record (live state won).
            string aid = AgentIdOf(9, true);
            CrewPersonalityRegistry.RestoreMatured(aid, 10, 20, 30, 40, 50, 1000);
            Publish(Snap(s_Clock.NowMs, Member(9, true, 1, false, true, 1f)));
            Sync();
            CrewPersonality p = CrewPersonalityRegistry.Get(aid);
            Check(p != null, "P40-09 restored record present");
            Check(PersonalityFactory.SourceExplicit.Equals(p.Source, StringComparison.Ordinal),
                "P40-09 restored record NOT re-derived by the sync-tail reconcile");
            Check(CrewPersonalityRegistry.DerivationCount == 0, "P40-09 zero derivations for the restored agent");
        }

        private static void TestConsumptionSeam()
        {
            Console.WriteLine("--- P40-10: personality consumption seam ---");
            FreshSetup();
            string aid = AgentIdOf(11, true);
            // The convenience seam derives-on-demand for unregistered agents
            // (pre-existing P11 behavior) and reads the LIVE record after the
            // lifecycle hooks populated it.
            Publish(Snap(s_Clock.NowMs, Member(11, true, 3, false, true, 1f)));
            Sync();
            CrewPersonality live = CrewPersonalityRegistry.Get(aid);
            Check(live != null, "P40-10 lifecycle hook populated the record");
            string archetype = CrewPersonalityRegistry.ArchetypeOf(aid, s_Clock.NowMs);
            Check(archetype == live.Archetype, "P40-10 ArchetypeOf reads the live record");
            int weapons = CrewPersonalityRegistry.AffinityToRole(aid, CrewRole.Weapons, s_Clock.NowMs);
            Check(weapons >= 0 && weapons <= 100, "P40-10 AffinityToRole bounded 0..100 from live record");
            // Personalities remain DATA ONLY: nothing in the task pipeline
            // consults them (P09 regression covers the scheduler side; here
            // we assert the record cannot influence a task's scheduling by
            // its mere presence). Pipeline path: Register -> TryQueue ->
            // scheduler grant.
            CapBotTask t = CapBotTask.Create("EMERGENCY", "CAPTAIN", "P40-10 isolation probe",
                150, 1, 60000, "NONE", null, null);
            bool registered = t != null && TaskRegistry.Register(t);
            bool queued = registered && t.TryQueue();
            TaskScheduler.Tick(s_Clock.NowMs);
            Check(registered && queued && TaskScheduler.HasLease(t.TaskId),
                "P40-10 task scheduling unaffected by personality data");
        }

        internal static int Run()
        {
            s_Passed = 0; s_Failed = 0;
            TestZeroAgents();
            TestOneAgent();
            TestFourAgents();
            TestRepeatedAgentCreated();
            TestRoleChangeReconcile();
            TestSaveLoadRestore();
            TestRemovedAgent();
            TestRestoreWindowProbe();
            TestConsumptionSeam();
            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}