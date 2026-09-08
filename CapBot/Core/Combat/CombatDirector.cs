using System;
using System.Collections.Generic;
using CapBot.Core.World;

namespace CapBot.Core.Combat
{
    // ---- Phase 17: combat director -----------------------------------------------
    //
    // Bounded deterministic COMBAT TRACKING layer. Phase 17 is REPORT-ONLY
    // by MANDATE, not by absence (unlike Phase 16): the P7 catalog DOES
    // contain one combat-adjacent capability (SET_CAPTAIN_TARGET —
    // RegisteredCapabilities.cs, executor-dispatchable via
    // PulsarCapabilityDispatcher.DispatchSetCaptainTarget), but its
    // authorship is owned by the Phase 9 emergency director (the
    // DangerousCombat emergency is the only in-tree author of that
    // capability) and by the legacy captain tick (ComputeDesiredOrder /
    // BoardEnemy / blind-jump own every combat mutation: targets, orders,
    // ClaimShip, AlertLevel, AddHostileShip, blind jump). A combat report
    // is therefore DATA ONLY: the director never fires, never targets,
    // never engages, never disengages, creates NO tasks, and never assigns
    // severity — the P9 emergency director owns combat SEVERITY (the
    // DangerousCombat Severe/Warning ladder) and owns fire/reactor/hull
    // emergencies; this director produces the combat-side coordination
    // record from the P6 snapshot.
    //
    // All inputs are VERIFIED snapshot fields (P6) plus the Phase 17
    // additive capture (P9-style ctor chains):
    //   - ThreatSnapshot.KnownHostileShipIds — the authoritative hostile
    //     set (the ship's own HostileShips list; QualityImprover-safe:
    //     the list is read, hostility logic is never called);
    //   - ThreatSnapshot.OurCombatLevel / PlayerTargetCombatLevel —
    //     INFERRED semantics (research §6.6), data-only;
    //   - ThreatSnapshot.InvadersOnboardCount — Phase 17 additive capture
    //     (PLShipInfoBase.InvadersOnboard, DLL reflection-verified
    //     System.Int32 property);
    //   - ShipSnapshot.InWarp / HullFraction / TookDamageRecently —
    //     per-ship data from the bounded Ships list; TookDamageRecently
    //     is the Phase 17 additive capture of the shipped Patch.cs:242
    //     "took damage recently" window (compile-proven);
    //   - Navigation.InWarp — player-ship warp context.
    //
    // What the director does (all inputs VERIFIED or documented below):
    //   - ENGAGEMENT episode: opens on the first readable pass with >= 1
    //     authoritative hostile, reports CombatOpened immediately
    //     (emergencies must not wait for dwell — the P9 director owns
    //     immediate severity; this director only documents the episode),
    //     refreshes a HostileEngagementReport after EngagementDwellMs of
    //     a STABLE hostile picture (the hostile list can flicker during
    //     combat — the report fires only when the composition is stable),
    //     and closes the episode when hostiles clear (one-shot
    //     HostileClearedReport; re-entry re-arms everything);
    //   - warp-combat picture: hostiles present while the player ship is
    //     in warp (data record — vanilla and legacy own all warp/escape
    //     behavior);
    //   - UnderFireReport: the player ship TookDamageRecently while
    //     hostiles are present (combat-activity edge, one per episode,
    //     re-armed when the condition clears);
    //   - BoarderReport: InvadersOnboardCount > 0 (boarding-side record;
    //     legacy order-6 board-enemy / vanilla repel own the response);
    //   - suppresses every report while inputs are UNKNOWN sentinels
    //     (NaN combat levels / -1 boarders / no hostile ids) — the
    //     detector contract: unknown data never triggers;
    //   - suppresses every report while the snapshot is unreadable
    //     (capture failure or never-captured) — no decisions on
    //     uncertainty.
    //
    // Boundaries (Phase 17 contract):
    //   - No tasks, no task metadata, no registry writes, no scheduler/
    //     claim/executor interaction, no RPCs, no scene scans, no LINQ,
    //     no per-frame work (cadence-gated 5 s, driven from the WorldTick
    //     Postfix after the economy block).
    //   - Deny-by-default authority seam: no probe / faulting probe /
    //     non-master -> no evaluation (clients never produce combat
    //     reports; the hostile list and combat levels are ship-synced
    //     master-authoritative values per the P6 authority model).
    //   - No game enums in the pure domain: the combat-visual classification
    //     is NOT needed this phase (hostile membership comes from the
    //     authoritative HostileShips list, not sector classes) — no
    //     classifier seam is opened (P16 lesson: seams only where a
    //     verified enum vocabulary is actually consumed).
    //   - Fail-safe on stale/missing/never-captured/not-started snapshots
    //     (same 20 s window as the P9/P14/P15/P16 directors).
    //   - All timestamps are explicit nowMs values (TaskClock semantics);
    //     no wall-clock reads; every collection bounded.
    //
    // Audit honesty notes (mirroring the P16 ReserveFloor note):
    //   - The legacy Config combat sliders (AIReactionSpeed, AIAccuracy,
    //     CombatEngageRange, CombatDisengageHealth) are DEAD (audit H4:
    //     nothing reads them). The legacy blind-jump flee logic uses a
    //     hardcoded hull floor of 0.2 and a 60 s cooldown (Patch.cs:431).
    //     This director documents those constants as DATA thresholds
    //     (LegacyBlindJumpHullFraction / LegacyBlindJumpCooldownSec) and
    //     does NOT wire any slider — config wiring is a separate
    //     later-phase concern and would silently change legacy behavior.
    //   - CombatLevelGapUnfavorable mirrors the P9 EmergencyDetector's
    //     INFERRED 1.33 vanilla threat-readout constant as a data-only
    //     gap label; P17 never assigns severity from it.
    //   - Engagement-range reporting is impossible from the snapshot
    //     (no compile-proven PLShipInfoBase.Position read exists in the
    //     repo) and CombatEngageRange is a dead knob — range logic is
    //     OUT OF SCOPE.
    public static class CombatDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 5000;            // decision cadence (1 Hz snapshot, 5 s decisions)
        public const int MaxActiveCombatRecords = 8;     // bounded tracked-combat-record set
        public const int MaxHistory = 16;                // bounded resolved-record history
        public const int ActiveExpiryMs = 60000;         // inputs unreadable past this => record decays
        public const int MaxStaleSnapshotMs = 20000;     // fail-safe: no decisions on older snapshots
        public const int EngagementDwellMs = 15000;      // stable-picture dwell before the engagement report
        public const int MaxPendingLines = 4;            // bounded emission buffer per pass

