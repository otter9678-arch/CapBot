using System;
using System.Collections.Generic;
using CapBot.Core.Capabilities;
using CapBot.Core.Logging;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using UnityEngine;

namespace CapBot.Core.Executor
{
    // ---- Phase 8: game-facing capability dispatcher ---------------------------
    //
    // The ONLY implementation of ICapabilityDispatcher. Dispatches on
    // CapabilityId through STATIC, CODE-REVIEWED branches — there is no
    // reflection dispatch, no method-name lookup, no runtime compilation,
    // no interpretation of task metadata or target text as commands. A
    // capability id without a branch here is refused Rejected (fail-closed):
    // registering a capability in the P7 registry does NOT make it
    // executable until a reviewed branch exists below.
    //
    // EVERY branch calls a VERIFIED PULSAR API in the exact shipped call
    // shape (Patch.cs-compiled or DLL-reflectable). Verification table:
    //
    //   SET_CAPTAIN_ORDER  PLServer.CaptainSetOrderID(Int32) — public instance
    //                      method, direct call (Patch.cs:260/1950 shape).
    //                      DLL-verified this session: (Int32 inOrdersID).
    //   ISSUE_MOVE_ORDER   PLPlayer.IssueMoveOrder(Vector3) — [PunRPC],
    //                      DLL-verified (private impl; fields MovementOrderLoc
    //                      /TLI/Interior/Time). Invoked via the pawn's
    //                      photonView.RPC (compile-proven pattern Patch.cs:2646
    //                      pawn.photonView.RPC(...)). Vector3 location derived
    //                      ONLY from verified galaxy data
    //                      (PLSectorInfo.Position, compile-proven Patch.cs:2516).
    //                      Call mask PhotonTargets.All mirrors the shipped
    //                      course-goal RPCs — INFERRED mask on a VERIFIED RPC.
    //   SET_CAPTAIN_TARGET PLShipInfoBase.Captain_SetTargetShip(Int32) —
    //                      DLL-verified public virtual (Int32 inShipID);
    //                      direct master-side call; the target id syncs via
    //                      the ship's stream (research §131), so NO
    //                      PhotonTargets.All send and no duplicate risk.
    //   ADD/REMOVE/CLEAR   PLServer.Instance.photonView.RPC(name,
    //   _COURSE_GOALS      PhotonTargets.All, args) — exact shipped shapes
    //                      (Patch.cs:418/423/2604/2621/2629).
    //   READ_WORLD_SNAPSHOT Pure P6 read: WorldStateService.Latest.
    //
    // Multiplayer posture: every branch is master-side gameplay. The executor
    // + claims layer already guarantee this process is the authoritative host
    // before Dispatch runs; no branch sends a request-RPC (vanilla's
    // client->MasterClient pattern is untouched) and no branch sends BOTH a
    // local action and a PhotonTargets.All RPC for the same logical action
    // (research §260/§431 duplicate-action rule).
    internal sealed class PulsarCapabilityDispatcher : ICapabilityDispatcher
    {
        // The captain-order vocabulary, extracted from the shipped
        // ComputeDesiredOrder decision table (Patch.cs:33-98, plus the
        // special-case order 11 at Patch.cs:1950). Any other value is
        // refused — never guessed.
        private static readonly int[] CaptainOrderVocabulary =
            { 1, 4, 6, 8, 9, 10, 11, 12, 13 };

