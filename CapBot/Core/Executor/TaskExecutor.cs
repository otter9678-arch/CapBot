using System;
using System.Collections.Generic;
using CapBot.Core.Capabilities;
using CapBot.Core.Tasks;

namespace CapBot.Core.Executor
{
    // ---- Phase 8: task executor ----------------------------------------------
    //
    // The execution layer for ALREADY-APPROVED tasks: takes one granted task
    // plus one registered safe capability and performs the allowed operation
    // through the appropriate safe pathway.
    //
    // ARCHITECTURE (master prompt): the scheduler (P4) selects. Recovery
    // (P3) manages failed/stuck work. The capability registry (P7) defines
    // what is legal. The executor performs EXACTLY ONE approved capability
    // action — it invents no tasks, changes no priorities, creates no
    // capabilities, and never interprets strings as commands.
    //
    // PER-ATTEMPT FLOW (all gates BEFORE the capability action):
    //
    //   1. resolve the live task (registry truth; terminal/recovery-owned
    //      tasks are refused without mutation)
    //   2. consume the scheduler grant (TryTakeLease) — no grant, no
    //      execution; the lease is consumed so exactly one executor pass
    //      can ever start the task
    //   3. move the task Queued->Running through the Phase 2 contract
    //      (TryStart) — from here on every refusal still resolves the task
    //      through the lifecycle (TryFail), never leaving it wedged
    //   4. build a CapabilityRequest from the task's own fields + the
    //      "CapabilityId"/"Argument" metadata (UNTRUSTED data — never
    //      parsed as behavior; the registry validates it)
    //   5. CapabilityRegistry.Validate — the full P7 gate ladder
    //   6. ExecutionClaims.TryClaim — duplicate-execution protection; the
    //      P5 layer is the LAST authority gate before the action
    //   7. dispatch through ICapabilityDispatcher — static, code-reviewed
    //      branches calling VERIFIED PULSAR APIs only (game-facing impl:
    //      PulsarCapabilityDispatcher); the executor holds no game refs
    //   8. ExecutionClaims.RecordExecutionResult — idempotent outcome
    //      recording; sticky success; duplicate/stale callbacks ignored
    //   9. resolve the task through the Phase 2 contract: Success ->
    //      TryComplete, failure/rejection -> TryFail(reason) handing the
    //      result to Phase 3 recovery. NO retry logic lives here — retry,
    //      backoff and abandon policy is exclusively recovery's.
    //
    // ORDERING NOTE — validate BEFORE claim (deliberate): the P7 claim-
    // conflict gate consults the P5 claim probe, which treats ANY unexpired
    // claim on the task as a conflict. If the executor claimed first, its
    // own claim would fail validation (self-conflict). The user's flow
    // diagram lists claim before capability validation, but the same
    // components in the P7-tested order (Validate -> TryClaim -> execute ->
    // RecordExecutionResult) preserve every listed guarantee: all capability
    // gates still run before any gameplay action, the claim-conflict gate
    // correctly catches PRIOR duplicates (ledger success / other paths'
    // claims), and the recorded result releases the claim afterwards.
    //
    // MULTIPLAYER (research §9): all work here is host-authoritative. The
    // claims layer is deny-by-default until Mod.cs wires the authority
    // policy to PhotonNetwork.isMasterClient; the registry independently
    // re-checks authority through the same seam. Clients never execute —
    // vanilla's request->master pattern is untouched. The Tick driver must
    // additionally be isMasterClient-gated by the caller (Patch.cs).
    //
    // PERFORMANCE: no per-frame loop (Tick gate 250 ms, scheduler grants at
    // 1 s cadence), no scene scans, no FindObjectsOfType, no LINQ on any
    // path, bounded per tick (MaxAttemptsPerTick), zero allocation beyond
    // the registry's own snapshot. Bounded by construction: the executor
    // holds no collections of its own.
    public static class TaskExecutor
    {
        public const int MinRecheckMs = 250;       // executor Tick gate (scheduler grants at 1 s)
        public const int MaxAttemptsPerTick = 4;   // bounded work per pass

