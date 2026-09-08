using System;
using System.Collections.Generic;

namespace CapBot.Core.Compatibility
{
    // ---- Conflict engine: audited classification registry (Phase 46) -----------
    // Deterministic owner of the classification lifecycle: it stores bounded
    // per-mod evidence (static profile + latest symptom + latest A/B result),
    // classifies it through ConflictRules, audits every decision, and drives the
    // quarantine STATE MACHINE that the production executor (file moves,
    // conflict.json) obeys. The engine itself performs NO file IO and NO
    // Harmony/PML calls — physical quarantine is executed by the production
    // layer, which reports the outcome back through MarkQuarantined.
    //
    // Loop protection: a mod that fails its post-restore retest enters
    // QUARANTINE_AGAIN; each re-confirmation increments a bounded counter and
    // at ReconfirmationsForSafeMode the engine latches SAFE MODE (fail-safe:
    // the standing authorization requires it after severe repeated failures).
    //
    // Rate limiting: per-mod decision lines are emitted only when the decision
    // text changes or DecisionReemitMs elapsed — a stable verdict never spams.
    //
    // Pure C# domain (the P19 lesson): System* only, no PULSAR/PML/Harmony.
    // Evidence model types (SymptomReport/ComparisonResult/ConflictDecision)
    // live in ConflictModel.cs — same assembly, internal access.
    public static class ConflictEngine
    {
        private sealed class ModRecord
        {
            internal string Name = "";
            internal bool IsProtected;
            internal bool UsesHarmony;
            internal bool SharesDependency;
            internal bool TouchesPlayerOrBot;
            internal bool FilenameSimilarity;
            internal bool StaticSpeculationOnly;
            internal SymptomReport Symptom;          // latest wins (bounded store)
            internal ComparisonResult Comparison;    // latest A/B wins
            internal ConflictDecision LastDecision;  // last Evaluate() verdict
            internal long LastEmitMs = -1;           // rate-limit clock
            internal string LastEmitKey = "";        // decision-text dedup key
            internal QuarantineState Quarantine;     // state machine
            internal long QuarantinedAtMs = -1;
            internal int QuarantineAgainCount;       // loop-protection counter
            internal string QuarantineReason = "";   // last quarantine reason
        }

        public enum QuarantineState
        {
            None = 0,
            QuarantineRecommended = 1,  // engine verdict; executor should move the DLL
            Quarantined = 2,            // executor confirmed the physical move
            RestoredForRetest = 3,      // executor restored the DLL for a controlled retest
            CompatibleAfterRetest = 4,  // retest clean — mod reinstated as compatible
            QuarantineAgain = 5         // retest reproduced the conflict (loop protection)
        }

        private const int MaxTrackedMods = 32;
        private const int MaxStatusLines = 14;
        private const long DecisionReemitMs = 60000;         // stable verdict re-emit interval
        internal const int ReconfirmationsForSafeMode = 3;   // QUARANTINE_AGAIN events before safe mode

        private static readonly object m_Lock = new object();
        private static readonly Dictionary<string, ModRecord> m_Mods = new Dictionary<string, ModRecord>(MaxTrackedMods);
        private static Action<string> m_DecisionListener;
        private static Action<string, QuarantineState> m_StateListener;

        private static int m_RefusedCount;                   // over-bound / invalid calls
        private static int m_Evaluations;
        private static int m_DecisionEmits;
        private static int m_QuarantineAgainEvents;
        private static int m_DuplicateCapBotEvents;
        private static bool m_SafeMode;
        private static string m_SafeModeReason = "";
        private static long m_SafeModeAtMs = -1;

        // ---- seams ------------------------------------------------------------

        public static void SetDecisionListener(Action<string> listener)
        {
            lock (m_Lock) { m_DecisionListener = listener; }
        }

        // Quarantine-state change seam: fired on every quarantine-state
        // transition (production layer persists compatibility-state.json;
        // the engine itself performs no IO). modName, newState.
        public static void SetStateListener(Action<string, QuarantineState> listener)
        {
            lock (m_Lock) { m_StateListener = listener; }
        }

        // ---- evidence intake ----------------------------------------------------

