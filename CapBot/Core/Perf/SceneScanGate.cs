using System;
using System.Collections.Generic;

namespace CapBot.Core.Perf
{
    // ---- Phase 31: scene-scan gate (audit H2) ------------------------------------
    //
    // A per-key re-scan gate for the scripted-sector handlers. The captain tick
    // (PLPlayer.UpdateAIPriorities Postfix) runs EVERY FRAME; the five scripted
    // sector handlers (AtColony/WastedWing/AtRaces/PlanetExploration/HighRollers)
    // each did 2-5 full-scene FindObjectsOfType scans per frame inside it —
    // dozens of scene scans per frame in those sectors (audit H2, violating the
    // no-per-frame-expensive-operations rule).
    //
    // The gate is POLICY ONLY (pure C#, no game refs): Allow(key, nowMs) is
    // true when at least IntervalMs has passed since the last allow for that
    // key (first call always allows). The engine commits the scan by key; the
    // next Allow inside the interval refuses, so each handler runs its scene
    // scans at most 4x/second instead of 60x/second. Between gated runs the
    // handler's last-set AI targets persist in PLBot (game-side), so movement
    // stays fluid — the handler recomputes targets on its cadence.
    //
    // Deterministic: no wall-clock reads (the engine passes TaskClock.NowMs —
    // wrap-aware, virtual in tests). Bounded (MaxKeys=32; overflow refuses to
    // scan-allow until Reset or a free slot — fail-closed, same as every other
    // CapBot bound).
    public static class SceneScanGate
    {
        public const int IntervalMs = 250;   // 4 scans/sec max per key (movement stays fluid)
        public const int MaxKeys = 32;

        private sealed class GateEntry
        {
            internal int LastAllowMs = int.MinValue;
        }

        private static readonly object m_Lock = new object();
        private static readonly Dictionary<string, GateEntry> m_Entries =
            new Dictionary<string, GateEntry>(MaxKeys, StringComparer.Ordinal);

        // Returns true when the caller may run its scene scans now. Does NOT
        // record the pass — Commit(key, nowMs) does (the engine owns the
        // distinction: Allow without Commit means "scans still pending").
        public static bool Allow(string key, int nowMs)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (m_Lock)
            {
                GateEntry e;
                if (!m_Entries.TryGetValue(key, out e))
                {
                    if (m_Entries.Count >= MaxKeys) return false;   // bounded: fail-closed
                    e = new GateEntry();
                    m_Entries.Add(key, e);
                    e.LastAllowMs = nowMs;                          // first call allows AND arms
                    return true;
                }
                return unchecked(nowMs - e.LastAllowMs) >= IntervalMs;
                // NOTE: Allow does not advance the stamp — repeated Allows
                // within the interval all refuse until Commit.
            }
        }

        // Records that a scan pass ran for this key at nowMs (advances the
        // stamp so the next Allow inside the interval refuses).
        public static void Commit(string key, int nowMs)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (m_Lock)
            {
                GateEntry e;
                if (!m_Entries.TryGetValue(key, out e))
                {
                    if (m_Entries.Count >= MaxKeys) return;   // bounded: fail-closed
                    e = new GateEntry();
                    m_Entries.Add(key, e);
                }
                e.LastAllowMs = nowMs;
            }
        }

        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Entries.Clear();
            }
        }

        public static int KeyCount { get { lock (m_Lock) return m_Entries.Count; } }
    }
}