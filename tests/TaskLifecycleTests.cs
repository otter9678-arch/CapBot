// Dev-side unit tests for the Phase 2 task lifecycle domain (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against TaskState.cs, CapBotTask.cs, TaskRegistry.cs only.
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;

namespace CapBot.TaskTests
{
    internal static class TaskLifecycleTests
    {
        private static int s_Passed;
        private static int s_Failed;

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        private static CapBotTask NewTask(int maxRetries, int timeoutMs, params long[] deps)
        {
            return CapBotTask.Create("TEST_TYPE", "CAPTAIN", "unit test", 5, maxRetries, timeoutMs, "SECTOR", "opaque-ref-1", deps);
        }

        private static int Main()
        {
            // ---- creation / validation -------------------------------------
            Check(CapBotTask.Create(null, "CAPTAIN", "", 1, 0, -1, null, null, null) == null, "create rejects null type");
            Check(CapBotTask.Create(new string('x', 65), "CAPTAIN", "", 1, 0, -1, null, null, null) == null, "create rejects >64-char type");
            Check(CapBotTask.Create("T", null, "", 1, 0, -1, null, null, null) == null, "create rejects null owner");
            Check(CapBotTask.Create("T", "CAPTAIN", "", 1, 11, -1, null, null, null) == null, "create rejects maxRetries>10");
            Check(CapBotTask.Create("T", "CAPTAIN", "", 1, -1, -1, null, null, null) == null, "create rejects negative maxRetries");

            CapBotTask t = NewTask(2, -1);
            Check(t != null, "create succeeds");
            Check(t.State == TaskState.Created, "initial state Created");
            Check(t.TaskId > 0, "taskId positive");
            Check(t.CreatedTimeMs >= 0 && t.StartedTimeMs == -1 && t.CompletedTimeMs == -1, "initial timestamps");
            Check(t.IsTimedOut(TaskClock.NowMs) == false, "no timeout when TimeoutMs<0");

            // dependencies: dedup + bounds
            CapBotTask d = CapBotTask.Create("T", "C", "", 1, 0, -1, null, null, new long[] { 3, 3, 0, -5, 4 });
            Check(d.Dependencies.Count == 2 && d.Dependencies[0] == 3 && d.Dependencies[1] == 4, "deps deduped, nonpositive dropped");
            List<long> many = new List<long>();
            for (long i = 1; i <= 20; i++) many.Add(i);
            CapBotTask d2 = CapBotTask.Create("T", "C", "", 1, 0, -1, null, null, many);
            Check(d2.Dependencies.Count == 16, "deps capped at 16");

            // ---- registry: register + listener ------------------------------
            int listenerEvents = 0;
            TaskRegistry.SetTransitionListener(delegate (CapBotTask task, string label) { listenerEvents++; });
            Check(TaskRegistry.Register(t), "register ok");
            Check(listenerEvents == 1, "register fired listener");
            Check(!TaskRegistry.Register(t), "double register rejected");
            Check(!TaskRegistry.Register(null), "register null rejected");

            // ---- happy path: queue -> run -> complete ------------------------
            Check(!t.TryStart(), "Created->Running illegal");
            Check(!t.TryComplete(), "Created->Completed illegal");
            Check(!t.TryPause(), "Created->Paused illegal");
            Check(t.TryQueue(), "Created->Queued legal");
            Check(!t.TryPause(), "Queued->Paused illegal");
            Check(!t.TryComplete(), "Queued->Completed illegal");
            Check(t.TryStart(), "Queued->Running legal");
            Check(t.StartedTimeMs >= 0, "StartedTimeMs stamped");
            t.SetProgress(0.5f);
            Check(Math.Abs(t.Progress - 0.5f) < 0.001f, "progress stored");
            t.SetProgress(-1f); Check(t.Progress == 0f, "progress clamped low");
            t.SetProgress(2f); Check(t.Progress == 1f, "progress clamped high");
            Check(t.TryPause(), "Running->Paused legal");
            Check(!t.TryComplete(), "Paused->Completed illegal");
            Check(t.TryResume(), "Paused->Running legal");
            t.SetProgress(1f);
            int completedBefore = 0;
            Check(t.TryComplete(), "Running->Completed legal");
            completedBefore = t.CompletedTimeMs;
            Check(t.IsTerminal && t.State == TaskState.Completed, "terminal Completed");
            Check(t.Progress == 1f, "completed progress 1");

            // ---- idempotence: no double completion ---------------------------
            Check(!t.TryComplete(), "second Complete rejected");
            Check(t.CompletedTimeMs == completedBefore, "CompletedTimeMs unchanged");
            Check(t.State == TaskState.Completed, "state still Completed");
            Check(!t.TryCancel("late"), "terminal cannot cancel");
            Check(!t.TryFail("late"), "terminal cannot fail");
            Check(!t.TryExpire(), "terminal cannot expire");
            Check(!t.TryRetry(), "terminal cannot retry");

            // ---- registry bucketing after completion --------------------------
            Check(TaskRegistry.LiveCount == 0, "completed moved out of live");
            Check(TaskRegistry.HistoryCount == 1, "history holds task");
            Check(TaskRegistry.Get(t.TaskId) == t, "Get finds task in history");
            Check(TaskRegistry.Get(999999) == null, "Get unknown returns null");

            // ---- failure + retry ---------------------------------------------
            CapBotTask f = NewTask(2, -1);
            TaskRegistry.Register(f);
            f.TryQueue(); f.TryStart();
            Check(f.TryFail("attempt one"), "Running->Failed legal");
            Check(f.State == TaskState.Failed && f.FailureReason == "attempt one", "failure reason stored");
            Check(!f.TryStart(), "Failed->Running illegal");
            Check(!f.TryFail("again"), "Failed->Failed illegal");
            int completedAfterFail = f.CompletedTimeMs;
            Check(completedAfterFail >= 0, "Failed stamped terminal time");
            Check(f.TryRetry(), "retry 1 legal");
            Check(f.State == TaskState.Queued && f.RetryCount == 1, "retry moved to Queued, counter up");
            Check(f.CompletedTimeMs == -1, "retry cleared terminal stamp");
            Check(f.FailureReason == "attempt one", "failure reason kept across retry");
            Check(f.StartedTimeMs >= 0, "StartedTimeMs kept across retry");
            f.TryStart();
            Check(f.TryFail("attempt two"), "fail 2");
            Check(f.TryRetry(), "retry 2 legal (max 2)");
            f.TryStart();
            Check(f.TryFail("attempt three"), "fail 3");
            Check(!f.TryRetry(), "retry beyond MaxRetries rejected");
            Check(f.RetryCount == 2, "retry count capped");
            Check(f.TryCancel("giving up"), "Failed->Cancelled legal");
            Check(f.IsTerminal && f.CancellationReason == "giving up", "cancel reason stored");
            Check(!f.TryRetry(), "cancelled task cannot retry");

            // ---- cancellation from Created -----------------------------------
            CapBotTask c = NewTask(0, -1);
            TaskRegistry.Register(c);
            Check(c.TryCancel("never needed"), "Created->Cancelled legal");
            Check(!c.TryQueue(), "cancelled cannot queue");

            // ---- expiry -------------------------------------------------------
            CapBotTask e = CapBotTask.Create("T", "C", "", 1, 0, 10, null, null, null);
            TaskRegistry.Register(e);
            int created = e.CreatedTimeMs;
            Check(!e.IsTimedOut(created), "not timed out at creation instant");
            Check(e.IsTimedOut(created + 11), "timed out after deadline");
            int swept = TaskRegistry.SweepExpired(created + 12);
            Check(swept == 1, "sweep expired one task");
            Check(e.State == TaskState.Expired && e.CancellationReason == "timeout", "expiry state + reason");
            Check(!e.IsTimedOut(created + 13), "terminal task no longer timed out");
            Check(TaskRegistry.LiveCount == 0 || !ContainsLive(e), "expired left live set");

            // ---- live cap: no eviction, registration fails at cap -------------
            TaskRegistry.ResetForTests();
            listenerEvents = 0;
            TaskRegistry.SetTransitionListener(delegate (CapBotTask task, string label) { listenerEvents++; });
            CapBotTask first = null;
            int registered = 0;
            for (int i = 0; i < TaskRegistry.MaxLiveTasks; i++)
            {
                CapBotTask x = CapBotTask.Create("LOAD", "HOST", "cap test", 1, 0, -1, null, null, null);
                if (TaskRegistry.Register(x)) { registered++; if (first == null) first = x; }
            }
            Check(registered == TaskRegistry.MaxLiveTasks, "cap count registered");
            CapBotTask overflow = CapBotTask.Create("LOAD", "HOST", "cap test", 1, 0, -1, null, null, null);
            Check(!TaskRegistry.Register(overflow), "registration beyond cap rejected");
            Check(overflow != null, "overflow task still creatable (not evicted)");
            first.TryQueue(); first.TryStart(); first.TryComplete();
            Check(TaskRegistry.LiveCount == TaskRegistry.MaxLiveTasks - 1, "completion freed a live slot");
            Check(TaskRegistry.Register(overflow), "register ok after slot freed");

            // ---- history ring bound -------------------------------------------
            // Fresh registry: the live-cap test above intentionally leaves live
            // tasks occupying slots (registration fails at the cap by design),
            // which would starve this loop's registrations.
            TaskRegistry.ResetForTests();
            listenerEvents = 0;
            TaskRegistry.SetTransitionListener(delegate (CapBotTask task, string label) { listenerEvents++; });
            CapBotTask ringFirst = null;
            CapBotTask hist = null;
            for (int i = 0; i < TaskRegistry.MaxCompletedHistory + 20; i++)
            {
                hist = CapBotTask.Create("RING", "HOST", "ring", 1, 0, -1, null, null, null);
                if (TaskRegistry.Register(hist))
                {
                    hist.TryQueue(); hist.TryStart(); hist.TryComplete();
                    if (ringFirst == null) ringFirst = hist;
                }
            }
            Check(TaskRegistry.HistoryCount == TaskRegistry.MaxCompletedHistory, "history ring bounded");
            Check(TaskRegistry.Get(ringFirst.TaskId) == null, "oldest history evicted");
            Check(TaskRegistry.Get(hist.TaskId) == hist, "reference identity preserved");

            // ---- identity / equality ------------------------------------------
            Check(t.Equals(t), "self equality");
            Check(!t.Equals(f), "different ids not equal");
            Check(!t.Equals(null), "null not equal");
            Check(t.GetHashCode() == t.GetHashCode(), "hash stable");
            Check(TaskRegistry.Get(hist.TaskId) == hist, "reference identity preserved");

            // ---- metadata -------------------------------------------------------
            Check(t.SetMetadata("k", "v"), "metadata set");
            Check(t.GetMetadata("k") == "v", "metadata get");
            Check(!t.SetMetadata(new string('k', 65), "v"), "long key rejected");
            Check(!t.SetMetadata("", "v"), "empty key rejected");
            for (int i = 0; i < 40; i++) t.SetMetadata("k" + i, "v");
            Check(t.Metadata.Count == 32, "metadata capped at 32");

            // ---- status reporting ----------------------------------------------
            string line = t.ToStatusLine(TaskClock.NowMs);
            Check(line.IndexOf("#" + t.TaskId) >= 0 && line.IndexOf("Completed") >= 0, "status line contains id+state");
            List<string> lines = TaskRegistry.StatusLines(TaskClock.NowMs);
            bool sorted = true;
            for (int i = 1; i < lines.Count; i++)
            {
                // ids ascend; compare leading "#<n>"
                long a = ParseId(lines[i - 1]); long b = ParseId(lines[i]);
                if (b < a) { sorted = false; break; }
            }
            Check(sorted, "status lines sorted by id");
            Check(lines.Count == TaskRegistry.LiveCount + TaskRegistry.HistoryCount, "status covers live+history");

            // ---- ids monotonic ---------------------------------------------------
            long i1 = TaskIds.Next(); long i2 = TaskIds.Next();
            Check(i2 == i1 + 1, "ids monotonic");
            Check(TaskIds.Current >= i2, "Current reflects next-1");

            // ---- clock ------------------------------------------------------------
            int now = TaskClock.NowMs;
            Check(TaskClock.DeltaMs(now, now) == 0, "delta self zero");
            Check(TaskClock.DeltaMs(now - 100, now) == 100, "delta forward");

            // ---- pause/resume/cancel on queued task -------------------------------
            CapBotTask q = NewTask(0, -1);
            TaskRegistry.Register(q);
            q.TryQueue();
            Check(!q.TryPause(), "Queued->Paused rejected");
            Check(q.TryCancel("plan change"), "Queued->Cancelled legal");

            TaskRegistry.ResetForTests();
            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed == 0 ? 0 : 1;
        }

        private static bool ContainsLive(CapBotTask task)
        {
            foreach (string line in TaskRegistry.StatusLines(TaskClock.NowMs))
            {
                if (line.IndexOf("#" + task.TaskId + " ") == 0 && line.IndexOf("state=Expired") < 0) return true;
            }
            return false;
        }

        private static long ParseId(string statusLine)
        {
            // lines start with "#<id> "
            int end = statusLine.IndexOf(' ');
            return long.Parse(statusLine.Substring(1, end - 1));
        }
    }
}