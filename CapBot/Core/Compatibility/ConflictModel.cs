using System;
using System.Collections.Generic;

namespace CapBot.Core.Compatibility
{
    // ---- Conflict engine: deterministic classification model (Phase 46) --------
    // Pure-C# domain for the standing mod-conflict directive. NO file IO, NO
    // game/PML/Harmony references: the engine CLASSIFIES evidence and returns
    // decisions; the production layer (Mod.cs / Patch.cs wiring) executes any
    // physical quarantine. The deterministic engine decides — an LLM may only
    // recommend. Fail-closed: missing evidence classifies UNVERIFIED, and only
    // CONFIRMED Class-D evidence can ever produce a quarantine decision.
    //
    // The "never remove for" rule is structural: RefusalReason enumerates the
    // non-conflicts (shared dependency, Harmony usage, touching PLPlayer/PLBot,
    // filename similarity, static speculation) and Classify refuses to emit any
    // conflict class while the only evidence is one of those.
    public enum ConflictClass
    {
        None = 0,
        ClassA_SafeOverlap = 1,        // harmless shared surface — KEEP BOTH
        ClassB_ManageableOverlap = 2,  // KEEP + COMPATIBILITY (priority/guard/shim/gating)
        ClassC_FeatureConflict = 3,    // verified, scope-limited — disable the CapBot feature, keep the mod
        ClassD_ModConflict = 4         // verified systemic conflict — quarantine eligible (CONFIRMED only)
    }

    public enum ConflictConfidence
    {
        Unverified = 0,   // symptom seen, no controlled comparison
        Probable = 1,     // repeated symptom + owner isolation, no A/B yet
        Confirmed = 2     // A/B: removal removes failure AND reintroduction reproduces
    }

    public enum SymptomKind
    {
        None = 0,
        ExceptionStorm = 1,
        FalseBotDeath = 2,
        LifecycleFlicker = 3,
        DuplicateBotsOrAgents = 4,
        MemoryLoss = 5,
        CommandLoop = 6,
        NavLoop = 7,
        ExecutorRetryStorm = 8,
        TaskDuplication = 9,
        AuthorityViolation = 10,
        RpcStorm = 11,
        DataCorruption = 12
    }

    // Non-conflict reasons — evidence of these kinds can NEVER escalate a mod
    // toward quarantine by itself (the "never remove for" list, enforced).
    public enum RefusalReason
    {
        None = 0,
        SharedDependencyOnly = 1,
        UsesHarmonyOnly = 2,
        TouchesPlayerOrBotOnly = 3,
        FilenameSimilarityOnly = 4,
        StaticSpeculationOnly = 5,
        ProtectedMod = 6,
        InsufficientEvidence = 7,
        SafeIsolationAvailable = 8
    }

    public enum Remediation
    {
        None = 0,
        KeepBoth = 1,             // Class A
        CompatibilityFix = 2,     // Class B
        DisableCapBotFeature = 3, // Class C
        Quarantine = 4,           // Class D + Confirmed ONLY
        Observe = 5               // Unverified/Probable — collect more evidence, change nothing
    }

    // One observed symptom report (bounded, immutable).
    public sealed class SymptomReport
    {
        internal readonly SymptomKind Kind;
        internal readonly string ModOwner;        // harmony owner / mod name attribution (may be "")
        internal readonly string FingerprintKey;  // e.g. exception fingerprint: type|method|assembly
        internal readonly int Count;              // occurrences in the reporting window
        internal readonly long FirstSeenMs;
        internal readonly long LastSeenMs;

        internal SymptomReport(SymptomKind kind, string modOwner, string fingerprintKey, int count, long firstSeenMs, long lastSeenMs)
        {
            Kind = kind;
            ModOwner = modOwner ?? "";
            FingerprintKey = fingerprintKey ?? "";
            Count = count;
            FirstSeenMs = firstSeenMs;
            LastSeenMs = lastSeenMs;
        }
    }

    // A/B comparison result for one controlled experiment.
    public sealed class ComparisonResult
    {
        internal readonly string Label;           // experiment name
        internal readonly bool SymptomPresentWithMod;
        internal readonly bool SymptomPresentWithoutMod;
        internal readonly bool ReintroductionReproduced;   // false until a reintroduction test ran
        internal readonly long RecordedMs;

        internal ComparisonResult(string label, bool presentWith, bool presentWithout, bool reintroductionReproduced, long recordedMs)
        {
            Label = label ?? "";
            SymptomPresentWithMod = presentWith;
            SymptomPresentWithoutMod = presentWithout;
            ReintroductionReproduced = reintroductionReproduced;
            RecordedMs = recordedMs;
        }
    }

