using System;
using CapBot.Core.Tasks;
using CapBot.Core.World;

namespace CapBot.Core.Capabilities
{
    // ---- Phase 7: initial capability catalog ---------------------------------
    //
    // Registers the ONLY capabilities this phase ships: the four verified
    // vanilla captain command channels plus their course-goal variants, and
    // one read-style capability. Everything here is a CONTRACT:
    //
    //   - no gameplay is performed by registration or validation,
    //   - the VerifiedApi field documents the exact RPC the future executor
    //     will call (all PunRPC-verified against Assembly-CSharp),
    //   - authority is MasterOnly (default for authoritative gameplay),
    //   - cooldowns guard vanilla cadence (research §11: decisions 1–1.5 s),
    //   - ownership restricts captain-channel commands to the CAPTAIN owner
    //     (the captain-bot task owner vocabulary from Phase 2),
    //   - richer live preconditions (ship exists, sector in warp range, …)
    //     are deliberately NOT invented here — they require runtime checks
    //     that belong to the executor phase against verified APIs.
    //
    // IssueMoveOrder carries a Vector3 in vanilla; a bounded safe contract
    // cannot validate raw coordinates from untrusted task data, so its
    // target requirement is None and the executor must derive the location
    // from verified task/world data at execution time (P8 policy).
    internal static class RegisteredCapabilities
    {
        public const string SetCaptainOrder = "SET_CAPTAIN_ORDER";
        public const string IssueMoveOrder = "ISSUE_MOVE_ORDER";
        public const string SetCaptainTarget = "SET_CAPTAIN_TARGET";
        public const string AddCourseGoal = "ADD_COURSE_GOAL";
        public const string RemoveCourseGoal = "REMOVE_COURSE_GOAL";
        public const string ClearCourseGoals = "CLEAR_COURSE_GOALS";
        public const string ReadWorldSnapshot = "READ_WORLD_SNAPSHOT";

        // Number of built-in capabilities (registration completeness check).
        public const int BuiltInCount = 7;