        // Metadata keys a task uses to bind itself to a capability. Values
        // are UNTRUSTED data: never parsed as commands, only validated by
        // the registry (unknown ids are rejected, never guessed).
        public const string MetadataCapabilityId = "CapabilityId";
        public const string MetadataArgument = "Argument";

        private static readonly object m_Lock = new object();
        private static ICapabilityDispatcher m_Dispatcher;   // null => nothing executes (fail-closed)
        private static Action<string> m_OnDecision;          // ExecutorLogBridge attaches at boot
        private static int m_LastTickMs = -1;
        private static bool m_Enabled = true;
        private static long m_TickCount;                     // Phase 29: status readback (attempts sum)
        private static long m_ExecTickCount;                 // Phase 29: status readback (Tick calls)

        // ---- configuration ---------------------------------------------------
        public static bool Enabled
        {
            get { lock (m_Lock) return m_Enabled; }
            set { lock (m_Lock) m_Enabled = value; }
        }

        // The ONLY execution seam. No dispatcher attached => every attempt is
        // refused Unavailable (the layer is inert by construction, same as
        // scheduler/recovery before their drivers existed).
        public static void SetDispatcher(ICapabilityDispatcher dispatcher)
        {
            lock (m_Lock) m_Dispatcher = dispatcher;
        }

        // Diagnostic hook: one line per executor decision (accept/reject/
        // result/invariant). Fired outside the lock; must never throw.
        public static void SetDecisionListener(Action<string> listener)
        {
            lock (m_Lock) m_OnDecision = listener;
        }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // ---- Tick --------------------------------------------------------------
        // Consumes scheduler grants. This is deliberately NOT a second
        // scheduler: it does not rank, age, preempt or budget work — it only
        // picks up leases the Phase 4 scheduler already issued and runs the
        // attempt pipeline for each. Safe at any cadence (gate-bounded).
        // Returns the number of execution attempts performed.
        public static int Tick(int nowMs)
        {
            if (!Enabled) return 0;
            lock (m_Lock)
            {
                if (m_LastTickMs >= 0 && unchecked(nowMs - m_LastTickMs) < MinRecheckMs) return 0;
                m_LastTickMs = nowMs;
                m_ExecTickCount++;
            }

            // Snapshot the granted set (Queued tasks holding an unexpired
            // lease) outside any executor state. Bounded by the live cap.
            int attempts = 0;
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            for (int i = 0; i < live.Count && attempts < MaxAttemptsPerTick; i++)
            {
                CapBotTask t = live[i];
                if (t.State != TaskState.Queued) continue;
                if (!TaskScheduler.HasLease(t.TaskId)) continue;
                AttemptExecution(t, nowMs);
                attempts++;
            }
            lock (m_Lock) { m_TickCount += attempts; }
            return attempts;
        }

        // Phase 29: status readbacks (bounded counters; the StatusHub surface).
        public static long TickCallCount { get { lock (m_Lock) return m_ExecTickCount; } }
        public static long AttemptCount { get { lock (m_Lock) return m_TickCount; } }

