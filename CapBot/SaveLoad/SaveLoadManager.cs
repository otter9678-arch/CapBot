using System.Collections.Generic;
using System.IO;
using UnityEngine;
using CapBot.AI;
using CapBot.Personality;

namespace CapBot.SaveLoad
{
    public static class SaveLoadManager
    {
        private static string SavePath =>
            Path.Combine(Application.persistentDataPath, "CapBotSave.json");

        public static void SaveAllBots()
        {
            List<BotSaveData> allData = new List<BotSaveData>();

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0)
                {
                    CaptainBot bot = AIRegistry.Get(p);
                    allData.Add(CreateSaveData(bot));
                }
            }

            string json = JsonUtility.ToJson(new Wrapper { Bots = allData }, true);
            File.WriteAllText(SavePath, json);
        }

        public static void LoadAllBots()
        {
            if (!File.Exists(SavePath))
                return;

            string json = File.ReadAllText(SavePath);
            Wrapper wrapper = JsonUtility.FromJson<Wrapper>(json);
            if (wrapper == null || wrapper.Bots == null)
                return;

            foreach (BotSaveData data in wrapper.Bots)
            {
                foreach (PLPlayer p in PLServer.Instance.AllPlayers)
                {
                    if (p != null && p.GetPlayerID() == data.PlayerID)
                    {
                        CaptainBot bot = AIRegistry.Get(p);
                        ApplySaveData(bot, data);
                    }
                }
            }
        }

        // -----------------------------
        // CREATE SAVE DATA
        // -----------------------------
        private static BotSaveData CreateSaveData(CaptainBot bot)
        {
            BotSaveData data = new BotSaveData();

            data.PlayerID = bot.Player.GetPlayerID();
            data.Role = bot.Role;

            // Level + XP
            data.Level = bot.Level;
            data.XP = bot.XP;

            // Personality
            Personality.BotPersonality p = PersonalityManager.Get(bot);
            data.Aggression = p.Aggression;
            data.Caution = p.Caution;
            data.Curiosity = p.Curiosity;
            data.Loyalty = p.Loyalty;

            // Talents
            foreach (Talents.Talent t in bot.TalentManager.Tree.Talents)
            {
                if (t.IsUnlocked)
                    data.UnlockedTalents.Add(t.Name);
            }

            return data;
        }

        // -----------------------------
        // APPLY SAVE DATA
        // -----------------------------
        private static void ApplySaveData(CaptainBot bot, BotSaveData data)
        {
            bot.Role = data.Role;

            bot.Level = data.Level;
            bot.XP = data.XP;

            // Apply personality
            Personality.BotPersonality p = PersonalityManager.Get(bot);
            p.Aggression = data.Aggression;
            p.Caution = data.Caution;
            p.Curiosity = data.Curiosity;
            p.Loyalty = data.Loyalty;

            // Apply talents
            foreach (Talents.Talent t in bot.TalentManager.Tree.Talents)
            {
                if (data.UnlockedTalents.Contains(t.Name))
                    t.Unlock();
            }
        }

        // Wrapper for JSON array
        [System.Serializable]
        private class Wrapper
        {
            public List<BotSaveData> Bots;
        }
    }
}