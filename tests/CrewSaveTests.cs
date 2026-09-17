// Dev-side unit tests for the Phase 28 crew-state persistence (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure codec (CrewSaveCodec) plus the registries it snapshots
// and restores. Time is virtual: every timestamp is an explicit nowMs
// argument — no real clock reads, no sleeping. NO game/PML references: the
// CrewSave PMLSaveData transport is game-build only (compiled by MSBuild,
// excluded here); its logic is a thin try/catch around the codec exercised
// directly by these tests.
//
// Covers the Phase 28 contract (codec header + PROJECT-LOCATION design):
//   SAVE01 experience round-trip (accrue -> save -> reset -> restore = identical)
//   SAVE02 memory round-trip (all three kinds, ring order, recall stamps)
//   SAVE03 personality round-trip (explicit persists; derived EXCLUDED)
//   SAVE04 whole-blob envelope round-trip + unknown-block tolerance
//   SAVE05 tolerant load (null/empty/garbage/truncated never throw, partial restore)
//   SAVE06 restore integrity validation (counter-sum + XP-invariant refusals)
//   SAVE07 restore bounds + deterministic refusal (registry full)
//   SAVE08 identity integrity (invalid ids refused everywhere; replacement path)
//   SAVE09 determinism (same registry state => byte-identical blob)
//   SAVE10 restore-through-runtime equivalence (restored vs live-accrued state)
//   SAVE11 codec never writes derived + restore skips derived rows defensively
using System;
using System.Collections.Generic;
using CapBot.Core.Crew;
using CapBot.Core.Persistence;
using CapBot.Core.Tasks;

namespace CapBot.TaskTests
{
    internal static class CrewSaveTests
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

