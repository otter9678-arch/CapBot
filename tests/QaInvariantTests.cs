// Dev-side unit tests for the Phase 32 QA invariant regression (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1.
// Time is virtual: every timestamp is an explicit nowMs argument.
//
// Covers the Phase 32 mandated scenarios (master-prompt invariant census):
//   QA01 authority deny-by-default across ALL authority seams
//   QA02 LLM advisory-only (vocabulary gate; advice never reinterpreted)
//   QA03 bounded-state census (all documented bounds within limits)
//   QA04 pipeline round trip end-to-end w/ duplicate protection
//   QA05 recovery invariants (backoff monotonic, budget bounded)
//   QA06 persistence live-wins invariant (capture -> restore)
//   QA07 cross-system determinism (identical timelines => identical reports)
//   QA08 no-fabrication (invalid inputs refused, never coerced)
//   QA09 updater chain integrity (honest passes, tampered refuses)
//   QA10 perf gate invariants (stamp moves only on arm/Commit)
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;
using CapBot.Core.Compatibility;
using CapBot.Core.Persistence;
using CapBot.Core.Update;
using CapBot.Core.Perf;
using CapBot.Core.Ollama;
using CapBot.Core.Executor;
using CapBot.Core.Diagnostics;

namespace CapBot.TaskTests
{
    internal static class QaInvariantTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        private static void FreshSetup()
        {
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            TaskExecutor.ResetForTests();
            WorldStateService.ResetForTests();
            CrewAgentRegistry.ResetForTests();
            CrewPersonalityRegistry.ResetForTests();
            CrewExperienceRegistry.ResetForTests();
            CrewMemorySystem.ResetForTests();
            MultiplayerAuthorityMonitor.ResetForTests();
            CompatManager.ResetForTests();
            SceneScanGate.ResetForTests();
        }

