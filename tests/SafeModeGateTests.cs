// Dev-side unit tests for the Phase 50 Safe Mode behavioral gate (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1.
// Time is virtual: every Tick call takes an explicit nowMs.
//
// Covers the Phase 50 mandated scenarios:
//   SM01 unwired provider => never suspends (no evidence, no behavior change)
//   SM02 latched provider => suspends; Tick returns true (fail-closed behavior)
//   SM03 unlatch does NOT auto-clear (session-sticky, mirrors the engine latch)
//   SM04 rising-edge reason capture + suspension counter
//   SM05 provider fault => fail-closed suspension (never fails open)
//   SM06 internal gate fault => fail-closed (provider throws inside Tick path)
//   SM07 idempotent latch: EnableSafeMode no-op never re-counts an edge
//   SM08 audit line shape + suspended=no on clean session
//   SM09 re-emit only after interval while suspended (rate-limited)
//   SM10 status lines bounded + shaped + reset clears everything
//   SM11 engine-integration: 3x QUARANTINE_AGAIN latch flips the gate (real seam)
//   SM12 gate fault never kills the caller (exception swallowed, suspend returned)
using System;
using System.Collections.Generic;
using CapBot.Core.Compatibility;

namespace CapBot.TaskTests
{
    internal static class SafeModeGateTests
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

