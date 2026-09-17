using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CapBot.Core.Crew;

namespace CapBot.Core.Persistence
{
    // ---- Phase 28: crew state save codec ---------------------------------------
    //
    // Deterministic, bounded byte codec for the Phase 12 experience records,
    // Phase 13 memory rings, and the NON-DERIVED Phase 11 personalities (the
    // P25 matured/explicit half — derived personalities are a pure function of
    // the stable AgentId and re-derive identically on demand, so they are
    // never persisted; this closes the P25 documented contract gap).
    //
    // The transport is the PML save pipeline (CrewSave : PMLSaveData in this
    // folder — game-build only). This file is the PURE-DOMAIN half: no game,
    // PML, Photon, wall-clock, LINQ, or per-frame reads. Load is TOLERANT:
    // malformed or truncated input restores what it can and never throws
    // (PML contains exceptions, but the contract here is never-throw anyway).
    //
    // Restore paths are the registries' own validated restore hooks
    // (CrewExperienceRegistry.RestoreRecord, CrewMemorySystem.RestoreMemoryEntry)
    // and the sanctioned write path for personalities (CrewPersonalityRegistry
    // .SetPersonality) — no direct static mutation, never a fabricated record.
    // Determinism: same registry state => byte-identical blob (records are
    // serialized in ordinal AgentId order; fields are fixed-width).
    public static class CrewSaveCodec
    {
        public const int SaveSchemaVersion = 1;
        public const int MaxPersistedAgents = 32;      // = all crew registry bounds
        public const int MaxBlockBytes = 262144;       // defensive per-block bound (256 KB)

        // Block magics (fixed, compared — never parsed as structure).
        internal const string MagicExperience = "CBXP";
        internal const string MagicMemory = "CBME";
        internal const string MagicPersonality = "CBPE";

        // ---- experience block -------------------------------------------------