        public ExecutionResult Dispatch(CapabilityDescriptor capability, CapabilityRequest request, CapBotTask task)
        {
            if (capability == null || request == null || task == null)
                return ExecutionResult.Rejected("dispatcher: null contract data");

            // Static branch table on the bounded capability id. Ordinal
            // compares against compile-time constants only.
            if (capability.CapabilityId == RegisteredCapabilities.SetCaptainOrder)
                return DispatchSetCaptainOrder(request);
            if (capability.CapabilityId == RegisteredCapabilities.IssueMoveOrder)
                return DispatchIssueMoveOrder(request);
            if (capability.CapabilityId == RegisteredCapabilities.SetCaptainTarget)
                return DispatchSetCaptainTarget(request);
            if (capability.CapabilityId == RegisteredCapabilities.AddCourseGoal)
                return DispatchAddCourseGoal(request);
            if (capability.CapabilityId == RegisteredCapabilities.RemoveCourseGoal)
                return DispatchRemoveCourseGoal(request);
            if (capability.CapabilityId == RegisteredCapabilities.ClearCourseGoals)
                return DispatchClearCourseGoals(request);
            if (capability.CapabilityId == RegisteredCapabilities.ReadWorldSnapshot)
                return DispatchReadWorldSnapshot();

            // Unknown to the dispatcher = not executable here, even though
            // the registry knows it. Fail-closed by construction.
            return ExecutionResult.Rejected("no dispatch branch for capability " + capability.CapabilityId);
        }

        // ---- SET_CAPTAIN_ORDER -------------------------------------------------
        // Target: BoundedToken "ORDER" kind carrying the order id as its id
        // (e.g. "4"). The registry validated the charset; we additionally
        // require it to parse as an integer inside the static vocabulary.
        private ExecutionResult DispatchSetCaptainOrder(CapabilityRequest request)
        {
            int order;
            if (!int.TryParse(request.TargetId, out order))
                return ExecutionResult.Rejected("order token is not an integer: " + request.TargetId);
            bool known = false;
            for (int i = 0; i < CaptainOrderVocabulary.Length; i++)
            {
                if (CaptainOrderVocabulary[i] == order) { known = true; break; }
            }
            if (!known)
                return ExecutionResult.Rejected("order not in verified vocabulary: " + order);

            PLServer server = PLServer.Instance;
            if (server == null) return ExecutionResult.Unavailable("no PLServer");

            server.CaptainSetOrderID(order); // [PunRPC] VERIFIED, direct-call shape (Patch.cs:260)
            CapBotLog.Info(CapBotLog.CAPABILITY, "Dispatched SET_CAPTAIN_ORDER order=" + order);
            return ExecutionResult.Success("captain order set to " + order).WithMeta("order", order.ToString());
        }

        // ---- ISSUE_MOVE_ORDER ---------------------------------------------------
        // Target: None in the contract, so the registry enforced nothing —
        // the dispatcher derives the Vector3 from VERIFIED data only: a
        // "SECTOR" target id resolved through PLGlobal.Instance.Galaxy.
        // AllSectorInfos (compile-proven indexer/Position reads, Patch.cs:415/
        // 2516). Any other target kind is refused — never parsed from text.
        private ExecutionResult DispatchIssueMoveOrder(CapabilityRequest request)
        {
            if (request.TargetKind != "SECTOR")
                return ExecutionResult.Rejected("no verified location derivation for target kind " + request.TargetKind);
            int sectorId;
            if (!int.TryParse(request.TargetId, out sectorId) || sectorId < 0)
                return ExecutionResult.Rejected("sector id is not a valid integer: " + request.TargetId);

            PLGlobal global = PLGlobal.Instance;
            if (global == null || global.Galaxy == null || global.Galaxy.AllSectorInfos == null)
                return ExecutionResult.Unavailable("no galaxy data");

            // Bounded scan (no FindObjectsOfType, no KeyNotFoundException):
            // AllSectorInfos.Values iteration is compile-proven (Patch.cs:2356).
            PLSectorInfo target = null;
            foreach (PLSectorInfo si in global.Galaxy.AllSectorInfos.Values)
            {
                if (si != null && si.ID == sectorId) { target = si; break; }
            }
            if (target == null)
                return ExecutionResult.Rejected("target sector not in galaxy: " + sectorId);

            PLServer server = PLServer.Instance;
            if (server == null || server.AllPlayers == null)
                return ExecutionResult.Unavailable("no PLServer/AllPlayers");

            // The move order is carried by the captain player (same crew pass
            // pattern as PulsarWorldSource — compile-proven predicate).
            PLPlayer captain = null;
            foreach (PLPlayer player in server.AllPlayers)
            {
                if (player != null && player.IsBot && player.TeamID == 0 && player.GetClassID() == 0)
                {
                    captain = player;
                    break;
                }
            }
            if (captain == null) return ExecutionResult.Unavailable("no captain player");

            PLPawn pawn = captain.GetPawn(); // GetPawn() compile-proven (Patch.cs:102)
            if (pawn == null || pawn.photonView == null)
                return ExecutionResult.Unavailable("captain has no pawn/photonView");

            try
            {
                // [PunRPC] VERIFIED method (DLL: IssueMoveOrder(Vector3 loc),
                // sets MovementOrderLoc/TLI/Interior/Time; PLBotController
                // honors for 20s). Sent via photonView.RPC — the compile-proven
                // invocation pattern; PhotonTargets.All mirrors every shipped
                // course-goal send so all peers run the handler exactly once
                // (no separate local call — no duplicate execution).
                pawn.photonView.RPC("IssueMoveOrder", PhotonTargets.All, new object[] { target.Position });
            }
            catch (Exception ex)
            {
                return ExecutionResult.FailureRetryable("IssueMoveOrder RPC fault: " + ex.GetType().Name);
            }
            CapBotLog.Info(CapBotLog.CAPABILITY, "Dispatched ISSUE_MOVE_ORDER sector=" + sectorId);
            return ExecutionResult.Success("move order issued to sector " + sectorId)
                .WithMeta("sector", sectorId.ToString());
        }