        // Static profile from the mod inventory (production layer derives the
        // flags from live inventory + protected-list membership; the engine
        // never scans files itself).
        public static bool SetModProfile(string modName, bool isProtected, bool usesHarmony, bool sharesDependency,
            bool touchesPlayerOrBot, bool filenameSimilarity, bool staticSpeculationOnly)
        {
            if (string.IsNullOrEmpty(modName)) { CountRefused(); return false; }
            lock (m_Lock)
            {
                ModRecord r = GetOrCreateLocked(modName);
                if (r == null) return false;
                r.IsProtected = isProtected;
                r.UsesHarmony = usesHarmony;
                r.SharesDependency = sharesDependency;
                r.TouchesPlayerOrBot = touchesPlayerOrBot;
                r.FilenameSimilarity = filenameSimilarity;
                r.StaticSpeculationOnly = staticSpeculationOnly;
                return true;
            }
        }

        public static bool RecordSymptom(string modName, SymptomKind kind, string modOwner, string fingerprintKey,
            int count, long firstSeenMs, long lastSeenMs)
        {
            if (string.IsNullOrEmpty(modName)) { CountRefused(); return false; }
            if (kind == SymptomKind.None || count <= 0) { CountRefused(); return false; }
            lock (m_Lock)
            {
                ModRecord r = GetOrCreateLocked(modName);
                if (r == null) return false;
                r.Symptom = new SymptomReport(kind, modOwner, fingerprintKey, count, firstSeenMs, lastSeenMs);
                return true;
            }
        }

        public static bool RecordComparison(string modName, string label, bool presentWith, bool presentWithout,
            bool reintroductionReproduced, long recordedMs)
        {
            if (string.IsNullOrEmpty(modName)) { CountRefused(); return false; }
            if (string.IsNullOrEmpty(label)) { CountRefused(); return false; }
            lock (m_Lock)
            {
                ModRecord r = GetOrCreateLocked(modName);
                if (r == null) return false;
                r.Comparison = new ComparisonResult(label, presentWith, presentWithout, reintroductionReproduced, recordedMs);
                return true;
            }
        }

        // ---- evaluation -----------------------------------------------------------

        // Runs the deterministic rules over the stored evidence, audits the
        // verdict (rate-limited), and advances the quarantine state machine.
        public static ConflictDecision Evaluate(string modName, long nowMs)
        {
            if (string.IsNullOrEmpty(modName)) { CountRefused(); return null; }
            lock (m_Lock)
            {
                ModRecord r;
                if (!m_Mods.TryGetValue(modName, out r)) { CountRefused(); return null; }
                m_Evaluations++;

                ConflictDecision d = ClassifyLocked(r, nowMs);
                r.LastDecision = d;

                if (d.Action == Remediation.Quarantine && r.Quarantine == QuarantineState.None)
                {
                    r.Quarantine = QuarantineState.QuarantineRecommended;
                    r.QuarantineReason = d.Reason;
                }

                // Rate-limited audit: emit when the verdict text changes or the
                // re-emit interval elapsed. A stable verdict never spams.
                string line = d.ToAuditLine();
                bool changed = r.LastEmitKey != line;
                bool elapsed = r.LastEmitMs < 0 || (nowMs - r.LastEmitMs) >= DecisionReemitMs;
                if (changed || elapsed)
                {
                    EmitLocked(line);
                    r.LastEmitKey = line;
                    r.LastEmitMs = nowMs;
                }
                return d;
            }
        }

