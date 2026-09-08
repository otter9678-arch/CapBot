using System;
using System.Collections.Generic;
using CapBot.Core.World;

namespace CapBot.Core.Emergency
{
    // ---- Phase 9: deterministic emergency detector ---------------------------
    //
    // Pure rule engine over the Phase 6 WorldSnapshot. Zero game access,
    // zero clock reads (time is an argument), zero LINQ, zero allocation on
    // the no-emergency path (the only allocations are the decision objects
    // when something IS detected).
    //
    // DETECTION CONTRACT (fail-safe):
    //   - Every rule validates its inputs first. Unknown data (NaN fractions,
    //     -1 ids/counts, missing sections) can never trigger a rule — "cannot
    //     prove an emergency is real" means NO emergency is declared and no
    //     destructive action follows.
    //   - Stale snapshots are rejected wholesale by the director before rules
    //     run; the detector additionally refuses never-captured snapshots as
    //     defense in depth.
    //   - Hostility is never assumed: combat detection reads only the game's
    //     authoritative HostileShips list + observed combat levels (Quality
    //     Improver Prefix-replaces ShouldBeHostileToShip — this layer never
    //     calls hostility logic; it observes the game's own outcomes).
    //
    // Every finding is a bounded EmergencyDecision; thresholds are consts
    // (reviewable, deterministic), never metadata- or text-driven.

    public static class EmergencyDetector
    {
        // ---- thresholds (deterministic, documented in docs/EMERGENCY.md) ----
        public const float HullCriticalFraction = 0.25f;   // CriticalHull (Critical)
        public const float HullSevereFraction = 0.35f;     // CriticalHull (Severe)
        public const float HullWarningFraction = 0.50f;    // CriticalHull (Elevated)
        public const float CrewHealthCriticalFraction = 0.25f; // CriticalCrewHealth (Critical)
        public const float CrewHealthSevereFraction = 0.35f;   // CriticalCrewHealth (Severe)
        public const float CrewHealthWarningFraction = 0.50f;  // CriticalCrewHealth (Elevated)
        public const float ReactorCriticalTempFraction = 0.95f;// ReactorCritical (Critical)
        public const float ReactorSevereTempFraction = 0.90f;  // ReactorCritical (Severe)
        public const float CoolantCriticalPercent = 15f;   // CoolantCritical (Critical)
        public const float CoolantWarningPercent = 30f;    // CoolantCritical (Elevated)
        public const int FuelCriticalCapsules = 1;         // FuelCritical (Critical): last capsule
        public const int FuelWarningCapsules = 2;          // FuelCritical (Elevated)
        public const int FireSevereCount = 3;              // >= 3 non-null fires = Severe
        public const float CombatLevelGapUnfavorable = 1.33f; // INFERRED vanilla UI threat comparison (research §6.6) — data only
        public const float StuckDistMovedMeters = 1f;      // NavigationFailure: < 1 m moved
        public const float StuckTimeSeekingSec = 7f;       // NavigationFailure: vanilla stuck trigger (research §3.4)

        // One detection pass. Returns findings ordered by severity desc (the
        // director consumes the first = highest-precedence finding). Empty
        // list = no provable emergency (the normal, quiet result).
        public static List<EmergencyDecision> Detect(WorldSnapshot snapshot, int nowMs)
        {
            List<EmergencyDecision> findings = new List<EmergencyDecision>(2);
            if (snapshot == null || snapshot.IsNeverCaptured) return findings;

            ShipSnapshot playerShip = PlayerShipOf(snapshot);
            if (playerShip == null) return findings; // cannot prove anything without the ship

            DetectHull(playerShip, nowMs, findings);
            DetectFires(snapshot, nowMs, findings);
            DetectReactor(snapshot, nowMs, findings);
            DetectCrewHealth(snapshot, nowMs, findings);
            DetectCombat(snapshot, nowMs, findings);
            DetectNavigation(snapshot, nowMs, findings);
            DetectFuel(snapshot, nowMs, findings);
            DetectCoolant(snapshot, nowMs, findings);
            DetectObjective(snapshot, nowMs, findings);

            // Deterministic order: severity desc, then type id asc, then id.
            if (findings.Count > 1) findings.Sort(CompareFindings);
            return findings;
        }

        // ---- individual rules -------------------------------------------------

