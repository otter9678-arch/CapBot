// Dev-side unit tests for the Phase 31 scene-scan gate (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1.
// Time is virtual: every timestamp is an explicit nowMs argument.
//
// Covers the Phase 31 mandated scenarios:
//   PERF01 first call allows and arms
//   PERF02 within-interval refusal (Allow does not self-advance)
//   PERF03 Commit advances the stamp (Allow refuses until interval passes)
//   PERF04 interval crossing re-allows (250ms boundary, exact)
//   PERF05 wrap-aware time deltas (negative->positive crossing)
//   PERF06 bounded keys (MaxKeys fail-closed) + reset
//   PERF07 key isolation (handlers gate independently)
//   PERF08 determinism (same timeline => same allow/commit results)
using System;
using CapBot.Core.Perf;

namespace CapBot.TaskTests
{
    internal static class PerfGateTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        internal static int Run()
        {
            // ---- PERF01: first call allows and arms -----------------------------
            SceneScanGate.ResetForTests();
            Check(SceneScanGate.Allow("colony", 1000), "PERF01 first Allow allows");
            Check(SceneScanGate.KeyCount == 1, "PERF01 key recorded");
            Check(!SceneScanGate.Allow("colony", 1100), "PERF01 immediate re-Allow refuses (armed by first)");
            Check(!SceneScanGate.Allow("colony", 1249), "PERF01 249ms boundary refuses");
            Check(SceneScanGate.Allow("colony", 1250), "PERF04 exactly at 250ms boundary allows");
            // Allow does NOT advance the stamp (engine Commits after scanning),
            // so the next gate boundary is measured from the ARM at 1000 unless
            // the engine committed. Commit at the boundary arms the next window.
            SceneScanGate.Commit("colony", 1250);
            Check(!SceneScanGate.Allow("colony", 1400), "PERF04 Commit at boundary arms the next interval");
            Check(SceneScanGate.Allow("colony", 1500), "PERF04 250ms after the boundary Commit allows again");

            // ---- PERF03: Commit advances the stamp ------------------------------
            SceneScanGate.ResetForTests();
            Check(SceneScanGate.Allow("wastedwing", 1000), "PERF03 first Allow allows");
            SceneScanGate.Commit("wastedwing", 1050);
            Check(!SceneScanGate.Allow("wastedwing", 1200), "PERF03 Allow refuses after Commit within interval");
            Check(SceneScanGate.Allow("wastedwing", 1300), "PERF03 Allow allows at 250ms after Commit");
            // Allow-without-Commit does NOT advance: repeated Allows inside the
            // interval refuse, and once the interval passes WITHOUT a Commit the
            // gate allows again on the next frame (the engine then Commits).
            SceneScanGate.ResetForTests();
            SceneScanGate.Allow("k", 1000);
            Check(!SceneScanGate.Allow("k", 1200), "PERF03 uncommitted Allows do not advance");
            Check(!SceneScanGate.Allow("k", 1249), "PERF03 249ms after first allow refuses");
            Check(SceneScanGate.Allow("k", 1250), "PERF03 interval passed without Commit => allows (engine commits next)");
            SceneScanGate.Commit("k", 1249);
            Check(!SceneScanGate.Allow("k", 1300), "PERF03 Commit at 1249 blocks through 1499");

            // ---- PERF05: wrap-aware deltas ---------------------------------------
            SceneScanGate.ResetForTests();
            SceneScanGate.Allow("wrap", -100);         // armed near int wrap
            Check(SceneScanGate.Allow("wrap", 150), "PERF05 wrap crossing (unchecked delta) allows");
            SceneScanGate.Commit("wrap", 150);
            Check(!SceneScanGate.Allow("wrap", 200), "PERF05 post-wrap refusal within interval");

            // ---- PERF06: bounded keys + reset -------------------------------------
            SceneScanGate.ResetForTests();
            for (int i = 0; i < 32; i++)
            {
                Check(SceneScanGate.Allow("key" + i, i * 10), "PERF06 key " + i + " registered (within bound)");
            }
            Check(SceneScanGate.KeyCount == 32, "PERF06 bound reached exactly");
            Check(!SceneScanGate.Allow("overflow", 1000), "PERF06 over-bound key refused (fail-closed)");
            Check(SceneScanGate.KeyCount == 32, "PERF06 no new key stored on refusal");
            SceneScanGate.Commit("overflow", 200);      // Commit on unknown key also bounded
            Check(SceneScanGate.KeyCount == 32, "PERF06 Commit does not exceed the bound");
            SceneScanGate.ResetForTests();
            Check(SceneScanGate.KeyCount == 0, "PERF06 reset clears all keys");
            Check(SceneScanGate.Allow("colony", 500), "PERF06 post-reset Allow allows again");

            // ---- PERF07: key isolation --------------------------------------------
            SceneScanGate.ResetForTests();
            Check(SceneScanGate.Allow("colony", 1000), "PERF07 colony allowed");
            Check(SceneScanGate.Allow("races", 1000), "PERF07 races independent of colony");
            Check(SceneScanGate.Allow("highrollers", 1000), "PERF07 highrollers independent");
            Check(!SceneScanGate.Allow("colony", 1100), "PERF07 colony gated while others free");
            Check(SceneScanGate.Allow("planetexplore", 1100), "PERF07 planetexplore independent");

            // ---- PERF08: determinism ------------------------------------------------
            SceneScanGate.ResetForTests();
            bool a1 = SceneScanGate.Allow("d", 1000);
            SceneScanGate.Commit("d", 1000);
            bool a2 = SceneScanGate.Allow("d", 1200);
            bool a3 = SceneScanGate.Allow("d", 1300);
            SceneScanGate.ResetForTests();
            SceneScanGate.Allow("d", 1000);
            SceneScanGate.Commit("d", 1000);
            bool b2 = SceneScanGate.Allow("d", 1200);
            bool b3 = SceneScanGate.Allow("d", 1300);
            Check(a1 && a2 == b2 && a3 == b3, "PERF08 identical timeline => identical gate results");

            // Null/empty keys never allow and never allocate (fresh registry).
            SceneScanGate.ResetForTests();
            Check(!SceneScanGate.Allow(null, 1000), "PERF06 null key refused");
            Check(!SceneScanGate.Allow("", 1000), "PERF06 empty key refused");
            Check(SceneScanGate.KeyCount == 0, "PERF06 null/empty keys do not allocate");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}