        private static ConflictDecision ClassifyLocked(ModRecord r, long nowMs)
        {
            // Mirror of ConflictRules.Classify on internal storage: the pure
            // rules stay authoritative for direct calls; this adapts the
            // engine's record shape onto the same ladder (single lock domain).
            if (r.IsProtected)
            {
                return new ConflictDecision(r.Name, ConflictClass.None, ConflictConfidence.Unverified, Remediation.KeepBoth,
                    RefusalReason.ProtectedMod, "protected game/runtime dependency", nowMs);
            }
            SymptomReport s = r.Symptom;
            if (s == null || s.Kind == SymptomKind.None || s.Count <= 0)
            {
                RefusalReason why;
                if (r.SharesDependency) why = RefusalReason.SharedDependencyOnly;
                else if (r.UsesHarmony) why = RefusalReason.UsesHarmonyOnly;
                else if (r.TouchesPlayerOrBot) why = RefusalReason.TouchesPlayerOrBotOnly;
                else if (r.FilenameSimilarity) why = RefusalReason.FilenameSimilarityOnly;
                else if (r.StaticSpeculationOnly) why = RefusalReason.StaticSpeculationOnly;
                else why = RefusalReason.None;
                return new ConflictDecision(r.Name, ConflictClass.None, ConflictConfidence.Unverified, Remediation.KeepBoth,
                    why, "no symptom attributed", nowMs);
            }
            ComparisonResult ab = r.Comparison;
            if (ab == null || string.IsNullOrEmpty(ab.Label))
            {
                return new ConflictDecision(r.Name, ConflictRules.ClassForSymptom(s.Kind), ConflictConfidence.Probable,
                    Remediation.Observe, RefusalReason.InsufficientEvidence, "symptom without controlled A/B", nowMs);
            }
            bool removalRemovesFailure = ab.SymptomPresentWithMod && !ab.SymptomPresentWithoutMod;
            if (!removalRemovesFailure)
            {
                return new ConflictDecision(r.Name, ConflictRules.ClassForSymptom(s.Kind), ConflictConfidence.Unverified,
                    Remediation.KeepBoth, RefusalReason.None, "A/B did not implicate mod", nowMs);
            }
            ConflictConfidence conf = ab.ReintroductionReproduced ? ConflictConfidence.Confirmed : ConflictConfidence.Probable;
            ConflictClass cls = ConflictRules.ClassForSymptom(s.Kind);
            Remediation action;
            switch (cls)
            {
                case ConflictClass.ClassD_ModConflict:
                    action = conf == ConflictConfidence.Confirmed ? Remediation.Quarantine : Remediation.Observe;
                    break;
                case ConflictClass.ClassC_FeatureConflict: action = Remediation.DisableCapBotFeature; break;
                case ConflictClass.ClassB_ManageableOverlap: action = Remediation.CompatibilityFix; break;
                default: action = Remediation.KeepBoth; break;
            }
            string reason = "A/B removal-removes-failure" + (ab.ReintroductionReproduced ? " + reintroduction-reproduces" : " (reintroduction not tested)");
            return new ConflictDecision(r.Name, cls, conf, action, RefusalReason.None, reason, nowMs);
        }

        // ---- quarantine state machine (executor callbacks) --------------------------

        // Production executor confirms the physical quarantine (files moved,
        // conflict.json written). Idempotent: re-confirmations are no-ops.
        public static bool MarkQuarantined(string modName, long nowMs)
        {
            if (string.IsNullOrEmpty(modName)) { CountRefused(); return false; }
            lock (m_Lock)
            {
                ModRecord r;
                if (!m_Mods.TryGetValue(modName, out r)) { CountRefused(); return false; }
                if (r.Quarantine == QuarantineState.Quarantined) return true;   // idempotent
                if (r.Quarantine != QuarantineState.QuarantineRecommended &&
                    r.Quarantine != QuarantineState.QuarantineAgain) { CountRefused(); return false; }
                r.Quarantine = QuarantineState.Quarantined;
                r.QuarantinedAtMs = nowMs;
                EmitLocked("CompatibilityQuarantined mod=" + r.Name + " reason=" + r.QuarantineReason);
                FireStateLocked(r.Name, r.Quarantine);
                return true;
            }
        }

        // Executor restored the mod for a controlled retest (one variable).
        public static bool MarkRestoredForRetest(string modName, long nowMs)
        {
            if (string.IsNullOrEmpty(modName)) { CountRefused(); return false; }
            lock (m_Lock)
            {
                ModRecord r;
                if (!m_Mods.TryGetValue(modName, out r)) { CountRefused(); return false; }
                if (r.Quarantine != QuarantineState.Quarantined) { CountRefused(); return false; }
                r.Quarantine = QuarantineState.RestoredForRetest;
                EmitLocked("CompatibilityRetestRestored mod=" + r.Name);
                FireStateLocked(r.Name, r.Quarantine);
                return true;
            }
        }

