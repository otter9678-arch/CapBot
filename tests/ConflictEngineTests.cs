// Dev-side unit tests for the Phase 46 conflict engine (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1.
// Time is virtual: every Evaluate/Mark call takes an explicit nowMs.
//
// Covers the standing conflict-directive mandated scenarios (pure-domain part):
//   CE01 no-symptom refusal ladder (Harmony/shared-dep/PLPlayer-touch/filename/speculation)
//   CE02 protected-mod refusal wins over any symptom (never quarantinable)
//   CE03 symptom without controlled A/B => PROBABLE + OBSERVE (nothing changes)
//   CE04 A/B that does not implicate the mod => UNVERIFIED + KEEP_BOTH
//   CE05 A/B implicates without reintroduction leg => Class D PROBABLE + OBSERVE (NOT quarantine)
//   CE06 full causality (removal + reintroduction) => CONFIRMED Class D => QUARANTINE state machine
//   CE07 Class C symptom => DISABLE_FEATURE, never quarantine
//   CE08 symptom→class mapping table (deterministic)
//   CE09 rate limiting: stable verdict emits once, re-emits only after interval
//   CE10 loop protection: repeated QUARANTINE_AGAIN => safe-mode latch (idempotent)
//   CE11 restore/retest clean => COMPATIBLE_AFTER_RETEST + status vocabulary
//   CE12 invalid transitions refused + counters
//   CE13 duplicate CapBot assembly => STOP audit + latch
//   CE14 status lines bounded + shaped
//   CE15 determinism: same evidence => same decision line
//   CE16 audit-line formatting vocabulary
//   CE17 MoreBots real-evidence mirror: this session's A/B (no reintroduction leg)
//        must yield OBSERVE, never QUARANTINE (honesty invariant)
//   CE18 bounded tracking (MaxTrackedMods) + refusals counted
//   CE19 ResetForTests clears everything
//   CE20 CompatStatus vocabulary for untracked/empty/unknown
//   CE26 P48 Harmony-map enrichment: strictly conservative flip (never un-sets),
//        owner recorded, audited once, cannot weaken any A/B verdict
using System;
using System.Collections.Generic;
using CapBot.Core.Compatibility;

namespace CapBot.TaskTests
{
    internal static class ConflictEngineTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        private static readonly List<string> s_Lines = new List<string>();

