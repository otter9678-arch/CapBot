using System;
using System.Collections.Generic;

namespace CapBot.Core.Tasks
{
    // Outcome of a claim attempt (deterministic — same inputs, same result).
    public enum ClaimResult : byte
    {
        Granted = 0,
        GrantedTakeover = 1,          // an expired claim was explicitly taken over
        DuplicateExecutionRejected = 2, // this exact logical action already succeeded
        RejectedNotAuthoritative = 3, // authority policy denies this process
        RejectedTaskMissing = 4,      // task not in the live registry
        RejectedTaskTerminal = 5,     // task is Completed/Cancelled/Expired
        RejectedRecoveryOwned = 6,    // task is Failed/Paused — Phase 3 recovery owns it
        RejectedOwnedByOther = 7,     // an unexpired claim held by a different owner
        RejectedActiveClaim = 8,      // an unexpired claim held by the same owner (different action identity)
        RejectedInvalid = 9           // malformed arguments (never thrown — refused)
    }

    // Outcome of a result-recording attempt (idempotency layer).
    public enum ResultStatus : byte
    {
        Recorded = 0,            // first result for this claim — recorded, claim released
        DuplicateIgnored = 1,    // same result already recorded (duplicate callback)
        StaleCallbackIgnored = 2,// no matching claim (late/RPC-duplicate callback)
        RejectedNotAuthoritative = 3,
        RejectedInvalid = 4
    }

    // Point-in-time claim snapshot (diagnostics/tests).
    public struct ClaimInfo
    {
        public long TaskId;
        public string ActionId;
        public string Owner;
        public string ActionKind;
        public int AttemptEpoch;
        public int ExpiresAtMs;
        public bool Active; // false when no claim or the lease already expired
    }

    // ---- Phase 5: execution claim safety ------------------------------------
    // The protection layer future executors (P7/P8) must route every gameplay
    // action through. NOT an executor: this code runs no gameplay, touches no
    // game state, and holds no world references. Its entire job:
    //
    //   1. CLAIM — exactly one owner may hold the execution claim for a task
    //      at a time; duplicates are refused deterministically.
    //   2. LEASE — a claim lives ClaimLeaseDurationMs (5 s). Expired claims
    //      are recoverable by anyone (stale owners never retain ownership);
    //      takeover is explicit and logged.
    //   3. IDEMPOTENCY — the ActionLedger remembers action outcomes, so the
    //      same logical execution request (duplicate scheduler pass, repeated
    //      RPC callback, retried executor) can never execute its underlying
    //      action twice: a claim whose action already Succeeded is refused,
    //      and duplicate/late result callbacks are ignored.
    //   4. AUTHORITY — a pluggable deny-by-default policy gate. Phase 8 wires
    //      it to PhotonNetwork.isMasterClient (the verified pattern used by
    //      every vanilla AI path, research §9.1); until then NOTHING can
    //      claim — the layer is inert by construction, same as the scheduler
    //      and recovery before their drivers exist.
    //
    // Scheduler separation (Phase 4 contract): a scheduler GRANT is a
    // selection suggestion (lease in TaskScheduler); a CLAIM is execution
    // safety (this file). They are separate records with separate lifetimes;
    // claiming neither requires nor consumes a grant, so the scheduler never
    // becomes an executor through this layer.
    //
    // Recovery separation (Phase 3 contract): tasks in Failed/Paused are
    // recovery-owned and refuse NEW claims outright; a claim held while
    // recovery pauses the task simply persists (it is the same attempt) and
    // expires naturally if abandoned. Failure results RELEASE the claim
    // immediately (recovery then owns the task; retry re-claims a fresh
    // epoch after backoff). No retry loops are created here — retry policy
    // is exclusively Phase 3's.
    //
    // Multiplayer posture (research §9.1/§13): claims and results are
    // process-local bookkeeping on the authoritative host; no RPCs are sent,
    // nothing syncs. The deny-by-default gate prevents clients from
    // independently claiming authoritative execution. This layer introduces
    // no PhotonTargets.All pattern — vanilla's client→MasterClient request
    // pattern remains the only networked route (P8 policy).
    //
    // Bounded by construction: one claim record per live task (≤ 64), the
    // ledger ≤ 256 entries (FIFO), rejection-log throttle state per claim.
    // Nothing grows without a registered task. No LINQ on any path.
    public static class ExecutionClaims
    {
        public const int ClaimLeaseDurationMs = 5000;  // bounded lease lifetime
        public const int MaxLiveClaims = 64;           // == TaskRegistry.MaxLiveTasks
        private const int RejectEmitGateMs = 1000;     // per-claim rejection-log throttle