        private static void FreshSetup(Func<bool> latchProvider, Func<string> reasonProvider)
        {
            ConflictEngine.ResetForTests();
            SafeModeGate.ResetForTests();
            s_Lines.Clear();
            SafeModeGate.SetAuditListener(delegate (string line) { s_Lines.Add(line); });
            ConflictEngine.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            if (latchProvider != null) SafeModeGate.SetSafeModeProvider(latchProvider);
            if (reasonProvider != null) SafeModeGate.SetSafeModeReasonProvider(reasonProvider);
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // Drives the engine's full loop-protection cycle so the latch engages
        // for exactly the production reason (mirrors ConflictEngineTests CE10).
        private static void LatchEngineSafeMode(string modName)
        {
            ConflictEngine.SetModProfile(modName, false, false, false, false, false, false);
            ConflictEngine.RecordSymptom(modName, SymptomKind.DataCorruption, "owner", "fp", 9, 0, 500);
            ConflictEngine.RecordComparison(modName, "ab", true, false, true, 1000);
            ConflictEngine.Evaluate(modName, 2000);
            ConflictEngine.MarkQuarantined(modName, 3000);
            for (int cycle = 1; cycle <= 3; cycle++)
            {
                if (cycle > 1) ConflictEngine.MarkQuarantined(modName, 3500 + cycle * 100);
                ConflictEngine.MarkRestoredForRetest(modName, 4000 + cycle * 100);
                ConflictEngine.MarkRetestResult(modName, true, 5000 + cycle * 100);
            }
        }

        internal static int Run()
        {
            // ---- SM01: unwired provider => never suspends --------------------------
            FreshSetup(null, null);
            Check(!SafeModeGate.Tick(1000), "SM01 unwired gate does not suspend");
            Check(!SafeModeGate.Tick(2000), "SM01 unwired gate stays inert");
            Check(SafeModeGate.TicksTotal == 2, "SM01 tick counter still tracks");

            // ---- SM02: latched provider => suspends --------------------------------
            bool latched = false;
            FreshSetup(delegate { return latched; }, null);
            Check(!SafeModeGate.Tick(1000), "SM02 not-latched passes");
            latched = true;
            Check(SafeModeGate.Tick(2000), "SM02 latched gate suspends");
            Check(SafeModeGate.Suspended, "SM02 readback suspended");
            Check(SafeModeGate.TicksSuspended >= 1, "SM02 suspended tick counted");

            // ---- SM03: unlatch does not auto-clear (session-sticky) ----------------
            latched = false;
            Check(SafeModeGate.Tick(3000), "SM03 provider un-latch stays suspended (sticky)");
            Check(SafeModeGate.Suspended, "SM03 sticky readback");

            // ---- SM04: rising-edge reason capture + edge counter -------------------
            string reason = "";
            FreshSetup(delegate { return reason.Length > 0; }, delegate { return reason; });
            Check(!SafeModeGate.Tick(1000), "SM04 pre-latch passes");
            reason = "repeated re-confirmed conflicts";
            Check(SafeModeGate.Tick(2000), "SM04 rising edge suspends");
            Check(SafeModeGate.SuspensionCount == 1, "SM04 one edge counted");
            Check(SafeModeGate.Reason == "repeated re-confirmed conflicts", "SM04 reason captured at edge");
            Check(HasLineContaining("SafeModeGate suspended=yes reason=repeated re-confirmed conflicts"),
                "SM04 audit line carries the reason");

            // ---- SM05: provider fault => fail-closed -------------------------------
            FreshSetup(delegate { throw new InvalidOperationException("boom"); }, null);
            Check(SafeModeGate.Tick(1000), "SM05 provider fault suspends on FIRST tick (fail-closed)");
            Check(SafeModeGate.Tick(2000), "SM05 provider fault suspends on later ticks");
            Check(SafeModeGate.Suspended, "SM05 suspension readback sticky");

            // ---- SM06+SM12: internal gate fault => fail-closed, caller survives ----
            // A reason-provider fault must not crash the gate nor un-suspend.
            string throwReason = null;
            FreshSetup(delegate { return true; }, delegate { if (throwReason == null) throw new InvalidOperationException("r"); return throwReason ?? ""; });
            Check(SafeModeGate.Tick(1000), "SM06 latched gate suspends despite reason fault");
            Check(SafeModeGate.Suspended, "SM06 suspension state intact after reason fault");

            // ---- SM07: idempotent latch never re-counts -----------------------------
            FreshSetup(delegate { return ConflictEngine.SafeMode; }, delegate { return ConflictEngine.SafeModeReason; });
            LatchEngineSafeMode("IdempotentMod");
            Check(ConflictEngine.SafeMode, "SM07 engine latched");
            Check(SafeModeGate.Tick(1000), "SM07 gate suspended after first-observation latch");
            Check(SafeModeGate.SuspensionCount == 0, "SM07 first-observation-latched gate counts no edge (sticky state, no transition)");
            Check(SafeModeGate.Tick(2000) && SafeModeGate.Tick(3000), "SM07 steady-state ticks stay suspended");
            Check(SafeModeGate.SuspensionCount == 0, "SM07 steady state never re-counts");

            // ---- SM08: audit line shape on a clean session ---------------------------
            FreshSetup(delegate { return false; }, null);
            SafeModeGate.Tick(1000);
            Check(HasLineContaining("SafeModeGate suspended=no"), "SM08 clean session emits suspended=no");

            // ---- SM09: re-emit only after interval while suspended -------------------
            FreshSetup(delegate { return true; }, delegate { return "r9"; });
            SafeModeGate.Tick(1000);   // edge + first line
            int afterFirst = s_Lines.Count;
            SafeModeGate.Tick(30000);
            SafeModeGate.Tick(60000);
            Check(s_Lines.Count == afterFirst, "SM09 stable suspension within interval does not re-emit");
            SafeModeGate.Tick(1000 + 60000);
            Check(s_Lines.Count == afterFirst + 1, "SM09 re-emit after ReemitMs");

            // ---- SM10: status lines + reset ------------------------------------------
            FreshSetup(null, null);
            SafeModeGate.Tick(1000);
            List<string> st = SafeModeGate.StatusLines();
            Check(st.Count == 2, "SM10 status surface bounded (2 lines)");
            Check(st[0].IndexOf("SafeModeGate: suspended=no", StringComparison.Ordinal) == 0, "SM10 summary shape");
            Check(st[1].IndexOf("covers", StringComparison.Ordinal) > 0, "SM10 coverage line present");
            SafeModeGate.ResetForTests();
            Check(!SafeModeGate.Suspended && SafeModeGate.SuspensionCount == 0 && SafeModeGate.TicksTotal == 0
                && SafeModeGate.TicksSuspended == 0, "SM10 reset clears everything");

            // ---- SM11: engine integration (production seam, end-to-end) --------------
            FreshSetup(delegate { return ConflictEngine.SafeMode; }, delegate { return ConflictEngine.SafeModeReason; });
            Check(!SafeModeGate.Tick(500), "SM11 pre-latch gate passes");
            LatchEngineSafeMode("LoopMod");
            Check(ConflictEngine.SafeMode, "SM11 engine latched after 3 reconfirmations");
            Check(SafeModeGate.Tick(1000), "SM11 gate suspends via the real engine latch");
            Check(SafeModeGate.Reason.Length > 0, "SM11 reason flows from the engine");
            Check(HasLineContaining("CompatibilitySafeMode enabled"), "SM11 engine latch audited");
            Check(HasLineContaining("SafeModeGate suspended=yes"), "SM11 gate suspension audited");

            Console.WriteLine("");
            Console.WriteLine("SAFEMODE_GATE_TESTS passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}