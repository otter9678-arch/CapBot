// Dev-side unit tests for the Phase 7 capability domain (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure capability-domain files (CapabilityDescriptor,
// CapabilityRegistry, RegisteredCapabilities) plus the P2/P5/P6 domain
// files the registry integrates with (CapBotTask, TaskRegistry,
// ActionIdentity, ExecutionClaims, WorldSnapshot, WorldStateService).
// Time is virtual: cooldowns read the injected nowMs provider and claim
// leases use the same virtual clock — no real clock reads, no sleeping.
// Covers the Phase 7 mandated scenarios: registration, duplicate
// registration, unknown rejection, authority rejection, invalid target,
// failed precondition, stale world state, disabled capability,
// deterministic lookup, task/capability mismatch, ownership mismatch,
// duplicate execution claim integration, bounded registry, and capability
// metadata integrity.
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Capabilities;

namespace CapBot.TaskTests
{
    internal static class CapabilityTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        // ---- helpers ----------------------------------------------------------

        // Virtual clock shared by the nowMs provider and the claim probe, so
        // cooldown stamps and claim leases always agree on "now".
        private sealed class VirtualClock { public int NowMs; }
        private static VirtualClock s_Clock;

        private static void StartClock(int nowMs)
        {
            s_Clock = new VirtualClock();
            s_Clock.NowMs = nowMs;
            CapabilityRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
        }

        // Claims policy (deny-by-default otherwise) + registry authority probe
        // pointing at the claims layer, exactly like production wiring.
        private static void Authorize(bool authoritative)
        {
            ExecutionClaims.SetAuthorityPolicy(delegate { return authoritative; });
            CapabilityRegistry.SetAuthorityProbe(delegate { return ExecutionClaims.IsAuthoritative(); });
        }

        // Same claim probe RegisteredCapabilities.AttachProductionSeams wires,
        // but on the virtual clock instead of TaskClock.
        private static void WireClaimProbe()
        {
            CapabilityRegistry.SetClaimProbe(delegate (long taskId, string actionId)
            {
                ClaimInfo claim = ExecutionClaims.GetClaim(taskId, s_Clock.NowMs);
                if (claim.Active) return true;
                return ExecutionClaims.Ledger.Observe(actionId) == ActionOutcome.Succeeded;
            });
        }

        private static bool RegisterBuiltIns()
        {
            return RegisteredCapabilities.RegisterBuiltIns() == RegisteredCapabilities.BuiltInCount;
        }

        // Fresh isolation for every section: registry, task registry, claims
        // (back to deny-by-default) and world service all reset; seams cleared.
        private static void FreshSetup()
        {
            CapabilityRegistry.ResetForTests();
            TaskRegistry.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
        }

        private static CapBotTask LiveTaskOwned(string owner, string taskType, string targetKind, string targetId)
        {
            CapBotTask t = CapBotTask.Create(taskType, owner, "capability test task", 5, 3, -1, targetKind, targetId, null);
            TaskRegistry.Register(t);
            return t;
        }

        private static CapBotTask LiveCaptainTask(string taskType, string targetKind, string targetId)
        {
            return LiveTaskOwned("CAPTAIN", taskType, targetKind, targetId);
        }

        private static CapabilityRequest Req(CapBotTask t, string argument)
        {
            return new CapabilityRequest(t.TaskId, t.TaskType, t.OwnerActorId, t.TargetKind, t.TargetId, argument);
        }

        private static CapabilityRequest ReqOwner(CapBotTask t, string owner, string argument)
        {
            return new CapabilityRequest(t.TaskId, t.TaskType, owner, t.TargetKind, t.TargetId, argument);
        }

        // Minimal read-only descriptor for registry-level tests.
        private static CapabilityDescriptor Simple(string id)
        {
            return new CapabilityDescriptor(id, "T", "d", null, CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.None, null, 0, false, null, null, "test", "CAPABILITY");
        }

        // ---- tests -------------------------------------------------------------