        private sealed class ClaimRecord
        {
            public string ActionId;
            public string ActionKind;
            public int AttemptEpoch;
            public string Owner;
            public int ExpiresAtMs;
            public int LastRejectEmitMs = -1; // rejection-log throttle state
        }

        private static readonly Dictionary<long, ClaimRecord> m_Claims =
            new Dictionary<long, ClaimRecord>();
        private static readonly ActionLedger m_Ledger = new ActionLedger();
        private static readonly object m_Lock = new object();
        private static Func<bool> m_AuthorityPolicy;   // null => deny all (fail-closed until P8 wires it)
        private static Action<string> m_OnDecision;    // ClaimLogBridge attaches at boot

        // ---- configuration ---------------------------------------------------
        // Authority seam: returns true when this process may claim/record.
        // Deny-by-default: with no policy set, every claim is refused, so a
        // future executor that skips wiring cannot bypass master-client
        // authority. The delegate must answer from current state only and
        // never throw.
        public static void SetAuthorityPolicy(Func<bool> policy)
        {
            lock (m_Lock) m_AuthorityPolicy = policy;
        }

        public static bool IsAuthoritative()
        {
            Func<bool> f;
            lock (m_Lock) f = m_AuthorityPolicy;
            return f != null && f();
        }

        // Diagnostic hook: one line per claim decision. Fired outside the
        // lock; must never throw.
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

        // Per-claim 1 s rejection throttle: repeated refused attempts against
        // the same claim log at most once per second (hot loops cannot spam).
        private static void EmitRejection(ClaimRecord rec, string line, int nowMs)
        {
            if (rec != null && rec.LastRejectEmitMs >= 0 && unchecked(nowMs - rec.LastRejectEmitMs) < RejectEmitGateMs) return;
            if (rec != null) rec.LastRejectEmitMs = nowMs;
            Emit(line);
        }

        // ---- identity helpers -------------------------------------------------
        public static string MakeActionId(long taskId, string actionKind, int attemptEpoch, string targetKey)
        {
            return ActionIdentity.MakeActionId(taskId, actionKind, attemptEpoch, targetKey);
        }

        // Deterministic default: the attempt epoch of a task's next execution
        // is its retry count (attempt 0 = first run, +1 per recovery retry).
        public static string MakeDefaultActionId(CapBotTask task, string actionKind)
        {
            if (task == null) return null;
            return ActionIdentity.MakeActionId(task.TaskId, actionKind, task.RetryCount, task.TargetId);
        }

        public static int LiveClaimCount { get { lock (m_Lock) return m_Claims.Count; } }

        // The shared idempotency ledger (diagnostics/tests may observe it).
        public static ActionLedger Ledger { get { return m_Ledger; } }

        // Point-in-time claim snapshot (diagnostics/tests). Active reflects
        // lease validity at the caller-supplied time.
        public static ClaimInfo GetClaim(long taskId, int nowMs)
        {
            lock (m_Lock)
            {
                ClaimRecord rec;
                if (!m_Claims.TryGetValue(taskId, out rec))
                {
                    return new ClaimInfo { TaskId = taskId, Active = false };
                }
                return new ClaimInfo
                {
                    TaskId = taskId,
                    ActionId = rec.ActionId,
                    Owner = rec.Owner,
                    ActionKind = rec.ActionKind,
                    AttemptEpoch = rec.AttemptEpoch,
                    ExpiresAtMs = rec.ExpiresAtMs,
                    Active = unchecked(nowMs - rec.ExpiresAtMs) < 0
                };
            }
        }

        public static bool HasClaim(long taskId, int nowMs)
        {
            lock (m_Lock)
            {
                ClaimRecord rec;
                return m_Claims.TryGetValue(taskId, out rec) && unchecked(nowMs - rec.ExpiresAtMs) < 0;
            }
        }

