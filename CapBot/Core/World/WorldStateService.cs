using System;
using System.Collections.Generic;

namespace CapBot.Core.World
{
    // Pluggable seam: the game-facing reader (PulsarWorldSource) implements
    // this; pure domain and tests see only the interface. Implementations
    // must be non-throwing (catch their own exceptions), null-safe, and
    // side-effect-free: reading the world must never mutate the game.
    public interface IWorldSource
    {
        WorldSnapshot Capture(WorldSnapshot previous, int nowMs);
    }

    // How a consumer sees snapshot freshness.
    public enum WorldSnapshotFreshness
    {
        NeverCaptured = 0,
        Stale = 1,          // older than MaxSnapshotAgeMs
        Fresh = 2,
    }

    // ---- Phase 6: world-state cache / transition detector -------------------
    //
    // Pure C# (System* only). Owns the latest WorldSnapshot behind a lock and
    // the last-seen sector/warp values for transition detection. Nothing here
    // scans a scene, reads a wall clock, or touches the game: the source does
    // the reading, callers bring the time, listeners fire OUTSIDE the lock.
    //
    // Inert by construction until boot wires a real source AND a tick caller
    // refreshes it; the fail-open probe contract (stale snapshot => no
    // destructive recovery decisions) means an un-refreshed cache can never
    // cause gameplay behavior changes by itself.
    public static class WorldStateService
    {
        public const int RefreshIntervalMs = 1000;   // ~vanilla decision cadence; refresh is throttled
        public const int MaxSnapshotAgeMs = 10000;   // beyond this a snapshot is "stale"
        private const int MaxTransitionsPerRefresh = 4;

        private static readonly object m_Lock = new object();
        private static IWorldSource m_Source;
        private static WorldSnapshot m_Latest = WorldSnapshot.Empty;
        private static int m_LastRefreshMs = -1;
        private static int m_LastSeenSectorId = -1;
        private static bool m_LastSeenInWarp;
        private static bool m_HasLastSeen;
        private static Action<WorldTransition> m_OnTransition;
        private static bool m_SectorChangedSinceLastRefresh;  // sticky flag, cleared by AcknowledgeSectorChanged
        private static long m_RefreshCount;
        private static long m_ErrorCount;
        private static string m_LastError;

        // ---- configuration -------------------------------------------------
        public static int MinRefreshIntervalMs = RefreshIntervalMs;

        // ---- wiring ----------------------------------------------------------
        // The transition listener fires OUTSIDE the service lock, after the
        // new snapshot is committed. Never fires synchronously from Capture
        // callers' perspective more than once per refresh, and never fires
        // for the very first capture (nothing to transition FROM).
        public static void SetTransitionListener(Action<WorldTransition> listener)
        {
            lock (m_Lock) m_OnTransition = listener;
        }

        public static void SetSource(IWorldSource source)
        {
            lock (m_Lock)
            {
                m_Source = source;
                m_Latest = WorldSnapshot.Empty;
                m_HasLastSeen = false;
                m_SectorChangedSinceLastRefresh = false;
            }
        }

        // ---- cache state -------------------------------------------------------
        public static WorldSnapshot Latest
        {
            get { lock (m_Lock) return m_Latest; }
        }

        public static long RefreshCount { get { lock (m_Lock) return m_RefreshCount; } }
        public static long ErrorCount { get { lock (m_Lock) return m_ErrorCount; } }
        public static string LastError { get { lock (m_Lock) return m_LastError; } }

        public static WorldSnapshotFreshness GetFreshness(int nowMs)
        {
            lock (m_Lock)
            {
                if (m_Latest.IsNeverCaptured) return WorldSnapshotFreshness.NeverCaptured;
                return (nowMs >= m_Latest.SnapshotTimeMs
                        && nowMs - m_Latest.SnapshotTimeMs <= MaxSnapshotAgeMs)
                    ? WorldSnapshotFreshness.Fresh : WorldSnapshotFreshness.Stale;
            }
        }