        // ---- SET_CAPTAIN_TARGET --------------------------------------------------
        // Target: ShipId (registry parsed the integer). Resolves the target
        // through encounter.AllShips (compile-proven iteration, PulsarWorldSource
        // :102) and calls the public [PunRPC] method DIRECTLY on the player
        // ship: CaptainTargetedSpaceTargetID syncs via the stream (research
        // §131), so a direct master call needs no PhotonTargets.All send —
        // avoiding the local+All duplicate pattern entirely (research §260/§431).
        private ExecutionResult DispatchSetCaptainTarget(CapabilityRequest request)
        {
            int shipId;
            if (!int.TryParse(request.TargetId, out shipId) || shipId < 0)
                return ExecutionResult.Rejected("ship id is not a valid integer: " + request.TargetId);

            PLEncounterManager encounter = PLEncounterManager.Instance;
            if (encounter == null || encounter.AllShips == null)
                return ExecutionResult.Unavailable("no encounter/AllShips");

            // Bounded scan of the encounter ship table (no indexer gamble,
            // no FindObjectsOfType).
            bool found = false;
            foreach (KeyValuePair<int, PLShipInfoBase> kv in encounter.AllShips)
            {
                if (kv.Key == shipId && kv.Value != null) { found = true; break; }
            }
            if (!found)
                return ExecutionResult.Rejected("target ship not in encounter: " + shipId);

            PLShipInfoBase ourShip = encounter.PlayerShip;
            if (ourShip == null) return ExecutionResult.Unavailable("no player ship");

            try
            {
                ourShip.Captain_SetTargetShip(shipId); // public virtual [PunRPC] VERIFIED (DLL)
            }
            catch (Exception ex)
            {
                return ExecutionResult.FailureRetryable("Captain_SetTargetShip fault: " + ex.GetType().Name);
            }
            CapBotLog.Info(CapBotLog.CAPABILITY, "Dispatched SET_CAPTAIN_TARGET ship=" + shipId);
            return ExecutionResult.Success("captain target set to ship " + shipId)
                .WithMeta("ship", shipId.ToString());
        }

        // ---- Course-goal channels -------------------------------------------------
        // All three use the exact shipped RPC shape on the PLServer photonView
        // (Patch.cs:418/423/2604/2621/2629). Sector existence is verified
        // against the galaxy table first (bounded scan, compile-proven
        // iteration); a non-existent sector is refused, never guessed.