        // ---- claiming ----------------------------------------------------------
        // Attempt to become the single execution owner for one logical action
        // on a task. Deterministic; never throws; refused (never thrown) on
        // every rejection path. actionKind must be a static vocabulary token
        // (validated by ActionIdentity); targetKey is opaque data.
        public static ClaimResult TryClaim(long taskId, string actionKind, int attemptEpoch, string owner, string targetKey, int nowMs)
        {
            if (taskId <= 0 || attemptEpoch < 0 || string.IsNullOrEmpty(owner) || owner.Length > 64)
            {
                Emit("ClaimRejected #" + taskId + " invalid claim arguments");
                return ClaimResult.RejectedInvalid;
            }
            string actionId = ActionIdentity.MakeActionId(taskId, actionKind, attemptEpoch, targetKey);
            if (actionId == null)
            {
                Emit("ClaimRejected #" + taskId + " invalid action identity");
                return ClaimResult.RejectedInvalid;
            }
            if (!IsAuthoritative())
            {
                Emit("ClaimRejected #" + taskId + " not authoritative");
                return ClaimResult.RejectedNotAuthoritative;
            }

            CapBotTask task = TaskRegistry.Get(taskId);
            if (task == null)
            {
                Emit("ClaimRejected #" + taskId + " task not live");
                return ClaimResult.RejectedTaskMissing;
            }
            if (task.IsTerminal)
            {
                Emit("ClaimRejected #" + taskId + " task terminal (" + task.State + ")");
                return ClaimResult.RejectedTaskTerminal;
            }
            if (task.State == TaskState.Failed || task.State == TaskState.Paused)
            {
                Emit("ClaimRejected #" + taskId + " recovery owns task (state=" + task.State + ")");
                return ClaimResult.RejectedRecoveryOwned;
            }

            bool takeover = false;
            ClaimRecord stale = null;
            lock (m_Lock)
            {
                ClaimRecord rec;
                if (m_Claims.TryGetValue(taskId, out rec))
                {
                    if (unchecked(nowMs - rec.ExpiresAtMs) < 0)
                    {
                        // Unexpired claim: only the identical owner may even ask
                        // again, and only to be told "already claimed".
                        if (rec.Owner != owner)
                        {
                            EmitRejection(rec, "ClaimRejected #" + taskId + " owned by " + rec.Owner, nowMs);
                            return ClaimResult.RejectedOwnedByOther;
                        }
                        if (rec.ActionId == actionId)
                        {
                            if (m_Ledger.Observe(actionId) == ActionOutcome.Succeeded)
                            {
                                EmitRejection(rec, "DuplicateExecutionRejected #" + taskId + " " + actionId + " already succeeded", nowMs);
                                return ClaimResult.DuplicateExecutionRejected;
                            }
                            EmitRejection(rec, "ClaimRejected #" + taskId + " duplicate claim (active) by " + owner, nowMs);
                            return ClaimResult.RejectedActiveClaim;
                        }
                        EmitRejection(rec, "ClaimRejected #" + taskId + " active claim on a different action (" + rec.ActionId + ")", nowMs);
                        return ClaimResult.RejectedActiveClaim;
                    }
                    // Expired lease: explicit takeover. Stale owners never retain
                    // ownership — any authorized caller may recover the claim.
                    takeover = true;
                    stale = rec;
                    m_Claims.Remove(taskId);
                }

                // Idempotency gate: this exact logical action already completed.
                if (m_Ledger.Observe(actionId) == ActionOutcome.Succeeded)
                {
                    Emit("DuplicateExecutionRejected #" + taskId + " " + actionId + " already succeeded");
                    return ClaimResult.DuplicateExecutionRejected;
                }

                m_Claims[taskId] = new ClaimRecord
                {
                    ActionId = actionId,
                    ActionKind = actionKind,
                    AttemptEpoch = attemptEpoch,
                    Owner = owner,
                    ExpiresAtMs = nowMs + ClaimLeaseDurationMs
                };
            }

            if (takeover && stale != null)
            {
                Emit("LeaseExpired #" + taskId + " (stale owner " + stale.Owner + ")");
                Emit("OwnershipReleased #" + taskId + " owner=" + stale.Owner + " reason=lease expired");
            }
            Emit("ClaimAccepted #" + taskId + " " + actionId + " owner=" + owner
                + (takeover ? " (takeover)" : "") + " leaseMs=" + ClaimLeaseDurationMs);
            return takeover ? ClaimResult.GrantedTakeover : ClaimResult.Granted;
        }

        // ---- results (idempotency) --------------------------------------------
        // Records the outcome of the claimed action. The first result for a
        // claim releases it; duplicate or late callbacks are ignored safely.
        // Success is remembered by the ledger (sticky), so the same logical
        // action can never be claimed — and therefore executed — again.
        public static ResultStatus RecordExecutionResult(long taskId, string actionId, ActionOutcome outcome, int nowMs)
        {
            if (taskId <= 0 || string.IsNullOrEmpty(actionId) || (outcome != ActionOutcome.Succeeded && outcome != ActionOutcome.Failed))
            {
                return ResultStatus.RejectedInvalid;
            }
            if (!IsAuthoritative())
            {
                Emit("StaleCallbackIgnored #" + taskId + " result from non-authoritative process");
                return ResultStatus.RejectedNotAuthoritative;
            }

            ClaimRecord rec;
            lock (m_Lock)
            {
                if (!m_Claims.TryGetValue(taskId, out rec) || rec.ActionId != actionId)
                {
                    Emit("StaleCallbackIgnored #" + taskId + " " + actionId + " (no matching claim)");
                    return ResultStatus.StaleCallbackIgnored;
                }
            }

            if (!m_Ledger.RecordOutcome(actionId, outcome, nowMs))
            {
                Emit("StaleCallbackIgnored #" + taskId + " duplicate " + outcome + " result for " + actionId);
                return ResultStatus.DuplicateIgnored;
            }

            // First recorded result resolves the attempt: release the claim.
            // Failure releases immediately (Phase 3 recovery then owns the
            // task and its retry policy — this layer never retries).
            lock (m_Lock) m_Claims.Remove(taskId);
            Emit("OwnershipReleased #" + taskId + " owner=" + rec.Owner + " reason=" + outcome);
            return ResultStatus.Recorded;
        }

