using System;
using System.Collections.Generic;

namespace CapBot.Core.Tasks
{
    // ---- Phase 26: multiplayer authority monitor --------------------------------
    //
    // Authority-flip observation for the task-pipeline layer. The WorldTick
    // driver is host-gated: when this process LOSES authority (host
    // migration), every host-side tick driver stops running, so any
    // transition-time cleanup that lives BEHIND the gate is unreachable in
    // production (the P10 CrewAgentRegistry authority-lost clear has been
    // exactly this dead-in-game code since Phase 10). This monitor runs
    // EVERY FRAME (before the host-only gate), observes true→false and
    // false→true flips of the same authority seam the whole pipeline uses
    // (ExecutionClaims.IsAuthoritative), and invokes registered fail-safe
    // clear handlers on authority LOST.
    //
    // Design:
    //   - Observe-only core: the monitor never mutates pipeline state
    //     directly; it owns NO gameplay semantics. It is a pre-gate
    //     observer + dispatcher to registered handlers.
    //   - Deny-by-default: with the authority probe unset, no transitions
    //     are observed and handlers never fire (fail-closed). A faulting
    //     probe answers "unknown" — no transition is synthesized on a
    //     fault (a fault could mean transient authority confusion; only an
    //     authoritative answer drives state). Handlers fire on a false
    //     answer ONLY after a prior true observation, so a boot-time fault
    //     (never true yet) fires nothing.
    //   - Registered handlers must be fail-safe (never throw): each handler
    //     is wrapped in its own try/catch; one faulting handler cannot block
    //     others or the caller. Handlers run OUTSIDE the monitor lock, after
    //     the transition is recorded, in registration order.
    //   - No per-frame allocations on the steady-state path (probe answered,
    //     no transition): handler list never changes size in steady state
    //     and no list is built unless a transition fires.
    //   - Handler registration is bounded (MaxHandlers) and append-only with
    //     a duplicate-safe Contains check; the count is deterministic.
    //
    // Multiplayer contract (Phase 26, docs/MULTIPLAYER_HARDENING.md §2): on
    // authority LOST this process must not retain volatile authoritative
    // state — claims (leases) are dropped (the sticky-success ledger is
    // KEPT: duplicate-execution protection is identity truth, not a lease),
    // crew agents are cleared (deterministic identity ⇒ nothing user-visible
    // lost; rebuilt by the next authoritative sync), and scheduler leases
    // are dropped the same way (grants are 5 s bookkeeping).
    //
    // Pure C# domain (System* only), CapBotLog-free like the P19 validator —
    // run_tests.ps1 compiles a narrow file set without CapBotLog.cs; the log
    // bridge handles logging via the listener seam. Never throws to the
    // caller (Patch.cs wraps it anyway, per the house tick discipline).
    public static class MultiplayerAuthorityMonitor
    {
        public const int MaxHandlers = 16; // bounded registration

        private static readonly object m_Lock = new object();
        private static readonly object m_AddLock = new object();
        private static Func<bool> m_Probe;                // null/fault => no transitions observed
        private static Action<string> m_OnDecision;       // MPLogBridge attaches at boot
        private static readonly List<Action> s_Handlers = new List<Action>(); // fail-safe clear handlers
        private static bool m_HasLast;
        private static bool m_LastKnown;
        private static long m_Transitions;
        private static long m_LostEvents;
        private static long m_RegainEvents;
        private static long m_HandlersFired;
        private static long m_HandlerFaults;
        private static long m_ProbeFaults;
        private static string m_LastEvent;

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_Probe = probe; }
        public static void SetDecisionListener(Action<string> listener) { lock (m_Lock) m_OnDecision = listener; }

