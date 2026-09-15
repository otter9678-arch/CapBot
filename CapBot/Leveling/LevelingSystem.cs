using UnityEngine;

namespace CapBot.AI
{
    public static class LevelingSystem
    {
        public static void AddXP(CaptainBot bot, int amount)
        {
            if (bot == null || amount <= 0) return;

            bot.XP += amount;

            int needed = bot.Level * 100;
            if (bot.XP >= needed)
            {
                bot.XP -= needed;
                bot.Level++;
                OnLevelUp(bot);
            }
        }

        private static void OnLevelUp(CaptainBot bot)
        {
            Debug.Log($"[CapBot] {bot.Player.GetPlayerName()} leveled up to {bot.Level}");

            bot.TalentManager.AddXP(bot.Level * 100);
            EvolvePersonality(bot);
        }

        private static void EvolvePersonality(CaptainBot bot)
        {
            Personality.BotPersonality p = Personality.PersonalityManager.Get(bot);

            if (bot.Role == CapBotRole.Weapons)
                p.Aggression = Mathf.Clamp01(p.Aggression + 0.05f);
            if (bot.Role == CapBotRole.Engineer)
                p.Caution = Mathf.Clamp01(p.Caution + 0.05f);
            if (bot.Role == CapBotRole.Science)
                p.Curiosity = Mathf.Clamp01(p.Curiosity + 0.05f);
            if (bot.Role == CapBotRole.Pilot)
                p.Loyalty = Mathf.Clamp01(p.Loyalty + 0.05f);
        }
    }
}