        // One row per CrewExperienceRecord, ordinal AgentId order. An empty
        // registry saves an EMPTY blob (the block is omitted from the envelope).
        public static byte[] SaveExperience()
        {
            List<CrewExperienceRecord> records = CrewExperienceRegistry.SnapshotsForSave();
            if (records.Count == 0) return new byte[0];
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicExperience.ToCharArray());
                w.Write((byte)SaveSchemaVersion);
                w.Write(records.Count);
                for (int i = 0; i < records.Count; i++)
                {
                    CrewExperienceRecord r = records[i];
                    WriteToken(w, r.AgentId);
                    w.Write(r.CreatedTimeMs);
                    w.Write(r.TasksCompleted);
                    w.Write(r.TasksCancelled);
                    w.Write(r.TasksExpired);
                    w.Write(r.TasksVanished);
                    w.Write(r.TasksFailed);
                    w.Write(r.TotalOutcomes);
                    w.Write(r.ExperiencePoints);
                    WriteToken(w, r.LastOutcome);
                    w.Write(r.LastResultMs);
                }
                return ms.ToArray();
            }
        }

        // Restores experience rows through CrewExperienceRegistry.RestoreRecord.
        // Returns the number of records restored (0 on empty/absent rows);
        // malformed header => 0. Truncation stops at the last complete row.
        public static int LoadExperience(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;
            int restored = 0;
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader r = new BinaryReader(ms))
                {
                    if (!ReadHeader(r, MagicExperience)) return 0;
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxPersistedAgents) return 0;
                    for (int i = 0; i < count; i++)
                    {
                        string agentId = ReadToken(r);
                        int createdTimeMs = r.ReadInt32();
                        long completed = r.ReadInt64();
                        long cancelled = r.ReadInt64();
                        long expired = r.ReadInt64();
                        long vanished = r.ReadInt64();
                        long failed = r.ReadInt64();
                        long totalOutcomes = r.ReadInt64();
                        long xp = r.ReadInt64();
                        string lastOutcome = ReadToken(r);
                        int lastResultMs = r.ReadInt32();
                        if (CrewExperienceRegistry.RestoreRecord(agentId, createdTimeMs,
                            completed, cancelled, expired, vanished, failed,
                            totalOutcomes, xp, lastOutcome, lastResultMs))
                        {
                            restored++;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Tolerant: partial restore stands, never a throw.
            }
            return restored;
        }

        // ---- memory block -------------------------------------------------------

        // All memory rows of all agents (bounded ≤ 32 agents × 8 entries),
        // agents in ordinal AgentId order, rows in ring order. An empty
        // registry saves an EMPTY blob.
        public static byte[] SaveMemories()
        {
            List<CrewMemorySystem.MemoryRow> rows = CrewMemorySystem.RowsForSave();
            if (rows.Count == 0) return new byte[0];
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicMemory.ToCharArray());
                w.Write((byte)SaveSchemaVersion);
                w.Write(rows.Count);
                for (int i = 0; i < rows.Count; i++)
                {
                    CrewMemorySystem.MemoryRow row = rows[i];
                    WriteToken(w, row.AgentId);
                    w.Write((byte)row.Kind);
                    w.Write(row.TaskId);
                    WriteToken(w, row.Text);
                    WriteToken(w, row.Outcome);
                    w.Write(row.CreatedTimeMs);
                    w.Write(row.LastSeenMs);
                    w.Write(row.UpdateCount);
                }
                return ms.ToArray();
            }
        }

        // Restores memory rows through CrewMemorySystem.RestoreMemoryEntry.
        // Same tolerant contract as LoadExperience.
        public static int LoadMemories(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;
            int restored = 0;
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader r = new BinaryReader(ms))
                {
                    if (!ReadHeader(r, MagicMemory)) return 0;
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxPersistedAgents * CrewMemorySystem.MaxMemoriesPerAgent) return 0;
                    for (int i = 0; i < count; i++)
                    {
                        string agentId = ReadToken(r);
                        byte kindByte = r.ReadByte();
                        long taskId = r.ReadInt64();
                        string text = ReadToken(r);
                        string outcome = ReadToken(r);
                        int createdTimeMs = r.ReadInt32();
                        int lastSeenMs = r.ReadInt32();
                        long updateCount = r.ReadInt64();
                        if (kindByte < 1 || kindByte > 3) continue; // unknown kind: skip row
                        if (CrewMemorySystem.RestoreMemoryEntry(agentId, (MemoryKind)kindByte,
                            taskId, text, outcome, createdTimeMs, lastSeenMs, updateCount))
                        {
                            restored++;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Tolerant: partial restore stands, never a throw.
            }
            return restored;
        }

        // ---- personality block --------------------------------------------------

        // Explicit/neutral (non-derived) personalities only. Derived records
        // re-derive identically from the AgentId (PersonalityFactory.Derive) —
        // persisting them would be redundant state.
        public static byte[] SavePersonalities()
        {
            List<CrewPersonality> all = CrewPersonalityRegistry.AllForSave();
            List<CrewPersonality> persist = new List<CrewPersonality>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Source != PersonalityFactory.SourceDerived) persist.Add(all[i]);
            }
            if (persist.Count == 0) return new byte[0];
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(MagicPersonality.ToCharArray());
                w.Write((byte)SaveSchemaVersion);
                w.Write(persist.Count);
                for (int i = 0; i < persist.Count; i++)
                {
                    CrewPersonality p = persist[i];
                    WriteToken(w, p.AgentId);
                    w.Write(p.Get(PersonalityTrait.Discipline));
                    w.Write(p.Get(PersonalityTrait.Boldness));
                    w.Write(p.Get(PersonalityTrait.Sociability));
                    w.Write(p.Get(PersonalityTrait.Diligence));
                    w.Write(p.Get(PersonalityTrait.Adaptability));
                    WriteToken(w, p.Archetype);
                    WriteToken(w, p.Source);
                    w.Write(p.DerivedTimeMs);
                }
                return ms.ToArray();
            }
        }

        // Restores personalities through SetPersonality (the sole sanctioned
        // write channel) after rebuilding the immutable record. Rows with a
        // "derived" source are skipped defensively (save never writes them).
        // Same tolerant contract as LoadExperience.
        public static int LoadPersonalities(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;
            int restored = 0;
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader r = new BinaryReader(ms))
                {
                    if (!ReadHeader(r, MagicPersonality)) return 0;
                    int count = r.ReadInt32();
                    if (count < 0 || count > MaxPersistedAgents) return 0;
                    for (int i = 0; i < count; i++)
                    {
                        string agentId = ReadToken(r);
                        int discipline = r.ReadInt32();
                        int boldness = r.ReadInt32();
                        int sociability = r.ReadInt32();
                        int diligence = r.ReadInt32();
                        int adaptability = r.ReadInt32();
                        string archetype = ReadToken(r);
                        string source = ReadToken(r);
                        int derivedTimeMs = r.ReadInt32();
                        if (source == PersonalityFactory.SourceDerived) continue;
                        CrewPersonality rebuilt = new CrewPersonality(agentId,
                            new int[5] { discipline, boldness, sociability, diligence, adaptability },
                            archetype, source, derivedTimeMs);
                        if (CrewPersonalityRegistry.SetPersonality(agentId, rebuilt, derivedTimeMs))
                        {
                            restored++;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Tolerant: partial restore stands, never a throw.
            }
            return restored;
        }

        // ---- whole-blob envelope (what the PML save stores) ----------------------

        // Length-prefixed concatenation of the self-describing blocks; each
        // entry is [4-byte length][payload] where the payload itself begins
        // with its [4-byte magic][1-byte schema version] header. LoadAll walks
        // the entry stream, restores each recognized block, and skips unknown
        // ones — forward compatible. Empty blocks are omitted entirely.
        public static byte[] SaveAll()
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                WriteBlock(w, SaveExperience());
                WriteBlock(w, SaveMemories());
                WriteBlock(w, SavePersonalities());
                return ms.ToArray();
            }
        }

        // Returns the total number of restored records across all blocks
        // (experience + memory rows + personalities). Never throws.
        public static int LoadAll(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;
            int restored = 0;
            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader r = new BinaryReader(ms))
                {
                    while (r.BaseStream.Length - r.BaseStream.Position >= 4)
                    {
                        int length = r.ReadInt32();
                        if (length <= 0 || length > MaxBlockBytes) break;
                        if (r.BaseStream.Length - r.BaseStream.Position < length) break;
                        byte[] payload = r.ReadBytes(length);
                        if (payload.Length < 5) continue;
                        string magic = new string(new char[4]
                        {
                            (char)payload[0], (char)payload[1], (char)payload[2], (char)payload[3],
                        });
                        if (magic == MagicExperience) restored += LoadExperience(payload);
                        else if (magic == MagicMemory) restored += LoadMemories(payload);
                        else if (magic == MagicPersonality) restored += LoadPersonalities(payload);
                        // unknown magic: skip (forward compatible)
                    }
                }
            }
            catch (Exception)
            {
                // Tolerant: partial restore stands, never a throw.
            }
            return restored;
        }

        // ---- helpers --------------------------------------------------------------

        private static void WriteBlock(BinaryWriter w, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            w.Write(payload.Length);
            w.Write(payload);
        }

        private static bool ReadHeader(BinaryReader r, string expectedMagic)
        {
            string magic = new string(r.ReadChars(4));
            byte version = r.ReadByte();
            return magic == expectedMagic && version == SaveSchemaVersion;
        }

        // Token: 1-byte null flag, 1-byte length (≤ 255), UTF-8 bytes. All
        // persisted strings are short (ids 12, outcomes ≤ 8, text ≤ 32,
        // archetypes ≤ 11) — the 255 cap is defensive headroom.
        private static void WriteToken(BinaryWriter w, string s)
        {
            if (s == null)
            {
                w.Write((byte)0);
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > 255) bytes = Encoding.UTF8.GetBytes(s.Substring(0, 255));
            w.Write((byte)1);
            w.Write((byte)bytes.Length);
            w.Write(bytes);
        }

        private static string ReadToken(BinaryReader r)
        {
            byte flag = r.ReadByte();
            if (flag == 0) return null;
            if (flag != 1) throw new IOException("corrupt token flag");
            int length = r.ReadByte();
            byte[] bytes = r.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }
    }
}