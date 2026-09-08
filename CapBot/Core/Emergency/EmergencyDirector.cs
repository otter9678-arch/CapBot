using System;
using System.Collections.Generic;
using CapBot.Core.Capabilities;
using CapBot.Core.Tasks;
using CapBot.Core.World;

namespace CapBot.Core.Emergency
{
    // ---- Phase 9: emergency director ------------------------------------------
    //
    // The deterministic priority-override layer. One evaluation every
    // MinRecheckMs (production: 5 s) — never per frame:
    //
    //   WorldStateService.Latest (P6 snapshot)
    //     -> freshness gate (fail-safe on stale/missing/invalid)
    //     -> EmergencyDetector.Detect (pure rules over verified data)
    //     -> deduplicate against bounded ACTIVE records
    //     -> emergency state machine (hysteresis-gated transitions)
    //     -> emergency decision (bounded data, logged)
    //     -> emergency task through the P2 lifecycle (Create/Register/TryQueue)
    //        — Preemptible="true" metadata lets the P4 scheduler's OWN
    //        policy-gated preemption displace lower-priority running work
    //     -> (scheduler grants it -> P8 executor claims/validates/executes
    //        exactly as for every other task — the director never executes)
    //     -> lifecycle resolution feeds P3 recovery on failure.
    //
    // HARD BOUNDARIES:
    //   - Never executes a capability, never RPCs, never mutates gameplay.
    //   - Never bypasses scheduler/executor/claims/capability validation.
    //   - Never pauses, fails, or cancels other systems' tasks (preemption is
    //     REQUESTED via the scheduler's existing policy-gated path only).
    //   - No LLM/Ollama/Qwen, no natural-language decisions, no chat/NPC/
    //     mission-text parsing. All vocabulary is static code.
    //   - Bounded everywhere: active records <= MaxActiveEmergencies (8),
    //     history <= MaxHistory (16) — nothing grows unbounded.
    //
    // AUTHORITY: with no authority probe wired (or a faulting one), Evaluate
    // is a no-op — deny-by-default, exactly like P5 claims. Production wiring
    // is ExecutionClaims.IsAuthoritative() (wired to PhotonNetwork.
    // isMasterClient at boot), so clients never produce emergency tasks and
    // master-only semantics are inherited, not reimplemented.
    public static class EmergencyDirector
    {
        // ---- cadence + bounds ---------------------------------------------------
        public const int MinRecheckMs = 5000;          // emergency cadence (1 Hz snapshot, 5 s decisions — no per-frame loop)
        public const int MaxActiveEmergencies = 8;     // bounded active set
        public const int MaxHistory = 16;              // bounded resolved history
        public const int EmergencyTaskTimeoutMs = 120000; // emergency tasks self-expire (bounded work)
        public const int ActiveExpiryMs = 30000;       // un-reconfirmed emergency decays after this
        public const int TaskRequeueBlockMs = 20000;   // re-arm delay after a task resolves for its emergency
        public const int StateDwellMs = 5000;          // hysteresis: every state transition needs this much justification
        public const int RecoveryHoldMs = 10000;       // minimum time in Recovery before Normal
        public const int MaxStaleSnapshotMs = 20000;   // fail-safe: no decisions on older snapshots

        // Task vocabulary (static; the same task types the executor dispatches
        // via registered capabilities; "" capability = coordination-only).
        private const string TaskTypeEmergency = "EMERGENCY";

        private sealed class DirectorState
        {
            public readonly Dictionary<string, ActiveEmergency> Active =
                new Dictionary<string, ActiveEmergency>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public EmergencyState State = EmergencyState.Normal;
            public int StateEnteredMs = -1;
            public int LastEvalMs = -1;
            public long Evaluations;
            public long EmergenciesDetected;
            public long TasksCreated;
            public long DuplicatesSuppressed;
            public long StaleRejections;
            public long TransitionsRejected;
            public string LastUncertainReason;
        }

