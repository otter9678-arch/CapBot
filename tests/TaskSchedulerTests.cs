// Dev-side unit tests for the Phase 4 task scheduler domain (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against TaskState.cs, CapBotTask.cs, TaskRegistry.cs, TaskRecovery.cs,
// TaskRecoveryManager.cs, TaskScheduler.cs. Time is virtual (explicit nowMs
// offsets relative to each task's real CreatedTimeMs) — no real clock reads,
// no sleeping. Isolation resets registry + recovery manager + scheduler, and
// the decision listener feeds a capture list so ordering/refusals are
// asserted from the scheduler's own decision stream.
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;

namespace CapBot.TaskTests
{
    internal static class TaskSchedulerTests
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

        // Isolation: fresh registry + recovery + scheduler, a decision listener
        // capturing every scheduler line, and a registry listener mirroring
        // TaskLogBridge's Track-on-register (recovery stays inert unless Tick).
        private static void FreshSetup()
        {
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            s_Probe = new ScriptedProbe();
            TaskRecoveryManager.Probe = s_Probe;
            s_Lines.Clear();
            TaskScheduler.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            TaskRegistry.SetTransitionListener(delegate (CapBotTask t, string label)
            { if (label == "Registered") TaskRecoveryManager.Track(t); });
        }

        private static CapBotTask NewQueued(string owner, int priority, long[] deps)
        {
            CapBotTask t = CapBotTask.Create("SCHED_TYPE", owner, "scheduler test", priority, 2, -1, null, null, deps);
            TaskRegistry.Register(t);
            t.TryQueue();
            return t;
        }

        private static CapBotTask NewRunning(string owner, int priority, bool preemptible)
        {
            CapBotTask t = CapBotTask.Create("SCHED_TYPE", owner, "scheduler test", priority, 2, -1, null, null, null);
            TaskRegistry.Register(t);
            t.TryQueue();
            t.TryStart();
            if (preemptible) t.SetMetadata("Preemptible", "true");
            return t;
        }

