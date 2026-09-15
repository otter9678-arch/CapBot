using UnityEngine;
using System.Linq;

namespace CapBot.AI
{
    public static class CombatBehavior
    {
        public static bool Run(CapBot bot)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;
            if (ship == null) return false;

            // If no hostiles, no combat
            if (ship.HostileShips.Count == 0 &&
                ship.TargetShip == null &&
                ship.TargetSpaceTarget == null)
                return false;

            // Set alert level
            ship.AlertLevel = 2;

            // Pick best target
            PLShipInfoBase target = SelectTarget(ship);
            if (target == null)
                return false;

            ship.TargetShip = target;

            // Maneuver toward target
            Maneuver(ship, target);

            // Fire weapons
            FireWeapons(ship);

            // Use programs
            UsePrograms(ship, target);

            // Boarding logic
            TryBoardEnemy(bot, target);

            // Emergency retreat
            TryEmergencyRetreat(bot, target);

            return true;
        }

        // -----------------------------
        // TARGET SELECTION
        // -----------------------------
        private static PLShipInfoBase SelectTarget(PLShipInfo ship)
        {
            // Prefer current target if valid
            if (ship.TargetShip != null &&
                ship.TargetShip != ship &&
                !ship.TargetShip.IsAbandoned())
                return ship.TargetShip;

            // Otherwise pick nearest hostile
            PLShipInfoBase best = null;
            float bestDist = float.MaxValue;

            foreach (PLShipInfoBase hostile in ship.HostileShips)
            {
                if (hostile == null) continue;

                float dist = (hostile.Exterior.transform.position -
                              ship.Exterior.transform.position).sqrMagnitude;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = hostile;
                }
            }

            return best;
        }

        // -----------------------------
        // MANEUVERING
        // -----------------------------
        private static void Maneuver(PLShipInfo ship, PLShipInfoBase target)
        {
            Vector3 targetPos = target.Exterior.transform.position;

            // Face the target
            ship.MyFlightAI.RequestLookTarget(targetPos);

            float dist = (targetPos - ship.Exterior.transform.position).magnitude;

            // Maintain ideal combat distance
            float ideal = 1800f;

            if (dist > ideal * 1.2f)
            {
                ship.MyFlightAI.RequestThrust(1f);
            }
            else if (dist < ideal * 0.8f)
            {
                ship.MyFlightAI.RequestThrust(-0.5f);
            }
            else
            {
                ship.MyFlightAI.RequestThrust(0f);
            }

            // Use boost if chasing
            if (dist > ideal * 1.5f &&
                ship.MyStats.EngineBoostCharge >= 0.9f)
            {
                ship.MyFlightAI.RequestBoost();
            }
        }

        // -----------------------------
        // WEAPON FIRING
        // -----------------------------
        private static void FireWeapons(PLShipInfo ship)
        {
            foreach (PLWeapon weapon in ship.MyStats.Weapons)
            {
                if (weapon == null) continue;

                if (weapon.TimeUntilReady <= 0f)
                {
                    weapon.Fire();
                }
            }
        }

        // -----------------------------
        // PROGRAM USAGE
        // -----------------------------
        private static void UsePrograms(PLShipInfo ship, PLShipInfoBase target)
        {
            foreach (PLProgram program in ship.MyStats.Programs)
            {
                if (program == null) continue;

                // Shield booster when shields low
                if (program.ProgramType == EProgramType.E_SHIELD_BOOST &&
                    ship.MyStats.ShieldsCurrent / ship.MyStats.ShieldsMax < 0.5f)
                {
                    program.AttemptProgramExecution();
                }

                // Virus when enemy vulnerable
                if (program.ProgramType == EProgramType.E_VIRUS &&
                    target != null &&
                    !target.IsQuantumShieldActive)
                {
                    program.AttemptProgramExecution();
                }

                // Defense programs when taking heavy fire
                if (program.ProgramType == EProgramType.E_DEFENSE &&
                    Time.time - ship.LastTookDamageTime() < 5f)
                {
                    program.AttemptProgramExecution();
                }
            }
        }

        // -----------------------------
        // BOARDING LOGIC
        // -----------------------------
        private static void TryBoardEnemy(CapBot bot, PLShipInfoBase target)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;

            if (target == null) return;

            // Only board if enemy is weak or disabled
            bool canBoard =
                !target.IsQuantumShieldActive &&
                target.MyStats.HullCurrent / target.MyStats.HullMax < 0.3f;

            if (!canBoard) return;

            // Set captain order to board
            if (PLServer.Instance.CaptainsOrdersID != 6)
            {
                PLServer.Instance.CaptainSetOrderID(6);
            }
        }

        // -----------------------------
        // EMERGENCY RETREAT
        // -----------------------------
        private static void TryEmergencyRetreat(CapBot bot, PLShipInfoBase target)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;

            bool outnumbered = ship.HostileShips.Count > 1;
            bool strongerEnemy = target.GetCombatLevel() > ship.GetCombatLevel();
            bool lowHull = ship.MyStats.HullCurrent / ship.MyStats.HullMax < 0.2f;

            if ((outnumbered || strongerEnemy) && lowHull && !ship.InWarp)
            {
                // Move to blind jump console
                Vector3 consolePos = (ship.Spawners[4] as GameObject).transform.position;

                player.MyBot.AI_TargetPos = consolePos;
                player.MyBot.AI_TargetPos_Raw = consolePos;
                player.MyBot.AI_TargetTLI = ship.MyTLI;

                if ((consolePos - player.GetPawn().transform.position).sqrMagnitude > 4f)
                {
                    player.MyBot.EnablePathing = true;
                }
                else
                {
                    ship.BlindJumpUnlocked = true;
                    PLServer.Instance.photonView.RpcSecure("AttemptBlindJump", PhotonTargets.MasterClient, true,
                        new object[] { ship.ShipID, player.GetPlayerID() });
                }
            }
        }
    }
}