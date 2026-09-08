using System;
using System.Collections.Generic;
using CapBot.Core.Capabilities;
using CapBot.Core.Commands;
using CapBot.Core.World;

namespace CapBot.Core.Commands
{
    // ---- Phase 41: authoritative no-op probes ---------------------------------
    //
    // The desired-state side of the gate's no-op suppression: for each
    // capability, compares the command's desired state against AUTHORITATIVE
    // game state and answers IsNoOp only on a positive match. Everything the
    // probe reads here is already compile-proven in shipped code:
    //
    //   PLServer.Instance.CaptainsOrdersID        (Patch.cs:249/269/1969,
    //                                             dispatcher vocabulary list)
    //   PLServer.Instance.m_ShipCourseGoals       (PulsarWorldSource:220,
    //                                             Patch.cs:417-427)
    //   PLShipInfoBase.TargetShip / .ShipID       (PulsarWorldSource:182-185)
    //   PLServer.GetCurrentSector().ID            (PulsarWorldSource:209/359)
    //
    // FAIL-OPEN CONTRACT (the gate's rule): any missing game surface, null
    // collection, unparsable request, or unknown state => CannotDetermine =>
    // the gate ALLOWS the command and the regular pipeline (P7/P5/dispatcher)
    // decides its fate. A probe never rejects, never mutates, never calls a
    // gameplay API. Reads only; bounded loops only (course-goal list is
    // game-bounded; the scan stops at the first match).
    //
    // The capability id in the request is matched against a STATIC, closed
    // switch — an unknown capability (including anything a future custom
    // registration might add) is CannotDetermine by construction. This file
    // never grows a second dispatch mechanism: it only answers "would this
    // command change nothing right now?".
    public static class PulsarNoOpProbes
    {
        // The single probe the gate calls. The capability id comes from the
        // task's CapabilityId metadata (the gate passes it explicitly —
        // CapabilityRequest itself carries only task fields). Returns
        // CannotDetermine for every capability without a proven read.
        public static CommandGate.ProbeAnswer Probe(string capabilityId, CapabilityRequest request)
        {
            if (request == null || string.IsNullOrEmpty(capabilityId)) return CommandGate.ProbeAnswer.CannotDetermine;
            switch (capabilityId)
            {
                case RegisteredCapabilities.SetCaptainOrder: return ProbeSetCaptainOrder(request);
                case RegisteredCapabilities.IssueMoveOrder: return ProbeIssueMoveOrder(request);
                case RegisteredCapabilities.SetCaptainTarget: return ProbeSetCaptainTarget(request);
                case RegisteredCapabilities.AddCourseGoal: return ProbeAddCourseGoal(request);
                case RegisteredCapabilities.RemoveCourseGoal: return ProbeRemoveCourseGoal(request);
                case RegisteredCapabilities.ClearCourseGoals: return ProbeClearCourseGoals(request);
                default: return CommandGate.ProbeAnswer.CannotDetermine; // unknown capability: fail-open, never guessed
            }
        }

        // ---- SET_CAPTAIN_ORDER ----------------------------------------------------
        // Desired state: CaptainsOrdersID == requested order id.
        // NO-OP answer: the authoritative order ALREADY equals the request.
        private static CommandGate.ProbeAnswer ProbeSetCaptainOrder(CapabilityRequest request)
        {
            int orderId;
            if (!int.TryParse(request.TargetId, out orderId)) return CommandGate.ProbeAnswer.CannotDetermine;

            PLServer server = PLServer.Instance;
            if (server == null) return CommandGate.ProbeAnswer.CannotDetermine;
            try
            {
                if ((int)server.CaptainsOrdersID == orderId) return CommandGate.ProbeAnswer.IsNoOp;
                return CommandGate.ProbeAnswer.IsNotNoOp;
            }
            catch (Exception) { return CommandGate.ProbeAnswer.CannotDetermine; }
        }

        // ---- ISSUE_MOVE_ORDER -----------------------------------------------------
        // Desired state: a movement order toward the requested sector. The
        // command's target is the sector, not a course-goal slot, so the
        // positive no-op evidence is: (a) the ship is already IN the target
        // sector, or (b) the sector is already the ship's FIRST course goal.
        // Vanilla PLBotController honors a re-issued move order for 20 s —
        // an already-satisfied order re-dispatched every pass is exactly the
        // loop class the mandate names ("command → no state change → same
        // command"). Anything else (including a later goal slot or unknown
        // nav state) is IsNotNoOp / CannotDetermine, never suppressed.
        private static CommandGate.ProbeAnswer ProbeIssueMoveOrder(CapabilityRequest request)
        {
            int sectorId;
            if (!int.TryParse(request.TargetId, out sectorId) || sectorId < 0)
                return CommandGate.ProbeAnswer.CannotDetermine;

            try
            {
                PLSectorInfo current = PLServer.GetCurrentSector();
                if (current == null) return CommandGate.ProbeAnswer.CannotDetermine;
                if (current.ID == sectorId) return CommandGate.ProbeAnswer.IsNoOp;

                PLServer server = PLServer.Instance;
                if (server == null || server.m_ShipCourseGoals == null || server.m_ShipCourseGoals.Count == 0)
                    return CommandGate.ProbeAnswer.IsNotNoOp; // no course goals at all: a move order is meaningful
                if (server.m_ShipCourseGoals[0] == sectorId) return CommandGate.ProbeAnswer.IsNoOp;
                return CommandGate.ProbeAnswer.IsNotNoOp;
            }
            catch (Exception) { return CommandGate.ProbeAnswer.CannotDetermine; }
        }