        // Explicit ownership release (the claim holder finishing/cancelling
        // its work without a result callback). Owner is verified: ownership
        // changes are explicit, logged, and never silent.
        public static bool ReleaseClaim(long taskId, string owner, string reason, int nowMs)
        {
            if (taskId <= 0) return false;
            ClaimRecord rec;
            lock (m_Lock)
            {
                if (!m_Claims.TryGetValue(taskId, out rec)) return false;
                if (rec.Owner != owner)
                {
                    Emit("InvariantViolation #" + taskId + " claim release owner mismatch (expected " + rec.Owner + ", got " + owner + ")");
                    return false;
                }
                m_Claims.Remove(taskId);
            }
            Emit("OwnershipReleased #" + taskId + " owner=" + owner + " reason=" + (reason ?? "unspecified"));
            return true;
        }

        // ---- maintenance --------------------------------------------------------
        // Hygiene pass: drop expired leases and claims whose task is gone or
        // terminal (cancelled/completed while claimed). Returns the number of
        // claims dropped. Deterministic; safe at any cadence.
        public static int Tick(int nowMs)
        {
            List<KeyValuePair<long, ClaimRecord>> drops = null;
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, ClaimRecord> kv in m_Claims)
                {
                    bool expired = unchecked(nowMs - kv.Value.ExpiresAtMs) >= 0;
                    CapBotTask task = TaskRegistry.Get(kv.Key);
                    bool dead = task == null || task.IsTerminal;
                    if (expired || dead)
                    {
                        if (drops == null) drops = new List<KeyValuePair<long, ClaimRecord>>();
                        drops.Add(kv);
                    }
                }
                if (drops != null)
                {
                    for (int i = 0; i < drops.Count; i++) m_Claims.Remove(drops[i].Key);
                }
            }
            if (drops == null) return 0;
            foreach (KeyValuePair<long, ClaimRecord> kv in drops)
            {
                CapBotTask task = TaskRegistry.Get(kv.Key);
                if (task != null && task.IsTerminal)
                {
                    Emit("ClaimDropped #" + kv.Key + " reason=task " + task.State);
                }
                else if (task == null)
                {
                    Emit("ClaimDropped #" + kv.Key + " reason=task missing");
                }
                else
                {
                    Emit("LeaseExpired #" + kv.Key + " (stale owner " + kv.Value.Owner + ")");
                    Emit("OwnershipReleased #" + kv.Key + " owner=" + kv.Value.Owner + " reason=lease expired");
                }
            }
            return drops.Count;
        }

        // Deterministic status snapshot (bounded): "id|actionId|owner|remainMs|active".
        public static List<string> ClaimStatusLines(int nowMs)
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<long, ClaimRecord> kv in m_Claims)
                {
                    int remain = unchecked(kv.Value.ExpiresAtMs - nowMs);
                    bool active = remain >= 0;
                    lines.Add(kv.Key + "|" + kv.Value.ActionId + "|" + kv.Value.Owner + "|" + remain + "ms|" + (active ? "active" : "stale"));
                }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Claims.Clear();
                m_AuthorityPolicy = null;
                m_OnDecision = null;
            }
            m_Ledger.ResetForTests();
        }

        // Phase 26: authority-loss surface for the MultiplayerAuthorityMonitor.
        // Claims (leases) are volatile authoritative bookkeeping: on authority
        // loss this process must not hold execution leases for a game state it
        // no longer owns. The sticky-success LEDGER IS KEPT — duplicate-
        // execution protection is identity truth, not a lease (an in-flight
        // action that actually reached the game must still be recognized as
        // done when authority returns). Returns the number of claims dropped.
        // Never throws.
        public static int ClearForAuthorityLoss()
        {
            try
            {
                int dropped;
                lock (m_Lock)
                {
                    dropped = m_Claims.Count;
                    m_Claims.Clear();
                }
                if (dropped > 0)
                {
                    Emit("ClaimsClearedAuthorityLost claims=" + dropped + " (ledger preserved)");
                }
                return dropped;
            }
            catch (Exception) { return 0; }
        }
    }
}