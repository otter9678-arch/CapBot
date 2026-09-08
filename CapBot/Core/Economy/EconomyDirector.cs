using System;
using System.Collections.Generic;
using CapBot.Core.World;

namespace CapBot.Core.Economy
{
    // ---- Phase 16: economy director ----------------------------------------------
    //
    // Bounded deterministic ECONOMY TRACKING layer. Phase 16 is deliberately
    // REPORT-ONLY, exactly like Phase 15: the P7 capability catalog contains
    // NO economy/trade/purchase capability (deliberately excluded in Phase 7 —
    // docs/CAPABILITIES.md "no speculative combat/mission/economy/build
    // capabilities"), so there is no verified, executor-dispatchable action an
    // economy plan could request. A task without CapabilityId metadata fails
    // at start per the Phase 8 executor contract, so the director produces
    // DATA records + bounded diagnostic lines and creates NO tasks. Trade
    // RPCs (BuyComponent/SellComponent/CaptainBuy_Fuel/_Coolant/_MissileRefill/
    // AttemptExtraction), direct credit mutation patterns (legacy audit M10),
    // CrewPurchaseLimitsEnabled (audit L4), PLTradeData (referenced nowhere —
    // treated as nonexistent), and ShopRepMultiplier internals (private helper,
    // body never verified) all remain OUT OF SCOPE. The legacy BotEconomy/
    // BotExtractor/HandleShop behaviors in Autonomy.cs/Patch.cs are untouched.
    //
    // What the director does (all inputs VERIFIED snapshot fields):
    //   - tracks the crew-credits picture with one-shot edge reports:
    //     EconomyOpened (first readable credits reading), CreditsLowReport
    //     (credits fall to or below the reserve band — one report per episode,
    //     re-armed when credits recover above the band), CreditsDeltaReport
    //     (large credit change beyond MaxDeltaReportAbs; direction + magnitude
    //     only, never cause inference — the snapshot cannot attribute deltas);
    //   - reports store-sector presence (StoreSectorReport) when the
    //     shop-sector classifier seam (production: the compile-proven
    //     ESectorVisualIndication shop-class list, Patch.cs:169-171) marks the
    //     current sector as a shop AND the ship has dwelled there StoreDwellMs
    //     — one report per sector episode;
    //   - reports FUEL/COOLANT AFFORDABILITY (FuelAffordabilityReport /
    //     CoolantAffordabilityReport): supply low (mirroring the P9 warning
    //     thresholds) AND the additive unit-price capture (Phase 16 P6
    //     addition — PLServer.GetFuelBasePrice()/GetCoolantBasePrice(),
    //     compile-proven Patch.cs:2220/2234) readable AND credits cannot buy
    //     even one unit — one report per episode, re-armed when the condition
    //     clears. Pure data, no purchase action — legacy HandleShop owns all
    //     buying behavior, and the P9 emergency director owns low-supply
    //     SEVERITY; this is the economy-side coordination record (can the crew
    //     afford the fix AT ALL);
    //   - reports warp-toll inaffordability (WarpTollReport) when a snapshot
    //     WARP_STATION carries a positive Price that exceeds readable credits
    //     (verified comparison shape: Patch.cs:2609), dwell-gated, one report
    //     per toll episode (the flight-AI cache list is not order-stable);
    //   - suppresses every report while inputs are UNKNOWN sentinels
    //     (Credits == -1 / NaN coolant / -1 fuel / -1 prices / -1 sector) —
    //     the detector contract: unknown data never triggers;
    //   - suppresses every report while the snapshot is unreadable (capture
    //     failure or never-captured) — no decisions on uncertainty.
    //
    // Boundaries (Phase 16 contract):
    //   - No tasks, no task metadata, no registry writes, no scheduler/claim/
    //     executor interaction, no RPCs, no scene scans, no LINQ, no per-frame
    //     work (cadence-gated 5 s, driven from the WorldTick Postfix after the
    //     mission block).
    //   - Deny-by-default authority seam: no probe / faulting probe /
    //     non-master -> no evaluation (clients never produce economy reports;
    //     credits are MasterDerived host-computed values per the P6 authority
    //     model).
    //   - The shop-sector classifier is a SEAM (deny-by-default: null or
    //     faulting classifier => never a shop sector), mirroring the P10
    //     SetRoleNameResolver pattern — the domain stays pure C# with zero
    //     game references.
    //   - Fail-safe on stale/missing/never-captured/not-started snapshots
    //     (same 20 s window as the P9/P14/P15 directors).
    //   - All timestamps are explicit nowMs values (TaskClock semantics); no
    //     wall-clock reads; every collection bounded.
    //
    // Audit honesty note (H4): the legacy MinCreditsReserve config slider is
    // DEAD (nothing reads it — grep-verified in CAPBOT_AUDIT.md). This
    // director documents the legacy hardcoded CREDIT_RESERVE = 2500 as the
    // reporting floor (ReserveFloor constant) but does NOT wire the slider —
    // config wiring is a separate, later-phase concern and would silently
    // change legacy behavior if adopted here.
    public static class EconomyDirector
    {
        // ---- bounds + cadence ---------------------------------------------------
        public const int MinRecheckMs = 5000;          // decision cadence (1 Hz snapshot, 5 s decisions)
        public const int MaxActiveEconomyRecords = 8;  // bounded tracked-economy-record set
        public const int MaxHistory = 16;              // bounded resolved-record history
        public const int ActiveExpiryMs = 60000;       // credits unreadable past this => record decays
        public const int MaxStaleSnapshotMs = 20000;   // fail-safe: no decisions on older snapshots
        public const int StoreDwellMs = 15000;         // shop-sector presence dwell before the report
        public const int WarpTollDwellMs = 15000;      // warp-toll observation dwell before the report
        public const int ReserveFloor = 2500;          // legacy CREDIT_RESERVE documentation (audit H4)
        public const int MaxDeltaReportAbs = 10000;    // credits delta beyond this fires a delta report
        public const int FuelLowCapsules = 2;          // mirrors P9 FuelWarningCapsules (data threshold only)
        public const float CoolantLowPercent = 30f;    // mirrors P9 CoolantWarningPercent (data threshold only)
        public const int MaxTextLen = 120;             // bounded DATA carry for note text

