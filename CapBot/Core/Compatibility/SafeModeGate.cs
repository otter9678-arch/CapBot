using System;
using System.Collections.Generic;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 50: Safe Mode behavioral gate ------------------------------------
    // Until P49 the engine's Safe Mode was latch + audit only: nothing in the
    // running mod consulted ConflictEngine.SafeMode. This gate is the production
    // reader: every CapBot behavior surface (host tick pipeline, legacy feature
    // tick, spawn command) calls Tick(nowMs) first and suspends when the latch
    // is engaged.
    //
    // Directive contract (what Safe Mode DOES once latched):
    //   - the host-side task pipeline is frozen: no scheduler grants, no
    //     executor dispatch, no director authoring (the pipeline resumes from
    //     its own bounded state when Safe Mode is cleared — a future latch
    //     reset; today the latch is session-latched exactly like the engine's);
    //   - the legacy feature tick (Autonomy.OnTick + the captain-bot logic in
    //     PostfixCore) stops: no talent spends, no economy, no orders, no
    //     compat actions, no smart item use — bots fall back to vanilla AI;
    //   - /capbot refuses new spawns (no NEW CapBot instances enter a session
    //     the engine has declared unsafe);
    //   - evidence collection is NEVER suspended (exception telemetry and the
    //     Harmony map audit run to completion so a future un-latch decision
    //     is made on live data, and /capbotstatus stays truthful).
    //
    // Purity (the P19 lesson): System-only, no Unity/PML/Harmony/IO. The gate
    // reads the engine latch through a seam so tests can drive it without the
    // engine's own reset semantics; fail-closed: any internal fault suspends.
    public static class SafeModeGate
    {
        private const int MaxStatusLines = 4;
        private const long ReemitMs = 60000;   // stable-state line re-emit interval

        private static readonly object m_Lock = new object();
        private static Func<bool> m_SafeModeProvider;      // production: ConflictEngine.SafeMode
        private static Func<string> m_SafeModeReasonProvider; // production: ConflictEngine.SafeModeReason
        private static Action<string> m_AuditListener;

        private static bool m_Suspended;          // current behavioral state (sticky per session)
        private static bool m_LastObserved;       // previous provider observation (edge detect)
        private static bool m_HadObservation;     // false until first Tick (first-observation latch = no edge)
        private static string m_Reason = "";      // latch reason at suspension time
        private static long m_SuspendedAtMs = -1;
        private static long m_LastEmitMs = -1;
        private static bool m_LastEmitSuspended;  // dedup key: only emit on change or interval
        private static bool m_EmittedAny;         // first-tick line: positive wiring evidence
        private static int m_Suspensions;         // latch edges observed (bounded diagnostics)
        private static int m_TicksTotal;
        private static int m_TicksSuspended;
        private static int m_ListenerFaults;      // listener faults NEVER suspend (never crash the gate)

        // ---- seams --------------------------------------------------------------

        public static void SetSafeModeProvider(Func<bool> provider) { lock (m_Lock) { m_SafeModeProvider = provider; } }
        public static void SetSafeModeReasonProvider(Func<string> provider) { lock (m_Lock) { m_SafeModeReasonProvider = provider; } }
        public static void SetAuditListener(Action<string> listener) { lock (m_Lock) { m_AuditListener = listener; } }

        // ---- tick ---------------------------------------------------------------

        // Called at the head of every gated surface (host WorldTick pipeline,
        // legacy feature tick, /capbot spawn). Returns TRUE when the caller
        // must SUSPEND its behavior (fail-closed: any internal fault suspends —
        // the gate never becomes the crash source and never fails open).
        public static bool Tick(long nowMs)
        {
            m_TicksTotal++;
            try
            {
                Func<bool> provider = m_SafeModeProvider;
                bool latched;
                if (provider == null) latched = false;          // unwired gate = nothing observed
                else
                {
                    try { latched = provider(); }
                    catch (Exception) { latched = true; }        // provider fault: fail-closed
                }

                if (m_HadObservation && latched && !m_LastObserved && !m_Suspended)
                {
                    // Rising edge: a clean session just latched — capture reason
                    // + count BEFORE the sticky suspension below is applied.
                    // A first-observation-latched gate (m_HadObservation=false)
                    // counts NO edge: the sticky latch itself is the state, and
                    // the engine's idempotent re-confirm must never re-count.
                    m_Suspensions++;
                    Func<string> reasonFn = m_SafeModeReasonProvider;
                    if (reasonFn != null)
                    {
                        try { m_Reason = reasonFn() ?? ""; }
                        catch (Exception) { m_Reason = ""; }
                    }
                    m_SuspendedAtMs = nowMs;
                }
                m_LastObserved = latched;
                m_HadObservation = true;

                // The latch is session-sticky: once latched this session, the
                // behavioral suspension stays until a future phase's explicit
                // un-latch flow (the engine's own latch is idempotent and has
                // no auto-clear; mirroring that here is the honest behavior).
                if (latched) m_Suspended = true;

                bool emit = !m_EmittedAny
                    || m_Suspended != m_LastEmitSuspended
                    || (m_Suspended && (m_LastEmitMs < 0 || (nowMs - m_LastEmitMs) >= ReemitMs));
                if (emit)
                {
                    EmitLocked("SafeModeGate suspended=" + (m_Suspended ? "yes" : "no")
                        + (m_Suspended ? " reason=" + (m_Reason.Length == 0 ? "unspecified" : m_Reason) : "")
                        + " ticks=" + m_TicksSuspended + "/" + m_TicksTotal);
                    m_LastEmitMs = nowMs;
                    m_LastEmitSuspended = m_Suspended;
                    m_EmittedAny = true;
                }
                m_TicksSuspended += m_Suspended ? 1 : 0;
                return m_Suspended;
            }
            catch (Exception)
            {
                // Fail-closed: an internal fault must never silently re-enable
                // behavior while the engine may be latched.
                m_TicksSuspended++;
                return true;
            }
        }

        // Readbacks (bounded diagnostics for status + tests).
        public static bool Suspended { get { lock (m_Lock) { return m_Suspended; } } }
        public static string Reason { get { lock (m_Lock) { return m_Reason; } } }
        public static int SuspensionCount { get { lock (m_Lock) { return m_Suspensions; } } }
        public static int TicksTotal { get { lock (m_Lock) { return m_TicksTotal; } } }
        public static int TicksSuspended { get { lock (m_Lock) { return m_TicksSuspended; } } }

        // Bounded status surface: summary + suspension detail.
        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>(MaxStatusLines);
            lock (m_Lock)
            {
                lines.Add("SafeModeGate: suspended=" + (m_Suspended ? "yes" : "no")
                    + (m_Suspended ? " reason=" + (m_Reason.Length == 0 ? "unspecified" : m_Reason) : "")
                    + " edges=" + m_Suspensions
                    + " ticks=" + m_TicksSuspended + "/" + m_TicksTotal
                    + " lastAtMs=" + (m_SuspendedAtMs < 0 ? "none" : m_SuspendedAtMs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                lines.Add("SafeModeGate: covers hostTickPipeline, legacyFeatureTick, capbotSpawn; evidence collection NOT gated");
            }
            return lines;
        }

        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_SafeModeProvider = null;
                m_SafeModeReasonProvider = null;
                m_AuditListener = null;
                m_Suspended = false;
                m_LastObserved = false;
                m_HadObservation = false;
                m_Reason = "";
                m_SuspendedAtMs = -1;
                m_LastEmitMs = -1;
                m_LastEmitSuspended = false;
                m_EmittedAny = false;
                m_Suspensions = 0;
                m_TicksTotal = 0;
                m_TicksSuspended = 0;
            }
        }

        private static void EmitLocked(string line)
        {
            Action<string> listener = m_AuditListener;
            if (listener == null) return;
            try { listener(line); }
            catch (Exception) { m_ListenerFaults++; }
        }
    }
}