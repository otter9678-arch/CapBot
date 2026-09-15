using System.Collections.Generic;
using CapBot.AI;
using UnityEngine;

namespace CapBot.Personality
{
    public static class PersonalityManager
    {
        private static readonly Dictionary<int, BotPersonality> Personalities =
            new Dictionary<int, BotPersonality>();

        public static BotPersonality Get(CapBot bot)
        {
            int id = bot.Player.GetPlayerID();

            if (!Personalities.ContainsKey(id))
            {
                Personalities[id] = GenerateForRole(bot.Role);
            }

            return Personalities[id];
        }

        // Role-based personality presets
        private static BotPersonality GenerateForRole(CapBotRole role)
        {
            BotPersonality baseP;

            switch (role)
            {
                case CapBotRole.Engineer:
                    baseP = new BotPersonality(
                        aggression: 0.2f,
                        caution: 0.8f,
                        curiosity: 0.4f,
                        loyalty: 0.9f
                    );
                    break;

                case CapBotRole.Weapons:
                    baseP = new BotPersonality(
                        aggression: 0.9f,
                        caution: 0.3f,
                        curiosity: 0.3f,
                        loyalty: 0.7f
                    );
                    break;

                case CapBotRole.Science:
                    baseP = new BotPersonality(
                        aggression: 0.3f,
                        caution: 0.6f,
                        curiosity: 0.9f,
                        loyalty: 0.8f
                    );
                    break;

                case CapBotRole.Pilot:
                    baseP = new BotPersonality(
                        aggression: 0.5f,
                        caution: 0.5f,
                        curiosity: 0.6f,
                        loyalty: 0.9f
                    );
                    break;

                default:
                    baseP = new BotPersonality(0.5f, 0.5f, 0.5f, 0.5f);
                    break;
            }

            // Add slight randomness so bots feel unique
            return BotPersonality.RandomizeAround(baseP);
        }

        // Personality modifiers used by AI behaviors
        public static float Aggression(CapBot bot) => Get(bot).Aggression;
        public static float Caution(CapBot bot) => Get(bot).Caution;
        public static float Curiosity(CapBot bot) => Get(bot).Curiosity;
        public static float Loyalty(CapBot bot) => Get(bot).Loyalty;
    }
}