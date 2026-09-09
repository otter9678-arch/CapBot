using System;
using System.Collections.Generic;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 49: live-telemetry symptom detectors ------------------------------
    // Pure C# windowing over the Unity log stream (the game-facing subscription
    // lives in Mod.cs; this class never touches Unity/PML/Harmony — the P19
    // lesson). Counts exception fingerprints per attributed mod inside a
    // sliding window and feeds ConflictEngine.RecordSymptom at the storm
    // threshold: the bridge from live exceptions to the engine's evidence
    // intake. The engine stays the only classifier; a recorded symptom alone
    // can only ever yield PROBABLE/OBSERVE (ConflictRules.Classify) — the A/B
    // legs stay manual (the quarantine path is unreachable from here).
    //
    // Honesty rules:
    //   - an exception is attributed ONLY via the injected resolver (the
    //     production resolver matches loaded mod assembly names against the
    //     stack text); unresolvable => unattributed counter, never invented;
    //   - CapBot's own log lines are skipped (never self-attribute);
    //   - LogType Warning/Log events are noise and never counted
    //     (UnityEngine.LogType verified: Error=0, Assert=1, Warning=2,
    //     Log=3, Exception=4).
    public static class SymptomDetectors
    {
        private sealed class WindowState
        {
            internal long FirstMs;
            internal long LastMs;
            internal int Count;
            internal bool StormRecorded;   // one RecordSymptom per threshold crossing
        }

        internal const int StormWindowMs = 60000;   // sliding window
        internal const int StormThreshold = 20;     // events per window => ExceptionStorm
        private const int MaxFingerprints = 64;     // bounded (mod|fingerprint keys)
        private const int MaxFingerprintLen = 200;
        private const int MaxStatusRows = 6;

        private static readonly object m_Lock = new object();
        private static readonly Dictionary<string, WindowState> m_Windows =
            new Dictionary<string, WindowState>(16, StringComparer.Ordinal);
        private static readonly List<string> m_KeyOrder = new List<string>(16);
        private static Func<string, string> m_ModResolver;    // stackTrace -> modName (""/null = unattributed)
        private static Action<string> m_AuditListener;

        private static int m_Unattributed;
        private static int m_Dropped;
        private static int m_SymptomsRecorded;
        private static string m_LastUnattributedFingerprint = "";

        // ---- seams --------------------------------------------------------------

        public static void SetModResolver(Func<string, string> resolver)
        {
            lock (m_Lock) { m_ModResolver = resolver; }
        }

        public static void SetAuditListener(Action<string> listener)
        {
            lock (m_Lock) { m_AuditListener = listener; }
        }

        // ---- intake --------------------------------------------------------------

        // One log event. logType is the numeric UnityEngine.LogType value
        // (verified mapping above). nowMs comes from the caller's clock seam.
        // Fail-safe by contract: this runs inside the Unity log callback —
        // any fault is swallowed, never propagated into the game.
        public static void OnLog(string condition, string stackTrace, int logType, long nowMs)
        {
            if (logType < 0 || logType > 4) return;
            if (logType == 2 || logType == 3) return;   // Warning/Log: noise
            try
            {
                if (string.IsNullOrEmpty(condition)) return;
                if (condition.IndexOf("[CapBot:", StringComparison.Ordinal) >= 0) return;   // never self-attribute

                string fingerprint = ExtractExceptionType(condition) + "|" + ExtractFirstFrameMethod(stackTrace);
                if (fingerprint.Length > MaxFingerprintLen) fingerprint = fingerprint.Substring(0, MaxFingerprintLen);

                string modName = null;
                Func<string, string> resolver = m_ModResolver;
                if (resolver != null)
                {
                    try { modName = resolver(stackTrace); }
                    catch (Exception) { modName = null; }   // resolver fault = unattributed (fail-closed)
                }

                lock (m_Lock)
                {
                    if (string.IsNullOrEmpty(modName))
                    {
                        m_Unattributed++;
                        m_LastUnattributedFingerprint = fingerprint;
                        return;
                    }

                    string key = modName + "|" + fingerprint;
                    WindowState w;
                    if (!m_Windows.TryGetValue(key, out w))
                    {
                        if (m_Windows.Count >= MaxFingerprints) { m_Dropped++; return; }
                        w = new WindowState();
                        w.FirstMs = nowMs;
                        m_Windows[key] = w;
                        m_KeyOrder.Add(key);
                    }
                    if (nowMs - w.FirstMs > StormWindowMs)
                    {
                        w.FirstMs = nowMs;
                        w.Count = 0;
                        w.StormRecorded = false;
                    }
                    w.Count++;
                    w.LastMs = nowMs;

                    if (!w.StormRecorded && w.Count >= StormThreshold)
                    {
                        w.StormRecorded = true;
                        m_SymptomsRecorded++;
                        bool recorded = ConflictEngine.RecordSymptom(
                            modName, SymptomKind.ExceptionStorm, modName, fingerprint,
                            w.Count, w.FirstMs, w.LastMs);
                        EmitLocked("SymptomDetected mod=" + modName + " kind=ExceptionStorm"
                            + " count=" + w.Count + " windowMs=" + StormWindowMs
                            + " fingerprint=" + fingerprint
                            + " engine=" + (recorded ? "recorded" : "refused"));
                        if (recorded)
                        {
                            try { ConflictEngine.Evaluate(modName, nowMs); }
                            catch (Exception) { /* engine audit must never crash the detector */ }
                        }
                    }
                }
            }
            catch (Exception) { /* the detector must never become the crash source */ }
        }

        // ---- readbacks -----------------------------------------------------------

        public static int TrackedCount { get { lock (m_Lock) { return m_Windows.Count; } } }
        public static int UnattributedCount { get { lock (m_Lock) { return m_Unattributed; } } }
        public static int DroppedCount { get { lock (m_Lock) { return m_Dropped; } } }
        public static int SymptomsRecordedCount { get { lock (m_Lock) { return m_SymptomsRecorded; } } }
        internal static int MaxFingerprintsBound { get { return MaxFingerprints; } }

        public static string UnattributedLastFingerprint()
        {
            lock (m_Lock) { return m_LastUnattributedFingerprint; }
        }

        // Bounded status surface (summary + ≤MaxStatusRows window rows in
        // insertion order; deterministic).
        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>(MaxStatusRows + 2);
            lock (m_Lock)
            {
                lines.Add("SymptomDetectors: tracked=" + m_Windows.Count +
                          " storms=" + m_SymptomsRecorded +
                          " unattributed=" + m_Unattributed +
                          " dropped=" + m_Dropped +
                          " threshold=" + StormThreshold + "/" + StormWindowMs + "ms");
                int shown = 0;
                for (int i = 0; i < m_KeyOrder.Count && shown < MaxStatusRows; i++)
                {
                    WindowState w;
                    if (!m_Windows.TryGetValue(m_KeyOrder[i], out w)) continue;
                    lines.Add("SymptomDetectors: " + m_KeyOrder[i] +
                              " count=" + w.Count +
                              (w.StormRecorded ? " STORM" : ""));
                    shown++;
                }
            }
            return lines;
        }

        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Windows.Clear();
                m_KeyOrder.Clear();
                m_ModResolver = null;
                m_AuditListener = null;
                m_Unattributed = 0;
                m_Dropped = 0;
                m_SymptomsRecorded = 0;
                m_LastUnattributedFingerprint = "";
            }
        }

        // ---- internals -----------------------------------------------------------

        // Exception header: first line of condition up to the first ':' or the
        // whole first line (bounded). "NullReferenceException: Object …" =>
        // "NullReferenceException"; plain error text => its first token run.
        private static string ExtractExceptionType(string condition)
        {
            string first = condition;
            int nl = first.IndexOfAny(new char[] { '\r', '\n' });
            if (nl >= 0) first = first.Substring(0, nl);
            int colon = first.IndexOf(':');
            if (colon > 0) first = first.Substring(0, colon);
            first = first.Trim();
            if (first.Length > 96) first = first.Substring(0, 96);
            return first.Length == 0 ? "unknown" : first;
        }

        // First "at Type.Method (…)" stack frame, up to the '(' (bounded).
        private static string ExtractFirstFrameMethod(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return "nostack";
            int start = 0;
            while (start < stackTrace.Length)
            {
                int nl = stackTrace.IndexOf('\n', start);
                string line = nl < 0 ? stackTrace.Substring(start) : stackTrace.Substring(start, nl - start);
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("at ", StringComparison.Ordinal))
                {
                    string frame = trimmed.Substring(3);
                    int paren = frame.IndexOf('(');
                    if (paren > 0) frame = frame.Substring(0, paren);
                    frame = frame.Trim();
                    if (frame.Length > 128) frame = frame.Substring(0, 128);
                    return frame.Length == 0 ? "nostack" : frame;
                }
                if (nl < 0) break;
                start = nl + 1;
            }
            return "nostack";
        }

        private static void EmitLocked(string line)
        {
            Action<string> listener = m_AuditListener;
            if (listener == null) return;
            try { listener(line); }
            catch (Exception) { /* audit listeners are fail-safe by contract */ }
        }
    }
}