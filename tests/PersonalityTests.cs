// Dev-side unit tests for the Phase 11 crew personality layer (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure crew-domain files (CrewPersonality) plus the Phase 10
// crew-agent domain it builds on. Time is virtual: every timestamp is an
// explicit nowMs argument — no real clock reads, no sleeping.
//
// Covers the Phase 11 mandated scenarios (master prompt structure):
//   P01 identity-derived personality (deterministic, individual per agent)
//   P02 archetype assignment (dominant traits, tie-first, balanced)
//   P03 role affinity scoring (role-differentiated, bounded)
//   P04 trait clamping (out-of-range explicit values)
//   P05 explicit personality registration
//   P06 neutral personality
//   P07 registry stability (same id => same record; replacement counted)
//   P08 no cross-agent personality contamination
//   P09 personalities never influence scheduling/claims/execution
//   P10 no cross-round personality drift (derived records identical)
//   P11 bounded personality registry (refusal at cap, deterministic)
//   P12 no invalid-player data ingestion (fail-safe inputs refused)
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;

namespace CapBot.TaskTests
{
    internal static class PersonalityTests
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
            CrewPersonalityRegistry.ResetForTests();
            CrewAgentRegistry.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            s_Clock = new VirtualClock { NowMs = 400000 };
            s_Snap = null;
            s_Lines.Clear();
            CrewPersonalityRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetWorldProvider(delegate { return s_Snap; });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
        }

        private static void Advance(int ms) { s_Clock.NowMs += ms; }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // Snapshot/crew helpers mirroring CrewAgentTests (crew sections drive
        // agent creation; personalities then derive for those agents).
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

        private static string Id(int pid, bool isBot)
        {
            return CrewAgentRegistry.MakeAgentId(pid, isBot);
        }

        private static int Sync()
        {
            return CrewAgentRegistry.Sync(s_Clock.NowMs);
        }

        private static int[] Traits(CrewPersonality p)
        {
            return new int[5]
            {
                p.Get(PersonalityTrait.Discipline),
                p.Get(PersonalityTrait.Boldness),
                p.Get(PersonalityTrait.Sociability),
                p.Get(PersonalityTrait.Diligence),
                p.Get(PersonalityTrait.Adaptability),
            };
        }

        private static bool SameTraits(CrewPersonality a, CrewPersonality b)
        {
            int[] ta = Traits(a);
            int[] tb = Traits(b);
            for (int i = 0; i < 5; i++)
            {
                if (ta[i] != tb[i]) return false;
            }
            return true;
        }

        internal static int Run()
        {
            // ---- P01: identity-derived personality ----------------------------
            FreshSetup();
            string a = Id(11, true);
            CrewPersonality p1 = CrewPersonalityRegistry.DeriveFor(a, s_Clock.NowMs);
            Check(p1 != null, "P01 derived personality exists");
            Check(p1.AgentId == a, "P01 AgentId matches");
            Check(p1.Source == "derived", "P01 derived source");
            bool traitsInRange = true;
            for (int i = 0; i < 5; i++)
            {
                int v = p1.Get((PersonalityTrait)i);
                if (v < 0 || v > 100) traitsInRange = false;
            }
            Check(traitsInRange, "P01 all traits within 0..100");
            // Determinism: independent derivation reproduces the exact record.
            CrewPersonality p1b = PersonalityFactory.Derive(a, s_Clock.NowMs);
            Check(SameTraits(p1, p1b), "P01 re-derivation identical");
            Check(p1.Archetype == p1b.Archetype, "P01 archetype identical");
            // Distinctness across many agents (identity-derived spread).
            HashSet<string> sigs = new HashSet<string>();
            for (int pid = 1; pid <= 40; pid++)
            {
                CrewPersonality px = PersonalityFactory.Derive(Id(pid, true), 1000);
                int[] t = Traits(px);
                sigs.Add(t[0] + "," + t[1] + "," + t[2] + "," + t[3] + "," + t[4] + "," + px.Archetype);
            }
            Check(sigs.Count >= 35, "P01 personalities distinct across agents (sigs=" + sigs.Count + ")");
            // Registry reuse: same id returns the SAME record object.
            CrewPersonality p1c = CrewPersonalityRegistry.DeriveFor(a, s_Clock.NowMs);
            Check(p1c == p1, "P01 registry returns existing record");
            Check(CrewPersonalityRegistry.DerivationCount == 1, "P01 derivation counted once");
            // Integration with the Phase 10 agent identity: the derived id is
            // exactly the id the crew registry assigns for the same crew member,
            // and personality data is untouched by agent syncs.
            s_Snap = Snap(s_Clock.NowMs, Member(11, true, 4, false, true, 0.9f));
            Advance(1000);
            int mutations = Sync();
            Check(mutations >= 1, "P01 crew sync created agent");
            Check(CrewAgentRegistry.GetAgent(a) != null, "P01 agent id matches personality id");
            Check(CrewPersonalityRegistry.Get(a) == p1, "P01 personality untouched by agent sync");

            // ---- P02: archetype assignment --------------------------------------
            FreshSetup();
            CrewPersonality s1 = PersonalityFactory.FromValues(Id(1, true), 90, 20, 20, 20, 20, 1000);
            Check(s1.Archetype == PersonalityArchetypes.Sentinel, "P02 dominant Discipline -> SENTINEL");
            CrewPersonality s2 = PersonalityFactory.FromValues(Id(2, true), 20, 90, 20, 20, 20, 1000);
            Check(s2.Archetype == PersonalityArchetypes.Vanguard, "P02 dominant Boldness -> VANGUARD");
            CrewPersonality s3 = PersonalityFactory.FromValues(Id(3, true), 20, 20, 90, 20, 20, 1000);
            Check(s3.Archetype == PersonalityArchetypes.Coordinator, "P02 dominant Sociability -> COORDINATOR");
            CrewPersonality s4 = PersonalityFactory.FromValues(Id(4, true), 20, 20, 20, 90, 20, 1000);
            Check(s4.Archetype == PersonalityArchetypes.Technician, "P02 dominant Diligence -> TECHNICIAN");
            CrewPersonality s5 = PersonalityFactory.FromValues(Id(5, true), 20, 20, 20, 20, 90, 1000);
            Check(s5.Archetype == PersonalityArchetypes.Adapter, "P02 dominant Adaptability -> ADAPTER");
            CrewPersonality s6 = PersonalityFactory.FromValues(Id(6, true), 40, 40, 40, 40, 40, 1000);
            Check(s6.Archetype == PersonalityArchetypes.Balanced, "P02 no dominant trait -> BALANCED");
            // Tie at threshold resolves to the FIRST trait in enum order.
            CrewPersonality s7 = PersonalityFactory.FromValues(Id(7, true), 70, 70, 20, 20, 20, 1000);
            Check(s7.Archetype == PersonalityArchetypes.Sentinel, "P02 threshold tie -> first trait wins");
            // Archetype vocabulary is static — never invented at runtime.
            CrewPersonality s8 = PersonalityFactory.FromValues(Id(8, true), 69, 69, 69, 69, 69, 1000);
            Check(s8.Archetype == PersonalityArchetypes.Balanced, "P02 sub-threshold all -> BALANCED");
            Check(CrewPersonalityRegistry.SetPersonality(Id(8, true), s8, s_Clock.NowMs), "P02 register for readback");
            Check(CrewPersonalityRegistry.Get(Id(8, true)).Archetype == "BALANCED", "P02 archetype token stable");

            // ---- P03: role affinity scoring --------------------------------------
            FreshSetup();
            // An extreme engineer-leaning personality scores higher for Engineer
            // than for Captain; the reverse holds for a captain-leaning one.
            CrewPersonality eng = PersonalityFactory.FromValues(Id(1, true), 30, 5, 5, 100, 15, 1000);
            int engScore = RoleAffinity.Score(eng, CrewRole.Engineer);
            int engCaptain = RoleAffinity.Score(eng, CrewRole.Captain);
            Check(engScore >= 0 && engScore <= 100, "P03 engineer score bounded");
            Check(engScore > engCaptain, "P03 diligent personality prefers Engineer over Captain");
            CrewPersonality cap = PersonalityFactory.FromValues(Id(2, true), 100, 20, 75, 5, 5, 1000);
            Check(RoleAffinity.Score(cap, CrewRole.Captain) > RoleAffinity.Score(cap, CrewRole.Engineer),
                "P03 disciplined personality prefers Captain over Engineer");
            // A weapons-leaning personality prefers Weapons over Pilot.
            CrewPersonality wpn = PersonalityFactory.FromValues(Id(3, true), 30, 100, 5, 20, 5, 1000);
            Check(RoleAffinity.Score(wpn, CrewRole.Weapons) > RoleAffinity.Score(wpn, CrewRole.Pilot),
                "P03 bold personality prefers Weapons over Pilot");
            // A pilot-leaning personality prefers Pilot over Scientist.
            CrewPersonality plt = PersonalityFactory.FromValues(Id(4, true), 20, 70, 10, 5, 100, 1000);
            Check(RoleAffinity.Score(plt, CrewRole.Pilot) > RoleAffinity.Score(plt, CrewRole.Scientist),
                "P03 adaptable personality prefers Pilot over Scientist");
            // Neutral personality scores identically for all roles (every table
            // sums to 100, so all-50 traits always score exactly 50).
            CrewPersonality neu = PersonalityFactory.Neutral(Id(5, true), 1000);
            int capN = RoleAffinity.Score(neu, CrewRole.Captain);
            int engN = RoleAffinity.Score(neu, CrewRole.Engineer);
            int pilN = RoleAffinity.Score(neu, CrewRole.Pilot);
            Check(capN == 50 && engN == 50 && pilN == 50, "P03 neutral scores 50 across roles");
            // Unknown/Other roles use the uniform table.
            Check(RoleAffinity.Score(eng, CrewRole.Other) == RoleAffinity.Score(eng, CrewRole.Unknown),
                "P03 unknown/other uniform table");
            Check(RoleAffinity.Score(eng, CrewRole.Other) >= 0 && RoleAffinity.Score(eng, CrewRole.Other) <= 100,
                "P03 unknown/other bounded");
            // Null personality => -1 (invalid query, never fabricated).
            Check(RoleAffinity.Score(null, CrewRole.Engineer) == -1, "P03 null personality refused");
            // Weight tables always sum to exactly 100 (score stays 0..100).
            CrewRole[] allRoles = new CrewRole[7]
            {
                CrewRole.Captain, CrewRole.Pilot, CrewRole.Scientist,
                CrewRole.Weapons, CrewRole.Engineer, CrewRole.Unknown, CrewRole.Other,
            };
            bool weightsSum = true;
            bool weightsImmutable = true;
            for (int r = 0; r < allRoles.Length; r++)
            {
                int[] w = RoleAffinity.Weights(allRoles[r]);
                int sum = w[0] + w[1] + w[2] + w[3] + w[4];
                if (sum != 100) weightsSum = false;
                w[0] = 999; w[4] = 999; // mutate the copy
                int[] w2 = RoleAffinity.Weights(allRoles[r]);
                if (w2[0] == 999 || w2[4] == 999) weightsImmutable = false;
            }
            Check(weightsSum, "P03 weight tables sum to 100");
            Check(weightsImmutable, "P03 weight tables returned as copies");
            // Convenience path derives on demand and agrees with the direct one.
            FreshSetup();
            string pid4 = Id(4, true);
            int direct = RoleAffinity.Score(PersonalityFactory.Derive(pid4, 1000), CrewRole.Captain);
            int viaRegistry = CrewPersonalityRegistry.AffinityToRole(pid4, CrewRole.Captain, 1000);
            Check(direct == viaRegistry, "P03 affinity convenience derives identically");
            Check(CrewPersonalityRegistry.Get(pid4) != null, "P03 affinity convenience registers derivation");

            // ---- P04: trait clamping ----------------------------------------------
            FreshSetup();
            CrewPersonality cl = PersonalityFactory.FromValues(Id(1, true), -50, 250, 40, 40, 40, 1000);
            Check(cl.Get(PersonalityTrait.Discipline) == 0, "P04 negative clamped to 0");
            Check(cl.Get(PersonalityTrait.Boldness) == 100, "P04 over-range clamped to 100");
            Check(cl.Get(PersonalityTrait.Sociability) == 40, "P04 in-range preserved");
            // Registry Set must not accept mismatched records; factory refuses bad ids.
            Check(!CrewPersonalityRegistry.SetPersonality(Id(2, true), cl, 1000), "P04 mismatched record refused");
            Check(CrewPersonalityRegistry.RefusedCount == 1, "P04 refusal counted");
            Check(PersonalityFactory.Derive(null, 1000) == null, "P04 null agentId refused");
            Check(PersonalityFactory.Derive("", 1000) == null, "P04 empty agentId refused");
            Check(PersonalityFactory.Derive("XX:notanagent", 1000) == null, "P04 non-AGT id refused");
            Check(PersonalityFactory.Derive(new string('A', 40), 1000) == null, "P04 overlong id refused");
            Check(PersonalityFactory.Neutral("AGT:", 1000) == null, "P04 bare prefix refused");
            Check(CrewPersonalityRegistry.DeriveFor("not-an-id", 1000) == null, "P04 registry refuses invalid id");
            // Out-of-range trait READ returns -1 (invalid query).
            Check(cl.Get((PersonalityTrait)9) == -1, "P04 out-of-range trait read -1");

            // ---- P05: explicit personality registration ---------------------------
            FreshSetup();
            string e1 = Id(21, true);
            CrewPersonality ep = PersonalityFactory.FromValues(e1, 10, 95, 30, 60, 25, s_Clock.NowMs);
            Check(CrewPersonalityRegistry.SetPersonality(e1, ep, s_Clock.NowMs), "P05 explicit registration accepted");
            Check(CrewPersonalityRegistry.Get(e1) == ep, "P05 lookup returns record");
            Check(CrewPersonalityRegistry.AssignedCount == 1, "P05 assignment counted");
            Check(HasLineContaining("PersonalityAssigned " + e1), "P05 assignment emitted");
            // Replacement allowed and counted, never a size change.
            CrewPersonality ep2 = PersonalityFactory.FromValues(e1, 80, 10, 30, 60, 25, s_Clock.NowMs);
            Check(CrewPersonalityRegistry.SetPersonality(e1, ep2, s_Clock.NowMs), "P05 replacement accepted");
            Check(CrewPersonalityRegistry.Get(e1) == ep2, "P05 replacement visible");
            Check(CrewPersonalityRegistry.Count == 1, "P05 size unchanged on replacement");
            Check(CrewPersonalityRegistry.ReplacedCount == 1, "P05 replacement counted");
            Check(CrewPersonalityRegistry.AssignedCount == 1, "P05 assigned count stable");
            // Null record refused.
            Check(!CrewPersonalityRegistry.SetPersonality(e1, null, s_Clock.NowMs), "P05 null record refused");

            // ---- P06: neutral personality ------------------------------------------
            FreshSetup();
            string n1 = Id(31, true);
            CrewPersonality np = CrewPersonalityRegistry.DeriveFor(n1, s_Clock.NowMs); // derived first
            CrewPersonality nExplicit = PersonalityFactory.Neutral(n1, s_Clock.NowMs);
            Check(nExplicit != null && nExplicit.Source == "neutral", "P06 neutral source");
            bool neutralAll = true;
            for (int i = 0; i < 5; i++)
            {
                if (nExplicit.Get((PersonalityTrait)i) != 50) neutralAll = false;
            }
            Check(neutralAll, "P06 neutral all-50");
            Check(nExplicit.Archetype == PersonalityArchetypes.Balanced, "P06 neutral -> BALANCED");
            // Fail-safe role: a derived-with-no-data agent still yields a usable
            // bounded personality (registry path never nulls out a valid id).
            Check(np != null, "P06 derived fallback exists for valid id");
            // Remove lifecycle hook.
            Check(CrewPersonalityRegistry.Remove(n1), "P06 remove accepted");
            Check(CrewPersonalityRegistry.Get(n1) == null, "P06 removed record absent");
            Check(!CrewPersonalityRegistry.Remove(n1), "P06 double remove refused");
            Check(HasLineContaining("PersonalityRemoved " + n1), "P06 removal emitted");
            // Diagnostic surfaces are bounded and deterministic.
            Check(CrewPersonalityRegistry.Lines().Count == CrewPersonalityRegistry.Count, "P06 Lines count matches");
            Check(CrewPersonalityRegistry.StatusLines().Count == 1, "P06 StatusLines single summary");

            // ---- P07: registry stability -------------------------------------------
            FreshSetup();
            string st = Id(41, true);
            CrewPersonality first = CrewPersonalityRegistry.DeriveFor(st, s_Clock.NowMs);
            Advance(30000);
            CrewPersonality second = CrewPersonalityRegistry.DeriveFor(st, s_Clock.NowMs);
            Check(first == second, "P07 same id same record across time");
            Advance(30000);
            CrewPersonality third = CrewPersonalityRegistry.DeriveFor(st, s_Clock.NowMs);
            Check(first == third, "P07 same id same record after long idle");
            Check(CrewPersonalityRegistry.DerivationCount == 1, "P07 derivation not repeated");
            // SetPersonality explicitly replaces; DeriveFor still returns existing.
            CrewPersonality manual = PersonalityFactory.FromValues(st, 5, 5, 5, 5, 100, s_Clock.NowMs);
            Check(CrewPersonalityRegistry.SetPersonality(st, manual, s_Clock.NowMs), "P07 explicit replace ok");
            Check(CrewPersonalityRegistry.DeriveFor(st, s_Clock.NowMs) == manual, "P07 DeriveFor returns existing after explicit set");

            // ---- P08: no cross-agent contamination ---------------------------------
            FreshSetup();
            string c1 = Id(51, true);
            string c2 = Id(52, true);
            CrewPersonality q1 = CrewPersonalityRegistry.DeriveFor(c1, s_Clock.NowMs);
            CrewPersonality q2 = CrewPersonalityRegistry.DeriveFor(c2, s_Clock.NowMs);
            Check(q1 != q2, "P08 distinct records per agent");
            Check(!SameTraits(q1, q2), "P08 trait vectors differ for distinct agents");
            // Records are immutable (no setters) — assert the registry returns
            // exactly the registered object every time.
            Check(CrewPersonalityRegistry.Get(c1) == q1, "P08 lookup stable for agent 1");
            Check(CrewPersonalityRegistry.Get(c2) == q2, "P08 lookup stable for agent 2");
            // Bot vs human same pid produce different ids (and personalities).
            Check(Id(7, true) != Id(7, false), "P08 bot/human ids distinct");
            Check(CrewPersonalityRegistry.DeriveFor(Id(7, true), 1000)
                != CrewPersonalityRegistry.DeriveFor(Id(7, false), 1000),
                "P08 bot/human personalities distinct");

            // ---- P09: personalities never influence scheduling ----------------------
            FreshSetup();
            CapBotTask tA = CapBotTask.Create("TEST", "BOT:60", "personality isolation A", 5, 0, 60000, null, null, null);
            CapBotTask tB2 = CapBotTask.Create("TEST", "CAPTAIN", "personality isolation B", 5, 0, 60000, null, null, null);
            Check(tA != null && tB2 != null, "P09 tasks created");
            TaskRegistry.Register(tA);
            TaskRegistry.Register(tB2);
            Check(tA.TryQueue() && tB2.TryQueue(), "P09 tasks queued");
            // Derive personalities for both owners BEFORE scheduling: the
            // scheduler must be unaware they exist.
            CrewPersonalityRegistry.DeriveFor(Id(60, true), s_Clock.NowMs);
            CrewPersonalityRegistry.DeriveFor(Id(61, false), s_Clock.NowMs);
            int basePriorityA = tA.Priority;
            int granted1 = TaskScheduler.Tick(s_Clock.NowMs);
            Check(granted1 >= 1, "P09 scheduler grants normally");
            Check(TaskScheduler.HasLease(tA.TaskId), "P09 lease taken for owner A");
            // Consume the grant with a real claim + start (owner A now busy via
            // Running, the same owner-busy gate P4 defines — no personality
            // involvement).
            Check(ExecutionClaims.TryClaim(tA.TaskId, "EXECUTE", 0, "BOT:60", "T", s_Clock.NowMs)
                == ClaimResult.Granted,
                "P09 claim granted for owner A");
            Check(tA.TryStart(), "P09 task A running");
            // Personality churn between ticks: derive, replace, remove — none of
            // it may change scheduling outcomes.
            CrewPersonalityRegistry.SetPersonality(Id(62, true),
                PersonalityFactory.FromValues(Id(62, true), 1, 2, 3, 4, 5, s_Clock.NowMs), s_Clock.NowMs);
            CrewPersonalityRegistry.Remove(Id(61, false));
            CrewPersonalityRegistry.DeriveFor(Id(63, true), s_Clock.NowMs);
            Check(tA.Priority == basePriorityA, "P09 priority untouched by personality churn");
            Advance(1000);
            CapBotTask tC = CapBotTask.Create("TEST", "BOT:60", "personality isolation C", 9, 0, 60000, null, null, null);
            TaskRegistry.Register(tC);
            tC.TryQueue();
            TaskScheduler.Tick(s_Clock.NowMs);
            Check(!TaskScheduler.HasLease(tC.TaskId), "P09 busy owner still not granted (one grant per owner)");
            Check(TaskScheduler.HasLease(tB2.TaskId), "P09 other owner granted independently");
            // Claims remain deny-by-default when authority policy is absent.
            ExecutionClaims.ResetForTests();
            Check(ExecutionClaims.TryClaim(tB2.TaskId, "EXECUTE", 0, "CAPTAIN", "T", s_Clock.NowMs)
                != ClaimResult.Granted,
                "P09 claims still deny-by-default (no authority policy)");
            Check(tB2.State == TaskState.Queued, "P09 refused claim left task queued");

            // ---- P10: no cross-round personality drift ------------------------------
            FreshSetup();
            string d1 = Id(71, true);
            CrewPersonality v1 = CrewPersonalityRegistry.DeriveFor(d1, 1000);
            int[] vec1 = Traits(v1);
            FreshSetup(); // simulated next session/round: registry cleared
            CrewPersonality v2 = CrewPersonalityRegistry.DeriveFor(d1, 2000);
            int[] vec2 = Traits(v2);
            Check(v1 != v2, "P10 fresh record object after reset");
            for (int i = 0; i < 5; i++)
            {
                Check(vec1[i] == vec2[i], "P10 trait " + i + " identical across rounds");
            }
            Check(v1.Archetype == v2.Archetype, "P10 archetype identical across rounds");

            // ---- P11: bounded personality registry ----------------------------------
            FreshSetup();
            for (int pid = 1; pid <= 32; pid++)
            {
                CrewPersonalityRegistry.DeriveFor(Id(pid, true), 1000);
            }
            Check(CrewPersonalityRegistry.Count == 32, "P11 cap reached exactly");
            Check(CrewPersonalityRegistry.DeriveFor(Id(33, true), 1000) == null, "P11 derivation refused at cap");
            Check(CrewPersonalityRegistry.RefusedCount >= 1, "P11 refusal counted");
            Check(CrewPersonalityRegistry.Count == 32, "P11 size unchanged after refusal");
            // Replacement of an existing record at cap is allowed (no size change).
            CrewPersonality rep = PersonalityFactory.FromValues(Id(1, true), 5, 50, 50, 50, 50, 1000);
            Check(CrewPersonalityRegistry.SetPersonality(Id(1, true), rep, 1000), "P11 replacement at cap accepted");
            Check(CrewPersonalityRegistry.Count == 32, "P11 size stable after replacement");
            // Free a slot, then derivation works again for the same id.
            Check(CrewPersonalityRegistry.Remove(Id(5, true)), "P11 slot freed");
            CrewPersonality re = CrewPersonalityRegistry.DeriveFor(Id(33, true), 1000);
            Check(re != null, "P11 derivation succeeds after slot freed");
            Check(CrewPersonalityRegistry.Count == 32, "P11 size stays bounded");

            // ---- P12: no invalid-player data ingestion ------------------------------
            FreshSetup();
            // Garbage ids never create records.
            Check(CrewPersonalityRegistry.Get(null) == null, "P12 null lookup null");
            Check(CrewPersonalityRegistry.Get("") == null, "P12 empty lookup null");
            Check(CrewPersonalityRegistry.Get("AGT:zzzz") == null, "P12 unknown id lookup null");
            Check(CrewPersonalityRegistry.AffinityToRole(null, CrewRole.Pilot, 1000) == -1, "P12 affinity refuses null id");
            Check(CrewPersonalityRegistry.ArchetypeOf("garbage", 1000) == null, "P12 archetype refuses garbage id");
            // Records with fabricated archetypes are refused by the registry.
            CrewPersonality forged = PersonalityFactory.FromValues(Id(81, true), 50, 50, 50, 50, 50, 1000);
            CrewPersonality badArch = new CrewPersonality(Id(81, true),
                new int[5] { 50, 50, 50, 50, 50 }, new string('X', 40), "explicit", 1000);
            Check(CrewPersonalityRegistry.SetPersonality(Id(81, true), forged, 1000), "P12 clean record accepted");
            Check(!CrewPersonalityRegistry.SetPersonality(Id(81, true), badArch, 1000), "P12 overlong archetype refused");
            // Mismatched owner record refused (identity integrity).
            CrewPersonality stolen = PersonalityFactory.FromValues(Id(82, true), 50, 50, 50, 50, 50, 1000);
            Check(!CrewPersonalityRegistry.SetPersonality(Id(83, true), stolen, 1000), "P12 record for other agent refused");
            Check(CrewPersonalityRegistry.Get(Id(83, true)) == null, "P12 no record created for mismatched key");
            Check(CrewPersonalityRegistry.Remove(null) == false, "P12 remove refuses null");
            Check(CrewPersonalityRegistry.Remove("") == false, "P12 remove refuses empty");

            Console.WriteLine("SUITE PersonalityTests passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}