        // ---- One execution attempt ---------------------------------------------
        // Runs the full pipeline for ONE task. Never throws; returns a
        // deterministic result. The task must be registered; the caller may
        // pass any live task — every gate below refuses what it must.
        public static ExecutionResult AttemptExecution(CapBotTask task, int nowMs)
        {
            if (task == null) return Finish(null, ExecutionResult.Unavailable("null task"), "ExecutorUnavailable null task");
            if (!Enabled) return Finish(task, ExecutionResult.Unavailable("executor disabled"), "ExecutorUnavailable executor disabled");

            // 1) registry truth: the presented object must BE the live task.
            CapBotTask live = TaskRegistry.Get(task.TaskId);
            if (live == null || !live.Equals(task))
                return Finish(task, ExecutionResult.Rejected("task not live in registry"),
                    "ExecutorRefused #" + task.TaskId + " task not live in registry");
            if (live.IsTerminal)
                return Finish(task, ExecutionResult.Rejected("task terminal (" + live.State + ")"),
                    "ExecutorRefused #" + live.TaskId + " task terminal (" + live.State + ")");
            if (live.State == TaskState.Failed || live.State == TaskState.Paused)
                return Finish(task, ExecutionResult.Rejected("recovery owns task (state=" + live.State + ")"),
                    "ExecutorRefused #" + live.TaskId + " recovery owns task (state=" + live.State + ")");

            // 2) scheduler grant requirement: consume the lease (exactly-one-
            //    executor-pass guarantee). The executor NEVER grants work.
            if (!TaskScheduler.TryTakeLease(live.TaskId, nowMs))
                return Finish(task, ExecutionResult.Rejected("no scheduler grant lease"),
                    "ExecutorRefused #" + live.TaskId + " no scheduler grant lease");

            // 3) lifecycle through the Phase 2 contract only.
            if (!live.TryStart())
                return Finish(task, ExecutionResult.Rejected("start refused (state=" + live.State + ")"),
                    "ExecutorRefused #" + live.TaskId + " start refused (state=" + live.State + ")");

            // 4) capability binding from task metadata (untrusted data).
            string capabilityId = live.GetMetadata(MetadataCapabilityId);
            if (string.IsNullOrEmpty(capabilityId))
                return FailStarted(live, "no capability bound (metadata " + MetadataCapabilityId + " missing)",
                    ExecutionResult.Rejected("no capability bound"));
            string argument = live.GetMetadata(MetadataArgument) ?? string.Empty;
            CapabilityRequest request = new CapabilityRequest(
                live.TaskId, live.TaskType, live.OwnerActorId,
                live.TargetKind, live.TargetId, argument);

            // 5) registry validation — the P7 gate ladder decides what is legal.
            CapabilityValidation validation = CapabilityRegistry.Validate(capabilityId, request, live);
            if (validation != CapabilityValidation.Approved)
                return FailStarted(live, "capability validation failed: " + validation,
                    ExecutionResult.Rejected("capability " + validation)
                        .WithMeta("validation", validation.ToString()));

            // Re-check recovery ownership: validation (preconditions, world
            // probes) can take real time and recovery may have paused/failed
            // the task in between. Never claim a recovery-owned task.
            if (live.State == TaskState.Failed || live.State == TaskState.Paused)
                return Finish(live, ExecutionResult.Rejected("recovery took task during validation (state=" + live.State + ")"),
                    "ExecutorRefused #" + live.TaskId + " recovery took task during validation (state=" + live.State + ")");

            // 6) claim — the P5 duplicate-execution layer is the last gate
            //    before any gameplay action. attemptEpoch = RetryCount: each
            //    recovery retry is a NEW logical action identity.
            ClaimResult claim = ExecutionClaims.TryClaim(
                live.TaskId, capabilityId, live.RetryCount, live.OwnerActorId, live.TargetId, nowMs);
            if (claim != ClaimResult.Granted && claim != ClaimResult.GrantedTakeover)
                return OnClaimRefused(live, capabilityId, claim);

            string actionId = ExecutionClaims.MakeDefaultActionId(live, capabilityId);

            // Invariant: we hold the claim we just requested.
            if (!ExecutionClaims.GetClaim(live.TaskId, nowMs).Active)
                return OnInvariant(live, "claim not active immediately after TryClaim (granted=" + claim + ")");

            // 7) dispatch EXACTLY ONE registered capability action.
            ICapabilityDispatcher dispatcher;
            lock (m_Lock) dispatcher = m_Dispatcher;
            if (dispatcher == null)
                return OnDispatchFault(live, actionId, nowMs, ExecutionResult.Unavailable("no dispatcher attached"), "no dispatcher attached");

            ExecutionResult result;
            try
            {
                CapabilityDescriptor descriptor = CapabilityRegistry.Get(capabilityId);
                result = dispatcher.Dispatch(descriptor, request, live);
            }
            catch (Exception ex)
            {
                // A dispatcher fault must never crash the tick or skip
                // recording: bounded diagnostic, outcome recorded as failed.
                result = ExecutionResult.FailureRetryable("dispatcher fault: " + ex.GetType().Name);
            }
            if (result == null)
                result = ExecutionResult.FailureRetryable("dispatcher returned null result");

            // Deterministic failure reason for the task record (the result
            // object itself is untouched — its metadata stays intact).
            string failReason = result.IsSuccess ? null
                : (string.IsNullOrEmpty(result.Reason)
                    ? "capability action failed (dispatcher gave no reason)"
                    : result.Reason);

            // The claim must still be ours when we record. If it vanished
            // (lease expiry or another pass resolved it), the identity
            // protection is already broken — refuse to record against a
            // foreign/stale attempt and let recovery own the task. No
            // lifecycle mutation here: the task is NOT resolved by this pass.
            if (!ExecutionClaims.GetClaim(live.TaskId, nowMs).Active)
                return OnInvariant(live, "claim vanished mid-execution (actionId=" + actionId + ")");

            // 8) record the outcome (idempotent; releases the claim).
            ActionOutcome outcome = result.IsSuccess ? ActionOutcome.Succeeded : ActionOutcome.Failed;
            ResultStatus status = ExecutionClaims.RecordExecutionResult(live.TaskId, actionId, outcome, nowMs);

            // 9) resolve the task through the lifecycle contract. Recovery
            //    owns what happens after a failure — no retry logic here.
            switch (status)
            {
                case ResultStatus.Recorded:
                    if (result.IsSuccess)
                    {
                        if (live.TryComplete())
                            return Finish(live, result.WithMeta("actionId", actionId),
                                "ExecutorResult #" + live.TaskId + " " + capabilityId + " SUCCESS task=Completed");
                        // Complete refused (recovery paused us mid-flight):
                        // the claim is already released, the action succeeded —
                        // recovery's stuck detector will resolve the state.
                        return Finish(live,
                            ExecutionResult.FailureRetryable("complete refused (state=" + live.State + ")")
                                .WithMeta("actionId", actionId),
                            "ExecutorInvariant #" + live.TaskId + " success recorded but completion refused (state=" + live.State + ")");
                    }
                    if (live.TryFail(failReason))
                        return Finish(live, result.WithMeta("actionId", actionId),
                            "ExecutorResult #" + live.TaskId + " " + capabilityId + " FAILED task=Failed (recovery owns retry)");
                    return Finish(live, result.WithMeta("actionId", actionId),
                        "ExecutorInvariant #" + live.TaskId + " failure recorded but fail transition refused (state=" + live.State + ")");

                case ResultStatus.DuplicateIgnored:
                    // This exact identity already had its result recorded.
                    // Never dispatch twice: resolve from the ledger truth.
                    if (ExecutionClaims.Ledger.Observe(actionId) == ActionOutcome.Succeeded)
                    {
                        if (live.TryComplete())
                            return Finish(live, result.WithMeta("actionId", actionId),
                                "ExecutorResult #" + live.TaskId + " " + capabilityId + " duplicate result -> task=Completed");
                        return Finish(live, result.WithMeta("actionId", actionId),
                            "ExecutorInvariant #" + live.TaskId + " duplicate success but completion refused (state=" + live.State + ")");
                    }
                    live.TryFail(failReason); // idempotent; may already be Failed
                    return Finish(live, result.WithMeta("actionId", actionId),
                        "ExecutorResult #" + live.TaskId + " " + capabilityId + " duplicate failure ignored");

                case ResultStatus.StaleCallbackIgnored:
                    // No lifecycle mutation: this pass does not own the task's
                    // truth anymore. Recovery's stuck detection resolves it.
                    return Finish(live,
                        ExecutionResult.FailureRetryable("stale callback ignored (claim no longer ours)")
                            .WithMeta("actionId", actionId),
                        "ExecutorStaleCallback #" + live.TaskId + " " + actionId);

                case ResultStatus.RejectedNotAuthoritative:
                    // We were authoritative at claim time — this is an
                    // authority flip mid-attempt. Record nothing further.
                    return OnInvariant(live, "result recording refused: authority lost mid-attempt");

                default: // RejectedInvalid
                    return OnInvariant(live, "result recording refused: invalid arguments (invariant)");
            }
        }