        private static readonly DirectorState S = new DirectorState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed) -------------------------------------
        private static Func<bool> m_AuthorityProbe;      // null/fault => no-op evaluation
        private static Func<int> m_NowMsProvider;        // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_OnDecision;      // EmergencyLogBridge attaches at boot

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_AuthorityProbe = probe; }
        public static void SetNowMsProvider(Func<int> provider) { lock (m_Lock) m_NowMsProvider = provider; }
        public static void SetWorldProvider(Func<WorldSnapshot> provider) { lock (m_Lock) m_WorldProvider = provider; }
        public static void SetDecisionListener(Action<string> listener) { lock (m_Lock) m_OnDecision = listener; }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // ---- readback (diagnostics/tests) ---------------------------------------
        public static EmergencyState CurrentState { get { lock (m_Lock) return S.State; } }
        public static int ActiveCount { get { lock (m_Lock) return S.Active.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long EvaluationCount { get { lock (m_Lock) return S.Evaluations; } }
        public static long EmergenciesDetected { get { lock (m_Lock) return S.EmergenciesDetected; } }
        public static long TasksCreated { get { lock (m_Lock) return S.TasksCreated; } }
        public static long DuplicatesSuppressed { get { lock (m_Lock) return S.DuplicatesSuppressed; } }
        public static long StaleRejections { get { lock (m_Lock) return S.StaleRejections; } }
        public static long TransitionsRejected { get { lock (m_Lock) return S.TransitionsRejected; } }

        public static List<string> ActiveLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, ActiveEmergency> kv in S.Active)
                {
                    lines.Add(kv.Key + "|task=" + kv.Value.TaskId + "|sev=" + kv.Value.Severity);
                }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Returns the number of NEW emergency tasks created this pass (0 on the
        // quiet path AND on every failure path). Never throws.
        public static int Evaluate(int nowMs)
        {
            Func<bool> auth;
            Func<WorldSnapshot> world;
            lock (m_Lock)
            {
                auth = m_AuthorityProbe;
                world = m_WorldProvider;
            }

            // Deny-by-default authority: no probe / faulting probe => no-op.
            // Clients (or an unwired boot) can never produce emergency tasks.
            if (auth == null) return 0;
            bool isAuth;
            try { isAuth = auth(); } catch (Exception) { return 0; }
            if (!isAuth) return 0;

            lock (m_Lock)
            {
                if (S.LastEvalMs >= 0 && unchecked(nowMs - S.LastEvalMs) < MinRecheckMs) return 0;
                S.LastEvalMs = nowMs;
            }

            // ---- fail-safe world gate -----------------------------------------
            WorldSnapshot snap = null;
            if (world != null)
            {
                try { snap = world(); } catch (Exception) { snap = null; }
            }
            if (snap == null || snap.IsNeverCaptured)
            {
                MarkUncertain("no world snapshot captured (fail-safe: no decisions)");
                return 0;
            }
            if (unchecked(nowMs - snap.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snap.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no decisions)");
                return 0;
            }
            if (!snap.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no decisions)");
                return 0;
            }

            // ---- hygiene: expire unconfirmed actives --------------------------
            List<KeyValuePair<string, ActiveEmergency>> expiryPairs = null;
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, ActiveEmergency> kv in S.Active)
                {
                    if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs)
                    {
                        if (expiryPairs == null) expiryPairs = new List<KeyValuePair<string, ActiveEmergency>>();
                        expiryPairs.Add(kv);
                    }
                }
            }
            if (expiryPairs != null)
            {
                foreach (KeyValuePair<string, ActiveEmergency> kv in expiryPairs)
                {
                    ResolveActive(kv.Key, "expired unconfirmed");
                }
            }

            // ---- detection (pure, fail-safe) -----------------------------------
            List<EmergencyDecision> findings = EmergencyDetector.Detect(snap, nowMs);
            int created = 0;
            if (findings != null && findings.Count > 0)
            {
                foreach (EmergencyDecision f in findings)
                {
                    bool createdThis = false;
                    ProcessFinding(f, nowMs, ref createdThis);
                    if (createdThis) created++;
                }
            }

            // ---- state machine ---------------------------------------------------
            UpdateStateMachine(findings, nowMs);

            if (created == 0 && (findings == null || findings.Count == 0))
            {
                // Quiet pass — nothing uncertain, nothing detected.
                lock (m_Lock) S.LastUncertainReason = null;
            }
            lock (m_Lock) S.Evaluations++;
            return created;
        }

        // ---- finding processing (dedup + task creation) ----------------------------
        private static void ProcessFinding(EmergencyDecision finding, int nowMs, ref bool created)
        {
            lock (m_Lock)
            {
                ActiveEmergency active;
                if (S.Active.TryGetValue(finding.EmergencyId, out active))
                {
                    // Same underlying emergency: refresh, never re-create.
                    S.DuplicatesSuppressed++;
                    active.LastSeenMs = nowMs;
                    if (finding.Severity > active.Severity)
                    {
                        // Escalation of a live emergency: escalate severity and
                        // task priority data — but still no second task. The
                        // existing task's escalation path is the lifecycle's
                        // (failure/retry); a new task would duplicate the action.
                        active.Severity = finding.Severity;
                        Emit("EmergencyEscalated " + finding.EmergencyId + " sev=" + finding.Severity
                            + " (existing task #" + active.TaskId + " keeps lifecycle)");
                    }
                    return;
                }

                if (S.Active.Count >= MaxActiveEmergencies)
                {
                    // Bounded set full: shed the OLDEST emergency deterministically.
                    string oldest = null;
                    int oldestSeen = int.MaxValue;
                    foreach (KeyValuePair<string, ActiveEmergency> kv in S.Active)
                    {
                        if (kv.Value.LastSeenMs < oldestSeen) { oldestSeen = kv.Value.LastSeenMs; oldest = kv.Key; }
                    }
                    if (oldest != null) ResolveActive(oldest, "shed: active set full");
                }

                // NEW emergency: create the task through the P2 lifecycle.
                long taskId = CreateEmergencyTask(finding, nowMs);
                if (taskId <= 0)
                {
                    // Registry full / creation refused — fail safe (no task, no
                    // duplicate storm on the next pass because the record is
                    // still created; re-detection refreshes it).
                    Emit("EmergencyTaskRefused " + finding.EmergencyId + " (task creation failed; recorded active to prevent loops)");
                    S.Active[finding.EmergencyId] = new ActiveEmergency(finding.EmergencyId, finding.EmergencyType, 0, nowMs, finding.Severity);
                    return;
                }
                S.Active[finding.EmergencyId] = new ActiveEmergency(finding.EmergencyId, finding.EmergencyType, taskId, nowMs, finding.Severity);
                S.EmergenciesDetected++;
                S.TasksCreated++;
                created = true;
                Emit(finding.ToStatusLine());
            }
        }

        // Deterministically resolve one active emergency (history-bounded).
        private static void ResolveActive(string emergencyId, string reason)
        {
            lock (m_Lock)
            {
                ActiveEmergency removed;
                if (!S.Active.TryGetValue(emergencyId, out removed)) return;
                S.Active.Remove(emergencyId);
                S.HistoryIds.Enqueue(emergencyId);
                while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                Emit("EmergencyResolved " + emergencyId + " reason=" + reason + " (task #" + removed.TaskId + " untouched — lifecycle/recovery own it)");
            }
        }

        // ---- emergency task creation (P2 lifecycle only) ----------------------------
        //
        // The task IS the emergency work item: EMERGENCY type, precedence
        // priority, Preemptible="true" (the scheduler's policy-gated preemption
        // can displace lower-priority running work), bounded timeout. The task
        // carries metadata EmergencyId + CapabilityId + TargetId + EmergencyType
        // so the executor's existing pipeline handles it unchanged.
        private static long CreateEmergencyTask(EmergencyDecision finding, int nowMs)
        {
            string targetKind = ResolveTargetKind(finding);
            CapBotTask task = CapBotTask.Create(
                TaskTypeEmergency,
                "CAPTAIN",
                "emergency: " + finding.Reason,
                finding.Priority,
                1,                        // one retry — recovery may retry once; never a storm
                EmergencyTaskTimeoutMs,
                targetKind,
                finding.TargetReference,
                null);
            if (task == null) return 0;

            if (!task.SetMetadata("EmergencyId", finding.EmergencyId)) return 0;
            if (!task.SetMetadata("EmergencyType", finding.EmergencyType.ToString())) return 0;
            if (!task.SetMetadata("Preemptible", "true")) return 0;
            if (!string.IsNullOrEmpty(finding.RequiredCapability))
            {
                if (!task.SetMetadata("CapabilityId", finding.RequiredCapability)) return 0;
                if (!string.IsNullOrEmpty(finding.TargetReference)
                    && !task.SetMetadata("Argument", finding.TargetReference)) return 0;
            }

            if (!TaskRegistry.Register(task)) return 0;
            if (!task.TryQueue()) return task.TaskId; // registered but queue refused — lifecycle logged it
            Emit("EmergencyTaskCreated #" + task.TaskId + " " + finding.EmergencyId
                + " type=" + finding.EmergencyType + " sev=" + finding.Severity
                + " pri=" + finding.Priority + " cap=" + (finding.RequiredCapability.Length == 0 ? "-" : finding.RequiredCapability));
            return task.TaskId;
        }

        // Target kind vocabulary the capability registry already accepts:
        // SET_CAPTAIN_ORDER takes ORDER (BoundedToken), SET_CAPTAIN_TARGET
        // takes SHIP (ShipId). Unknown capability -> no target requirements.
        private static string ResolveTargetKind(EmergencyDecision finding)
        {
            if (string.IsNullOrEmpty(finding.RequiredCapability)) return string.Empty;
            if (finding.EmergencyType == EmergencyType.DangerousCombat) return "SHIP";
            if (finding.EmergencyType == EmergencyType.ObjectiveCritical) return "MISSION";
            return "ORDER";
        }

        // ---- emergency state machine -------------------------------------------------
        //
        // Maps the strongest current finding severity to a desired state and
        // applies it ONLY through the legal-transition table with dwell-time
        // hysteresis: a transition is applied when the desired state has been
        // continuously justified for StateDwellMs. No oscillation from tiny
        // state changes.
        private static void UpdateStateMachine(List<EmergencyDecision> findings, int nowMs)
        {
            EmergencyState desired;
            if (findings == null || findings.Count == 0)
            {
                // Nothing detected: Normal (via Recovery when leaving Emergency+).
                desired = EmergencyState.Normal;
            }
            else
            {
                EmergencySeverity top = findings[0].Severity; // sorted desc by detector
                if (top >= EmergencySeverity.Critical) desired = EmergencyState.Critical;
                else if (top >= EmergencySeverity.Severe) desired = EmergencyState.Emergency;
                else desired = EmergencyState.Warning;
            }

            lock (m_Lock)
            {
                if (desired == S.State) { S.StateEnteredMs = S.StateEnteredMs < 0 ? nowMs : S.StateEnteredMs; return; }

                // Direct multi-step paths: the state machine's table is the
                // adjacency map; a jump Warning -> Critical is realized as the
                // highest legal target directly (escalation is monotone and
                // emergency-safety-relevant), while de-escalation must pass
                // through Recovery (table-enforced).
                if (!EmergencyStates.CanTransition(S.State, desired))
                {
                    // Not directly legal (e.g. Warning -> Normal). Two legal
                    // options: Recovery (de-escalation) or a higher rung
                    // (escalation). Choose deterministically: escalation up
                    // the chain, else Recovery.
                    EmergencyState step;
                    if (IsEscalation(S.State, desired))
                    {
                        step = NextEscalationRung(S.State);
                        if (step == S.State) return; // nothing legal (should not happen)
                    }
                    else
                    {
                        step = EmergencyState.Recovery;
                    }
                    TryTransition(step, nowMs, "auto-step toward " + desired);
                    return;
                }
                TryTransition(desired, nowMs, findings != null && findings.Count > 0
                    ? "findings present (top=" + findings[0].Severity + ")"
                    : "no findings");
            }
        }

        private static bool IsEscalation(EmergencyState from, EmergencyState to)
        {
            return to > from && to != EmergencyState.Recovery;
        }

        private static EmergencyState NextEscalationRung(EmergencyState from)
        {
            switch (from)
            {
                case EmergencyState.Normal: return EmergencyState.Monitoring;
                case EmergencyState.Monitoring: return EmergencyState.Warning;
                case EmergencyState.Warning: return EmergencyState.Emergency;
                case EmergencyState.Emergency: return EmergencyState.Critical;
                default: return from;
            }
        }

        // Applies one transition with hysteresis + legal-transition enforcement.
        private static void TryTransition(EmergencyState to, int nowMs, string why)
        {
            lock (m_Lock)
            {
                if (to == S.State) return;

                if (!EmergencyStates.CanTransition(S.State, to))
                {
                    S.TransitionsRejected++;
                    Emit("EmergencyTransitionRejected " + S.State + " -> " + to + " ("
                        + EmergencyStates.IllegalReason(S.State, to) + ")");
                    return;
                }

                // Hysteresis: a transition needs the CURRENT state to have been
                // held for at least StateDwellMs (except the first entry from
                // the never-initialized -1 stamp).
                if (S.StateEnteredMs >= 0 && unchecked(nowMs - S.StateEnteredMs) < StateDwellMs) return;

                // Recovery -> Normal additionally requires the recovery hold.
                if (S.State == EmergencyState.Recovery && to == EmergencyState.Normal
                    && S.StateEnteredMs >= 0 && unchecked(nowMs - S.StateEnteredMs) < RecoveryHoldMs)
                    return;

                EmergencyState from = S.State;
                S.State = to;
                S.StateEnteredMs = nowMs;
                Emit("EmergencyState " + from + " -> " + to + " (" + why + ")");
            }
        }

        // ---- fail-safe marker ----------------------------------------------------------
        private static void MarkUncertain(string reason)
        {
            lock (m_Lock)
            {
                S.LastUncertainReason = reason;
                Emit("EmergencyUncertain " + reason);
            }
        }

        // ---- maintenance ------------------------------------------------------------------
        // Resolves active records whose emergency task reached a terminal state
        // (or whose task vanished from the registry), with a re-arm delay so a
        // persisting condition re-arms only after TaskRequeueBlockMs. Called by
        // the driver after Evaluate (bounded, snapshot-free).
        public static void ReconcileTasks(int nowMs)
        {
            List<KeyValuePair<string, ActiveEmergency>> stale = null;
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, ActiveEmergency> kv in S.Active)
                {
                    if (kv.Value.TaskId <= 0) continue;
                    CapBotTask t = TaskRegistry.Get(kv.Value.TaskId);
                    if (t == null)
                    {
                        // Task vanished (history ring / registry reset). Re-arm
                        // only after the block window since creation.
                        if (unchecked(nowMs - kv.Value.TaskCreatedMs) >= TaskRequeueBlockMs)
                        {
                            if (stale == null) stale = new List<KeyValuePair<string, ActiveEmergency>>();
                            stale.Add(kv);
                        }
                        continue;
                    }
                    if (!t.IsTerminal) continue;
                    // Task resolved (Completed/Cancelled/Expired — Failed retries
                    // are recovery's business until terminal). Deactivate the
                    // emergency record; the next detection pass re-arms it after
                    // the block window if the condition persists.
                    if (stale == null) stale = new List<KeyValuePair<string, ActiveEmergency>>();
                    stale.Add(kv);
                }
            }
            if (stale == null) return;
            foreach (KeyValuePair<string, ActiveEmergency> kv in stale)
            {
                ResolveActive(kv.Key, "task resolved");
            }
        }

        // Deterministic diagnostics (bounded).
        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                lines.Add("state=" + S.State + " active=" + S.Active.Count + " history=" + S.HistoryIds.Count);
                lines.Add("evals=" + S.Evaluations + " detected=" + S.EmergenciesDetected
                    + " tasks=" + S.TasksCreated + " dups=" + S.DuplicatesSuppressed
                    + " stale=" + S.StaleRejections + " rejectedTransitions=" + S.TransitionsRejected);
            }
            return lines;
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Active.Clear();
                S.HistoryIds.Clear();
                S.State = EmergencyState.Normal;
                S.StateEnteredMs = -1;
                S.LastEvalMs = -1;
                S.Evaluations = 0;
                S.EmergenciesDetected = 0;
                S.TasksCreated = 0;
                S.DuplicatesSuppressed = 0;
                S.StaleRejections = 0;
                S.TransitionsRejected = 0;
                S.LastUncertainReason = null;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_OnDecision = null;
            }
        }
    }
}