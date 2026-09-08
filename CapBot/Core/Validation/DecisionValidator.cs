using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Capabilities;
using CapBot.Core.Executor;

namespace CapBot.Core.Validation
{
    // ---- Phase 19: pre-dispatch decision validator ---------------------------
    //
    // A diagnostics-only pre-screen that runs in the WorldTick Postfix
    // IMMEDIATELY BEFORE TaskScheduler.Tick. It reviews QUEUED tasks that
    // carry a CapabilityId metadata binding and emits bounded diagnostics
    // when the task's shape contradicts what its dispatcher will do with it
    // (dispatcher-only shape screens), or when the author's world premise has
    // gone stale (author-premise staleness screens).
    //
    // OWNERSHIP ARGUMENT (the load-bearing design rule â€” full argument in
    // docs/DECISION_VALIDATOR.md):
    //   - It NEVER mutates lifecycle. Every cancel/fail/pause decision stays
    //     owned by the recovery ladder (P3 TaskRecoveryManager, per its
    //     documented contract "a stall must never become a task"). This
    //     validator is a second pair of eyes, not a third arm.
    //   - It NEVER re-runs gates the pipeline already owns: the P7 13-gate
    //     ladder (authority/actor/ownership/cooldown/claim/target-shape for
    //     registry-validated capabilities), P3 recovery rules, P5 claim
    //     leases, or galaxy/encounter membership (the dispatcher checks those
    //     with real game data â€” the validator only sees snapshot data and may
    //     not double-authorize on it).
    //   - It closes ONLY the two evidence-proven gaps: (1) ISSUE_MOVE_ORDER's
    //     registry TargetRequirement is None, so a non-SECTOR task passes the
    //     registry and dies post-start at the dispatcher â€” the validator
    //     surfaces that mismatch pre-dispatch; (2) tasks authored from a
    //     world snapshot premise (CAPTAIN_DELIB, NAV_RECOVERY) whose premise
    //     (current sector / warp state) changed between authoring and
    //     dispatch â€” the validator surfaces the staleness pre-dispatch.
    //
    // FAIL SEMANTICS (mirroring WorldSnapshotProbe / P6 policy):
    //   - FAIL-OPEN on uncertainty: missing world data, unknown ids, missing
    //     nav metrics can never produce a rejection. An uncertain validator
    //     must never be the reason healthy work dies â€” the P7/P5/P3 ladder
    //     still guards execution.
    //   - FAIL-CLOSED on action: it holds no task records and calls no
    //     lifecycle API. Diagnostics only. Worst case = noise, not damage.
    //
    // Known-fail-by-contract tasks are skipped deliberately: EMERGENCY
    // coordination tasks (stuck/objective) are authored with NO capability
    // binding and intentionally fail at executor start per the Phase 9
    // contract; the validator does not screen tasks without a CapabilityId
    // metadata binding, so those never trigger spurious diagnostics.
    //
    // House director pattern (P9/P14/P15/P16/P17/P18): public static class +
    // private DirectorState + m_Lock; 4 fail-closed seams; cadence-gated
    // Evaluate(nowMs) with snapshot fail-safe; bounded work under lock;
    // pending lines fired after lock release; bounded counters; ResetForTests.

    public static class DecisionValidator
    {
        // ---- thresholds (deterministic, documented in docs) ----
        public const int MinRecheckMs = 1000;      // matches scheduler MinRecheckMs cadence
        public const int MaxStaleSnapshotMs = 20000; // reuse P16/P17/P18 director staleness threshold â€” one shared freshness standard, NOT a third (P6 WorldStateService.MaxSnapshotAgeMs=10s stays the world-layer's own rule)
        public const int MaxPendingLines = 4;      // bounded diagnostics per pass
        public const int MaxSnapshotShipsScan = 24; // matches WorldSnapshot.MaxShips

        // ---- seams (fail-closed: unset = deny-by-default) ----
        private static Func<bool> m_AuthorityProbe;
        private static Func<int> m_NowMsProvider;
        private static Func<WorldSnapshot> m_WorldProvider;
        private static Action<string> m_DecisionListener;