        private ExecutionResult DispatchAddCourseGoal(CapabilityRequest request)
        {
            PLSectorInfo sector;
            ExecutionResult pre = ResolveSector(request, out sector);
            if (pre != null) return pre;

            PLServer server = PLServer.Instance;
            if (server == null || server.photonView == null)
                return ExecutionResult.Unavailable("no PLServer/photonView");

            try
            {
                server.photonView.RPC("AddCourseGoal", PhotonTargets.All, new object[] { sector.ID });
            }
            catch (Exception ex)
            {
                return ExecutionResult.FailureRetryable("AddCourseGoal RPC fault: " + ex.GetType().Name);
            }
            CapBotLog.Info(CapBotLog.CAPABILITY, "Dispatched ADD_COURSE_GOAL sector=" + sector.ID);
            return ExecutionResult.Success("course goal added for sector " + sector.ID)
                .WithMeta("sector", sector.ID.ToString());
        }

        private ExecutionResult DispatchRemoveCourseGoal(CapabilityRequest request)
        {
            PLSectorInfo sector;
            ExecutionResult pre = ResolveSector(request, out sector);
            if (pre != null) return pre;

            PLServer server = PLServer.Instance;
            if (server == null || server.photonView == null)
                return ExecutionResult.Unavailable("no PLServer/photonView");

            try
            {
                server.photonView.RPC("RemoveCourseGoal", PhotonTargets.All, new object[] { sector.ID });
            }
            catch (Exception ex)
            {
                return ExecutionResult.FailureRetryable("RemoveCourseGoal RPC fault: " + ex.GetType().Name);
            }
            CapBotLog.Info(CapBotLog.CAPABILITY, "Dispatched REMOVE_COURSE_GOAL sector=" + sector.ID);
            return ExecutionResult.Success("course goal removed for sector " + sector.ID)
                .WithMeta("sector", sector.ID.ToString());
        }

        private ExecutionResult DispatchClearCourseGoals(CapabilityRequest request)
        {
            PLServer server = PLServer.Instance;
            if (server == null || server.photonView == null)
                return ExecutionResult.Unavailable("no PLServer/photonView");

            try
            {
                server.photonView.RPC("ClearCourseGoals", PhotonTargets.All, new object[0]);
            }
            catch (Exception ex)
            {
                return ExecutionResult.FailureRetryable("ClearCourseGoals RPC fault: " + ex.GetType().Name);
            }
            CapBotLog.Info(CapBotLog.CAPABILITY, "Dispatched CLEAR_COURSE_GOALS");
            return ExecutionResult.Success("course goals cleared");
        }

        // Shared sector resolution: parse + galaxy lookup. Returns null when
        // the sector resolved (via out param), else a Rejected/Unavailable
        // result. Uses AllSectorInfos.Values iteration (compile-proven) so a
        // bad id can never throw.
        private ExecutionResult ResolveSector(CapabilityRequest request, out PLSectorInfo sector)
        {
            sector = null;
            int sectorId;
            if (!int.TryParse(request.TargetId, out sectorId) || sectorId < 0)
                return ExecutionResult.Rejected("sector id is not a valid integer: " + request.TargetId);

            PLGlobal global = PLGlobal.Instance;
            if (global == null || global.Galaxy == null || global.Galaxy.AllSectorInfos == null)
                return ExecutionResult.Unavailable("no galaxy data");

            foreach (PLSectorInfo si in global.Galaxy.AllSectorInfos.Values)
            {
                if (si != null && si.ID == sectorId) { sector = si; break; }
            }
            if (sector == null)
                return ExecutionResult.Rejected("target sector not in galaxy: " + sectorId);
            return null;
        }

        // ---- READ_WORLD_SNAPSHOT ---------------------------------------------------
        // Pure read through the Phase 6 service. No authority, no cooldown.
        private ExecutionResult DispatchReadWorldSnapshot()
        {
            WorldSnapshot snap = WorldStateService.Latest;
            if (snap == null || snap.IsNeverCaptured)
                return ExecutionResult.Unavailable("no world snapshot captured yet");
            return ExecutionResult.Success("world snapshot available").WithMeta("snapshotAgeless", "1");
        }
    }
}