        public const string TrackIdPrefix = "ECONOMY:";     // "ECONOMY:<kind>"
        public const string TrackCredits = "CREDITS";       // TrackId "ECONOMY:CREDITS"
        public const string TargetKindEconomy = "ECONOMY";  // diagnostic vocabulary — no capability exists

        // Public readback record (GetTrack surface — mirrors MissionTrackRecord:
        // a public class with public fields; the director hands out the live
        // record for bounded diagnostic reads, exactly as P15 does).
        public sealed class EconomyRecord
        {
            public readonly string TrackId;          // "ECONOMY:<kind>"
            public readonly string Kind;
            public readonly int FirstSeenMs;

            public int LastSeenMs;                   // last pass with readable inputs
            public long UpdateCount;
            public bool OpenedReported;              // EconomyOpened fired

            public EconomyRecord(string trackId, string kind, int nowMs)
            {
                TrackId = trackId;
                Kind = kind;
                FirstSeenMs = nowMs;
                LastSeenMs = nowMs;
            }
        }

        private sealed class DirectorState
        {
            public readonly Dictionary<string, EconomyRecord> Active =
                new Dictionary<string, EconomyRecord>(StringComparer.Ordinal);
            public readonly Queue<string> HistoryIds = new Queue<string>();
            public int LastEvalMs = -1;

            // credits edge-tracking (ECONOMY:CREDITS record semantics)
            public int LastCredits = -1;             // last readable credits (-1 unknown)
            public bool LowEpisodeOpen;              // reserve-band episode state

            // store-sector episode state
            public int StoreSectorId = -1;           // shop sector we are dwelling in (-1 none)
            public int StoreFirstSeenMs = -1;
            public bool StoreReported;               // StoreSectorReport fired for this episode

            // warp-toll episode state
            public int WarpTollPrice = -1;           // last observed unaffordable toll (-1 none)
            public int WarpTollFirstSeenMs = -1;
            public bool WarpTollReported;            // WarpTollReport fired for this episode