        // Grant order as emitted by the scheduler's decision stream.
        private static List<long> GrantedIds()
        {
            List<long> ids = new List<long>();
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].StartsWith("Granted #", StringComparison.Ordinal))
                {
                    int start = "Granted #".Length;
                    int end = s_Lines[i].IndexOf(' ', start);
                    ids.Add(long.Parse(s_Lines[i].Substring(start, end - start)));
                }
            }
            return ids;
        }

        private static int CountLines(string prefix)
        {
            int n = 0;
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].StartsWith(prefix, StringComparison.Ordinal)) n++;
            }
            return n;
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        internal static int Run()
        {
            // ---- deterministic ordering: equal priority => FCFS / TaskId ------
            FreshSetup();
            CapBotTask a1 = NewQueued("BOT:1", 5, null);
            CapBotTask a2 = NewQueued("BOT:2", 5, null);
            CapBotTask a3 = NewQueued("BOT:3", 5, null);
            Check(a1.TaskId < a2.TaskId && a2.TaskId < a3.TaskId, "task ids monotonic with creation");
            int v = a1.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 1000) == 3, "all three equal-priority tasks granted in one pass");
            List<long> order = GrantedIds();
            Check(order.Count == 3 && order[0] == a1.TaskId && order[1] == a2.TaskId && order[2] == a3.TaskId,
                "equal-priority order deterministic: FCFS with TaskId tie-break");
            s_Lines.Clear();
            TaskScheduler.Tick(v + 7000); // first leases expired -> same pool again
            List<long> order2 = GrantedIds();
            Check(order2.Count == 3 && order2[0] == a1.TaskId && order2[1] == a2.TaskId && order2[2] == a3.TaskId,
                "ordering repeatable across passes (same snapshot -> same order)");

            // ---- deterministic ordering: higher priority first ----------------
            FreshSetup();
            CapBotTask lo = NewQueued("BOT:1", 1, null);
            CapBotTask hi = NewQueued("BOT:2", 9, null);
            TaskScheduler.Tick(lo.CreatedTimeMs + 1000);
            order = GrantedIds();
            Check(order.Count == 2 && order[0] == hi.TaskId, "higher priority granted before lower");

            // ---- bounded aging (visible through effPri in the grant line) -----
            FreshSetup();
            CapBotTask age = NewQueued("BOT:1", 0, null);
            v = age.CreatedTimeMs;
            TaskScheduler.Tick(v + 35000);
            Check(HasLineContaining("effPri=1"), "aging +1 after 30s waited (effPri=1)");
            s_Lines.Clear();
            TaskScheduler.Tick(v + 125000);
            Check(HasLineContaining("effPri=4"), "aging accumulates 1/30s (effPri=4)");
            s_Lines.Clear();
            TaskScheduler.Tick(v + 400000);
            Check(HasLineContaining("effPri=" + (0 + TaskScheduler.MaxAgingBonus)), "aging capped at +" + TaskScheduler.MaxAgingBonus);

            // ---- dependency gate ----------------------------------------------
            FreshSetup();
            CapBotTask dep = NewQueued("BOT:9", 5, null);
            dep.TryStart();
            dep.TryComplete();
            Check(dep.IsTerminal && TaskRegistry.LiveCount == 0, "completed dependency left the live set");
            CapBotTask dependent = NewQueued("BOT:1", 5, new long[] { dep.TaskId });
            v = dependent.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 1000) == 1, "dependency Completed (registry history) -> dependent granted");
            Check(TaskScheduler.HasLease(dependent.TaskId), "dependent holds the grant");

            FreshSetup();
            CapBotTask pending = NewQueued("BOT:9", 5, null); // stays Queued, not Completed
            CapBotTask d2 = NewQueued("BOT:1", 5, new long[] { pending.TaskId });
            v = d2.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 1000) == 1, "blocked candidate refused, sibling still granted");
            Check(TaskScheduler.HasLease(pending.TaskId) && !TaskScheduler.HasLease(d2.TaskId),
                "dependency-blocked task not scheduled");
            Check(CountLines("Refuse #" + d2.TaskId + " dependencies unmet") == 1, "dependency refusal logged");

            FreshSetup();
            CapBotTask d3 = NewQueued("BOT:1", 5, new long[] { 999999999 });
            TaskScheduler.Tick(d3.CreatedTimeMs + 1000);
            Check(!TaskScheduler.HasLease(d3.TaskId), "unresolvable dependency never scheduled (P3 contract)");

            FreshSetup();
            CapBotTask edep = CapBotTask.Create("SCHED_TYPE", "BOT:9", "dep", 5, 2, 100, null, null, null);
            TaskRegistry.Register(edep);
            edep.TryQueue();
            CapBotTask d4 = NewQueued("BOT:1", 5, new long[] { edep.TaskId });
            v = d4.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 200) == 0, "expired-dependency chain: nothing granted");
            Check(edep.State == TaskState.Queued, "scheduler never expires the dead dependency itself");
            Check(TaskRegistry.SweepExpired(v + 400) == 1, "recovery's SweepExpired owns expiring");
            Check(edep.State == TaskState.Expired, "dependency expired by sweep");
            s_Lines.Clear();
            TaskScheduler.Tick(v + 1500);
            Check(!TaskScheduler.HasLease(d4.TaskId), "Expired dependency still blocks its dependent");

            // ---- terminal tasks are not rescheduled ---------------------------
            FreshSetup();
            CapBotTask done = NewQueued("BOT:1", 5, null);
            done.TryStart();
            done.TryComplete();
            CapBotTask canc = NewQueued("BOT:2", 5, null);
            canc.TryCancel("no longer needed");
            CapBotTask fail = NewQueued("BOT:3", 5, null);
            fail.TryStart();
            fail.TryFail("boom");
            Check(TaskRegistry.LiveCount == 1, "terminal tasks left the live set (Failed stays live)");
            v = fail.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 1000) == 0, "no grants for completed/cancelled/failed tasks");
            Check(fail.State == TaskState.Failed, "scheduler never retries or resurrects failed tasks");
            Check(TaskRegistry.Get(done.TaskId) != null && TaskRegistry.Get(done.TaskId).State == TaskState.Completed,
                "completed task resolvable from history but not schedulable");

            // ---- deadline: scheduler refuses but never expires ----------------
            FreshSetup();
            CapBotTask dead = CapBotTask.Create("SCHED_TYPE", "BOT:1", "deadline", 5, 2, 100, null, null, null);
            TaskRegistry.Register(dead);
            dead.TryQueue();
            v = dead.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 200) == 0, "deadline-elapsed task refused");
            Check(CountLines("Refuse #" + dead.TaskId + " deadline elapsed") == 1, "deadline refusal logged");
            Check(dead.State == TaskState.Queued, "scheduler never expires (recovery owns expiring)");
            Check(TaskRegistry.SweepExpired(v + 400) == 1, "SweepExpired expires it afterwards");

            // ---- recovery interaction: backoff-pending Queued is grantable ----
            FreshSetup();
            CapBotTask r1 = CapBotTask.Create("SCHED_TYPE", "BOT:1", "backoff", 5, 2, -1, null, null, null);
            TaskRegistry.Register(r1);
            r1.TryQueue();
            r1.TryStart();
            r1.TryFail("attempt failed");
            Check(r1.TryRetry(), "recovery-style retry moves Failed -> Queued");
            Check(r1.RetryCount == 1 && r1.State == TaskState.Queued, "mid-retry task Queued with RetryCount 1/2");
            v = r1.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 1000) == 1, "backoff-pending Queued task is grantable (no retry gate)");

            FreshSetup();
            CapBotTask r2 = CapBotTask.Create("SCHED_TYPE", "BOT:1", "final retry", 5, 1, -1, null, null, null);
            TaskRegistry.Register(r2);
            r2.TryQueue();
            r2.TryStart();
            r2.TryFail("attempt failed");
            r2.TryRetry();
            Check(r2.RetryCount == r2.MaxRetries && r2.State == TaskState.Queued, "final-retry attempt Queued with full counters");
            Check(TaskScheduler.Tick(r2.CreatedTimeMs + 1000) == 1, "final-retry attempt still schedulable");

            // ---- recovery interaction: scheduler touches only its own pauses --
            FreshSetup();
            CapBotTask cap = CapBotTask.Create("SCHED_TYPE", "BOT:1", "cap", 5, 2, -1, null, null, null);
            TaskRegistry.Register(cap);
            cap.TryQueue();
            cap.TryStart();
            v = cap.CreatedTimeMs;
            s_Probe.Capability = false;
            TaskRecoveryManager.Tick(v + 1000);
            Check(cap.State == TaskState.Paused, "recovery capability-paused the running task");
            TaskScheduler.Tick(v + 20000);
            Check(cap.State == TaskState.Paused, "scheduler never auto-resumes recovery's capability-pause");
            s_Probe.Capability = true;
            TaskRecoveryManager.Tick(v + 3000);
            Check(cap.State == TaskState.Running, "recovery resumes its own capability-pause");

            FreshSetup();
            CapBotTask ext = NewQueued("BOT:1", 5, null);
            ext.TryStart();
            ext.TryPause();
            v = ext.CreatedTimeMs;
            TaskScheduler.Tick(v + 20000);
            Check(ext.State == TaskState.Paused, "scheduler never auto-resumes external pauses");

            // ---- owner gate: lease busy + Running busy ------------------------
            FreshSetup();
            CapBotTask o1 = NewQueued("BOT:1", 5, null);
            CapBotTask o2 = NewQueued("BOT:1", 5, null);
            v = o1.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 1000) == 1, "one grant per owner per pass");
            Check(TaskScheduler.HasLease(o1.TaskId) && !TaskScheduler.HasLease(o2.TaskId),
                "second same-owner task not granted (lease busy)");
            Check(CountLines("Refuse #" + o2.TaskId + " owner busy: BOT:1") == 1, "owner-busy refusal logged");

            FreshSetup();
            CapBotTask run = NewRunning("BOT:1", 5, false);
            CapBotTask o3 = NewQueued("BOT:1", 5, null);
            v = run.CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 1000) == 0, "owner with Running task is busy (execution outlasts the lease)");
            Check(CountLines("Refuse #" + o3.TaskId + " owner busy: BOT:1") == 1, "Running-busy refusal logged");
            run.TryComplete();
            s_Lines.Clear();
            Check(TaskScheduler.Tick(v + 3000) == 1, "owner freed on completion -> queued task granted");
            Check(TaskScheduler.HasLease(o3.TaskId), "freed owner's queued task holds the grant");

            // ---- bounded behavior: grants/pass, 1s gate, Enabled --------------
            FreshSetup();
            List<CapBotTask> many = new List<CapBotTask>();
            for (int i = 0; i < TaskScheduler.MaxGrantsPerTick + 2; i++)
            {
                many.Add(NewQueued("BOT:" + i, 5, null));
            }
            v = many[0].CreatedTimeMs;
            Check(TaskScheduler.Tick(v + 1000) == TaskScheduler.MaxGrantsPerTick,
                "grants per pass bounded by MaxGrantsPerTick");
            Check(TaskScheduler.ActiveGrantCount == TaskScheduler.MaxGrantsPerTick, "active grants bounded");
            Check(TaskScheduler.Tick(v + 2000) == 2, "remainder granted on the next pass (no starvation)");
            Check(TaskScheduler.Tick(v + 4000) == 0, "nothing re-granted while leases live (duplicate prevention)");
            s_Lines.Clear();
            Check(TaskScheduler.Tick(v + 4500) == 0, "recheck gate: Tick <1s after last pass is a no-op");

            FreshSetup();
            CapBotTask e1 = NewQueued("BOT:1", 5, null);
            v = e1.CreatedTimeMs;
            TaskScheduler.Enabled = false;
            Check(TaskScheduler.Tick(v + 1000) == 0, "disabled scheduler grants nothing");
            TaskScheduler.Enabled = true;
            Check(TaskScheduler.Tick(v + 2000) == 1, "re-enabled scheduler grants again");

            // ---- lease model: claim seam, double claim, expiry re-grant -------
            FreshSetup();
            CapBotTask lt = NewQueued("BOT:1", 5, null);
            v = lt.CreatedTimeMs;
            TaskScheduler.Tick(v + 1000);
            Check(TaskScheduler.HasLease(lt.TaskId), "grant -> lease held");
            Check(lt.State == TaskState.Queued && lt.StartedTimeMs == -1,
                "grant is bookkeeping only: task state untouched");
            Check(TaskScheduler.TryTakeLease(lt.TaskId, v + 2000), "executor claims the lease");
            Check(!TaskScheduler.HasLease(lt.TaskId), "claim consumed the lease");
            Check(!TaskScheduler.TryTakeLease(lt.TaskId, v + 2000), "double claim rejected");
            Check(CountLines("GrantLeaseTaken #" + lt.TaskId) == 1, "lease-taken logged exactly once");
            Check(TaskScheduler.Tick(v + 3000) == 1, "task re-grantable after lease consumed (state still Queued)");

            FreshSetup();
            CapBotTask lt2 = NewQueued("BOT:1", 5, null);
            v = lt2.CreatedTimeMs;
            TaskScheduler.Tick(v + 1000);
            Check(TaskScheduler.HasLease(lt2.TaskId), "grant -> lease held (expiry block)");
            s_Lines.Clear();
            TaskScheduler.Tick(v + 7000);   // lease window (v+6000) passed
            Check(TaskScheduler.HasLease(lt2.TaskId), "expired lease frees the task for re-granting");
            Check(CountLines("LeaseExpired #" + lt2.TaskId) == 1, "lease expiry logged");
            Check(CountLines("Granted #" + lt2.TaskId) == 1, "re-granted after lease expiry");

            FreshSetup();
            CapBotTask lt3 = NewQueued("BOT:1", 5, null);
            v = lt3.CreatedTimeMs;
            TaskScheduler.Tick(v + 1000);
            Check(!TaskScheduler.TryTakeLease(lt3.TaskId, v + 6000), "claim on expired lease rejected");
            Check(!TaskScheduler.HasLease(lt3.TaskId), "expired lease removed by claim attempt");

            // ---- status snapshot ----------------------------------------------
            FreshSetup();
            CapBotTask st = NewQueued("BOT:7", 5, null);
            v = st.CreatedTimeMs;
            TaskScheduler.Tick(v + 1000);
            List<string> lines = TaskScheduler.SchedulerStatusLines(v + 2000);
            Check(lines.Count == 1 && lines[0].StartsWith(st.TaskId + "|lease|BOT:7|") && lines[0].EndsWith("ms"),
                "status line format id|lease|owner|remainMs");

            // ---- preemption: full policy path ---------------------------------
            FreshSetup();
            CapBotTask victim = NewRunning("BOT:1", 3, true);
            v = victim.CreatedTimeMs;
            CapBotTask cand = CapBotTask.Create("SCHED_TYPE", "BOT:1", "preemptor", 6, 2, -1, null, null, null);
            TaskRegistry.Register(cand);
            cand.TryQueue();
            Check(TaskScheduler.Tick(v + 4000) == 1, "preemption pass grants the high-priority candidate");
            Check(victim.State == TaskState.Paused, "victim lifecycle-enforced to Paused");
            Check(TaskScheduler.HasLease(cand.TaskId), "candidate granted after preemption");
            Check(HasLineContaining("Preempted #" + victim.TaskId), "preemption logged");
            List<string> stl = TaskScheduler.SchedulerStatusLines(v + 4000);
            Check(stl.Exists(delegate (string l) { return l.StartsWith(victim.TaskId + "|preempted x1", StringComparison.Ordinal); }),
                "preemption record x1");

            // auto-resume at +15s, then immediate re-preemption (count 2), then
            // lifetime cap blocks a third, and the resumed record is dormant.
            s_Lines.Clear();
            TaskScheduler.Tick(v + 19000);
            Check(HasLineContaining("PreemptResume #" + victim.TaskId), "scheduler resumed its own preemption-pause at +15s");
            Check(HasLineContaining("Preempted #" + victim.TaskId), "dominant candidate re-preempts after resume");
            Check(victim.State == TaskState.Paused, "victim paused again by second preemption");
            s_Lines.Clear();
            TaskScheduler.Tick(v + 34000);
            Check(HasLineContaining("PreemptResume #" + victim.TaskId), "second pause auto-resumed");
            Check(victim.State == TaskState.Running, "victim running after second auto-resume");
            Check(TaskScheduler.Tick(v + 36000) == 0, "lifetime cap: third preemption refused, nothing granted");
            Check(victim.State == TaskState.Running, "victim not paused beyond MaxPreemptionsPerTask");
            Check(TaskScheduler.SchedulerStatusLines(v + 36000).Exists(delegate (string l)
            { return l.StartsWith(victim.TaskId + "|preempted x" + TaskScheduler.MaxPreemptionsPerTask, StringComparison.Ordinal); }),
                "preemption tally survives auto-resume (dormant record)");

            FreshSetup();
            CapBotTask fp = NewRunning("BOT:1", 3, true);
            v = fp.CreatedTimeMs;
            CapBotTask fc = CapBotTask.Create("SCHED_TYPE", "BOT:1", "preemptor", 6, 2, -1, null, null, null);
            TaskRegistry.Register(fc);
            fc.TryQueue();
            TaskScheduler.Tick(v + 4000);      // preempt -> Paused
            Check(fp.State == TaskState.Paused, "first preemption applied (foreign-pause scenario)");
            fc.TryCancel("dominant task gone"); // nothing dominant remains -> resume is clean
            TaskScheduler.Tick(v + 19000);     // auto-resume -> Running
            Check(fp.State == TaskState.Running, "auto-resumed after dominant candidate cancelled");
            s_Lines.Clear();
            Check(fp.TryPause(), "another system pauses the resumed task (foreign pause)");
            TaskScheduler.Tick(v + 40000);
            Check(fp.State == TaskState.Paused, "scheduler never auto-resumes a foreign pause on a once-preempted task");
            Check(!HasLineContaining("PreemptResume #" + fp.TaskId), "no resume emitted for the foreign pause");
            Check(TaskScheduler.SchedulerStatusLines(v + 40000).Exists(delegate (string l)
            { return l.StartsWith(fp.TaskId + "|preempted x1", StringComparison.Ordinal); }),
                "preemption tally survives as dormant record");

            // ---- preemption policy gates --------------------------------------
            FreshSetup(); // margin insufficient
            CapBotTask m1 = NewRunning("BOT:1", 5, true);
            CapBotTask m2 = CapBotTask.Create("SCHED_TYPE", "BOT:1", "cand", 7, 2, -1, null, null, null);
            TaskRegistry.Register(m2);
            m2.TryQueue();
            TaskScheduler.Tick(m1.CreatedTimeMs + 4000);
            Check(m1.State == TaskState.Running && !TaskScheduler.HasLease(m2.TaskId),
                "margin <= PreemptMargin: no preemption");

            FreshSetup(); // min runtime not reached
            CapBotTask m3 = NewRunning("BOT:1", 3, true);
            CapBotTask m4 = CapBotTask.Create("SCHED_TYPE", "BOT:1", "cand", 6, 2, -1, null, null, null);
            TaskRegistry.Register(m4);
            m4.TryQueue();
            TaskScheduler.Tick(m3.CreatedTimeMs + 2000);
            Check(m3.State == TaskState.Running && !TaskScheduler.HasLease(m4.TaskId),
                "victim keeps PreemptMinRunMs grace");

            FreshSetup(); // not marked preemptible
            CapBotTask m5 = NewRunning("BOT:1", 3, false);
            CapBotTask m6 = CapBotTask.Create("SCHED_TYPE", "BOT:1", "cand", 9, 2, -1, null, null, null);
            TaskRegistry.Register(m6);
            m6.TryQueue();
            TaskScheduler.Tick(m5.CreatedTimeMs + 4000);
            Check(m5.State == TaskState.Running && !TaskScheduler.HasLease(m6.TaskId),
                "default (unmarked) running task is never preempted");

            FreshSetup(); // cross-owner: candidate simply grants normally, victim untouched
            CapBotTask x1 = NewRunning("BOT:1", 3, true);
            CapBotTask x2 = CapBotTask.Create("SCHED_TYPE", "BOT:2", "cand", 9, 2, -1, null, null, null);
            TaskRegistry.Register(x2);
            x2.TryQueue();
            TaskScheduler.Tick(x1.CreatedTimeMs + 4000);
            Check(x1.State == TaskState.Running && x2.OwnerActorId != x1.OwnerActorId
                && TaskScheduler.HasLease(x2.TaskId),
                "cross-owner candidate grants normally (same-owner displacement only)");

            FreshSetup(); // lowest-efficiency victim chosen among two
            CapBotTask vHi = NewRunning("BOT:1", 4, true);
            CapBotTask vLo = NewRunning("BOT:1", 2, true);
            CapBotTask pick = CapBotTask.Create("SCHED_TYPE", "BOT:1", "cand", 7, 2, -1, null, null, null);
            TaskRegistry.Register(pick);
            pick.TryQueue();
            TaskScheduler.Tick(vLo.CreatedTimeMs + 4000);
            Check(vLo.State == TaskState.Paused && vHi.State == TaskState.Running,
                "lowest effective-priority victim displaced");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}