        // Retest outcome: clean → COMPATIBLE_AFTER_RETEST; conflict reproduced →
        // QUARANTINE_AGAIN (+ loop-protection counter, safe-mode latch).
        public static bool MarkRetestResult(string modName, bool stillConflicting, long nowMs)
        {
            if (string.IsNullOrEmpty(modName)) { CountRefused(); return false; }
            lock (m_Lock)
            {
                ModRecord r;
                if (!m_Mods.TryGetValue(modName, out r)) { CountRefused(); return false; }
                if (r.Quarantine != QuarantineState.RestoredForRetest) { CountRefused(); return false; }
                if (!stillConflicting)
                {
                    r.Quarantine = QuarantineState.CompatibleAfterRetest;
                    EmitLocked("CompatibilityDecision mod=" + r.Name + " action=COMPATIBLE_AFTER_RETEST reason=retest clean");
                    FireStateLocked(r.Name, r.Quarantine);
                    return true;
                }
                r.Quarantine = QuarantineState.QuarantineAgain;
                r.QuarantineAgainCount++;
                m_QuarantineAgainEvents++;
                EmitLocked("CompatibilityDecision mod=" + r.Name + " action=QUARANTINE_AGAIN reconfirms=" + r.QuarantineAgainCount);
                FireStateLocked(r.Name, r.Quarantine);
                if (m_QuarantineAgainEvents >= ReconfirmationsForSafeMode && !m_SafeMode)
                {
                    EnableSafeModeLocked("repeated re-confirmed conflicts", nowMs);
                }
                return true;
            }
        }

        // ---- duplicate CapBot execution (STOP condition) -----------------------------

        // Production inventory detects more than one active CapBot assembly.
        // Audits and latches; the caller must stop duplicate execution safely.
        public static bool ReportDuplicateCapBot(int activeAssemblies, long nowMs)
        {
            lock (m_Lock)
            {
                m_DuplicateCapBotEvents++;
                EmitLocked("CompatibilityDecision mod=CapBot action=STOP_DUPLICATE_EXECUTION assemblies=" + activeAssemblies + " (duplicate CapBot plugins must never run concurrently)");
                return true;
            }
        }

        // ---- safe mode -----------------------------------------------------------------

        public static bool EnableSafeMode(string reason, long nowMs)
        {
            if (string.IsNullOrEmpty(reason)) { CountRefused(); return false; }
            lock (m_Lock)
            {
                if (m_SafeMode) return true;   // idempotent latch
                EnableSafeModeLocked(reason, nowMs);
                return true;
            }
        }

        private static void EnableSafeModeLocked(string reason, long nowMs)
        {
            m_SafeMode = true;
            m_SafeModeReason = reason;
            m_SafeModeAtMs = nowMs;
            EmitLocked("CompatibilitySafeMode enabled reason=" + reason);
        }

        public static bool SafeMode { get { lock (m_Lock) { return m_SafeMode; } } }
        public static string SafeModeReason { get { lock (m_Lock) { return m_SafeModeReason; } } }
        public static long SafeModeAtMs { get { lock (m_Lock) { return m_SafeModeAtMs; } } }

        // ---- readbacks (/capbotcompat + /capbotstatus) ----------------------------------

        // Status vocabulary: Loaded / Compatible / Conflict / AutoDisabled / Quarantined (+ reason).
        public static string CompatStatus(string modName)
        {
            if (string.IsNullOrEmpty(modName)) return "Unknown";
            lock (m_Lock)
            {
                ModRecord r;
                if (!m_Mods.TryGetValue(modName, out r)) return "Loaded";
                switch (r.Quarantine)
                {
                    case QuarantineState.Quarantined:
                    case QuarantineState.QuarantineAgain:
                        return "Quarantined reason=" + r.QuarantineReason;
                    case QuarantineState.QuarantineRecommended:
                        return "Conflict reason=" + r.QuarantineReason;
                    case QuarantineState.RestoredForRetest:
                        return "Conflict reason=retest in progress";
                    case QuarantineState.CompatibleAfterRetest:
                        return "Compatible reason=retest clean";
                    default:
                        ConflictDecision d = r.LastDecision;
                        if (d == null) return "Loaded";
                        if (d.Class == ConflictClass.None) return "Compatible reason=no conflict";
                        return "Conflict class=" + ConflictRules.ClassText(d.Class) + " confidence=" + ConflictRules.ConfidenceText(d.Confidence);
                }
            }
        }

