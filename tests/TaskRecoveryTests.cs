// Dev-side unit tests for the Phase 3 task recovery domain (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against TaskState.cs, CapBotTask.cs, TaskRegistry.cs, TaskRecovery.cs,
// TaskRecoveryManager.cs. Time is virtual (explicit nowMs offsets relative to
// each task's real CreatedTimeMs) — no real clock reads, no sleeping.
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;

namespace CapBot.TaskTests
{
    internal static class TaskRecoveryTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        private sealed class ScriptedProbe : ITaskWorldProbe
        {
            public bool Target = true;
            public bool Owner = true;
            public bool Capability = true;
            public bool Invalidates = false;

            public bool TargetValid(CapBotTask task) { return Target; }
            public bool OwnerAvailable(string ownerActorId) { return Owner; }
            public bool CapabilityAvailable(CapBotTask task) { return Capability; }
            public bool WorldInvalidatesTask(CapBotTask task) { return Invalidates; }
        }

        private static ScriptedProbe s_Probe = new ScriptedProbe();
        private static int s_Actions;

        // Isolation: fresh registry + manager + probe + counting listener, and
        // a registry listener that mirrors TaskLogBridge's Track-on-register.
        private static void FreshSetup()
        {
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            s_Probe = new ScriptedProbe();
            TaskRecoveryManager.Probe = s_Probe;
            s_Actions = 0;
            TaskRecoveryManager.SetActionListener(delegate (CapBotTask t, RecoveryActionType a, string r, bool acted)
            { if (acted) s_Actions++; });
            TaskRegistry.SetTransitionListener(delegate (CapBotTask t, string label)
            { if (label == "Registered") TaskRecoveryManager.Track(t); });
        }

        private static CapBotTask NewTask(int maxRetries, int timeoutMs)
        {
            CapBotTask t = CapBotTask.Create("REC_TYPE", "CAPTAIN", "recovery test", 5, maxRetries, timeoutMs, null, null, null);
            TaskRegistry.Register(t);
            return t;
        }