        // Registers the built-in catalog exactly once; safe to call again
        // (duplicates are rejected by the registry). Returns the number of
        // NEW registrations performed by this call.
        public static int RegisterBuiltIns()
        {
            int before = CapabilityRegistry.Count;

            CapabilityRegistry.Register(new CapabilityDescriptor(
                SetCaptainOrder,
                "Set captain crew order",
                "Crew order channel: sets PLServer.CaptainsOrdersID via the CaptainSetOrderID RPC. Bots react through vanilla E_CAPTAINS_ORDER priority overrides. Order value is validated by the executor against the static order vocabulary.",
                new string[] { "CAPTAIN" },
                CapabilityAuthority.MasterOnly,
                CapabilityDanger.Reversible, CapabilityReversibility.Reversible,
                null, TargetRequirement.BoundedToken, new string[] { "ORDER" },
                2000, false, null, null,
                "PLServer.CaptainSetOrderID(Int32) [PunRPC, VERIFIED]",
                "CAPABILITY"));

            CapabilityRegistry.Register(new CapabilityDescriptor(
                IssueMoveOrder,
                "Issue captain movement order",
                "Movement-order channel: PLPlayer.IssueMoveOrder sets a crew movement order honored by PLBotController for 20s. The Vector3 location is derived by the executor from verified task/world data — never parsed from untrusted text.",
                new string[] { "CAPTAIN" },
                CapabilityAuthority.MasterOnly,
                CapabilityDanger.Reversible, CapabilityReversibility.Reversible,
                null, TargetRequirement.None, null,
                1000, false, null, null,
                "PLPlayer.IssueMoveOrder(Vector3) [PunRPC, VERIFIED]",
                "CAPABILITY"));

            CapabilityRegistry.Register(new CapabilityDescriptor(
                SetCaptainTarget,
                "Set captain ship target",
                "Combat-focus channel: Captain_SetTargetShip sets CaptainTargetedSpaceTargetID (synced). Ship targets validate as integers; hostility semantics NOT assumed — live hostile checks are the executor's job.",
                new string[] { "CAPTAIN" },
                CapabilityAuthority.MasterOnly,
                CapabilityDanger.Reversible, CapabilityReversibility.Reversible,
                null, TargetRequirement.ShipId, new string[] { "SHIP" },
                1000, false, null, null,
                "PLShipInfoBase.Captain_SetTargetShip(Int32) [PunRPC, VERIFIED]",
                "CAPABILITY"));

            CapabilityRegistry.Register(new CapabilityDescriptor(
                AddCourseGoal,
                "Add ship course goal",
                "Navigation channel: PLServer.AddCourseGoal appends a sector id to m_ShipCourseGoals (PhotonTargets.All); PLStarmap rebuilds CurrentShipPath. Sector ids validated as integers.",
                new string[] { "CAPTAIN" },
                CapabilityAuthority.MasterOnly,
                CapabilityDanger.Reversible, CapabilityReversibility.Reversible,
                null, TargetRequirement.SectorId, new string[] { "SECTOR" },
                1000, false, null, null,
                "PLServer.AddCourseGoal(Int32) [PunRPC, VERIFIED]",
                "CAPABILITY"));

            CapabilityRegistry.Register(new CapabilityDescriptor(
                RemoveCourseGoal,
                "Remove ship course goal",
                "Navigation channel: PLServer.RemoveCourseGoal removes a sector id from m_ShipCourseGoals (PhotonTargets.All). Sector ids validated as integers.",
                new string[] { "CAPTAIN" },
                CapabilityAuthority.MasterOnly,
                CapabilityDanger.Reversible, CapabilityReversibility.Reversible,
                null, TargetRequirement.SectorId, new string[] { "SECTOR" },
                1000, false, null, null,
                "PLServer.RemoveCourseGoal(Int32) [PunRPC, VERIFIED]",
                "CAPABILITY"));

            CapabilityRegistry.Register(new CapabilityDescriptor(
                ClearCourseGoals,
                "Clear ship course goals",
                "Navigation channel: PLServer.ClearCourseGoals empties m_ShipCourseGoals (PhotonTargets.All). Wipes the whole course — reversible only by re-adding goals, hence a higher cooldown and explicit classification.",
                new string[] { "CAPTAIN" },
                CapabilityAuthority.MasterOnly,
                CapabilityDanger.Reversible, CapabilityReversibility.Reversible,
                null, TargetRequirement.None, null,
                5000, false, null, null,
                "PLServer.ClearCourseGoals() [PunRPC, VERIFIED]",
                "CAPABILITY"));

            CapabilityRegistry.Register(new CapabilityDescriptor(
                ReadWorldSnapshot,
                "Read world state snapshot",
                "Read-only observation via the Phase 6 world layer: returns the latest bounded WorldSnapshot view. No gameplay effect, no authority requirement, no cooldown. Executors/directors consume snapshots through this contract rather than scanning the scene.",
                null,
                CapabilityAuthority.ReadOnly,
                CapabilityDanger.Benign, CapabilityReversibility.NotApplicable,
                null, TargetRequirement.None, null,
                0, false, null, null,
                "CapBot.Core.World.WorldStateService.Latest (Phase 6, VERIFIED)",
                "CAPABILITY"));

            return CapabilityRegistry.Count - before;
        }

        // Production seams: wire the registry to the live P5/P6/clock layers.
        // Authority stays fail-closed until P8 wires ExecutionClaims to
        // master-client state (the claims layer's own deny-by-default).
        public static void AttachProductionSeams()
        {
            CapabilityRegistry.SetAuthorityProbe(delegate { return ExecutionClaims.IsAuthoritative(); });
            CapabilityRegistry.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapabilityRegistry.SetWorldProvider(delegate { return WorldStateService.Latest; });
            CapabilityRegistry.SetClaimProbe(delegate (long taskId, string actionId)
            {
                // Conflict = an unexpired claim exists on this task, OR this
                // exact logical action already succeeded (duplicate execution).
                ClaimInfo claim = ExecutionClaims.GetClaim(taskId, TaskClock.NowMs);
                if (claim.Active) return true;
                return ExecutionClaims.Ledger.Observe(actionId) == ActionOutcome.Succeeded;
            });
        }
    }
}