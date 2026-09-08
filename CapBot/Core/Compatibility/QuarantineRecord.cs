using System;
using System.Collections.Generic;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 46: quarantine record model -------------------------------------
    // Pure serialization shapes for the quarantine audit trail. The production
    // executor (future phase) writes conflict.json per quarantined mod and
    // compatibility-state.json for boot-safety restore gating; THIS file holds
    // only the data contracts + bounded text rendering, so the shapes stay
    // unit-testable without any file system.
    //
    // Honesty rules encoded here: evidence lines are attached verbatim (never
    // paraphrased), confidence is carried through, and a record without the
    // reintroduction leg cannot claim CONFIRMED (the record factory refuses).
    public sealed class QuarantineRecord
    {
        internal readonly string ModName;
        internal readonly string AssemblyName;     // file name, e.g. "SomeMod.dll"
        internal readonly string ModVersion;
        internal readonly string HarmonyId;
        internal readonly string ClassAudit;       // A/B/C/D audit text
        internal readonly string Confidence;       // UNVERIFIED/PROBABLE/CONFIRMED
        internal readonly string Symptoms;         // bounded joined symptom summary
        internal readonly string Evidence;         // bounded joined evidence lines (verbatim)
        internal readonly string Decision;         // decision text (why quarantined)
        internal readonly string CapBotVersion;
        internal readonly string GameVersion;
        internal readonly bool Reversible;
        internal readonly long TimestampMs;

        internal QuarantineRecord(string modName, string assemblyName, string modVersion, string harmonyId,
            string conflictClass, string confidence, string symptoms, string evidence, string decision,
            string capBotVersion, string gameVersion, bool reversible, long timestampMs)
        {
            ModName = modName ?? "";
            AssemblyName = assemblyName ?? "";
            ModVersion = modVersion ?? "";
            HarmonyId = harmonyId ?? "";
            ClassAudit = conflictClass ?? "none";
            Confidence = confidence ?? "UNVERIFIED";
            Symptoms = symptoms ?? "";
            Evidence = evidence ?? "";
            Decision = decision ?? "";
            CapBotVersion = capBotVersion ?? "";
            GameVersion = gameVersion ?? "";
            Reversible = reversible;
            TimestampMs = timestampMs;
        }

        // JSON-ish rendering (hand-rolled, bounded — the mod has no JSON
        // dependency and must not gain one for an audit file).
        public string ToJson()
        {
            return "{\n" +
                   "  \"mod\": " + Q(ModName) + ",\n" +
                   "  \"assembly\": " + Q(AssemblyName) + ",\n" +
                   "  \"version\": " + Q(ModVersion) + ",\n" +
                   "  \"harmonyId\": " + Q(HarmonyId) + ",\n" +
                   "  \"conflictClass\": " + Q(ClassAudit) + ",\n" +
                   "  \"confidence\": " + Q(Confidence) + ",\n" +
                   "  \"symptoms\": " + Q(Symptoms) + ",\n" +
                   "  \"evidence\": " + Q(Evidence) + ",\n" +
                   "  \"decision\": " + Q(Decision) + ",\n" +
                   "  \"capbotVersion\": " + Q(CapBotVersion) + ",\n" +
                   "  \"gameVersion\": " + Q(GameVersion) + ",\n" +
                   "  \"reversible\": " + (Reversible ? "true" : "false") + ",\n" +
                   "  \"timestampMs\": " + TimestampMs + "\n" +
                   "}";
        }

        // Bounded flat audit line for the CompatibilityDecision channel.
        public string ToAuditLine()
        {
            return "CompatibilityQuarantineRecord mod=" + ModName +
                   " assembly=" + AssemblyName +
                   " class=" + ClassAudit +
                   " confidence=" + Confidence +
                   " reversible=" + (Reversible ? "yes" : "no");
        }

        // Factory guard: only a CONFIRMED Class-D decision may produce a
        // quarantine record (mirrors the engine's remediation ladder). The
        // parameter names must not shadow the instance fields.
        public static bool CanRecord(ConflictClass cls, ConflictConfidence conf)
        {
            return cls == ConflictClass.ClassD_ModConflict && conf == ConflictConfidence.Confirmed;
        }

        private static string Q(string s)
        {
            // Proper JSON escaping: control characters are escaped (evidence
            // stays verbatim), never flattened.
            return "\"" + (s ?? "")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "\"";
        }
    }

    // Compatibility-state rows: the boot-safety / restore-gating surface.
    // Persisted by the production layer; interpreted by the boot path.
    public sealed class CompatibilityStateRow
    {
        internal readonly string ModName;
        internal readonly string QuarantineState;      // engine QuarantineState text
        internal readonly int Reconfirmations;
        internal readonly long LastChangedMs;
        internal readonly string Reason;

        internal CompatibilityStateRow(string modName, string quarantineState, int reconfirmations, long lastChangedMs, string reason)
        {
            ModName = modName ?? "";
            QuarantineState = quarantineState ?? "";
            Reconfirmations = reconfirmations;
            LastChangedMs = lastChangedMs;
            Reason = reason ?? "";
        }

        public string ToJson()
        {
            return "  { \"mod\": " + Q(ModName) +
                   ", \"state\": " + Q(QuarantineState) +
                   ", \"reconfirmations\": " + Reconfirmations +
                   ", \"lastChangedMs\": " + LastChangedMs +
                   ", \"reason\": " + Q(Reason) + " }";
        }

        // Boot-gate rule: a mod left in Quarantined/QuarantineAgain state by a
        // previous session stays quarantined until a controlled restore runs —
        // boot NEVER auto-restores (DisabledUntilCompatibilityTest semantics).
        public static bool BootMustKeepQuarantined(string quarantineState)
        {
            return string.Equals(quarantineState, "Quarantined", StringComparison.Ordinal)
                || string.Equals(quarantineState, "QuarantineAgain", StringComparison.Ordinal);
        }

        private static string Q(string s)
        {
            // Proper JSON escaping: control characters are escaped, never flattened.
            return "\"" + (s ?? "")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n") + "\"";
        }
    }

    // Bounded in-memory audit trail (the production layer flushes it to
    // docs/audit files; the engine and records never touch IO).
    public static class CompatibilityAuditTrail
    {
        private const int MaxEntries = 256;

        private static readonly object m_Lock = new object();
        private static readonly List<string> m_Entries = new List<string>(MaxEntries);
        private static int m_Dropped;

        public static void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (m_Lock)
            {
                if (m_Entries.Count >= MaxEntries)
                {
                    m_Entries.RemoveAt(0);
                    m_Dropped++;
                }
                m_Entries.Add(line);
            }
        }

        // Bounded snapshot (oldest first).
        public static List<string> Snapshot()
        {
            lock (m_Lock) { return new List<string>(m_Entries); }
        }

        public static int DroppedCount { get { lock (m_Lock) { return m_Dropped; } } }
        public static int Count { get { lock (m_Lock) { return m_Entries.Count; } } }
        public static int MaxEntriesBound { get { return MaxEntries; } }

        public static void ResetForTests()
        {
            lock (m_Lock) { m_Entries.Clear(); m_Dropped = 0; }
        }
    }
}