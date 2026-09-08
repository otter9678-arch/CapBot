// Dev-side unit tests for the Phase 28 crew data persistence (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the persistence + crew domains. Time is virtual: every timestamp
// is an explicit nowMs argument.
//
// Covers the Phase 28 mandated scenarios:
//   PERS01 capture/encode/decode/restore round-trip (all three layers)
//   PERS02 determinism (same logical payload => byte-identical blob)
//   PERS03 restore is insert-only: live record wins, never overwritten
//   PERS04 memory restore is whole-agent (any live memory => skip)
//   PERS05 derived personalities never persisted; matured survive round trip
//   PERS06 archetype re-derived on restore (never trusted from payload)
//   PERS07 level recomputed from XP on restore (never trusted)
//   PERS08 decode robustness (null/truncated/bad-magic/future-version/over-bound)
//   PERS09 payload validation refusals (bounds, vocabulary, consistency)
//   PERS10 bounded payload size + restore counters/readbacks
using System;
using System.Collections.Generic;
using CapBot.Core.Crew;
using CapBot.Core.Persistence;

namespace CapBot.TaskTests
{
    internal static class PersistenceTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        private static void FreshSetup()
        {
            CrewExperienceRegistry.ResetForTests();
            CrewPersonalityRegistry.ResetForTests();
            CrewMemorySystem.ResetForTests();
        }

