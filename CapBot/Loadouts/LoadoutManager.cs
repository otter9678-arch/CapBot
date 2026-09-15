using CapBot.AI;
using UnityEngine;

namespace CapBot.Loadouts
{
    public static class LoadoutManager
    {
        private static readonly System.Collections.Generic.Dictionary<int, BotLoadout> Loadouts =
            new System.Collections.Generic.Dictionary<int, BotLoadout>();

        public static BotLoadout GetLoadout(CapBot bot)
        {
            int id = bot.Player.GetPlayerID();

            if (!Loadouts.ContainsKey(id))
            {
                Loadouts[id] = CreateLoadout(bot.Role);
            }

            return Loadouts[id];
        }

        private static BotLoadout CreateLoadout(CapBotRole role)
        {
            switch (role)
            {
                case CapBotRole.Engineer: return new EngineerLoadout();
                case CapBotRole.Weapons: return new WeaponsLoadout();
                case CapBotRole.Science: return new ScienceLoadout();
                case CapBotRole.Pilot: return new PilotLoadout();
                default: return new WeaponsLoadout();
            }
        }

        // Called when bot sees a pickup
        public static bool ShouldPickUp(CapBot bot, string itemName)
        {
            BotLoadout loadout = GetLoadout(bot);
            return loadout.WantsItem(itemName);
        }
    }
}