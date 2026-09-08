// Dev-side unit tests for the Phase 9 emergency director domain (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1
// against the pure emergency-domain files (EmergencyState, EmergencyDetector,
// EmergencyDirector) plus the P2/P4/P5/P6/P7 domain files the director
// integrates with. Time is virtual: the director reads the injected nowMs
// provider and a stored snapshot — no real clock reads, no sleeping. Every
// evaluation after the first advances the virtual clock past the director's
// 5 s recheck gate (the gate itself is covered by S20's no-storm assertions).
// Covers the Phase 9 mandated scenarios: state transitions (1-5), illegal
// transition rejection (6), precedence (7-10), duplicate detection (11),
// identity determinism (12), stale world (13), invalid target (14),
// authority rejection (15), capability rejection (16), execution-claim
// rejection (17), preemption (18), preempted-recoverable (19), no infinite
// loops (20), bounded history (21), Quality-Improver-safe hostility (22),
// master-only (23), repeated evaluation without duplicate actions (24),
// fail-safe on missing state (25) — plus per-rule detection coverage.
using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Capabilities;
using CapBot.Core.Emergency;

namespace CapBot.TaskTests
{
    internal static class EmergencyTests
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

        private static void FreshSetup()
        {
            EmergencyDirector.ResetForTests();
            TaskRegistry.ResetForTests();
            TaskRecoveryManager.ResetForTests();
            TaskScheduler.ResetForTests();
            CapabilityRegistry.ResetForTests();
            ExecutionClaims.ResetForTests();
            WorldStateService.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 100000 };
            s_Snap = null;
            s_Lines.Clear();
            EmergencyDirector.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            EmergencyDirector.SetWorldProvider(delegate { return s_Snap; });
            EmergencyDirector.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            TaskScheduler.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            EmergencyDirector.SetAuthorityProbe(delegate { return true; });
        }

        private static void Publish(WorldSnapshot snapshot) { s_Snap = snapshot; }
        private static void Advance(int ms) { s_Clock.NowMs += ms; }
        private static int Eval() { return EmergencyDirector.Evaluate(s_Clock.NowMs); }

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

        // ---- snapshot builder (every input defaults to unknown/quiet) --------

        private static CrewMemberSnapshot Bot(int id, float health)
        {
            return new CrewMemberSnapshot(id, "bot" + id, true, 0, 0, true, true, health, "Bridge", false, 3);
        }

        private static MissionSnapshot Mission(int typeId, int total, int completed)
        {
            return new MissionSnapshot(typeId, false, false, total, completed, "objective");
        }

        private static WorldSnapshot Snap(int timeMs, bool gameStarted = true, float hull = 1f,
            int fires = -1, float reactor = float.NaN, CrewMemberSnapshot[] crew = null,
            List<int> hostiles = null, int targetShipId = -1, float targetLevel = float.NaN,
            float ourLevel = float.NaN, bool navMetrics = false, float moved = float.NaN,
            float seeking = float.NaN, int fuel = -1, float coolant = float.NaN,
            MissionSnapshot[] missions = null, bool includePlayerShip = true)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            if (includePlayerShip)
                ships.Add(new ShipSnapshot(1, "player", true, 0, false, hull, 0.5f, false, 0, -1, 0, 10f));
            List<CrewMemberSnapshot> crewList = crew != null ? new List<CrewMemberSnapshot>(crew) : new List<CrewMemberSnapshot>();
            List<MissionSnapshot> missionList = missions != null ? new List<MissionSnapshot>(missions) : new List<MissionSnapshot>();
            return new WorldSnapshot(
                timeMs, gameStarted, true, 7, WorldAuthority.MasterDerived,
                ships, crewList, missionList,
                new ThreatSnapshot(hostiles, 0, 0, 0, targetShipId, targetLevel, ourLevel),
                new NavigationSnapshot(5, "Sector Five", -1, false, -1, null, navMetrics, moved, float.NaN, seeking, false),
                new ResourceSnapshot(1000, null, -1, fuel, coolant),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                fires, reactor);
        }

        private static EmergencyDecision Single(WorldSnapshot snap)
        {
            List<EmergencyDecision> f = EmergencyDetector.Detect(snap, s_Clock.NowMs);
            return f != null && f.Count == 1 ? f[0] : null;
        }

        private static CapBotTask NewRunning(string owner, int priority, bool preemptible)
        {
            CapBotTask t = CapBotTask.Create("SCHED_TYPE", owner, "emergency preemption test", priority, 2, -1, null, null, null);
            TaskRegistry.Register(t);
            t.TryQueue();
            t.TryStart();
            if (preemptible) t.SetMetadata("Preemptible", "true");
            return t;
        }

        private static CapBotTask FindEmergencyTask(string capabilityId)
        {
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            for (int i = 0; i < live.Count; i++)
            {
                CapBotTask t = live[i];
                if (t.TaskType != "EMERGENCY") continue;
                string cap = t.GetMetadata("CapabilityId");
                if (capabilityId == null ? string.IsNullOrEmpty(cap) : cap == capabilityId) return t;
            }
            return null;
        }

        private static CapabilityRequest Req(CapBotTask t, string argument)
        {
            return new CapabilityRequest(t.TaskId, t.TaskType, t.OwnerActorId, t.TargetKind, t.TargetId, argument);
        }

        // ---- tests -----------------------------------------------------------

        internal static int Run()
        {
            // ================================================================
            // S1 (mandated 1): Normal -> Monitoring -> Warning with dwell
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs));
            Check(Eval() == 0, "S1 quiet pass creates nothing");
            Check(EmergencyDirector.CurrentState == EmergencyState.Normal, "S1 stays Normal with no findings");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Check(Eval() == 1, "S1 elevated hull creates exactly one emergency task");
            Check(EmergencyDirector.CurrentState == EmergencyState.Monitoring, "S1 Normal->Monitoring legal auto-step");
            Advance(EmergencyDirector.StateDwellMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Check(Eval() == 0, "S1 repeat detection deduplicated (no second task)");
            Check(EmergencyDirector.CurrentState == EmergencyState.Warning, "S1 Monitoring->Warning after dwell");
            Check(EmergencyDirector.TasksCreated == 1 && EmergencyDirector.EmergenciesDetected == 1,
                "S1 exactly one emergency and one task total");
            Check(CountLines("EmergencyTaskCreated") == 1, "S1 one EmergencyTaskCreated decision emitted");

            // ================================================================
            // S2 (mandated 2): Emergency -> Critical through legal rungs
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, hull: 0.24f));
            Check(Eval() == 1, "S2 critical hull creates task");
            Check(EmergencyDirector.CurrentState == EmergencyState.Monitoring, "S2 escalation starts at Monitoring");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.24f));
            EmergencyDirector.Evaluate(s_Clock.NowMs);
            Check(EmergencyDirector.CurrentState == EmergencyState.Warning, "S2 Monitoring->Warning");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.24f));
            EmergencyDirector.Evaluate(s_Clock.NowMs);
            Check(EmergencyDirector.CurrentState == EmergencyState.Emergency, "S2 Warning->Emergency");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.24f));
            Check(EmergencyDirector.Evaluate(s_Clock.NowMs) == 0, "S2 sustained critical creates no more tasks");
            Check(EmergencyDirector.CurrentState == EmergencyState.Critical, "S2 Emergency->Critical reached");
            Check(EmergencyDirector.TransitionsRejected == 0, "S2 escalation chain stayed legal");
            Check(EmergencyDirector.TasksCreated == 1, "S2 escalation created no extra tasks");

            // ================================================================
            // S3 (mandated 3): de-escalation routes through Recovery + hold
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Eval();                                                  // Monitoring
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Eval();                                                  // Warning
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs));
            Eval();                                                  // -> Recovery
            Check(EmergencyDirector.CurrentState == EmergencyState.Recovery, "S3 Warning->Recovery auto-step applied");
            Advance(2000);
            Publish(Snap(s_Clock.NowMs));
            Eval();
            Check(EmergencyDirector.CurrentState == EmergencyState.Recovery, "S3 recovery hold blocks premature Normal");
            Advance(8000);
            Publish(Snap(s_Clock.NowMs));
            Eval();
            Check(EmergencyDirector.CurrentState == EmergencyState.Normal, "S3 Recovery->Normal after hold");
            Check(EmergencyDirector.TasksCreated == 1 && EmergencyDirector.TransitionsRejected == 0, "S3 bounded, legal");

            // ================================================================
            // S4 (mandated 4): Critical -> Recovery direct legal edge
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, reactor: 0.96f));
            Eval();                                                  // Monitoring
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, reactor: 0.96f));
            Eval();                                                  // Warning
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, reactor: 0.96f));
            Eval();                                                  // Emergency
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, reactor: 0.96f));
            Eval();                                                  // Critical
            Check(EmergencyDirector.CurrentState == EmergencyState.Critical, "S4 reactor critical reaches Critical");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs));
            Eval();
            Check(EmergencyDirector.CurrentState == EmergencyState.Recovery, "S4 Critical->Recovery direct legal");
            Advance(EmergencyDirector.RecoveryHoldMs);
            Publish(Snap(s_Clock.NowMs));
            Eval();
            Check(EmergencyDirector.CurrentState == EmergencyState.Normal, "S4 Recovery->Normal after hold");

            // ================================================================
            // S5 (mandated 5): Warning -> Emergency direct legal on severity
            // escalation of the same emergency
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Eval();                                                  // Monitoring
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Eval();                                                  // Warning
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.33f));
            Check(Eval() == 0, "S5 severity escalation creates no second task");
            Check(HasLineContaining("EmergencyEscalated"), "S5 escalation logged");
            Check(EmergencyDirector.CurrentState == EmergencyState.Emergency, "S5 Warning->Emergency direct legal");
            Check(EmergencyDirector.TasksCreated == 1, "S5 still one task for the same emergency");
            Check(EmergencyDirector.DuplicatesSuppressed >= 1, "S5 dedup counter advanced");

            // ================================================================
            // S6 (mandated 6): illegal transitions are rejected by the table
            // ================================================================
            Check(!EmergencyStates.CanTransition(EmergencyState.Normal, EmergencyState.Emergency), "S6 Normal->Emergency illegal");
            Check(!EmergencyStates.CanTransition(EmergencyState.Normal, EmergencyState.Critical), "S6 Normal->Critical illegal");
            Check(!EmergencyStates.CanTransition(EmergencyState.Recovery, EmergencyState.Warning), "S6 Recovery->Warning illegal");
            Check(!EmergencyStates.CanTransition(EmergencyState.Warning, EmergencyState.Monitoring), "S6 Warning->Monitoring illegal (no reverse skip)");
            Check(!EmergencyStates.CanTransition(EmergencyState.Critical, EmergencyState.Normal), "S6 Critical->Normal illegal (must pass Recovery)");
            Check(EmergencyStates.IllegalReason(EmergencyState.Normal, EmergencyState.Critical).Length > 0, "S6 illegal reason non-empty");
            Check(EmergencyStates.CanTransition(EmergencyState.Normal, EmergencyState.Monitoring), "S6 Normal->Monitoring legal");
            Check(EmergencyStates.CanTransition(EmergencyState.Emergency, EmergencyState.Recovery), "S6 Emergency->Recovery legal");
            Check(EmergencyStates.CanTransition(EmergencyState.Recovery, EmergencyState.Normal), "S6 Recovery->Normal legal");
            // The director itself never applies an illegal transition:
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, hull: 0.24f));
            Eval();
            Check(EmergencyDirector.CurrentState == EmergencyState.Monitoring, "S6 director auto-steps, never jumps Normal->Critical");

            // ================================================================
            // S7-S10 (mandated 7-10): precedence classes + severity bumps
            // ================================================================
            int priHull = EmergencyPrecedence.PriorityFor(EmergencyType.CriticalHull, EmergencySeverity.Critical);
            int priCrew = EmergencyPrecedence.PriorityFor(EmergencyType.CriticalCrewHealth, EmergencySeverity.Critical);
            int priFire = EmergencyPrecedence.PriorityFor(EmergencyType.Fire, EmergencySeverity.Critical);
            int priCombat = EmergencyPrecedence.PriorityFor(EmergencyType.DangerousCombat, EmergencySeverity.Critical);
            int priNav = EmergencyPrecedence.PriorityFor(EmergencyType.NavigationFailure, EmergencySeverity.Warning);
            int priMission = EmergencyPrecedence.PriorityFor(EmergencyType.ObjectiveCritical, EmergencySeverity.Critical);
            Check(priCrew > priHull, "S7 crew survival outranks ship survival");
            Check(priHull > priCombat, "S8 ship survival outranks combat survival");
            Check(priFire > priCombat, "S9 catastrophe class outranks combat class");
            Check(EmergencyPrecedence.PriorityFor(EmergencyType.DangerousCombat, EmergencySeverity.Warning) > priMission,
                "S10 combat Warning still outranks mission Critical");
            Check(priNav > priMission, "S10 navigation safety outranks mission preservation");
            Check(EmergencyPrecedence.PriorityFor(EmergencyType.ObjectiveCritical, EmergencySeverity.Critical) > 13,
                "S10 lowest emergency class outranks highest normal priority plus aging bonus");
            Check(EmergencyPrecedence.PriorityFor(EmergencyType.Fire, EmergencySeverity.Critical)
                > EmergencyPrecedence.PriorityFor(EmergencyType.Fire, EmergencySeverity.Severe),
                "S7 severity bump orders within a class");
            Check(EmergencyPrecedence.ClassFor(EmergencyType.CriticalCrewHealth) == EmergencyPrecedence.ClassCrewSurvival,
                "S7 crew-health maps to crew-survival class");
            Check(EmergencyPrecedence.ClassFor(EmergencyType.CoolantCritical) == EmergencyPrecedence.ClassCatastrophe,
                "S9 coolant maps to catastrophe class");
            Check(EmergencyPrecedence.ClassFor(EmergencyType.FuelCritical) == EmergencyPrecedence.ClassNavigation,
                "S10 fuel maps to navigation class");

            // ================================================================
            // S11 (mandated 11): duplicate detection across passes + distinct
            // subjects get distinct emergencies
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Eval();
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Eval();
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Eval();
            Check(EmergencyDirector.TasksCreated == 1 && EmergencyDirector.DuplicatesSuppressed == 2,
                "S11 same emergency deduplicated across three passes");
            Check(TaskRegistry.LiveSnapshot().Count == 1, "S11 exactly one live task");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.45f, crew: new CrewMemberSnapshot[] { Bot(4, 0.4f) }));
            Check(Eval() == 1, "S11 distinct subject (crew bot) creates its own emergency");
            Check(EmergencyDirector.TasksCreated == 2 && EmergencyDirector.ActiveCount == 2, "S11 two bounded active records");

            // ================================================================
            // S12 (mandated 12): identity determinism
            // ================================================================
            string e1 = EmergencyIdentity.MakeEmergencyId(EmergencyType.Fire, "SHIP|ORDER:6");
            string e2 = EmergencyIdentity.MakeEmergencyId(EmergencyType.Fire, "SHIP|ORDER:6");
            Check(e1 == e2, "S12 same type+key yields identical emergency id");
            Check(EmergencyIdentity.MakeEmergencyId(EmergencyType.Fire, "a") != EmergencyIdentity.MakeEmergencyId(EmergencyType.Fire, "b"),
                "S12 different keys differ");
            Check(EmergencyIdentity.MakeEmergencyId(EmergencyType.Fire, "k") != EmergencyIdentity.MakeEmergencyId(EmergencyType.ReactorCritical, "k"),
                "S12 different types differ");
            Check(e1.StartsWith("EID:", StringComparison.Ordinal), "S12 EID prefix format");
            Check(ActionIdentity.ComputeStableHash("capbot") == ActionIdentity.ComputeStableHash("capbot")
                && ActionIdentity.ComputeStableHash("capbot") != ActionIdentity.ComputeStableHash("capboT"),
                "S12 shared FNV-1a identity hash is stable and case-sensitive");
            List<EmergencyDecision> idA = EmergencyDetector.Detect(Snap(1000, hull: 0.4f), 1000);
            List<EmergencyDecision> idB = EmergencyDetector.Detect(Snap(2000, hull: 0.4f), 2000);
            Check(idA.Count == 1 && idB.Count == 1 && idA[0].EmergencyId == idB[0].EmergencyId,
                "S12 re-detection at a different time yields the same identity");

            // ================================================================
            // S13 (mandated 13): stale / future-dated snapshots fail safe
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs - (EmergencyDirector.MaxStaleSnapshotMs + 1000), hull: 0.24f));
            Check(Eval() == 0, "S13 stale snapshot produces no tasks");
            Check(EmergencyDirector.StaleRejections == 1 && EmergencyDirector.TasksCreated == 0, "S13 stale rejection counted, nothing created");
            Check(HasLineContaining("EmergencyUncertain"), "S13 uncertainty logged");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs + 30000, hull: 0.24f));       // snapshot dated 30 s in the FUTURE
            Check(Eval() == 0, "S13 future-dated snapshot produces no tasks");
            Check(EmergencyDirector.StaleRejections == 2, "S13 future-dated rejection counted");

            // ================================================================
            // S14 (mandated 14): invalid target references are rejected by the
            // registry; valid authoritative ids flow through
            // ================================================================
            FreshSetup();
            RegisteredCapabilities.RegisterBuiltIns();
            CapabilityRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CapabilityRegistry.SetAuthorityProbe(delegate { return true; });
            Publish(Snap(s_Clock.NowMs, hostiles: new List<int> { 9 }, targetShipId: 9));
            Check(Eval() == 1, "S14 combat emergency created");
            CapBotTask ct = FindEmergencyTask(RegisteredCapabilities.SetCaptainTarget);
            Check(ct != null && ct.TargetKind == "SHIP" && ct.TargetId == "9", "S14 combat task carries the authoritative hostile id");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(ct, "9"), ct)
                == CapabilityValidation.Approved, "S14 valid ship-id target approved by registry");
            CapBotTask bad = CapBotTask.Create("EMERGENCY", "CAPTAIN", "corrupt target", 155, 1, -1, "SHIP", "abc", null);
            TaskRegistry.Register(bad);
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(bad, "abc"), bad)
                == CapabilityValidation.RejectedTargetInvalid, "S14 corrupt target reference rejected by registry (never bypassed)");
            CapBotTask neg = CapBotTask.Create("EMERGENCY", "CAPTAIN", "negative target", 155, 1, -1, "SHIP", "-7", null);
            TaskRegistry.Register(neg);
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainTarget, Req(neg, "-7"), neg)
                == CapabilityValidation.RejectedTargetInvalid, "S14 negative ship id rejected by registry");

            // ================================================================
            // S15 (mandated 15): authority rejection (deny-by-default)
            // ================================================================
            FreshSetup();
            EmergencyDirector.SetAuthorityProbe(delegate { return false; });
            Publish(Snap(s_Clock.NowMs, hull: 0.24f));
            Check(Eval() == 0, "S15 non-authoritative probe -> no emergency tasks");
            Check(EmergencyDirector.TasksCreated == 0 && EmergencyDirector.ActiveCount == 0, "S15 nothing recorded client-side");
            EmergencyDirector.SetAuthorityProbe(delegate { throw new InvalidOperationException("probe fault"); });
            Check(Eval() == 0, "S15 faulting authority probe -> fail-closed no-op");
            EmergencyDirector.SetAuthorityProbe(null);
            Check(Eval() == 0, "S15 unwired authority probe -> deny-by-default");
            EmergencyDirector.SetAuthorityProbe(delegate { return true; });
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, hull: 0.24f));
            Check(Eval() == 1, "S15 master authority produces the emergency task");

            // ================================================================
            // S16 (mandated 16): capability rejection is owned by the registry
            // (director never executes or bypasses validation)
            // ================================================================
            FreshSetup();
            RegisteredCapabilities.RegisterBuiltIns();
            CapabilityRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CapabilityRegistry.SetAuthorityProbe(delegate { return true; });
            Publish(Snap(s_Clock.NowMs, hull: 0.45f));
            Check(Eval() == 1, "S16 hull emergency task created");
            CapBotTask et = FindEmergencyTask(RegisteredCapabilities.SetCaptainOrder);
            Check(et != null && "9" == et.GetMetadata("Argument"), "S16 emergency task carries capability + argument metadata");
            Check(et.State == TaskState.Queued, "S16 director only queued the task (never executed)");
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(et, "9"), et)
                == CapabilityValidation.Approved, "S16 emergency order request approved while enabled");
            CapabilityRegistry.SetEnabled(RegisteredCapabilities.SetCaptainOrder, false);
            Check(CapabilityRegistry.Validate(RegisteredCapabilities.SetCaptainOrder, Req(et, "9"), et)
                == CapabilityValidation.RejectedDisabled, "S16 disabled capability rejected (executor-side fail-safe)");
            Check(et.State == TaskState.Queued, "S16 rejection left lifecycle state to the scheduler, not the director");

            // ================================================================
            // S17 (mandated 17): execution-claim rejection applies to
            // emergency tasks (deny-by-default claims preserved)
            // ================================================================
            FreshSetup();
            ExecutionClaims.SetAuthorityPolicy(delegate { return false; });
            CapBotTask em = CapBotTask.Create("EMERGENCY", "CAPTAIN", "emergency: claim test", 172, 1, 120000, "ORDER", "9", null);
            TaskRegistry.Register(em);
            em.TryQueue();
            Check(ExecutionClaims.TryClaim(em.TaskId, "EXECUTE", 0, "EXECUTOR", "9", s_Clock.NowMs)
                == ClaimResult.RejectedNotAuthoritative, "S17 emergency task claim denied without authority");
            ExecutionClaims.SetAuthorityPolicy(delegate { return true; });
            Check(ExecutionClaims.TryClaim(em.TaskId, "EXECUTE", 0, "EXECUTOR", "9", s_Clock.NowMs)
                == ClaimResult.Granted, "S17 with authority the emergency task claims through P5");
            Check(ExecutionClaims.TryClaim(em.TaskId, "EXECUTE", 0, "EXECUTOR", "9", s_Clock.NowMs + 10)
                == ClaimResult.RejectedActiveClaim, "S17 duplicate claim on the emergency task rejected");

            // ================================================================
            // S18 (mandated 18): preemption REQUESTED via the scheduler's own
            // policy path — the director never pauses anything itself
            // ================================================================
            FreshSetup();
            CapBotTask victim = NewRunning("BOT:1", 3, true);
            CapBotTask emPre = CapBotTask.Create("EMERGENCY", "BOT:1", "emergency: hull", 172, 1, 120000, "ORDER", "9", null);
            emPre.SetMetadata("EmergencyId", "EID:TEST:1");
            emPre.SetMetadata("Preemptible", "true");
            TaskRegistry.Register(emPre);
            emPre.TryQueue();
            Check(TaskScheduler.Tick(victim.CreatedTimeMs + 4000) == 1, "S18 scheduler displaces running work for the emergency task");
            Check(victim.State == TaskState.Paused, "S18 victim paused by the scheduler's policy-gated path");
            Check(TaskScheduler.HasLease(emPre.TaskId), "S18 emergency task holds the lease");
            Check(HasLineContaining("Preempted #" + victim.TaskId), "S18 preemption logged by the scheduler");

            // ================================================================
            // S19 (mandated 19): the preempted task stays recoverable
            // (scheduler auto-resumes its own preemption pause once the
            // dominant emergency task is gone)
            // ================================================================
            Check(emPre.TryCancel("emergency resolved"), "S19 emergency task cancellable through the lifecycle");
            Check(TaskScheduler.Tick(victim.CreatedTimeMs + 19000) >= 0, "S19 tick at auto-resume window");
            Check(HasLineContaining("PreemptResume #" + victim.TaskId), "S19 scheduler auto-resumed its own preemption-pause");
            Check(victim.State == TaskState.Running && !victim.IsTerminal, "S19 victim recovered — still live, never cancelled");

            // ================================================================
            // S20 (mandated 20): no infinite loops — a persisting emergency
            // cannot storm tasks, however long it lasts
            // ================================================================
            FreshSetup();
            for (int i = 0; i < 20; i++)
            {
                Publish(Snap(s_Clock.NowMs, hull: 0.24f));
                EmergencyDirector.Evaluate(s_Clock.NowMs);
                EmergencyDirector.ReconcileTasks(s_Clock.NowMs);
                Advance(EmergencyDirector.MinRecheckMs);
            }
            Check(EmergencyDirector.TasksCreated == 1, "S20 persisting emergency created exactly one task across 20 passes");
            Check(EmergencyDirector.DuplicatesSuppressed == 19, "S20 every re-detection was deduplicated");
            Check(EmergencyDirector.EvaluationCount == 20, "S20 all evaluations ran (no stall, no loop)");
            // several distinct persisting emergencies: each gets exactly one
            // task and every repeat is deduplicated; the active set stays
            // bounded and nothing loops.
            FreshSetup();
            for (int i = 0; i < 12; i++)
            {
                Publish(Snap(s_Clock.NowMs, hull: 0.45f, fires: 1, reactor: 0.91f, fuel: 2, coolant: 25f));
                EmergencyDirector.Evaluate(s_Clock.NowMs);
                EmergencyDirector.ReconcileTasks(s_Clock.NowMs);
                Advance(EmergencyDirector.MinRecheckMs);
            }
            Check(EmergencyDirector.TasksCreated == 5, "S20 five distinct persisting emergencies, one task each");
            Check(EmergencyDirector.DuplicatesSuppressed == 55, "S20 every repeat deduplicated (5 x 11 passes)");
            Check(EmergencyDirector.ActiveCount == 5 && EmergencyDirector.HistoryCount == 0,
                "S20 active set bounded and stable under sustained multi-emergency load");

            // ================================================================
            // S21 (mandated 21): bounded active set with deterministic
            // shedding; resolved history bounded
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, hull: 0.45f, fires: 1, reactor: 0.91f, fuel: 2, coolant: 25f,
                hostiles: new List<int> { 9 }, targetShipId: 9, navMetrics: true, moved: 0.2f, seeking: 8f,
                crew: new CrewMemberSnapshot[] { Bot(1, 0.4f) },
                missions: new MissionSnapshot[] { Mission(502, 3, 2) }));
            Eval();
            Check(EmergencyDirector.TasksCreated == 9, "S21 nine distinct emergencies in one pass");
            Check(EmergencyDirector.ActiveCount == EmergencyDirector.MaxActiveEmergencies,
                "S21 single-pass burst: active set bounded at MaxActiveEmergencies");
            Check(EmergencyDirector.HistoryCount == 1, "S21 overflow shed into bounded history");
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            for (int i = 0; i < live.Count; i++) live[i].TryCancel("test cleanup");
            EmergencyDirector.ReconcileTasks(s_Clock.NowMs);
            Check(EmergencyDirector.ActiveCount == 0, "S21 reconcile resolved actives whose tasks reached terminal");
            Check(EmergencyDirector.HistoryCount == 9 && EmergencyDirector.HistoryCount <= EmergencyDirector.MaxHistory,
                "S21 history stays bounded after mass resolution");

            // ================================================================
            // S22 (mandated 22): Quality-Improver-safe hostility — detection
            // reads only the authoritative hostile list, never hostility logic
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, hostiles: new List<int> { 9 }, targetShipId: 9));
            Check(Eval() == 1, "S22 authoritative hostile list yields a combat emergency");
            CapBotTask ct22 = FindEmergencyTask(RegisteredCapabilities.SetCaptainTarget);
            Check(ct22 != null && ct22.TargetId == "9", "S22 target reference is the game's own hostile ship id");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs));
            Check(Eval() == 0, "S22 no hostiles -> no combat emergency (no hostility assumed)");
            List<CapBotTask> live22 = TaskRegistry.LiveSnapshot();
            int combatTasks = 0;
            for (int i = 0; i < live22.Count; i++)
            {
                if (live22[i].GetMetadata("CapabilityId") == RegisteredCapabilities.SetCaptainTarget) combatTasks++;
            }
            Check(combatTasks == 1, "S22 no hostility invented for hostile-free snapshots");

            // ================================================================
            // S23 (mandated 23): master-only — a client produces nothing ever
            // ================================================================
            FreshSetup();
            EmergencyDirector.SetAuthorityProbe(delegate { return false; });
            for (int i = 0; i < 5; i++)
            {
                Publish(Snap(s_Clock.NowMs, hull: 0.24f, fires: 5, crew: new CrewMemberSnapshot[] { Bot(1, 0.2f) }));
                EmergencyDirector.Evaluate(s_Clock.NowMs);
                EmergencyDirector.ReconcileTasks(s_Clock.NowMs);
                Advance(EmergencyDirector.MinRecheckMs);
            }
            Check(EmergencyDirector.TasksCreated == 0 && EmergencyDirector.ActiveCount == 0 && TaskRegistry.LiveCount == 0,
                "S23 client-side evaluation never produces tasks or records");

            // ================================================================
            // S24 (mandated 24): repeated evaluation never duplicates actions
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, hull: 0.45f, fires: 1, fuel: 2));
            Check(Eval() == 3, "S24 three distinct emergencies in one pass");
            for (int i = 0; i < 6; i++)
            {
                Advance(EmergencyDirector.MinRecheckMs);
                Publish(Snap(s_Clock.NowMs, hull: 0.45f, fires: 1, fuel: 2));
                Check(Eval() == 0, "S24 repeat pass creates nothing (pass " + (i + 1) + ")");
            }
            Check(EmergencyDirector.TasksCreated == 3 && EmergencyDirector.DuplicatesSuppressed == 18,
                "S24 exactly one task per distinct emergency across all repeats");
            Check(TaskRegistry.LiveSnapshot().Count == 3, "S24 live task set matches distinct emergencies");

            // ================================================================
            // S25 (mandated 25): fail-safe on missing/invalid state
            // ================================================================
            FreshSetup();
            Publish(Snap(s_Clock.NowMs, includePlayerShip: false));
            Check(Eval() == 0 && EmergencyDirector.TasksCreated == 0, "S25 missing player ship -> no decisions");
            Advance(EmergencyDirector.MinRecheckMs);
            EmergencyDirector.SetWorldProvider(delegate { throw new InvalidOperationException("world fault"); });
            Check(Eval() == 0, "S25 faulting world provider -> fail-closed");
            Check(HasLineContaining("EmergencyUncertain"), "S25 uncertainty logged on fault");
            Advance(EmergencyDirector.MinRecheckMs);
            EmergencyDirector.SetWorldProvider(delegate { return null; });
            Check(Eval() == 0, "S25 null snapshot -> fail-closed");
            Advance(EmergencyDirector.MinRecheckMs);
            EmergencyDirector.SetWorldProvider(delegate { return WorldSnapshot.Empty; });
            Check(Eval() == 0, "S25 never-captured snapshot -> fail-closed");
            Advance(EmergencyDirector.MinRecheckMs);
            Publish(Snap(s_Clock.NowMs, gameStarted: false, hull: 0.24f));
            Check(Eval() == 0, "S25 game-not-started snapshot -> fail-safe");
            Check(EmergencyDirector.ActiveCount == 0 && TaskRegistry.LiveCount == 0, "S25 no state mutated on any failure path");

            // ================================================================
            // Per-rule detection coverage (all nine implemented rules)
            // ================================================================
            EmergencyDecision d;
            d = Single(Snap(1, hull: 0.45f));
            Check(d != null && d.EmergencyType == EmergencyType.CriticalHull && d.Severity == EmergencySeverity.Elevated
                && d.RequiredCapability == "SET_CAPTAIN_ORDER" && d.TargetReference == "9",
                "R1 CriticalHull elevated detects with order-9 capability");
            d = Single(Snap(1, crew: new CrewMemberSnapshot[] { Bot(3, 0.2f) }));
            Check(d != null && d.EmergencyType == EmergencyType.CriticalCrewHealth && d.Severity == EmergencySeverity.Critical
                && "BOT:3" == d.AffectedActor, "R2 CriticalCrewHealth detects the worst bot at Critical");
            d = Single(Snap(1, fires: 5));
            Check(d != null && d.EmergencyType == EmergencyType.Fire && d.Severity == EmergencySeverity.Severe
                && d.RequiredCapability == "SET_CAPTAIN_ORDER" && d.TargetReference == "6",
                "R3 Fire >= 3 detects Severe with order-6 capability");
            d = Single(Snap(1, fires: 1));
            Check(d != null && d.EmergencyType == EmergencyType.Fire && d.Severity == EmergencySeverity.Warning,
                "R3 single fire detects Warning");
            d = Single(Snap(1, reactor: 0.96f));
            Check(d != null && d.EmergencyType == EmergencyType.ReactorCritical && d.Severity == EmergencySeverity.Critical,
                "R4 ReactorCritical at 96% detects Critical");
            d = Single(Snap(1, reactor: 0.91f));
            Check(d != null && d.EmergencyType == EmergencyType.ReactorCritical && d.Severity == EmergencySeverity.Severe,
                "R4 ReactorCritical at 91% detects Severe");
            d = Single(Snap(1, hostiles: new List<int> { 9, 10, 11 }));
            Check(d != null && d.EmergencyType == EmergencyType.DangerousCombat && d.Severity == EmergencySeverity.Severe,
                "R5 DangerousCombat with 3 hostiles detects Severe");
            d = Single(Snap(1, hostiles: new List<int> { 9 }, targetShipId: 9));
            Check(d != null && d.EmergencyType == EmergencyType.DangerousCombat && d.Severity == EmergencySeverity.Warning
                && d.RequiredCapability == "SET_CAPTAIN_TARGET" && d.TargetReference == "9",
                "R5 DangerousCombat with one hostile and unknown levels detects Warning");
            d = Single(Snap(1, navMetrics: true, moved: 0.2f, seeking: 8f));
            Check(d != null && d.EmergencyType == EmergencyType.NavigationFailure && d.Severity == EmergencySeverity.Warning
                && d.RequiredCapability.Length == 0, "R6 NavigationFailure stuck-bot rule detects Warning, coordination-only");
            d = Single(Snap(1, fuel: 1));
            Check(d != null && d.EmergencyType == EmergencyType.FuelCritical && d.Severity == EmergencySeverity.Critical
                && d.TargetReference == "1", "R7 FuelCritical last capsule detects Critical with order-1");
            d = Single(Snap(1, fuel: 2));
            Check(d != null && d.EmergencyType == EmergencyType.FuelCritical && d.Severity == EmergencySeverity.Elevated,
                "R7 FuelCritical low detects Elevated");
            d = Single(Snap(1, coolant: 10f));
            Check(d != null && d.EmergencyType == EmergencyType.CoolantCritical && d.Severity == EmergencySeverity.Critical,
                "R8 CoolantCritical at 10% detects Critical");
            d = Single(Snap(1, missions: new MissionSnapshot[] { Mission(502, 3, 2) }));
            Check(d != null && d.EmergencyType == EmergencyType.ObjectiveCritical && d.Severity == EmergencySeverity.Warning
                && d.RequiredCapability.Length == 0, "R8 ObjectiveCritical final-stretch detects Warning, coordination-only");
            // fail-safe detection inputs (never trigger on unknown data)
            Check(Single(Snap(1, hull: float.NaN)) == null, "S25 NaN hull never triggers");
            Check(Single(Snap(1, includePlayerShip: false)) == null, "S25 no player ship -> no findings");
            Check(EmergencyDetector.Detect(WorldSnapshot.Empty, 1).Count == 0, "S25 never-captured snapshot -> no findings");
            Check(EmergencyDetector.Detect(null, 1) != null && EmergencyDetector.Detect(null, 1).Count == 0,
                "S25 null snapshot -> empty findings (defensive)");
            Check(Single(Snap(1, fuel: -1)) == null, "S25 unknown fuel never triggers");
            Check(Single(Snap(1, fires: -1)) == null, "S25 unknown fire count never triggers");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}