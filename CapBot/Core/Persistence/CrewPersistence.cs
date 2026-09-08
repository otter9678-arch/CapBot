using System;
using System.Collections.Generic;
using System.IO;
using CapBot.Core.Crew;

namespace CapBot.Core.Persistence
{
    // ---- Phase 28: crew data persistence ----------------------------------------
    //
    // Pure C# serializer for the cross-session crew data layers: P12 experience
    // records, P13 memory rows, and MATURED (explicit-source) P11 personalities.
    //
    // Scope contract (P28):
    //   - The three registries stay the SOLE owners of live state; this layer
    //     is a read-only exporter / insert-only restorer between them and the
    //     byte payload. It never mutates live state beyond the insert-only
    //     restore APIs, never touches the task pipeline, and has no Harmony
    //     targets, no tick driver, no WorldTick block.
    //   - Derived personalities are NOT persisted (reproducible from the
    //     stable AgentId by design — the P11 derivation is a pure function);
    //     persisting them would be redundant and could go stale. Archetypes
    //     are NEVER persisted either (re-derived from traits on restore).
    //   - Live state always wins: restore is INSERT-ONLY, never overwrites or
    //     evicts live records. Memory restore is whole-agent (an agent with
    //     any live memory is skipped entirely).
    //   - No PULSAR/PML/Harmony references (the P19 narrow-compile lesson);
    //     the PML save-slot adapter lives in CapBot\CrewPersistenceAdapter.cs
    //     (production wiring, not testable in the pure domain suite).
    //
    // Format: "CAPB" magic, u16 version, bounded sections; deterministic
    // byte-for-byte for the same logical payload (tests assert round-trip +
    // determinism). All strings are length-prefixed UTF-8, null-safe.
    //
    // The legacy Autonomy.Learning XP matrix keeps its own save slot
    // (CapBotLearningSave, untouched); this layer ADDS crew-data persistence,
    // it does not migrate or re-interpret the legacy slot.
    public static class CrewPersistence
    {
        public const ushort FormatVersion = 1;
        public const int MaxAgents = 32;               // mirrors the P10/P11/P12/P13 bound
        public const int MaxMemoriesPerAgent = 8;      // mirrors CrewMemorySystem.MaxMemoriesPerAgent
        public const int MaxTextLen = 32;              // mirrors CrewMemorySystem.MaxTextLen
        public const int MaxEncodedLength = 64 * 1024; // hard bound on the encoded blob (sanity)

        private const uint Magic = 0x42504143u;        // "CAPB" little-endian

        // ---- capture: live registries -> snapshot -------------------------------

        public sealed class CrewDataSnapshot
        {
            public readonly List<CrewExperienceRecord> Experience =
                new List<CrewExperienceRecord>();
            public readonly List<CrewPersonalityRegistry.MaturedTraitRow> MaturedPersonalities =
                new List<CrewPersonalityRegistry.MaturedTraitRow>();
            public readonly List<CrewMemorySystem.MemoryRow> Memories =
                new List<CrewMemorySystem.MemoryRow>();
        }

        // Captures the current live state of the three registries (defensive
        // copies; deterministic row order from the registry export APIs).
        public static CrewDataSnapshot Capture()
        {
            CrewDataSnapshot snap = new CrewDataSnapshot();
            snap.Experience.AddRange(CrewExperienceRegistry.ExportAll());
            snap.MaturedPersonalities.AddRange(CrewPersonalityRegistry.ExportMatured());
            snap.Memories.AddRange(CrewMemorySystem.ExportAll());
            return snap;
        }

        // ---- encode / decode ----------------------------------------------------

        public static byte[] Encode(CrewDataSnapshot snap)
        {
            if (snap == null) return null;
            if (snap.Experience.Count > MaxAgents
                || snap.MaturedPersonalities.Count > MaxAgents
                || snap.Memories.Count > MaxAgents * MaxMemoriesPerAgent) return null;
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);

            // Header
            w.Write(Magic);                       // u32 "CAPB"
            w.Write((ushort)FormatVersion);       // u16 format version