        // True when the most recent refresh detected a sector change and the
        // change has not been acknowledged yet. Sticky: cleared only by
        // AcknowledgeSectorChanged — so a consumer that checks once per task
        // pass cannot miss a change that happened between its checks.
        public static bool HasUnacknowledgedSectorChange()
        {
            lock (m_Lock) return m_SectorChangedSinceLastRefresh;
        }

        public static void AcknowledgeSectorChanged()
        {
            lock (m_Lock) m_SectorChangedSinceLastRefresh = false;
        }

        // ---- refresh -----------------------------------------------------------
        // Throttled to MinRefreshIntervalMs. nowMs comes from the caller
        // (TaskClock.NowMs in production) — no wall-clock reads here.
        // All exceptions from the source are caught and counted (source is
        // required to be non-throwing; this is the second safety net).
        public static void Refresh(int nowMs)
        {
            IWorldSource source;
            lock (m_Lock)
            {
                source = m_Source;
                if (source == null) return;
                if (m_LastRefreshMs >= 0 && nowMs - m_LastRefreshMs < MinRefreshIntervalMs) return;
            }

            WorldSnapshot previous;
            WorldSnapshot next;
            try
            {
                previous = Latest;
                next = source.Capture(previous, nowMs);
                if (next == null) next = WorldSnapshot.Empty;
            }
            catch (Exception ex)
            {
                RecordError("capture failed: " + ex.GetType().Name);
                return;
            }

            List<WorldTransition> fired = new List<WorldTransition>(MaxTransitionsPerRefresh);
            lock (m_Lock)
            {
                m_Latest = next;
                m_RefreshCount++;
                m_LastRefreshMs = nowMs;

                // Transition detection from last-seen values (NOT from the
                // previous snapshot, so detection survives SetSource resets).
                if (m_HasLastSeen)
                {
                    int curSector = next.Navigation.CurrentSectorId;
                    bool curWarp = next.Navigation.InWarp;
                    if (curSector >= 0 && m_LastSeenSectorId >= 0 && curSector != m_LastSeenSectorId)
                    {
                        fired.Add(new WorldTransition("SECTOR_CHANGED", m_LastSeenSectorId, curSector, nowMs));
                        m_SectorChangedSinceLastRefresh = true;
                    }
                    if (!m_LastSeenInWarp && curWarp)
                        fired.Add(new WorldTransition("WARP_STARTED", m_LastSeenSectorId, curSector, nowMs));
                    else if (m_LastSeenInWarp && !curWarp)
                        fired.Add(new WorldTransition("WARP_ENDED", m_LastSeenSectorId, curSector, nowMs));
                    if (fired.Count >= MaxTransitionsPerRefresh) fired.RemoveRange(MaxTransitionsPerRefresh, fired.Count - MaxTransitionsPerRefresh);
                }
                m_LastSeenSectorId = next.Navigation.CurrentSectorId;
                m_LastSeenInWarp = next.Navigation.InWarp;
                m_HasLastSeen = true;
            }

            // Fire outside the lock — listeners may call back into the service.
            for (int i = 0; i < fired.Count; i++)
            {
                Action<WorldTransition> listener;
                lock (m_Lock) listener = m_OnTransition;
                if (listener == null) break;
                try { listener(fired[i]); }
                catch (Exception ex) { RecordError("transition listener failed: " + ex.GetType().Name); }
            }
        }

        private static void RecordError(string message)
        {
            lock (m_Lock)
            {
                m_ErrorCount++;
                m_LastError = message;
            }
        }

        // Bounded-diagnostics reset for tests; keeps listener + source wiring
        // (callers re-wire explicitly anyway). Mirrors ResetForTests on other
        // managers.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Source = null;
                m_Latest = WorldSnapshot.Empty;
                m_LastRefreshMs = -1;
                m_LastSeenSectorId = -1;
                m_LastSeenInWarp = false;
                m_HasLastSeen = false;
                m_OnTransition = null;
                m_SectorChangedSinceLastRefresh = false;
                m_RefreshCount = 0;
                m_ErrorCount = 0;
                m_LastError = null;
                MinRefreshIntervalMs = RefreshIntervalMs;
            }
        }
    }
}