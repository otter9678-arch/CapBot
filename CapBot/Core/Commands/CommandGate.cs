using System;
using System.Collections.Generic;
using CapBot.Core.Capabilities;
using CapBot.Core.Executor;
using CapBot.Core.Tasks;
using CapBot.Core.World;

namespace CapBot.Core.Commands
{
    // Gate verdict vocabulary (mandate §4: REFUSE / SUPPRESS / REUSE classes).
    // A verdict is DATA: it is logged and stamped on the task — it is never
    // parsed, never executed, and never read back as behavior by any layer.
    public enum GateVerdict : byte
    {
        Allow = 0,                     // no cross-task objection — continue through the pipeline
        SuppressActiveDuplicate = 1,   // same semantic command already live on another task (REUSE: the live task keeps the work)
        SuppressRecentSucceeded = 2,   // same semantic command succeeded recently — desired state already established
        SuppressRecentFailed = 3,      // same semantic command failed recently — bounded retry hold, never an infinite loop
        SuppressNoOp = 4,              // authoritative world state ALREADY equals the desired state (no-op)
        SuppressStalePremise = 5,      // command's world premise is gone (sector moved out from under it) — superseded
    }

    // Suppression reasons stamped on cancelled duplicates (static vocabulary,
    // logged only) plus the per-source bounded diagnostics throttle.
    public static class CommandGateReasons
    {
        public const string Active = "duplicate-active";
        public const string Succeeded = "duplicate-succeeded";
        public const string Failed = "duplicate-failed-recently";
        public const string NoOp = "no-op-suppressed";
        public const string Stale = "stale-premise";
        public const string Stamped = "gate-checked";

        public const int LineThrottleMs = 5000;          // per (semantic, reason) diagnostics cadence
        public const int MaxTrackedSemantics = 256;      // bounded throttle map; cleared (never grown past) at cap

        private static readonly Dictionary<long, Dictionary<string, int>> s_LastEmit =
            new Dictionary<long, Dictionary<string, int>>();

        // True when this (semantic, reason) line may emit now. Bounded: at
        // most MaxTrackedSemantics distinct semantics keep state; the map is
        // cleared when a new key would exceed the cap (conservative — worst
        // case one extra diagnostic line per cap cycle).
        public static bool ShouldEmit(long semanticHash, string reason, int nowMs)
        {
            Dictionary<string, int> perReason;
            if (!s_LastEmit.TryGetValue(semanticHash, out perReason))
            {
                if (s_LastEmit.Count >= MaxTrackedSemantics) s_LastEmit.Clear();
                perReason = new Dictionary<string, int>(4, StringComparer.Ordinal);
                s_LastEmit[semanticHash] = perReason;
                perReason[reason] = nowMs;
                return true;
            }
            int last;
            if (perReason.TryGetValue(reason, out last) && unchecked(nowMs - last) < LineThrottleMs)
                return false;
            perReason[reason] = nowMs;
            return true;
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            s_LastEmit.Clear();
        }
    }