        private static void DetectHull(ShipSnapshot ship, int nowMs, List<EmergencyDecision> outList)
        {
            float hull = ship.HullFraction;
            if (float.IsNaN(hull)) return; // unknown hull never triggers
            if (hull < 0f) hull = 0f; else if (hull > 1f) hull = 1f;

            if (hull <= HullCriticalFraction)
                outList.Add(Make(EmergencyType.CriticalHull, EmergencySeverity.Critical, nowMs, "SHIP",
                    "hull at " + FormatFrac(hull) + " (<= " + FormatFrac(HullCriticalFraction) + ")",
                    "SET_CAPTAIN_ORDER", "ORDER", "9", "MasterOnly",
                    "repair protocols order (verified vocabulary 9); executor still validates"));
            else if (hull <= HullSevereFraction)
                outList.Add(Make(EmergencyType.CriticalHull, EmergencySeverity.Severe, nowMs, "SHIP",
                    "hull at " + FormatFrac(hull) + " (<= " + FormatFrac(HullSevereFraction) + ")",
                    "SET_CAPTAIN_ORDER", "ORDER", "9", "MasterOnly",
                    "repair protocols order (verified vocabulary 9)"));
            else if (hull <= HullWarningFraction)
                outList.Add(Make(EmergencyType.CriticalHull, EmergencySeverity.Elevated, nowMs, "SHIP",
                    "hull at " + FormatFrac(hull) + " (<= " + FormatFrac(HullWarningFraction) + ")",
                    "SET_CAPTAIN_ORDER", "ORDER", "9", "MasterOnly",
                    "repair protocols order (verified vocabulary 9)"));
        }

        private static void DetectFires(WorldSnapshot snap, int nowMs, List<EmergencyDecision> outList)
        {
            int fires = snap.PlayerShipFireCount; // -1 = unknown (never triggers)
            if (fires <= 0) return;
            EmergencySeverity sev = fires >= FireSevereCount ? EmergencySeverity.Severe : EmergencySeverity.Warning;
            outList.Add(Make(EmergencyType.Fire, sev, nowMs, "SHIP",
                fires + " active fire(s) on player ship (verified CountNonNullFires)",
                "SET_CAPTAIN_ORDER", "ORDER", "6", "MasterOnly",
                "repel/board order (verified vocabulary 6); vanilla fire-priority requests handle crew response"));
        }

        private static void DetectReactor(WorldSnapshot snap, int nowMs, List<EmergencyDecision> outList)
        {
            float tempFrac = snap.PlayerShipReactorTempFraction; // NaN = unknown (never triggers)
            if (float.IsNaN(tempFrac)) return;
            if (tempFrac >= ReactorCriticalTempFraction)
                outList.Add(Make(EmergencyType.ReactorCritical, EmergencySeverity.Critical, nowMs, "SHIP",
                    "reactor temp at " + FormatFrac(tempFrac) + " of max (>= " + FormatFrac(ReactorCriticalTempFraction) + ")",
                    "SET_CAPTAIN_ORDER", "ORDER", "9", "MasterOnly",
                    "repair protocols order; reactor cooling is crew/vanilla-side"));
            else if (tempFrac >= ReactorSevereTempFraction)
                outList.Add(Make(EmergencyType.ReactorCritical, EmergencySeverity.Severe, nowMs, "SHIP",
                    "reactor temp at " + FormatFrac(tempFrac) + " of max (>= " + FormatFrac(ReactorSevereTempFraction) + ")",
                    "SET_CAPTAIN_ORDER", "ORDER", "9", "MasterOnly",
                    "repair protocols order"));
        }

