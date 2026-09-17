// Dev-side unit tests for the Phase 26 trait profile consumer (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure consumer file (TraitProfileDirector) plus the Phase
// 10/11/25 domains it reads. Time is virtual: every timestamp is an explicit
// nowMs argument — no real clock reads, no sleeping.
//
// Covers the Phase 26 contract (director header + PROJECT-LOCATION stop state):
//   TRAIT01 baseline arm + end-to-end DOMINANT signal (readback shape)
//   TRAIT02 LOWAFFINITY signal (deterministic Engineer weights)
//   TRAIT03 deny-by-default authority (null/faulting/non-authoritative probe)
//   TRAIT04 snapshot fail-safes (missing / stale / future / not-started)
//   TRAIT05 evaluation rate limit + persistence through it
//   TRAIT06 one-shot per record + anti-churn re-report block + dup suppression
//   TRAIT07 NOPERS global one-shot (derive-on-demand interplay)
//   TRAIT08 MATURED signal after real P25 learning (consumer never writes)
//   TRAIT09 multi-agent isolation + GetRecord null-safety
//   TRAIT10 hygiene decay after real registry removal + fresh re-arm
//   TRAIT11 determinism + data-only proof + post-reset inert
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;
using CapBot.Core.Learning;

namespace CapBot.TaskTests
{
    internal static class TraitConsumerTests
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
            TraitProfileDirector.ResetForTests();
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
            TraitProfileDirector.SetAuthorityProbe(delegate { return true; });
            TraitProfileDirector.SetWorldProvider(delegate { return s_Snap; });
            TraitProfileDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
        }

        private static void Advance(int ms) { s_Clock.NowMs += ms; }

        private static void SyncNow() { CrewAgentRegistry.Sync(s_Clock.NowMs); }

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

        // ---- snapshot builders ------------------------------------------------

        private static CrewMemberSnapshot Member(int id, bool isBot, int classId, bool isCaptain, bool alive, float health)
        {
            return new CrewMemberSnapshot(id, (isBot ? "bot" : "human") + id, isBot, classId, 0,
                true, alive, health, "Bridge", isCaptain, isBot ? 3 : -1);
        }

        private static WorldSnapshot Snap(int timeMs, bool gameStarted, params CrewMemberSnapshot[] crew)
        {
            List<CrewMemberSnapshot> crewList = new List<CrewMemberSnapshot>(crew);
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 0.5f, false, 0, -1, 0, 10f));
            return new WorldSnapshot(
                timeMs, gameStarted, true, 7, WorldAuthority.MasterDerived,
                ships, crewList, new List<MissionSnapshot>(),
                new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN),
                new NavigationSnapshot(5, "Sector Five", -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false),
                new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        private static WorldSnapshot Snap(int timeMs, params CrewMemberSnapshot[] crew)
        {
            return Snap(timeMs, true, crew);
        }

        private static string Id(int playerId, bool isBot)
        {
            return CrewAgentRegistry.MakeAgentId(playerId, isBot);
        }

        // Creates the live agent for a bot crew member (Sync at the current
        // clock time against the published snapshot).
        private static void CreateAgent(int playerId, int classId)
        {
            s_Snap = Snap(s_Clock.NowMs, Member(playerId, true, classId, false, true, 1f));
            CrewAgentRegistry.Sync(s_Clock.NowMs);
        }

        // Publishes a fresh snapshot at the current clock time (keeps the
        // 20 s freshness gate satisfied across Advance() calls).
        private static void PublishNow(params CrewMemberSnapshot[] crew)
        {
            s_Snap = Snap(s_Clock.NowMs, crew);
        }

        // Direct P25 accrual + learning pass (the natural pair).
        private static void AccrueAndNotify(string agentId, string outcome)
        {
            CrewExperienceRegistry.RecordOutcome(agentId, outcome, s_Clock.NowMs);
            AdaptiveLearningDirector.NotifyOutcome(agentId, outcome, s_Clock.NowMs);
        }

        // Seeds an explicit personality from raw values at the current time.
        private static void Seed(string agentId, int discipline, int boldness, int sociability, int diligence, int adaptability)
        {
            CrewPersonalityRegistry.SetPersonality(agentId,
                PersonalityFactory.FromValues(agentId, discipline, boldness, sociability, diligence, adaptability, s_Clock.NowMs),
                s_Clock.NowMs);
        }

        internal static int Run()
        {
            // ---- TRAIT01: baseline arm + end-to-end DOMINANT ---------------------
            // SENTINEL (Discipline 80) bot on Weapons: arch 1, affinity
            // (80*30 + 50*40 + 50*5 + 50*20 + 50*5)/100 = 59. Arm pass is
            // observation-free; the second readable pass reports the profile.
            {
                FreshSetup();
                string a = Id(3, true);
                Seed(a, 80, 50, 50, 50, 50);
                CreateAgent(3, 3); // classId 3 => Weapons
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT01 arm pass silent");
                Check(TraitProfileDirector.RecordCount == 0, "TRAIT01 arm pass tracks nothing");
                Check(TraitProfileDirector.EvaluationCount == 1, "TRAIT01 arm pass evaluated");
                Advance(1100);
                s_Snap = Snap(s_Clock.NowMs, Member(3, true, 3, false, true, 1f));
                SyncNow();
                Advance(4200);
                s_Snap = Snap(s_Clock.NowMs, Member(3, true, 3, false, true, 1f));
                int reports = TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(reports == 1, "TRAIT01 second readable pass reports once");
                TraitProfileDirector.TraitProfileRecord rec = TraitProfileDirector.GetRecord(a);
                Check(rec != null, "TRAIT01 record tracked");
                Check(rec != null && rec.ArchetypeId == 1, "TRAIT01 SENTINEL maps to arch 1");
                Check(rec != null && rec.AffinityScore == 59, "TRAIT01 Weapons affinity computed (59)");
                Check(rec != null && !rec.Matured && rec.ReportCount == 1, "TRAIT01 one-shot report counted");
                Check(rec != null && rec.LastReportMs == s_Clock.NowMs, "TRAIT01 report timestamp stamped");
                Check(HasLineContaining("TraitSignal " + TraitProfileDirector.TrackIdPrefix + a + " dominant arch=1"),
                    "TRAIT01 dominant signal line emitted");
                Check(rec != null && rec.ToLine().EndsWith("arch=1 aff=59 matured=0 reports=1", StringComparison.Ordinal),
                    "TRAIT01 record line shape");
                Check(TraitProfileDirector.Lines().Count == 1, "TRAIT01 one diagnostic line");
            }

            // ---- TRAIT02: LOWAFFINITY --------------------------------------------
            // VANGUARD seed {50,90,50,5,5} on Engineer (weights 30/5/5/45/15):
            // affinity (1500+450+250+225+75)/100 = 25 < 40 => lowAffinity wins
            // over the also-dominant archetype (detail priority).
            {
                FreshSetup();
                string a = Id(4, true);
                Seed(a, 50, 90, 50, 5, 5);
                CreateAgent(4, 4); // classId 4 => Engineer
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Advance(1100);
                s_Snap = Snap(s_Clock.NowMs, Member(4, true, 4, false, true, 1f));
                SyncNow();
                Advance(4200);
                s_Snap = Snap(s_Clock.NowMs, Member(4, true, 4, false, true, 1f));
                int reports = TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(reports == 1, "TRAIT02 low-affinity report fired");
                TraitProfileDirector.TraitProfileRecord rec = TraitProfileDirector.GetRecord(a);
                Check(rec != null && rec.AffinityScore == 25, "TRAIT02 Engineer affinity 25");
                Check(rec != null && rec.ArchetypeId == 2, "TRAIT02 VANGUARD arch 2 (recorded, not prioritized)");
                Check(HasLineContaining("lowAffinity aff=25"), "TRAIT02 lowAffinity detail wins over dominant");
                Check(HasLineContaining("(read-only trait consumer; traits are data)"), "TRAIT02 signal carries the consumer tag");
            }

            // ---- TRAIT03: deny-by-default authority -------------------------------
            {
                FreshSetup();
                TraitProfileDirector.ResetForTests(); // clears the seams FreshSetup set
                string a = Id(31, true);
                Seed(a, 80, 50, 50, 50, 50);
                CreateAgent(31, 3);
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT03 null probe denies");
                Check(TraitProfileDirector.EvaluationCount == 0 && TraitProfileDirector.RecordCount == 0, "TRAIT03 nothing evaluated without authority");
                TraitProfileDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
                TraitProfileDirector.SetWorldProvider(delegate { return s_Snap; });
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT03 faulting probe denies");
                TraitProfileDirector.SetAuthorityProbe(delegate { return false; });
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT03 non-authoritative probe denies");
                Check(TraitProfileDirector.RecordCount == 0, "TRAIT03 no records without authority");
                TraitProfileDirector.SetAuthorityProbe(delegate { return true; });
                TraitProfileDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT03 authority restored => arm accepted");
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, Member(31, true, 3, false, true, 1f));
                int reports = TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(reports == 1 && TraitProfileDirector.RecordCount == 1, "TRAIT03 profiling proceeds once authorized");
            }

            // ---- TRAIT04: snapshot fail-safes --------------------------------------
            {
                FreshSetup();
                TraitProfileDirector.ResetForTests();
                TraitProfileDirector.SetAuthorityProbe(delegate { return true; });
                TraitProfileDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
                string a = Id(41, true);
                CreateAgent(41, 0);
                // (a) no snapshot at all
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT04 no snapshot => 0");
                Check(TraitProfileDirector.LastUncertainReason != null
                    && TraitProfileDirector.LastUncertainReason.IndexOf("no world snapshot", StringComparison.Ordinal) >= 0,
                    "TRAIT04 missing-snapshot reason recorded");
                TraitProfileDirector.SetWorldProvider(delegate { return s_Snap; });
                // (b) stale snapshot (25 s old)
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs - 25000, Member(41, true, 3, false, true, 1f));
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT04 stale snapshot => 0");
                Check(TraitProfileDirector.StaleRejectionCount == 1, "TRAIT04 stale rejection counted");
                // (c) snapshot from the future (negative age)
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs + 5000, Member(41, true, 3, false, true, 1f));
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT04 future snapshot => 0");
                Check(TraitProfileDirector.StaleRejectionCount == 2, "TRAIT04 future rejection counted as stale");
                // (d) game not started
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, false, Member(41, true, 3, false, true, 1f));
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT04 not-started snapshot => 0");
                Check(TraitProfileDirector.LastUncertainReason.IndexOf("game not started", StringComparison.Ordinal) >= 0,
                    "TRAIT04 not-started reason recorded");
                Check(TraitProfileDirector.RecordCount == 0 && TraitProfileDirector.EvaluationCount == 0,
                    "TRAIT04 no pass ever read through the fail-safes");
                // (e) a readable snapshot still arms normally afterwards
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, true, Member(41, true, 3, false, true, 1f));
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0 && TraitProfileDirector.EvaluationCount == 1,
                    "TRAIT04 readable snapshot arms after the fail-safes");
            }

            // ---- TRAIT05: rate limit + persistence ---------------------------------
            {
                FreshSetup();
                string a = Id(51, true);
                Seed(a, 80, 50, 50, 50, 50);
                CreateAgent(51, 3);
                TraitProfileDirector.Evaluate(s_Clock.NowMs); // arm (LastEvalMs stamped)
                long before = TraitProfileDirector.EvaluationCount;
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs + 100) == 0, "TRAIT05 in-gate pass denied");
                Check(TraitProfileDirector.EvaluationCount == before, "TRAIT05 in-gate pass not evaluated");
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, Member(51, true, 3, false, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(TraitProfileDirector.EvaluationCount == before + 1, "TRAIT05 pass proceeds past the gate");
                Check(TraitProfileDirector.RecordCount == 1, "TRAIT05 profile persisted through the gate");
                Check(TraitProfileDirector.GetRecord(a).LastSeenMs == s_Clock.NowMs, "TRAIT05 last-seen refreshed (hygiene accurate)");
            }

            // ---- TRAIT06: one-shot + anti-churn block + dup suppression ------------
            {
                FreshSetup();
                string a = Id(61, true);
                Seed(a, 80, 50, 50, 50, 50);
                CreateAgent(61, 3);
                TraitProfileDirector.Evaluate(s_Clock.NowMs); // arm
                Advance(1100);
                s_Snap = Snap(s_Clock.NowMs, Member(61, true, 3, false, true, 1f));
                SyncNow();
                Advance(4200);
                s_Snap = Snap(s_Clock.NowMs, Member(61, true, 3, false, true, 1f));
                int t2 = s_Clock.NowMs;
                TraitProfileDirector.Evaluate(t2); // first report
                Check(TraitProfileDirector.ReportCount == 1, "TRAIT06 first report");
                // Inside the 20 s report block: silent, counted as a recheck block.
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, Member(61, true, 3, false, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(TraitProfileDirector.ReportCount == 1, "TRAIT06 no re-report inside the block");
                Check(TraitProfileDirector.RecheckBlockCount == 1, "TRAIT06 recheck block counted");
                Check(TraitProfileDirector.DuplicatesSuppressedCount == 0, "TRAIT06 no dup suppression inside the block");
                // Past the block: the persistent profile re-reports (block expires).
                Advance(21000);
                s_Snap = Snap(s_Clock.NowMs, Member(61, true, 3, false, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                TraitProfileDirector.TraitProfileRecord rec = TraitProfileDirector.GetRecord(a);
                Check(TraitProfileDirector.ReportCount == 2, "TRAIT06 re-report after the block expired");
                Check(rec != null && rec.ReportCount == 2, "TRAIT06 record report count advanced");
                Check(TraitProfileDirector.DuplicatesSuppressedCount == 1, "TRAIT06 dup-suppression counted on the refresh pass");
                Check(HasLineContaining("dominant arch=1"), "TRAIT06 re-report line present");
            }

            // ---- TRAIT07: NOPERS global one-shot ------------------------------------
            // The arm pass itself performs the per-agent readbacks (which
            // derive-on-demand and register personalities), so NOPERS is the
            // honest condition only when NO agent was readable at the arm
            // pass — the mid-session-joiner scenario: arm on an empty crew,
            // agents appear before the second pass, registry count is read
            // BEFORE the derivations populate it.
            {
                FreshSetup();
                s_Snap = Snap(s_Clock.NowMs); // empty crew
                SyncNow();
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT07 arm pass on empty crew silent");
                Check(CrewPersonalityRegistry.Count == 0, "TRAIT07 registry still empty after the empty-crew arm");
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, Member(71, true, 1, false, true, 1f), Member(72, true, 2, false, true, 1f));
                SyncNow();
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, Member(71, true, 1, false, true, 1f), Member(72, true, 2, false, true, 1f));
                int reports = TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(reports >= 1, "TRAIT07 second readable pass emitted");
                Check(HasLineContaining("TraitSignal " + TraitProfileDirector.TrackIdPrefix + TraitProfileDirector.TrackNoPersonality
                    + " activeCrew=2 personalities=0"), "TRAIT07 NOPERS line emitted");
                Check(CrewPersonalityRegistry.Count == 2, "TRAIT07 readbacks derived both personalities (registry populated after the count)");
                // One-shot: a later pass never repeats it.
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, Member(71, true, 1, false, true, 1f), Member(72, true, 2, false, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(CountLines("TraitSignal " + TraitProfileDirector.TrackIdPrefix + TraitProfileDirector.TrackNoPersonality) == 1,
                    "TRAIT07 NOPERS is session-global one-shot");
                Check(TraitProfileDirector.RecordCount == 2, "TRAIT07 both agents tracked");
            }

            // ---- TRAIT08: MATURED after real P25 learning ---------------------------
            {
                FreshSetup();
                string a = Id(8, true);
                Seed(a, 50, 50, 50, 50, 50); // neutral => BALANCED, all affinities 50
                CreateAgent(8, 0); // Captain
                TraitProfileDirector.Evaluate(s_Clock.NowMs); // arm
                Advance(1100);
                s_Snap = Snap(s_Clock.NowMs, Member(8, true, 0, true, true, 1f));
                SyncNow();
                Advance(4200);
                s_Snap = Snap(s_Clock.NowMs, Member(8, true, 0, true, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                TraitProfileDirector.TraitProfileRecord rec = TraitProfileDirector.GetRecord(a);
                Check(rec != null && rec.ReportCount == 0 && rec.ArchetypeId == 0, "TRAIT08 neutral profile tracked quiet");
                // Real P25 maturation: 5 spaced completions => level-2 crossing => +1 Diligence.
                AccrueAndNotify(a, CrewAgentRegistry.OutcomeCompleted);
                for (int i = 1; i <= 4; i++) { Advance(1100); AccrueAndNotify(a, CrewAgentRegistry.OutcomeCompleted); }
                Check(AdaptiveLearningDirector.GetRecord(a) != null && AdaptiveLearningDirector.GetRecord(a).AdjustCount == 1,
                    "TRAIT08 P25 adjustment performed");
                Check(CrewPersonalityRegistry.Get(a).Get(PersonalityTrait.Diligence) == 51, "TRAIT08 Diligence matured to 51");
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, Member(8, true, 0, true, true, 1f));
                int reports = TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(reports == 1, "TRAIT08 maturation re-report fired");
                Check(rec.Matured, "TRAIT08 record matured flag set");
                Check(HasLineContaining("matured (read-only trait consumer; traits are data)"), "TRAIT08 matured signal line");
                Check(CrewPersonalityRegistry.Get(a).Get(PersonalityTrait.Diligence) == 51,
                    "TRAIT08 consumer never re-wrote traits (still 51)");
            }

            // ---- TRAIT09: multi-agent isolation + null-safety -----------------------
            {
                FreshSetup();
                string a1 = Id(91, true);
                string a2 = Id(92, true);
                string a3 = Id(93, true);
                Seed(a1, 80, 50, 50, 50, 50); // SENTINEL (will signal)
                Seed(a2, 50, 50, 50, 50, 50); // BALANCED quiet
                Seed(a3, 50, 50, 50, 50, 50); // BALANCED quiet
                CreateAgent(91, 3);
                CreateAgent(92, 0);
                CreateAgent(93, 4);
                TraitProfileDirector.Evaluate(s_Clock.NowMs); // arm
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs,
                    Member(91, true, 3, false, true, 1f),
                    Member(92, true, 0, true, true, 1f),
                    Member(93, true, 4, false, true, 1f));
                SyncNow();
                Advance(4200);
                s_Snap = Snap(s_Clock.NowMs,
                    Member(91, true, 3, false, true, 1f),
                    Member(92, true, 0, true, true, 1f),
                    Member(93, true, 4, false, true, 1f));
                int reports = TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(reports == 1, "TRAIT09 exactly the dominant agent signaled");
                Check(TraitProfileDirector.RecordCount == 3, "TRAIT09 three records tracked independently");
                Check(TraitProfileDirector.GetRecord(a2) != null && TraitProfileDirector.GetRecord(a2).ReportCount == 0,
                    "TRAIT09 quiet agent tracked but silent");
                Check(TraitProfileDirector.GetRecord(a3) != null && TraitProfileDirector.GetRecord(a3).AffinityScore == 50,
                    "TRAIT09 Engineer affinity 50 (uniform neutral)");
                List<string> lines = TraitProfileDirector.Lines();
                Check(lines.Count == 3 && lines[0].CompareTo(lines[1]) < 0 && lines[1].CompareTo(lines[2]) < 0,
                    "TRAIT09 diagnostic lines sorted deterministically");
                Check(TraitProfileDirector.GetRecord(null) == null, "TRAIT09 GetRecord null-safe");
                Check(TraitProfileDirector.GetRecord("") == null, "TRAIT09 GetRecord empty-safe");
                Check(TraitProfileDirector.GetRecord("AGT:ffffffff") == null, "TRAIT09 GetRecord absent-safe");
            }

            // ---- TRAIT10: hygiene decay (real removal) + fresh re-arm ---------------
            {
                FreshSetup();
                string a1 = Id(81, true);
                string a2 = Id(82, true);
                string a3 = Id(83, true);
                // Other-class bots: BALANCED + affinity -1 => tracked quiet.
                CreateAgent(81, 6);
                CreateAgent(82, 6);
                CreateAgent(83, 6);
                TraitProfileDirector.Evaluate(s_Clock.NowMs); // arm
                Advance(1100);
                s_Snap = Snap(s_Clock.NowMs, Member(81, true, 6, false, true, 1f), Member(82, true, 6, false, true, 1f), Member(83, true, 6, false, true, 1f));
                SyncNow();
                Advance(4200);
                s_Snap = Snap(s_Clock.NowMs, Member(81, true, 6, false, true, 1f), Member(82, true, 6, false, true, 1f), Member(83, true, 6, false, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(TraitProfileDirector.RecordCount == 3 && TraitProfileDirector.RecordsTrackedCount == 3, "TRAIT10 three quiet profiles tracked");
                // Crew gone: deactivate now, removal after the 15 s grace.
                Advance(16000);
                s_Snap = Snap(s_Clock.NowMs);
                SyncNow();
                Check(CrewAgentRegistry.ActiveAgentCount == 0, "TRAIT10 agents deactivated after crew loss");
                Advance(16000);
                s_Snap = Snap(s_Clock.NowMs);
                SyncNow();
                Check(CrewAgentRegistry.AgentViews().Count == 0, "TRAIT10 agents removed after grace");
                // Profile sweep: 37+ s since last profiled pass => all three expire.
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs);
                int expired = TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(expired == 3, "TRAIT10 three expiry reports");
                Check(TraitProfileDirector.RecordCount == 0 && TraitProfileDirector.RecordsExpiredCount == 3,
                    "TRAIT10 all records decayed");
                Check(TraitProfileDirector.HistoryCount == 3, "TRAIT10 bounded history holds the expired ids");
                Check(CountLines("TraitProfileExpired") == 3, "TRAIT10 expiry lines emitted");
                // Re-arm: the same bots re-appear => FRESH records (budget re-armed).
                s_Snap = Snap(s_Clock.NowMs, Member(81, true, 6, false, true, 1f), Member(82, true, 6, false, true, 1f), Member(83, true, 6, false, true, 1f));
                SyncNow();
                Advance(5200);
                s_Snap = Snap(s_Clock.NowMs, Member(81, true, 6, false, true, 1f), Member(82, true, 6, false, true, 1f), Member(83, true, 6, false, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Check(TraitProfileDirector.RecordCount == 3, "TRAIT10 three fresh records re-armed");
                Check(TraitProfileDirector.RecordsTrackedCount == 6, "TRAIT10 re-arm created new records (tracked count advanced)");
            }

            // ---- TRAIT11: determinism + data-only + post-reset inert -----------------
            {
                // (a) identical setups produce identical readbacks
                List<string> run1;
                long reports1;
                FreshSetup();
                string a = Id(11, true);
                Seed(a, 80, 50, 50, 50, 50);
                CreateAgent(11, 3);
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Advance(1100);
                s_Snap = Snap(s_Clock.NowMs, Member(11, true, 3, false, true, 1f));
                SyncNow();
                Advance(4200);
                s_Snap = Snap(s_Clock.NowMs, Member(11, true, 3, false, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                run1 = TraitProfileDirector.Lines();
                reports1 = TraitProfileDirector.ReportCount;
                FreshSetup();
                string b = Id(11, true);
                Seed(b, 80, 50, 50, 50, 50);
                CreateAgent(11, 3);
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                Advance(1100);
                s_Snap = Snap(s_Clock.NowMs, Member(11, true, 3, false, true, 1f));
                SyncNow();
                Advance(4200);
                s_Snap = Snap(s_Clock.NowMs, Member(11, true, 3, false, true, 1f));
                TraitProfileDirector.Evaluate(s_Clock.NowMs);
                List<string> run2 = TraitProfileDirector.Lines();
                Check(run1.Count == run2.Count && reports1 == TraitProfileDirector.ReportCount,
                    "TRAIT11 identical inputs => identical counter state");
                bool same = run1.Count == run2.Count;
                for (int i = 0; same && i < run1.Count; i++) same = run1[i] == run2[i];
                Check(same, "TRAIT11 identical diagnostic lines");
                // (b) data-only proof: no task pipeline activity, no trait writes,
                // no personality writes beyond the explicit seed.
                Check(CrewPersonalityRegistry.Get(b).Get(PersonalityTrait.Discipline) == 80, "TRAIT11 seeded trait untouched");
                Check(!HasLineContaining("PersonalityAdjusted"), "TRAIT11 no learning write emitted");
                Check(!HasLineContaining("TaskCreated") && !HasLineContaining("TaskStarted"), "TRAIT11 task pipeline untouched");
                // (c) post-reset inert
                TraitProfileDirector.ResetForTests();
                Check(TraitProfileDirector.Evaluate(s_Clock.NowMs) == 0, "TRAIT11 evaluate inert after reset (probe cleared)");
                Check(TraitProfileDirector.RecordCount == 0, "TRAIT11 records cleared");
                // (d) status lines bounded + shaped
                List<string> status = TraitProfileDirector.StatusLines();
                Check(status.Count == 2 && status[0].IndexOf("traitProfiles=", StringComparison.Ordinal) >= 0,
                    "TRAIT11 status lines shape");
            }

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}