        private static string Id(int n)
        {
            return "AGT:" + n.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static int Run()
        {
            // ---- QA01: authority deny-by-default across ALL seams ----------------
            FreshSetup();
            // Claims: fresh state => deny.
            CapBotTask t1 = CapBotTask.Create("TEST", "BOT:1", "qa deny", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(t1);
            t1.TryQueue();
            t1.TryStart();
            Check(ExecutionClaims.TryClaim(t1.TaskId, "EXECUTE", 0, "EXE", t1.TargetId, 1000)
                == ClaimResult.RejectedNotAuthoritative, "QA01 claims deny-by-default");
            // MP monitor: no probe => no observation, no transition.
            Check(MultiplayerAuthorityMonitor.Observe() == 0, "QA01 monitor without probe is inert");
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
            Check(MultiplayerAuthorityMonitor.Observe() == 0, "QA01 faulting monitor probe => no transition");
            Check(MultiplayerAuthorityMonitor.TransitionCount == 0, "QA01 no transition synthesized on fault");
            // Crew agents: deny-by-default authority.
            CrewAgentRegistry.SetAuthorityProbe(delegate { return false; });
            CrewAgentRegistry.SetNowMsProvider(delegate { return 1000; });
            CrewAgentRegistry.Sync(1000);
            Check(CrewAgentRegistry.AgentCount == 0, "QA01 crew sync denied when authority false");
            // Crew personality restore provenance is always explicit (matured-only channel).
            Check(CrewPersonalityRegistry.RestoreMatured(Id(1), 50, 50, 50, 50, 50, 0),
                "QA01 personality restore works only via explicit source path");

            // ---- QA02: LLM advisory-only ------------------------------------------
            string advice;
            Check(!OllamaAdvisor.ValidateAdvice("I AM THE CAPTAIN NOW", out advice),
                "QA02 non-ADVICE content rejected");
            Check(!OllamaAdvisor.ValidateAdvice("ADVICE:\nINJECT LOG LINE", out advice),
                "QA02 control characters rejected");
            string wellFormed;
            Check(OllamaAdvisor.ValidateAdvice("ADVICE: move to engine (power fault)", out wellFormed)
                && wellFormed == "ADVICE: move to engine (power fault)",
                "QA02 well-formed advice passes verbatim");
            // Oversize advice is bounded, never executed.
            string longAdvice = "ADVICE: " + new string('x', 400);
            Check(OllamaAdvisor.ValidateAdvice(longAdvice, out advice) && advice.Length <= OllamaAdvisor.MaxAdviceLen,
                "QA02 oversize advice bounded at MaxAdviceLen");
            // The advice is data: it round-trips as opaque text (the well-formed
            // value is unchanged; the structural no-dispatch proof is the IL
            // scan in the verify scripts).
            Check(wellFormed.IndexOf("move to engine", StringComparison.Ordinal) >= 0,
                "QA02 advice round-trips as opaque text");

            // ---- QA03: bounded-state census ---------------------------------------
            Check(CrewAgentRegistry.MaxAgents <= 64, "QA03 crew agent bound");
            Check(CrewExperienceRegistry.MaxRecords <= 64, "QA03 experience bound");
            Check(CrewPersonalityRegistry.MaxPersonalities <= 64, "QA03 personality bound");
            Check(CrewMemorySystem.MaxAgents <= 64, "QA03 memory agent bound");
            Check(CrewMemorySystem.MaxMemoriesPerAgent <= 8, "QA03 memory-per-agent bound");
            Check(StatusHub.MaxLines == 128, "QA03 status report bound");
            Check(SceneScanGate.MaxKeys == 32, "QA03 scan-gate key bound");
            Check(UpdatePolicy.MaxDllBytes == 32 * 1024 * 1024, "QA03 updater payload bound");
            Check(CompatManager.MaxActionsBound == 8, "QA03 compat action bound");
            Check(CrewPersistence.MaxEncodedLength == 64 * 1024, "QA03 persistence blob bound");

            // ---- QA04: pipeline round trip end-to-end ------------------------------
            FreshSetup();
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            CapBotTask t4 = CapBotTask.Create("TEST", "BOT:2", "qa pipeline", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(t4);
            t4.TryQueue();
            t4.TryStart();
            string action4 = ExecutionClaims.MakeDefaultActionId(t4, "EXECUTE");
            Check(ExecutionClaims.TryClaim(t4.TaskId, "EXECUTE", 0, "EXE", t4.TargetId, 1000)
                == ClaimResult.Granted, "QA04 claim granted with authority");
            // Duplicate protection: same action id => duplicate refused.
            Check(ExecutionClaims.Ledger.RecordOutcome(action4, ActionOutcome.Succeeded, 1100),
                "QA04 outcome recorded");
            Check(ExecutionClaims.Ledger.Observe(action4) == ActionOutcome.Succeeded, "QA04 ledger sticky");
            // A second claim on the same action id (same task/attempt) is rejected.
            Check(ExecutionClaims.TryClaim(t4.TaskId, "EXECUTE", 0, "EXE", t4.TargetId, 1200)
                == ClaimResult.DuplicateExecutionRejected, "QA04 duplicate execution rejected");

            // ---- QA05: recovery invariants -----------------------------------------
            // BackoffDelayMs: monotonic non-decreasing in retryCount, hard-capped.
            int b0 = TaskRecoveryManager.BackoffDelayMs(0);
            int b1 = TaskRecoveryManager.BackoffDelayMs(1);
            int b2 = TaskRecoveryManager.BackoffDelayMs(2);
            int b10 = TaskRecoveryManager.BackoffDelayMs(10);
            Check(b0 == TaskRecoveryManager.BackoffBaseMs, "QA05 base backoff");
            Check(b0 <= b1 && b1 <= b2 && b2 <= b10, "QA05 backoff monotonic non-decreasing");
            Check(b10 <= TaskRecoveryManager.MaxBackoffMs, "QA05 backoff capped at budget");
            int prev = -1;
            bool monotonic = true;
            for (int i = 0; i < 20; i++)
            {
                int b = TaskRecoveryManager.BackoffDelayMs(i);
                if (b < prev) { monotonic = false; break; }
                prev = b;
            }
            Check(monotonic, "QA05 20-step backoff sequence monotonic");
            Check(TaskRecoveryManager.TrackedCount == 0, "QA05 fresh registry tracks nothing");

            // ---- QA06: persistence live-wins -----------------------------------------
            FreshSetup();
            CrewExperienceRegistry.RecordOutcome(Id(6), CrewAgentRegistry.OutcomeCompleted, 100);
            long liveXp = CrewExperienceRegistry.Get(Id(6)).ExperiencePoints;
            CrewPersistence.CrewDataSnapshot snap = CrewPersistence.Capture();
            byte[] blob = CrewPersistence.Encode(snap);
            CrewExperienceRegistry.ResetForTests();
            CrewExperienceRegistry.RecordOutcome(Id(6), CrewAgentRegistry.OutcomeFailed, 100);  // live differs
            CrewPersistence.Restore(CrewPersistence.Decode(blob));
            Check(CrewExperienceRegistry.Get(Id(6)).TasksFailed == 1 && CrewExperienceRegistry.Get(Id(6)).ExperiencePoints == 2,
                "QA06 live record wins (XP from live outcome, not stale save)");

            // ---- QA07: cross-system determinism ---------------------------------------
            FreshSetup();
            List<string> repA = StatusHub.Collect(1000);
            FreshSetup();
            List<string> repB = StatusHub.Collect(1000);
            Check(repA.Count == repB.Count, "QA07 identical report lengths");
            bool identical = true;
            for (int i = 0; i < repA.Count; i++)
            {
                if (repA[i] != repB[i]) { identical = false; break; }
            }
            Check(identical, "QA07 identical report content from identical timelines");

            // ---- QA08: no-fabrication census --------------------------------------------
            FreshSetup();
            Check(!CrewExperienceRegistry.RestoreRecord("BAD", 10, 0, 0, 0, 0, 0, 1, null, 0, -1, 0),
                "QA08 invalid agent id refused");
            Check(!CrewPersonalityRegistry.RestoreMatured(Id(8), 101, 50, 50, 50, 50, 0),
                "QA08 out-of-range trait refused");
            Check(CrewPersistence.Decode(new byte[] { 1, 2, 3 }) == null, "QA08 garbage blob refused");
            Check(UpdatePolicy.SanitizeDllFileName("..\\evil.dll") == null, "QA08 traversal refused");
            Check(CrewMemorySystem.RestoreRow(Id(9), (MemoryKind)9, 5, "x", null, 0, 0, 0) == false,
                "QA08 unknown memory kind refused");

            // ---- QA09: updater chain integrity (re-assert P30) ---------------------------
            byte[] good = new byte[UpdatePolicy.MinDllBytes];
            good[0] = 0x4D; good[1] = 0x5A;
            string goodSha = UpdatePolicy.ComputeSha256(good);
            Check(UpdatePolicy.ValidateDllBytes(good) == null && UpdatePolicy.VerifySha256(good, goodSha),
                "QA09 honest payload passes the chain");
            byte[] tampered = (byte[])good.Clone();
            tampered[10] ^= 0xFF;
            Check(!UpdatePolicy.VerifySha256(tampered, goodSha), "QA09 tampered payload refuses");
            Check(UpdatePolicy.CheckDownloadUrl("http://github.com/x") == UpdatePolicy.UrlVerdict.RefusedScheme,
                "QA09 plain http refuses");

            // ---- QA10: perf gate invariants ----------------------------------------------
            SceneScanGate.ResetForTests();
            Check(SceneScanGate.Allow("qa", 1000), "QA10 first allow");
            SceneScanGate.Commit("qa", 1000);
            Check(!SceneScanGate.Allow("qa", 1100), "QA10 commit blocks inside interval");
            Check(SceneScanGate.Allow("qa", 1250), "QA10 interval crossing re-allows");
            // Null/empty keys never allocate.
            SceneScanGate.ResetForTests();
            Check(!SceneScanGate.Allow(null, 1000) && SceneScanGate.KeyCount == 0, "QA10 null key never allocates");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}