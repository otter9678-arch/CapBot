// Dev-side unit tests for the Phase 49 live-telemetry symptom detectors (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1.
// Time is virtual: every OnLog call takes an explicit nowMs.
//
// Covers the Phase 49 mandated scenarios:
//   SY01 warning/log events are noise (never counted)
//   SY02 unattributed exceptions counted, never invented into evidence
//   SY03 storm threshold crossing records ExceptionStorm into the engine (PROBABLE/OBSERVE)
//   SY04 one record per window crossing (idempotent storm latch)
//   SY05 window expiry resets the count (old events fall out)
//   SY06 different fingerprints never merge (fingerprint separation)
//   SY07 different mods never merge (per-mod attribution)
//   SY08 CapBot's own log lines are skipped (never self-attribute)
//   SY09 resolver fault = unattributed (fail-closed), never attributed
//   SY10 bounded fingerprints + drop counting
//   SY11 status lines bounded + shaped + reset
//   SY12 engine evaluation fired on storm (audit line contains the decision)
using System;
using System.Collections.Generic;
using CapBot.Core.Compatibility;

namespace CapBot.TaskTests
{
    internal static class SymptomDetectorTests
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

        private static void FreshSetup(Func<string, string> resolver)
        {
            ConflictEngine.ResetForTests();
            SymptomDetectors.ResetForTests();
            s_Lines.Clear();
            SymptomDetectors.SetAuditListener(delegate (string line) { s_Lines.Add(line); });
            ConflictEngine.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            if (resolver != null) SymptomDetectors.SetModResolver(resolver);
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private const int Error = 0;      // UnityEngine.LogType.Error
        private const int Exception = 4;  // UnityEngine.LogType.Exception
        private const int Warning = 2;
        private const int Log = 3;

        private static void Storm(string mod, int n, long startMs)
        {
            for (int i = 0; i < n; i++)
            {
                SymptomDetectors.OnLog("NullReferenceException: boom", "  at MoreBotsPatch.Prefix () [0x00000] in <" + mod + ">", Error, startMs + i);
            }
        }

        internal static int Run()
        {
            // ---- SY01: warnings/logs never counted --------------------------------
            FreshSetup(delegate (string st) { return "NoiseMod"; });
            for (int i = 0; i < 50; i++)
            {
                SymptomDetectors.OnLog("warning text", "stack NoiseMod", Warning, 1000 + i);
                SymptomDetectors.OnLog("log text", "stack NoiseMod", Log, 1000 + i);
            }
            Check(SymptomDetectors.TrackedCount == 0, "SY01 warnings/logs not tracked");
            Check(SymptomDetectors.UnattributedCount == 0, "SY01 warnings/logs not counted anywhere");

            // ---- SY02: unattributed => counted, never recorded ---------------------
            FreshSetup(null);   // no resolver at all
            for (int i = 0; i < 50; i++)
                SymptomDetectors.OnLog("NullReferenceException: x", "at SomeFrame () in <asm>", Error, 1000 + i);
            Check(SymptomDetectors.UnattributedCount == 50, "SY02 unattributed counted");
            Check(SymptomDetectors.TrackedCount == 0, "SY02 nothing attributed");
            Check(SymptomDetectors.SymptomsRecordedCount == 0, "SY02 no symptom fabricated");
            Check(SymptomDetectors.UnattributedLastFingerprint().StartsWith("NullReferenceException|", StringComparison.Ordinal),
                "SY02 fingerprint of last unattributed kept");

            // ---- SY03: storm crossing records into the engine ----------------------
            FreshSetup(delegate (string st) { return st.IndexOf("MoreBotsPatch", StringComparison.Ordinal) >= 0 ? "MoreBots" : null; });
            ConflictEngine.SetModProfile("MoreBots", false, false, false, false, false, false);
            Storm("MoreBots", SymptomDetectors.StormThreshold, 1000);
            Check(SymptomDetectors.SymptomsRecordedCount == 1, "SY03 storm recorded");
            Check(HasLineContaining("SymptomDetected mod=MoreBots kind=ExceptionStorm count=" + SymptomDetectors.StormThreshold),
                "SY03 audited");
            ConflictDecision d = ConflictEngine.Evaluate("MoreBots", 5000);
            Check(d != null && d.Class == ConflictClass.ClassD_ModConflict && d.Confidence == ConflictConfidence.Probable
                && d.Action == Remediation.Observe, "SY03 symptom alone => PROBABLE/OBSERVE (never quarantine)");

            // ---- SY04: storm latch idempotent ---------------------------------------
            Storm("MoreBots", 30, 2000);   // more events in the same window
            Check(SymptomDetectors.SymptomsRecordedCount == 1, "SY04 no re-record inside window");

            // ---- SY05: window expiry resets -----------------------------------------
            FreshSetup(delegate (string st) { return "MoreBots"; });
            ConflictEngine.SetModProfile("MoreBots", false, false, false, false, false, false);
            for (int i = 0; i < SymptomDetectors.StormThreshold - 1; i++)
                SymptomDetectors.OnLog("KeyNotFoundException: k", "at M.P () in MoreBots", Error, 1000 + i);
            Check(SymptomDetectors.SymptomsRecordedCount == 0, "SY05 below threshold quiet");
            // window expiry (FirstMs + StormWindowMs + 1)
            SymptomDetectors.OnLog("KeyNotFoundException: k", "at M.P () in MoreBots", Error, 1000 + SymptomDetectors.StormWindowMs + 10);
            Check(SymptomDetectors.SymptomsRecordedCount == 0, "SY05 window reset (old events forgotten)");
            for (int i = 0; i < SymptomDetectors.StormThreshold; i++)
                SymptomDetectors.OnLog("KeyNotFoundException: k", "at M.P () in MoreBots", Error,
                    1000 + SymptomDetectors.StormWindowMs + 20 + i);
            Check(SymptomDetectors.SymptomsRecordedCount == 1, "SY05 fresh window re-crosses and records");

            // ---- SY06: fingerprints never merge --------------------------------------
            FreshSetup(delegate (string st) { return "Mod"; });
            ConflictEngine.SetModProfile("Mod", false, false, false, false, false, false);
            for (int i = 0; i < SymptomDetectors.StormThreshold - 1; i++)
                SymptomDetectors.OnLog("NullReferenceException: n", "at A.P () in Mod", Error, 1000 + i);
            for (int i = 0; i < SymptomDetectors.StormThreshold - 1; i++)
                SymptomDetectors.OnLog("IndexOutOfRangeException: i", "at A.Q () in Mod", Exception, 2000 + i);
            Check(SymptomDetectors.SymptomsRecordedCount == 0, "SY06 two distinct fingerprints below threshold each");
            SymptomDetectors.OnLog("NullReferenceException: n", "at A.P () in Mod", Error, 3000);
            Check(SymptomDetectors.SymptomsRecordedCount == 1, "SY06 first fingerprint crosses independently");
            SymptomDetectors.OnLog("IndexOutOfRangeException: i", "at A.Q () in Mod", Exception, 3001);
            Check(SymptomDetectors.SymptomsRecordedCount == 2, "SY06 second fingerprint crosses independently");

            // ---- SY07: per-mod attribution separation --------------------------------
            FreshSetup(delegate (string st)
            {
                if (st.IndexOf("AsmOne", StringComparison.Ordinal) >= 0) return "One";
                if (st.IndexOf("AsmTwo", StringComparison.Ordinal) >= 0) return "Two";
                return null;
            });
            ConflictEngine.SetModProfile("One", false, false, false, false, false, false);
            ConflictEngine.SetModProfile("Two", false, false, false, false, false, false);
            for (int i = 0; i < SymptomDetectors.StormThreshold - 1; i++)
                SymptomDetectors.OnLog("NullReferenceException: n", "at A.P () in AsmOne", Error, 1000 + i);
            Check(SymptomDetectors.SymptomsRecordedCount == 0, "SY07 One below threshold");
            SymptomDetectors.OnLog("NullReferenceException: n", "at B.Q () in AsmTwo", Error, 2000);
            Check(SymptomDetectors.SymptomsRecordedCount == 0, "SY07 Two's single event does not merge into One");
            SymptomDetectors.OnLog("NullReferenceException: n", "at A.P () in AsmOne", Error, 3000);
            Check(SymptomDetectors.SymptomsRecordedCount == 1, "SY07 One crosses with its own window");
            ConflictDecision d7 = ConflictEngine.Evaluate("Two", 4000);
            Check(d7 != null && d7.Action == Remediation.KeepBoth, "SY07 Two has no symptom (no evidence invented)");

            // ---- SY08: CapBot's own lines are skipped ---------------------------------
            FreshSetup(delegate (string st) { return "CapBot"; });
            for (int i = 0; i < 50; i++)
                SymptomDetectors.OnLog("[CapBot:COMPAT] [INFO] some line", "stack", Error, 1000 + i);
            Check(SymptomDetectors.TrackedCount == 0 && SymptomDetectors.UnattributedCount == 0,
                "SY08 CapBot-tagged lines never counted");

            // ---- SY09: resolver fault = unattributed (fail-closed) ---------------------
            FreshSetup(delegate (string st) { throw new InvalidOperationException("boom"); });
            for (int i = 0; i < 50; i++)
                SymptomDetectors.OnLog("NullReferenceException: n", "at X.P () in SomeMod", Error, 1000 + i);
            Check(SymptomDetectors.UnattributedCount == 50, "SY09 resolver fault => unattributed");
            Check(SymptomDetectors.TrackedCount == 0, "SY09 nothing attributed on resolver fault");
            Check(SymptomDetectors.SymptomsRecordedCount == 0, "SY09 no symptom fabricated on resolver fault");

            // ---- SY10: bounded fingerprints + drop counting -----------------------------
            FreshSetup(delegate (string st)
            {
                // each stack gets a unique fingerprint
                return "Mod";
            });
            ConflictEngine.SetModProfile("Mod", false, false, false, false, false, false);
            for (int i = 0; i < SymptomDetectors.MaxFingerprintsBound + 5; i++)
                SymptomDetectors.OnLog("InvalidOperationException: op" + i, "at F" + i + ".M () in Mod", Error, 1000 + i);
            Check(SymptomDetectors.TrackedCount == SymptomDetectors.MaxFingerprintsBound, "SY10 fingerprint bound holds");
            Check(SymptomDetectors.DroppedCount == 5, "SY10 overflow dropped + counted");

            // ---- SY11: status lines bounded + shaped + reset -----------------------------
            FreshSetup(null);
            List<string> st11 = SymptomDetectors.StatusLines();
            Check(st11.Count >= 1 && st11[0].IndexOf("SymptomDetectors: tracked=", StringComparison.Ordinal) == 0,
                "SY11 summary present");
            Check(st11[0].IndexOf("threshold=", StringComparison.Ordinal) > 0, "SY11 summary shape");
            SymptomDetectors.ResetForTests();
            Check(SymptomDetectors.TrackedCount == 0 && SymptomDetectors.SymptomsRecordedCount == 0
                && SymptomDetectors.UnattributedCount == 0 && SymptomDetectors.DroppedCount == 0,
                "SY11 reset clears counters");

            // ---- SY12: engine evaluation fired on storm (decision line audited) ----------
            FreshSetup(delegate (string st) { return "LoopMod"; });
            ConflictEngine.SetModProfile("LoopMod", false, false, false, false, false, false);
            Storm("LoopMod", SymptomDetectors.StormThreshold, 1000);
            Check(HasLineContaining("CompatibilityDecision mod=LoopMod class=D confidence=PROBABLE action=OBSERVE"),
                "SY12 engine evaluated after storm");

            Console.WriteLine("");
            Console.WriteLine("SYMPTOM_DETECTOR_TESTS passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}