            // affordability episode states
            public bool FuelAffordEpisodeOpen;
            public bool CoolantAffordEpisodeOpen;

            public long Evaluations;
            public long RecordsTracked;
            public long OpenedReports;
            public long LowReports;
            public long DeltaReports;
            public long StoreReports;
            public long FuelAffordabilityReports;
            public long CoolantAffordabilityReports;
            public long WarpTollReports;
            public long VanishedReports;
            public long DuplicatesSuppressed;
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
        private static Func<int, bool> m_ShopSectorClassifier; // raw ESectorVisualIndication -> is shop (deny-by-default)
        private static Action<string> m_OnDecision;         // EconomyLogBridge attaches at boot

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_AuthorityProbe = probe; }
        public static void SetNowMsProvider(Func<int> provider) { lock (m_Lock) m_NowMsProvider = provider; }
        public static void SetWorldProvider(Func<WorldSnapshot> provider) { lock (m_Lock) m_WorldProvider = provider; }
        public static void SetShopSectorClassifier(Func<int, bool> classifier) { lock (m_Lock) m_ShopSectorClassifier = classifier; }
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
        public static long LowReportCount { get { lock (m_Lock) return S.LowReports; } }
        public static long DeltaReportCount { get { lock (m_Lock) return S.DeltaReports; } }
        public static long StoreReportCount { get { lock (m_Lock) return S.StoreReports; } }
        public static long FuelAffordabilityReportCount { get { lock (m_Lock) return S.FuelAffordabilityReports; } }
        public static long CoolantAffordabilityReportCount { get { lock (m_Lock) return S.CoolantAffordabilityReports; } }
        public static long WarpTollReportCount { get { lock (m_Lock) return S.WarpTollReports; } }
        public static long VanishedReportCount { get { lock (m_Lock) return S.VanishedReports; } }
        public static long DuplicatesSuppressedCount { get { lock (m_Lock) return S.DuplicatesSuppressed; } }
        public static long PlansExpiredCount { get { lock (m_Lock) return S.PlansExpired; } }
        public static long StaleRejectionCount { get { lock (m_Lock) return S.StaleRejections; } }
        public static long UnknownInputPassCount { get { lock (m_Lock) return S.UnknownInputPasses; } }
        public static string LastUncertainReason { get { lock (m_Lock) return S.LastUncertainReason; } }

        // Deterministic lookup by track id (null when absent).
        public static EconomyRecord GetTrack(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            lock (m_Lock)
            {
                EconomyRecord r;
                return S.Active.TryGetValue(trackId, out r) ? r : null;
            }
        }

        // One bounded diagnostic line per tracked record (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, EconomyRecord> kv in S.Active)
                {
                    EconomyRecord r = kv.Value;
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
                lines.Add("economy=" + S.Active.Count + " history=" + S.HistoryIds.Count
                    + " evals=" + S.Evaluations + " tracked=" + S.RecordsTracked
                    + " opened=" + S.OpenedReports + " low=" + S.LowReports
                    + " delta=" + S.DeltaReports + " store=" + S.StoreReports
                    + " tolls=" + S.WarpTollReports);
                lines.Add("fuelAff=" + S.FuelAffordabilityReports
                    + " coolantAff=" + S.CoolantAffordabilityReports
                    + " vanished=" + S.VanishedReports + " dupSuppressed=" + S.DuplicatesSuppressed
                    + " expired=" + S.PlansExpired + " stale=" + S.StaleRejections
                    + " unknownInputs=" + S.UnknownInputPasses
                    + " uncertain=" + (S.LastUncertainReason ?? "-"));
            }
            return lines;
        }