        private static readonly object m_Lock = new object();

        private sealed class DirectorState
        {
            public int LastEvalMs;               // cadence gate
            public int Validations;              // tasks screened clean this session
            public int Rejections;               // shape/premise rejections emitted
            public int UncertainPasses;          // fail-open passes (uncertainty, never a rejection)
            public int StalePremiseRejections;   // subset of Rejections: author premise stale
            public int ShapeRejections;          // subset of Rejections: dispatcher shape mismatch
            public int LastUncertainMs;          // throttle for uncertainty lines
            public string LastUncertainReason = string.Empty;
        }

        private static readonly DirectorState s_State = new DirectorState();

        // ---- seams (P6/P18 pattern) ----

        public static void SetAuthorityProbe(Func<bool> probe)
        {
            lock (m_Lock) { m_AuthorityProbe = probe; }
        }

        public static void SetNowMsProvider(Func<int> provider)
        {
            lock (m_Lock) { m_NowMsProvider = provider; }
        }

        public static void SetWorldProvider(Func<WorldSnapshot> provider)
        {
            lock (m_Lock) { m_WorldProvider = provider; }
        }

        public static void SetDecisionListener(Action<string> listener)
        {
            lock (m_Lock) { m_DecisionListener = listener; }
        }

        // ---- one pass -------------------------------------------------------

