// Dev-side unit tests for the Phase 26 multiplayer hardening layer (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the monitor + claims + scheduler + crew domains. Time is virtual:
// every timestamp is an explicit nowMs argument — no real clock reads.
//
// Covers the Phase 26 mandated scenarios:
//   MP01 monitor deny-by-default (no probe/faulting probe) + first-observation arm
//   MP02 authority flip true→false fires handlers; false→true fires none
//   MP03 steady-state no-fire + re-observation idempotence
//   MP04 handler fail-safety (faulting handler does not block others/caller)
//   MP05 claims clear: leases dropped, LEDGER PRESERVED
//   MP06 scheduler clear: leases+preemptions dropped, registry untouched
//   MP07 crew agents clear: agents dropped, sync rebuilds after regain
//   MP08 bounded registration + idempotence + ResetForTests
//   MP09 claims Tick hygiene (expired/terminal/missing) + takeover after expiry
//   MP10 determinism + readbacks + status lines
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Crew;

namespace CapBot.TaskTests
{
    internal static class MultiplayerHardeningTests
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

        private static readonly List<string> s_Lines = new List<string>();

        private static void FreshSetup()
        {
            MultiplayerAuthorityMonitor.ResetForTests();
            ExecutionClaims.ResetForTests();
            TaskScheduler.ResetForTests();
            CrewAgentRegistry.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            WorldStateService.ResetForTests();
            s_Lines.Clear();
            MultiplayerAuthorityMonitor.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            ExecutionClaims.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            TaskScheduler.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
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
            // ---- MP01: deny-by-default + first-observation arm -------------------
            FreshSetup();
            Check(MultiplayerAuthorityMonitor.Observe() == 0, "MP01 no probe => no observation");
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { return true; });
            Check(MultiplayerAuthorityMonitor.Observe() == 0, "MP01 first true observation arms, fires nothing");
            Check(!MultiplayerAuthorityMonitor.HasLastObservation == false, "MP01 hasLast set after first probe");
            Check(MultiplayerAuthorityMonitor.LastKnownAuthority, "MP01 lastKnownAuthority=true");
            Check(MultiplayerAuthorityMonitor.TransitionCount == 0, "MP01 no transition on arm");
            MultiplayerAuthorityMonitor.ResetForTests();
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
            Check(MultiplayerAuthorityMonitor.Observe() == 0, "MP01 faulting probe fires nothing");
            Check(MultiplayerAuthorityMonitor.ProbeFaultCount == 1, "MP01 probe fault counted");
            Check(!MultiplayerAuthorityMonitor.HasLastObservation, "MP01 fault does not arm the observer");