        internal static int Run()
        {
            // ---- backoff curve ------------------------------------------------
            Check(TaskRecoveryManager.BackoffDelayMs(0) == 2000, "backoff 0 = 2s");
            Check(TaskRecoveryManager.BackoffDelayMs(1) == 4000, "backoff 1 = 4s");
            Check(TaskRecoveryManager.BackoffDelayMs(2) == 8000, "backoff 2 = 8s");
            Check(TaskRecoveryManager.BackoffDelayMs(3) == 16000, "backoff 3 = 16s");
            Check(TaskRecoveryManager.BackoffDelayMs(4) == 30000, "backoff 4 capped at 30s");
            Check(TaskRecoveryManager.BackoffDelayMs(9) == 30000, "backoff stays capped");

            // ---- timeout rule: deadline elapsed -> expire -----------------------
            FreshSetup();
            CapBotTask t = NewTask(2, 100);
            t.TryQueue(); t.TryStart();
            int v = t.CreatedTimeMs;
            Check(TaskRecoveryManager.Tick(v + 200) == 1, "timeout tick executes one action");
            Check(t.State == TaskState.Expired && t.CancellationReason == "timeout", "deadline elapsed -> Expired(timeout)");
            Check(TaskRecoveryManager.TrackedCount == 0, "expired task dropped from tracking");
            Check(TaskRecoveryManager.Tick(v + 1300) == 0, "no action after terminal");
            Check(s_Actions == 1, "expired task produced exactly one action ever");

            // ---- precedence: timeout beats owner-loss ---------------------------
            FreshSetup();
            CapBotTask p = NewTask(2, 100);
            p.TryQueue(); p.TryStart();
            s_Probe.Owner = false;
            TaskRecoveryManager.Tick(p.CreatedTimeMs + 200);
            Check(p.State == TaskState.Expired, "timeout rule wins over owner-loss (precedence)");

            // ---- world-invalidated premise -> cancel ----------------------------
            FreshSetup();
            CapBotTask w = NewTask(2, -1);
            w.TryQueue(); w.TryStart();
            s_Probe.Invalidates = true;
            TaskRecoveryManager.Tick(w.CreatedTimeMs + 2000);
            Check(w.State == TaskState.Cancelled, "world-invalidated -> Cancelled");
            Check(w.CancellationReason != null && w.CancellationReason.IndexOf("world") >= 0, "world invalidation reason recorded");

            // ---- target invalid/lost -> cancel ----------------------------------
            FreshSetup();
            CapBotTask g = NewTask(2, -1);
            g.TryQueue(); g.TryStart();
            s_Probe.Target = false;
            TaskRecoveryManager.Tick(g.CreatedTimeMs + 2000);
            Check(g.State == TaskState.Cancelled, "target lost -> Cancelled");
            Check(g.CancellationReason != null && g.CancellationReason.IndexOf("target") >= 0, "target-loss reason recorded");

            // ---- owner loss: Running -> Fail -> bounded backoff retry -> abandon
            // Backoff anchors on the record's FailedAtMs (set when the manager
            // executes the Fail at +1000), so retry #1 fires at +3000 (2s
            // backoff) and retry #2 at +10000 (4s backoff from the +6000
            // fail). All stamps strictly increasing (the 1s recheck gate
            // rejects backwards time).
            FreshSetup();
            CapBotTask f = NewTask(2, -1);
            f.TryQueue(); f.TryStart();
            v = f.CreatedTimeMs;
            s_Probe.Owner = false;
            TaskRecoveryManager.Tick(v + 1000);
            Check(f.State == TaskState.Failed && f.FailureReason != null && f.FailureReason.IndexOf("owner unavailable") >= 0, "owner loss on Running -> Failed(reason)");
            TaskRecoveryManager.Tick(v + 2000);
            Check(f.State == TaskState.Failed, "backoff not elapsed -> no action");
            TaskRecoveryManager.Tick(v + 3000);
            Check(f.State == TaskState.Queued && f.RetryCount == 1, "backoff elapsed -> Retry to Queued, count 1");
            Check(f.CompletedTimeMs == -1 && f.StartedTimeMs >= 0, "retry cleared terminal stamp, kept StartedTimeMs");
            s_Probe.Owner = true;
            Check(f.TryStart(), "retried task can start (lifecycle intact)");
            s_Probe.Owner = false;
            TaskRecoveryManager.Tick(v + 6000);
            Check(f.State == TaskState.Failed && f.RetryCount == 1, "second failure recorded");
            TaskRecoveryManager.Tick(v + 7000);
            Check(f.State == TaskState.Failed, "second backoff (4s) not elapsed -> no action");
            TaskRecoveryManager.Tick(v + 10000);
            Check(f.State == TaskState.Queued && f.RetryCount == 2, "retry 2/2 after 4s backoff");
            s_Probe.Owner = true;
            f.TryStart();
            s_Probe.Owner = false;
            TaskRecoveryManager.Tick(v + 11000);
            Check(f.State == TaskState.Failed, "third failure recorded");
            TaskRecoveryManager.Tick(v + 12000);
            Check(f.State == TaskState.Cancelled && f.CancellationReason.IndexOf("exhausted") >= 0, "retries exhausted -> abandoned");
            Check(TaskRecoveryManager.TrackedCount == 0, "abandoned task dropped from tracking");
            s_Probe.Owner = true;
            TaskRecoveryManager.Tick(v + 16000);
            Check(s_Actions == 6, "no actions after terminal (no infinite retry loop): Fail,Retry,Fail,Retry,Fail,Cancel");

            // ---- capability: pause / resume / too-long abandon -------------------
            FreshSetup();
            CapBotTask c = NewTask(2, -1);
            c.TryQueue(); c.TryStart();
            v = c.CreatedTimeMs;
            s_Probe.Capability = false;
            TaskRecoveryManager.Tick(v + 1000);
            Check(c.State == TaskState.Paused, "capability down on Running -> Paused");
            TaskRecoveryManager.Tick(v + 2000);
            Check(c.State == TaskState.Paused, "capability still down, not too long -> stays Paused");
            s_Probe.Capability = true;
            TaskRecoveryManager.Tick(v + 3000);
            Check(c.State == TaskState.Running, "capability back -> auto-Resume to Running");
            s_Probe.Capability = false;
            TaskRecoveryManager.Tick(v + 4000);
            Check(c.State == TaskState.Paused, "re-paused when capability drops again");
            TaskRecoveryManager.Tick(v + 65000);
            Check(c.State == TaskState.Cancelled && c.CancellationReason.IndexOf("capability") >= 0, "capability down > 60s -> abandoned");

            // ---- externally-initiated pause is not auto-resumed ------------------
            FreshSetup();
            CapBotTask x = NewTask(2, -1);
            x.TryQueue(); x.TryStart();
            v = x.CreatedTimeMs;
            Check(x.TryPause(), "external pause applied");
            TaskRecoveryManager.Tick(v + 16000);
            Check(x.State == TaskState.Paused, "external pause NOT auto-resumed by recovery");
            s_Probe.Owner = false;
            TaskRecoveryManager.Tick(v + 17000);
            Check(x.State == TaskState.Cancelled, "owner loss on Paused -> Cancelled");

            // ---- stuck detection (no observable progress) -------------------------
            FreshSetup();
            CapBotTask s1 = NewTask(0, -1);
            s1.TryQueue(); s1.TryStart();
            v = s1.CreatedTimeMs;
            TaskRecoveryManager.Tick(v + 16000);
            Check(s1.State == TaskState.Failed && s1.FailureReason.IndexOf("stuck") >= 0, "no progress 15s -> Failed(stuck)");
            TaskRecoveryManager.Tick(v + 20000);
            Check(s1.State == TaskState.Cancelled && s1.CancellationReason.IndexOf("exhausted") >= 0, "MaxRetries=0 -> stuck fail abandons immediately");
            CapBotTask s2 = NewTask(2, -1);
            s2.TryQueue(); s2.TryStart();
            Check(s2.TryPause(), "external pause for stuck-scope test");
            TaskRecoveryManager.Tick(s2.CreatedTimeMs + 40000);
            Check(s2.State == TaskState.Paused, "stuck rule does not fire on Paused tasks");

            // ---- progress observation refreshes the stuck clock -------------------
            FreshSetup();
            CapBotTask pr = NewTask(2, -1);
            pr.TryQueue(); pr.TryStart();
            v = pr.CreatedTimeMs;
            TaskRecoveryManager.Tick(v + 1000);
            TaskRecoveryManager.Tick(v + 9000);
            Check(pr.State == TaskState.Running, "9s without progress is not yet stuck");
            pr.SetProgress(0.6f);
            TaskRecoveryManager.Tick(v + 20000);
            Check(pr.State == TaskState.Running, "observable progress refreshes stuck clock");
            TaskRecoveryManager.Tick(v + 36001);
            Check(pr.State == TaskState.Failed && pr.FailureReason.IndexOf("stuck") >= 0, "stuck fires 16s after last progress change");

            // ---- lifetime recovery budget backstop --------------------------------
            // Loop timeline per iteration: Fail at +1s, backoff 2s -> Retry at
            // +41s (the +40000 jump clears both the backoff and the gate).
            // 6 iterations x 2 actions = 12 non-terminal actions, then the
            // 13th decision is forced to a terminal Cancel (budget backstop).
            FreshSetup();
            CapBotTask b = NewTask(10, -1);
            b.TryQueue(); b.TryStart();
            v = b.CreatedTimeMs;
            s_Probe.Owner = false;
            for (int i = 0; i < 6; i++)
            {
                TaskRecoveryManager.Tick(v += 1000); // Fail (owner down)
                b.TryStart();                         // retry path: manual start (executor's job in later phases)
                TaskRecoveryManager.Tick(v += 40000); // Retry (backoff elapsed)
                b.TryStart();
            }
            Check(b.State == TaskState.Running && s_Actions == 12, "12 non-terminal recovery actions spent");
            TaskRecoveryManager.Tick(v += 1000);
            Check(b.State == TaskState.Cancelled, "budget exhausted -> terminal Cancel");
            Check(b.CancellationReason != null && b.CancellationReason.IndexOf("budget exhausted") >= 0, "budget reason recorded");
            TaskRecoveryManager.Tick(v += 40000);
            Check(s_Actions == 13, "nothing further after budget abandon (13th was the backstop cancel)");

            // ---- recheck gate: one examination per task per second -----------------
            FreshSetup();
            CapBotTask r1t = NewTask(2, -1);
            r1t.TryQueue(); r1t.TryStart();
            v = r1t.CreatedTimeMs;
            s_Probe.Owner = false;
            Check(TaskRecoveryManager.Tick(v + 16000) == 1, "stuck fail executes");
            Check(TaskRecoveryManager.Tick(v + 16500) == 0, "second Tick within 1s is gated off");
            Check(r1t.State == TaskState.Failed, "gated tick changed nothing");

            // ---- disabled manager ---------------------------------------------------
            TaskRecoveryManager.Enabled = false;
            Check(TaskRecoveryManager.Tick(v + 30000) == 0, "disabled manager acts on nothing");
            TaskRecoveryManager.Enabled = true;

            // ---- null probe resolves to NullWorldProbe ------------------------------
            TaskRecoveryManager.Probe = null;
            Check(TaskRecoveryManager.Probe is NullWorldProbe, "null probe replaced with NullWorldProbe");
            CapBotTask n = NewTask(1, -1);
            n.TryQueue(); n.TryStart();
            Check(TaskRecoveryManager.Tick(n.CreatedTimeMs + 20000) >= 0 && n.State == TaskState.Failed, "null probe safe: stuck path still works (lifecycle-only rules)");

            // ---- tracking lifecycle ---------------------------------------------------
            FreshSetup();
            CapBotTask k1 = NewTask(0, -1), k2 = NewTask(0, -1), k3 = NewTask(0, -1);
            Check(TaskRecoveryManager.TrackedCount == 3, "one record per registered task");
            k1.TryQueue(); k1.TryStart(); k1.TryComplete();
            TaskRecoveryManager.Tick(TaskClock.NowMs); // terminal drop is lazy (next Tick)
            Check(TaskRecoveryManager.TrackedCount == 2, "terminal task record dropped");
            TaskRecoveryManager.Forget(k2.TaskId);
            Check(!TaskRecoveryManager.IsTracked(k2.TaskId) && TaskRecoveryManager.IsTracked(k3.TaskId), "Forget + IsTracked");

            // ---- status snapshot ---------------------------------------------------------
            List<string> lines = TaskRecoveryManager.RecoveryStatusLines(TaskClock.NowMs);
            Check(lines.Count == 1 && lines[0].IndexOf("|actions=") > 0, "recovery status lines bounded + formatted");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }

