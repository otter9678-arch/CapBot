// Dev-side unit tests for the Phase 5 execution-claim safety domain (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the 8 pure task-domain files. Time is virtual (explicit nowMs
// anchored at each task's real CreatedTimeMs) — no real clock reads, no
// sleeping. Isolation resets registry + recovery + scheduler + claims; the
// authority policy is per-test explicit (deny-by-default is itself one of
// the tested behaviors).
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;

namespace CapBot.TaskTests
{
    internal static class ExecutionClaimTests
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

        // Granular probe control: recovery precedence (target > owner >
        // capability) means a scenario must disable ONLY the fact it tests.
        private static void ProbeSet(bool target, bool owner, bool capability)
        {
            s_Probe.Target = target;
            s_Probe.Owner = owner;
            s_Probe.Capability = capability;
            s_Probe.Invalidates = false;
        }

        // Isolation: fresh registry + recovery + scheduler + claims, scripted
        // probe wired into recovery, decision listener captured, registry
        // listener mirroring TaskLogBridge's Track-on-register, and an
        // allow-all authority policy (deny-by-default is tested separately).
        private static void FreshSetup()
        {
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            ExecutionClaims.ResetForTests();
            s_Probe = new ScriptedProbe();
            TaskRecoveryManager.Probe = s_Probe;
            s_Lines.Clear();
            ExecutionClaims.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            TaskRegistry.SetTransitionListener(delegate (CapBotTask t, string label)
            { if (label == "Registered") TaskRecoveryManager.Track(t); });
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
        }