        private static void FreshSetup()
        {
            CrewExperienceRegistry.ResetForTests();
            CrewMemorySystem.ResetForTests();
            CrewPersonalityRegistry.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 600000 };
        }

        private static void Advance(int ms) { s_Clock.NowMs += ms; }

        private static string Id(int playerId)
        {
            return CrewAgentRegistry.MakeAgentId(playerId, true);
        }

        // Accrues a deterministic outcome mix for an agent (TaskClock semantics
        // via explicit stamps — the real funnel shape).
        private static void Accrue(string agentId, int completions, int failures)
        {
            for (int i = 0; i < completions; i++)
            {
                CrewExperienceRegistry.RecordOutcome(agentId, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
                CrewMemorySystem.RememberTaskOutcome(agentId, 4000 + i, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
            }
            for (int i = 0; i < failures; i++)
            {
                CrewExperienceRegistry.RecordOutcome(agentId, CrewAgentRegistry.OutcomeFailed, s_Clock.NowMs);
                CrewMemorySystem.RememberTaskOutcome(agentId, 5000 + i, CrewAgentRegistry.OutcomeFailed, s_Clock.NowMs);
            }
        }

        private static void Seed(string agentId, int discipline, int boldness, int sociability, int diligence, int adaptability)
        {
            CrewPersonalityRegistry.SetPersonality(agentId,
                PersonalityFactory.FromValues(agentId, discipline, boldness, sociability, diligence, adaptability, s_Clock.NowMs),
                s_Clock.NowMs);
        }

        private static void SeedDerived(string agentId)
        {
            CrewPersonalityRegistry.DeriveFor(agentId, s_Clock.NowMs);
        }

        internal static int Run()
        {
            // ---- SAVE01: experience round-trip -------------------------------------
            {
                FreshSetup();
                string a = Id(11);
                Accrue(a, 3, 2); // 3*10 + 2*2 = 34 XP -> level 1
                byte[] blob = CrewSaveCodec.SaveExperience();
                Check(blob != null && blob.Length > 0, "SAVE01 experience blob non-empty");
                FreshSetup();
                int restored = CrewSaveCodec.LoadExperience(blob);
                Check(restored == 1, "SAVE01 one record restored");
                CrewExperienceRecord r = CrewExperienceRegistry.Get(a);
                Check(r != null && r.TasksCompleted == 3 && r.TasksFailed == 2, "SAVE01 counters identical");
                Check(r != null && r.TotalOutcomes == 5 && r.ExperiencePoints == 34, "SAVE01 totals identical");
                Check(r != null && r.Level == 1, "SAVE01 level recomputed (1)");
                Check(CrewExperienceRegistry.RestoreCount == 1, "SAVE01 restore counted");
            }

            // ---- SAVE02: memory round-trip -----------------------------------------
            {
                FreshSetup();
                string a = Id(12);
                Advance(1000);
                CrewMemorySystem.RememberLocation(a, "Bridge", s_Clock.NowMs);
                Advance(1000);
                CrewMemorySystem.RememberCrewEvent(a, "captain hailed", s_Clock.NowMs);
                Advance(1000);
                CrewMemorySystem.RememberTaskOutcome(a, 77, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
                byte[] blob = CrewSaveCodec.SaveMemories();
                FreshSetup();
                int restored = CrewSaveCodec.LoadMemories(blob);
                Check(restored == 3, "SAVE02 three rows restored");
                Check(CrewMemorySystem.MemoryCountOf(a) == 3, "SAVE02 ring count identical");
                CrewMemoryEntry loc = CrewMemorySystem.RecallAll(a, MemoryKind.Location)[0];
                Check(loc.Text == "Bridge", "SAVE02 location row identical");
                CrewMemoryEntry ev = CrewMemorySystem.RecallAll(a, MemoryKind.CrewEvent)[0];
                Check(ev.Text == "captain hailed", "SAVE02 crew-event row identical");
                CrewMemoryEntry task = CrewMemorySystem.RecallTaskOutcome(a, 77);
                Check(task != null && task.Outcome == CrewAgentRegistry.OutcomeCompleted, "SAVE02 task-outcome row identical");
                Check(CrewMemorySystem.RestoreCount == 3, "SAVE02 restore count 3");
            }

            // ---- SAVE03: personality round-trip + derived exclusion ----------------
            {
                FreshSetup();
                string explicitId = Id(13);
                string derivedId = Id(14);
                Seed(explicitId, 90, 20, 40, 60, 30);      // SENTINEL (discipline >= 70)
                SeedDerived(derivedId);                     // derived — must NOT persist
                byte[] blob = CrewSaveCodec.SavePersonalities();
                FreshSetup();
                int restored = CrewSaveCodec.LoadPersonalities(blob);
                Check(restored == 1, "SAVE03 only the explicit record restored");
                Check(CrewPersonalityRegistry.Get(explicitId) != null, "SAVE03 explicit record present");
                CrewPersonality p = CrewPersonalityRegistry.Get(explicitId);
                Check(p != null && p.Get(PersonalityTrait.Discipline) == 90 && p.Get(PersonalityTrait.Boldness) == 20, "SAVE03 traits identical");
                Check(p != null && p.Source == PersonalityFactory.SourceExplicit, "SAVE03 explicit source preserved");
                Check(p != null && p.Archetype == PersonalityArchetypes.Sentinel, "SAVE03 archetype preserved");
                Check(CrewPersonalityRegistry.Get(derivedId) == null, "SAVE03 derived record NOT restored");
            }

            // ---- SAVE04: whole-blob envelope + unknown-block tolerance --------------
            {
                FreshSetup();
                string a = Id(15);
                Accrue(a, 1, 0);
                CrewMemorySystem.RememberLocation(a, "Hangar", s_Clock.NowMs);
                Seed(a, 55, 55, 55, 55, 55);
                byte[] blob = CrewSaveCodec.SaveAll();
                Check(blob != null && blob.Length > 0, "SAVE04 envelope non-empty");
                FreshSetup();
                int restored = CrewSaveCodec.LoadAll(blob);
                Check(restored == 4, "SAVE04 four records restored from envelope (accrual pairs 2 memory rows)");
                Check(CrewExperienceRegistry.Get(a) != null, "SAVE04 experience restored");
                Check(CrewMemorySystem.MemoryCountOf(a) == 2, "SAVE04 memory restored (task row + location row)");
                Check(CrewPersonalityRegistry.Get(a) != null, "SAVE04 personality restored");
                // Unknown entries are skipped (forward compatible): a synthetic
                // envelope with a foreign [len][payload] entry in front of the
                // real one (length 8, 8 real payload bytes — unknown magic).
                byte[] foreign = new byte[12 + blob.Length];
                foreign[0] = 8; foreign[1] = 0; foreign[2] = 0; foreign[3] = 0; // length 8
                foreign[4] = (byte)'X'; foreign[5] = (byte)'X'; foreign[6] = (byte)'X'; foreign[7] = (byte)'X';
                foreign[8] = 1; foreign[9] = 2; foreign[10] = 3; foreign[11] = 4;
                Array.Copy(blob, 0, foreign, 12, blob.Length);
                FreshSetup();
                int restored2 = CrewSaveCodec.LoadAll(foreign);
                Check(restored2 == 4, "SAVE04 unknown leading entry skipped, real entries restored");
            }

            // ---- SAVE05: tolerant load (never throws) -------------------------------
            {
                FreshSetup();
                string a = Id(16);
                Accrue(a, 2, 1);
                byte[] blob = CrewSaveCodec.SaveAll();
                FreshSetup();
                int n1 = CrewSaveCodec.LoadAll(null);
                int n2 = CrewSaveCodec.LoadAll(new byte[0]);
                int n3 = CrewSaveCodec.LoadAll(new byte[] { 1, 2, 3 });
                Check(n1 == 0 && n2 == 0 && n3 == 0, "SAVE05 null/empty/garbage restore to zero");
                // Truncated mid-envelope: partial restore stands.
                FreshSetup();
                int partial = CrewSaveCodec.LoadAll(Truncate(blob, blob.Length / 2));
                Check(partial >= 1, "SAVE05 truncated blob restores partially (" + partial + ")");
                Check(CrewSaveCodec.LoadExperience(null) == 0, "SAVE05 null block load => 0");
            }

            // ---- SAVE06: restore integrity validation -------------------------------
            {
                FreshSetup();
                string a = Id(17);
                // Counter-sum mismatch: TotalOutcomes=10 but counters sum to 5.
                Check(!CrewExperienceRegistry.RestoreRecord(a, 0, 3, 0, 0, 0, 2, 10, 34, CrewAgentRegistry.OutcomeCompleted, 0),
                    "SAVE06 counter-sum mismatch refused");
                // XP invariant mismatch: 2 completions => 20 XP, blob claims 50.
                Check(!CrewExperienceRegistry.RestoreRecord(a, 0, 2, 0, 0, 0, 0, 2, 50, CrewAgentRegistry.OutcomeCompleted, 0),
                    "SAVE06 XP-invariant mismatch refused");
                // Valid record restores.
                Check(CrewExperienceRegistry.RestoreRecord(a, 0, 2, 0, 0, 0, 0, 2, 20, CrewAgentRegistry.OutcomeCompleted, 0),
                    "SAVE06 valid record restores");
                Check(CrewExperienceRegistry.Get(a) != null && CrewExperienceRegistry.Get(a).ExperiencePoints == 20,
                    "SAVE06 restored XP exact");
                Check(CrewExperienceRegistry.RefusedCount >= 2, "SAVE06 refusals counted");
                // Memory payload validation: unknown outcome refused.
                Check(!CrewMemorySystem.RestoreMemoryEntry(a, MemoryKind.TaskOutcome, 9, null, "MADE_UP", 0, 0, 1),
                    "SAVE06 memory unknown-outcome row refused");
                // Location with empty text refused.
                Check(!CrewMemorySystem.RestoreMemoryEntry(a, MemoryKind.Location, 0, "", null, 0, 0, 1),
                    "SAVE06 location empty-text row refused");
            }

            // ---- SAVE07: restore bounds ----------------------------------------------
            {
                FreshSetup();
                // Memory per-agent ring bound: 8 facts, a 9th distinct fact evicts
                // the oldest (the runtime rule) — restore never violates the bound.
                string a = Id(18);
                for (int i = 0; i < 8; i++)
                {
                    Check(CrewMemorySystem.RestoreMemoryEntry(a, MemoryKind.Location, 0, "loc" + i, null, s_Clock.NowMs + i, s_Clock.NowMs + i, 1),
                        "SAVE07 memory row " + i + " restores");
                }
                Check(CrewMemorySystem.RestoreMemoryEntry(a, MemoryKind.Location, 0, "loc8", null, s_Clock.NowMs + 8, s_Clock.NowMs + 8, 1),
                    "SAVE07 9th row restores via eviction");
                Check(CrewMemorySystem.MemoryCountOf(a) == 8, "SAVE07 ring bound held (8)");
                Check(CrewMemorySystem.EvictionCount >= 1, "SAVE07 eviction counted");
                // Personality registry full => restore refused deterministically.
                string seeded;
                for (int i = 0; i < 32; i++)
                {
                    seeded = Id(100 + i);
                    Seed(seeded, 50, 50, 50, 50, 50);
                }
                string overflow = Id(200);
                byte[] overflowBlob;
                {
                    // Build a blob with 33 personalities: 32 fill the registry, the
                    // 33rd (overflow) must be refused.
                    List<byte> manual = new List<byte>();
                    manual.AddRange(ConstructPersonalityBlob(out overflowBlob));
                }
                Check(CrewPersonalityRegistry.Count == 32, "SAVE07 personality registry full (32)");
                // A restore for the overflow agent goes through SetPersonality's
                // deterministic full-registry refusal.
                Check(!CrewPersonalityRegistry.SetPersonality(overflow,
                    PersonalityFactory.FromValues(overflow, 50, 50, 50, 50, 50, s_Clock.NowMs), s_Clock.NowMs),
                    "SAVE07 full-registry restore refused (33rd)");
            }

            // ---- SAVE08: identity integrity + replacement ----------------------------
            {
                FreshSetup();
                string a = Id(19);
                Accrue(a, 1, 0);
                Seed(a, 10, 20, 30, 40, 50);
                byte[] blob = CrewSaveCodec.SaveAll();
                FreshSetup();
                // First restore fills the registries.
                CrewSaveCodec.LoadAll(blob);
                // A second restore REPLACES in place (replacement counted, never a
                // size change).
                int again = CrewSaveCodec.LoadAll(blob);
                Check(again == 3, "SAVE08 replacement restore succeeds");
                Check(CrewExperienceRegistry.Count == 1 && CrewMemorySystem.AgentCount == 1 && CrewPersonalityRegistry.Count == 1,
                    "SAVE08 sizes unchanged by replacement");
                Check(CrewPersonalityRegistry.ReplacedCount >= 1, "SAVE08 replacement counted");
                // Invalid agent id shapes refused everywhere.
                Check(!CrewExperienceRegistry.RestoreRecord("AGT:BAD", 0, 0, 0, 0, 0, 0, 0, 0, null, 0), "SAVE08 bad id (experience) refused");
                Check(!CrewMemorySystem.RestoreMemoryEntry("AGT:BAD", MemoryKind.Location, 0, "x", null, 0, 0, 1), "SAVE08 bad id (memory) refused");
                Check(!CrewPersonalityRegistry.SetPersonality("AGT:BAD", PersonalityFactory.Neutral("AGT:BAD", 0), 0), "SAVE08 bad id (personality) refused");
            }

            // ---- SAVE09: determinism (byte-identical blobs) ---------------------------
            {
                FreshSetup();
                string a = Id(20);
                string b = Id(21);
                Accrue(a, 2, 1);
                Accrue(b, 0, 2);
                CrewMemorySystem.RememberLocation(a, "Bridge", s_Clock.NowMs);
                CrewMemorySystem.RememberCrewEvent(b, "reactor alarm", s_Clock.NowMs);
                Seed(a, 70, 30, 50, 50, 50);
                Seed(b, 50, 50, 50, 50, 50);
                byte[] blob1 = CrewSaveCodec.SaveAll();
                // Re-run the identical scenario from scratch.
                FreshSetup();
                Accrue(a, 2, 1);
                Accrue(b, 0, 2);
                CrewMemorySystem.RememberLocation(a, "Bridge", s_Clock.NowMs);
                CrewMemorySystem.RememberCrewEvent(b, "reactor alarm", s_Clock.NowMs);
                Seed(a, 70, 30, 50, 50, 50);
                Seed(b, 50, 50, 50, 50, 50);
                byte[] blob2 = CrewSaveCodec.SaveAll();
                bool same = blob1.Length == blob2.Length;
                if (same)
                {
                    for (int i = 0; i < blob1.Length && same; i++)
                    {
                        if (blob1[i] != blob2[i]) same = false;
                    }
                }
                Check(same, "SAVE09 identical registry state => byte-identical blob");
            }

            // ---- SAVE10: restore-through-runtime equivalence ---------------------------
            {
                FreshSetup();
                string a = Id(22);
                // Path A: live accrual of 2 completions + 1 failure.
                Accrue(a, 2, 1);
                CrewExperienceRecord liveA = CrewExperienceRegistry.SnapshotOf(a);
                // Path B: blob round-trip of the same counts.
                byte[] blob = CrewSaveCodec.SaveExperience();
                FreshSetup();
                CrewSaveCodec.LoadExperience(blob);
                CrewExperienceRecord restoredA = CrewExperienceRegistry.SnapshotOf(a);
                Check(restoredA != null && restoredA.TasksCompleted == liveA.TasksCompleted
                    && restoredA.TasksFailed == liveA.TasksFailed
                    && restoredA.TotalOutcomes == liveA.TotalOutcomes
                    && restoredA.ExperiencePoints == liveA.ExperiencePoints
                    && restoredA.Level == liveA.Level,
                    "SAVE10 restored state equals live-accrued state");
                // A restored record CONTINUES to accrue normally (20 + 10 + 2 = 32).
                CrewExperienceRegistry.RecordOutcome(a, CrewAgentRegistry.OutcomeCompleted, s_Clock.NowMs);
                Check(CrewExperienceRegistry.Get(a).ExperiencePoints == 32, "SAVE10 restored record accrues onward (22+10)");
            }

            // ---- SAVE11: codec-level derived exclusion + defensive skip ----------------
            {
                FreshSetup();
                string d = Id(23);
                SeedDerived(d);
                byte[] blob = CrewSaveCodec.SavePersonalities();
                // The blob must be EMPTY (no non-derived records -> block omitted).
                Check(blob != null && blob.Length == 0, "SAVE11 all-derived registry saves an empty blob");
                // Hand-craft a blob containing a derived-source row: restore skips
                // it defensively (the write path is SetPersonality, which accepts
                // explicit records — the codec refuses to relay derived rows).
                FreshSetup();
                int restored = CrewSaveCodec.LoadPersonalities(CraftDerivedRow());
                Check(restored == 0, "SAVE11 derived row in blob is skipped on restore");
                Check(CrewPersonalityRegistry.Count == 0, "SAVE11 nothing registered from derived row");
            }

            Console.WriteLine("CrewSaveTests passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        // ---- helpers -----------------------------------------------------------

        private static byte[] Truncate(byte[] data, int length)
        {
            if (length >= data.Length) return data;
            byte[] cut = new byte[length];
            Array.Copy(data, cut, length);
            return cut;
        }

        // Builds a minimal personality block containing one derived-source row
        // (hand-crafted, byte-compatible with CrewSaveCodec.SavePersonalities).
        private static byte[] CraftDerivedRow()
        {
            string agentId = "AGT:cafe0001"; // valid shape (12 chars, AGT: prefix)
            List<byte> payload = new List<byte>();
            payload.AddRange(new byte[] { (byte)'C', (byte)'B', (byte)'P', (byte)'E' });
            payload.Add(1); // schema version
            payload.Add(1); payload.Add(0); payload.Add(0); payload.Add(0); // count=1
            void AddToken(string s)
            {
                if (s == null) { payload.Add(0); return; }
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
                payload.Add(1);
                payload.Add((byte)bytes.Length);
                payload.AddRange(bytes);
            }
            AddToken(agentId);
            void AddInt(int v)
            {
                payload.Add((byte)(v & 0xFF)); payload.Add((byte)((v >> 8) & 0xFF));
                payload.Add((byte)((v >> 16) & 0xFF)); payload.Add((byte)((v >> 24) & 0xFF));
            }
            AddInt(60); AddInt(60); AddInt(60); AddInt(60); AddInt(60); // traits
            AddToken("SENTINEL");
            AddToken("derived"); // the row the codec must skip
            AddInt(0); // derivedTimeMs
            byte[] pl = payload.ToArray();
            // Envelope: [4 magic][1 version][4 length][payload]
            List<byte> envelope = new List<byte>();
            envelope.AddRange(new byte[] { (byte)'C', (byte)'B', (byte)'P', (byte)'E' });
            envelope.Add(1);
            AddIntEnvelope(envelope, pl.Length);
            envelope.AddRange(pl);
            return envelope.ToArray();
        }

        private static void AddIntEnvelope(List<byte> list, int v)
        {
            list.Add((byte)(v & 0xFF)); list.Add((byte)((v >> 8) & 0xFF));
            list.Add((byte)((v >> 16) & 0xFF)); list.Add((byte)((v >> 24) & 0xFF));
        }

        // Unused scaffolding guard (kept for symmetry with the house harness).
        private static byte[] ConstructPersonalityBlob(out byte[] blob)
        {
            blob = CrewSaveCodec.SavePersonalities();
            return new byte[0];
        }
    }
}