        // Registers a fail-safe clear handler fired on authority LOST.
        // Idempotent per reference (same delegate instance registered twice
        // is a no-op); bounded at MaxHandlers (extra registrations refused,
        // counted). Never throws.
        public static bool AddClearHandler(Action handler)
        {
            if (handler == null) return false;
            lock (m_AddLock)
            {
                if (s_Handlers.Contains(handler)) return true;
                if (s_Handlers.Count >= MaxHandlers) return false;
                s_Handlers.Add(handler);
                return true;
            }
        }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // Readbacks (diagnostics/tests).
        public static bool HasLastObservation { get { lock (m_Lock) return m_HasLast; } }
        public static bool LastKnownAuthority { get { lock (m_Lock) return m_HasLast && m_LastKnown; } }
        public static long TransitionCount { get { lock (m_Lock) return m_Transitions; } }
        public static long LostEventCount { get { lock (m_Lock) return m_LostEvents; } }
        public static long RegainEventCount { get { lock (m_Lock) return m_RegainEvents; } }
        public static long HandlersFiredCount { get { lock (m_Lock) return m_HandlersFired; } }
        public static long HandlerFaultCount { get { lock (m_Lock) return m_HandlerFaults; } }
        public static long ProbeFaultCount { get { lock (m_Lock) return m_ProbeFaults; } }
        public static int HandlerCount { get { lock (m_AddLock) return s_Handlers.Count; } }
        public static string LastEvent { get { lock (m_Lock) return m_LastEvent; } }

        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                lines.Add("mpAuthority monitored=" + (m_Probe != null ? "yes" : "no")
                    + " known=" + (m_HasLast ? (m_LastKnown ? "master" : "client") : "unknown")
                    + " transitions=" + m_Transitions.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " lost=" + m_LostEvents.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " regained=" + m_RegainEvents.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " probeFaults=" + m_ProbeFaults.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("mpHandlers registered=" + s_Handlers.Count
                    + " fired=" + m_HandlersFired.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " faults=" + m_HandlerFaults.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("mpLastEvent=" + (m_LastEvent ?? "-"));
            }
            return lines;
        }

        // Per-frame observation (Patch.cs WorldTick Prefix-shape call: runs
        // BEFORE the host-only gate so authority loss is observed even when
        // every host-side driver is about to stop). Steady state = one probe
        // read, no allocation. On a true→false transition: records the flip,
        // snapshots the handlers under m_AddLock, fires each OUTSIDE all
        // locks (own try/catch), then emits one bounded MPAuthorityLost
        // line. On false→true: records the flip + emits MPAuthorityRegained.
        // Returns the number of clear handlers actually invoked this pass.
        public static int Observe()
        {
            Func<bool> probe;
            lock (m_Lock) probe = m_Probe;
            if (probe == null) return 0;

            bool known;
            try { known = probe(); }
            catch (Exception)
            {
                // Unknown: no transition synthesized, counted only. A boot-
                // time fault can never fire handlers (nothing armed yet).
                lock (m_Lock) m_ProbeFaults++;
                return 0;
            }

            List<Action> toFire = null;
            lock (m_Lock)
            {
                if (!m_HasLast)
                {
                    // First authoritative observation arms the monitor — it is
                    // NOT a transition and fires nothing. m_HasLast must be
                    // set under the same lock that reads it, or the next pass
                    // would see arm-as-flip (found by MP01: hasLast was
                    // stamped outside the lock, so the second true answer
                    // looked like false→true).
                    m_HasLast = true;
                    m_LastKnown = known;
                    return 0;
                }
                if (known == m_LastKnown) return 0; // steady state
                // Transition.
                m_LastKnown = known;
                m_Transitions++;
                if (known)
                {
                    m_RegainEvents++;
                    m_LastEvent = "regained t=" + TickNow();
                }
                else
                {
                    m_LostEvents++;
                    m_LastEvent = "lost t=" + TickNow();
                    if (s_Handlers.Count > 0) toFire = new List<Action>(s_Handlers);
                }
            }

            int fired = 0;
            if (toFire != null)
            {
                for (int i = 0; i < toFire.Count; i++)
                {
                    try { toFire[i](); fired++; m_HandlersFired++; }
                    catch (Exception) { m_HandlerFaults++; }
                }
            }
            Emit(known
                ? "MPAuthorityRegained transitions=" + m_Transitions.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "MPAuthorityLost handlers=" + fired.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return fired;
        }

        // TaskClock.NowMs is the house clock; a local helper keeps this file
        // free of cross-domain using directives while staying truthful.
        private static long TickNow()
        {
            try { return TaskClock.NowMs; }
            catch (Exception) { return -1; }
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Probe = null;
                m_OnDecision = null;
                m_HasLast = false;
                m_LastKnown = false;
                m_Transitions = 0;
                m_LostEvents = 0;
                m_RegainEvents = 0;
                m_HandlersFired = 0;
                m_HandlerFaults = 0;
                m_ProbeFaults = 0;
                m_LastEvent = null;
            }
            lock (m_AddLock) s_Handlers.Clear();
        }
    }
}