        // Data-only thresholds (documenting legacy/P9 constants; P17 never
        // assigns severity and never wires a slider).
        public const int HostileSevereCount = 3;         // documents P9 FireSevereCount (EmergencyDetector) — data only
        public const float CombatLevelGapUnfavorable = 1.33f; // mirrors P9 INFERRED threat-readout const — data only
        public const float LegacyBlindJumpHullFraction = 0.2f;  // documents legacy Patch.cs:431 flee floor (audit H4)
        public const int LegacyBlindJumpCooldownSec = 60;       // documents legacy Patch.cs:431 cooldown (audit H4)
        public const int MaxTextLen = 120;               // bounded DATA carry for note text

        public const string TrackIdPrefix = "COMBAT:";   // "COMBAT:<kind>"
        public const string TrackEngagement = "ENGAGEMENT"; // TrackId "COMBAT:ENGAGEMENT"
        public const string TargetKindCombat = "COMBAT";    // diagnostic vocabulary — no capability wiring

        // Public readback record (GetTrack surface — mirrors
        // EconomyRecord/MissionTrackRecord: a public class with public
        // fields; the director hands out the live record for bounded
        // diagnostic reads).
        public sealed class CombatRecord
        {
            public readonly string TrackId;          // "COMBAT:<kind>"
            public readonly string Kind;
            public readonly int FirstSeenMs;

            public int LastSeenMs;                   // last pass with readable inputs
            public long UpdateCount;
            public bool OpenedReported;              // CombatOpened fired

            public CombatRecord(string trackId, string kind, int nowMs)
            {
                TrackId = trackId;
                Kind = kind;
                FirstSeenMs = nowMs;
                LastSeenMs = nowMs;
            }
        }