        // ---- refusal paths ------------------------------------------------------

        // Claim refused after start: the task is Running and must resolve
        // through the lifecycle (hand to recovery), never wedge.
        private static ExecutionResult OnClaimRefused(CapBotTask task, string capabilityId, ClaimResult claim)
        {
            string reason;
            switch (claim)
            {
                case ClaimResult.DuplicateExecutionRejected:
                    // The exact logical action already succeeded (ledger
                    // sticky). Never dispatch again — the attempt's goal is
                    // already achieved, so resolve the task as complete.
                    if (task.TryComplete())
                        return Finish(task, ExecutionResult.Success("duplicate execution ignored (already succeeded)"),
                            "ExecutorDuplicateExecution #" + task.TaskId + " " + capabilityId + " ignored -> task=Completed");
                    return Finish(task, ExecutionResult.Rejected("duplicate execution; complete refused (state=" + task.State + ")"),
                        "ExecutorInvariant #" + task.TaskId + " duplicate execution but completion refused (state=" + task.State + ")");
                case ClaimResult.RejectedNotAuthoritative:
                    reason = "claim refused: not authoritative";
                    break;
                case ClaimResult.RejectedTaskMissing:
                    reason = "claim refused: task not live";
                    break;
                case ClaimResult.RejectedTaskTerminal:
                    reason = "claim refused: task terminal";
                    break;
                case ClaimResult.RejectedRecoveryOwned:
                    reason = "claim refused: recovery owns task";
                    break;
                case ClaimResult.RejectedOwnedByOther:
                    reason = "claim refused: owned by another executor";
                    break;
                case ClaimResult.RejectedActiveClaim:
                    reason = "claim refused: active claim exists";
                    break;
                default: // RejectedInvalid — our own arguments (invariant)
                    reason = "claim refused: invalid arguments (invariant)";
                    break;
            }
            return FailStarted(task, reason, ExecutionResult.Rejected("claim " + claim)
                .WithMeta("claim", claim.ToString()));
        }