        private static CapBotTask NewQueued(string owner, int priority)
        {
            CapBotTask t = CapBotTask.Create("CLAIM_TYPE", owner, "claim test", priority, 2, -1, null, null, null);
            TaskRegistry.Register(t);
            t.TryQueue();
            return t;
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

        private static bool HasLine(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static string id1_of(long taskId, string targetKey)
        {
            return ActionIdentity.MakeActionId(taskId, "EXECUTE", 0, targetKey);
        }

        internal static int Run()
        {
            // ---- deterministic action identity (safe, data-only) --------------
            string id1 = ActionIdentity.MakeActionId(12, "EXECUTE", 0, "target-A");
            string id2 = ActionIdentity.MakeActionId(12, "EXECUTE", 0, "target-A");
            string id3 = ActionIdentity.MakeActionId(12, "EXECUTE", 1, "target-A");
            string id4 = ActionIdentity.MakeActionId(12, "EXECUTE", 0, "target-B");
            Check(id1 != null && id1 == id2, "same inputs -> identical action id (deterministic)");
            Check(id3 != null && id3 != id1, "different attempt epoch -> different id");
            Check(id4 != null && id4 != id1, "different target -> different id");
            Check(ActionIdentity.MakeActionId(12, "EXECUTE", 0, null) == ActionIdentity.MakeActionId(12, "EXECUTE", 0, ""),
                "null and empty target hash identically");
            Check(ActionIdentity.MakeActionId(-1, "EXECUTE", 0, "t") == null, "negative taskId rejected");
            Check(ActionIdentity.MakeActionId(12, "bad kind", 0, "t") == null, "actionKind with spaces rejected");
            Check(ActionIdentity.MakeActionId(12, new string('x', 33), 0, "t") == null, "actionKind > 32 chars rejected");
            Check(ActionIdentity.MakeActionId(12, "OK_KIND_9", 0, "t") != null, "underscore+digits actionKind accepted");
            Check(ActionIdentity.MakeActionId(12, "EXECUTE", 0, new string('T', 5000)) != null,
                "unbounded untrusted target text is hashed, never embedded");
            Check(id1.Split(':').Length == 4, "id shape: task:kind:epoch:hash8");

            // ---- authority gating (deny-by-default) ----------------------------
            FreshSetup();
            // FreshSetup wired an allow-all policy; ResetForTests clears it
            // (and the listener) -> the unwired layer is inert by construction.
            ExecutionClaims.ResetForTests();
            CapBotTask a = NewQueued("BOT:1", 5);
            int v = a.CreatedTimeMs;
            Check(!ExecutionClaims.IsAuthoritative(), "no policy set -> not authoritative");
            Check(ExecutionClaims.TryClaim(a.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v) == ClaimResult.RejectedNotAuthoritative,
                "deny-by-default: claim refused without wired policy");
            Check(ExecutionClaims.RecordExecutionResult(a.TaskId, "12:EXECUTE:0:x", ActionOutcome.Succeeded, v) == ResultStatus.RejectedNotAuthoritative,
                "deny-by-default: result recording refused too");
            ExecutionClaims.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            Check(ExecutionClaims.TryClaim(a.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v) == ClaimResult.RejectedNotAuthoritative,
                "refusal repeated while unwired");
            Check(CountLines("ClaimRejected #" + a.TaskId + " not authoritative") == 1, "non-authoritative rejection logged");
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            Check(ExecutionClaims.IsAuthoritative(), "wired policy -> authoritative");
            Check(ExecutionClaims.TryClaim(a.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v) == ClaimResult.Granted,
                "authorized claim granted after policy wired");
            bool flip = false;
            ExecutionClaims.SetAuthorityPolicy(delegate { return flip; });
            Check(ExecutionClaims.TryClaim(a.TaskId, "EXECUTE", 1, "EXECUTOR", "T1", v + 10) == ClaimResult.RejectedNotAuthoritative,
                "policy flip to false -> claims refused (authority is a live gate)");

            // ---- claiming: duplicate / same-owner / different-owner -------------
            FreshSetup();
            CapBotTask c = NewQueued("BOT:1", 5);
            v = c.CreatedTimeMs;
            Check(ExecutionClaims.TryClaim(c.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v) == ClaimResult.Granted,
                "first claim granted");
            Check(ExecutionClaims.HasClaim(c.TaskId, v), "claim visible while lease live");
            Check(ExecutionClaims.TryClaim(c.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + 10) == ClaimResult.RejectedActiveClaim,
                "same-owner repeated claim rejected (duplicate attempt)");
            Check(ExecutionClaims.TryClaim(c.TaskId, "EXECUTE", 0, "OTHER", "T1", v + 10) == ClaimResult.RejectedOwnedByOther,
                "different-owner claim rejected while lease active");
            Check(ExecutionClaims.TryClaim(c.TaskId, "EXECUTE", 1, "EXECUTOR", "T1", v + 10) == ClaimResult.RejectedActiveClaim,
                "same-owner claim on a DIFFERENT action rejected while lease active");
            ClaimInfo info = ExecutionClaims.GetClaim(c.TaskId, v + 10);
            Check(info.Active && info.Owner == "EXECUTOR" && info.ActionKind == "EXECUTE" && info.AttemptEpoch == 0,
                "claim snapshot: owner/kind/epoch/active");
            Check(ExecutionClaims.TryClaim(c.TaskId, "BAD KIND", 0, "EXECUTOR", "T1", v + 10) == ClaimResult.RejectedInvalid,
                "malformed actionKind rejected");
            Check(ExecutionClaims.TryClaim(999999, "EXECUTE", 0, "EXECUTOR", "T1", v + 10) == ClaimResult.RejectedTaskMissing,
                "claim on missing task rejected");
            s_Lines.Clear();
            ExecutionClaims.TryClaim(c.TaskId, "EXECUTE", 0, "OTHER", "T1", v + 20);
            ExecutionClaims.TryClaim(c.TaskId, "EXECUTE", 0, "OTHER", "T1", v + 2005);
            Check(CountLines("ClaimRejected #" + c.TaskId) == 1, "rejection log throttled to 1/s per claim (v+20 within the v+10 window suppressed, v+2005 emitted)");

            // ---- lease expiration + stale-lease recovery ------------------------
            FreshSetup();
            CapBotTask l = NewQueued("BOT:1", 5);
            v = l.CreatedTimeMs;
            Check(ExecutionClaims.TryClaim(l.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v) == ClaimResult.Granted, "lease claim granted");
            Check(ExecutionClaims.TryClaim(l.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + ExecutionClaims.ClaimLeaseDurationMs - 1)
                == ClaimResult.RejectedActiveClaim, "claim still active 1ms before expiry");
            Check(!ExecutionClaims.GetClaim(l.TaskId, v + ExecutionClaims.ClaimLeaseDurationMs).Active,
                "claim inactive after lease expiry");
            Check(ExecutionClaims.TryClaim(l.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + ExecutionClaims.ClaimLeaseDurationMs)
                == ClaimResult.GrantedTakeover, "expired lease -> explicit takeover granted");
            Check(HasLine("LeaseExpired #" + l.TaskId) && HasLine("OwnershipReleased #" + l.TaskId + " owner=EXECUTOR reason=lease expired"),
                "stale-owner loss logged (LeaseExpired + OwnershipReleased)");
            Check(HasLine("ClaimAccepted #" + l.TaskId + " " + id1_of(l.TaskId, "T1") + " owner=EXECUTOR (takeover)"),
                "takeover logged as explicit ownership change");
            Check(ExecutionClaims.TryClaim(l.TaskId, "EXECUTE", 0, "NEXT", "T1", v + ExecutionClaims.ClaimLeaseDurationMs + 6000)
                == ClaimResult.GrantedTakeover, "different owner can take over after next expiry (stale owners never keep ownership)");
            ClaimInfo taken = ExecutionClaims.GetClaim(l.TaskId, v + ExecutionClaims.ClaimLeaseDurationMs + 6000);
            Check(taken.Active && taken.Owner == "NEXT", "new owner holds the recovered claim");

            FreshSetup();
            CapBotTask l2 = NewQueued("BOT:1", 5);
            v = l2.CreatedTimeMs;
            ExecutionClaims.TryClaim(l2.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            Check(ExecutionClaims.Tick(v + ExecutionClaims.ClaimLeaseDurationMs) == 1, "Tick drops expired lease");
            Check(!ExecutionClaims.HasClaim(l2.TaskId, v + ExecutionClaims.ClaimLeaseDurationMs), "expired lease gone after Tick");
            Check(HasLine("LeaseExpired #" + l2.TaskId), "Tick logs lease expiry");
            Check(ExecutionClaims.Tick(v + ExecutionClaims.ClaimLeaseDurationMs + 1000) == 0, "Tick is idempotent (nothing left to drop)");

            // ---- idempotent success + duplicate/stale completion callbacks -------
            FreshSetup();
            CapBotTask r = NewQueued("BOT:1", 5);
            v = r.CreatedTimeMs;
            string rid = ExecutionClaims.MakeActionId(r.TaskId, "EXECUTE", 0, "T1");
            Check(ExecutionClaims.TryClaim(r.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v) == ClaimResult.Granted, "result-scenario claim granted");
            Check(ExecutionClaims.RecordExecutionResult(r.TaskId, rid, ActionOutcome.Succeeded, v + 100) == ResultStatus.Recorded,
                "first success recorded, claim released");
            Check(!ExecutionClaims.HasClaim(r.TaskId, v + 110), "claim released after result");
            Check(ExecutionClaims.RecordExecutionResult(r.TaskId, rid, ActionOutcome.Succeeded, v + 120) == ResultStatus.StaleCallbackIgnored,
                "duplicate completion ignored (no matching claim anymore)");
            Check(ExecutionClaims.RecordExecutionResult(r.TaskId, rid, ActionOutcome.Failed, v + 130) == ResultStatus.StaleCallbackIgnored,
                "late failure cannot downgrade a sticky success");
            Check(ExecutionClaims.Ledger.Observe(rid) == ActionOutcome.Succeeded, "ledger remembers the success");
            Check(HasLine("StaleCallbackIgnored #" + r.TaskId), "duplicate callback logged as ignored");
            Check(ExecutionClaims.TryClaim(r.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + 1000) == ClaimResult.DuplicateExecutionRejected,
                "claim on already-succeeded action rejected (duplicate execution)");
            Check(HasLine("DuplicateExecutionRejected #" + r.TaskId), "duplicate-execution rejection logged");
            Check(ExecutionClaims.TryClaim(r.TaskId, "EXECUTE", 1, "EXECUTOR", "T1", v + 1000) == ClaimResult.Granted,
                "next attempt epoch is a different logical action (claimable)");
            ExecutionClaims.ReleaseClaim(r.TaskId, "EXECUTOR", "work done differently", v + 1001);

            // ---- idempotent failure + Failed->Succeeded upgrade -------------------
            FreshSetup();
            CapBotTask f = NewQueued("BOT:1", 5);
            v = f.CreatedTimeMs;
            string fid = ExecutionClaims.MakeActionId(f.TaskId, "EXECUTE", 0, "T1");
            ExecutionClaims.TryClaim(f.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            Check(ExecutionClaims.RecordExecutionResult(f.TaskId, fid, ActionOutcome.Failed, v + 10) == ResultStatus.Recorded,
                "first failure recorded");
            Check(!ExecutionClaims.HasClaim(f.TaskId, v + 15), "failure released the claim (recovery owns the task now)");
            Check(ExecutionClaims.RecordExecutionResult(f.TaskId, fid, ActionOutcome.Failed, v + 20) == ResultStatus.StaleCallbackIgnored,
                "duplicate failure callback after release ignored");
            Check(ExecutionClaims.Ledger.Observe(fid) == ActionOutcome.Failed, "failure remembered");
            Check(ExecutionClaims.TryClaim(f.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + 40) == ClaimResult.Granted,
                "failed (not succeeded) action re-claimable in the same attempt window");
            Check(ExecutionClaims.RecordExecutionResult(f.TaskId, fid, ActionOutcome.Failed, v + 45) == ResultStatus.DuplicateIgnored,
                "duplicate failure while claim held ignored (no change)");
            Check(ExecutionClaims.RecordExecutionResult(f.TaskId, fid, ActionOutcome.Succeeded, v + 50) == ResultStatus.Recorded,
                "failure upgraded to success (legitimate late resolution)");
            Check(ExecutionClaims.Ledger.Observe(fid) == ActionOutcome.Succeeded, "upgrade stored (Failed -> Succeeded)");

            // ---- stale callbacks (no claim / after explicit release) ---------------
            FreshSetup();
            CapBotTask sc = NewQueued("BOT:1", 5);
            v = sc.CreatedTimeMs;
            Check(ExecutionClaims.RecordExecutionResult(sc.TaskId, "999:EXECUTE:0:deadbeef", ActionOutcome.Succeeded, v) == ResultStatus.StaleCallbackIgnored,
                "result with no matching claim ignored (stale callback)");
            string scid = ExecutionClaims.MakeActionId(sc.TaskId, "EXECUTE", 0, "T1");
            ExecutionClaims.TryClaim(sc.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            ExecutionClaims.ReleaseClaim(sc.TaskId, "EXECUTOR", "abandoned", v + 5);
            Check(ExecutionClaims.RecordExecutionResult(sc.TaskId, scid, ActionOutcome.Succeeded, v + 10) == ResultStatus.StaleCallbackIgnored,
                "late callback after explicit release ignored");
            Check(HasLine("StaleCallbackIgnored #" + sc.TaskId), "stale callback logged");

            // ---- explicit ownership release + invariant logging -------------------
            FreshSetup();
            CapBotTask rel = NewQueued("BOT:1", 5);
            v = rel.CreatedTimeMs;
            ExecutionClaims.TryClaim(rel.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            Check(!ExecutionClaims.ReleaseClaim(rel.TaskId, "IMPOSTOR", "steal", v + 1), "release by non-owner refused");
            Check(HasLine("InvariantViolation #" + rel.TaskId + " claim release owner mismatch"), "ownership-change invariant violation logged");
            Check(ExecutionClaims.HasClaim(rel.TaskId, v + 1), "refused release changed nothing");
            Check(ExecutionClaims.ReleaseClaim(rel.TaskId, "EXECUTOR", "executor done", v + 2), "owner release accepted");
            Check(!ExecutionClaims.HasClaim(rel.TaskId, v + 2), "claim gone after explicit release");
            Check(HasLine("OwnershipReleased #" + rel.TaskId + " owner=EXECUTOR reason=executor done"), "explicit release logged");
            Check(!ExecutionClaims.ReleaseClaim(rel.TaskId, "EXECUTOR", "again", v + 3), "release of absent claim returns false");

            // ---- task cancelled while claimed ---------------------------------------
            FreshSetup();
            CapBotTask cc = NewQueued("BOT:1", 5);
            v = cc.CreatedTimeMs;
            ExecutionClaims.TryClaim(cc.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            Check(cc.TryCancel("superseded"), "task cancelled while claimed (lifecycle stays sole mutator)");
            s_Lines.Clear();
            Check(ExecutionClaims.Tick(v + 10) == 1, "Tick drops claim of cancelled task");
            Check(HasLine("ClaimDropped #" + cc.TaskId + " reason=task Cancelled"), "cancel-drop logged");
            Check(!ExecutionClaims.HasClaim(cc.TaskId, v + 10), "claim gone after task cancellation");

            // ---- task completed while claimed ---------------------------------------
            FreshSetup();
            CapBotTask cw = NewQueued("BOT:1", 5);
            v = cw.CreatedTimeMs;
            ExecutionClaims.TryClaim(cw.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            Check(cw.TryStart() && cw.TryComplete(), "task completes while claimed");
            s_Lines.Clear();
            Check(ExecutionClaims.Tick(v + 10) == 1, "Tick drops claim of completed task");
            Check(HasLine("ClaimDropped #" + cw.TaskId + " reason=task Completed"), "completion-drop logged");
            Check(ExecutionClaims.TryClaim(cw.TaskId, "EXECUTE", 1, "EXECUTOR", "T1", v + 20) == ClaimResult.RejectedTaskTerminal,
                "terminal task cannot be re-claimed");

            // ---- recovery interaction (no weakening of Phase 3) ----------------------
            FreshSetup();
            CapBotTask rec = NewQueued("BOT:1", 5);
            v = rec.CreatedTimeMs;
            rec.TryStart();
            ExecutionClaims.TryClaim(rec.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            ProbeSet(true, false, true); // owner unavailable (target + capability valid)
            TaskRecoveryManager.Tick(v + 1000); // recovery fails the owner-less running task
            Check(rec.State == TaskState.Failed, "recovery failed the claimed task (owner down)");
            Check(ExecutionClaims.RecordExecutionResult(rec.TaskId, ExecutionClaims.MakeActionId(rec.TaskId, "EXECUTE", 0, "T1"), ActionOutcome.Failed, v + 1100)
                == ResultStatus.Recorded, "failure result recorded and claim released");
            Check(!ExecutionClaims.HasClaim(rec.TaskId, v + 1100), "no claim while task Failed (recovery-owned)");
            Check(ExecutionClaims.TryClaim(rec.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + 1200) == ClaimResult.RejectedRecoveryOwned,
                "task in Failed refuses new claims (recovery owns it)");
            TaskRecoveryManager.Tick(v + 4000); // backoff elapsed -> Retry back to Queued
            Check(rec.State == TaskState.Queued && rec.RetryCount == 1, "recovery retried the task (retry policy stays Phase 3's)");
            string freshEpochId = ExecutionClaims.MakeDefaultActionId(rec, "EXECUTE");
            Check(freshEpochId != null && freshEpochId.Split(':')[2] == "1", "default identity after retry = fresh epoch 1");
            Check(ExecutionClaims.TryClaim(rec.TaskId, "EXECUTE", 1, "EXECUTOR", "T1", v + 4100) == ClaimResult.Granted,
                "retried task re-claimed under its fresh epoch");
            ExecutionClaims.ReleaseClaim(rec.TaskId, "EXECUTOR", "test cleanup", v + 4200);

            FreshSetup();
            CapBotTask cp = NewQueued("BOT:1", 5);
            v = cp.CreatedTimeMs;
            cp.TryStart();
            ExecutionClaims.TryClaim(cp.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            ProbeSet(true, true, false); // capability down (target + owner valid)
            TaskRecoveryManager.Tick(v + 1000);
            Check(cp.State == TaskState.Paused, "recovery capability-paused the claimed task");
            ClaimInfo pausedClaim = ExecutionClaims.GetClaim(cp.TaskId, v + 2000);
            Check(pausedClaim.Active && pausedClaim.Owner == "EXECUTOR", "claim persists through a recovery pause (same attempt)");
            ProbeSet(true, true, true);
            TaskRecoveryManager.Tick(v + 3000);
            Check(cp.State == TaskState.Running, "recovery resumed the task");
            Check(ExecutionClaims.HasClaim(cp.TaskId, v + 3000), "claim intact across capability pause/resume");

            // ---- scheduler pass repeated twice (selection vs claiming are separate) --
            FreshSetup();
            CapBotTask s1 = NewQueued("BOT:1", 5);
            v = s1.CreatedTimeMs;
            TaskScheduler.Tick(v + 1000);                     // scheduler pass 1: grants
            Check(TaskScheduler.HasLease(s1.TaskId), "scheduler granted the task (selection)");
            Check(ExecutionClaims.TryClaim(s1.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + 1000) == ClaimResult.Granted,
                "executor claims the granted task");
            TaskScheduler.Tick(v + 2000);                     // scheduler pass 2 within lease: no re-grant
            Check(ExecutionClaims.TryClaim(s1.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + 2000) == ClaimResult.RejectedActiveClaim,
                "repeated scheduler pass cannot double-execute: second claim rejected");
            string sid = ExecutionClaims.MakeActionId(s1.TaskId, "EXECUTE", 0, "T1");
            ExecutionClaims.RecordExecutionResult(s1.TaskId, sid, ActionOutcome.Succeeded, v + 3000);
            TaskScheduler.Tick(v + 11000);                    // scheduler lease long expired -> re-grants
            Check(TaskScheduler.HasLease(s1.TaskId), "scheduler may re-select the task (separate concept)");
            Check(ExecutionClaims.TryClaim(s1.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + 12000) == ClaimResult.DuplicateExecutionRejected,
                "but the execution identity already succeeded -> duplicate execution refused");
            Check(!ExecutionClaims.HasClaim(s1.TaskId, v + 12000), "refused duplicate claim created no claim record");

            // ---- MakeDefaultActionId determinism across retries ------------------------
            FreshSetup();
            CapBotTask d1 = NewQueued("BOT:1", 5);
            string def1 = ExecutionClaims.MakeDefaultActionId(d1, "EXECUTE");
            Check(def1 != null && def1.Split(':')[2] == "0", "default epoch = RetryCount (0 on first attempt)");
            Check(d1.TryStart() && d1.TryFail("x") && d1.TryRetry(), "task retried through lifecycle mutators");
            string def2 = ExecutionClaims.MakeDefaultActionId(d1, "EXECUTE");
            Check(def2.Split(':')[2] == "1" && def2 != def1, "after retry the default epoch advances (new logical action)");

            // ---- bounded ledger cleanup (no unbounded claim/history state) -------------
            FreshSetup();
            for (int i = 0; i < 300; i++)
            {
                string bid = ActionIdentity.MakeActionId(1000000 + i, "LEDGER", 0, "T");
                ExecutionClaims.Ledger.RecordOutcome(bid, ActionOutcome.Succeeded, 1000 + i);
            }
            Check(ExecutionClaims.Ledger.EntryCount == ActionLedger.MaxEntries, "ledger bounded at MaxEntries (FIFO eviction)");
            Check(ExecutionClaims.Ledger.Observe(ActionIdentity.MakeActionId(1000000, "LEDGER", 0, "T")) == ActionOutcome.Unknown,
                "oldest entries evicted");
            Check(ExecutionClaims.Ledger.Observe(ActionIdentity.MakeActionId(1000299, "LEDGER", 0, "T")) == ActionOutcome.Succeeded,
                "newest entries retained (immediate-duplicate window covered)");

            FreshSetup();
            CapBotTask[] bts = new CapBotTask[5];
            for (int i = 0; i < 5; i++)
            {
                bts[i] = NewQueued("BOT:" + i, 5);
                ExecutionClaims.TryClaim(bts[i].TaskId, "EXECUTE", 0, "EXECUTOR", "T" + i, bts[i].CreatedTimeMs);
            }
            Check(ExecutionClaims.LiveClaimCount == 5, "five live claims");
            s_Lines.Clear();
            Check(ExecutionClaims.Tick(bts[4].CreatedTimeMs + 999999) == 5, "Tick drops all (expired)");
            Check(ExecutionClaims.LiveClaimCount == 0, "live claims cleanable to zero (bounded)");
            Check(CountLines("LeaseExpired #") == 5, "every drop logged exactly once");

            // ---- release with null reason + instant re-claim -----------------------
            FreshSetup();
            CapBotTask rl = NewQueued("BOT:1", 5);
            v = rl.CreatedTimeMs;
            ExecutionClaims.TryClaim(rl.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            Check(ExecutionClaims.ReleaseClaim(rl.TaskId, "EXECUTOR", null, v + 1), "release with null reason accepted");
            Check(HasLine("OwnershipReleased #" + rl.TaskId + " owner=EXECUTOR reason=unspecified"), "null reason logged as unspecified");
            Check(ExecutionClaims.TryClaim(rl.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v + 2) == ClaimResult.Granted,
                "released task instantly re-claimable (released, not succeeded)");

            // ---- claim status snapshot ------------------------------------------------
            FreshSetup();
            CapBotTask st = NewQueued("BOT:7", 5);
            v = st.CreatedTimeMs;
            ExecutionClaims.TryClaim(st.TaskId, "EXECUTE", 0, "EXECUTOR", "T1", v);
            List<string> lines = ExecutionClaims.ClaimStatusLines(v + 1000);
            Check(lines.Count == 1 && lines[0].StartsWith(st.TaskId + "|" + id1_of(st.TaskId, "T1") + "|EXECUTOR|") && lines[0].EndsWith("|active"),
                "claim status line format id|actionId|owner|remainMs|active");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}