            // Experience section
            w.Write((byte)snap.Experience.Count);
            for (int i = 0; i < snap.Experience.Count; i++)
            {
                CrewExperienceRecord r = snap.Experience[i];
                WriteString(w, r.AgentId);
                w.Write(r.ExperiencePoints);      // i64
                w.Write(r.TasksCompleted);        // i64
                w.Write(r.TasksCancelled);        // i64
                w.Write(r.TasksExpired);          // i64
                w.Write(r.TasksVanished);         // i64
                w.Write(r.TasksFailed);           // i64
                w.Write(r.TotalOutcomes);         // i64
                WriteString(w, r.LastOutcome);    // len-prefixed UTF-8, null-safe
                w.Write(r.CreatedTimeMs);         // i32
                w.Write(r.LastResultMs);          // i32
                w.Write(r.UpdateCount);           // i64
            }

            // Matured personalities section
            w.Write((byte)snap.MaturedPersonalities.Count);
            for (int i = 0; i < snap.MaturedPersonalities.Count; i++)
            {
                CrewPersonalityRegistry.MaturedTraitRow p = snap.MaturedPersonalities[i];
                WriteString(w, p.AgentId);
                w.Write(p.Discipline);            // i32 (0..100)
                w.Write(p.Boldness);
                w.Write(p.Sociability);
                w.Write(p.Diligence);
                w.Write(p.Adaptability);
                w.Write(p.DerivedTimeMs);         // i32
            }

            // Memory section
            w.Write((byte)snap.Memories.Count);
            for (int i = 0; i < snap.Memories.Count; i++)
            {
                CrewMemorySystem.MemoryRow m = snap.Memories[i];
                WriteString(w, m.AgentId);
                w.Write((byte)m.Kind);            // u8 vocabulary (1..3)
                w.Write(m.TaskId);                // i64
                WriteString(w, m.Text);           // null-safe
                WriteString(w, m.Outcome);        // null-safe
                w.Write(m.CreatedTimeMs);         // i32
                w.Write(m.LastSeenMs);            // i32
                w.Write(m.UpdateCount);           // i64
            }

            byte[] bytes = ms.ToArray();
            if (bytes.Length > MaxEncodedLength) return null;
            return bytes;
        }

        // Decodes a payload into a snapshot of ROWS ONLY — no live registry is
        // touched. Returns null on any structural problem (bad magic, version
        // above this code's FormatVersion, truncation, over-bound counts,
        // malformed strings). Never throws, never fabricates.
        public static CrewDataSnapshot Decode(byte[] data)
        {
            if (data == null || data.Length < 7 || data.Length > MaxEncodedLength) return null;
            try
            {
                MemoryStream ms = new MemoryStream(data);
                BinaryReader r = new BinaryReader(ms);

                if (r.ReadUInt32() != Magic) return null;
                ushort version = r.ReadUInt16();
                if (version > FormatVersion) return null;   // future formats: refuse (older ones: forward-compatible readers only)

                CrewDataSnapshot snap = new CrewDataSnapshot();

                int expCount = r.ReadByte();
                if (expCount > MaxAgents) return null;
                for (int i = 0; i < expCount; i++)
                {
                    string agentId = ReadString(r, PersonalityFactory.MaxAgentIdLength);
                    if (agentId == null) return null;
                    long xp = r.ReadInt64();
                    long done = r.ReadInt64();
                    long cancel = r.ReadInt64();
                    long expire = r.ReadInt64();
                    long vanish = r.ReadInt64();
                    long fail = r.ReadInt64();
                    long total = r.ReadInt64();
                    string lastOutcome = ReadString(r, 16);
                    int created = r.ReadInt32();
                    int lastResult = r.ReadInt32();
                    long updates = r.ReadInt64();
                    CrewExperienceRecord rec = new CrewExperienceRecord(agentId, created);
                    rec.ExperiencePoints = xp;
                    rec.TasksCompleted = done;
                    rec.TasksCancelled = cancel;
                    rec.TasksExpired = expire;
                    rec.TasksVanished = vanish;
                    rec.TasksFailed = fail;
                    rec.TotalOutcomes = total;
                    rec.LastOutcome = lastOutcome;
                    rec.LastResultMs = lastResult;
                    rec.UpdateCount = updates;
                    snap.Experience.Add(rec);
                }

                int persCount = r.ReadByte();
                if (persCount > MaxAgents) return null;
                for (int i = 0; i < persCount; i++)
                {
                    CrewPersonalityRegistry.MaturedTraitRow row = new CrewPersonalityRegistry.MaturedTraitRow();
                    row.AgentId = ReadString(r, PersonalityFactory.MaxAgentIdLength);
                    if (row.AgentId == null) return null;
                    row.Discipline = r.ReadInt32();
                    row.Boldness = r.ReadInt32();
                    row.Sociability = r.ReadInt32();
                    row.Diligence = r.ReadInt32();
                    row.Adaptability = r.ReadInt32();
                    row.DerivedTimeMs = r.ReadInt32();
                    snap.MaturedPersonalities.Add(row);
                }

                int memCount = r.ReadByte();
                if (memCount > MaxAgents * MaxMemoriesPerAgent) return null;
                for (int i = 0; i < memCount; i++)
                {
                    CrewMemorySystem.MemoryRow row = new CrewMemorySystem.MemoryRow();
                    row.AgentId = ReadString(r, PersonalityFactory.MaxAgentIdLength);
                    if (row.AgentId == null) return null;
                    byte kindByte = r.ReadByte();
                    if (kindByte < 1 || kindByte > 3) return null;
                    row.Kind = (MemoryKind)kindByte;
                    row.TaskId = r.ReadInt64();
                    row.Text = ReadString(r, MaxTextLen);
                    row.Outcome = ReadString(r, 16);   // outcome vocabulary tokens are ≤ 9 chars
                    row.CreatedTimeMs = r.ReadInt32();
                    row.LastSeenMs = r.ReadInt32();
                    row.UpdateCount = r.ReadInt64();
                    snap.Memories.Add(row);
                }

                return snap;
            }
            catch (Exception)
            {
                return null;   // truncation/IO fault => null, never a partial state
            }
        }