        // Gate order: authority â†’ cadence â†’ snapshot fail-safe â†’ bounded scan.
        // Called from the WorldTick Postfix between the master gate and
        // TaskScheduler.Tick, so screened tasks are still Queued (no race
        // with grants/leases/claims) and every rejection is pre-dispatch.
        public static void Evaluate(int nowMs)
        {
            Func<bool> authorityProbe;
            Func<int> nowProvider;
            Func<WorldSnapshot> worldProvider;
            Action<string> listener;
            lock (m_Lock)
            {
                authorityProbe = m_AuthorityProbe;
                nowProvider = m_NowMsProvider;
                worldProvider = m_WorldProvider;
                listener = m_DecisionListener;
            }

            // Authority: deny-by-default (house rule — unset or faulting probe
            // = no-op; CaptainDirector pattern, P18).
            if (authorityProbe == null) return;
            bool isAuth;
            try { isAuth = authorityProbe(); } catch (Exception) { return; }
            if (!isAuth) return;

            // Cadence gate (unchecked subtraction — same-timestamp safe).
            if (nowMs - s_State.LastEvalMs < MinRecheckMs) return;
            s_State.LastEvalMs = nowMs;

            // Snapshot fail-safe (P16/P17/P18 fail-open-on-uncertainty):
            // null / never-captured / stale / future / pre-game → uncertain
            // diagnostics (throttled), zero rejections, return.
            WorldSnapshot snapshot = null;
            if (worldProvider != null)
            {
                try { snapshot = worldProvider(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null)
            {
                RecordUncertain("no snapshot", nowMs, listener);
                return;
            }
            if (snapshot.IsNeverCaptured)
            {
                RecordUncertain("snapshot never captured", nowMs, listener);
                return;
            }
            int age = nowMs - snapshot.SnapshotTimeMs;
            if (age > MaxStaleSnapshotMs)
            {
                RecordUncertain("snapshot stale", nowMs, listener);
                return;
            }
            if (age < -MaxStaleSnapshotMs)
            {
                RecordUncertain("snapshot from future", nowMs, listener);
                return;
            }
            if (!snapshot.GameStarted)
            {
                RecordUncertain("game not started", nowMs, listener);
                return;
            }

            // Bounded scan: Queued tasks with a capability binding only.
            List<CapBotTask> live = TaskRegistry.LiveSnapshot();
            if (live == null || live.Count == 0) return;

            List<string> pending = null; // lazily allocated
            int screened = 0;
            for (int i = 0; i < live.Count && screened < MaxPendingLines; i++)
            {
                CapBotTask t = live[i];
                if (t == null) continue;
                if (t.State != TaskState.Queued) continue;
                string cap = t.GetMetadata(TaskExecutor.MetadataCapabilityId);
                if (string.IsNullOrEmpty(cap)) continue; // known-fail-by-contract tasks skipped (P9 EMERGENCY coordination)
                screened++;

                string verdict = ScreenTask(t, cap, snapshot);
                if (verdict == null) continue;       // clean pass, no line
                if (verdict.Length == 0)             // uncertain marker
                {
                    s_State.UncertainPasses++;
                    continue;
                }
                if (pending == null) pending = new List<string>(2);
                pending.Add(verdict);
            }

            if (screened > 0) s_State.Validations += screened;

            // Fire pending lines AFTER lock release (house discipline). The
            // per-task screens ran under the caller's tick (no nested lock
            // taken), so no state was mutated under lock during screens.
            if (pending != null)
            {
                for (int i = 0; i < pending.Count && i < MaxPendingLines; i++)
                {
                    if (listener != null) listener(pending[i]);
                }
            }
        }

        // Returns null = clean; "" = uncertain (counted, no line); otherwise
        // a bounded diagnostics line. NEVER a lifecycle mutation.
        private static string ScreenTask(CapBotTask t, string cap, WorldSnapshot snapshot)
        {
            string targetKind = t.TargetKind ?? string.Empty;
            string targetId = t.TargetId ?? string.Empty;

            // ---- (a) dispatcher-only shape screens (evidence report Â§2) ----
            // Mirror the dispatcher's own code paths exactly â€” no new game
            // reads, no vocabulary invention.
            if (cap == RegisteredCapabilities.IssueMoveOrder)
            {
                // Registry TargetRequirement=None lets any kind through; the
                // dispatcher rejects non-SECTOR post-start (research Â§2).
                if (targetKind != "SECTOR")
                    return Reject(t, cap, "target kind mismatch", false);
                int sectorId;
                if (!TryParseId(targetId, out sectorId) || sectorId < 0)
                    return Reject(t, cap, "order id unknown", false);
            }
            else if (cap == RegisteredCapabilities.SetCaptainOrder)
            {
                int orderId;
                if (!TryParseId(targetId, out orderId) || !IsKnownOrderId(orderId))
                    return Reject(t, cap, "order id unknown", false);
            }
            else if (cap == RegisteredCapabilities.AddCourseGoal
                  || cap == RegisteredCapabilities.RemoveCourseGoal)
            {
                if (targetKind != "SECTOR")
                    return Reject(t, cap, "target kind mismatch", false);
                int sectorId;
                if (!TryParseId(targetId, out sectorId) || sectorId < 0)
                    return Reject(t, cap, "order id unknown", false);
            }
            // SET_CAPTAIN_TARGET / CLEAR_COURSE_GOALS / READ_WORLD_SNAPSHOT:
            // registry TargetRequirement already enforces ShipId/None shapes
            // at gate 5; no dispatcher-only gap exists for these (research
            // Â§2) â€” no screen beyond premise checks below.

            // ---- (b) author-premise staleness (evidence report Â§4) ----
            // Only for the two families authored FROM world snapshots. The
            // registry's target-shape gate already proved int-parseable â‰¥0
            // for SectorId requirements; for ISSUE_MOVE_ORDER (TargetReq
            // None) the shape screen above proved the parse. Positive
            // evidence only: uncertain data (nav missing, -1, NaN) â†’ uncertain, not rejected.
            if (t.TaskType == "CAPTAIN_DELIB" || t.TaskType == "NAV_RECOVERY")
            {
                NavigationSnapshot nav = snapshot.Navigation;
                if (nav == null)
                    return string.Empty; // uncertain: nav section missing

                if (targetKind == "SECTOR")
                {
                    int claimed;
                    if (!TryParseId(targetId, out claimed))
                        return string.Empty; // uncertain: unreadable target
                    int current = nav.CurrentSectorId;
                    if (current < 0)
                        return string.Empty; // uncertain: current sector unknown
                    if (current != claimed)
                        return Reject(t, cap, "stale premise: sector changed", true);
                    if (nav.InWarp)
                        return Reject(t, cap, "stale premise: in warp", true);
                }
            }

            // ---- (c) bounded sanity: Argument==TargetId for sector-capability tasks ----
            // P18/P14 authors set Argument = sector text for sector tasks;
            // divergence = authoring bug, surfaced (not fixed) here.
            string argument = t.GetMetadata(TaskExecutor.MetadataArgument);
            if (targetKind == "SECTOR" && !string.IsNullOrEmpty(argument) && argument != targetId)
                return Reject(t, cap, "argument mismatch", false);

            return null; // clean
        }

        // ---- diagnostics vocabulary (house style) ----

        // Returns the bounded rejection line; counters mutate through the
        // static state directly (no ref â€” static readonly fields cannot be
        // passed by ref). Rejection = fail-closed on diagnostics only.
        private static string Reject(CapBotTask t, string cap, string reason, bool stale)
        {
            if (stale) s_State.StalePremiseRejections++;
            else s_State.ShapeRejections++;
            s_State.Rejections++;
            return "DecisionRejected #" + t.TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " cap=" + cap + " type=" + (t.TaskType ?? string.Empty)
                + " reason=" + reason;
        }

        private static void RecordUncertain(string reason, int nowMs, Action<string> listener)
        {
            s_State.UncertainPasses++;
            s_State.LastUncertainMs = nowMs;
            s_State.LastUncertainReason = reason;
            // Throttle: one uncertainty line per pass max, already gated by
            // cadence â€” no additional spam guard needed (cadence 1s and the
            // log bridge's own spam guard bound this).
            if (listener != null) listener("DecisionUncertain " + reason);
        }

        private static bool TryParseId(string text, out int value)
        {
            return int.TryParse(text ?? string.Empty, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        // Verified vanilla captain-order vocabulary (PulsarCapabilityDispatcher
        // CaptainOrderVocabulary â€” mirrored exactly, no new ids invented).
        private static bool IsKnownOrderId(int orderId)
        {
            switch (orderId)
            {
                case 1: case 4: case 6: case 8:
                case 9: case 10: case 11: case 12: case 13:
                    return true;
                default:
                    return false;
            }
        }

        // ---- readbacks (bounded counters + status lines, house pattern) ----

        public static int GetValidations() { lock (m_Lock) { return s_State.Validations; } }
        public static int GetRejections() { lock (m_Lock) { return s_State.Rejections; } }
        public static int GetUncertainPasses() { lock (m_Lock) { return s_State.UncertainPasses; } }
        public static int GetStalePremiseRejections() { lock (m_Lock) { return s_State.StalePremiseRejections; } }
        public static int GetShapeRejections() { lock (m_Lock) { return s_State.ShapeRejections; } }
        public static string GetLastUncertainReason() { lock (m_Lock) { return s_State.LastUncertainReason; } }

        public static List<string> Lines()
        {
            lock (m_Lock)
            {
                return new List<string>
                {
                    "validations=" + s_State.Validations.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "rejections=" + s_State.Rejections.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " (stale=" + s_State.StalePremiseRejections.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " shape=" + s_State.ShapeRejections.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
                    "uncertain=" + s_State.UncertainPasses.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (s_State.LastUncertainReason.Length > 0 ? " last=" + s_State.LastUncertainReason : string.Empty)
                };
            }
        }

        public static List<string> StatusLines()
        {
            lock (m_Lock)
            {
                return new List<string>
                {
                    "DecisionValidator: validations=" + s_State.Validations.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " rejections=" + s_State.Rejections.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " uncertain=" + s_State.UncertainPasses.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
            }
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                s_State.LastEvalMs = 0;
                s_State.Validations = 0;
                s_State.Rejections = 0;
                s_State.UncertainPasses = 0;
                s_State.StalePremiseRejections = 0;
                s_State.ShapeRejections = 0;
                s_State.LastUncertainMs = 0;
                s_State.LastUncertainReason = string.Empty;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_DecisionListener = null;
            }
        }
    }
}