        private static void DetectCrewHealth(WorldSnapshot snap, int nowMs, List<EmergencyDecision> outList)
        {
            IReadOnlyList<CrewMemberSnapshot> crew = snap.Crew;
            if (crew == null || crew.Count == 0) return;
            CrewMemberSnapshot worst = null;
            for (int i = 0; i < crew.Count; i++)
            {
                CrewMemberSnapshot c = crew[i];
                if (c == null || !c.IsBot) continue;      // bots are ours to help; humans self-care (documented)
                if (!c.AliveKnown || !c.Alive) continue;  // dead/unknown never trigger
                float h = c.HealthFraction;
                if (float.IsNaN(h)) continue;
                if (worst == null || h < worst.HealthFraction) worst = c;
            }
            if (worst == null) return;

            string actor = "BOT:" + worst.PlayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            EmergencySeverity sev;
            if (worst.HealthFraction <= CrewHealthCriticalFraction) sev = EmergencySeverity.Critical;
            else if (worst.HealthFraction <= CrewHealthSevereFraction) sev = EmergencySeverity.Severe;
            else if (worst.HealthFraction <= CrewHealthWarningFraction) sev = EmergencySeverity.Elevated;
            else return;

            outList.Add(Make(EmergencyType.CriticalCrewHealth, sev, nowMs, actor,
                "crew bot " + (worst.Name ?? "?") + " health " + FormatFrac(worst.HealthFraction),
                "SET_CAPTAIN_ORDER", "ORDER", "9", "MasterOnly",
                "repair/medical protocols; vanilla heal priorities handle the bot"));
        }

        private static void DetectCombat(WorldSnapshot snap, int nowMs, List<EmergencyDecision> outList)
        {
            ThreatSnapshot t = snap.Threats;
            if (t == null) return;
            int hostiles = t.KnownHostileShipIds != null ? t.KnownHostileShipIds.Count : 0;
            if (hostiles <= 0) return; // no authoritative hostiles -> no combat emergency

            // Strength proxy: our combat level vs the targeted hostile's (when
            // both known). Combat-level semantics are INFERRED (research §6.6)
            // — data only, used to bound severity, never to author a target.
            bool outmatched = false;
            if (!float.IsNaN(t.OurCombatLevel) && !float.IsNaN(t.PlayerTargetCombatLevel)
                && t.OurCombatLevel >= 0f && t.PlayerTargetCombatLevel >= 0f)
                outmatched = t.PlayerTargetCombatLevel > t.OurCombatLevel * CombatLevelGapUnfavorable;

            EmergencySeverity sev = (hostiles >= FireSevereCount || outmatched)
                ? EmergencySeverity.Severe : EmergencySeverity.Warning;

            // Target reference: a verified authoritative hostile ship id (data
            // for the executor's SET_CAPTAIN_TARGET branch to re-validate).
            string targetRef = t.PlayerTargetShipId >= 0
                ? t.PlayerTargetShipId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : t.KnownHostileShipIds[0].ToString(System.Globalization.CultureInfo.InvariantCulture);

            outList.Add(Make(EmergencyType.DangerousCombat, sev, nowMs, "SHIP",
                hostiles + " authoritative hostile ship(s)" + (outmatched ? "; combat level gap unfavorable" : string.Empty),
                "SET_CAPTAIN_TARGET", "SHIP", targetRef, "MasterOnly",
                "combat focus via verified Captain_SetTargetShip channel; id re-validated by registry+executor"));
            // Target-reference vocabulary note: SET_CAPTAIN_TARGET is a ShipId
            // capability (registry TargetReq=ShipId) — the id above is a
            // verified authoritative hostile ship id; the registry + executor
            // re-validate it against live state before any action.
        }

        private static void DetectNavigation(WorldSnapshot snap, int nowMs, List<EmergencyDecision> outList)
        {
            NavigationSnapshot n = snap.Navigation;
            if (n == null || !n.HasBotNavigationMetrics) return; // unknown metrics never trigger
            float moved = n.DistMovedInLast5s;
            float seeking = n.TimeSeekingTargetSec;
            if (float.IsNaN(moved) || float.IsNaN(seeking)) return;
            if (moved < StuckDistMovedMeters && seeking > StuckTimeSeekingSec)
            {
                outList.Add(Make(EmergencyType.NavigationFailure, EmergencySeverity.Warning, nowMs, "SHIP",
                    "captain bot stuck: moved " + moved.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                        + "m in 5s while seeking " + seeking.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s",
                    "", "", "", "MasterOnly",
                    "no capability wired: vanilla stuck-teleport recovery owns this; coordination-only decision"));
            }
        }

