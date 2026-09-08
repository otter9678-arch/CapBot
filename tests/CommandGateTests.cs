// Phase 41: unified command gate tests — the 17 mandated regressions.
// Gates under test: cross-task semantic dedup (fingerprint, never task id),
// no-op suppression against authoritative state, stale-premise supersession,
// bounded failed/cancelled holds, fail-closed unknown/missing-capability
// paths, and the preserved P39 stall discipline. Pure-domain bundle only:
// game-facing paths (PulsarNoOpProbes, dispatcher rejections) are covered by
// a stubbed probe here and verified live in-game.
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Capabilities;
using CapBot.Core.Executor;
using CapBot.Core.Commands;

namespace CapBot.TaskTests
{
    internal static class CommandGateTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        // ---- harness ---------------------------------------------------------

        private sealed class VirtualClock { public int NowMs; }
        private static VirtualClock s_Clock;
        private static WorldSnapshot s_Snap;
        private static readonly List<string> s_Lines = new List<string>();
        private static CommandGate.ProbeAnswer s_ProbeAnswer;
        private static bool s_ProbeThrow;

        private static void FreshSetup()
        {
            CommandGate.ResetForTests();
            CommandGateReasons.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            TaskExecutor.ResetForTests();
            ExecutionClaims.ResetForTests();
            CapabilityRegistry.ResetForTests();
            WorldStateService.ResetForTests();
            // Mirror TaskLogBridge's Track-on-register so recovery tracking
            // (stall reports, budgets) sees tasks exactly as production does.
            TaskRegistry.SetTransitionListener(delegate (CapBotTask t, string label)
            { if (label == "Registered") TaskRecoveryManager.Track(t); });
            // Base the virtual clock on the real one: recovery/scheduler
            // anchors (TaskClock.NowMs at Register/Track) must line up with
            // the virtual now the suites pass into Tick/Check.
            s_Clock = new VirtualClock { NowMs = TaskClock.NowMs };
            s_Snap = FreshCalm(s_Clock.NowMs, 5);
            s_Lines.Clear();
            s_ProbeAnswer = CommandGate.ProbeAnswer.CannotDetermine;
            s_ProbeThrow = false;
            CommandGate.SetWorldProvider(delegate { return s_Snap; });
            CommandGate.SetNoOpProbe(delegate (string capabilityId, CapabilityRequest r)
            {
                if (s_ProbeThrow) throw new InvalidOperationException("probe fault");
                return s_ProbeAnswer;
            });
            CommandGate.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
        }

        private static void Advance(int ms) { s_Clock.NowMs += ms; }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // ---- snapshot builder (sector-parameterized for stale-premise tests) ---

        private static WorldSnapshot FreshCalm(int timeMs, int sectorId)
        {
            return EmergencyCalm(timeMs, sectorId, float.NaN);
        }