        internal static int Run()
        {
            s_Passed = 0;
            s_Failed = 0;

            RunRegistrationTests();
            RunLookupTests();
            RunValidationTests();
            RunTaskIntegrationTests();
            RunClaimIntegrationTests();
            RunWorldStateTests();
            RunCatalogTests();

            Console.WriteLine("CAPABILITY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        private static void RunRegistrationTests()
        {
            FreshSetup();
            Check(CapabilityRegistry.Count == 0, "registry starts empty");
            Check(CapabilityRegistry.Register(Simple("TEST_CAP")), "valid capability registers");
            Check(CapabilityRegistry.Count == 1, "registered capability counted");

            Check(!CapabilityRegistry.Register(Simple("TEST_CAP")), "duplicate registration rejected");
            Check(CapabilityRegistry.Count == 1, "duplicate registration does not grow the registry");
            Check(!CapabilityRegistry.Register(null), "null descriptor rejected");

            // id vocabulary: [A-Za-z0-9_] only, <= 32 chars
            Check(!CapabilityRegistry.Register(Simple("BAD-ID")), "id with hyphen rejected");
            Check(!CapabilityRegistry.Register(Simple("BAD ID")), "id with space rejected");
            Check(!CapabilityRegistry.Register(Simple("BAD!ID")), "id with punctuation rejected");
            Check(CapabilityRegistry.Register(Simple(new string('A', 32))), "32-char id accepted (MaxIdLength boundary)");
            Check(!CapabilityRegistry.Register(Simple(new string('A', 33))), "33-char id rejected (over MaxIdLength)");

            // bounded registry cap
            FreshSetup();
            int registered = 0;
            for (int i = 0; i < CapabilityRegistry.MaxCapabilities + 5; i++)
            {
                if (CapabilityRegistry.Register(Simple("CAP_" + i))) registered++;
            }
            Check(registered == CapabilityRegistry.MaxCapabilities, "bounded registry: only MaxCapabilities registrations succeed");
            Check(CapabilityRegistry.Count == CapabilityRegistry.MaxCapabilities, "registry Count respects the cap");
            Check(!CapabilityRegistry.IsRegistered("CAP_" + CapabilityRegistry.MaxCapabilities), "beyond-cap capability NOT registered");
            Check(!CapabilityRegistry.Register(Simple("ONE_MORE")), "registration past the cap rejected");
        }

        private static void RunLookupTests()
        {
            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers");

            // deterministic lookup: repeated lookups return the same instance
            CapabilityDescriptor a = CapabilityRegistry.Get(RegisteredCapabilities.SetCaptainOrder);
            CapabilityDescriptor b = CapabilityRegistry.Get(RegisteredCapabilities.SetCaptainOrder);
            Check(a != null && ReferenceEquals(a, b), "deterministic lookup: Get returns the same descriptor instance every time");
            Check(CapabilityRegistry.IsRegistered(RegisteredCapabilities.SetCaptainOrder), "IsRegistered true for built-in id");
            Check(CapabilityRegistry.Get("NO_SUCH_CAPABILITY") == null, "Get of unknown id returns null");
            Check(!CapabilityRegistry.IsRegistered(null), "IsRegistered(null) false");
            Check(!CapabilityRegistry.IsRegistered(""), "IsRegistered(empty) false");
            Check(CapabilityRegistry.Get(null) == null, "Get(null) null-safe");

            // bounded, sorted, deterministic id list
            List<string> ids = CapabilityRegistry.RegisteredIds();
            Check(ids.Count == RegisteredCapabilities.BuiltInCount, "RegisteredIds bounded to the built-in count");
            ids.Sort(StringComparer.Ordinal);
            Check(ids[0] == "ADD_COURSE_GOAL" && ids[ids.Count - 1] == "SET_CAPTAIN_TARGET", "RegisteredIds ordinally sorted (deterministic order)");
            Check(CapabilityRegistry.Count == RegisteredCapabilities.BuiltInCount, "registry Count equals BuiltInCount");
        }

        private static void RunValidationTests()
        {
            // ---- happy path + unknown + malformed ------------------------------------
            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (validation)");
            StartClock(1000);
            Authorize(true);
            CapBotTask t = LiveCaptainTask("NAV_ALIGN", "ORDER", "PROCEED");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(t, "PROCEED"), t) == CapabilityValidation.Approved, "happy path: live owned task + authoritative process approves");

            Check(CapabilityRegistry.Validate("TOTALLY_UNKNOWN", Req(t, "PROCEED"), t) == CapabilityValidation.RejectedUnknownCapability, "unknown capability id rejected");
            Check(CapabilityRegistry.Validate((string)null, Req(t, "PROCEED"), t) == CapabilityValidation.RejectedUnknownCapability, "null capability id rejected");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, null, t) == CapabilityValidation.RejectedInvalidRequest, "null request rejected");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(t, "PROCEED"), null) == CapabilityValidation.RejectedInvalidRequest, "null task rejected");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, new CapabilityRequest(0, t.TaskType, t.OwnerActorId, "ORDER", "PROCEED", null), t) == CapabilityValidation.RejectedInvalidRequest, "request with taskId<=0 rejected");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, new CapabilityRequest(t.TaskId, t.TaskType, null, "ORDER", "PROCEED", null), t) == CapabilityValidation.RejectedInvalidRequest, "request with null owner rejected");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(t, "PROCEED"), t) == CapabilityValidation.RejectedCooldown, "repeat inside cooldown rejected (cooldown stamped on approval only)");

            // ---- disabled capability ---------------------------------------------------
            Check(CapabilityRegistry.SetEnabled(RegisteredCapabilities.SetCaptainOrder, false), "SetEnabled(false) on registered capability succeeds");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(t, "PROCEED"), t) == CapabilityValidation.RejectedDisabled, "disabled capability rejects every request");
            Check(CapabilityRegistry.IsEnabled(RegisteredCapabilities.SetCaptainOrder) == false, "disabled state observed via IsEnabled");
            Check(!CapabilityRegistry.SetEnabled("TOTALLY_UNKNOWN", true), "SetEnabled on unknown id rejected (unknown never enable/disable)");
            Check(CapabilityRegistry.SetEnabled(RegisteredCapabilities.SetCaptainOrder, true), "re-enable succeeds");
            s_Clock.NowMs = 1000 + 2000;
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(t, "PROCEED"), t) == CapabilityValidation.Approved, "after cooldown window the capability approves again");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(t, "PROCEED"), t) == CapabilityValidation.RejectedCooldown, "approval re-stamped the cooldown");

            // ---- authority rejection ----------------------------------------------------
            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (authority)");
            StartClock(1000);
            Authorize(false);
            CapBotTask at = LiveCaptainTask("NAV_ALIGN", "ORDER", "PROCEED");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(at, "PROCEED"), at) == CapabilityValidation.RejectedAuthority, "MasterOnly capability rejected when process not authoritative");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.ReadWorldSnapshot, Req(at, null), at) == CapabilityValidation.Approved, "ReadOnly capability unaffected by authority denial");
            Authorize(true);
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(at, "PROCEED"), at) == CapabilityValidation.Approved, "same request approved once the process is authoritative");

            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (fail-closed authority)");
            StartClock(1000);
            CapBotTask ft = LiveCaptainTask("NAV_ALIGN", "ORDER", "PROCEED");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(ft, "PROCEED"), ft) == CapabilityValidation.RejectedAuthority, "no authority probe wired -> MasterOnly fails closed");
            CapabilityRegistry.SetAuthorityProbe(delegate { throw new InvalidOperationException("boom"); });
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(ft, "PROCEED"), ft) == CapabilityValidation.RejectedAuthority, "faulting authority probe -> fail-closed");

            // ---- actor allowlist -----------------------------------------------------------
            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (actor)");
            StartClock(1000);
            Authorize(true);
            CapBotTask botTask = LiveTaskOwned("BOT:3", "NAV_ALIGN", "ORDER", "PROCEED");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(botTask, "PROCEED"), botTask) == CapabilityValidation.RejectedActorNotAllowed, "task owner outside capability allowlist rejected");
            CapBotTask capTask = LiveCaptainTask("NAV_ALIGN", "ORDER", "PROCEED");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(capTask, "PROCEED"), capTask) == CapabilityValidation.Approved, "task owner inside capability allowlist approved");

            // ---- target validation -----------------------------------------------------------
            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (targets)");
            StartClock(1000);
            Authorize(true);
            CapBotTask shipTask = LiveCaptainTask("NAV_ALIGN", "SHIP", "42");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(shipTask, null), shipTask) == CapabilityValidation.Approved, "SHIP target with integer id approved");
            CapBotTask badKind = LiveCaptainTask("NAV_ALIGN", "SECTOR", "12");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(badKind, null), badKind) == CapabilityValidation.RejectedTargetInvalid, "target kind outside allowlist rejected");
            CapBotTask badId = LiveCaptainTask("NAV_ALIGN", "SHIP", "xyz");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(badId, null), badId) == CapabilityValidation.RejectedTargetInvalid, "non-integer ship id rejected");
            CapBotTask negId = LiveCaptainTask("NAV_ALIGN", "SHIP", "-5");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(negId, null), negId) == CapabilityValidation.RejectedTargetInvalid, "negative ship id rejected");
            CapBotTask emptyId = LiveCaptainTask("NAV_ALIGN", "SHIP", "");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(emptyId, null), emptyId) == CapabilityValidation.RejectedTargetInvalid, "empty target id rejected");
            CapBotTask longToken = LiveCaptainTask("NAV_ALIGN", "ORDER", new string('A', 33));
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(longToken, null), longToken) == CapabilityValidation.RejectedTargetInvalid, "33-char bounded token rejected (over MaxActionKindLength)");
            CapBotTask punctToken = LiveCaptainTask("NAV_ALIGN", "ORDER", "RUN;DROP");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(punctToken, null), punctToken) == CapabilityValidation.RejectedTargetInvalid, "punctuation inside bounded token rejected (untrusted text never becomes a command)");

            // ---- declared precondition / target validator --------------------------------------
            FreshSetup();
            StartClock(1000);
            Authorize(true);
            CapabilityDescriptor gated = new CapabilityDescriptor(
                "PRECONDITION_CAP", "g", "d", null, CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.None, null, 0, false,
                delegate (CapabilityRequest r) { return r.Argument == "GO"; }, null, "test", "CAPABILITY");
            Check(CapabilityRegistry.Register(gated), "precondition capability registers");
            CapBotTask pt = LiveCaptainTask("NAV_ALIGN", null, null);
            Check(CapabilityRegistry.Validate("PRECONDITION_CAP", Req(pt, "GO"), pt) == CapabilityValidation.Approved, "declared precondition passing approves");
            Check(CapabilityRegistry.Validate("PRECONDITION_CAP", Req(pt, "STOP"), pt) == CapabilityValidation.RejectedPrecondition, "declared precondition false rejected");
            Check(CapabilityRegistry.Validate("PRECONDITION_CAP", Req(pt, null), pt) == CapabilityValidation.RejectedPrecondition, "null argument fails precondition");

            CapabilityDescriptor gatedTarget = new CapabilityDescriptor(
                "TARGETVALIDATOR_CAP", "g", "d", null, CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.SectorId, new string[] { "SECTOR" }, 0, false, null,
                delegate (CapabilityRequest r) { return r.TargetId == "42"; }, "test", "CAPABILITY");
            Check(CapabilityRegistry.Register(gatedTarget), "target-validator capability registers");
            Check(CapabilityRegistry.Validate("TARGETVALIDATOR_CAP", new CapabilityRequest(pt.TaskId, pt.TaskType, pt.OwnerActorId, "SECTOR", "42", null), pt) == CapabilityValidation.Approved, "target validator passing approves");
            Check(CapabilityRegistry.Validate("TARGETVALIDATOR_CAP", new CapabilityRequest(pt.TaskId, pt.TaskType, pt.OwnerActorId, "SECTOR", "43", null), pt) == CapabilityValidation.RejectedTargetInvalid, "target validator failing rejects");

            CapabilityDescriptor throwing = new CapabilityDescriptor(
                "THROWING_PRECONDITION_CAP", "g", "d", null, CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.None, null, 0, false,
                delegate (CapabilityRequest r) { throw new InvalidOperationException("boom"); }, null, "test", "CAPABILITY");
            Check(CapabilityRegistry.Register(throwing), "faulting-precondition capability registers");
            Check(CapabilityRegistry.Validate("THROWING_PRECONDITION_CAP", Req(pt, null), pt) == CapabilityValidation.RejectedPrecondition, "faulting precondition -> fail-closed reject (never throws into caller)");

            // ---- task/capability type mismatch -----------------------------------------------
            FreshSetup();
            StartClock(1000);
            Authorize(true);
            CapabilityDescriptor typed = new CapabilityDescriptor(
                "TYPED_CAP", "g", "d", null, CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                new string[] { "SPECIAL_TYPE" }, TargetRequirement.None, null, 0, false, null, null, "test", "CAPABILITY");
            Check(CapabilityRegistry.Register(typed), "task-typed capability registers");
            CapBotTask wrong = LiveCaptainTask("OTHER_TYPE", null, null);
            Check(CapabilityRegistry.Validate("TYPED_CAP", Req(wrong, null), wrong) == CapabilityValidation.RejectedTaskMismatch, "task type outside capability allowlist rejected");
            CapBotTask right = LiveCaptainTask("SPECIAL_TYPE", null, null);
            Check(CapabilityRegistry.Validate("TYPED_CAP", Req(right, null), right) == CapabilityValidation.Approved, "task type inside capability allowlist approved");
        }

        private static void RunTaskIntegrationTests()
        {
            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (ownership)");
            StartClock(1000);
            Authorize(true);
            CapBotTask t = LiveCaptainTask("NAV_ALIGN", "ORDER", "PROCEED");

            CapBotTask orphan = CapBotTask.Create("NAV_ALIGN", "CAPTAIN", "unregistered", 5, 3, -1, "ORDER", "PROCEED", null);
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(orphan, "PROCEED"), orphan) == CapabilityValidation.RejectedOwnershipMismatch, "task not live in registry -> ownership mismatch");

            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, ReqOwner(t, "BOT:9", "PROCEED"), t) == CapabilityValidation.RejectedOwnershipMismatch, "request owner differs from live task owner -> ownership mismatch");

            CapBotTask spoof = CapBotTask.Create("NAV_ALIGN", "captain", "spoof", 5, 3, -1, "ORDER", "PROCEED", null);
            TaskRegistry.Register(spoof);
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(spoof, "PROCEED"), spoof) == CapabilityValidation.RejectedActorNotAllowed, "wrong-case owner fails exact actor allowlist (untrusted data)");

            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, new CapabilityRequest(999, t.TaskType, t.OwnerActorId, "ORDER", "PROCEED", null), t) == CapabilityValidation.RejectedOwnershipMismatch, "request task id does not reference the live task -> ownership mismatch");

            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(t, "PROCEED"), t) == CapabilityValidation.Approved, "matching live ownership approves");

            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (terminal task)");
            StartClock(1000);
            Authorize(true);
            CapBotTask cancelled = LiveCaptainTask("NAV_ALIGN", "ORDER", "PROCEED");
            cancelled.TryCancel("test");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(cancelled, "PROCEED"), cancelled) == CapabilityValidation.RejectedOwnershipMismatch, "cancelled task no longer live -> ownership mismatch");
        }

        private static void RunClaimIntegrationTests()
        {
            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (claims)");
            StartClock(1000);
            Authorize(true);
            WireClaimProbe();
            CapBotTask t = LiveCaptainTask("NAV_ALIGN", "SHIP", "42");

            Check(ActionIdentity.MakeActionId(1, RegisteredCapabilities.SetCaptainTarget, 0, "42") != null, "CapabilityId is a valid actionKind (shared bounded vocabulary, identity builds)");
            string actionId = ActionIdentity.MakeActionId(t.TaskId, RegisteredCapabilities.SetCaptainTarget, t.RetryCount, t.TargetId);
            Check(actionId != null, "action id builds from CapabilityId + live task");

            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(t, null), t) == CapabilityValidation.Approved, "first request approves (no claim, empty ledger)");
            Check(ExecutionClaims.TryClaim(t.TaskId, RegisteredCapabilities.SetCaptainTarget, t.RetryCount, "CAPTAIN", t.TargetId, s_Clock.NowMs) == ClaimResult.Granted, "executor claim granted under allow policy");
            Check(ExecutionClaims.RecordExecutionResult(t.TaskId, actionId, ActionOutcome.Succeeded, s_Clock.NowMs + 1) == ResultStatus.Recorded, "executor records success");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(t, null), t) == CapabilityValidation.RejectedCooldown, "immediate repeat inside cooldown rejected before the claim gate");
            s_Clock.NowMs = 1000 + 1000;
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(t, null), t) == CapabilityValidation.RejectedClaimConflict, "repeat after cooldown: ledger Succeeded -> duplicate execution rejected");

            // active claim on the task conflicts for any other capability request
            CapBotTask t2 = LiveCaptainTask("NAV_ALIGN", "SECTOR", "12");
            Check(ExecutionClaims.TryClaim(t2.TaskId, "OTHER_KIND", 0, "CAPTAIN", t2.TargetId, s_Clock.NowMs) == ClaimResult.Granted, "second task claim granted");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.AddCourseGoal, Req(t2, null), t2) == CapabilityValidation.RejectedClaimConflict, "unexpired claim on the task -> claim conflict");
            s_Clock.NowMs = 8000; // past the 5s lease
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.AddCourseGoal, Req(t2, null), t2) == CapabilityValidation.Approved, "expired claim lease no longer conflicts");

            FreshSetup();
            Check(RegisterBuiltIns(), "built-in catalog registers (claim probe fault)");
            StartClock(1000);
            Authorize(true);
            CapabilityRegistry.SetClaimProbe(delegate { throw new InvalidOperationException("boom"); });
            CapBotTask t3 = LiveCaptainTask("NAV_ALIGN", "ORDER", "PROCEED");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(t3, "PROCEED"), t3) == CapabilityValidation.RejectedClaimConflict, "faulting claim probe -> fail-closed conflict");
        }

        private static void RunWorldStateTests()
        {
            FreshSetup();
            StartClock(5000);
            Authorize(true);
            CapabilityDescriptor worldCap = new CapabilityDescriptor(
                "FRESH_WORLD_CAP", "g", "d", null, CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.None, null, 0, true, null, null, "test", "CAPABILITY");
            Check(CapabilityRegistry.Register(worldCap), "fresh-world capability registers");
            CapBotTask t = LiveCaptainTask("NAV_ALIGN", null, null);

            Check(CapabilityRegistry.Validate("FRESH_WORLD_CAP", Req(t, null), t) == CapabilityValidation.RejectedWorldStateMissing, "RequiresFreshWorldState with no world seam -> RejectedWorldStateMissing");

            ScriptedSource src = new ScriptedSource(SnapFor(5, 5000));
            WorldStateService.SetSource(src);
            CapabilityRegistry.SetWorldProvider(delegate { return WorldStateService.Latest; });
            Check(CapabilityRegistry.Validate("FRESH_WORLD_CAP", Req(t, null), t) == CapabilityValidation.RejectedWorldStateMissing, "world seam wired but never captured -> RejectedWorldStateMissing");

            WorldStateService.Refresh(5000);
            Check(CapabilityRegistry.Validate("FRESH_WORLD_CAP", Req(t, null), t) == CapabilityValidation.Approved, "fresh snapshot -> approved");

            CapabilityRegistry.SetWorldProvider(delegate { throw new InvalidOperationException("boom"); });
            Check(CapabilityRegistry.Validate("FRESH_WORLD_CAP", Req(t, null), t) == CapabilityValidation.RejectedWorldStateMissing, "faulting world provider -> RejectedWorldStateMissing (fail-closed)");

            FreshSetup();
            StartClock(26000);
            CapabilityDescriptor worldCap2 = new CapabilityDescriptor(
                "FRESH_WORLD_CAP", "g", "d", null, CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.None, null, 0, true, null, null, "test", "CAPABILITY");
            Check(CapabilityRegistry.Register(worldCap2), "fresh-world capability registers (stale)");
            CapBotTask t2 = LiveCaptainTask("NAV_ALIGN", null, null);
            ScriptedSource freshSrc = new ScriptedSource(SnapFor(5, 26000));
            WorldStateService.SetSource(freshSrc);
            CapabilityRegistry.SetWorldProvider(delegate { return WorldStateService.Latest; });
            WorldStateService.Refresh(26000); // captured at 26000
            Check(CapabilityRegistry.Validate("FRESH_WORLD_CAP", Req(t2, null), t2) == CapabilityValidation.Approved, "fresh snapshot at capture time -> approved");
            s_Clock.NowMs = 26000 + WorldStateService.MaxSnapshotAgeMs + 1; // advance the clock, snapshot ages
            Check(CapabilityRegistry.Validate("FRESH_WORLD_CAP", Req(t2, null), t2) == CapabilityValidation.RejectedWorldStateStale, "snapshot older than MaxSnapshotAgeMs -> RejectedWorldStateStale");

            FreshSetup();
            StartClock(26000);
            CapabilityDescriptor worldCap3 = new CapabilityDescriptor(
                "FRESH_WORLD_CAP", "g", "d", null, CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.None, null, 0, true, null, null, "test", "CAPABILITY");
            Check(CapabilityRegistry.Register(worldCap3), "fresh-world capability registers (boundary)");
            CapBotTask t3 = LiveCaptainTask("NAV_ALIGN", null, null);
            ScriptedSource boundarySrc = new ScriptedSource(SnapFor(5, 16000));
            WorldStateService.SetSource(boundarySrc);
            CapabilityRegistry.SetWorldProvider(delegate { return WorldStateService.Latest; });
            WorldStateService.Refresh(26000);
            Check(CapabilityRegistry.Validate("FRESH_WORLD_CAP", Req(t3, null), t3) == CapabilityValidation.Approved, "snapshot exactly at MaxSnapshotAgeMs boundary -> still fresh");
        }

        private static void RunCatalogTests()
        {
            FreshSetup();
            Check(RegisterBuiltIns(), "RegisterBuiltIns registers exactly BuiltInCount capabilities");
            Check(RegisteredCapabilities.RegisterBuiltIns() == 0, "RegisterBuiltIns duplicate-safe (second call registers nothing)");
            Check(CapabilityRegistry.Count == RegisteredCapabilities.BuiltInCount, "registry Count == BuiltInCount after registration");

            string[] expected = {
                RegisteredCapabilities.SetCaptainOrder, RegisteredCapabilities.IssueMoveOrder,
                RegisteredCapabilities.SetCaptainTarget, RegisteredCapabilities.AddCourseGoal,
                RegisteredCapabilities.RemoveCourseGoal, RegisteredCapabilities.ClearCourseGoals,
                RegisteredCapabilities.ReadWorldSnapshot };
            for (int i = 0; i < expected.Length; i++)
                Check(CapabilityRegistry.IsRegistered(expected[i]), "built-in registered: " + expected[i]);

            // authority + verification status per capability (metadata integrity)
            CapabilityDescriptor d = CapabilityRegistry.Get(RegisteredCapabilities.SetCaptainOrder);
            Check(d.Authority == CapabilityAuthority.MasterOnly && d.TargetReq == TargetRequirement.BoundedToken && d.CooldownMs == 2000
                && d.VerifiedApi.Contains("PLServer.CaptainSetOrderID(Int32)") && d.VerifiedApi.Contains("VERIFIED")
                && d.AllowedOwners.Count == 1 && d.AllowedOwners[0] == "CAPTAIN",
                "SET_CAPTAIN_ORDER: MasterOnly, ORDER token, 2000ms, verified RPC, CAPTAIN owner");
            d = CapabilityRegistry.Get(RegisteredCapabilities.IssueMoveOrder);
            Check(d.Authority == CapabilityAuthority.MasterOnly && d.TargetReq == TargetRequirement.None && d.CooldownMs == 1000
                && d.VerifiedApi.Contains("PLPlayer.IssueMoveOrder(Vector3)") && d.VerifiedApi.Contains("VERIFIED"),
                "ISSUE_MOVE_ORDER: MasterOnly, no target requirement, verified RPC");
            d = CapabilityRegistry.Get(RegisteredCapabilities.SetCaptainTarget);
            Check(d.Authority == CapabilityAuthority.MasterOnly && d.TargetReq == TargetRequirement.ShipId && d.CooldownMs == 1000
                && d.VerifiedApi.Contains("PLShipInfoBase.Captain_SetTargetShip(Int32)") && d.VerifiedApi.Contains("VERIFIED"),
                "SET_CAPTAIN_TARGET: MasterOnly, ShipId target, verified RPC");
            d = CapabilityRegistry.Get(RegisteredCapabilities.AddCourseGoal);
            Check(d.Authority == CapabilityAuthority.MasterOnly && d.TargetReq == TargetRequirement.SectorId && d.CooldownMs == 1000
                && d.VerifiedApi.Contains("PLServer.AddCourseGoal(Int32)") && d.VerifiedApi.Contains("VERIFIED"),
                "ADD_COURSE_GOAL: MasterOnly, SectorId target, verified RPC");
            d = CapabilityRegistry.Get(RegisteredCapabilities.RemoveCourseGoal);
            Check(d.Authority == CapabilityAuthority.MasterOnly && d.TargetReq == TargetRequirement.SectorId && d.CooldownMs == 1000
                && d.VerifiedApi.Contains("PLServer.RemoveCourseGoal(Int32)") && d.VerifiedApi.Contains("VERIFIED"),
                "REMOVE_COURSE_GOAL: MasterOnly, SectorId target, verified RPC");
            d = CapabilityRegistry.Get(RegisteredCapabilities.ClearCourseGoals);
            Check(d.Authority == CapabilityAuthority.MasterOnly && d.TargetReq == TargetRequirement.None && d.CooldownMs == 5000
                && d.VerifiedApi.Contains("PLServer.ClearCourseGoals()") && d.VerifiedApi.Contains("VERIFIED"),
                "CLEAR_COURSE_GOALS: MasterOnly, no target, 5000ms cooldown, verified RPC");
            d = CapabilityRegistry.Get(RegisteredCapabilities.ReadWorldSnapshot);
            Check(d.Authority == CapabilityAuthority.ReadOnly && d.TargetReq == TargetRequirement.None && d.CooldownMs == 0
                && d.Danger == CapabilityDanger.Benign && d.Reversibility == CapabilityReversibility.NotApplicable
                && d.VerifiedApi.Contains("WorldStateService.Latest"),
                "READ_WORLD_SNAPSHOT: ReadOnly, benign, no cooldown, Phase 6 API");

            // every descriptor bounded + complete + contract line carries the id
            List<string> ids = CapabilityRegistry.RegisteredIds();
            foreach (string id in ids)
            {
                CapabilityDescriptor c = CapabilityRegistry.Get(id);
                bool bounded = c.CapabilityId.Length <= CapabilityDescriptor.MaxIdLength
                    && c.Name.Length <= CapabilityDescriptor.MaxNameLength
                    && c.Description.Length <= CapabilityDescriptor.MaxDescriptionLength
                    && c.AllowedOwners.Count <= CapabilityDescriptor.MaxAllowedOwners
                    && c.TaskTypes.Count <= CapabilityDescriptor.MaxTaskTypes
                    && c.AllowedTargetKinds.Count <= CapabilityDescriptor.MaxTargetKinds
                    && c.CooldownMs >= 0
                    && !string.IsNullOrEmpty(c.VerifiedApi)
                    && !string.IsNullOrEmpty(c.LoggingCategory);
                Check(bounded, "metadata bounded and complete: " + id);
                Check(c.ToContractLine().Contains(id), "ToContractLine carries the id: " + id);
            }

            List<string> lines = CapabilityRegistry.StatusLines(0);
            Check(lines.Count == RegisteredCapabilities.BuiltInCount, "StatusLines covers every capability");
            bool sorted = true;
            for (int i = 1; i < lines.Count; i++) sorted &= string.CompareOrdinal(lines[i - 1], lines[i]) < 0;
            Check(sorted, "StatusLines deterministically sorted");

            Check(CapabilityRegistry.IsEnabled(RegisteredCapabilities.SetCaptainOrder), "built-in starts enabled");
            CapabilityRegistry.SetEnabled(RegisteredCapabilities.SetCaptainOrder, false);
            Check(!CapabilityRegistry.IsEnabled(RegisteredCapabilities.SetCaptainOrder), "SetEnabled(false) observed");
            CapabilityRegistry.SetEnabled(RegisteredCapabilities.SetCaptainOrder, true);
            Check(CapabilityRegistry.IsEnabled(RegisteredCapabilities.SetCaptainOrder), "SetEnabled(true) restores");
        }

        // ---- world helpers -------------------------------------------------------

        private sealed class ScriptedSource : IWorldSource
        {
            private readonly WorldSnapshot m_Next;
            public ScriptedSource(WorldSnapshot next) { m_Next = next; }
            public WorldSnapshot Capture(WorldSnapshot previous, int nowMs) { return m_Next; }
        }

        private static WorldSnapshot SnapFor(int sectorId, int timeMs)
        {
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                new List<ShipSnapshot>(), new List<CrewMemberSnapshot>(), new List<MissionSnapshot>(),
                null,
                new NavigationSnapshot(sectorId, null, -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false),
                null,
                null,
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved);
        }
    }
}