    // The engine's decision record (audited; payload consumed by status + the
    // production quarantine executor).
    public sealed class ConflictDecision
    {
        internal readonly string ModName;
        internal readonly ConflictClass Class;
        internal readonly ConflictConfidence Confidence;
        internal readonly Remediation Action;
        internal readonly RefusalReason Refusal;      // None when a class was assigned
        internal readonly string Reason;              // bounded human-readable line
        internal readonly long DecidedMs;

        internal ConflictDecision(string modName, ConflictClass cls, ConflictConfidence conf, Remediation action, RefusalReason refusal, string reason, long decidedMs)
        {
            ModName = modName ?? "";
            Class = cls;
            Confidence = conf;
            Action = action;
            Refusal = refusal;
            Reason = reason ?? "";
            DecidedMs = decidedMs;
        }

        // One-line audit format: CompatibilityDecision mod= class= confidence= action= [refusal=] reason=
        public string ToAuditLine()
        {
            string baseLine = "CompatibilityDecision mod=" + ModName +
                              " class=" + ConflictRules.ClassText(Class) +
                              " confidence=" + ConflictRules.ConfidenceText(Confidence) +
                              " action=" + ConflictRules.ActionText(Action);
            if (Refusal != RefusalReason.None) baseLine += " refusal=" + ConflictRules.RefusalText(Refusal);
            if (Reason.Length > 0) baseLine += " reason=" + Reason;
            return baseLine;
        }
    }

    // Deterministic classification rules (pure functions; the engine registry
    // feeds them evidence and audits their verdicts). Also the shared audit
    // vocabulary: enum → status/audit-line text for every domain type.
    public static class ConflictRules
    {
        internal static string ClassText(ConflictClass c)
        {
            switch (c)
            {
                case ConflictClass.ClassA_SafeOverlap: return "A";
                case ConflictClass.ClassB_ManageableOverlap: return "B";
                case ConflictClass.ClassC_FeatureConflict: return "C";
                case ConflictClass.ClassD_ModConflict: return "D";
                default: return "none";
            }
        }

        internal static string ConfidenceText(ConflictConfidence c)
        {
            switch (c)
            {
                case ConflictConfidence.Probable: return "PROBABLE";
                case ConflictConfidence.Confirmed: return "CONFIRMED";
                default: return "UNVERIFIED";
            }
        }

        internal static string ActionText(Remediation r)
        {
            switch (r)
            {
                case Remediation.KeepBoth: return "KEEP_BOTH";
                case Remediation.CompatibilityFix: return "COMPATIBILITY_FIX";
                case Remediation.DisableCapBotFeature: return "DISABLE_FEATURE";
                case Remediation.Quarantine: return "QUARANTINE";
                case Remediation.Observe: return "OBSERVE";
                default: return "none";
            }
        }