        // Same calm world, but with the coolant level parameterized — the
        // COOLANTCRITICAL evidence class (10% => detector fires Critical).
        private static WorldSnapshot EmergencyCalm(int timeMs, int sectorId, float coolantLevelPercent)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 1f, false, 0, -1, 0, 10f, false));
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(new CrewMemberSnapshot(1, "captain-bot", true, 1, 0, true, true, 1f, "Bridge", false, -1));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(sectorId, "Sector " + sectorId, 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, coolantLevelPercent, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // ---- task builders -----------------------------------------------------

        private static CapBotTask MakeQueuedTask(string taskType, string owner, string targetKind,
            string targetId, string capabilityId, string argument)
        {
            return MakeQueuedTask(taskType, owner, targetKind, targetId, capabilityId, argument, 60000);
        }

        private static CapBotTask MakeQueuedTask(string taskType, string owner, string targetKind,
            string targetId, string capabilityId, string argument, int timeoutMs)
        {
            CapBotTask t = CapBotTask.Create(taskType, owner, "CommandGateTests:" + taskType,
                5, 2, timeoutMs, targetKind, targetId, null);
            if (t == null) return null;
            if (capabilityId != null)
            {
                if (!t.SetMetadata(TaskExecutor.MetadataCapabilityId, capabilityId)) return null;
                if (argument != null && !t.SetMetadata(TaskExecutor.MetadataArgument, argument)) return null;
            }
            if (!TaskRegistry.Register(t)) return null;
            if (!t.TryQueue()) return null;
            return t;
        }

        // ---- the seventeen mandated regressions ---------------------------------

        internal static int Run()
        {
            // ---- CG1: same command twice → second suppressed, first keeps working ----
            FreshSetup();
            CapBotTask a1 = MakeQueuedTask("CAPTAIN_DELIB", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            CapBotTask b1 = MakeQueuedTask("CAPTAIN_DELIB", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            Check(a1 != null && b1 != null, "CG1 tasks created");
            GateVerdict vA = CommandGate.Check(a1, s_Clock.NowMs);
            GateVerdict vB = CommandGate.Check(b1, s_Clock.NowMs);
            Check(vA == GateVerdict.Allow, "CG1 first command allowed");
            Check(vB == GateVerdict.SuppressActiveDuplicate, "CG1 second identical command suppressed");
            Check(CommandGate.ResolveSuppressed(b1, "command gate: " + vB), "CG1 suppressed duplicate cancelled");
            Check(b1.State == TaskState.Cancelled && a1.State == TaskState.Queued, "CG1 live task keeps the work");
            Check(CommandGate.SuppressedActiveCount == 1, "CG1 dedup counter");

            // ---- CG2: different task IDs, same semantic action → suppressed ----
            // Different taskType (CAPTAIN_DELIB vs MISSION_WORK), same effective
            // command (capability+action+target+owner). TaskType is NOT part of
            // the identity — cross-author dedup is the mandate's core ask.
            FreshSetup();
            CapBotTask a2 = MakeQueuedTask("CAPTAIN_DELIB", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            CapBotTask b2 = MakeQueuedTask("MISSION_WORK", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            Check(a2 != null && b2 != null && a2.TaskId != b2.TaskId, "CG2 distinct tasks created");
            CommandGate.Check(a2, s_Clock.NowMs);
            GateVerdict vB2 = CommandGate.Check(b2, s_Clock.NowMs);
            Check(vB2 == GateVerdict.SuppressActiveDuplicate, "CG2 same semantic action across task ids suppressed");

            // ---- CG3: state-exists → no-op (SET_CAPTAIN_ORDER(9) when already 9) ----
            FreshSetup();
            s_ProbeAnswer = CommandGate.ProbeAnswer.IsNoOp;
            CapBotTask a3 = MakeQueuedTask("CAPTAIN_ORDER", "CAPTAIN", "ORDER", "9", RegisteredCapabilities.SetCaptainOrder, "9");
            Check(a3 != null, "CG3 task created");
            GateVerdict v3 = CommandGate.Check(a3, s_Clock.NowMs);
            Check(v3 == GateVerdict.SuppressNoOp, "CG3 no-op suppressed when authoritative state already equals desired state");
            Check(CommandGate.SuppressedNoOpCount == 1 && HasLineContaining("reason=no-op-suppressed"), "CG3 no-op counter + line");

            // ---- CG4: bounded retry — identical failure held, own retry exempt ----
            FreshSetup();
            CapBotTask a4 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9");
            CommandGate.Check(a4, s_Clock.NowMs);
            // Mirror the executor's failed attempt: Running -> Failed, then the
            // outcome feedback (RecordOutcome's contract: called post-attempt).
            a4.TryStart();
            a4.TryFail("simulated attempt failure");
            CommandGate.RecordOutcome(a4, false, s_Clock.NowMs);
            CapBotTask b4 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9");
            GateVerdict vB4 = CommandGate.Check(b4, s_Clock.NowMs);
            Check(vB4 == GateVerdict.SuppressRecentFailed, "CG4 identical retry after failure held (no infinite retry)");
            Check(CommandGate.SuppressedFailedCount == 1, "CG4 retrySuppressed counter");
            Check(CommandGate.ResolveSuppressed(b4, "command gate: " + vB4), "CG4 held duplicate resolved (never lingers queued)");
            // Bounded hold: past the failed-hold window (and with the failed
            // original TERMINAL, so no active twin exists) a fresh identical
            // command is allowed again — never an infinite hold.
            Advance(CommandGate.RecentFailedHoldMs + 1);
            CapBotTask c4 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9");
            Check(CommandGate.Check(c4, s_Clock.NowMs) == GateVerdict.Allow, "CG4 hold is bounded (released after window)");
            // The FAILED task's own recovery retry (same TaskId, re-queued via
            // the lifecycle) is never vetoed — retry policy stays exclusively
            // recovery's (P39).
            Check(a4.TryRetry(), "CG4 recovery re-queue accepted (Failed -> Queued)");
            Check(CommandGate.Check(a4, s_Clock.NowMs) == GateVerdict.Allow,
                "CG4 failed task's own bounded retry exempt (recovery owns retry policy)");

            // ---- CG5: bounded stall — P39 report-only discipline preserved ----
            FreshSetup();
            CapBotTask a5 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9");
            Check(a5 != null, "CG5 task created");
            List<string> recoveryLines = new List<string>();
            TaskRecoveryManager.SetActionListener(delegate (CapBotTask t, RecoveryActionType action, string reason, bool acted)
            {
                recoveryLines.Add(action + "|" + reason);
            });
            Advance(TaskRecoveryManager.StallReportAfterMs + 1000);
            TaskRecoveryManager.Tick(s_Clock.NowMs);
            Check(recoveryLines.Count == 1 && recoveryLines[0].IndexOf("ACTION_STALLED", StringComparison.Ordinal) >= 0,
                "CG5 ACTION_STALLED reported");
            Check(a5.State == TaskState.Queued && TaskRegistry.LiveCount == 1,
                "CG5 stall is report-only: task not mutated, nothing recreated by the gate");

            // ---- CG6: timeout terminal ----
            FreshSetup();
            CapBotTask a6 = MakeQueuedTask("CAPTAIN_DELIB", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7", 1000);
            Check(a6 != null, "CG6 task created");
            Advance(2000);
            int expired = TaskRegistry.SweepExpired(s_Clock.NowMs);
            Check(expired == 1 && a6.State == TaskState.Expired, "CG6 timed-out task expired terminally");
            Check(CommandGate.Check(a6, s_Clock.NowMs) == GateVerdict.Allow, "CG6 terminal task outside gate scope (no zombie churn)");

            // ---- CG7: cancelled action not recreated by the same source ----
            // Gate-suppressed duplicate is cancelled; an immediate identical
            // re-authoring is still refused (no-op still true + bounded
            // cancelled-hold) — the loop class "suppress → recreate → suppress"
            // cannot dispatch.
            FreshSetup();
            s_ProbeAnswer = CommandGate.ProbeAnswer.IsNoOp;
            CapBotTask a7 = MakeQueuedTask("CAPTAIN_ORDER", "CAPTAIN", "ORDER", "9", RegisteredCapabilities.SetCaptainOrder, "9");
            GateVerdict v7 = CommandGate.Check(a7, s_Clock.NowMs);
            CommandGate.ResolveSuppressed(a7, "command gate: " + v7);
            Check(a7.State == TaskState.Cancelled, "CG7 suppressed duplicate cancelled");
            CapBotTask b7 = MakeQueuedTask("CAPTAIN_ORDER", "CAPTAIN", "ORDER", "9", RegisteredCapabilities.SetCaptainOrder, "9");
            GateVerdict vB7 = CommandGate.Check(b7, s_Clock.NowMs);
            Check(vB7 != GateVerdict.Allow, "CG7 immediate recreation of cancelled action refused");
            Check(CommandGate.ResolveSuppressed(b7, "command gate: " + vB7) && b7.State == TaskState.Cancelled,
                "CG7 recreation cancelled, never dispatched");

            // ---- CG8: replan → no storm (new plan generation is not a new identity) ----
            FreshSetup();
            CapBotTask a8 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9");
            CommandGate.Check(a8, s_Clock.NowMs);
            // Mirror the executor's successful attempt: Running -> Completed,
            // then the outcome feedback (RecordOutcome's post-attempt contract).
            a8.TryStart();
            a8.TryComplete();
            CommandGate.RecordOutcome(a8, true, s_Clock.NowMs); // attempt succeeded; plan generation re-authors
            CapBotTask b8 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9");
            CapBotTask c8 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9");
            GateVerdict vB8 = CommandGate.Check(b8, s_Clock.NowMs);
            Check(vB8 == GateVerdict.SuppressRecentSucceeded, "CG8 replanned duplicate suppressed");
            GateVerdict vC8 = CommandGate.Check(c8, s_Clock.NowMs);
            Check(vC8 == GateVerdict.SuppressRecentSucceeded, "CG8 replan storm suppressed");
            Check(CommandGate.ResolveSuppressed(b8, "command gate: " + vB8) && CommandGate.ResolveSuppressed(c8, "command gate: " + vC8),
                "CG8 storm twins resolved (no queued zombie duplicates)");
            Advance(CommandGate.RecentSucceededWindowMs + 1);
            CapBotTask d8 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9");
            Check(CommandGate.Check(d8, s_Clock.NowMs) == GateVerdict.Allow, "CG8 window bounded: legitimate later re-issue allowed");

            // ---- CG9: two different Qwen responses, same effective command → suppressed ----
            // Argument normalization (trim/case/whitespace) makes formatting
            // differences non-identities; the fingerprint never sees the task id.
            FreshSetup();
            CapBotTask a9 = MakeQueuedTask("CREW_ADVICE", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, " 7 ");
            CapBotTask b9 = MakeQueuedTask("CREW_ADVICE", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            Check(CommandGate.Normalize("  DePlOY \t to  sector 7 ") == "deploy to sector 7", "CG9 normalization contract");
            CommandGate.Check(a9, s_Clock.NowMs);
            Check(CommandGate.Check(b9, s_Clock.NowMs) == GateVerdict.SuppressActiveDuplicate,
                "CG9 formatting-variant duplicate from advisor flow suppressed");

            // ---- CG10: emergency loop blocked (COOLANTCRITICAL evidence class) ----
            // Known-fail-by-contract EMERGENCY coordination tasks (no capability
            // binding) stay outside the gate (fail closed at the executor per
            // the P9 contract); the emergency AUTHORING loop stays bounded by
            // the P39 SuppressionGate — driven here through the real detector
            // with a coolant-critical snapshot (COOLANTCRITICAL:3f5584d3 class).
            FreshSetup();
            CapBotTask e10 = MakeQueuedTask("EMERGENCY_STUCK", "EMERGENCY", "ORDER", "", null, null);
            Check(e10 != null, "CG10 emergency task created");
            Check(CommandGate.Check(e10, s_Clock.NowMs) == GateVerdict.Allow, "CG10 no-capability task outside gate scope");
            CapBot.Core.Emergency.EmergencyDirector.ResetForTests();
            CapBot.Core.Emergency.EmergencyDirector.SetAuthorityProbe(delegate { return true; });
            CapBot.Core.Emergency.EmergencyDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CapBot.Core.Emergency.EmergencyDirector.SetWorldProvider(delegate { return s_Snap; });
            // One full remediation cycle at constant Critical severity, exactly
            // as EmergencyTests S27 drives it: task created -> executor
            // completes it -> reconcile -> same finding re-detected.
            Func<bool> coolantCycle = delegate
            {
                s_Snap = EmergencyCalm(s_Clock.NowMs, 5, 10f);
                CapBot.Core.Emergency.EmergencyDirector.Evaluate(s_Clock.NowMs);
                List<CapBotTask> liveNow = TaskRegistry.LiveSnapshot();
                for (int i = 0; i < liveNow.Count; i++)
                {
                    if (liveNow[i].GetMetadata("EmergencyId") == null) continue;
                    liveNow[i].TryStart();
                    liveNow[i].TryComplete();
                }
                CapBot.Core.Emergency.EmergencyDirector.ReconcileTasks(s_Clock.NowMs);
                Advance(CapBot.Core.Emergency.EmergencyDirector.MinRecheckMs);
                return true;
            };
            coolantCycle(); coolantCycle(); coolantCycle();
            Advance(CapBot.Core.Emergency.EmergencyDirector.MinRecheckMs);
            s_Snap = EmergencyCalm(s_Clock.NowMs, 5, 10f);
            CapBot.Core.Emergency.EmergencyDirector.Evaluate(s_Clock.NowMs);
            Check(CapBot.Core.Emergency.EmergencyDirector.SuppressionGateCount > 0,
                "CG10 emergency authoring loop bounded by P39 SuppressionGate");

            // ---- CG11: sector transition invalidates stale custom nav commands ----
            FreshSetup();
            CapBotTask a11 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            CommandGate.Check(a11, s_Clock.NowMs); // arms the sector context (5)
            s_Snap = FreshCalm(s_Clock.NowMs, 9);  // authoritative world moved
            CapBotTask b11 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            GateVerdict v11 = CommandGate.Check(b11, s_Clock.NowMs);
            Check(v11 == GateVerdict.SuppressStalePremise, "CG11 sector-moved premise superseded");
            Check(CommandGate.SuppressedStaleCount == 1 && HasLineContaining("reason=stale-premise"), "CG11 superseded counter + line");
            // Non-nav capability, new target ≠ authored context: not
            // premise-bound (P19 family semantics — dispatcher owns validity).
            FreshSetup();
            s_Snap = FreshCalm(s_Clock.NowMs, 5);
            CapBotTask c11 = MakeQueuedTask("MISSION_WORK", "PLANNER", "SECTOR", "8", RegisteredCapabilities.AddCourseGoal, "8");
            CommandGate.Check(c11, s_Clock.NowMs); // arms context (5)
            s_Snap = FreshCalm(s_Clock.NowMs, 9);
            CapBotTask d11 = MakeQueuedTask("MISSION_WORK", "PLANNER", "SECTOR", "8", RegisteredCapabilities.AddCourseGoal, "8");
            Check(CommandGate.Check(d11, s_Clock.NowMs) == GateVerdict.SuppressActiveDuplicate,
                "CG11 non-premise-bound duplicate routes through normal dedup (fingerprint twin still suppressed)");

            // ---- CG12: unknown command type fails closed ----
            FreshSetup();
            RegisteredCapabilities.RegisterBuiltIns();
            CapBotTask a12 = MakeQueuedTask("CUSTOM_MAGIC", "CAPTAIN", "ORDER", "42", "TELEPORT_MOON", "42");
            Check(a12 != null, "CG12 custom task created");
            Check(CommandGate.Check(a12, s_Clock.NowMs) == GateVerdict.Allow, "CG12 gate fails OPEN for unknown capability (uncertainty never rejects)");
            CapabilityRequest r12 = new CapabilityRequest(a12.TaskId, a12.TaskType, a12.OwnerActorId, a12.TargetKind, a12.TargetId, "42");
            Check(CapabilityRegistry.Validate("TELEPORT_MOON", r12, a12) == CapabilityValidation.RejectedUnknownCapability,
                "CG12 registry fails CLOSED for unknown capability (never dispatched)");
            // Same-task TOCTOU re-check: structurally one attempt (the survivor
            // rule is about OTHER tasks) — never self-vetoed.
            Check(CommandGate.Check(a12, s_Clock.NowMs) == GateVerdict.Allow,
                "CG12 unknown capability still fingerprinted (same-task re-check passes)");
            // A fresh twin of the same custom command IS suppressed — custom
            // commands go through the same gate as built-ins.
            CapBotTask b12 = MakeQueuedTask("CUSTOM_MAGIC", "CAPTAIN", "ORDER", "42", "TELEPORT_MOON", "42");
            Check(CommandGate.Check(b12, s_Clock.NowMs) == GateVerdict.SuppressActiveDuplicate,
                "CG12 custom-command twin suppressed (same gate for custom commands)");

            // ---- CG13: missing capability fails closed at the executor ----
            FreshSetup();
            CapBotTask a13 = MakeQueuedTask("EMERGENCY_STUCK", "EMERGENCY", "ORDER", "", null, null);
            TaskScheduler.Tick(s_Clock.NowMs);              // grant
            Advance(300);
            TaskExecutor.Tick(s_Clock.NowMs);               // attempt: no capability bound
            Check(a13.State == TaskState.Failed, "CG13 no-capability task failed (P8 contract, never wedged)");
            Check(a13.FailureReason != null && a13.FailureReason.IndexOf("no capability bound", StringComparison.Ordinal) >= 0,
                "CG13 bounded failure reason (single rejection, no retry storm)");

            // ---- CG14: P39 recovery budget is the backstop, preserved ----
            // (The dispatcher-rejection half of CG14 is game-facing; verified
            // live. Here: the recovery lifetime budget that guarantees a
            // rejected/failed task can never retry forever.)
            FreshSetup();
            CapBotTask a14 = MakeQueuedTask("NAV_RECOVERY", "PLANNER", "SECTOR", "9", RegisteredCapabilities.IssueMoveOrder, "9", 120000);
            a14.TryStart();
            a14.TryFail("simulated repeated rejection");
            bool budgetReached = false;
            for (int i = 0; i < 20 && !budgetReached; i++)
            {
                Advance(TaskRecoveryManager.BackoffDelayMs(a14.RetryCount) + 1100);
                TaskRecoveryManager.Tick(s_Clock.NowMs);
                if (a14.State == TaskState.Cancelled) budgetReached = true; // retries exhausted -> abandonment
                else if (a14.State == TaskState.Queued) { a14.TryStart(); a14.TryFail("simulated repeated rejection"); }
            }
            Check(a14.State == TaskState.Cancelled, "CG14 retries exhausted -> terminal abandonment (never retries forever)");
            Check(a14.CancellationReason != null && a14.CancellationReason.IndexOf("retries exhausted", StringComparison.Ordinal) >= 0,
                "CG14 bounded vocabulary on the terminal abandonment");

            // ---- CG15: non-idempotent duplicate suppressed + registry cooldown guards ----
            FreshSetup();
            RegisteredCapabilities.RegisterBuiltIns();
            CapabilityRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            // Production-shaped authority wiring (CapabilityTests pattern):
            // MasterOnly clears validate only through a real authority probe.
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            CapabilityRegistry.SetAuthorityProbe(delegate { return ExecutionClaims.IsAuthoritative(); });
            CapBotTask a15 = MakeQueuedTask("NAV_RECOVERY", "CAPTAIN", "SECTOR", "-1", RegisteredCapabilities.ClearCourseGoals, "");
            CapBotTask b15 = MakeQueuedTask("NAV_RECOVERY", "CAPTAIN", "SECTOR", "-1", RegisteredCapabilities.ClearCourseGoals, "");
            Check(CommandGate.Check(a15, s_Clock.NowMs) == GateVerdict.Allow, "CG15 first clear allowed (deterministic survivor)");
            Check(CommandGate.Check(b15, s_Clock.NowMs) == GateVerdict.SuppressActiveDuplicate,
                "CG15 non-idempotent duplicate suppressed while first is active");
            Check(CommandGate.ResolveSuppressed(b15, "command gate: duplicate-active"), "CG15 held duplicate resolved");
            Advance(1000);
            // Mirror the executor's successful attempt before the outcome.
            a15.TryStart();
            a15.TryComplete();
            CommandGate.RecordOutcome(a15, true, s_Clock.NowMs); // the clear executed + succeeded
            Advance(CommandGate.RecentSucceededWindowMs + 1);    // past the desired-state reuse window
            CapBotTask c15 = MakeQueuedTask("NAV_RECOVERY", "CAPTAIN", "SECTOR", "-1", RegisteredCapabilities.ClearCourseGoals, "");
            Check(CommandGate.Check(c15, s_Clock.NowMs) == GateVerdict.Allow, "CG15 gate releases after window (bounded)");
            CapabilityRequest r15 = new CapabilityRequest(c15.TaskId, c15.TaskType, c15.OwnerActorId, c15.TargetKind, c15.TargetId, "");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.ClearCourseGoals, r15, c15) == CapabilityValidation.Approved,
                "CG15 first validation after gate release approves (cooldown stamps on approval only)");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.ClearCourseGoals, r15, c15) == CapabilityValidation.RejectedCooldown,
                "CG15 registry cooldown rate-limits immediate repeat clears (idempotency discipline preserved)");

            // ---- CG16: dedup independent of personality initialization ----
            FreshSetup();
            CapBot.Core.Crew.CrewAgentRegistry.ResetForTests(); // zero agents, zero personalities
            CapBotTask a16 = MakeQueuedTask("CAPTAIN_DELIB", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            CapBotTask b16 = MakeQueuedTask("MISSION_WORK", "CAPTAIN", "SECTOR", "7", RegisteredCapabilities.IssueMoveOrder, "7");
            CommandGate.Check(a16, s_Clock.NowMs);
            Check(CommandGate.Check(b16, s_Clock.NowMs) == GateVerdict.SuppressActiveDuplicate,
                "CG16 semantic dedup fully functional with empty crew/personality state");

            // ---- CG17: client authority rules (denial preserved end-to-end) ----
            FreshSetup();
            RegisteredCapabilities.RegisterBuiltIns();
            RegisteredCapabilities.AttachProductionSeams();
            ExecutionClaims.SetAuthorityPolicy(delegate { return false; }); // client
            CapBotTask a17 = MakeQueuedTask("CAPTAIN_ORDER", "CAPTAIN", "ORDER", "9", RegisteredCapabilities.SetCaptainOrder, "9");
            TaskScheduler.Tick(s_Clock.NowMs);
            Advance(300);
            TaskExecutor.Tick(s_Clock.NowMs);
            Check(a17.State == TaskState.Failed, "CG17 client never executes (task failed through the bounded rejection path)");
            Check(a17.FailureReason != null && a17.FailureReason.IndexOf("authority", StringComparison.OrdinalIgnoreCase) >= 0,
                "CG17 authority refusal surfaced (P7 gate ladder owns the denial)");
            // And the client-side duplicate does NOT poison the host record:
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });

            // ---- counters/readbacks sanity (bounded vocabulary) ----
            FreshSetup();
            Check(CommandGate.StatusLines().Count == 2, "CG18 status lines bounded");
            Check(CommandGate.RecordCount == 0 && CommandGate.SeenCount == 0, "CG18 reset clears state");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}