        private sealed class DirectorState
        {
            public readonly Dictionary<string, CombatRecord> Active =
                new Dictionary<string, CombatRecord>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;

            // engagement episode state (single COMBAT:ENGAGEMENT record)
            public bool EpisodeOpen;                 // hostiles present and readable
            public int EpisodeFirstSeenMs = -1;      // first readable hostile pass of this episode
            public bool OpenedReported;              // CombatOpened fired (immediate)
            public bool EngagementReported;          // HostileEngagementReport fired (dwell)
            public bool WarpPictureReported;         // WarpEngagementReport fired (dwell)
            public int LastHostileCount = -1;        // last readable hostile count (-1 unknown)

            // under-fire episode state (player ship TookDamageRecently while hostiles present)
            public bool UnderFireEpisodeOpen;
            public bool UnderFireReported;

            // boarder episode state (InvadersOnboardCount > 0)
            public bool BoarderEpisodeOpen;

            public long Evaluations;
            public long RecordsTracked;
            public long OpenedReports;
            public long EngagementReports;
            public long WarpPictureReports;
            public long UnderFireReports;
            public long BoarderReports;
            public long ClearedReports;
            public long VanishedReports;
            public long PlansExpired;
            public long StaleRejections;
            public long UnknownInputPasses;
            public string LastUncertainReason;
        }

        private static readonly DirectorState S = new DirectorState();
        private static readonly object m_Lock = new object();

        // ---- seams (pluggable, fail-closed) -------------------------------------
        private static Func<bool> m_AuthorityProbe;         // null/fault => no-op evaluation
        private static Func<int> m_NowMsProvider;           // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_OnDecision;         // CombatLogBridge attaches at boot

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