        public static ConflictDecision LastDecision(string modName)
        {
            if (string.IsNullOrEmpty(modName)) return null;
            lock (m_Lock)
            {
                ModRecord r;
                if (!m_Mods.TryGetValue(modName, out r)) return null;
                return r.LastDecision;
            }
        }

        public static int TrackedModCount { get { lock (m_Lock) { return m_Mods.Count; } } }
        public static int RefusedCount { get { lock (m_Lock) { return m_RefusedCount; } } }
        public static int EvaluationCount { get { lock (m_Lock) { return m_Evaluations; } } }
        public static int DecisionEmitCount { get { lock (m_Lock) { return m_DecisionEmits; } } }
        public static int QuarantineAgainEvents { get { lock (m_Lock) { return m_QuarantineAgainEvents; } } }
        public static int DuplicateCapBotEvents { get { lock (m_Lock) { return m_DuplicateCapBotEvents; } } }
        public static int MaxTrackedModsBound { get { return MaxTrackedMods; } }
        public static int MaxStatusLinesBound { get { return MaxStatusLines; } }

        // Bounded status surface (exactly ≤ MaxStatusLines): summary + per-mod
        // lines; the last slot is reserved for a truncation marker only when
        // mods would otherwise overflow.
        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                lines.Add("ConflictEngine: mods=" + m_Mods.Count +
                          " evaluations=" + m_Evaluations +
                          " quarantineAgain=" + m_QuarantineAgainEvents +
                          " dupCapBot=" + m_DuplicateCapBotEvents +
                          " safeMode=" + (m_SafeMode ? "yes" : "no"));
                int remaining = m_Mods.Count;
                foreach (KeyValuePair<string, ModRecord> kv in m_Mods)
                {
                    remaining--;
                    if (lines.Count >= MaxStatusLines - 1 && remaining > 0) { lines.Add("ConflictEngine: …truncated"); break; }
                    ModRecord r = kv.Value;
                    string line = "ConflictEngine: " + r.Name +
                                  " quarantine=" + r.Quarantine +
                                  " reconfirms=" + r.QuarantineAgainCount;
                    if (r.LastDecision != null)
                    {
                        line += " class=" + ConflictRules.ClassText(r.LastDecision.Class) +
                                " action=" + ConflictRules.ActionText(r.LastDecision.Action);
                    }
                    lines.Add(line);
                }
            }
            return lines;
        }

        // ---- tests ------------------------------------------------------------------------

        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Mods.Clear();
                m_DecisionListener = null;
                m_StateListener = null;
                m_RefusedCount = 0;
                m_Evaluations = 0;
                m_DecisionEmits = 0;
                m_QuarantineAgainEvents = 0;
                m_DuplicateCapBotEvents = 0;
                m_SafeMode = false;
                m_SafeModeReason = "";
                m_SafeModeAtMs = -1;
            }
        }

        // ---- internals ----------------------------------------------------------------------

        private static ModRecord GetOrCreateLocked(string modName)
        {
            ModRecord r;
            if (m_Mods.TryGetValue(modName, out r)) return r;
            if (m_Mods.Count >= MaxTrackedMods) { m_RefusedCount++; return null; }
            r = new ModRecord();
            r.Name = modName;
            m_Mods[modName] = r;
            return r;
        }

        private static void CountRefused()
        {
            lock (m_Lock) { m_RefusedCount++; }
        }

        private static void FireStateLocked(string modName, QuarantineState newState)
        {
            Action<string, QuarantineState> listener = m_StateListener;
            if (listener == null) return;
            try { listener(modName, newState); }
            catch (Exception) { /* state listeners are fail-safe by contract */ }
        }

        private static void EmitLocked(string line)
        {
            m_DecisionEmits++;
            Action<string> listener = m_DecisionListener;
            if (listener == null) return;
            try { listener(line); }
            catch (Exception) { /* decision listeners are fail-safe by contract */ }
        }
    }
}