    // ---- Phase 41: unified command gate --------------------------------------
    //
    // The ONE cross-source validation/deduplication layer every command passes
    // through (mandate: "Do not create a second parallel command system").
    // Sources = the four production authors (emergency/navigation/captain/
    // mission-work) + any future registered author + advisor flows. Advisors
    // stay recommend-only: the only route an LLM recommendation can take
    // toward gameplay is the CapBotTask.Create -> Register -> TryQueue
    // pipeline, so a command from ANY source necessarily lands here.
    //
    // WHAT IT ADDS (the two evidence-proven architectural gaps):
    //
    //   1. CROSS-TASK SEMANTIC DEDUP. The P5 ActionLedger dedups per attempt
    //      only — the actionId embeds the taskId, so two different tasks
    //      carrying the same effective command both dispatch. The gate
    //      fingerprints the SEMANTIC IDENTITY of the command:
    //
    //          <capabilityId> | <normalized action/argument> |
    //          <normalized targetKind>:<normalized targetId> | <owner actor>
    //
    //      — never the task id, never a random id (mandate: "DO NOT use
    //      random task IDs as the deduplication identity"). Normalization is
    //      conservative and bounded: trim, ordinal lowercase, collapse
    //      whitespace runs. Two different Qwen responses, two authors, or a
    //      replan producing the same effective command collide here and the
    //      later duplicate is suppressed.
    //
    //      Mandate fingerprint components map as follows: command type →
    //      CapabilityId; normalized action → Argument (normalized); target
    //      identity → TargetKind/TargetId (normalized); owner/actor →
    //      OwnerActorId. Destination/sector and the authoritative context are
    //      honored by the STALE-PREMISE screen (a command whose sector
    //      premise moved is superseded regardless of identity) and by the
    //      time-bounded windows (a duplicate only suppresses within its
    //      window); "current plan generation" is deliberately NOT a key
    //      component — a replan re-issuing an identical command is exactly
    //      the loop this gate exists to break (mandate §11).
    //
    //   2. NO-OP SUPPRESSION. SET_CAPTAIN_ORDER(9) when the captain order is
    //      ALREADY 9 completed successfully every time. The gate compares
    //      the command's desired state against AUTHORITATIVE game state
    //      through a registered probe (PulsarNoOpProbes) and suppresses the
    //      no-op. Probe unknown/absent/faulting => CannotDetermine => ALLOW
    //      (fail-open, never a rejection) — the world pipeline (P6/P7)
    //      still guards execution.
    //
    // LOOP-PROTECTION CHECKS (mandate §4 A-G), in evaluation order:
    //   stale premise → same action already ACTIVE (reuse the live task) →
    //   desired state exists (recent identical success) → recently failed
    //   (bounded hold) → no-op (authoritative state already equal).
    //   Verdicts are REFUSE/SUPPRESS/REUSE outcomes: the surviving live task
    //   keeps the work; the obsolete duplicate is cancelled through the
    //   lifecycle ("A new task ID must NOT be enough to justify another
    //   execution", mandate §11).
    //
    // OWNERSHIP ARGUMENT (the load-bearing rule, mirroring DecisionValidator):
    //   - The gate NEVER mutates lifecycle on the allow path. A task that
    //     passes is untouched; every existing gate (P7 ladder, P5 claims,
    //     P3 recovery) still runs on it. The gate is a filter, not an owner.
    //   - On suppression the gate owns the OBSOLETE duplicate it refuses
    //     (neither running-with-a-claim nor recovery-owned — cancelling it
    //     is defined, idempotent, and logged; Created/Queued/Running ->
    //     Cancelled are legal transitions). Recovery keeps every
    //     retry/stuck/abandon decision (P39 discipline preserved: the
    //     StuckThresholdMs rule, the MaxRecoveryActions budget, retry-
    //     exhaustion cancels, and report-only ACTION_STALLED are untouched).
    //   - The gate NEVER re-runs the P7 ladder's own gates (authority,
    //     cooldowns, claim conflicts, target shapes) — it only adds the two
    //     checks nothing else performs.
    //   - Fail-open on uncertainty: no probe, probe fault, unknown sector =>
    //     allow. An uncertain gate must never be the reason healthy work
    //     dies (P19 fail-open precedent).
    //
    // WHERE IT RUNS: Check(task, nowMs) is invoked from the executor's Tick
    // scan for every Queued task BEFORE the lease is taken (a suppressed task
    // never consumes a grant or executor slot), and once more inside
    // AttemptExecution after TryStart (TOCTOU closure — the world can move
    // between the scan and the start). Both call sites are host-only (the
    // WorldTick gate), matching every executor caller.
    //
    // PERFORMANCE: no scene scans, no FindObjectsOfType, no LINQ, bounded
    // maps (records <= MaxLiveTasks with oldest-stamp eviction; throttle map
    // <= MaxTrackedSemantics), zero allocation on the allow path beyond the
    // fingerprint string itself. No per-frame loop — called from the
    // executor's existing 250 ms gate only. Dedup is independent of
    // personality initialization (mandate §20): no crew/agent reads anywhere.
    //
    // House director pattern (P9-P27): public static class + private state +
    // m_Lock; fail-closed seams; bounded counters; lines fired after lock
    // release; ResetForTests.
    public static class CommandGate
    {
        // ---- thresholds (deterministic, documented) ----
        public const int RecentSucceededWindowMs = 30000;  // desired-state reuse window (>= any capability cooldown)
        public const int RecentFailedHoldMs = 20000;       // bounded hold for a repeated identical failure
        public const int SectorGraceMs = 5000;             // stale-premise grace after a sector transition

