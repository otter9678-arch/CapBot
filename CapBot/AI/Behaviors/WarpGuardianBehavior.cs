using UnityEngine;

namespace CapBot.AI
{
    public static class WarpGuardianBehavior
    {
        public static void Run(CapBot bot)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;
            if (ship == null) return;

            // Ensure alert level is red
            ship.AlertLevel = 2;

            // Identify the Warp Guardian
            PLShipInfoBase guardian = FindWarpGuardian();
            if (guardian == null)
                return;

            // Set target
            ship.TargetShip = guardian;

            // Maintain distance and positioning
            MaintainCombatDistance(bot, guardian);

            // Fire weapons
            FireWeapons(ship);

            // Use programs
            UsePrograms(ship);

            // Warp skip if shields are full and safe
            TryWarpSkip(ship);

            // Emergency retreat
            TryEmergencyRetreat(bot, guardian);
        }

        // -----------------------------
        // FIND THE WARP GUARDIAN
        // -----------------------------
        private static PLShipInfoBase FindWarpGuardian()
        {
            foreach (PLShipInfoBase ship in PLEncounterManager.Instance.AllShips.Values)
            {
                if (ship != null && ship.ShipTypeID == EShipType.E_WARP_GUARDIAN)
                    return ship;
            }
            return null;
        }

        // -----------------------------
        // COMBAT POSITIONING
        // -----------------------------
        private static void MaintainCombatDistance(CapBot bot, PLShipInfoBase guardian)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;

            float desiredRange = 2500f;
            float currentRange = (guardian.Exterior.transform.position - ship.Exterior.transform.position).magnitude;

            if (currentRange < desiredRange * 0.7f)
            {
                // Back away
                ship.MyFlightAI.RequestThrust(-1f);
            }
            else if (currentRange > desiredRange * 1.3f)
            {
                // Move closer
                ship.MyFlightAI.RequestThrust(1f);
            }

            // Face the guardian
            ship.MyFlightAI.RequestLookTarget(guardian.Exterior.transform.position);
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
        private static void UsePrograms(PLShipInfo ship)
        {
            foreach (PLProgram program in ship.MyStats.Programs)
            {
                if (program == null) continue;

                // Use shield booster when shields drop
                if (program.ProgramType == EProgramType.E_SHIELD_BOOST &&
                    ship.MyStats.ShieldsCurrent / ship.MyStats.ShieldsMax < 0.5f)
                {
                    program.AttemptProgramExecution();
                }

                // Use virus when guardian is vulnerable
                if (program.ProgramType == EProgramType.E_VIRUS &&
                    ship.TargetShip != null &&
                    !ship.TargetShip.IsQuantumShieldActive)
                {
                    program.AttemptProgramExecution();
                }
            }
        }

        // -----------------------------
        // WARP SKIP WHEN SAFE
        // -----------------------------
        private static void TryWarpSkip(PLShipInfo ship)
        {
            if (!ship.InWarp &&
                ship.MyStats.ShieldsCurrent / ship.MyStats.ShieldsMax > 0.99f &&
                PLServer.Instance.AllPlayersLoaded())
            {
                PLInGameUI.Instance.WarpSkipButtonClicked();
            }
        }

        // -----------------------------
        // EMERGENCY RETREAT
        // -----------------------------
        private static void TryEmergencyRetreat(CapBot bot, PLShipInfoBase guardian)
        {
            PLPlayer player = bot.Player;
            PLShipInfo ship = player.StartingShip;

            bool outnumbered = ship.HostileShips.Count > 1;
            bool strongerEnemy = guardian.GetCombatLevel() > ship.GetCombatLevel();
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