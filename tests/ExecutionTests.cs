// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure domain files (Tasks/World/Capabilities/Executor). Time is
// virtual (explicit s_Now, shared with the registry seams) — no real clock
// reads, no sleeping. Covers the Phase 8 executor contract: the 20 mandated
// scenarios plus tick-driver behavior.
using System;
using System.Collections.Generic;
using CapBot.Core.Capabilities;
using CapBot.Core.Executor;
using CapBot.Core.Tasks;

namespace CapBot.TaskTests
{
    internal static class ExecutionTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        // Counting dispatcher: the executor's ONLY dispatch seam in tests.
        private sealed class FakeDispatcher : ICapabilityDispatcher
        {
            public int Calls;
            public ExecutionResult Next;
            public Func<CapabilityRequest, ExecutionResult> Override; // when set, replaces Next

            public ExecutionResult Dispatch(CapabilityDescriptor capability, CapabilityRequest request, CapBotTask task)
            {
                Calls++;
                if (Override != null) return Override(request);
                return Next;
            }
        }

        private static FakeDispatcher s_Dispatcher = new FakeDispatcher();
        private static int s_Now;          // virtual clock (monotonic across the suite)
        private static bool s_Authority;   // claims authority policy answer
        private static List<string> s_Lines = new List<string>();

        // Isolation: fresh registries/managers/seams, built-in catalog,
        // virtual-clock seams wired exactly like production
        // (RegisteredCapabilities.AttachProductionSeams) but on virtual time.
        private static void FreshSetup()
        {
            s_Now += 10000; // monotonic; keeps cooldowns/leases from earlier tests expired
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            CapabilityRegistry.ResetForTests();
            TaskExecutor.ResetForTests();

            RegisteredCapabilities.RegisterBuiltIns();
            CapabilityRegistry.SetNowMsProvider(delegate { return s_Now; });
            CapabilityRegistry.SetAuthorityProbe(delegate { return ExecutionClaims.IsAuthoritative(); });
            CapabilityRegistry.SetWorldProvider(delegate { return null; });
            CapabilityRegistry.SetClaimProbe(delegate (long taskId, string actionId)
            {
                ClaimInfo claim = ExecutionClaims.GetClaim(taskId, s_Now);
                if (claim.Active) return true;
                return ExecutionClaims.Ledger.Observe(actionId) == ActionOutcome.Succeeded;
            });
            s_Authority = true;
            ExecutionClaims.SetAuthorityPolicy(delegate { return s_Authority; });

            s_Dispatcher = new FakeDispatcher();
            TaskExecutor.SetDispatcher(s_Dispatcher);
            s_Lines.Clear();
            TaskExecutor.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
        }

        // Creates, registers, binds a capability via metadata, and queues.
        private static CapBotTask NewQueuedTask(string taskType, string owner, string targetKind, string targetId, string capabilityId)
        {
            CapBotTask t = CapBotTask.Create(taskType, owner, "execution test", 5, 0, -1, targetKind, targetId, null);
            if (capabilityId != null) t.SetMetadata(TaskExecutor.MetadataCapabilityId, capabilityId);
            TaskRegistry.Register(t);
            t.TryQueue();
            return t;
        }

        // Grants the task through the REAL scheduler (the only grant path).
        private static bool GrantViaScheduler(CapBotTask t)
        {
            TaskScheduler.Tick(s_Now);
            return TaskScheduler.HasLease(t.TaskId);
        }

        internal static int Run()
        {
            s_Passed = 0;
            s_Failed = 0;

            SuccessfulExecution();
            UnknownCapability();
            DisabledCapability();
            InvalidTask();
            InvalidOwner();
            InvalidTarget();
            FailedPrecondition();
            WrongAuthority();
            MissingClaim();
            DuplicateClaim();
            DuplicateExecutionRequest();
            StaleCallback();
            CancelledTask();
            CompletedTask();
            RecoveryOwnedTask();
            RetryableFailure();
            PermanentFailure();
            SchedulerGrantRequirement();
            DeterministicExecutionIdentity();
            MultiplayerAuthorityGating();
            TickDriver();

            Console.WriteLine("EXECUTION passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        // 1) successful execution
        private static void SuccessfulExecution()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE_SECTOR", "CAPTAIN", "ORDER", "4", RegisteredCapabilities.SetCaptainOrder);
            s_Dispatcher.Next = ExecutionResult.Success("order set");
            Check(GrantViaScheduler(t), "success: scheduler granted lease");
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Success, "success: result Success");
            Check(t.State == TaskState.Completed, "success: task Completed");
            Check(s_Dispatcher.Calls == 1, "success: dispatched exactly once");
            string actionId = ExecutionClaims.MakeDefaultActionId(t, RegisteredCapabilities.SetCaptainOrder);
            Check(ExecutionClaims.Ledger.Observe(actionId) == ActionOutcome.Succeeded, "success: ledger sticky Succeeded");
            Check(!ExecutionClaims.HasClaim(t.TaskId, s_Now), "success: claim released");
            Check(r.GetMetadata("actionId") == actionId, "success: actionId in result metadata");
        }