        // ---- seams (fail-open when unset: the gate adds restrictions only
        //      through registered probes; production wires all of them) ----
        // Probe shape: (capabilityId, request) — the capability id comes from
        // the task's CapabilityId metadata (CapabilityRequest itself carries
        // only task fields; identity shape mirrors the executor's request
        // construction).
        private static Func<WorldSnapshot> m_WorldProvider;                          // stale-premise context
        private static Func<string, CapabilityRequest, ProbeAnswer> m_NoOpProbe;     // authoritative desired-state reader
        private static Action<string> m_DecisionListener;                            // CommandGateLogBridge attaches at boot

        private static readonly object m_Lock = new object();

        public enum ProbeAnswer : byte
        {
            CannotDetermine = 0, // unknown world data -> fail-open (allow)
            IsNotNoOp = 1,       // authoritative state differs -> allow
            IsNoOp = 2,          // authoritative state already equals desired state -> suppress
        }

        public static void SetWorldProvider(Func<WorldSnapshot> provider)
        {
            lock (m_Lock) { m_WorldProvider = provider; }
        }

        public static void SetNoOpProbe(Func<string, CapabilityRequest, ProbeAnswer> probe)
        {
            lock (m_Lock) { m_NoOpProbe = probe; }
        }

        public static void SetDecisionListener(Action<string> listener)
        {
            lock (m_Lock) { m_DecisionListener = listener; }
        }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_DecisionListener;
            if (l != null) l(line);
        }