        // Dispatch refused/unavailable AFTER the claim was granted: release
        // the claim (owner-verified, no result recorded), then fail the task
        // into recovery's hands.
        private static ExecutionResult OnDispatchFault(CapBotTask task, string actionId, int nowMs, ExecutionResult result, string why)
        {
            ExecutionClaims.ReleaseClaim(task.TaskId, task.OwnerActorId, "executor dispatch fault: " + why, nowMs);
            return FailStarted(task, why, result);
        }

        // A started task whose attempt was refused/faulted: fail it (Phase 3
        // recovery owns the outcome) and emit one decision line.
        private static ExecutionResult FailStarted(CapBotTask task, string why, ExecutionResult result)
        {
            if (!task.TryFail(why))
            {
                // Transition refused (task went terminal/paused mid-flight) —
                // recovery already owns it; never fight the lifecycle.
                Emit("ExecutorInvariant #" + task.TaskId + " fail transition refused (state=" + task.State + "): " + why);
            }
            return Finish(task, result, "ExecutorRejected #" + task.TaskId + " " + why);
        }

        // Invariant violation: log loudly, leave the task for recovery (no
        // further mutation from this pass), return a retryable failure.
        private static ExecutionResult OnInvariant(CapBotTask task, string why)
        {
            Emit("ExecutorInvariantViolation #" + task.TaskId + " " + why);
            return Finish(task, ExecutionResult.FailureRetryable(why), null);
        }

        // Terminal helper: fire the decision listener once, return the result.
        private static ExecutionResult Finish(CapBotTask task, ExecutionResult result, string decisionLine)
        {
            if (decisionLine != null) Emit(decisionLine + " :: " + (result == null ? "?" : result.ToStatusLine()));
            return result;
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Dispatcher = null;
                m_OnDecision = null;
                m_LastTickMs = -1;
                m_Enabled = true;
                m_TickCount = 0;
                m_ExecTickCount = 0;
            }
        }
    }
}