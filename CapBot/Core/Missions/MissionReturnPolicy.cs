using System.Collections.Generic;
using System.Globalization;
using CapBot.Core.World;

namespace CapBot.Core.Missions
{
    // ---- P52: the ONE authoritative mission-return decision --------------------
    //
    // Port of the legacy Patch.cs MissionShouldReturnToSender table (type-id →
    // required-completed-objective-count) into the pure mission domain. The
    // mission pipeline has exactly ONE return decision (master prompt §9):
    // MissionDirector publishes the verdict as DATA (MissionReturnSignal), and
    // every consumer (MissionWorkDirector episode gating, /capbotmission,
    // future course planners) reads the policy or the snapshot — nothing
    // re-implements the table.
    //
    // Semantics (verbatim from the shipped legacy logic, Patch.cs:2731):
    //   - each mission TYPE completes at a specific objective count; once that
    //     count is reached, the crew must RETURN TO SENDER (deliver/turn-in)
    //     before the mission is finished;
    //   - the legacy table keys on MissionTypeID with hardcoded objective
    //     positions; missions of an unlisted type NEVER trigger a return
    //     (return false);
    //   - legacy indexed Objectives[0..2] unguarded (IndexOutOfRange for
    //     missions with fewer objectives — the port uses the bounded
    //     snapshot's completed count instead, which is equivalent for
    //     well-formed missions and fail-safe for malformed ones);
    //   - the legacy CONSUMER (HasActiveMissionInCurrentSector) treated a
    //     return requirement as "stop treating the sector mission as active
    //     work" — the ship should head back rather than keep planet-side.
    //
    // P52 upgrade path (probe9-verified data): when the capture carries a
    // stable MissionId (>= 0), the return decision prefers the game's own
    // turn-in readiness flag (GameReadyTurnInKnown/GameReadyTurnIn —
    // PLServer.IsMissionWithIDReadyToTurnIn) over the legacy type table.
    // The table remains the fallback for unknown-id missions (parity with
    // the shipped behavior; nothing regresses when identity is missing).
    //
    // Pure C#: no game types, no clock reads, never throws.
    public static class MissionReturnPolicy
    {
        // Legacy table (Patch.cs:2731, verified verbatim). typeId -> the
        // number of completed objectives that satisfies the return.
        private static readonly int[] TypeZero = new int[] { 0 };
        private static readonly int[] TwoObjectiveTypes = new int[]
        {
            25, 68, 71, 72, 780, 2437, 2580, 104851,
        };
        private static readonly int[] OneObjectiveTypes = new int[]
        {
            69, 264, 683, 81262, 24213, 24214, 25249,
        };

        // True when the mission has reached its return point (all work
        // objectives complete per the type table / turn-in flag).
        //
        // Legacy parity EXACT: a listed type returns when completed >= its
        // required count (the shipped behavior — including return-at-N for
        // multi-objective types). The P52 additive upgrade covers only
        // UNLISTED types: they return when the game itself confirms turn-in
        // readiness (GameReadyTurnIn — probe8-verified
        // PLServer.IsMissionWithIDReadyToTurnIn) AND all objectives are
        // complete. The verified game flag is never allowed to WEAKEN the
        // legacy table (unknown flag semantics must not regress the shipped
        // return flow; §6's inspect-authoritative-state mandate is served by
        // the additive unlisted-type path).
        public static bool ShouldReturnToSender(MissionSnapshot mission)
        {
            if (mission == null) return false;

            int typeId = mission.MissionTypeId;
            int required = RequiredCompletedCountForType(typeId);
            if (required >= 0)
            {
                return mission.TotalObjectives > 0
                    && mission.CompletedObjectives >= required;
            }

            // Unlisted type: the legacy table never triggered a return. The
            // P52 additive path returns when the game confirms turn-in
            // readiness and every objective is complete (unknown sentinels —
            // GameReadyTurnInKnown=false — never trigger).
            return mission.GameReadyTurnInKnown
                && mission.GameReadyTurnIn
                && mission.TotalObjectives > 0
                && mission.CompletedObjectives >= mission.TotalObjectives;
        }

        // Required completed-objective count for a mission type (-1 = the
        // type is unlisted: no return trigger exists). Exposed for
        // diagnostics; the authoritative path is ShouldReturnToSender.
        public static int RequiredCompletedCountForType(int missionTypeId)
        {
            if (missionTypeId < 0) return -1;
            foreach (int t in TypeZero) if (t == missionTypeId) return 3;
            foreach (int t in TwoObjectiveTypes) if (t == missionTypeId) return 2;
            foreach (int t in OneObjectiveTypes) if (t == missionTypeId) return 1;
            return -1;
        }

        // ---- return-signal records (data for consumers) --------------------

        // One bounded per-pass signal record. Static-vocabulary strings only;
        // missionId -1 = identity unknown.
        public sealed class MissionReturnSignal
        {
            public const string KindReturnRequired = "MISSION_RETURN_REQUIRED";
            public const string KindReturnComplete = "MISSION_RETURN_COMPLETE";

            public readonly string Kind;      // static vocabulary above
            public readonly int MissionTypeId;
            public readonly int MissionId;    // -1 = unknown
            public readonly int CompletedObjectives;
            public readonly int TotalObjectives;
            public readonly int TimeMs;

            public MissionReturnSignal(string kind, int missionTypeId, int missionId,
                int completedObjectives, int totalObjectives, int timeMs)
            {
                Kind = kind;
                MissionTypeId = missionTypeId;
                MissionId = missionId;
                CompletedObjectives = completedObjectives;
                TotalObjectives = totalObjectives;
                TimeMs = timeMs;
            }

            public override string ToString()
            {
                return Kind + " type=" + MissionTypeId.ToString(CultureInfo.InvariantCulture)
                    + " id=" + MissionId.ToString(CultureInfo.InvariantCulture)
                    + " done=" + CompletedObjectives.ToString(CultureInfo.InvariantCulture)
                    + "/" + TotalObjectives.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}