        internal static string RefusalText(RefusalReason r)
        {
            switch (r)
            {
                case RefusalReason.SharedDependencyOnly: return "SHARED_DEPENDENCY";
                case RefusalReason.UsesHarmonyOnly: return "USES_HARMONY";
                case RefusalReason.TouchesPlayerOrBotOnly: return "TOUCHES_PLPLAYER_PLBOT";
                case RefusalReason.FilenameSimilarityOnly: return "FILENAME_SIMILARITY";
                case RefusalReason.StaticSpeculationOnly: return "STATIC_SPECULATION";
                case RefusalReason.ProtectedMod: return "PROTECTED_MOD";
                case RefusalReason.InsufficientEvidence: return "INSUFFICIENT_EVIDENCE";
                case RefusalReason.SafeIsolationAvailable: return "SAFE_ISOLATION_AVAILABLE";
                default: return "none";
            }
        }
        // Classification ladder. Precedence: protected mod refusal > non-conflict
        // evidence refusal > confirmed causality ladder > symptom-only observe.
        public static ConflictDecision Classify(
            string modName,
            bool isProtectedMod,
            bool usesHarmony,
            bool sharesDependency,
            bool touchesPlayerOrBot,
            bool filenameSimilarity,
            bool staticSpeculationOnly,
            SymptomReport symptom,
            ComparisonResult abTest,
            long nowMs)
        {
            if (string.IsNullOrEmpty(modName))
            {
                return new ConflictDecision(modName, ConflictClass.None, ConflictConfidence.Unverified, Remediation.None,
                    RefusalReason.InsufficientEvidence, "no mod name", nowMs);
            }

            // Protected mods are structurally unquarantinable — refuse BEFORE any
            // symptom consideration (game/runtime dependency list).
            if (isProtectedMod)
            {
                return new ConflictDecision(modName, ConflictClass.None, ConflictConfidence.Unverified, Remediation.KeepBoth,
                    RefusalReason.ProtectedMod, "protected game/runtime dependency", nowMs);
            }

            // No symptom at all: Harmony usage / shared deps / touching PLPlayer /
            // filename similarity are NOT conflicts (never remove for these).
            if (symptom == null || symptom.Kind == SymptomKind.None || symptom.Count <= 0)
            {
                RefusalReason why;
                if (sharesDependency) why = RefusalReason.SharedDependencyOnly;
                else if (usesHarmony) why = RefusalReason.UsesHarmonyOnly;
                else if (touchesPlayerOrBot) why = RefusalReason.TouchesPlayerOrBotOnly;
                else if (filenameSimilarity) why = RefusalReason.FilenameSimilarityOnly;
                else if (staticSpeculationOnly) why = RefusalReason.StaticSpeculationOnly;
                else why = RefusalReason.None;
                return new ConflictDecision(modName, ConflictClass.None, ConflictConfidence.Unverified, Remediation.KeepBoth,
                    why, "no symptom attributed", nowMs);
            }

            // Symptom exists. Without a controlled A/B it can never exceed PROBABLE,
            // and only OBSERVE flows out of the engine (fix-first rule: an Unverified
            // symptom never disables anything).
            if (abTest == null || string.IsNullOrEmpty(abTest.Label))
            {
                return new ConflictDecision(modName, ClassForSymptom(symptom.Kind), ConflictConfidence.Probable, Remediation.Observe,
                    RefusalReason.InsufficientEvidence, "symptom without controlled A/B", nowMs);
            }

            // A/B present. Causality requires: symptom with mod, absent without.
            bool removalRemovesFailure = abTest.SymptomPresentWithMod && !abTest.SymptomPresentWithoutMod;
            if (!removalRemovesFailure)
            {
                // Controlled comparison did not implicate the mod.
                return new ConflictDecision(modName, ClassForSymptom(symptom.Kind), ConflictConfidence.Unverified, Remediation.KeepBoth,
                    RefusalReason.None, "A/B did not implicate mod", nowMs);
            }

            // Removal removes the failure. Confidence: Confirmed only when the
            // reintroduction leg also reproduced it (full causality chain).
            ConflictConfidence conf = abTest.ReintroductionReproduced
                ? ConflictConfidence.Confirmed
                : ConflictConfidence.Probable;

            ConflictClass cls = ClassForSymptom(symptom.Kind);

            // Quarantine is reachable ONLY at Class D + Confirmed. Everything else
            // takes the fix-first remediation ladder.
            Remediation action;
            switch (cls)
            {
                case ConflictClass.ClassD_ModConflict:
                    action = conf == ConflictConfidence.Confirmed
                        ? Remediation.Quarantine
                        : Remediation.Observe;   // Class D without full causality: keep observing, change nothing
                    break;
                case ConflictClass.ClassC_FeatureConflict:
                    action = Remediation.DisableCapBotFeature;  // disable the feature, keep the mod
                    break;
                case ConflictClass.ClassB_ManageableOverlap:
                    action = Remediation.CompatibilityFix;
                    break;
                default:
                    action = Remediation.KeepBoth;              // Class A / None: harmless overlap
                    break;
            }

            string reason = "A/B removal-removes-failure" + (abTest.ReintroductionReproduced ? " + reintroduction-reproduces" : " (reintroduction not tested)");
            return new ConflictDecision(modName, cls, conf, action, RefusalReason.None, reason, nowMs);
        }

        // Symptom -> class mapping (deterministic).
        public static ConflictClass ClassForSymptom(SymptomKind kind)
        {
            switch (kind)
            {
                case SymptomKind.ExceptionStorm:
                case SymptomKind.FalseBotDeath:
                case SymptomKind.LifecycleFlicker:
                case SymptomKind.DuplicateBotsOrAgents:
                case SymptomKind.MemoryLoss:
                case SymptomKind.DataCorruption:
                    return ConflictClass.ClassD_ModConflict;   // systemic: lifecycle/data integrity
                case SymptomKind.CommandLoop:
                case SymptomKind.NavLoop:
                case SymptomKind.ExecutorRetryStorm:
                case SymptomKind.TaskDuplication:
                case SymptomKind.AuthorityViolation:
                case SymptomKind.RpcStorm:
                    return ConflictClass.ClassC_FeatureConflict; // scope-limited behavioral loops
                default:
                    return ConflictClass.None;
            }
        }
    }
}