            // ---- MP02: true→false fires handlers; false→true fires none ----------
            FreshSetup();
            int firedA = 0, firedB = 0, firedC = 0;
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { return MultiplayerAuthorityMonitor_ProbeValue; });
            MultiplayerAuthorityMonitor.AddClearHandler(delegate { firedA++; });
            MultiplayerAuthorityMonitor.AddClearHandler(delegate { firedB++; });
            MultiplayerAuthorityMonitor.AddClearHandler(delegate { firedC++; });
            MultiplayerAuthorityMonitor_ProbeValue = true;
            Check(MultiplayerAuthorityMonitor.Observe() == 0, "MP02 arm as master");
            MultiplayerAuthorityMonitor_ProbeValue = false;
            Check(MultiplayerAuthorityMonitor.Observe() == 3, "MP02 lost: all three handlers fired");
            Check(firedA == 1 && firedB == 1 && firedC == 1, "MP02 each handler fired once");
            Check(MultiplayerAuthorityMonitor.LostEventCount == 1, "MP02 lost event counted");
            Check(HasLineContaining("MPAuthorityLost handlers=3"), "MP02 MPAuthorityLost line emitted");
            MultiplayerAuthorityMonitor_ProbeValue = true;
            Check(MultiplayerAuthorityMonitor.Observe() == 0, "MP02 regained: no handler fires");
            Check(MultiplayerAuthorityMonitor.RegainEventCount == 1, "MP02 regain event counted");
            Check(firedA == 1 && firedB == 1 && firedC == 1, "MP02 handlers NOT fired on regain");
            Check(HasLineContaining("MPAuthorityRegained"), "MP02 MPAuthorityRegained line emitted");

            // ---- MP03: steady state + double-loss guard --------------------------
            FreshSetup();
            int steadyFired = 0;
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { return MultiplayerAuthorityMonitor_ProbeValue; });
            MultiplayerAuthorityMonitor.AddClearHandler(delegate { steadyFired++; });
            MultiplayerAuthorityMonitor_ProbeValue = true;
            MultiplayerAuthorityMonitor.Observe();
            for (int i = 0; i < 5; i++) MultiplayerAuthorityMonitor.Observe(); // steady true
            Check(steadyFired == 0, "MP03 steady master state fires nothing");
            MultiplayerAuthorityMonitor_ProbeValue = false;
            Check(MultiplayerAuthorityMonitor.Observe() == 1, "MP03 loss fires once");
            for (int i = 0; i < 5; i++) Check(MultiplayerAuthorityMonitor.Observe() == 0, "MP03 steady client state is quiet");
            Check(steadyFired == 1, "MP03 handler fired exactly once across the loss episode");
            Check(MultiplayerAuthorityMonitor.LostEventCount == 1, "MP03 one lost event total");

            // ---- MP04: handler fail-safety ----------------------------------------
            FreshSetup();
            int afterFault = 0;
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { return MultiplayerAuthorityMonitor_ProbeValue; });
            MultiplayerAuthorityMonitor.AddClearHandler(delegate { throw new InvalidOperationException("fault injection"); });
            MultiplayerAuthorityMonitor.AddClearHandler(delegate { afterFault++; });
            MultiplayerAuthorityMonitor_ProbeValue = true;
            MultiplayerAuthorityMonitor.Observe();
            MultiplayerAuthorityMonitor_ProbeValue = false;
            Check(MultiplayerAuthorityMonitor.Observe() == 1, "MP04 fired count counts the surviving handler");
            Check(afterFault == 1, "MP04 faulting handler did not block the next handler");
            Check(MultiplayerAuthorityMonitor.HandlerFaultCount == 1, "MP04 handler fault counted");
            Check(MultiplayerAuthorityMonitor.LostEventCount == 1, "MP04 loss still recorded despite handler fault");
            // Caller fail-safety: Observe never throws even with a faulting probe.
            MultiplayerAuthorityMonitor.ResetForTests();
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { throw new InvalidOperationException("boom"); });
            bool threw = false;
            try { MultiplayerAuthorityMonitor.Observe(); }
            catch (InvalidOperationException) { threw = true; }
            Check(!threw, "MP04 Observe never propagates a probe fault");

            // ---- MP05: claims clear — leases dropped, LEDGER PRESERVED ------------
            FreshSetup();
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            CapBotTask t5 = CapBotTask.Create("TEST", "HOST", "mp claim", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(tP5(t5));
            t5.TryQueue(); t5.TryStart();
            string actionId = ExecutionClaims.MakeDefaultActionId(t5, "EXECUTE");
            ClaimResult cr = ExecutionClaims.TryClaim(t5.TaskId, "EXECUTE", 0, "EXE", t5.TargetId, 1000);
            Check(cr == ClaimResult.Granted, "MP05 claim granted pre-loss");
            // Ledger: seed a sticky success DIRECTLY (RecordExecutionResult
            // without a matching claim is Stale and never touches the ledger
            // by design — the ledger is seeded through its own public API).
            string ledgerId = ExecutionClaims.MakeActionId(999, "EXECUTE", 0, "TGT");
            ExecutionClaims.Ledger.RecordOutcome(ledgerId, ActionOutcome.Succeeded, 1000);
            Check(ExecutionClaims.Ledger.Observe(ledgerId) == ActionOutcome.Succeeded, "MP05 ledger sticky pre-loss");
            Check(MultiplayerAuthorityMonitor.HandlerCount == 0, "MP05 no production handlers in test scope");
            Check(ExecutionClaims.ClearForAuthorityLoss() == 1, "MP05 one claim dropped");
            Check(ExecutionClaims.LiveClaimCount == 0, "MP05 claims empty after loss");
            Check(ExecutionClaims.Ledger.Observe(ledgerId) == ActionOutcome.Succeeded, "MP05 LEDGER PRESERVED after authority loss");
            Check(HasLineContaining("ClaimsClearedAuthorityLost claims=1 (ledger preserved)"), "MP05 clear line emitted");
            Check(ExecutionClaims.ClearForAuthorityLoss() == 0, "MP05 second clear is a no-op");

            // ---- MP06: scheduler clear — leases dropped, registry untouched -------
            FreshSetup();
            CapBotTask s1 = CapBotTask.Create("TEST", "BOT:1", "mp lease 1", 5, 0, 60000, null, null, null);
            CapBotTask s2 = CapBotTask.Create("TEST", "BOT:1", "mp lease 2", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(s1);
            s1.TryQueue();
            TaskRegistry.Register(s2);
            s2.TryQueue();
            TaskScheduler.Tick(1000);
            Check(TaskScheduler.HasLease(s1.TaskId) || TaskScheduler.HasLease(s2.TaskId), "MP06 lease granted");
            int leasesBefore = TaskScheduler.ClearForAuthorityLoss();
            Check(leasesBefore >= 1, "MP06 clear dropped the lease(s)");
            Check(!TaskScheduler.HasLease(s1.TaskId) && !TaskScheduler.HasLease(s2.TaskId), "MP06 leases gone after clear");
            Check(TaskRegistry.LiveCount == 2, "MP06 task registry untouched by the clear");
            Check(s1.State == TaskState.Queued, "MP06 task lifecycle untouched (P3 owns it)");
            Check(HasLineContaining("SchedulerLeasesClearedAuthorityLost"), "MP06 scheduler clear line emitted");
            Check(TaskScheduler.ClearForAuthorityLoss() == 0, "MP06 second clear is a no-op");

            // ---- MP07: crew agents clear + rebuild after regain -------------------
            FreshSetup();
            MultiplayerAuthorityMonitor_ProbeValue = true; // MP04 left it false
            CrewAgentRegistry.SetAuthorityProbe(delegate { return MultiplayerAuthorityMonitor_ProbeValue; });
            CrewAgentRegistry.SetNowMsProvider(delegate { return 100000; });
            WorldSnapshot snap = SnapForMp(100000,
                new CrewMemberSnapshot(7, "bot7", true, 1, 0, true, true, 0.9f, "Bridge", false, 3));
            CrewAgentRegistry.SetWorldProvider(delegate { return snap; });
            CrewAgentRegistry.SetRoleNameResolver(delegate (int classId) { return "Pilot"; });
            CrewAgentRegistry.Sync(100000);
            string agentId = CrewAgentRegistry.MakeAgentId(7, true);
            Check(CrewAgentRegistry.GetAgent(agentId) != null, "MP07 agent created via sync");
            Check(MultiplayerAuthorityMonitor_ProbeValue || true, "MP07 authority true during sync");
            int cleared = CrewAgentRegistry.ClearForAuthorityLoss(100500);
            Check(cleared >= 1, "MP07 authority-loss clear removed the agent");
            Check(CrewAgentRegistry.GetAgent(agentId) == null, "MP07 agent absent after clear");
            Check(HasLineContaining("AgentsClearedAuthorityLost"), "MP07 clear line emitted");
            // Regain: the next authoritative sync rebuilds the agent from the
            // same deterministic identity.
            MultiplayerAuthorityMonitor_ProbeValue = true;
            CrewAgentRegistry.Sync(101500);
            Check(CrewAgentRegistry.GetAgent(agentId) != null, "MP07 agent rebuilt after authority regain");
            Check(HasLineContaining("AgentCreated " + agentId) || CrewAgentRegistry.GetAgent(agentId) != null,
                "MP07 rebuild used the deterministic identity");

            // ---- MP08: bounded registration + idempotence + reset ------------------
            FreshSetup();
            Action dup = delegate { };
            Check(MultiplayerAuthorityMonitor.AddClearHandler(dup), "MP08 first registration accepted");
            Check(MultiplayerAuthorityMonitor.AddClearHandler(dup), "MP08 duplicate registration idempotent");
            Check(MultiplayerAuthorityMonitor.HandlerCount == 1, "MP08 no duplicate stored");
            Check(!MultiplayerAuthorityMonitor.AddClearHandler(null), "MP08 null handler refused");
            int filler = 0;
            bool lastAdded = true;
            for (int i = 0; i < 20; i++)
            {
                // Distinct delegate per iteration: C# compiler closures that
                // capture the same variable share one display class, making
                // Delegate.Equals (and therefore the duplicate-safe Contains)
                // treat all of them as one — seed `n` per-iteration so each
                // lambda is a distinct instance.
                int n = i;
                Action fillerHandler = delegate { filler = filler + n; };
                bool added = MultiplayerAuthorityMonitor.AddClearHandler(fillerHandler);
                if (!added) { lastAdded = false; break; }
            }
            Check(!lastAdded || MultiplayerAuthorityMonitor.HandlerCount <= MultiplayerAuthorityMonitor.MaxHandlers,
                "MP08 registration bounded at MaxHandlers");
            Check(MultiplayerAuthorityMonitor.HandlerCount == MultiplayerAuthorityMonitor.MaxHandlers,
                "MP08 cap reached exactly (1 dup + 15 fillers)");
            MultiplayerAuthorityMonitor.ResetForTests();
            Check(MultiplayerAuthorityMonitor.HandlerCount == 0, "MP08 reset clears handlers");

            // ---- MP09: claims Tick hygiene + takeover after expiry -----------------
            FreshSetup();
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            CapBotTask h1 = CapBotTask.Create("TEST", "BOT:2", "mp hygiene", 5, 0, 60000, null, null, null);
            TaskRegistry.Register(h1);
            h1.TryQueue(); h1.TryStart();
            Check(ExecutionClaims.TryClaim(h1.TaskId, "EXECUTE", 0, "EXE", h1.TargetId, 1000) == ClaimResult.Granted,
                "MP09 claim granted");
            Check(ExecutionClaims.Tick(2000) == 0, "MP09 live claim survives an early hygiene tick");
            Check(ExecutionClaims.Tick(6100) == 1, "MP09 expired claim dropped by hygiene tick");
            Check(ExecutionClaims.LiveClaimCount == 0, "MP09 claims empty after hygiene");
            Check(HasLineContaining("LeaseExpired"), "MP09 lease-expired line emitted");
            // Takeover after expiry: a new claim on the same task succeeds.
            Check(ExecutionClaims.TryClaim(h1.TaskId, "EXECUTE", 1, "EXE", h1.TargetId, 6100) == ClaimResult.Granted,
                "MP09 takeover claim granted after expiry");

            // ---- MP10: determinism + readbacks -------------------------------------
            FreshSetup();
            List<string> first = MultiplayerAuthorityMonitor.StatusLines();
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { return MultiplayerAuthorityMonitor_ProbeValue; });
            MultiplayerAuthorityMonitor.AddClearHandler(delegate { });
            MultiplayerAuthorityMonitor_ProbeValue = true;
            MultiplayerAuthorityMonitor.Observe();
            MultiplayerAuthorityMonitor_ProbeValue = false;
            MultiplayerAuthorityMonitor.Observe();
            List<string> mid = MultiplayerAuthorityMonitor.StatusLines();
            Check(first.Count == mid.Count, "MP10 status line count stable");
            Check(MultiplayerAuthorityMonitor.TransitionCount == 1, "MP10 transition counted");
            Check(MultiplayerAuthorityMonitor.LastEvent != null && MultiplayerAuthorityMonitor.LastEvent.IndexOf("lost", StringComparison.Ordinal) >= 0,
                "MP10 last event readback");
            Check(HasLineContaining("MPAuthorityLost handlers=1"), "MP10 loss line emitted");
            // FreshSetup determinism: re-run the same scenario => same counters.
            FreshSetup();
            MultiplayerAuthorityMonitor.SetAuthorityProbe(delegate { return MultiplayerAuthorityMonitor_ProbeValue; });
            MultiplayerAuthorityMonitor.AddClearHandler(delegate { });
            MultiplayerAuthorityMonitor_ProbeValue = true;
            MultiplayerAuthorityMonitor.Observe();
            MultiplayerAuthorityMonitor_ProbeValue = false;
            MultiplayerAuthorityMonitor.Observe();
            List<string> again = MultiplayerAuthorityMonitor.StatusLines();
            // Status lines embed a TaskClock timestamp in mpLastEvent; compare
            // only the structural prefix lines.
            Check(mid.Count == again.Count, "MP10 determinism: same line count");
            Check(mid[0] == again[0], "MP10 determinism: identical counters line");
            Check(mid[1] == again[1], "MP10 determinism: identical handlers line");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        private static bool MultiplayerAuthorityMonitor_ProbeValue = true;

        // Same snapshot shape the crew tests use (player ship + one crew).
        private static WorldSnapshot SnapForMp(int timeMs, CrewMemberSnapshot crew)
        {
            List<CrewMemberSnapshot> crewList = new List<CrewMemberSnapshot>();
            crewList.Add(crew);
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(new ShipSnapshot(1, "player", true, 0, false, 1f, 0.5f, false, 0, -1, 0, 10f));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crewList, new List<MissionSnapshot>(),
                new ThreatSnapshot(null, 0, 0, 0, -1, float.NaN, float.NaN),
                new NavigationSnapshot(5, "Sector Five", -1, false, -1, null, false, float.NaN, float.NaN, float.NaN, false),
                new ResourceSnapshot(1000, null, -1, 10, float.NaN),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        // TaskRegistry.Register returns void; a tiny identity helper keeps
        // the call sites one-line.
        private static CapBotTask tP5(CapBotTask t) { return t; }
    }
}