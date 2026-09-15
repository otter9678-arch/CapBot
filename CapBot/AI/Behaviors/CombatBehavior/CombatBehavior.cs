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

            //