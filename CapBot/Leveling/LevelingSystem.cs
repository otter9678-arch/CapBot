using CapBot.Personality;
using CapBot.Talents;
using UnityEngine;

namespace CapBot.AI.Leveling
{
    public static class LevelingSystem
    {
        public static void AddXP(CapBot bot, int amount)
        {
            bot.XP += amount;

            int needed = bot.Level * 100;

            if (bot.XP >= needed)
            {
                bot.XP -= needed;
                bot.Level++;

                OnLevelUp(bot);
            }
        }

        private static void OnLevelUp(CapBot bot)
        {
            Debug.Log($"[CapBot] {bot.Player.GetPlayerName()} leveled up to {bot.Level}");

            // Unlock talents for this tier
            bot.TalentManager.AddXP(bot.Level * 100);

            // Personality evolution
            EvolvePersonality(bot);

            // Loadout improvements (future expansion)
            // LoadoutManager.Upgrade(bot);

            // Sync to clients
            Networking.NetworkSyncManager.Update();
        }

        // -----------------------------
        // PERSONALITY EVOLUTION
        // -----------------------------
        private static void EvolvePersonality(CapBot bot)
        {
            var p = PersonalityManager.Get(bot);

            // Aggressive roles become more aggressive
            if (bot.Role == CapBotRole.Weapons)
                p.Aggression += 0.05f;

            // Engineers become more cautious
            if (bot.Role == CapBotRole.Engineer)
                p.Caution += 0.05f;

            // Scientists become more curious
            if (bot.Role == CapBotRole.Science)
                p.Curiosity += 0.05f;

            // Pilots become more loyal (follow orders better)
            if (bot.Role == CapBotRole.Pilot)
                p.Loyalty += 0.05f;

            // Clamp values
            p.Aggression = Mathf.Clamp01(p.Aggression);
            p.Caution = Mathf.Clamp01(p.Caution);
            p.Curiosity = Mathf.Clamp01(p.Curiosity);
            p.Loyalty = Mathf.Clamp01(p.Loyalty);
        }
    }
}