        // Valid agent id helper ("AGT:" + 8 lowercase hex chars).
        private static string Id(int n)
        {
            return "AGT:" + n.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static int Run()
        {
            // ---- PERS01: round-trip across all three layers ----------------------
            FreshSetup();
            CrewExperienceRegistry.RecordOutcome(Id(1), CrewAgentRegistry.OutcomeCompleted, 1000);
            CrewExperienceRegistry.RecordOutcome(Id(1), CrewAgentRegistry.OutcomeCompleted, 1100);
            CrewExperienceRegistry.RecordOutcome(Id(1), CrewAgentRegistry.OutcomeFailed, 1200);
            CrewExperienceRegistry.RecordOutcome(Id(2), CrewAgentRegistry.OutcomeVanished, 1300);
            CrewPersonalityRegistry.SetPersonality(Id(1),
                PersonalityFactory.FromValues(Id(1), 80, 20, 50, 60, 40, 1400), 1400);
            CrewPersonalityRegistry.DeriveFor(Id(3), 1450);   // derived — must NOT persist
            CrewMemorySystem.RememberLocation(Id(1), "Bridge", 1500);
            CrewMemorySystem.RememberTaskOutcome(Id(1), 42, CrewAgentRegistry.OutcomeFailed, 1600);
            CrewMemorySystem.RememberCrewEvent(Id(2), "engine fire", 1700);

            CrewPersistence.CrewDataSnapshot snap = CrewPersistence.Capture();
            Check(snap.Experience.Count == 2, "PERS01 two experience records captured");
            Check(snap.MaturedPersonalities.Count == 1, "PERS01 only the matured personality captured");
            Check(snap.Memories.Count == 3, "PERS01 three memory rows captured");
            byte[] blob = CrewPersistence.Encode(snap);
            Check(blob != null && blob.Length > 0, "PERS01 encoded blob non-empty");
            CrewPersistence.CrewDataSnapshot back = CrewPersistence.Decode(blob);
            Check(back != null, "PERS01 decode succeeds");
            Check(back.Experience.Count == 2 && back.MaturedPersonalities.Count == 1 && back.Memories.Count == 3,
                "PERS01 round-trip row counts");

            FreshSetup();
            int inserted = CrewPersistence.Restore(back);
            Check(inserted == 6, "PERS01 all six rows restored");
            CrewExperienceRecord r1 = CrewExperienceRegistry.Get(Id(1));
            Check(r1 != null && r1.TasksCompleted == 2 && r1.TasksFailed == 1 && r1.ExperiencePoints == 22,
                "PERS01 experience values survive (2*10 + 1*2)");
            Check(r1 != null && r1.Level == ExperienceLevels.LevelForXp(22), "PERS01 level recomputed");
            CrewPersonality p1 = CrewPersonalityRegistry.Get(Id(1));
            Check(p1 != null && p1.Source == PersonalityFactory.SourceExplicit,
                "PERS01 matured personality survived as explicit");
            Check(p1 != null && p1.Get(PersonalityTrait.Discipline) == 80 && p1.Get(PersonalityTrait.Boldness) == 20,
                "PERS01 trait values survive");
            Check(CrewMemorySystem.MemoryCountOf(Id(1)) == 2, "PERS01 memory ring restored");
            Check(CrewMemorySystem.Recall(Id(1), MemoryKind.Location, "Bridge") != null,
                "PERS01 location row recallable");
            Check(CrewMemorySystem.Recall(Id(2), MemoryKind.CrewEvent, "engine fire") != null,
                "PERS01 crew-event row recallable");

            // ---- PERS02: determinism ----------------------------------------------
            FreshSetup();
            CrewExperienceRegistry.RecordOutcome(Id(4), CrewAgentRegistry.OutcomeCompleted, 100);
            CrewMemorySystem.RememberLocation(Id(4), "Engine Room", 200);
            byte[] b1 = CrewPersistence.Encode(CrewPersistence.Capture());
            byte[] b2 = CrewPersistence.Encode(CrewPersistence.Capture());
            Check(b1 != null && b2 != null && b1.Length == b2.Length, "PERS02 same-length blobs");
            bool identical = true;
            for (int i = 0; i < b1.Length; i++) { if (b1[i] != b2[i]) { identical = false; break; } }
            Check(identical, "PERS02 byte-identical encode from identical live state");

            // ---- PERS03: insert-only restore (live wins) --------------------------
            FreshSetup();
            CrewExperienceRegistry.RecordOutcome(Id(5), CrewAgentRegistry.OutcomeCompleted, 100);
            CrewExperienceRegistry.RecordOutcome(Id(5), CrewAgentRegistry.OutcomeCompleted, 200);
            CrewExperienceRegistry.RecordOutcome(Id(5), CrewAgentRegistry.OutcomeCompleted, 300);
            CrewExperienceRegistry.RecordOutcome(Id(5), CrewAgentRegistry.OutcomeCompleted, 400);
            long liveXp = CrewExperienceRegistry.Get(Id(5)).ExperiencePoints;   // 40
            CrewPersistence.CrewDataSnapshot staleSnap = new CrewPersistence.CrewDataSnapshot();
            CrewExperienceRecord stale = new CrewExperienceRecord(Id(5), 1);
            stale.ExperiencePoints = 500;
            stale.TasksCompleted = 50;
            stale.TotalOutcomes = 50;
            stale.Level = ExperienceLevels.LevelForXp(500);
            stale.LastOutcome = CrewAgentRegistry.OutcomeFailed;
            stale.LastResultMs = 99999;
            stale.UpdateCount = 50;
            staleSnap.Experience.Add(stale);
            Check(CrewPersistence.Restore(staleSnap) == 0, "PERS03 live record wins over stale save");
            Check(CrewExperienceRegistry.RestoreSkippedCount == 1, "PERS03 skip counted");
            Check(CrewExperienceRegistry.Get(Id(5)).ExperiencePoints == liveXp, "PERS03 live XP untouched");
            Check(CrewExperienceRegistry.Get(Id(5)).Level == 1, "PERS03 live level untouched");

            // ---- PERS04: whole-agent memory skip ----------------------------------
            FreshSetup();
            CrewMemorySystem.RememberLocation(Id(6), "Bridge", 100);
            CrewMemorySystem.RememberLocation(Id(6), "Engine Room", 200);
            byte[] savedBlob = CrewPersistence.Encode(CrewPersistence.Capture());
            Check(savedBlob != null, "PERS04 encode ok");
            CrewMemorySystem.ResetForTests();
            CrewMemorySystem.RememberLocation(Id(6), "Armory", 300);   // one LIVE memory pre-exists
            int restoredMem = CrewPersistence.Restore(CrewPersistence.Decode(savedBlob));
            Check(restoredMem == 0, "PERS04 whole-agent skip when any live memory exists");
            Check(CrewMemorySystem.RestoreSkippedCount == 2, "PERS04 both saved rows skipped (agent-level)");
            Check(CrewMemorySystem.MemoryCountOf(Id(6)) == 1, "PERS04 live memory untouched");
            Check(CrewMemorySystem.Recall(Id(6), MemoryKind.Location, "Armory") != null,
                "PERS04 live memory is the live one");

            // ---- PERS05: derived never persisted; matured survive ------------------
            FreshSetup();
            CrewPersonalityRegistry.DeriveFor(Id(7), 100);
            CrewPersonalityRegistry.SetPersonality(Id(8),
                PersonalityFactory.FromValues(Id(8), 10, 90, 30, 40, 70, 200), 200);
            CrewPersistence.CrewDataSnapshot snap5 = CrewPersistence.Capture();
            Check(snap5.MaturedPersonalities.Count == 1, "PERS05 derived personality not exported");
            Check(snap5.MaturedPersonalities[0].AgentId == Id(8), "PERS05 the matured one exported");
            CrewPersistence.CrewDataSnapshot back5 = CrewPersistence.Decode(CrewPersistence.Encode(snap5));
            FreshSetup();
            CrewPersistence.Restore(back5);
            Check(CrewPersonalityRegistry.Get(Id(7)) == null, "PERS05 no record for the derived agent");
            CrewPersonality restored5 = CrewPersonalityRegistry.Get(Id(8));
            Check(restored5 != null && restored5.Source == PersonalityFactory.SourceExplicit,
                "PERS05 matured restored as explicit");

            // ---- PERS06: archetype re-derived ---------------------------------------
            FreshSetup();
            CrewPersonalityRegistry.SetPersonality(Id(9),
                PersonalityFactory.FromValues(Id(9), 75, 30, 40, 20, 10, 100), 100);
            CrewPersonalityRegistry.SetPersonality(Id(10),
                PersonalityFactory.FromValues(Id(10), 25, 30, 40, 20, 10, 100), 100);
            CrewPersistence.CrewDataSnapshot snap6 = CrewPersistence.Capture();
            CrewPersonalityRegistry.ResetForTests();
            CrewPersistence.Restore(snap6);
            Check(CrewPersonalityRegistry.Get(Id(9)).Archetype == "SENTINEL",
                "PERS06 sentinel archetype re-derived from traits");
            Check(CrewPersonalityRegistry.Get(Id(10)).Archetype == "BALANCED",
                "PERS06 balanced archetype re-derived from traits");

            // ---- PERS07: level recomputed -------------------------------------------
            FreshSetup();
            CrewExperienceRegistry.RecordOutcome(Id(11), CrewAgentRegistry.OutcomeCompleted, 100);
            CrewPersistence.CrewDataSnapshot forgedSnap = new CrewPersistence.CrewDataSnapshot();
            CrewExperienceRecord forged = new CrewExperienceRecord(Id(11), 0);
            forged.ExperiencePoints = 10;
            forged.TasksCompleted = 1;
            forged.TotalOutcomes = 1;
            forged.Level = 10;                       // forged level rides in the payload record
            forged.LastOutcome = CrewAgentRegistry.OutcomeCompleted;
            forged.LastResultMs = 100;
            forged.UpdateCount = 1;
            forgedSnap.Experience.Add(forged);
            CrewExperienceRegistry.ResetForTests();
            CrewPersistence.Restore(forgedSnap);
            CrewExperienceRecord restored7 = CrewExperienceRegistry.Get(Id(11));
            Check(restored7 != null && restored7.Level == ExperienceLevels.LevelForXp(10),
                "PERS07 level recomputed from XP (forged level ignored)");

            // ---- PERS08: decode robustness -------------------------------------------
            Check(CrewPersistence.Encode(null) == null, "PERS08 encode(null) => null");
            Check(CrewPersistence.Decode(null) == null, "PERS08 decode(null) => null");
            Check(CrewPersistence.Decode(new byte[0]) == null, "PERS08 decode(empty) => null");
            byte[] badMagic = new byte[20];
            badMagic[0] = 0x58;   // not "CAPB"
            Check(CrewPersistence.Decode(badMagic) == null, "PERS08 bad magic refused");
            Check(CrewPersistence.Decode(new byte[5]) == null, "PERS08 truncated header refused");
            FreshSetup();
            CrewExperienceRegistry.RecordOutcome(Id(12), CrewAgentRegistry.OutcomeCompleted, 100);
            byte[] good = CrewPersistence.Encode(CrewPersistence.Capture());
            byte[] future = (byte[])good.Clone();
            future[4] = 99;       // version u16 low byte (after u32 magic)
            Check(CrewPersistence.Decode(future) == null, "PERS08 future version refused");
            byte[] overbound = (byte[])good.Clone();
            overbound[6] = 40;    // experience count byte (after u32 magic + u16 version)
            Check(CrewPersistence.Decode(overbound) == null, "PERS08 over-bound count refused");
            byte[] truncated = new byte[good.Length - 5];
            Array.Copy(good, truncated, truncated.Length);
            Check(CrewPersistence.Decode(truncated) == null, "PERS08 truncated body refused");

            // ---- PERS09: restore validation refusals ----------------------------------
            FreshSetup();
            CrewPersistence.CrewDataSnapshot bad1 = new CrewPersistence.CrewDataSnapshot();
            CrewExperienceRecord badExp = new CrewExperienceRecord("BAD", 0);   // invalid id shape
            badExp.ExperiencePoints = 5;
            badExp.TotalOutcomes = 1;
            badExp.TasksCompleted = 1;
            bad1.Experience.Add(badExp);
            Check(CrewPersistence.Restore(bad1) == 0, "PERS09 invalid agent id refused");
            Check(CrewExperienceRegistry.RestoreRefusedCount == 1, "PERS09 refusal counted");
            CrewPersistence.CrewDataSnapshot bad2 = new CrewPersistence.CrewDataSnapshot();
            CrewExperienceRecord negExp = new CrewExperienceRecord(Id(13), 0);
            negExp.ExperiencePoints = -5;
            bad2.Experience.Add(negExp);
            Check(CrewPersistence.Restore(bad2) == 0, "PERS09 negative XP refused");
            CrewPersistence.CrewDataSnapshot bad3 = new CrewPersistence.CrewDataSnapshot();
            CrewExperienceRecord incons = new CrewExperienceRecord(Id(14), 0);
            incons.ExperiencePoints = 10;
            incons.TasksCompleted = 3;
            incons.TotalOutcomes = 2;    // total < counted outcomes => inconsistent
            bad3.Experience.Add(incons);
            Check(CrewPersistence.Restore(bad3) == 0, "PERS09 inconsistent totals refused");
            CrewPersistence.CrewDataSnapshot bad4 = new CrewPersistence.CrewDataSnapshot();
            CrewPersonalityRegistry.MaturedTraitRow outOfRange = new CrewPersonalityRegistry.MaturedTraitRow();
            outOfRange.AgentId = Id(15);
            outOfRange.Discipline = 101;
            outOfRange.Boldness = 50; outOfRange.Sociability = 50; outOfRange.Diligence = 50; outOfRange.Adaptability = 50;
            outOfRange.DerivedTimeMs = 0;
            bad4.MaturedPersonalities.Add(outOfRange);
            Check(CrewPersistence.Restore(bad4) == 0, "PERS09 out-of-range trait refused");
            Check(CrewPersonalityRegistry.RestoreRefusedCount == 1, "PERS09 personality refusal counted");
            CrewPersistence.CrewDataSnapshot bad5 = new CrewPersistence.CrewDataSnapshot();
            CrewMemorySystem.MemoryRow badMem = new CrewMemorySystem.MemoryRow();
            badMem.AgentId = Id(16);
            badMem.Kind = MemoryKind.TaskOutcome;
            badMem.TaskId = 7;
            badMem.Outcome = "made-up-outcome";
            bad5.Memories.Add(badMem);
            Check(CrewPersistence.Restore(bad5) == 0, "PERS09 unknown outcome vocabulary refused");
            Check(CrewMemorySystem.RestoreRefusedCount == 1, "PERS09 memory refusal counted");

            // ---- PERS10: bounded payload + counters ------------------------------------
            FreshSetup();
            for (int i = 0; i < 32; i++)
            {
                CrewExperienceRegistry.RecordOutcome(Id(i + 20), CrewAgentRegistry.OutcomeCompleted, i * 10);
            }
            byte[] fullBlob = CrewPersistence.Encode(CrewPersistence.Capture());
            Check(fullBlob != null && fullBlob.Length < CrewPersistence.MaxEncodedLength,
                "PERS10 max-registry payload within bounds");
            Check(fullBlob.Length == CrewPersistence.Encode(CrewPersistence.Capture()).Length,
                "PERS10 payload size stable");
            FreshSetup();
            int restoredFull = CrewPersistence.Restore(CrewPersistence.Decode(fullBlob));
            Check(restoredFull == 32, "PERS10 all 32 restored");
            Check(CrewExperienceRegistry.RestoredCount == 32, "PERS10 restored readback");
            CrewPersistence.CrewDataSnapshot over = new CrewPersistence.CrewDataSnapshot();
            for (int i = 0; i < 40; i++)
            {
                CrewExperienceRecord extra = new CrewExperienceRecord(Id(i + 100), 0);
                extra.ExperiencePoints = 1;
                extra.TasksCompleted = 1;
                extra.TotalOutcomes = 1;
                over.Experience.Add(extra);
            }
            Check(CrewPersistence.Encode(over) == null, "PERS10 over-bound snapshot cannot encode");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}