        // Restores a decoded snapshot into the live registries (insert-only).
        // Returns the number of rows inserted across all three registries.
        public static int Restore(CrewDataSnapshot snap)
        {
            if (snap == null) return 0;
            int inserted = 0;
            for (int i = 0; i < snap.Experience.Count; i++)
            {
                CrewExperienceRecord r = snap.Experience[i];
                if (CrewExperienceRegistry.RestoreRecord(r.AgentId, r.ExperiencePoints,
                    r.TasksCompleted, r.TasksCancelled, r.TasksExpired, r.TasksVanished,
                    r.TasksFailed, r.TotalOutcomes, r.LastOutcome, r.CreatedTimeMs,
                    r.LastResultMs, r.UpdateCount))
                {
                    inserted++;
                }
            }
            for (int i = 0; i < snap.MaturedPersonalities.Count; i++)
            {
                CrewPersonalityRegistry.MaturedTraitRow p = snap.MaturedPersonalities[i];
                if (CrewPersonalityRegistry.RestoreMatured(p.AgentId,
                    p.Discipline, p.Boldness, p.Sociability, p.Diligence, p.Adaptability,
                    p.DerivedTimeMs))
                {
                    inserted++;
                }
            }
            if (snap.Memories.Count > 0)
            {
                inserted += CrewMemorySystem.RestoreRows(snap.Memories);
            }
            return inserted;
        }

        // ---- string codec (len-prefixed UTF-8, null-safe, bounded) --------------

        private static void WriteString(BinaryWriter w, string s)
        {
            if (s == null) { w.Write((ushort)0xFFFF); return; }
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
            if (bytes.Length > 0xFFFF) bytes = System.Text.Encoding.UTF8.GetBytes(s.Substring(0, MaxTextLen));
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }

        // Returns null on an over-bound length or a truncated buffer (caller
        // treats null as structural failure). maxChars bounds the DECODED
        // length; the encoded length is bounded by the reader's own stream.
        private static string ReadString(BinaryReader r, int maxChars)
        {
            ushort len = r.ReadUInt16();
            if (len == 0xFFFF) return null;
            if (r.BaseStream.Position + len > r.BaseStream.Length) return null;
            byte[] bytes = r.ReadBytes(len);
            if (bytes.Length != len) return null;
            string s = System.Text.Encoding.UTF8.GetString(bytes);
            if (s.Length > maxChars) return null;
            return s;
        }
    }
}