        // ---- readback (diagnostics/tests) ----------------------------------------
        public static int ActiveRecordCount { get { lock (m_Lock) return S.Active.Count; } }
        public static int HistoryCount { get { lock (m_Lock) return S.HistoryIds.Count; } }
        public static long EvaluationCount { get { lock (m_Lock) return S.Evaluations; } }
        public static long RecordsTrackedCount { get { lock (m_Lock) return S.RecordsTracked; } }
        public static long OpenedReportCount { get { lock (m_Lock) return S.OpenedReports; } }
        public static long EngagementReportCount { get { lock (m_Lock) return S.EngagementReports; } }
        public static long WarpPictureReportCount { get { lock (m_Lock) return S.WarpPictureReports; } }
        public static long UnderFireReportCount { get { lock (m_Lock) return S.UnderFireReports; } }
        public static long BoarderReportCount { get { lock (m_Lock) return S.BoarderReports; } }
        public static long ClearedReportCount { get { lock (m_Lock) return S.ClearedReports; } }
        public static long VanishedReportCount { get { lock (m_Lock) return S.VanishedReports; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static long UnknownInputPassCount { get { lock (m_Lock) return S.UnknownInputPasses; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        // Deterministic lookup by track id (null when absent).
        public static CombatRecord GetTrack(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            lock (m_Lock)
            {
                CombatRecord r;
                return S.Active.TryGetValue(trackId, out r) ? r : null;
            }
        }

        // One bounded diagnostic line per tracked record (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CombatRecord> kv in S.Active)
                {
                    CombatRecord r = kv.Value;
                    lines.Add("track " + r.TrackId
                        + " upd=" + r.UpdateCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + (r.OpenedReported ? " opened" : ""));
                }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                lines.Add("combat=" + S.Active.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tracked=" + S.RecordsTracked
                    + " opened=" + S.OpenedReports + " engage=" + S.EngagementReports
                    + " warp=" + S.WarpPictureReports + " underFire=" + S.UnderFireReports
                    + " boarders=" + S.BoarderReports);
                lines.Add("cleared=" + S.ClearedReports + " vanished=" + S.VanishedReports
                    + " stale=" + S.StaleRejections + " unknownInputs=" + S.UnknownInputPasses
                    + " uncertain=" + (S.LastUncertainReason ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Pulls the authoritative snapshot through the world seam (production:
        // WorldStateService.Latest) so the caller stays clock-only. Returns the
        // number of NEW combat reports this pass (0 on the quiet path AND on
        // every failure path). Never throws. Creates NO tasks (report-only
        // by mandate — SET_CAPTAIN_TARGET authorship is P9/legacy-owned).
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
            if (auth == null) return 0;
            bool isAuth;
            try { isAuth = auth(); } catch (Exception) { return 0; }
            if (!isAuth) return 0;

            lock (m_Lock)
            {
                if (S.LastEvalMs >= 0 && unchecked(nowMs - S.LastEvalMs) < MinRecheckMs) return 0;
                S.LastEvalMs = nowMs;
            }

            // ---- fail-safe snapshot gate ------------------------------------
            WorldSnapshot snapshot = null;
            if (world != null)
            {
                try { snapshot = world(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured)
            {
                MarkUncertain("no world snapshot captured (fail-safe: no combat tracking)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no combat tracking)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no combat tracking)");
                return 0;
            }

            List<string> pending = new List<string>(MaxPendingLines);
            int reports = 0;

            lock (m_Lock)
            {
                // ---- inputs (unknown sentinels never trigger) -------------------
                ThreatSnapshot threats = snapshot.Threats;
                // The authoritative hostile set. A null threat section renders
                // as the bounded "unknown" default (empty ids) — treat as zero
                // hostiles, not as an unknown (the P6 Bounded contract makes
                // capture failure indistinguishable from an empty readable
                // list; absence handling is the episode-close path).
                int hostileCount = threats != null ? threats.KnownHostileShipIds.Count : 0;
                float ourLevel = threats != null ? threats.OurCombatLevel : float.NaN;
                float targetLevel = threats != null ? threats.PlayerTargetCombatLevel : float.NaN;
                int invaders = threats != null ? threats.InvadersOnboardCount : -1;

                NavigationSnapshot navigation = snapshot.Navigation;
                bool playerInWarp = navigation != null && navigation.InWarp;

                // Player-ship hull fraction + took-damage flag (join on
                // IsPlayerShip within the bounded Ships list; player ship is
                // FIRST by contract, but the scan is defensive and bounded).
                float ourHullFraction = float.NaN;
                bool playerTookDamage = false;
                IReadOnlyList<ShipSnapshot> ships = snapshot.Ships;
                for (int i = 0; ships != null && i < ships.Count && i < WorldSnapshot.MaxShips; i++)
                {
                    ShipSnapshot ship = ships[i];
                    if (ship == null || !ship.IsPlayerShip) continue;
                    ourHullFraction = ship.HullFraction;
                    playerTookDamage = ship.TookDamageRecently;
                    break;
                }

                bool hostilesReadable = hostileCount > 0;

                // ---- engagement episode -----------------------------------------
                if (hostilesReadable)
                {
                    EnsureEngagementTracked(nowMs, pending, ref reports);
                    if (!S.EpisodeOpen)
                    {
                        // Fresh engagement episode: the stable-picture dwell
                        // clock starts on the first readable hostiles pass.
                        S.EpisodeOpen = true;
                        S.EpisodeFirstSeenMs = nowMs;
                        S.EngagementReported = false;
                        S.WarpPictureReported = false;
                        S.LastHostileCount = hostileCount;
                    }
                    EvaluateEngagement(
                        hostileCount, ourLevel, targetLevel, playerInWarp,
                        ourHullFraction, playerTookDamage, nowMs, pending, ref reports);
                    EvaluateUnderFire(playerTookDamage, nowMs, pending, ref reports);
                }
                else
                {
                    // No authoritative hostiles: close any open episode.
                    // The tracked record itself survives (hygiene owns expiry);
                    // only the episode state resets, and CombatOpened re-arms
                    // on the NEXT episode's record re-tracking (EnsureEngage-
                    // mentTracked emits again only after the record decays —
                    // within a live record the open edge stays quiet).
                    if (S.EpisodeOpen)
                    {
                        S.EpisodeOpen = false;
                        S.EpisodeFirstSeenMs = -1;
                        S.EngagementReported = false;
                        S.WarpPictureReported = false;
                        S.LastHostileCount = -1;
                        S.UnderFireEpisodeOpen = false;
                        S.UnderFireReported = false;
                        S.ClearedReports++;
                        reports++;
                        pending.Add("HostileClearedReport (engagement episode closed)");
                    }
                    // A readable zero-hostile pass is a legitimate quiet pass
                    // (NOT an unknown input); nothing is counted.
                }

                // ---- boarder rule (independent of hostiles) ----------------------
                EvaluateBoarders(invaders, pending, ref reports);

                // ---- hygiene: expire the engagement record when the hostile
                // set stays empty past ActiveExpiryMs (one-shot vanished
                // report, P15/P16 mirror). A LIVE engagement record refreshes
                // every readable pass and never expires while hostiles remain.
                if (!hostilesReadable)
                {
                    List<string> expired = new List<string>(MaxActiveCombatRecords);
                    foreach (KeyValuePair<string, CombatRecord> kv in S.Active)
                    {
                        if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs) expired.Add(kv.Key);
                    }
                    for (int i = 0; i < expired.Count; i++)
                    {
                        CombatRecord expiredRec = S.Active[expired[i]];
                        S.Active.Remove(expired[i]);
                        S.HistoryIds.Enqueue(expired[i]);
                        while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                        S.PlansExpired++;
                        S.VanishedReports++;
                        reports++;
                        pending.Add("CombatVanished " + expiredRec.TrackId + " (inputs unreadable)");
                    }
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count; i++) Emit(pending[i]);
            return reports;
        }

        // ---- rules -------------------------------------------------------------------

        private static void EnsureEngagementTracked(int nowMs, List<string> pending, ref int reports)
        {
            string trackId = TrackIdPrefix + TrackEngagement;
            CombatRecord rec;
            if (S.Active.TryGetValue(trackId, out rec))
            {
                rec.UpdateCount++;
                rec.LastSeenMs = nowMs;
                return;
            }

            if (S.Active.Count >= MaxActiveCombatRecords)
            {
                // Bounded tracked set: shed the oldest by LastSeenMs
                // (tie -> lowest key order). Bounded scan (<= 8). With a
                // single record this path is defensive-only (never reached
                // in the P17 contract; kept for house-pattern parity).
                string oldest = null;
                foreach (KeyValuePair<string, CombatRecord> okv in S.Active)
                {
                    if (oldest == null
                        || okv.Value.LastSeenMs < S.Active[oldest].LastSeenMs
                        || (okv.Value.LastSeenMs == S.Active[oldest].LastSeenMs
                            && string.CompareOrdinal(okv.Key, oldest) < 0))
                    {
                        oldest = okv.Key;
                    }
                }
                if (oldest != null)
                {
                    S.Active.Remove(oldest);
                    S.HistoryIds.Enqueue(oldest);
                    while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                    S.PlansExpired++;
                    pending.Add("CombatShed " + oldest + " (tracked set full)");
                }
            }

            rec = new CombatRecord(trackId, TrackEngagement, nowMs);
            S.Active[trackId] = rec;
            S.RecordsTracked++;
            rec.OpenedReported = true;
            S.OpenedReports++;
            reports++;
            pending.Add("CombatOpened " + trackId);
        }

        // Engagement picture: a HostileEngagementReport once the hostile
        // picture is stable for EngagementDwellMs (one per episode; a
        // composition change re-arms — fresh dwell), plus the warp-combat
        // picture under the same stability window. Data-only gap label
        // mirrors the P9 INFERRED threat-readout constant.
        private static void EvaluateEngagement(
            int hostileCount, float ourLevel, float targetLevel, bool playerInWarp,
            float ourHullFraction, bool playerTookDamage, int nowMs,
            List<string> pending, ref int reports)
        {
            // Composition-change re-arm: the picture is only "stable" when the
            // hostile count matches the count the episode last reported (or
            // opened) with. A change re-arms the engagement report with a
            // FRESH dwell clock (EpisodeFirstSeenMs restarts); the warp
            // picture re-arms with it.
            if (hostileCount != S.LastHostileCount)
            {
                S.EngagementReported = false;
                S.WarpPictureReported = false;
                S.LastHostileCount = hostileCount;
                S.EpisodeFirstSeenMs = nowMs;
            }

            if (!S.EngagementReported
                && S.EpisodeFirstSeenMs >= 0
                && unchecked(nowMs - S.EpisodeFirstSeenMs) >= EngagementDwellMs)
            {
                S.EngagementReported = true;
                S.EngagementReports++;
                reports++;
                // Data-only gap label: "unfavorable" mirrors the P9 INFERRED
                // threat-readout constant (target level > ours * 1.33); never
                // severity, never target authorship. NaN levels report as "-".
                string gap = !float.IsNaN(ourLevel) && !float.IsNaN(targetLevel) && targetLevel > ourLevel * CombatLevelGapUnfavorable
                    ? "unfavorable" : "-";
                pending.Add("HostileEngagementReport hostiles="
                    + hostileCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " ourLevel=" + (float.IsNaN(ourLevel) ? "-" : ourLevel.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                    + " targetLevel=" + (float.IsNaN(targetLevel) ? "-" : targetLevel.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                    + " gap=" + gap
                    + " hull=" + (float.IsNaN(ourHullFraction) ? "-" : ourHullFraction.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
            }

            // Warp-combat picture: hostiles present while the player ship is
            // in warp (data record only — vanilla/legacy own all warp/escape
            // behavior). One per episode, dwell-gated by the same stability
            // window (reset on composition change above).
            if (playerInWarp && !S.WarpPictureReported
                && S.EpisodeFirstSeenMs >= 0
                && unchecked(nowMs - S.EpisodeFirstSeenMs) >= EngagementDwellMs)
            {
                S.WarpPictureReported = true;
                S.WarpPictureReports++;
                reports++;
                pending.Add("WarpEngagementReport hostiles="
                    + hostileCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // Under-fire edge: the player ship TookDamageRecently while hostiles
        // are present. One report per episode, re-armed when the condition
        // clears. Pure data — the P9 director owns fire/hull SEVERITY.
        private static void EvaluateUnderFire(bool playerTookDamage, int nowMs, List<string> pending, ref int reports)
        {
            if (playerTookDamage)
            {
                if (!S.UnderFireEpisodeOpen)
                {
                    S.UnderFireEpisodeOpen = true;
                    S.UnderFireReported = true;
                    S.UnderFireReports++;
                    reports++;
                    pending.Add("UnderFireReport (player ship recently damaged; hostiles present)");
                }
            }
            else
            {
                S.UnderFireEpisodeOpen = false;
                S.UnderFireReported = false;
            }
        }

        // Boarder observation: InvadersOnboardCount > 0 (game-owned data;
        // -1 unknown never triggers and counts an unknown-input pass — the
        // P16 accounting semantic). One report per episode, re-armed when
        // boarders clear. Data only — vanilla repel + legacy order-6 own the
        // response (Patch.cs ComputeDesiredOrder).
        private static void EvaluateBoarders(int invaders, List<string> pending, ref int reports)
        {
            if (invaders < 0)
            {
                S.UnknownInputPasses++;
                return;
            }
            bool boarders = invaders > 0;
            if (boarders)
            {
                if (!S.BoarderEpisodeOpen)
                {
                    S.BoarderEpisodeOpen = true;
                    S.BoarderReports++;
                    reports++;
                    pending.Add("BoarderReport boarders="
                        + invaders.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                S.BoarderEpisodeOpen = false;
            }
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("CombatUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Active.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.EpisodeOpen = false;
                S.EpisodeFirstSeenMs = -1;
                S.OpenedReported = false;
                S.EngagementReported = false;
                S.WarpPictureReported = false;
                S.LastHostileCount = -1;
                S.UnderFireEpisodeOpen = false;
                S.UnderFireReported = false;
                S.BoarderEpisodeOpen = false;
                S.Evaluations = 0;
                S.RecordsTracked = 0;
                S.OpenedReports = 0;
                S.EngagementReports = 0;
                S.WarpPictureReports = 0;
                S.UnderFireReports = 0;
                S.BoarderReports = 0;
                S.ClearedReports = 0;
                S.VanishedReports = 0;
                S.PlansExpired = 0;
                S.StaleRejections = 0;
                S.UnknownInputPasses = 0;
                S.LastUncertainReason = null;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_OnDecision = null;
            }
        }
    }
}