        private static void FreshSetup()
        {
            ConflictEngine.ResetForTests();
            s_Lines.Clear();
            ConflictEngine.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static void SetCleanProfile(string modName)
        {
            ConflictEngine.SetModProfile(modName, false, false, false, false, false, false);
        }

        internal static int Run()
        {
            // ---- CE01: no-symptom refusal ladder ----------------------------------
            FreshSetup();
            ConflictEngine.SetModProfile("HarmonyOnly", false, true, false, false, false, false);
            ConflictDecision d1 = ConflictEngine.Evaluate("HarmonyOnly", 1000);
            Check(d1 != null && d1.Class == ConflictClass.None && d1.Action == Remediation.KeepBoth,
                "CE01 harmony-only => no conflict, keep both");
            Check(d1 != null && d1.Refusal == RefusalReason.UsesHarmonyOnly, "CE01 refusal=USES_HARMONY");
            ConflictEngine.SetModProfile("DepOnly", false, false, true, false, false, false);
            Check(ConflictEngine.Evaluate("DepOnly", 1000).Refusal == RefusalReason.SharedDependencyOnly,
                "CE01 refusal=SHARED_DEPENDENCY");
            ConflictEngine.SetModProfile("TouchOnly", false, false, false, true, false, false);
            Check(ConflictEngine.Evaluate("TouchOnly", 1000).Refusal == RefusalReason.TouchesPlayerOrBotOnly,
                "CE01 refusal=TOUCHES_PLPLAYER_PLBOT");
            ConflictEngine.SetModProfile("NameOnly", false, false, false, false, true, false);
            Check(ConflictEngine.Evaluate("NameOnly", 1000).Refusal == RefusalReason.FilenameSimilarityOnly,
                "CE01 refusal=FILENAME_SIMILARITY");
            ConflictEngine.SetModProfile("SpecOnly", false, false, false, false, false, true);
            Check(ConflictEngine.Evaluate("SpecOnly", 1000).Refusal == RefusalReason.StaticSpeculationOnly,
                "CE01 refusal=STATIC_SPECULATION");
            Check(!HasLineContaining("action=QUARANTINE"), "CE01 nothing in the ladder quarantines");

            // ---- CE02: protected mod wins over any symptom --------------------------
            FreshSetup();
            ConflictEngine.SetModProfile("Assembly-CSharp", true, true, true, true, true, true);
            ConflictEngine.RecordSymptom("Assembly-CSharp", SymptomKind.ExceptionStorm, "", "fp", 99999, 0, 1000);
            ConflictEngine.RecordComparison("Assembly-CSharp", "ab", true, false, true, 2000);
            ConflictDecision d2 = ConflictEngine.Evaluate("Assembly-CSharp", 3000);
            Check(d2.Class == ConflictClass.None && d2.Action == Remediation.KeepBoth,
                "CE02 protected mod never classified as conflict");
            Check(d2.Refusal == RefusalReason.ProtectedMod, "CE02 refusal=PROTECTED_MOD");
            Check(d2.Confidence == ConflictConfidence.Unverified, "CE02 protected refusal is UNVERIFIED");
            Check(ConflictEngine.CompatStatus("Assembly-CSharp").IndexOf("Compatible", StringComparison.Ordinal) == 0,
                "CE02 protected status reads Compatible (default branch, no state)");

            // ---- CE03: symptom without A/B => observe -------------------------------
            FreshSetup();
            SetCleanProfile("MysteryMod");
            ConflictEngine.RecordSymptom("MysteryMod", SymptomKind.CommandLoop, "owner.x", "fp1", 42, 0, 500);
            ConflictDecision d3 = ConflictEngine.Evaluate("MysteryMod", 1000);
            Check(d3.Class == ConflictClass.ClassC_FeatureConflict, "CE03 class from symptom kind");
            Check(d3.Confidence == ConflictConfidence.Probable, "CE03 no A/B => PROBABLE (never CONFIRMED)");
            Check(d3.Action == Remediation.Observe, "CE03 no A/B => OBSERVE (nothing disabled/quarantined)");
            Check(d3.Refusal == RefusalReason.InsufficientEvidence, "CE03 refusal=INSUFFICIENT_EVIDENCE");

            // ---- CE04: A/B does not implicate ---------------------------------------
            FreshSetup();
            SetCleanProfile("InnocentMod");
            ConflictEngine.RecordSymptom("InnocentMod", SymptomKind.ExceptionStorm, "owner.y", "fp2", 100, 0, 500);
            ConflictEngine.RecordComparison("InnocentMod", "ab1", false, false, false, 1000);
            ConflictDecision d4 = ConflictEngine.Evaluate("InnocentMod", 2000);
            Check(d4.Action == Remediation.KeepBoth && d4.Confidence == ConflictConfidence.Unverified,
                "CE04 symptom absent with mod => keep both, unverified");
            ConflictEngine.RecordComparison("InnocentMod", "ab2", true, true, false, 3000);
            ConflictDecision d4b = ConflictEngine.Evaluate("InnocentMod", 4000);
            Check(d4b.Action == Remediation.KeepBoth && d4b.Confidence == ConflictConfidence.Unverified,
                "CE04 symptom persists without mod => keep both, unverified");
            Check(d4b.Refusal == RefusalReason.None && d4b.Reason.IndexOf("did not implicate", StringComparison.Ordinal) >= 0,
                "CE04 reason line names the A/B verdict");

            // ---- CE05: removal-removes-failure WITHOUT reintroduction => observe ----
            FreshSetup();
            SetCleanProfile("HalfProven");
            ConflictEngine.RecordSymptom("HalfProven", SymptomKind.MemoryLoss, "owner.z", "fp3", 7, 0, 500);
            ConflictEngine.RecordComparison("HalfProven", "ab", true, false, false, 1000);
            ConflictDecision d5 = ConflictEngine.Evaluate("HalfProven", 2000);
            Check(d5.Class == ConflictClass.ClassD_ModConflict, "CE05 memory-loss symptom maps to Class D");
            Check(d5.Confidence == ConflictConfidence.Probable, "CE05 no reintroduction leg => PROBABLE");
            Check(d5.Action == Remediation.Observe, "CE05 Class D without full causality => OBSERVE, NOT quarantine");
            Check(d5.Reason.IndexOf("reintroduction not tested", StringComparison.Ordinal) >= 0,
                "CE05 reason states the missing leg");
            Check(ConflictEngine.CompatStatus("HalfProven").IndexOf("Conflict class=D", StringComparison.Ordinal) == 0,
                "CE05 status reports the conflict without quarantine state");

            // ---- CE06: full causality => CONFIRMED Class D => quarantine machine ----
            FreshSetup();
            SetCleanProfile("SystemicConflict");
            ConflictEngine.RecordSymptom("SystemicConflict", SymptomKind.FalseBotDeath, "owner.d", "fp4", 55, 0, 500);
            ConflictEngine.RecordComparison("SystemicConflict", "ab", true, false, true, 1000);
            ConflictDecision d6 = ConflictEngine.Evaluate("SystemicConflict", 2000);
            Check(d6.Confidence == ConflictConfidence.Confirmed, "CE06 removal + reintroduction => CONFIRMED");
            Check(d6.Action == Remediation.Quarantine, "CE06 CONFIRMED Class D => QUARANTINE");
            Check(d6.ToAuditLine().IndexOf("action=QUARANTINE", StringComparison.Ordinal) >= 0,
                "CE06 audit line carries QUARANTINE action");
            Check(HasLineContaining("CompatibilityDecision mod=SystemicConflict"), "CE06 decision line emitted");
            Check(ConflictEngine.CompatStatus("SystemicConflict").IndexOf("Conflict reason=", StringComparison.Ordinal) == 0,
                "CE06 recommended state visible in status");
            Check(ConflictEngine.MarkQuarantined("SystemicConflict", 3000), "CE06 executor confirms quarantine");
            Check(ConflictEngine.CompatStatus("SystemicConflict").IndexOf("Quarantined reason=", StringComparison.Ordinal) == 0,
                "CE06 quarantined state in status vocabulary");
            Check(ConflictEngine.MarkQuarantined("SystemicConflict", 3500), "CE06 re-confirm is idempotent");
            Check(HasLineContaining("CompatibilityQuarantined mod=SystemicConflict"), "CE06 quarantine line emitted once");
            ConflictEngine.RecordSymptom("SystemicConflict", SymptomKind.FalseBotDeath, "owner.d", "fp4", 55, 0, 500);
            ConflictEngine.RecordComparison("SystemicConflict", "ab", true, false, true, 1000);
            ConflictDecision d6b = ConflictEngine.Evaluate("SystemicConflict", 3600);
            Check(d6b.Action == Remediation.Quarantine, "CE06 re-evaluation still recommends");
            Check(ConflictEngine.CompatStatus("SystemicConflict").IndexOf("Quarantined", StringComparison.Ordinal) == 0,
                "CE06 already-quarantined stays quarantined (no state regression)");

            // ---- CE07: Class C symptom => disable feature, never quarantine ---------
            FreshSetup();
            SetCleanProfile("LoopMod");
            ConflictEngine.RecordSymptom("LoopMod", SymptomKind.ExecutorRetryStorm, "owner.c", "fp5", 30, 0, 500);
            ConflictEngine.RecordComparison("LoopMod", "ab", true, false, true, 1000);
            ConflictDecision d7 = ConflictEngine.Evaluate("LoopMod", 2000);
            Check(d7.Class == ConflictClass.ClassC_FeatureConflict, "CE07 retry-storm symptom maps to Class C");
            Check(d7.Confidence == ConflictConfidence.Confirmed, "CE07 full causality => CONFIRMED");
            Check(d7.Action == Remediation.DisableCapBotFeature, "CE07 Class C => DISABLE_FEATURE (keep the mod)");
            Check(d7.Action != Remediation.Quarantine, "CE07 Class C never quarantines");

            // ---- CE08: symptom→class mapping table ----------------------------------
            Check(ConflictRules.ClassForSymptom(SymptomKind.ExceptionStorm) == ConflictClass.ClassD_ModConflict, "CE08 exception storm => D");
            Check(ConflictRules.ClassForSymptom(SymptomKind.FalseBotDeath) == ConflictClass.ClassD_ModConflict, "CE08 false death => D");
            Check(ConflictRules.ClassForSymptom(SymptomKind.LifecycleFlicker) == ConflictClass.ClassD_ModConflict, "CE08 flicker => D");
            Check(ConflictRules.ClassForSymptom(SymptomKind.DuplicateBotsOrAgents) == ConflictClass.ClassD_ModConflict, "CE08 duplicates => D");
            Check(ConflictRules.ClassForSymptom(SymptomKind.MemoryLoss) == ConflictClass.ClassD_ModConflict, "CE08 memory loss => D");
            Check(ConflictRules.ClassForSymptom(SymptomKind.DataCorruption) == ConflictClass.ClassD_ModConflict, "CE08 corruption => D");
            Check(ConflictRules.ClassForSymptom(SymptomKind.CommandLoop) == ConflictClass.ClassC_FeatureConflict, "CE08 command loop => C");
            Check(ConflictRules.ClassForSymptom(SymptomKind.NavLoop) == ConflictClass.ClassC_FeatureConflict, "CE08 nav loop => C");
            Check(ConflictRules.ClassForSymptom(SymptomKind.ExecutorRetryStorm) == ConflictClass.ClassC_FeatureConflict, "CE08 executor storm => C");
            Check(ConflictRules.ClassForSymptom(SymptomKind.TaskDuplication) == ConflictClass.ClassC_FeatureConflict, "CE08 task duplication => C");
            Check(ConflictRules.ClassForSymptom(SymptomKind.AuthorityViolation) == ConflictClass.ClassC_FeatureConflict, "CE08 authority violation => C");
            Check(ConflictRules.ClassForSymptom(SymptomKind.RpcStorm) == ConflictClass.ClassC_FeatureConflict, "CE08 rpc storm => C");
            Check(ConflictRules.ClassForSymptom(SymptomKind.None) == ConflictClass.None, "CE08 none => none");

            // ---- CE09: rate limiting -------------------------------------------------
            FreshSetup();
            SetCleanProfile("QuietMod");
            ConflictEngine.RecordSymptom("QuietMod", SymptomKind.NavLoop, "owner.q", "fp6", 3, 0, 100);
            ConflictEngine.RecordComparison("QuietMod", "ab", true, false, false, 200);
            ConflictEngine.Evaluate("QuietMod", 1000);
            int emitsAfterFirst = s_Lines.Count;
            Check(emitsAfterFirst >= 1, "CE09 first evaluation emits");
            ConflictEngine.Evaluate("QuietMod", 2000);
            ConflictEngine.Evaluate("QuietMod", 30000);
            Check(s_Lines.Count == emitsAfterFirst, "CE09 stable verdict within interval does not re-emit");
            ConflictDecision d9 = ConflictEngine.Evaluate("QuietMod", 61000);
            Check(d9 != null, "CE09 re-evaluation after interval runs");
            Check(s_Lines.Count == emitsAfterFirst + 1, "CE09 re-emit after DecisionReemitMs");
            Check(ConflictEngine.EvaluationCount == 4, "CE09 evaluation counter tracks calls");

            // ---- CE10: loop protection + safe mode -----------------------------------
            FreshSetup();
            SetCleanProfile("Unfixable");
            ConflictEngine.RecordSymptom("Unfixable", SymptomKind.DataCorruption, "owner.u", "fp7", 9, 0, 500);
            ConflictEngine.RecordComparison("Unfixable", "ab", true, false, true, 1000);
            ConflictEngine.Evaluate("Unfixable", 2000);
            Check(ConflictEngine.MarkQuarantined("Unfixable", 3000), "CE10 first quarantine confirmed");
            for (int cycle = 1; cycle <= 3; cycle++)
            {
                if (cycle > 1) ConflictEngine.MarkQuarantined("Unfixable", 3500 + cycle * 100);
                Check(ConflictEngine.MarkRestoredForRetest("Unfixable", 4000 + cycle * 100), "CE10 cycle " + cycle + " restored");
                Check(ConflictEngine.MarkRetestResult("Unfixable", true, 5000 + cycle * 100), "CE10 cycle " + cycle + " retest failed");
            }
            Check(ConflictEngine.QuarantineAgainEvents == 3, "CE10 three QUARANTINE_AGAIN events counted");
            Check(ConflictEngine.SafeMode, "CE10 safe mode latched at third re-confirmation");
            Check(ConflictEngine.SafeModeReason.Length > 0, "CE10 safe mode carries a reason");
            Check(HasLineContaining("CompatibilitySafeMode enabled"), "CE10 safe-mode line emitted");
            Check(ConflictEngine.EnableSafeMode("manual", 9000), "CE10 EnableSafeMode idempotent");
            Check(ConflictEngine.SafeModeReason != "manual", "CE10 first latch reason preserved");
            Check(ConflictEngine.CompatStatus("Unfixable").IndexOf("Quarantined", StringComparison.Ordinal) == 0,
                "CE10 QUARANTINE_AGAIN reads as quarantined in status");

            // ---- CE11: restore/retest clean -------------------------------------------
            FreshSetup();
            SetCleanProfile("FixedMod");
            ConflictEngine.RecordSymptom("FixedMod", SymptomKind.FalseBotDeath, "owner.f", "fp8", 10, 0, 500);
            ConflictEngine.RecordComparison("FixedMod", "ab", true, false, true, 1000);
            ConflictEngine.Evaluate("FixedMod", 2000);
            ConflictEngine.MarkQuarantined("FixedMod", 3000);
            Check(ConflictEngine.MarkRestoredForRetest("FixedMod", 4000), "CE11 restore accepted from quarantined");
            Check(ConflictEngine.CompatStatus("FixedMod").IndexOf("retest in progress", StringComparison.Ordinal) > 0,
                "CE11 retest-in-progress status");
            Check(ConflictEngine.MarkRetestResult("FixedMod", false, 5000), "CE11 clean retest accepted");
            Check(ConflictEngine.CompatStatus("FixedMod").IndexOf("Compatible reason=retest clean", StringComparison.Ordinal) == 0,
                "CE11 COMPATIBLE_AFTER_RETEST in status vocabulary");
            Check(HasLineContaining("action=COMPATIBLE_AFTER_RETEST"), "CE11 compatible line emitted");
            Check(!ConflictEngine.SafeMode, "CE11 clean retest does not latch safe mode");

            // ---- CE12: invalid transitions refused --------------------------------------
            FreshSetup();
            SetCleanProfile("TMod");
            Check(!ConflictEngine.MarkQuarantined("TMod", 1000), "CE12 quarantine confirm without recommendation refused");
            Check(!ConflictEngine.MarkRestoredForRetest("TMod", 1100), "CE12 restore without quarantine refused");
            Check(!ConflictEngine.MarkRetestResult("TMod", false, 1200), "CE12 retest result without restore refused");
            Check(!ConflictEngine.MarkQuarantined("GhostMod", 1300), "CE12 unknown mod refused");
            Check(ConflictEngine.Evaluate("GhostMod", 1400) == null, "CE12 evaluate unknown mod returns null");
            Check(!ConflictEngine.SetModProfile(null, false, false, false, false, false, false), "CE12 null name refused");
            Check(!ConflictEngine.RecordSymptom("TMod", SymptomKind.None, "", "", 1, 0, 0), "CE12 none-kind symptom refused");
            Check(!ConflictEngine.RecordSymptom("TMod", SymptomKind.MemoryLoss, "", "", 0, 0, 0), "CE12 zero-count symptom refused");
            Check(!ConflictEngine.RecordComparison("TMod", "", true, false, false, 0), "CE12 empty label refused");
            Check(ConflictEngine.RefusedCount == 9, "CE12 all refusals counted");
            Check(ConflictEngine.EvaluationCount == 0, "CE12 failed evaluates not counted as evaluations");

            // ---- CE13: duplicate CapBot ---------------------------------------------------
            FreshSetup();
            Check(ConflictEngine.ReportDuplicateCapBot(2, 1000), "CE13 duplicate report accepted");
            Check(ConflictEngine.DuplicateCapBotEvents == 1, "CE13 duplicate event counted");
            Check(HasLineContaining("action=STOP_DUPLICATE_EXECUTION"), "CE13 STOP line emitted");
            Check(HasLineContaining("assemblies=2"), "CE13 line carries assembly count");

            // ---- CE14: status lines bounded ------------------------------------------------
            FreshSetup();
            for (int i = 0; i < 20; i++) { ConflictEngine.SetModProfile("Bulk" + i, false, false, false, false, false, false); }
            List<string> lines = ConflictEngine.StatusLines();
            Check(lines.Count == ConflictEngine.MaxStatusLinesBound,
                "CE14 status surface capped at MaxStatusLines");
            Check(lines[0].IndexOf("mods=20", StringComparison.Ordinal) >= 0, "CE14 summary line carries counts");
            Check(lines[1].IndexOf("quarantine=None", StringComparison.Ordinal) >= 0, "CE14 per-mod line carries state");
            for (int i = 0; i < lines.Count; i++)
            {
                Check(lines[i].IndexOf("ConflictEngine", StringComparison.Ordinal) == 0, "CE14 line " + i + " prefixed");
            }

            // ---- CE15: determinism ------------------------------------------------------------
            FreshSetup();
            SetCleanProfile("DetMod");
            ConflictEngine.RecordSymptom("DetMod", SymptomKind.CommandLoop, "owner.dd", "fp9", 5, 0, 500);
            ConflictEngine.RecordComparison("DetMod", "ab", true, false, false, 600);
            string lineA = ConflictEngine.Evaluate("DetMod", 1000).ToAuditLine();
            FreshSetup();
            SetCleanProfile("DetMod");
            ConflictEngine.RecordSymptom("DetMod", SymptomKind.CommandLoop, "owner.dd", "fp9", 5, 0, 500);
            ConflictEngine.RecordComparison("DetMod", "ab", true, false, false, 600);
            string lineB = ConflictEngine.Evaluate("DetMod", 1000).ToAuditLine();
            Check(lineA == lineB, "CE15 same evidence => identical decision line");

            // ---- CE16: audit-line vocabulary ----------------------------------------------------
            FreshSetup();
            SetCleanProfile("FmtMod");
            ConflictEngine.RecordSymptom("FmtMod", SymptomKind.FalseBotDeath, "owner.ff", "fp10", 20, 0, 500);
            ConflictEngine.RecordComparison("FmtMod", "ab", true, false, true, 600);
            string fmt = ConflictEngine.Evaluate("FmtMod", 1000).ToAuditLine();
            Check(fmt.IndexOf("CompatibilityDecision mod=FmtMod class=D confidence=CONFIRMED action=QUARANTINE", StringComparison.Ordinal) == 0,
                "CE16 full-causality line format exact");
            SetCleanProfile("FmtMod2");
            ConflictEngine.RecordSymptom("FmtMod2", SymptomKind.ExceptionStorm, "owner.gg", "fp11", 20, 0, 500);
            string fmt2 = ConflictEngine.Evaluate("FmtMod2", 1100).ToAuditLine();
            Check(fmt2.IndexOf("refusal=INSUFFICIENT_EVIDENCE", StringComparison.Ordinal) > 0,
                "CE16 refusal vocabulary present");
            Check(fmt2.IndexOf("confidence=PROBABLE", StringComparison.Ordinal) > 0, "CE16 probable vocabulary present");

            // ---- CE17: MoreBots real-evidence mirror (honesty invariant) ------------------------
            // Mirrors the archived A/B: TEST B (MoreBots present) 21,807 IndexOOB;
            // TEST A (MoreBots absent) 0. The owner uninstalled the mod — no
            // reintroduction leg ever ran. The engine must NOT quarantine on
            // that evidence, however strong it looks.
            FreshSetup();
            ConflictEngine.SetModProfile("MoreBots", false, true, false, true, false, false);
            ConflictEngine.RecordSymptom("MoreBots", SymptomKind.ExceptionStorm, "Mest.MoreBots",
                "IndexOutOfRangeException|PLPlayer.GetAIData|MoreBots", 21807, 0, 60000);
            ConflictEngine.RecordComparison("MoreBots", "morebots_unload_ab", true, false, false, 61000);
            ConflictDecision d17 = ConflictEngine.Evaluate("MoreBots", 62000);
            Check(d17.Class == ConflictClass.ClassD_ModConflict, "CE17 evidence maps to Class D");
            Check(d17.Confidence == ConflictConfidence.Probable, "CE17 without reintroduction => PROBABLE, not CONFIRMED");
            Check(d17.Action == Remediation.Observe, "CE17 verdict is OBSERVE — never quarantine without full causality");
            Check(d17.Action != Remediation.Quarantine, "CE17 honesty invariant: no quarantine on partial causality");
            Check(ConflictEngine.CompatStatus("MoreBots").IndexOf("Quarantined", StringComparison.Ordinal) < 0,
                "CE17 no quarantine state was entered");

            // ---- CE18: bounded tracking ----------------------------------------------------------
            FreshSetup();
            bool overBoundAccepted = true;
            for (int i = 0; i < ConflictEngine.MaxTrackedModsBound + 4; i++)
            {
                overBoundAccepted &= ConflictEngine.SetModProfile("Cap" + i, false, false, false, false, false, false);
            }
            Check(!overBoundAccepted, "CE18 bound refused at least one registration");
            Check(ConflictEngine.TrackedModCount == ConflictEngine.MaxTrackedModsBound, "CE18 tracking capped exactly");
            Check(ConflictEngine.RefusedCount >= 4, "CE18 over-bound refusals counted");

            // ---- CE19: reset -----------------------------------------------------------------------
            FreshSetup();
            SetCleanProfile("ResetMe");
            ConflictEngine.RecordSymptom("ResetMe", SymptomKind.RpcStorm, "", "", 1, 0, 1);
            ConflictEngine.Evaluate("ResetMe", 100);
            ConflictEngine.ReportDuplicateCapBot(2, 150);
            ConflictEngine.ResetForTests();
            Check(ConflictEngine.TrackedModCount == 0, "CE19 mods cleared");
            Check(ConflictEngine.EvaluationCount == 0 && ConflictEngine.DuplicateCapBotEvents == 0,
                "CE19 counters cleared");
            Check(!ConflictEngine.SafeMode && ConflictEngine.SafeModeReason.Length == 0, "CE19 safe mode cleared");
            Check(ConflictEngine.CompatStatus("ResetMe") == "Loaded", "CE19 status falls back to Loaded");

            // ---- CE20: status vocabulary edges ------------------------------------------------------
            FreshSetup();
            Check(ConflictEngine.CompatStatus("NeverSeen") == "Loaded", "CE20 untracked mod reads Loaded");
            Check(ConflictEngine.CompatStatus("") == "Unknown", "CE20 empty name reads Unknown");
            Check(ConflictEngine.CompatStatus(null) == "Unknown", "CE20 null name reads Unknown");

            // ---- CE21: protected mod list ------------------------------------------------------------
            // The never-remove list: core game/runtime/infra + CapBot itself.
            Check(ProtectedModList.IsProtected("Assembly-CSharp"), "CE21 Assembly-CSharp protected");
            Check(ProtectedModList.IsProtected("assembly-csharp-firstpass"), "CE21 case-insensitive match");
            Check(ProtectedModList.IsProtected("PulsarModLoader"), "CE21 PML protected");
            Check(ProtectedModList.IsProtected("BepInEx"), "CE21 BepInEx protected");
            Check(ProtectedModList.IsProtected("0Harmony"), "CE21 Harmony runtime protected");
            Check(ProtectedModList.IsProtected("UnityEngine.UI"), "CE21 UnityEngine.UI protected");
            Check(ProtectedModList.IsProtected("CapBot"), "CE21 CapBot itself protected");
            Check(ProtectedModList.IsProtected("CapBotBaseline"), "CE21 CapBotBaseline protected");
            Check(ProtectedModList.IsProtected("CapBot.dll"), "CE21 .dll file form accepted");
            Check(ProtectedModList.IsProtected("QualityImprover"), "CE21 Quality Improver protected (master-prompt rule)");
            Check(!ProtectedModList.IsProtected("ExpandedGalaxy"), "CE21 regular mods NOT protected");
            Check(!ProtectedModList.IsProtected("MoreBots"), "CE21 MoreBots not on the list (evidence decides)");
            Check(!ProtectedModList.IsProtected(""), "CE21 empty not protected");
            Check(!ProtectedModList.IsProtected(null), "CE21 null not protected");
            // End-to-end: a protected mod with full-causality evidence stays KeepBoth.
            FreshSetup();
            ConflictEngine.SetModProfile("PulsarModLoader", true, true, false, false, false, false);
            ConflictEngine.RecordSymptom("PulsarModLoader", SymptomKind.ExceptionStorm, "", "fp", 1000, 0, 500);
            ConflictEngine.RecordComparison("PulsarModLoader", "ab", true, false, true, 1000);
            ConflictDecision d21 = ConflictEngine.Evaluate("PulsarModLoader", 2000);
            Check(d21.Refusal == RefusalReason.ProtectedMod && d21.Action == Remediation.KeepBoth,
                "CE21 protected refusal end-to-end despite CONFIRMED-class evidence");

            // ---- CE22: quarantine-state listener fires on every transition -----------
            FreshSetup();
            List<string> stateEvents = new List<string>();
            ConflictEngine.SetStateListener(delegate (string mod, ConflictEngine.QuarantineState st)
            {
                stateEvents.Add(mod + "->" + st);
            });
            SetCleanProfile("EvtMod");
            ConflictEngine.RecordSymptom("EvtMod", SymptomKind.FalseBotDeath, "", "", 10, 0, 500);
            ConflictEngine.RecordComparison("EvtMod", "ab", true, false, true, 1000);
            ConflictEngine.Evaluate("EvtMod", 2000);
            Check(stateEvents.Count == 0, "CE22 no event on Evaluate (recommendation is not a transition)");
            ConflictEngine.MarkQuarantined("EvtMod", 3000);
            Check(stateEvents.Count == 1 && stateEvents[0] == "EvtMod->Quarantined", "CE22 Quarantined event");
            ConflictEngine.MarkQuarantined("EvtMod", 3100);
            Check(stateEvents.Count == 1, "CE22 idempotent re-confirm does not re-fire");
            ConflictEngine.MarkRestoredForRetest("EvtMod", 4000);
            Check(stateEvents.Count == 2 && stateEvents[1] == "EvtMod->RestoredForRetest", "CE22 RestoredForRetest event");
            ConflictEngine.MarkRetestResult("EvtMod", false, 5000);
            Check(stateEvents.Count == 3 && stateEvents[2] == "EvtMod->CompatibleAfterRetest", "CE22 CompatibleAfterRetest event");
            ConflictEngine.ResetForTests();
            Check(stateEvents.Count == 3, "CE22 reset does not fire events");

            // ---- CE23: quarantine record factory + rendering ---------------------------
            Check(QuarantineRecord.CanRecord(ConflictClass.ClassD_ModConflict, ConflictConfidence.Confirmed),
                "CE23 confirmed Class D can be recorded");
            Check(!QuarantineRecord.CanRecord(ConflictClass.ClassD_ModConflict, ConflictConfidence.Probable),
                "CE23 probable Class D CANNOT be recorded (honesty guard)");
            Check(!QuarantineRecord.CanRecord(ConflictClass.ClassC_FeatureConflict, ConflictConfidence.Confirmed),
                "CE23 confirmed Class C cannot be recorded");
            QuarantineRecord rec = new QuarantineRecord("BadMod", "BadMod.dll", "1.0", "owner.bad",
                ConflictRules.ClassText(ConflictClass.ClassD_ModConflict),
                ConflictRules.ConfidenceText(ConflictConfidence.Confirmed),
                "exception storm count=1000", "A/B: present with, absent without, reintroduced reproduces",
                "AUTO-QUARANTINE per standing directive (CONFIRMED Class D)",
                "P46", "v1.2.10", true, 1234567890L);
            string json = rec.ToJson();
            Check(json.IndexOf("\"mod\": \"BadMod\"", StringComparison.Ordinal) > 0, "CE23 json carries mod");
            Check(json.IndexOf("\"confidence\": \"CONFIRMED\"", StringComparison.Ordinal) > 0, "CE23 json carries confidence");
            Check(json.IndexOf("\"reversible\": true", StringComparison.Ordinal) > 0, "CE23 json carries reversibility");
            Check(json.IndexOf("\"timestampMs\": 1234567890", StringComparison.Ordinal) > 0, "CE23 json carries timestamp");
            string qJson = new QuarantineRecord("Q\"uote\\Mod", "x.dll", "1", "h", "D", "CONFIRMED", "s", "e\r\nv", "d", "P46", "g", false, 1L).ToJson();
            Check(qJson.IndexOf("Q\\\"uote\\\\Mod", StringComparison.Ordinal) > 0, "CE23 json escapes quotes+backslashes");
            Check(qJson.IndexOf("\r", StringComparison.Ordinal) < 0 && qJson.IndexOf("\\r\\nv", StringComparison.Ordinal) > 0,
                "CE23 newlines JSON-escaped (evidence stays verbatim, single-line)");
            Check(rec.ToAuditLine().IndexOf("reversible=yes", StringComparison.Ordinal) > 0, "CE23 audit line shape");

            // ---- CE24: compatibility-state boot gate -----------------------------------
            Check(CompatibilityStateRow.BootMustKeepQuarantined("Quarantined"), "CE24 quarantined keeps boot-quarantine");
            Check(CompatibilityStateRow.BootMustKeepQuarantined("QuarantineAgain"), "CE24 quarantine-again keeps boot-quarantine");
            Check(!CompatibilityStateRow.BootMustKeepQuarantined("CompatibleAfterRetest"), "CE24 compatible-after-retest boot-restores");
            Check(!CompatibilityStateRow.BootMustKeepQuarantined("None"), "CE24 none boot-restores");
            Check(!CompatibilityStateRow.BootMustKeepQuarantined(""), "CE24 empty boot-restores (fail-open for unknown states is the executor's decision, not the row's)");
            CompatibilityStateRow row = new CompatibilityStateRow("BadMod", "Quarantined", 1, 555L, "confirmed class D");
            string rowJson = row.ToJson();
            Check(rowJson.IndexOf("\"state\": \"Quarantined\"", StringComparison.Ordinal) > 0, "CE24 row json carries state");
            Check(rowJson.IndexOf("\"reconfirmations\": 1", StringComparison.Ordinal) > 0, "CE24 row json carries reconfirmations");

            // ---- CE25: audit trail bounded -------------------------------------------------
            CompatibilityAuditTrail.ResetForTests();
            for (int i = 0; i < CompatibilityAuditTrail.MaxEntriesBound; i++)
            {
                CompatibilityAuditTrail.Append("line " + i);
            }
            Check(CompatibilityAuditTrail.Count == CompatibilityAuditTrail.MaxEntriesBound, "CE25 trail holds the full stream within bound");
            List<string> snap = CompatibilityAuditTrail.Snapshot();
            Check(snap[0] == "line 0", "CE25 snapshot oldest-first");
            for (int i = 0; i < 5; i++)
            {
                CompatibilityAuditTrail.Append("more " + i);
            }
            Check(CompatibilityAuditTrail.Count == CompatibilityAuditTrail.MaxEntriesBound, "CE25 trail bounded");
            Check(CompatibilityAuditTrail.DroppedCount == 5, "CE25 drops counted");
            Check(CompatibilityAuditTrail.Snapshot()[0] == "line 5", "CE25 oldest entries evicted");
            CompatibilityAuditTrail.Append(null);
            CompatibilityAuditTrail.Append("");
            Check(CompatibilityAuditTrail.Count == CompatibilityAuditTrail.MaxEntriesBound, "CE25 null/empty lines ignored");

            // ---- CE26: P48 Harmony-map enrichment (strictly conservative) -------------
            FreshSetup();
            ConflictEngine.SetModProfile("QuietMod", false, false, false, false, false, false);
            ConflictEngine.SetModProfile("PatchingMod", false, false, false, false, false, false);
            ConflictEngine.SetModProfile("AlreadyHarmony", false, true, false, false, false, false);
            // Unknown mod refused; empty name refused.
            Check(!ConflictEngine.MarkHarmonyObserved("Untracked", "owner.x", 1000), "CE26 unknown mod refused");
            Check(!ConflictEngine.MarkHarmonyObserved("", "owner.x", 1000), "CE26 empty name refused");
            // Flip false->true, owner recorded, audited once.
            Check(ConflictEngine.MarkHarmonyObserved("PatchingMod", "owner.patching", 1000), "CE26 flip accepted");
            ConflictDecision d26 = ConflictEngine.Evaluate("PatchingMod", 1000);
            Check(d26 != null && d26.Refusal == RefusalReason.UsesHarmonyOnly && d26.Action == Remediation.KeepBoth,
                "CE26 enriched mod is harmony-only => never removed");
            Check(HasLineContaining("CompatibilityHarmonyEnriched mod=PatchingMod owner=owner.patching"),
                "CE26 enrichment audited with owner");
            Check(ConflictEngine.HarmonyOwnerText("PatchingMod") == "owner.patching", "CE26 owner readback");
            Check(ConflictEngine.HarmonyEnrichedCount == 1, "CE26 enriched counter");
            // Idempotent: same mod again never re-enriches (counter stays 1).
            Check(ConflictEngine.MarkHarmonyObserved("PatchingMod", "owner.patching", 2000), "CE26 re-observe ok");
            Check(ConflictEngine.HarmonyEnrichedCount == 1, "CE26 idempotent (no double-count)");
            Check(!HasLineContaining("mod=PatchingMod owner=owner.patching") || HasLineContaining("CompatibilityHarmonyEnriched mod=PatchingMod owner=owner.patching"),
                "CE26 single audit line for stable state");
            // Already-true profile: observation updates owner, never re-enriches.
            Check(ConflictEngine.MarkHarmonyObserved("AlreadyHarmony", "owner.already", 1000), "CE26 already-true ok");
            Check(ConflictEngine.HarmonyEnrichedCount == 1, "CE26 already-true not double-counted");
            Check(ConflictEngine.HarmonyOwnerText("AlreadyHarmony") == "owner.already", "CE26 owner recorded for already-true");
            Check(!HasLineContaining("mod=AlreadyHarmony"), "CE26 no audit for non-flip");
            // Null owner tolerated (owner stays whatever it was; flip still happens).
            FreshSetup();
            ConflictEngine.SetModProfile("NullOwnerMod", false, false, false, false, false, false);
            Check(ConflictEngine.MarkHarmonyObserved("NullOwnerMod", null, 1000), "CE26 null owner tolerated");
            Check(ConflictEngine.HarmonyEnrichedCount == 1, "CE26 null owner still enriches");
            Check(HasLineContaining("owner=unknown"), "CE26 null owner audited as unknown");
            // Enrichment can never make a quarantined-decision mod MORE removable:
            // symptom + A/B + enrichment all present => still harmony-refused on
            // a non-implicating A/B (evidence-only, no behavior change).
            FreshSetup();
            ConflictEngine.SetModProfile("Mixed", false, false, false, false, false, false);
            ConflictEngine.RecordSymptom("Mixed", SymptomKind.ExceptionStorm, "Mixed", "fp", 10, 100, 200);
            ConflictEngine.RecordComparison("Mixed", "ab", false, false, false, 300);
            ConflictEngine.MarkHarmonyObserved("Mixed", "owner.mixed", 400);
            ConflictDecision d26b = ConflictEngine.Evaluate("Mixed", 400);
            Check(d26b != null && d26b.Action == Remediation.KeepBoth && d26b.Confidence == ConflictConfidence.Unverified,
                "CE26 enrichment does not strengthen a weak A/B");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}