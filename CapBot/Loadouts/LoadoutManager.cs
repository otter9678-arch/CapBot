using System.Collections.Generic;
using CapBot.AI;

namespace CapBot.Loadouts
{
    public static class LoadoutManager
    {
        private static readonly Dictionary<int, BotLoadout> Loadouts =
            new Dictionary<int, BotLoadout>();

        public static BotLoadout GetLoadout(CaptainBot bot)
        {
            int id = bot.Player.GetPlayerID();

            if (!Loadouts.TryGetValue(id, out BotLoadout loadout) || loadout == null)
                Loadouts[id] = loadout = CreateLoadout(bot.Role);

            return loadout;
        }

        public static BotLoadout CreateLoadout(CapBotRole role)
        {
            BotLoadout loadout;
            switch (role)
            {
                case CapBotRole.Engineer: loadout = new EngineerLoadout(); break;
                case CapBotRole.Weapons: loadout = new WeaponsLoadout(); break;
                case CapBotRole.Science: loadout = new ScienceLoadout(); break;
                case CapBotRole.Pilot: loadout = new PilotLoadout(); break;
                default: loadout = new WeaponsLoadout(); break;
            }
            loadout.Initialize();
            return loadout;
        }

        // Called when bot sees a pickup
        public static bool ShouldPickUp(CaptainBot bot, string itemName)
        {
            BotLoadout loadout = GetLoadout(bot);
            return loadout.WantsItem(itemName);
        }
    }
}