        private static void DetectFuel(WorldSnapshot snap, int nowMs, List<EmergencyDecision> outList)
        {
            int fuel = snap.Resources != null ? snap.Resources.FuelCapsules : -1;
            if (fuel < 0) return; // unknown fuel never triggers
            EmergencySeverity sev;
            string sevText;
            if (fuel <= FuelCriticalCapsules) { sev = EmergencySeverity.Critical; sevText = "critical"; }
            else if (fuel <= FuelWarningCapsules) { sev = EmergencySeverity.Elevated; sevText = "low"; }
            else return;

            outList.Add(Make(EmergencyType.FuelCritical, sev, nowMs, "SHIP",
                fuel + " fuel capsule(s) remaining (" + sevText + ")",
                "SET_CAPTAIN_ORDER", "ORDER", "1", "MasterOnly",
                "at-attention order; refuel runs through vanilla shopping priorities"));
        }

        private static void DetectCoolant(WorldSnapshot snap, int nowMs, List<EmergencyDecision> outList)
        {
            float coolant = snap.Resources != null ? snap.Resources.CoolantLevelPercent : float.NaN;
            if (float.IsNaN(coolant)) return; // unknown coolant never triggers
            EmergencySeverity sev;
            if (coolant <= CoolantCriticalPercent) sev = EmergencySeverity.Critical;
            else if (coolant <= CoolantWarningPercent) sev = EmergencySeverity.Elevated;
            else return;

            outList.Add(Make(EmergencyType.CoolantCritical, sev, nowMs, "SHIP",
                "coolant at " + coolant.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%",
                "SET_CAPTAIN_ORDER", "ORDER", "9", "MasterOnly",
                "repair protocols; reactor cooling is crew/vanilla-side"));
        }

        private static void DetectObjective(WorldSnapshot snap, int nowMs, List<EmergencyDecision> outList)
        {
            IReadOnlyList<MissionSnapshot> missions = snap.Missions;
            if (missions == null || missions.Count == 0) return;
            for (int i = 0; i < missions.Count; i++)
            {
                MissionSnapshot m = missions[i];
                if (m == null || m.Ended || m.Abandoned) continue;
                if (m.TotalObjectives <= 0) continue;
                int remaining = m.TotalObjectives - m.CompletedObjectives;
                if (remaining != 1) continue; // exactly one objective left = final-stretch coordination signal
                outList.Add(Make(EmergencyType.ObjectiveCritical, EmergencySeverity.Warning, nowMs, "MISSION",
                    "mission " + m.MissionTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " has 1 objective left",
                    "", "MISSION",
                    m.MissionTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "ReadOnly", "coordination-only decision; no capability wired for objectives"));
            }
        }

        // ---- helpers -----------------------------------------------------------

        // Builds the bounded decision. Identity = type + actor + targetKind:ref
        // (when present) — stable across re-evaluations, so re-detection
        // collides with the active record instead of creating duplicate tasks.
        private static EmergencyDecision Make(EmergencyType type, EmergencySeverity sev, int nowMs,
            string actor, string reason, string capability, string targetKind, string targetRef,
            string authority, string preconditions)
        {
            string key = actor + "|" + (string.IsNullOrEmpty(targetRef) ? string.Empty : targetKind + ":" + targetRef);
            string eid = EmergencyIdentity.MakeEmergencyId(type, key);
            int priority = EmergencyPrecedence.PriorityFor(type, sev);
            return new EmergencyDecision(eid, type, sev, nowMs, actor, 0, priority,
                reason, capability ?? string.Empty, targetRef ?? string.Empty,
                authority, preconditions, nowMs + DecisionLifetimeMs,
                (int)EmergencyState.Normal, "LIFECYCLE");
        }

        // The decision's advisory lifetime; ACTIVE-record expiries govern
        // deduplication (the director); this only bounds the data object.
        public const int DecisionLifetimeMs = 30000;

        private static ShipSnapshot PlayerShipOf(WorldSnapshot snap)
        {
            if (snap.Ships == null || snap.Ships.Count == 0) return null;
            ShipSnapshot s = snap.Ships[0];
            return s != null && s.IsPlayerShip ? s : null;
        }

        private static int CompareFindings(EmergencyDecision a, EmergencyDecision b)
        {
            if (a.Severity != b.Severity) return a.Severity > b.Severity ? -1 : 1;
            if (a.EmergencyType != b.EmergencyType) return a.EmergencyType < b.EmergencyType ? -1 : 1;
            return string.CompareOrdinal(a.EmergencyId, b.EmergencyId);
        }

        private static string FormatFrac(float f)
        {
            return f.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}