        // ---- the evaluation pass ---------------------------------------------------
        //
        // Pulls the authoritative snapshot through the world seam (production:
        // WorldStateService.Latest) so the caller stays clock-only. Returns the
        // number of NEW economy reports this pass (0 on the quiet path AND on
        // every failure path). Never throws. Creates NO tasks (report-only phase).
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
                MarkUncertain("no world snapshot captured (fail-safe: no economy tracking)");
                return 0;
            }
            if (unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0)
            {
                lock (m_Lock) S.StaleRejections++;
                MarkUncertain("world snapshot stale or from the future (fail-safe: no economy tracking)");
                return 0;
            }
            if (!snapshot.GameStarted)
            {
                MarkUncertain("game not started (fail-safe: no economy tracking)");
                return 0;
            }

            List<string> pending = new List<string>(4);
            int reports = 0;

            lock (m_Lock)
            {
                // ---- inputs (unknown sentinels never trigger) -------------------
                ResourceSnapshot resources = snapshot.Resources;
                int credits = resources != null ? resources.Credits : -1;
                int fuel = resources != null ? resources.FuelCapsules : -1;
                float coolant = resources != null ? resources.CoolantLevelPercent : float.NaN;
                int fuelPrice = resources != null ? resources.FuelBasePrice : -1;
                int coolantPrice = resources != null ? resources.CoolantBasePrice : -1;

                NavigationSnapshot navigation = snapshot.Navigation;
                int sectorId = navigation != null ? navigation.CurrentSectorId : -1;
                int visualIndication = navigation != null ? navigation.SectorVisualIndication : -1;

                bool creditsReadable = credits >= 0;
                bool pricesReadable = fuelPrice > 0 && coolantPrice > 0;
                bool navReadable = navigation != null && sectorId >= 0 && visualIndication >= 0;

                // ---- credits rules (delta + reserve band) -----------------------
                if (creditsReadable)
                {
                    EnsureTracked(TrackIdPrefix + TrackCredits, TrackCredits, nowMs, pending, ref reports);
                    EvaluateCredits(credits, nowMs, pending, ref reports);
                }
                else
                {
                    S.UnknownInputPasses++;
                }

                // ---- affordability rules ----------------------------------------
                if (creditsReadable && pricesReadable)
                {
                    EvaluateAffordability(credits, fuel, coolant, fuelPrice, coolantPrice, pending, ref reports);
                }
                else if (creditsReadable)
                {
                    // Prices unknown (capture failure): the affordability rules
                    // stay silent — data-only director, the emergency director
                    // owns low-supply SEVERITY.
                    S.UnknownInputPasses++;
                }

                // ---- store-sector rule ------------------------------------------
                if (creditsReadable && navReadable)
                {
                    EvaluateStoreSector(visualIndication, sectorId, nowMs, pending, ref reports);
                }
                else if (creditsReadable)
                {
                    S.UnknownInputPasses++;
                }

                // ---- warp-toll rule ----------------------------------------------
                if (creditsReadable)
                {
                    EvaluateWarpTolls(snapshot, credits, nowMs, pending, ref reports);
                }

                // ---- hygiene: expire the credits record when credits go unreadable
                // for ActiveExpiryMs (one-shot vanished report, P15 mirror). A
                // LIVE readable stream never expires (record refreshes every pass).
                if (!creditsReadable)
                {
                    List<string> expired = new List<string>(MaxActiveEconomyRecords);
                    foreach (KeyValuePair<string, EconomyRecord> kv in S.Active)
                    {
                        if (unchecked(nowMs - kv.Value.LastSeenMs) >= ActiveExpiryMs) expired.Add(kv.Key);
                    }
                    for (int i = 0; i < expired.Count; i++)
                    {
                        EconomyRecord expiredRec = S.Active[expired[i]];
                        S.Active.Remove(expired[i]);
                        S.HistoryIds.Enqueue(expired[i]);
                        while (S.HistoryIds.Count > MaxHistory) S.HistoryIds.Dequeue();
                        S.PlansExpired++;
                        S.VanishedReports++;
                        reports++;
                        pending.Add("EconomyVanished " + expiredRec.TrackId + " (inputs unreadable)");
                    }
                }

                S.Evaluations++;
            }

            for (int i = 0; i < pending.Count; i++) Emit(pending[i]);
            return reports;
        }

        // ---- rules -------------------------------------------------------------------

        private static void EnsureTracked(
            string trackId, string kind, int nowMs, List<string> pending, ref int reports)
        {
            EconomyRecord rec;
            if (S.Active.TryGetValue(trackId, out rec))
            {
                rec.UpdateCount++;
                rec.LastSeenMs = nowMs;
                return;
            }

            if (S.Active.Count >= MaxActiveEconomyRecords)
            {
                // Bounded tracked set: shed the oldest by LastSeenMs
                // (tie -> lowest key order). Bounded scan (<= 8).
                string oldest = null;
                foreach (KeyValuePair<string, EconomyRecord> okv in S.Active)
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
                    pending.Add("EconomyShed " + oldest + " (tracked set full)");
                }
            }

            rec = new EconomyRecord(trackId, kind, nowMs);
            S.Active[trackId] = rec;
            S.RecordsTracked++;
            rec.OpenedReported = true;
            S.OpenedReports++;
            reports++;
            pending.Add("EconomyOpened " + trackId);
        }

        // Credits edges: low-band episode + large-delta observation.
        private static void EvaluateCredits(int credits, int nowMs, List<string> pending, ref int reports)
        {
            // Delta observation (direction + magnitude only; the snapshot
            // cannot attribute WHY credits moved — no cause inference).
            if (S.LastCredits >= 0)
            {
                long delta = unchecked((long)credits - S.LastCredits);
                if (delta > MaxDeltaReportAbs || delta < -MaxDeltaReportAbs)
                {
                    S.DeltaReports++;
                    reports++;
                    pending.Add("CreditsDeltaReport delta="
                        + delta.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " credits=" + credits.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (delta != 0)
                {
                    S.DuplicatesSuppressed++;
                }
            }
            S.LastCredits = credits;

            // Reserve-band low episode: report the crossing INTO the band once;
            // recovery (credits back above the band) re-arms the edge.
            if (credits <= ReserveFloor)
            {
                if (!S.LowEpisodeOpen)
                {
                    S.LowEpisodeOpen = true;
                    S.LowReports++;
                    reports++;
                    pending.Add("CreditsLowReport credits="
                        + credits.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " reserve=" + ReserveFloor.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                S.LowEpisodeOpen = false;
            }
        }

        // Shop-sector presence: classifier seam (production wires the verified
        // ESectorVisualIndication shop-class list, Patch.cs:169-171), dwell-gated,
        // one report per sector episode. Null/faulting classifier => never a
        // shop sector (deny-by-default).
        private static void EvaluateStoreSector(
            int visualIndication, int sectorId, int nowMs, List<string> pending, ref int reports)
        {
            bool isShopSector;
            lock (m_Lock)
            {
                Func<int, bool> classifier = m_ShopSectorClassifier;
                if (classifier == null) { isShopSector = false; }
                else
                {
                    try { isShopSector = classifier(visualIndication); }
                    catch (Exception) { isShopSector = false; }
                }
            }

            if (!isShopSector)
            {
                if (S.StoreSectorId >= 0)
                {
                    S.StoreSectorId = -1;
                    S.StoreFirstSeenMs = -1;
                    S.StoreReported = false;
                }
                return;
            }

            if (S.StoreSectorId != sectorId)
            {
                // New store episode (sector change or first entry).
                S.StoreSectorId = sectorId;
                S.StoreFirstSeenMs = nowMs;
                S.StoreReported = false;
                return;
            }

            if (!S.StoreReported
                && S.StoreFirstSeenMs >= 0
                && unchecked(nowMs - S.StoreFirstSeenMs) >= StoreDwellMs)
            {
                S.StoreReported = true;
                S.StoreReports++;
                reports++;
                pending.Add("StoreSectorReport sector=" + sectorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // Fuel/coolant affordability: supply low (P9-warning-mirroring data
        // thresholds) AND unit price readable AND credits cannot buy even one
        // unit. One report per episode, re-armed when the condition clears
        // (supply recovers OR credits become sufficient). Deterministic
        // data-only reporting — legacy HandleShop keeps exclusive ownership of
        // all purchase actions.
        private static void EvaluateAffordability(
            int credits, int fuel, float coolant, int fuelPrice, int coolantPrice,
            List<string> pending, ref int reports)
        {
            bool fuelUnaffordable = fuel >= 0 && fuel <= FuelLowCapsules && credits < fuelPrice;
            bool coolantUnaffordable = !float.IsNaN(coolant)
                && coolant <= CoolantLowPercent
                && credits < coolantPrice;

            if (fuelUnaffordable)
            {
                if (!S.FuelAffordEpisodeOpen)
                {
                    S.FuelAffordEpisodeOpen = true;
                    S.FuelAffordabilityReports++;
                    reports++;
                    pending.Add("FuelAffordabilityReport fuel=" + fuel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " price=" + fuelPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " credits=" + credits.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                S.FuelAffordEpisodeOpen = false;
            }

            if (coolantUnaffordable)
            {
                if (!S.CoolantAffordEpisodeOpen)
                {
                    S.CoolantAffordEpisodeOpen = true;
                    S.CoolantAffordabilityReports++;
                    reports++;
                    pending.Add("CoolantAffordabilityReport price="
                        + coolantPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " credits=" + credits.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                S.CoolantAffordEpisodeOpen = false;
            }
        }

        // Warp-toll observation: any snapshot WARP_STATION whose Price exceeds
        // readable credits. Dwell-gated (the flight-AI cache list is not
        // order-stable), one report per toll episode; Price <= 0 sentinel-safe.
        private static void EvaluateWarpTolls(
            WorldSnapshot snapshot, int credits, int nowMs, List<string> pending, ref int reports)
        {
            int bestPrice = -1;
            for (int i = 0; i < snapshot.WorldObjects.Count; i++)
            {
                WorldObjectSnapshot o = snapshot.WorldObjects[i];
                if (o == null) continue;
                if (o.Kind != "WARP_STATION" || o.Price <= 0) continue;
                if (o.Price > credits && (bestPrice < 0 || o.Price < bestPrice)) bestPrice = o.Price;
            }

            if (bestPrice < 0)
            {
                if (S.WarpTollPrice >= 0)
                {
                    S.WarpTollPrice = -1;
                    S.WarpTollFirstSeenMs = -1;
                    S.WarpTollReported = false;
                }
                return;
            }

            if (bestPrice != S.WarpTollPrice)
            {
                S.WarpTollPrice = bestPrice;
                S.WarpTollFirstSeenMs = nowMs;
                S.WarpTollReported = false;
                return;
            }

            if (!S.WarpTollReported
                && S.WarpTollFirstSeenMs >= 0
                && unchecked(nowMs - S.WarpTollFirstSeenMs) >= WarpTollDwellMs)
            {
                S.WarpTollReported = true;
                S.WarpTollReports++;
                reports++;
                pending.Add("WarpTollReport price=" + bestPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " credits=" + credits.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        private static void MarkUncertain(string reason)
        {
            lock (m_Lock) S.LastUncertainReason = reason;
            Emit("EconomyUncertain " + reason);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Active.Clear();
                S.HistoryIds.Clear();
                S.LastEvalMs = -1;
                S.LastCredits = -1;
                S.LowEpisodeOpen = false;
                S.StoreSectorId = -1;
                S.StoreFirstSeenMs = -1;
                S.StoreReported = false;
                S.WarpTollPrice = -1;
                S.WarpTollFirstSeenMs = -1;
                S.WarpTollReported = false;
                S.FuelAffordEpisodeOpen = false;
                S.CoolantAffordEpisodeOpen = false;
                S.Evaluations = 0;
                S.RecordsTracked = 0;
                S.OpenedReports = 0;
                S.LowReports = 0;
                S.DeltaReports = 0;
                S.StoreReports = 0;
                S.FuelAffordabilityReports = 0;
                S.CoolantAffordabilityReports = 0;
                S.WarpTollReports = 0;
                S.VanishedReports = 0;
                S.DuplicatesSuppressed = 0;
                S.PlansExpired = 0;
                S.StaleRejections = 0;
                S.UnknownInputPasses = 0;
                S.LastUncertainReason = null;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_ShopSectorClassifier = null;
                m_OnDecision = null;
            }
        }
    }
}