        // 2) unknown capability
        private static void UnknownCapability()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "ORDER", "4", "NO_SUCH_CAP");
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "unknown cap: rejected");
            Check(t.State == TaskState.Failed, "unknown cap: task Failed (recovery owns)");
            Check(s_Dispatcher.Calls == 0, "unknown cap: nothing dispatched");
            Check(!ExecutionClaims.HasClaim(t.TaskId, s_Now), "unknown cap: no claim left");
        }

        // 3) disabled capability
        private static void DisabledCapability()
        {
            FreshSetup();
            CapabilityRegistry.SetEnabled(RegisteredCapabilities.SetCaptainOrder, false);
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "ORDER", "4", RegisteredCapabilities.SetCaptainOrder);
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "disabled: rejected");
            Check(t.State == TaskState.Failed, "disabled: task Failed");
            Check(s_Dispatcher.Calls == 0, "disabled: nothing dispatched");
        }

        // 4) invalid task (not registered / no capability bound)
        private static void InvalidTask()
        {
            FreshSetup();
            CapBotTask orphan = CapBotTask.Create("OBSERVE", "CAPTAIN", "never registered", 5, 0, -1, null, null, null);
            ExecutionResult r = TaskExecutor.AttemptExecution(orphan, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "invalid task: unregistered object refused");
            Check(orphan.State == TaskState.Created, "invalid task: unregistered object untouched");

            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "ORDER", "4", null); // no CapabilityId metadata
            GrantViaScheduler(t);
            ExecutionResult r2 = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r2 != null && r2.Outcome == ExecutionOutcome.Rejected, "invalid task: no capability binding refused");
            Check(t.State == TaskState.Failed, "invalid task: started task failed (never wedged)");
            Check(s_Dispatcher.Calls == 0, "invalid task: nothing dispatched");
        }

        // 5) invalid owner (not in the capability's AllowedOwners)
        private static void InvalidOwner()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "BOT:7", "ORDER", "4", RegisteredCapabilities.SetCaptainOrder);
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "invalid owner: rejected");
            Check(t.State == TaskState.Failed, "invalid owner: task Failed");
            Check(s_Dispatcher.Calls == 0, "invalid owner: nothing dispatched");
        }

        // 6) invalid target (kind not allowed by the capability)
        private static void InvalidTarget()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "SHIP", "42", RegisteredCapabilities.SetCaptainOrder);
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "invalid target kind: rejected");
            Check(t.State == TaskState.Failed, "invalid target: task Failed");
            Check(s_Dispatcher.Calls == 0, "invalid target: nothing dispatched");

            FreshSetup();
            CapBotTask t2 = NewQueuedTask("OBSERVE", "CAPTAIN", "SHIP", "abc", RegisteredCapabilities.SetCaptainTarget);
            GrantViaScheduler(t2);
            ExecutionResult r2 = TaskExecutor.AttemptExecution(t2, s_Now);
            Check(r2 != null && r2.Outcome == ExecutionOutcome.Rejected, "invalid target: unparsable ship id rejected");
            Check(t2.State == TaskState.Failed, "invalid target: task Failed");
        }

        // 7) failed precondition (custom capability whose precondition is false)
        private static void FailedPrecondition()
        {
            FreshSetup();
            CapabilityRegistry.Register(new CapabilityDescriptor(
                "PRECONDITION_TEST", "precondition test", "test-only capability",
                new string[] { "CAPTAIN" }, CapabilityAuthority.MasterOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.None, null,
                0, false,
                delegate (CapabilityRequest rq) { return false; }, null,
                "none (test)", "CAPABILITY"));
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", "PRECONDITION_TEST");
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "precondition: rejected");
            Check(r.GetMetadata("validation") == CapabilityValidation.RejectedPrecondition.ToString(), "precondition: validation outcome recorded");
            Check(t.State == TaskState.Failed, "precondition: task Failed");
            Check(s_Dispatcher.Calls == 0, "precondition: nothing dispatched");
        }

        // 8) wrong authority (MasterOnly capability, non-authoritative process)
        private static void WrongAuthority()
        {
            FreshSetup();
            s_Authority = false;
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "ORDER", "4", RegisteredCapabilities.SetCaptainOrder);
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "authority: rejected");
            Check(r.GetMetadata("validation") == CapabilityValidation.RejectedAuthority.ToString(), "authority: registry gate refused");
            Check(t.State == TaskState.Failed, "authority: task Failed");
            Check(s_Dispatcher.Calls == 0, "authority: nothing dispatched");
        }

        // 9) missing claim (claim vanishes mid-execution -> invariant, no lifecycle mutation)
        private static void MissingClaim()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            s_Dispatcher.Next = ExecutionResult.Success("placeholder");
            GrantViaScheduler(t);
            // Dispatcher releases the claim before returning success.
            s_Dispatcher.Override = delegate (CapabilityRequest rq)
            {
                ExecutionClaims.ReleaseClaim(rq.TaskId, rq.OwnerActorId, "test: claim vanished", s_Now);
                return ExecutionResult.Success("released");
            };
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            s_Dispatcher.Override = null;
            Check(r != null && r.Outcome == ExecutionOutcome.FailureRetryable, "missing claim: retryable failure (no truth claimed)");
            Check(t.State == TaskState.Running, "missing claim: task left for recovery (no executor mutation)");
            Check(!ExecutionClaims.HasClaim(t.TaskId, s_Now), "missing claim: claim indeed gone");
            Check(s_Dispatcher.Calls == 1, "missing claim: dispatch attempted once");
        }

        // 10) duplicate claim (a foreign active claim on the task blocks execution)
        private static void DuplicateClaim()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            ClaimResult foreign = ExecutionClaims.TryClaim(t.TaskId, "TEST_KIND", 0, "FOREIGN_OWNER", t.TargetId, s_Now);
            Check(foreign == ClaimResult.Granted, "duplicate claim: foreign claim granted (setup)");
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "duplicate claim: rejected");
            Check(t.State == TaskState.Failed, "duplicate claim: task Failed");
            Check(s_Dispatcher.Calls == 0, "duplicate claim: nothing dispatched");
        }

        // 11) duplicate execution request (ledger already Succeeded for the identity)
        private static void DuplicateExecutionRequest()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            string actionId = ExecutionClaims.MakeDefaultActionId(t, RegisteredCapabilities.ReadWorldSnapshot);
            ExecutionClaims.Ledger.RecordOutcome(actionId, ActionOutcome.Succeeded, s_Now);
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "duplicate execution: rejected");
            Check(s_Dispatcher.Calls == 0, "duplicate execution: never dispatched twice");
            Check(ExecutionClaims.Ledger.Observe(actionId) == ActionOutcome.Succeeded, "duplicate execution: ledger untouched");
            Check(t.State == TaskState.Failed, "duplicate execution: task Failed (recovery owns)");
        }

        // 12) stale callback (duplicate result after success is ignored)
        private static void StaleCallback()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            s_Dispatcher.Next = ExecutionResult.Success("done");
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Success, "stale callback: first execution succeeded");
            Check(t.State == TaskState.Completed, "stale callback: task Completed");
            string actionId = ExecutionClaims.MakeDefaultActionId(t, RegisteredCapabilities.ReadWorldSnapshot);
            ResultStatus dup = ExecutionClaims.RecordExecutionResult(t.TaskId, actionId, ActionOutcome.Succeeded, s_Now + 1);
            Check(dup == ResultStatus.StaleCallbackIgnored, "stale callback: duplicate result ignored");
            Check(t.State == TaskState.Completed, "stale callback: task still Completed");
            Check(s_Dispatcher.Calls == 1, "stale callback: no re-dispatch");
        }

        // 13) cancelled task
        private static void CancelledTask()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            t.TryCancel("superseded");
            GrantViaScheduler(t); // scheduler never grants terminal tasks; lease must be absent
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "cancelled: rejected");
            Check(t.State == TaskState.Cancelled, "cancelled: still Cancelled");
            Check(s_Dispatcher.Calls == 0, "cancelled: nothing dispatched");
        }

        // 14) completed task
        private static void CompletedTask()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            t.TryStart();
            t.TryComplete(); // legal path: Running -> Completed
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "completed: rejected");
            Check(t.State == TaskState.Completed, "completed: still Completed");
            Check(s_Dispatcher.Calls == 0, "completed: nothing dispatched");
        }

        // 15) recovery-owned task (Failed and Paused both refuse claims)
        private static void RecoveryOwnedTask()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            t.TryStart();
            t.TryFail("earlier failure"); // legal path: Running -> Failed
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "recovery-owned (Failed): rejected");
            Check(t.State == TaskState.Failed, "recovery-owned (Failed): still Failed");
            Check(s_Dispatcher.Calls == 0, "recovery-owned (Failed): nothing dispatched");

            FreshSetup();
            CapBotTask p = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            p.TryQueue();
            p.TryStart();
            p.TryPause(); // capability-pause: recovery owns
            TaskScheduler.Tick(s_Now); // scheduler refuses Paused candidates
            ExecutionResult r2 = TaskExecutor.AttemptExecution(p, s_Now);
            Check(r2 != null && r2.Outcome == ExecutionOutcome.Rejected, "recovery-owned (Paused): rejected");
            Check(p.State == TaskState.Paused, "recovery-owned (Paused): still Paused");
            Check(s_Dispatcher.Calls == 0, "recovery-owned (Paused): nothing dispatched");
        }

        // 16) retryable failure (result -> failure handed to recovery; NO executor retry)
        private static void RetryableFailure()
        {
            FreshSetup();
            CapBotTask t = CapBotTask.Create("OBSERVE", "CAPTAIN", "retry test", 5, 3, -1, null, null, null);
            t.SetMetadata(TaskExecutor.MetadataCapabilityId, RegisteredCapabilities.ReadWorldSnapshot);
            TaskRegistry.Register(t);
            t.TryQueue();
            s_Dispatcher.Next = ExecutionResult.FailureRetryable("transient game fault");
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.FailureRetryable, "retryable: result FailureRetryable");
            Check(t.State == TaskState.Failed, "retryable: task Failed");
            Check(t.FailureReason != null && t.FailureReason.Contains("transient"), "retryable: reason carried to task");
            Check(s_Dispatcher.Calls == 1, "retryable: NO executor-side retry (recovery owns retry)");
            string actionId = ExecutionClaims.MakeDefaultActionId(t, RegisteredCapabilities.ReadWorldSnapshot);
            Check(ExecutionClaims.Ledger.Observe(actionId) == ActionOutcome.Failed, "retryable: ledger Failed");
            Check(!ExecutionClaims.HasClaim(t.TaskId, s_Now), "retryable: claim released");
            Check(t.RetryCount == 0, "retryable: RetryCount untouched by executor");

            // Recovery (Phase 3) owns the retry: its policy retries via TryRetry.
            s_Now += 10000;
            Check(t.TryRetry(), "retryable: recovery retry path legal from Failed");
            Check(t.RetryCount == 1, "retryable: retry epoch advanced (new action identity)");
        }

        // 17) permanent failure (executor still only fails once; policy is recovery's)
        private static void PermanentFailure()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            s_Dispatcher.Next = ExecutionResult.FailurePermanent("capability structurally impossible");
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.FailurePermanent, "permanent: result FailurePermanent");
            Check(t.State == TaskState.Failed, "permanent: task Failed");
            Check(s_Dispatcher.Calls == 1, "permanent: dispatched exactly once");
            Check(!ExecutionClaims.HasClaim(t.TaskId, s_Now), "permanent: claim released");
        }

        // 18) scheduler grant requirement (no lease -> refuse; executor never grants)
        private static void SchedulerGrantRequirement()
        {
            FreshSetup();
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "no grant: rejected");
            Check(t.State == TaskState.Queued, "no grant: task still Queued (untouched)");
            Check(s_Dispatcher.Calls == 0, "no grant: nothing dispatched");
            Check(TaskScheduler.ActiveGrantCount == 0, "no grant: executor issued no grants");
            Check(TaskScheduler.HasLease(t.TaskId) == false, "no grant: no lease created by executor");
        }

        // 19) deterministic execution identity
        private static void DeterministicExecutionIdentity()
        {
            FreshSetup();
            CapBotTask t = CapBotTask.Create("OBSERVE", "CAPTAIN", "identity test", 5, 3, -1, "SECTOR", "17", null);
            TaskRegistry.Register(t);
            string a1 = ExecutionClaims.MakeDefaultActionId(t, RegisteredCapabilities.SetCaptainOrder);
            string a2 = ExecutionClaims.MakeDefaultActionId(t, RegisteredCapabilities.SetCaptainOrder);
            Check(a1 != null && a1 == a2, "identity: deterministic within session");
            Check(a1.StartsWith(t.TaskId + ":SET_CAPTAIN_ORDER:0:"), "identity: encodes task/kind/epoch");
            string other = ExecutionClaims.MakeActionId(t.TaskId, RegisteredCapabilities.SetCaptainOrder, 0, "999");
            Check(other != a1, "identity: target key changes identity");
            // attempt epoch = RetryCount: a retried attempt is a NEW identity.
            t.TryQueue();
            t.TryStart();
            t.TryFail("to retry"); // legal path: Running -> Failed
            s_Now += 1000;
            Check(t.TryRetry(), "identity: retry accepted (MaxRetries allows)");
            string retried = ExecutionClaims.MakeDefaultActionId(t, RegisteredCapabilities.SetCaptainOrder);
            Check(retried != a1 && retried.Contains(":1:"), "identity: retry epoch distinguishes attempts");
        }

        // 20) multiplayer authority gating (ReadOnly capability still cannot claim without authority)
        private static void MultiplayerAuthorityGating()
        {
            FreshSetup();
            s_Authority = false;
            CapBotTask t = NewQueuedTask("OBSERVE", "HOST", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            GrantViaScheduler(t);
            ExecutionResult r = TaskExecutor.AttemptExecution(t, s_Now);
            Check(r != null && r.Outcome == ExecutionOutcome.Rejected, "authority gating (ReadOnly): rejected");
            Check(t.State == TaskState.Failed, "authority gating: task Failed");
            Check(s_Dispatcher.Calls == 0, "authority gating: nothing dispatched");
            Check(!ExecutionClaims.HasClaim(t.TaskId, s_Now), "authority gating: no claim granted");
        }

        // Tick driver: consumes grants end-to-end, gated and bounded.
        private static void TickDriver()
        {
            FreshSetup();
            // No granted tasks -> zero attempts.
            Check(TaskExecutor.Tick(s_Now) == 0, "tick: idle pass does nothing");
            s_Now += TaskExecutor.MinRecheckMs; // gate window elapsed

            // Single granted task executes through Tick.
            CapBotTask t = NewQueuedTask("OBSERVE", "CAPTAIN", "", "", RegisteredCapabilities.ReadWorldSnapshot);
            s_Dispatcher.Next = ExecutionResult.Success("done");
            TaskScheduler.Tick(s_Now);
            int attempts = TaskExecutor.Tick(s_Now);
            Check(attempts == 1, "tick: one granted task executed");
            Check(t.State == TaskState.Completed, "tick: task Completed via Tick");
            // Executor gate: an immediate second tick is gated.
            Check(TaskExecutor.Tick(s_Now) == 0, "tick: immediate re-tick gated");

            // Bounded: MaxAttemptsPerTick=4 across distinct owners.
            FreshSetup();
            List<CapBotTask> tasks = new List<CapBotTask>();
            string[] owners = { "CAPTAIN", "HOST", "BOT:1", "BOT:2", "BOT:3", "BOT:4" };
            for (int i = 0; i < owners.Length; i++)
            {
                tasks.Add(NewQueuedTask("OBSERVE", owners[i], "", "", RegisteredCapabilities.ReadWorldSnapshot));
            }
            s_Dispatcher.Next = ExecutionResult.Success("done");
            TaskScheduler.Tick(s_Now);
            int bounded = TaskExecutor.Tick(s_Now);
            Check(bounded == TaskExecutor.MaxAttemptsPerTick, "tick: bounded to MaxAttemptsPerTick");
            int remaining = 0;
            foreach (CapBotTask q in tasks) { if (q.State == TaskState.Queued) remaining++; }
            Check(remaining == 2, "tick: 2 tasks remain queued after bound");
            s_Now += TaskExecutor.MinRecheckMs;
            int rest = TaskExecutor.Tick(s_Now);
            Check(rest == 2, "tick: remaining tasks execute on a later tick");
        }
    }
}