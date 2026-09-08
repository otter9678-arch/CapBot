// Dev-side unit tests for the Phase 25 adaptive learning layer (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure learning-domain file (AdaptiveLearningDirector) plus the
// Phase 10/11/12 domains it builds on. Time is virtual: every timestamp is
// an explicit nowMs argument — no real clock reads, no sleeping.
//
// Covers the Phase 25 mandated scenarios (master prompt structure):
//   LEARN01 baseline arm + end-to-end crossing adjustment (completion rule)
//   LEARN02 direction rules: no-justification, adversity, completion precedence
//   LEARN03 deny-by-default authority (null/faulting/non-authoritative probe)
//   LEARN04 invalid inputs refused (id shape, outcome vocabulary, no record)
//   LEARN05 evaluation rate limit + crossing persistence through it
//   LEARN06 one-shot per crossing + clamp-bound consumption (no fabricated write)
//   LEARN07 multi-agent isolation + bounded set + hygiene decay/re-arm
//   LEARN08 funnel fail-safety (listener fault + experience fault isolation)
//   LEARN09 determinism + data-only proof (pipeline untouched) + post-reset inert
//   LEARN10 real Sync funnel end-to-end + sequential crossings + P11 counter shape
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;
using CapBot.Core.Learning;

namespace CapBot.TaskTests
{
    internal static class AdaptiveLearningTests
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
            AdaptiveLearningDirector.ResetForTests();
            CrewExperienceRegistry.ResetForTests();
            CrewPersonalityRegistry.ResetForTests();
            CrewAgentRegistry.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            s_Clock = new VirtualClock { NowMs = 600000 };
            s_Snap = null;
            s_Lines.Clear();
            CrewAgentRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetWorldProvider(delegate { return s_Snap; });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
            CrewPersonalityRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewExperienceRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            AdaptiveLearningDirector.SetAuthorityProbe(delegate { return true; });
            AdaptiveLearningDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
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

        // One funnel resolution: assign the (already terminal) task, then let
        // the real Sync observation resolve it — ClearTask fires the P12
        // accrual and (with the P25 hook) the learning evaluation.
        private static void ResolveViaFunnel(string agentId, CapBotTask task)
        {
            CrewAgentRegistry.AssignTask(agentId, task.TaskId, task.TaskType, null, s_Clock.NowMs);
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
        }

        // Direct accrual + learning pass (the natural pair — NotifyOutcome
        // reads the record P12 just wrote).
        private static void AccrueAndNotify(string agentId, string outcome)
        {
            CrewExperienceRegistry.RecordOutcome(agentId, outcome, s_Clock.NowMs);
            AdaptiveLearningDirector.NotifyOutcome(agentId, outcome, s_Clock.NowMs);
        }

        // Seeds an explicit all-50 personality so trait deltas are exact.
        private static void SeedNeutral(string agentId)
        {
            CrewPersonalityRegistry.SetPersonality(agentId,
                PersonalityFactory.FromValues(agentId, 50, 50, 50, 50, 50, s_Clock.NowMs), s_Clock.NowMs);
        }

        // Creates a terminal TEST task (created -> queued -> started -> completed).
        private static CapBotTask CompletedTask(string owner, string label)
        {
            CapBotTask t = CapBotTask.Create("TEST", owner, label, 5, 0, 60000, null, null, null);
            TaskRegistry.Register(t);
            t.TryQueue();
            t.TryStart();
            t.TryComplete();
            return t;
        }