        // ---- semantic identity ---------------------------------------------------
        // The dedup identity is the COMMAND, not the task. Deliberately
        // EXCLUDED: the task id, attempt epochs, source text, authoring time,
        // task type (a CAPTAIN_DELIB move and a MISSION_WORK move to the same
        // sector are the same effective command — cross-author dedup is the
        // mandate's core ask).
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string trimmed = text.Trim();
            char[] buffer = null;
            int w = 0;
            bool prevSpace = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                bool isSpace = c == ' ' || c == '\t' || c == '\r' || c == '\n';
                if (isSpace)
                {
                    if (prevSpace) continue;
                    if (buffer == null) buffer = new char[trimmed.Length];
                    buffer[w++] = ' ';
                    prevSpace = true;
                    continue;
                }
                if (buffer == null) buffer = new char[trimmed.Length];
                buffer[w++] = char.ToLowerInvariant(c);
                prevSpace = false;
            }
            return new string(buffer, 0, w);
        }

        // "<capability>|<normalized action>|<normalized target>|<owner>" —
        // capabilityId and owner are validated bounded tokens upstream
        // (P7 charset / owner <= 64); action and target are normalized
        // bounded text. The result is data for comparison, logging and
        // hashing ONLY — never parsed, never dispatched on.
        public static string SemanticIdentity(string capabilityId, CapabilityRequest request)
        {
            if (request == null) return null;
            return (capabilityId ?? string.Empty)
                + "|" + Normalize(request.Argument)
                + "|" + Normalize(request.TargetKind) + ":" + Normalize(request.TargetId)
                + "|" + (request.OwnerActorId ?? string.Empty);
        }

        // Context-free semantic fingerprint (see header: sector/context are
        // honored by the stale-premise screen, not by the key). Null when the
        // command carries no capability binding (outside gate scope).
        public static string FingerprintFor(string capabilityId, CapabilityRequest request)
        {
            if (string.IsNullOrEmpty(capabilityId) || request == null) return null;
            return SemanticIdentity(capabilityId, request);
        }

        // Convenience: build the request-shaped fingerprint from a task (same
        // shape the executor's CapabilityRequest will carry).
        public static string FingerprintFor(CapBotTask task)
        {
            if (task == null) return null;
            string capabilityId = task.GetMetadata(TaskExecutor.MetadataCapabilityId);
            if (string.IsNullOrEmpty(capabilityId)) return null;
            CapabilityRequest request = new CapabilityRequest(
                task.TaskId, task.TaskType, task.OwnerActorId,
                task.TargetKind, task.TargetId,
                task.GetMetadata(TaskExecutor.MetadataArgument) ?? string.Empty);
            return FingerprintFor(capabilityId, request);
        }

        public static long FingerprintHash(string fingerprint)
        {
            return ActionIdentity.ComputeStableHash(fingerprint ?? string.Empty);
        }

        // ---- records -------------------------------------------------------------
        private sealed class SemanticRecord
        {
            public long KeyHash;             // map key mirror (diagnostics)
            public string Fingerprint;       // full fingerprint string (bounded; diagnostics)
            public string CapabilityId;
            public GateVerdict LastVerdict;
            public string LastReason;
            public int LastSeenMs;
            public int LastSucceededMs = -1; // desired-state reuse window
            public int LastFailedMs = -1;    // bounded retry hold
            public long FailedTaskId = -1;   // the failed attempt's own task — its recovery retry is never vetoed
        }

        private static readonly Dictionary<long, SemanticRecord> m_Records =
            new Dictionary<long, SemanticRecord>();
        private static int m_LastSectorId = -1;          // last known authoritative sector (-1 = unknown)
        private static int m_LastSectorChangeMs = -1;    // when the authoritative sector last moved

        private sealed class Counters
        {
            public long Seen;                 // commands fingerprinted (customCommandReceived)
            public long Allowed;              // verdict=Allow (customCommandAccepted)
            public long SuppressedActive;     // customCommandDeduplicated (active reuse)
            public long SuppressedSucceeded;  // customCommandDeduplicated (desired state exists)
            public long SuppressedFailed;     // customCommandRetrySuppressed (bounded failed hold)
            public long SuppressedNoOp;       // customCommandNoOp
            public long SuppressedStale;      // customCommandSuperseded (stale premise)
            public long ProbeFaults;          // probe faults (fail-open passes)
            public long RecordEvictions;
        }

        private static readonly Counters s_Counters = new Counters();

        // ---- the check -------------------------------------------------------------
        // One gate pass for ONE task. Deterministic for a given (task, world,
        // records) triple; never throws; never mutates the task on Allow.
        // On any suppression verdict the caller MUST resolve the task through
        // ResolveSuppressed (the executor does).
        public static GateVerdict Check(CapBotTask task, int nowMs)
        {
            if (task == null) return GateVerdict.Allow;
            string capabilityId = task.GetMetadata(TaskExecutor.MetadataCapabilityId);
            if (string.IsNullOrEmpty(capabilityId)) return GateVerdict.Allow; // known-fail-by-contract tasks are not commands

            // ---- authoritative context (fail-open when unknown) ----
            WorldSnapshot snapshot = null;
            Func<string, CapabilityRequest, ProbeAnswer> noopProbe;
            Func<WorldSnapshot> worldProvider;
            lock (m_Lock)
            {
                noopProbe = m_NoOpProbe;
                worldProvider = m_WorldProvider;
            }
            if (worldProvider != null)
            {
                try { snapshot = worldProvider(); } catch (Exception) { snapshot = null; }
            }

            // ---- stale-premise invalidation (mandate §10/§11) ----
            // A navigation command whose premise MOVED recently (and left the
            // grace window) is obsolete by definition. Family semantics mirror
            // the P19 validator: NAV_RECOVERY is always premise-bound;
            // CAPTAIN_DELIB / MISSION_WORK / ISSUE_MOVE_ORDER requests are
            // premise-bound when the author targeted the current sector
            // (claimed == authored-context). Not failure, not retry:
            // superseded. Fail-open when the sector is unknown.
            int contextSector = -1;
            if (snapshot != null && snapshot.Navigation != null) contextSector = snapshot.Navigation.CurrentSectorId;
            // Track the authoritative sector BEFORE the stale screen reads it:
            // the first post-transition check must already observe the move
            // (the screen compares the CLAIMED sector against the tracked
            // context — tracking after the screen would miss one command).
            lock (m_Lock) { TrackSector(contextSector, nowMs); }
            if (contextSector >= 0 && task.TargetKind == "SECTOR" && task.TargetId != null)
            {
                int claimedSector;
                if (int.TryParse(task.TargetId, out claimedSector) && claimedSector >= 0)
                {
                    bool premiseBound = task.TaskType == "NAV_RECOVERY"
                        || capabilityId == RegisteredCapabilities.IssueMoveOrder
                        || claimedSector == m_LastSectorId;
                    if (premiseBound && claimedSector != contextSector && SectorTransitionedRecently(nowMs))
                    {
                        return Finish(task, capabilityId, null, GateVerdict.SuppressStalePremise,
                            CommandGateReasons.Stale, "stale premise: sector moved", nowMs, true);
                    }
                }
            }

            // ---- semantic fingerprint (never the task id) ----
            string argument = task.GetMetadata(TaskExecutor.MetadataArgument) ?? string.Empty;
            CapabilityRequest shape = new CapabilityRequest(
                task.TaskId, task.TaskType, task.OwnerActorId,
                task.TargetKind, task.TargetId, argument);
            string fingerprint = FingerprintFor(capabilityId, shape);
            if (fingerprint == null) return GateVerdict.Allow; // unbuildable identity -> outside scope
            long hash = FingerprintHash(fingerprint);

            lock (m_Lock)
            {
                s_Counters.Seen++;
                TrackSector(contextSector, nowMs);

                SemanticRecord rec;
                if (!m_Records.TryGetValue(hash, out rec))
                {
                    // First sighting of this semantic command: record + allow.
                    // (Per-attempt duplicates remain owned by the P5 claim
                    // probe inside the P7 ladder — unchanged ownership.)
                    rec = new SemanticRecord
                    {
                        KeyHash = hash,
                        Fingerprint = fingerprint,
                        CapabilityId = capabilityId,
                        LastVerdict = GateVerdict.Allow,
                        LastReason = CommandGateReasons.Stamped,
                        LastSeenMs = nowMs
                    };
                    m_Records[hash] = rec;
                    EvictIfBounded(nowMs);
                }

                // ---- loop-protection checks (mandate §4 A-G) ----
                // B) desired state already established by a recent identical
                //    SUCCESS (>= any capability cooldown window).
                if (rec.LastSucceededMs >= 0 && unchecked(nowMs - rec.LastSucceededMs) < RecentSucceededWindowMs)
                {
                    return Finish(task, capabilityId, rec, GateVerdict.SuppressRecentSucceeded,
                        CommandGateReasons.Succeeded, "recent identical success", nowMs, false);
                }

                // C) same semantic command recently FAILED on a DIFFERENT task
                //    (bounded hold — recovery owns retries of the FAILED task
                //    and its own bounded retry of the original is exempt; a
                //    NEW identical task is a duplicate by construction and
                //    waits the hold window instead of looping).
                if (rec.LastFailedMs >= 0 && rec.FailedTaskId != task.TaskId
                    && unchecked(nowMs - rec.LastFailedMs) < RecentFailedHoldMs)
                {
                    return Finish(task, capabilityId, rec, GateVerdict.SuppressRecentFailed,
                        CommandGateReasons.Failed, "recent identical failure (bounded hold)", nowMs, false);
                }

                // A) same semantic command already ACTIVE on a different task —
                //    with a DETERMINISTIC SURVIVOR RULE. When two identical
                //    tasks are queued before either is checked, a symmetric
                //    "the other one is live" test would veto BOTH (every task
                //    sees a twin; the command never executes). The lowest
                //    TaskId among live same-fingerprint tasks survives; every
                //    non-survivor is suppressed; the survivor always passes
                //    (a same-task TOCTOU re-check is structurally one attempt
                //    — the per-attempt claim gates own that duplicate class).
                if (IsDuplicateActiveNonSurvivor(hash, task))
                {
                    return Finish(task, capabilityId, rec, GateVerdict.SuppressActiveDuplicate,
                        CommandGateReasons.Active, "duplicate active (non-survivor)", nowMs, false);
                }

                // D) no-op: authoritative world state already equals the
                //    desired state. Probe fault / unknown => fail-open.
                if (noopProbe != null)
                {
                    ProbeAnswer answer;
                    try { answer = noopProbe(capabilityId, shape); }
                    catch (Exception) { s_Counters.ProbeFaults++; answer = ProbeAnswer.CannotDetermine; }
                    if (answer == ProbeAnswer.IsNoOp)
                    {
                        return Finish(task, capabilityId, rec, GateVerdict.SuppressNoOp,
                            CommandGateReasons.NoOp, "no-op (authoritative state already equals desired state)", nowMs, true);
                    }
                }

                // Allowed: refresh sighting (bounded work under lock).
                rec.LastVerdict = GateVerdict.Allow;
                rec.LastReason = CommandGateReasons.Stamped;
                rec.LastSeenMs = nowMs;
                s_Counters.Allowed++;
            }
            return GateVerdict.Allow;
        }

        // Sector-context tracker: stamps when the authoritative context last
        // changed (drives the stale-premise grace window). Fail-open: unknown
        // context (-1) never arms invalidation.
        private static void TrackSector(int contextSector, int nowMs)
        {
            if (contextSector < 0) return;
            if (m_LastSectorId >= 0 && contextSector != m_LastSectorId)
                m_LastSectorChangeMs = nowMs;
            m_LastSectorId = contextSector;
        }

        private static bool SectorTransitionedRecently(int nowMs)
        {
            return m_LastSectorChangeMs >= 0 && unchecked(nowMs - m_LastSectorChangeMs) < SectorGraceMs;
        }

        // True when the same semantic command is actively queued/running on
        // ANOTHER task AND this task is NOT the deterministic survivor. The
        // survivor is the lowest TaskId among live same-fingerprint tasks:
        // symmetric "the other is active" vetoes would deadlock identical
        // twins (both suppressed, command never executes). Bounded scan of
        // the live registry (<= MaxLiveTasks); comparison is hash-of-string.
        // The scan is order-independent: the minimum twin id decides.
        private static bool IsDuplicateActiveNonSurvivor(long hash, CapBotTask self)
        {
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            long selfId = self.TaskId;
            long lowestTwinId = -1;
            for (int i = 0; i < live.Count; i++)
            {
                CapBotTask t = live[i];
                if (t == null || t.TaskId == selfId) continue; // same attempt, never two live attempts
                if (t.State != TaskState.Queued && t.State != TaskState.Running) continue;
                string otherCap = t.GetMetadata(TaskExecutor.MetadataCapabilityId);
                if (string.IsNullOrEmpty(otherCap)) continue;
                CapabilityRequest other = new CapabilityRequest(
                    t.TaskId, t.TaskType, t.OwnerActorId, t.TargetKind, t.TargetId,
                    t.GetMetadata(TaskExecutor.MetadataArgument) ?? string.Empty);
                string otherFp = FingerprintFor(otherCap, other);
                if (otherFp == null) continue;
                if (FingerprintHash(otherFp) != hash) continue;
                if (lowestTwinId < 0 || t.TaskId < lowestTwinId) lowestTwinId = t.TaskId;
            }
            return lowestTwinId >= 0 && lowestTwinId < selfId; // a lower-id twin lives -> non-survivor
        }

        // ---- outcome recording (executor calls after the attempt) ----------------
        // Keeps the semantic record's success/failure windows truthful. The
        // per-attempt ledger (ActionLedger) stays the owner of per-identity
        // outcomes; this is the cross-task memory on top.
        public static void RecordOutcome(CapBotTask task, bool succeeded, int nowMs)
        {
            if (task == null) return;
            if (string.IsNullOrEmpty(task.GetMetadata(TaskExecutor.MetadataCapabilityId))) return;
            string fingerprint = FingerprintFor(task);
            if (fingerprint == null) return;
            long hash = FingerprintHash(fingerprint);
            lock (m_Lock)
            {
                SemanticRecord rec;
                if (!m_Records.TryGetValue(hash, out rec)) return;
                if (succeeded) { rec.LastSucceededMs = nowMs; rec.LastFailedMs = -1; rec.FailedTaskId = -1; }
                else { rec.LastFailedMs = nowMs; rec.LastSucceededMs = -1; rec.FailedTaskId = task.TaskId; }
                rec.LastSeenMs = nowMs;
            }
        }

        // ---- suppression resolution -----------------------------------------------
        // Resolve a suppressed task through the lifecycle (terminal, logged).
        // Cancel is the truthful terminal state for an OBSOLETE duplicate —
        // its work was already done, is being done, or was refused as a no-op.
        // Returns false when the lifecycle refused (recovery took ownership
        // mid-flight; the gate never fights recovery).
        public static bool ResolveSuppressed(CapBotTask task, string reason)
        {
            if (task == null) return false;
            return task.TryCancel(reason);
        }

        // Bounded eviction: records are <= live-task-bounded in honest use,
        // but a hostile author cycling fingerprints could still grow the map
        // (the live cap bounds concurrent tasks, not distinct identities over
        // time) — FIFO-evict the LEAST-RECENTLY-SEEN record once past the cap
        // (mirrors ActionLedger's bounded-ring precedent).
        private static void EvictIfBounded(int nowMs)
        {
            if (m_Records.Count <= TaskRegistry.MaxLiveTasks) return;
            long oldestKey = -1;
            int oldestStamp = int.MaxValue;
            foreach (KeyValuePair<long, SemanticRecord> kv in m_Records)
            {
                if (kv.Value.LastSeenMs < oldestStamp)
                {
                    oldestStamp = kv.Value.LastSeenMs;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey >= 0)
            {
                m_Records.Remove(oldestKey);
                s_Counters.RecordEvictions++;
            }
        }

        // Shared tail: counters, bounded diagnostics line, verdict return.
        private static GateVerdict Finish(CapBotTask task, string capabilityId, SemanticRecord rec,
            GateVerdict verdict, string reason, string detail, int nowMs, bool throttleLine)
        {
            lock (m_Lock)
            {
                switch (verdict)
                {
                    case GateVerdict.SuppressActiveDuplicate: s_Counters.SuppressedActive++; break;
                    case GateVerdict.SuppressRecentSucceeded: s_Counters.SuppressedSucceeded++; break;
                    case GateVerdict.SuppressRecentFailed: s_Counters.SuppressedFailed++; break;
                    case GateVerdict.SuppressNoOp: s_Counters.SuppressedNoOp++; break;
                    case GateVerdict.SuppressStalePremise: s_Counters.SuppressedStale++; break;
                    default: break;
                }
                if (rec != null)
                {
                    rec.LastVerdict = verdict;
                    rec.LastReason = reason;
                    rec.LastSeenMs = nowMs;
                }
            }
            // Line fires outside the lock (house discipline). The throttle
            // only applies to the recurring classes; stale/no-op lines are
            // rare by construction.
            string line = "CommandSuppressed #" + task.TaskId + " " + capabilityId
                + " reason=" + reason + " (" + detail + ") taskType=" + (task.TaskType ?? string.Empty)
                + " target=" + (task.TargetKind ?? string.Empty) + ":" + (task.TargetId ?? string.Empty)
                + " owner=" + (task.OwnerActorId ?? string.Empty);
            if (!throttleLine || CommandGateReasons.ShouldEmit(
                FingerprintHash((task.TaskType ?? string.Empty) + "|" + capabilityId + "|" + (task.TargetId ?? string.Empty) + "|" + reason),
                reason, nowMs))
            {
                Emit(line);
            }
            return verdict;
        }

        // ---- readbacks (bounded counters + status lines, house pattern) ----

        public static long SeenCount { get { lock (m_Lock) return s_Counters.Seen; } }
        public static long AllowedCount { get { lock (m_Lock) return s_Counters.Allowed; } }
        public static long SuppressedActiveCount { get { lock (m_Lock) return s_Counters.SuppressedActive; } }
        public static long SuppressedSucceededCount { get { lock (m_Lock) return s_Counters.SuppressedSucceeded; } }
        public static long SuppressedFailedCount { get { lock (m_Lock) return s_Counters.SuppressedFailed; } }
        public static long SuppressedNoOpCount { get { lock (m_Lock) return s_Counters.SuppressedNoOp; } }
        public static long SuppressedStaleCount { get { lock (m_Lock) return s_Counters.SuppressedStale; } }
        public static long ProbeFaultCount { get { lock (m_Lock) return s_Counters.ProbeFaults; } }
        public static int RecordCount { get { lock (m_Lock) return m_Records.Count; } }

        // Mandate §16 counter vocabulary mapped onto the gate's classes.
        public static List<string> StatusLines()
        {
            lock (m_Lock)
            {
                return new List<string>
                {
                    "received=" + s_Counters.Seen.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " accepted=" + s_Counters.Allowed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " deduped=" + (s_Counters.SuppressedActive + s_Counters.SuppressedSucceeded).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " noOp=" + s_Counters.SuppressedNoOp.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " retrySuppressed=" + s_Counters.SuppressedFailed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " superseded=" + s_Counters.SuppressedStale.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "records=" + m_Records.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " probeFaults=" + s_Counters.ProbeFaults.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " evictions=" + s_Counters.RecordEvictions.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
            }
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Records.Clear();
                m_WorldProvider = null;
                m_NoOpProbe = null;
                m_DecisionListener = null;
                m_LastSectorId = -1;
                m_LastSectorChangeMs = -1;
                s_Counters.Seen = 0;
                s_Counters.Allowed = 0;
                s_Counters.SuppressedActive = 0;
                s_Counters.SuppressedSucceeded = 0;
                s_Counters.SuppressedFailed = 0;
                s_Counters.SuppressedNoOp = 0;
                s_Counters.SuppressedStale = 0;
                s_Counters.ProbeFaults = 0;
                s_Counters.RecordEvictions = 0;
            }
            CommandGateReasons.ResetForTests();
        }
    }
}