using System.Collections.Generic;
using CapBot.AI;

namespace CapBot.Personality
{
    public static class PersonalityManager
    {
        private static readonly Dictionary<int, BotPersonality> Personalities =
            new Dictionary<int, BotPersonality>();

        public static BotPersonality Get(CaptainBot bot)
        {
            int id = bot.Player.GetPlayerID();

            if (!Personalities.TryGetValue(id, out BotPersonality p) || p == null)
                Personalities[id] = p = GenerateForRole(bot.Role);

            return p;
        }

        // Role-based personality presets
        private static BotPersonality GenerateForRole(CapBotRole role)
        {
            BotPersonality baseP;

            switch (role)
            {
                case CapBotRole.Engineer:
                    baseP = new BotPersonality(0.2f, 0.8f, 0.4f, 0.9f);
                    break;

                case CapBotRole.Weapons:
                    baseP = new BotPersonality(0.9f, 0.3f, 0.3f, 0.7f);
                    break;

                case CapBotRole.Science:
                    baseP = new BotPersonality(0.3f, 0.6f, 0.9f, 0.8f);
                    break;

                case CapBotRole.Pilot:
                    baseP = new BotPersonality(0.5f, 0.5f, 0.6f, 0.9f);
                    break;

                default:
                    baseP = new BotPersonality(0.5f, 0.5f, 0.5f, 0.5f);
                    break;
            }

            // Add slight randomness so bots feel unique
            return BotPersonality.RandomizeAround(baseP);
        }

        // Personality modifiers used by AI behaviors
        public static float Aggression(CaptainBot bot) => Get(bot).Aggression;
        public static float Caution(CaptainBot bot) => Get(bot).Caution;
        public static float Curiosity(CaptainBot bot) => Get(bot).Curiosity;
        public static float Loyalty(CaptainBot bot) => Get(bot).Loyalty;
    }
}