        internal static int Run()
        {
            // ---- LEARN01: baseline arm + end-to-end crossing (completion rule) ----
            FreshSetup();
            string a = Id(11, true);
            SeedNeutral(a);
            AccrueAndNotify(a, CrewAgentRegistry.OutcomeCompleted);
            AdaptiveLearningDirector.LearningRecord r1 = AdaptiveLearningDirector.GetRecord(a);
            Check(r1 != null, "LEARN01 record created on first outcome");
            Check(r1.ArmedLevel == 1, "LEARN01 baseline armed at current level");
            Check(AdaptiveLearningDirector.BaselineArmedCount == 1, "LEARN01 baseline counted");
            Check(AdaptiveLearningDirector.CrossingCount == 0, "LEARN01 arm pass is observation-free");
            Check(AdaptiveLearningDirector.AdjustmentCount == 0, "LEARN01 no adjustment on arm pass");
            Check(CrewPersonalityRegistry.Get(a).Get(PersonalityTrait.Diligence) == 50, "LEARN01 traits untouched on arm pass");
            // 4 more completions, spaced past the evaluation gate: 50 xp at the
            // 5th completion => level 2 crossing.
            for (int i = 1; i <= 4; i++)
            {
                Advance(1100);
                AccrueAndNotify(a, CrewAgentRegistry.OutcomeCompleted);
            }
            Check(CrewExperienceRegistry.Get(a).Level == 2, "LEARN01 experience crossed into level 2");
            Check(AdaptiveLearningDirector.CrossingCount == 1, "LEARN01 crossing consumed exactly once");
            Check(AdaptiveLearningDirector.AdjustmentCount == 1, "LEARN01 adjustment performed");
            Check(r1.ArmedLevel == 2, "LEARN01 armed level tracks the crossing");
            Check(r1.AdjustCount == 1, "LEARN01 record adjust count");
            Check(r1.LastAdjustRule == "completion", "LEARN01 completion rule selected");
            CrewPersonality p1 = CrewPersonalityRegistry.Get(a);
            Check(p1 != null && p1.Get(PersonalityTrait.Diligence) == 51, "LEARN01 Diligence 50 -> 51");
            Check(p1.Get(PersonalityTrait.Discipline) == 50, "LEARN01 other traits untouched");
            Check(p1.Source == PersonalityFactory.SourceExplicit, "LEARN01 matured record is explicit-source");
            Check(HasLineContaining("PersonalityAdjusted LEARN:" + a + " trait=Diligence delta=+1 level=2 rule=completion"),
                "LEARN01 adjustment line emitted");
            Check(HasLineContaining("(replaced)"), "LEARN01 P11 replacement visible");
            Check(AdaptiveLearningDirector.LastAdjustment.IndexOf("trait=Diligence", StringComparison.Ordinal) > 0,
                "LEARN01 LastAdjustment readback");

            // ---- LEARN02: direction rules ------------------------------------------
            // (a) No justified direction: 3 completed (30xp) + 10 cancelled (20xp)
            //     = 50 xp => level-2 crossing; completion share 6/13, adversity
            //     0/13 — neither rule matches => crossing consumed silently.
            FreshSetup();
            string b = Id(21, true);
            SeedNeutral(b);
            AccrueAndNotify(b, CrewAgentRegistry.OutcomeCompleted);
            for (int i = 1; i <= 3; i++) { Advance(1100); AccrueAndNotify(b, CrewAgentRegistry.OutcomeCompleted); }
            for (int i = 1; i <= 10; i++) { Advance(1100); AccrueAndNotify(b, CrewAgentRegistry.OutcomeCancelled); }
            Check(CrewExperienceRegistry.Get(b).Level == 2, "LEARN02a xp crossed into level 2 (50)");
            Check(AdaptiveLearningDirector.CrossingCount == 1, "LEARN02a crossing consumed");
            Check(AdaptiveLearningDirector.AdjustmentCount == 0, "LEARN02a no fabricated adjustment");
            Check(AdaptiveLearningDirector.ClampedCount == 0, "LEARN02a no clamp either (rule never picked)");
            Check(CrewPersonalityRegistry.Get(b).Get(PersonalityTrait.Adaptability) == 50, "LEARN02a traits untouched");
            // (b) Adversity: 25 failed outcomes (2 pts each) => level-2 crossing
            // at the 25th; failed share 25/25 => adversity rule. FreshSetup:
            // (a) ran in this scope and CrossingCount is a cumulative global.
            FreshSetup();
            string c = Id(22, true);
            SeedNeutral(c);
            AccrueAndNotify(c, CrewAgentRegistry.OutcomeFailed);
            for (int i = 1; i <= 24; i++) { Advance(1100); AccrueAndNotify(c, CrewAgentRegistry.OutcomeFailed); }
            Check(CrewExperienceRegistry.Get(c).Level == 2, "LEARN02b xp crossed into level 2 (50)");
            Check(AdaptiveLearningDirector.CrossingCount == 1, "LEARN02b crossing consumed (fresh setup isolates the global)");
            Check(AdaptiveLearningDirector.GetRecord(c).LastAdjustRule == "adversity",
                "LEARN02b adversity rule selected for failure-dominant mix");
            Check(CrewPersonalityRegistry.Get(c).Get(PersonalityTrait.Adaptability) == 51, "LEARN02b Adaptability 50 -> 51");
            Check(CrewPersonalityRegistry.Get(c).Get(PersonalityTrait.Diligence) == 50, "LEARN02b Diligence untouched by adversity rule");
            Check(HasLineContaining("rule=adversity"), "LEARN02b adversity line emitted");
            // (c) Precedence: 1 failed + 6 completed — completion and adversity
            // both match at the crossing? completed 5/6 => completion 10 >= 6
            // fires FIRST (enum order), adversity 2 < 6 anyway => Diligence.
            string d = Id(23, true);
            SeedNeutral(d);
            AccrueAndNotify(d, CrewAgentRegistry.OutcomeFailed);
            for (int i = 1; i <= 6; i++) { Advance(1100); AccrueAndNotify(d, CrewAgentRegistry.OutcomeCompleted); }
            Check(AdaptiveLearningDirector.GetRecord(d).LastAdjustRule == "completion",
                "LEARN02c completion precedence over adversity");
            Check(CrewPersonalityRegistry.Get(d).Get(PersonalityTrait.Diligence) == 51, "LEARN02c Diligence adjusted by precedence");
            Check(CrewPersonalityRegistry.Get(d).Get(PersonalityTrait.Adaptability) == 50, "LEARN02c Adaptability untouched by precedence");

            // ---- LEARN03: deny-by-default authority ---------------------------------
            FreshSetup();
            AdaptiveLearningDirector.ResetForTests(); // clears the authority probe set by FreshSetup
            string e = Id(31, true);
            CrewExperienceRegistry.RecordOutcome(e, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            Check(!AdaptiveLearningDirector.NotifyOutcome(e, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "LEARN03 null probe denies");
            Check(AdaptiveLearningDirector.GetRecord(e) == null, "LEARN03 no record without authority");
            AdaptiveLearningDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
            Check(!AdaptiveLearningDirector.NotifyOutcome(e, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "LEARN03 faulting probe denies");
            AdaptiveLearningDirector.SetAuthorityProbe(delegate { return false; });
            Check(!AdaptiveLearningDirector.NotifyOutcome(e, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "LEARN03 non-authoritative probe denies");
            Check(AdaptiveLearningDirector.RecordCount == 0, "LEARN03 baseline never armed without authority");
            AdaptiveLearningDirector.SetAuthorityProbe(delegate { return true; });
            Check(AdaptiveLearningDirector.NotifyOutcome(e, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "LEARN03 authority restored => pass accepted");
            Check(AdaptiveLearningDirector.BaselineArmedCount == 1, "LEARN03 baseline armed on first authorized pass");

            // ---- LEARN04: invalid inputs refused -------------------------------------
            FreshSetup();
            Check(!AdaptiveLearningDirector.NotifyOutcome(null, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs), "LEARN04 null id refused");
            Check(!AdaptiveLearningDirector.NotifyOutcome("", CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs), "LEARN04 empty id refused");
            Check(!AdaptiveLearningDirector.NotifyOutcome("garbage", CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs), "LEARN04 non-AGT id refused");
            Check(!AdaptiveLearningDirector.NotifyOutcome("AGT:", CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs), "LEARN04 bare prefix refused");
            Check(!AdaptiveLearningDirector.NotifyOutcome(new string('A', 40), CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs), "LEARN04 overlong id refused");
            Check(AdaptiveLearningDirector.RefusedCount == 5, "LEARN04 id refusals counted");
            string f = Id(41, true);
            Check(!AdaptiveLearningDirector.NotifyOutcome(f, "MADE_UP", s_Clock.NowMs), "LEARN04 unknown outcome refused");
            Check(!AdaptiveLearningDirector.NotifyOutcome(f, null, s_Clock.NowMs), "LEARN04 null outcome refused");
            Check(!AdaptiveLearningDirector.NotifyOutcome(f, "", s_Clock.NowMs), "LEARN04 empty outcome refused");
            Check(AdaptiveLearningDirector.RefusedCount == 8, "LEARN04 outcome refusals counted");
            Check(!AdaptiveLearningDirector.NotifyOutcome(f, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs), "LEARN04 no experience record => refused");
            Check(AdaptiveLearningDirector.RecordCount == 0, "LEARN04 nothing tracked without experience");
            Check(AdaptiveLearningDirector.GetRecord(null) == null, "LEARN04 GetRecord null-safe");
            Check(AdaptiveLearningDirector.GetRecord("") == null, "LEARN04 GetRecord empty-safe");
            Check(AdaptiveLearningDirector.GetRecord("AGT:ffffffff") == null, "LEARN04 GetRecord absent-safe");
            // Identity integrity: a matured record carries the registry key id.
            string g = Id(42, true);
            SeedNeutral(g);
            CrewExperienceRegistry.RecordOutcome(g, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            AdaptiveLearningDirector.NotifyOutcome(g, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            Advance(1100);
            AccrueAndNotify(g, CrewAgentRegistry.OutcomeCompleted);
            Advance(1100);
            AccrueAndNotify(g, CrewAgentRegistry.OutcomeCompleted);
            Advance(1100);
            AccrueAndNotify(g, CrewAgentRegistry.OutcomeCompleted);
            Advance(1100);
            AccrueAndNotify(g, CrewAgentRegistry.OutcomeCompleted);
            CrewPersonality pg = CrewPersonalityRegistry.Get(g);
            Check(pg != null && pg.AgentId == g, "LEARN04 matured record identity integrity");

            // ---- LEARN05: rate limit + crossing persistence ---------------------------
            FreshSetup();
            string h = Id(51, true);
            SeedNeutral(h);
            AccrueAndNotify(h, CrewAgentRegistry.OutcomeCompleted);
            Check(AdaptiveLearningDirector.GetRecord(h).ArmedLevel == 1, "LEARN05 armed at level 1");
            // Accrue to the level-2 threshold, then notify 4 times INSIDE the
            // 1 s evaluation gate: the crossing persists (ArmedLevel lags) and
            // is decided on a later pass.
            for (int i = 0; i < 4; i++) CrewExperienceRegistry.RecordOutcome(h, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 10 + i);
            AdaptiveLearningDirector.NotifyOutcome(h, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 10);
            AdaptiveLearningDirector.NotifyOutcome(h, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 20);
            AdaptiveLearningDirector.NotifyOutcome(h, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 30);
            AdaptiveLearningDirector.NotifyOutcome(h, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 40);
            Check(CrewExperienceRegistry.Get(h).Level == 2, "LEARN05 experience at level 2");
            AdaptiveLearningDirector.LearningRecord r5 = AdaptiveLearningDirector.GetRecord(h);
            Check(r5.ArmedLevel == 1, "LEARN05 rate-limited passes left the crossing pending");
            Check(AdaptiveLearningDirector.CrossingCount == 0, "LEARN05 crossing not consumed inside the gate");
            Check(r5.UpdateCount == 5, "LEARN05 bookkeeping updates counted on rate-limited passes");
            Check(r5.LastSeenMs == s_Clock.NowMs + 40, "LEARN05 last-seen refreshed (hygiene accurate)");
            // Past the gate: the pending crossing fires.
            AdaptiveLearningDirector.NotifyOutcome(h, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 1100);
            s_Clock.NowMs += 1100;
            Check(AdaptiveLearningDirector.CrossingCount == 1, "LEARN05 crossing decided after the gate");
            Check(AdaptiveLearningDirector.AdjustmentCount == 1, "LEARN05 adjustment after the gate");
            Check(AdaptiveLearningDirector.GetRecord(h).ArmedLevel == 2, "LEARN05 armed level advanced");

            // ---- LEARN06: one-shot per crossing + clamp-bound consumption -------------
            FreshSetup();
            string k = Id(61, true);
            SeedNeutral(k);
            AccrueAndNotify(k, CrewAgentRegistry.OutcomeCompleted);
            for (int i = 0; i < 4; i++) { Advance(1100); CrewExperienceRegistry.RecordOutcome(k, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs); }
            AdaptiveLearningDirector.NotifyOutcome(k, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            Check(AdaptiveLearningDirector.CrossingCount == 1, "LEARN06 crossing consumed");
            AdaptiveLearningDirector.NotifyOutcome(k, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 1100);
            s_Clock.NowMs += 1100;
            Check(AdaptiveLearningDirector.CrossingCount == 1, "LEARN06 same level never re-crosses");
            Check(AdaptiveLearningDirector.AdjustmentCount == 1, "LEARN06 one adjustment per crossing");
            // Clamp: Diligence pre-seeded at 100; the crossing is consumed but
            // performs NO write (never fabricated).
            string m = Id(62, true);
            CrewPersonalityRegistry.SetPersonality(m,
                PersonalityFactory.FromValues(m, 50, 50, 50, 100, 50, s_Clock.NowMs), s_Clock.NowMs);
            AccrueAndNotify(m, CrewAgentRegistry.OutcomeCompleted);
            for (int i = 0; i < 4; i++) { Advance(1100); CrewExperienceRegistry.RecordOutcome(m, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs); }
            long adjustmentsBefore = AdaptiveLearningDirector.AdjustmentCount;
            AdaptiveLearningDirector.NotifyOutcome(m, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            Check(AdaptiveLearningDirector.GetRecord(m).ArmedLevel == 2, "LEARN06 clamp path still consumed the crossing");
            Check(AdaptiveLearningDirector.AdjustmentCount == adjustmentsBefore, "LEARN06 no adjustment at the clamp bound");
            Check(AdaptiveLearningDirector.ClampedCount == 1, "LEARN06 clamp counted");
            Check(CrewPersonalityRegistry.Get(m).Get(PersonalityTrait.Diligence) == 100, "LEARN06 trait stayed at 100");
            Check(AdaptiveLearningDirector.LastAdjustment == null
                || AdaptiveLearningDirector.LastAdjustment.IndexOf(m, StringComparison.Ordinal) < 0,
                "LEARN06 no adjustment readback for the clamp path");

            // ---- LEARN07: multi-agent isolation + bounded set + hygiene ----------------
            FreshSetup();
            string x1 = Id(71, true);
            string x2 = Id(72, true);
            SeedNeutral(x1);
            SeedNeutral(x2);
            AccrueAndNotify(x1, CrewAgentRegistry.OutcomeCompleted);
            AccrueAndNotify(x2, CrewAgentRegistry.OutcomeFailed);
            Check(AdaptiveLearningDirector.RecordCount == 2, "LEARN07 two records tracked");
            Check(AdaptiveLearningDirector.GetRecord(x1).ArmedLevel == 1 && AdaptiveLearningDirector.GetRecord(x2).ArmedLevel == 1,
                "LEARN07 baselines independent");
            // Bounded set: fill to 32 records (2 exist; add 30), 33rd refused.
            // (Experience's own 32-record cap makes the learning cap defensive
            // only — the refusal path is identical either way.)
            for (int pid = 73; pid <= 102; pid++)
            {
                string idN = Id(pid, true);
                CrewExperienceRegistry.RecordOutcome(idN, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
                AdaptiveLearningDirector.NotifyOutcome(idN, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            }
            Check(AdaptiveLearningDirector.RecordCount == 32, "LEARN07 cap reached exactly");
            string overflow = Id(103, true);
            CrewExperienceRegistry.RecordOutcome(overflow, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            Check(!AdaptiveLearningDirector.NotifyOutcome(overflow, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs),
                "LEARN07 record creation refused at cap");
            Check(AdaptiveLearningDirector.RecordCount == 32, "LEARN07 size unchanged after refusal");
            // Hygiene on a small population: y1/y2/y3 armed, all stale after
            // ActiveExpiryMs; y1's next outcome refreshes its record IN THE
            // SAME PASS (sweep runs first, so y1 is re-created fresh) while
            // y2/y3 expire.
            FreshSetup();
            string y1 = Id(74, true);
            string y2 = Id(75, true);
            string y3 = Id(76, true);
            AccrueAndNotify(y1, CrewAgentRegistry.OutcomeCompleted);
            AccrueAndNotify(y2, CrewAgentRegistry.OutcomeCompleted);
            AccrueAndNotify(y3, CrewAgentRegistry.OutcomeCompleted);
            Check(AdaptiveLearningDirector.RecordCount == 3, "LEARN07 hygiene setup: three records");
            int expireAt = s_Clock.NowMs + AdaptiveLearningDirector.ActiveExpiryMs + 1100;
            CrewExperienceRegistry.RecordOutcome(y1, CrewAgentRegistry.OutcomeCompleted, expireAt);
            s_Clock.NowMs = expireAt;
            AdaptiveLearningDirector.NotifyOutcome(y1, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            Check(AdaptiveLearningDirector.ExpiredCount == 3, "LEARN07 hygiene decayed all three stale records (sweep runs before the refresh)");
            Check(HasLineContaining("LearningRecordExpired LEARN:" + y2), "LEARN07 expiry line (y2) emitted");
            Check(HasLineContaining("LearningRecordExpired LEARN:" + y3), "LEARN07 expiry line (y3) emitted");
            Check(AdaptiveLearningDirector.HistoryCount == 3, "LEARN07 history holds all expired ids");
            Check(AdaptiveLearningDirector.GetRecord(y2) == null, "LEARN07 expired record absent");
            // y1 was swept too (stale at sweep time) and re-created fresh:
            // armed at its CURRENT level (2 completions = 4 xp => level 1),
            // no bogus adjustment.
            AdaptiveLearningDirector.LearningRecord ry1 = AdaptiveLearningDirector.GetRecord(y1);
            Check(ry1 != null && ry1.ArmedLevel == CrewExperienceRegistry.Get(y1).Level,
                "LEARN07 swept-then-refreshed record re-arms at the current experience level");
            // Re-fire after decay for a removed agent: fresh record, baseline only.
            Advance(1100);
            AccrueAndNotify(y2, CrewAgentRegistry.OutcomeCompleted);
            AdaptiveLearningDirector.LearningRecord r75 = AdaptiveLearningDirector.GetRecord(y2);
            Check(r75 != null && r75.ArmedLevel == CrewExperienceRegistry.Get(y2).Level,
                "LEARN07 fresh record re-arms at the current level");
            Check(AdaptiveLearningDirector.AdjustmentCount == 0, "LEARN07 no adjustment without a crossing after re-arm");

            // ---- LEARN08: funnel fail-safety -------------------------------------------
            FreshSetup();
            string fs = Id(81, true);
            s_Snap = Snap(s_Clock.NowMs, Member(81, true, 1, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            Check(CrewAgentRegistry.GetAgent(fs) != null, "LEARN08 agent created");
            ResolveViaFunnel(fs, CompletedTask("BOT:81", "learning funnel"));
            Check(CrewExperienceRegistry.Get(fs) != null, "LEARN08 funnel accrued experience");
            Check(AdaptiveLearningDirector.GetRecord(fs) != null, "LEARN08 funnel armed the learning baseline");
            // (a) Direct caller: a faulting decision listener propagates.
            AdaptiveLearningDirector.SetDecisionListener(delegate (string line)
            {
                if (line.StartsWith("PersonalityAdjusted", StringComparison.Ordinal)) throw new InvalidOperationException("fault injection");
            });
            string fs2 = Id(82, true);
            SeedNeutral(fs2);
            AccrueAndNotify(fs2, CrewAgentRegistry.OutcomeCompleted);
            for (int i = 0; i < 4; i++) { Advance(1100); CrewExperienceRegistry.RecordOutcome(fs2, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs); }
            bool threw = false;
            try { AdaptiveLearningDirector.NotifyOutcome(fs2, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, "LEARN08 listener fault propagates to the direct caller");
            // (b) The funnel absorbs the same fault: a crossing resolved via
            // Sync with the faulting listener attached must still resolve the
            // agent's task state.
            string fs3 = Id(83, true);
            SeedNeutral(fs3);
            AccrueAndNotify(fs3, CrewAgentRegistry.OutcomeCompleted);
            for (int i = 0; i < 4; i++) { Advance(1100); CrewExperienceRegistry.RecordOutcome(fs3, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs); }
            s_Snap = Snap(s_Clock.NowMs, Member(83, true, 1, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            Check(CrewAgentRegistry.GetAgent(fs3) != null, "LEARN08 agent 83 created");
            ResolveViaFunnel(fs3, CompletedTask("BOT:83", "learning funnel 3"));
            CrewAgent agentAfter = CrewAgentRegistry.GetAgent(fs3);
            Check(agentAfter != null && agentAfter.CurrentTaskId == 0, "LEARN08 funnel resolved despite learning-layer fault");
            Check(agentAfter.LastTaskOutcome == CrewAgentRegistry.OutcomeCompleted, "LEARN08 outcome still recorded on the agent");
            // (c) Experience-layer emit fault: Apply precedes Emit, so the
            // record exists; the learning pass reads the snapshot normally.
            CrewExperienceRegistry.ResetForTests();
            CrewExperienceRegistry.SetDecisionListener(delegate (string line)
            {
                if (line.StartsWith("ExperienceRecorded", StringComparison.Ordinal)) throw new InvalidOperationException("fault injection");
            });
            AdaptiveLearningDirector.ResetForTests();
            AdaptiveLearningDirector.SetAuthorityProbe(delegate { return true; });
            AdaptiveLearningDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            string fs4 = Id(84, true);
            s_Snap = Snap(s_Clock.NowMs, Member(84, true, 1, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            ResolveViaFunnel(fs4, CompletedTask("BOT:84", "learning funnel 4"));
            CrewAgent agent84 = CrewAgentRegistry.GetAgent(fs4);
            Check(agent84 != null && agent84.CurrentTaskId == 0, "LEARN08 funnel resolved despite experience fault");
            Check(CrewExperienceRegistry.Get(fs4) != null, "LEARN08 experience record applied (Apply precedes Emit)");
            Check(AdaptiveLearningDirector.GetRecord(fs4) != null, "LEARN08 learning armed despite the experience emit fault");

            // ---- LEARN09: determinism + data-only proof + post-reset inert --------------
            // Determinism: identical scenario re-run => identical diagnostics.
            List<string> firstLines;
            List<string> firstStatus;
            FreshSetup();
            {
                string d1 = Id(91, true);
                SeedNeutral(d1);
                AccrueAndNotify(d1, CrewAgentRegistry.OutcomeCompleted);
                for (int i = 1; i <= 4; i++) { Advance(1100); AccrueAndNotify(d1, CrewAgentRegistry.OutcomeCompleted); }
            }
            firstLines = AdaptiveLearningDirector.Lines();
            firstStatus = AdaptiveLearningDirector.StatusLines();
            FreshSetup();
            {
                string d2 = Id(91, true);
                SeedNeutral(d2);
                AccrueAndNotify(d2, CrewAgentRegistry.OutcomeCompleted);
                for (int i = 1; i <= 4; i++) { Advance(1100); AccrueAndNotify(d2, CrewAgentRegistry.OutcomeCompleted); }
            }
            List<string> secondLines = AdaptiveLearningDirector.Lines();
            List<string> secondStatus = AdaptiveLearningDirector.StatusLines();
            Check(firstLines.Count == secondLines.Count, "LEARN09 determinism: same line count");
            bool identicalLines = true;
            for (int i = 0; i < firstLines.Count && i < secondLines.Count; i++)
            {
                if (firstLines[i] != secondLines[i]) identicalLines = false;
            }
            Check(identicalLines, "LEARN09 determinism: identical diagnostics");
            bool identicalStatus = firstStatus.Count == secondStatus.Count;
            for (int i = 0; identicalStatus && i < firstStatus.Count && i < secondStatus.Count; i++)
            {
                if (firstStatus[i] != secondStatus[i]) identicalStatus = false;
            }
            Check(identicalStatus, "LEARN09 determinism: identical status lines");
            // Data-only proof: the task pipeline is untouched by learning churn.
            CapBotTask tP = CapBotTask.Create("TEST", "BOT:92", "learning isolation", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tP);
            tP.TryQueue();
            TaskScheduler.Tick(s_Clock.NowMs);
            for (int i = 0; i < 25; i++)
            {
                CrewExperienceRegistry.RecordOutcome(Id(93, true), CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + i);
                AdaptiveLearningDirector.NotifyOutcome(Id(93, true), CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + i);
            }
            Check(TaskRegistry.LiveCount == 1, "LEARN09 registry size untouched");
            Check(tP.State == TaskState.Queued, "LEARN09 task state untouched");
            Check(ExecutionClaims.LiveClaimCount == 0, "LEARN09 claims untouched");
            Check(TaskRecoveryManager.TrackedCount == 0, "LEARN09 recovery untouched");
            // Experience record NOT mutated by a learning-only pass.
            CrewExperienceRecord expBefore = CrewExperienceRegistry.SnapshotOf(Id(93, true));
            AdaptiveLearningDirector.NotifyOutcome(Id(93, true), CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 1100);
            CrewExperienceRecord expAfter = CrewExperienceRegistry.SnapshotOf(Id(93, true));
            Check(expBefore.ExperiencePoints == expAfter.ExperiencePoints
                && expBefore.TotalOutcomes == expAfter.TotalOutcomes
                && expBefore.UpdateCount == expAfter.UpdateCount,
                "LEARN09 NotifyOutcome never accrues/mutates experience");
            // Post-reset inert.
            AdaptiveLearningDirector.ResetForTests();
            Check(!AdaptiveLearningDirector.NotifyOutcome(Id(93, true), CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs + 1200),
                "LEARN09 post-reset inert (authority seam cleared)");
            Check(AdaptiveLearningDirector.RecordCount == 0, "LEARN09 post-reset no records");

            // ---- LEARN10: real Sync funnel end-to-end + sequential crossings + P11 counters --
            FreshSetup();
            string q = Id(101, true);
            s_Snap = Snap(s_Clock.NowMs, Member(101, true, 4, false, true, 0.9f));
            Advance(1000);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            Check(CrewAgentRegistry.GetAgent(q) != null, "LEARN10 agent created via sync");
            ResolveViaFunnel(q, CompletedTask("BOT:101", "funnel one"));
            Check(HasLineContaining("AgentTaskResolved " + q), "LEARN10 funnel resolution emitted");
            Check(HasLineContaining("ExperienceRecorded " + q), "LEARN10 accrual emitted");
            Check(AdaptiveLearningDirector.GetRecord(q) != null && AdaptiveLearningDirector.GetRecord(q).ArmedLevel == 1,
                "LEARN10 learning baseline armed by the funnel");
            // Seed an explicit personality AFTER the arm so the P11 counter
            // shape is: 1 assignment (seed) + 1 replacement per adjustment.
            SeedNeutral(q);
            long assignedBefore = CrewPersonalityRegistry.AssignedCount;
            long replacedBefore = CrewPersonalityRegistry.ReplacedCount;
            // 7 more funnel completions: 8 total = 80 xp => level 2.
            for (int round = 0; round < 7; round++)
            {
                ResolveViaFunnel(q, CompletedTask("BOT:101", "funnel round " + round));
            }
            Check(CrewExperienceRegistry.Get(q).Level == 2, "LEARN10 crossed into level 2 (8 completions = 80 xp)");
            AdaptiveLearningDirector.LearningRecord r10 = AdaptiveLearningDirector.GetRecord(q);
            Check(r10 != null && r10.CrossingCount == 1, "LEARN10 one crossing consumed");
            Check(r10.AdjustCount == 1, "LEARN10 one adjustment");
            Check(CrewPersonalityRegistry.Get(q).Get(PersonalityTrait.Diligence) == 51, "LEARN10 trait matured through the funnel");
            Check(CrewPersonalityRegistry.ReplacedCount == replacedBefore + 1, "LEARN10 P11 replacement counted");
            Check(CrewPersonalityRegistry.AssignedCount == assignedBefore, "LEARN10 no new assignment (seed existed)");
            // 5 more: 13 completions = 130 xp => level 3 (threshold 120).
            for (int round = 0; round < 5; round++)
            {
                ResolveViaFunnel(q, CompletedTask("BOT:101", "funnel late " + round));
            }
            Check(CrewExperienceRegistry.Get(q).Level == 3, "LEARN10 crossed into level 3 (13 completions = 130 xp)");
            Check(AdaptiveLearningDirector.CrossingCount == 2, "LEARN10 second crossing consumed");
            Check(AdaptiveLearningDirector.AdjustmentCount == 2, "LEARN10 second adjustment");
            Check(CrewPersonalityRegistry.Get(q).Get(PersonalityTrait.Diligence) == 52, "LEARN10 Diligence 50 -> 52 over two crossings");
            Check(HasLineContaining("PersonalityAdjusted LEARN:" + q + " trait=Diligence delta=+1 level=3 rule=completion"),
                "LEARN10 second adjustment line");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}