    // Runs all fourteen suites in sequence; run_tests.ps1 gates on the exit code
    // and the final TOTAL line containing failed=0.
    internal static class TestMain
    {
        private static int Main()
        {
            int f1 = TaskLifecycleTests.Run();
            int f2 = TaskRecoveryTests.Run();
            int f3 = TaskSchedulerTests.Run();
            int f4 = ExecutionClaimTests.Run();
            int f5 = WorldStateTests.Run();
            int f6 = CapabilityTests.Run();
            int f7 = ExecutionTests.Run();
            int f8 = EmergencyTests.Run();
            int f9 = CrewAgentTests.Run();
            int f10 = PersonalityTests.Run();
            int f11 = ExperienceTests.Run();
            int f12 = MemoryTests.Run();
            int f13 = NavigationTests.Run();
            int f14 = MissionTests.Run();
            int f15 = EconomyTests.Run();
            int f16 = CombatTests.Run();
            int f17 = CaptainTests.Run();
            int f18 = DecisionValidatorTests.Run();
            int f19 = OllamaAdvisorTests.Run();
            Console.WriteLine("");
            Console.WriteLine("TOTAL passed=" + (TaskLifecycleTests.LastPassed + TaskRecoveryTests.LastPassed + TaskSchedulerTests.LastPassed + ExecutionClaimTests.LastPassed + WorldStateTests.LastPassed + CapabilityTests.LastPassed + ExecutionTests.LastPassed + EmergencyTests.LastPassed + CrewAgentTests.LastPassed + PersonalityTests.LastPassed + ExperienceTests.LastPassed + MemoryTests.LastPassed + NavigationTests.LastPassed + MissionTests.LastPassed + EconomyTests.LastPassed + CombatTests.LastPassed + CaptainTests.LastPassed + DecisionValidatorTests.LastPassed + OllamaAdvisorTests.LastPassed) + " failed=" + (f1 + f2 + f3 + f4 + f5 + f6 + f7 + f8 + f9 + f10 + f11 + f12 + f13 + f14 + f15 + f16 + f17 + f18 + f19));
            return (f1 + f2 + f3 + f4 + f5 + f6 + f7 + f8 + f9 + f10 + f11 + f12 + f13 + f14 + f15 + f16 + f17 + f18 + f19) == 0 ? 0 : 1;
        }
    }
}