        // ---- SET_CAPTAIN_TARGET ------------------------------------------------------
        // Desired state: the player ship's TargetShip == requested ship id
        // (compile-proven read shape PulsarWorldSource:182-185; the captain
        // target syncs via the ship stream — research §131).
        private static CommandGate.ProbeAnswer ProbeSetCaptainTarget(CapabilityRequest request)
        {
            int shipId;
            if (!int.TryParse(request.TargetId, out shipId) || shipId < 0)
                return CommandGate.ProbeAnswer.CannotDetermine;

            try
            {
                PLEncounterManager encounter = PLEncounterManager.Instance;
                if (encounter == null || encounter.PlayerShip == null) return CommandGate.ProbeAnswer.CannotDetermine;
                PLShipInfoBase target = encounter.PlayerShip.TargetShip;
                if (target == null) return CommandGate.ProbeAnswer.IsNotNoOp;
                if (target.ShipID == shipId) return CommandGate.ProbeAnswer.IsNoOp;
                return CommandGate.ProbeAnswer.IsNotNoOp;
            }
            catch (Exception) { return CommandGate.ProbeAnswer.CannotDetermine; }
        }

        // ---- ADD_COURSE_GOAL ----------------------------------------------------------
        // Desired state: the sector present in m_ShipCourseGoals.
        // NO-OP answer: the goal is ALREADY in the course (any slot — adding
        // a duplicate course goal is the exact repeated-effect the mandate
        // targets; AddCourseGoal appends unconditionally server-side).
        private static CommandGate.ProbeAnswer ProbeAddCourseGoal(CapabilityRequest request)
        {
            List<int> goals = ReadCourseGoals();
            if (goals == null) return CommandGate.ProbeAnswer.CannotDetermine;
            int sectorId;
            if (!int.TryParse(request.TargetId, out sectorId)) return CommandGate.ProbeAnswer.CannotDetermine;
            for (int i = 0; i < goals.Count; i++)
            {
                if (goals[i] == sectorId) return CommandGate.ProbeAnswer.IsNoOp;
            }
            return CommandGate.ProbeAnswer.IsNotNoOp;
        }

        // ---- REMOVE_COURSE_GOAL --------------------------------------------------------
        // Desired state: the sector absent from m_ShipCourseGoals.
        // NO-OP answer: the goal is ALREADY absent (removing it again is the
        // repeated no-effect dispatch class).
        private static CommandGate.ProbeAnswer ProbeRemoveCourseGoal(CapabilityRequest request)
        {
            List<int> goals = ReadCourseGoals();
            if (goals == null) return CommandGate.ProbeAnswer.CannotDetermine;
            int sectorId;
            if (!int.TryParse(request.TargetId, out sectorId)) return CommandGate.ProbeAnswer.CannotDetermine;
            for (int i = 0; i < goals.Count; i++)
            {
                if (goals[i] == sectorId) return CommandGate.ProbeAnswer.IsNotNoOp;
            }
            return CommandGate.ProbeAnswer.IsNoOp;
        }

        // ---- CLEAR_COURSE_GOALS ---------------------------------------------------------
        // Desired state: m_ShipCourseGoals empty.
        // NO-OP answer: the course is ALREADY empty.
        private static CommandGate.ProbeAnswer ProbeClearCourseGoals(CapabilityRequest request)
        {
            List<int> goals = ReadCourseGoals();
            if (goals == null) return CommandGate.ProbeAnswer.CannotDetermine;
            return goals.Count == 0 ? CommandGate.ProbeAnswer.IsNoOp : CommandGate.ProbeAnswer.IsNotNoOp;
        }

        // Bounded defensive copy of the authoritative course-goal list. The
        // game's list is bounded by design (starmap slots); the copy loop is
        // additionally capped so no game state can make this unbounded.
        private const int MaxCourseGoalsScan = 16;

        private static List<int> ReadCourseGoals()
        {
            try
            {
                PLServer server = PLServer.Instance;
                if (server == null || server.m_ShipCourseGoals == null) return null;
                List<int> copy = new List<int>(server.m_ShipCourseGoals.Count);
                for (int i = 0; i < server.m_ShipCourseGoals.Count && i < MaxCourseGoalsScan; i++)
                {
                    copy.Add(server.m_ShipCourseGoals[i]);
                }
                return copy;
            }
            catch (Exception) { return null; }
        }
    }
}