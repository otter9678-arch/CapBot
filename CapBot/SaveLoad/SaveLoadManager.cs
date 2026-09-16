using System.Collections.Generic;
using System.IO;
using UnityEngine;
using CapBot.AI;
using CapBot.Personality;

namespace CapBot.SaveLoad
{
    public static class SaveLoadManager
    {
        private const float AUTOSAVE_INTERVAL = 60f;

        private static string SavePath =>
            Path.Combine(Application.persistentDataPath, "CapBotSave.json");

        private static bool _loadAttempted;
        private static float _nextAutosave;

        // Called from the meta-layer ticker. The game writes its own encrypted
        // save on exit with no hookable completion point, so bot meta-progression
        // (XP/talents/personality) persists via mod-side autosave instead.
        public static void Poll()
        {
            if (PLServer.Instance == null || !PhotonNetwork.isMasterClient)
            {
                _loadAttempted = false;
                _nextAutosave = 0f;
                return;
            }

            List<PLPlayer> crewBots = GetCrewBots();
            if (crewBots.Count == 0)
            {
                _loadAttempted = false;
                return;
            }

            if (!_loadAttempted)
            {
                _loadAttempted = true;
                LoadAllBots(crewBots);
                _nextAutosave = Time.time + AUTOSAVE_INTERVAL;
                return;
            }

            if (Time.time >= _nextAutosave)
            {
                _nextAutosave = Time.time + AUTOSAVE_INTERVAL;
                SaveAllBots(crewBots);
            }
        }

        public static void SaveAllBots()
        {
            List<PLPlayer> crewBots = GetCrewBots();
            if (crewBots.Count > 0)
                SaveAllBots(crewBots);
        }

        public static void LoadAllBots()
        {
            _loadAttempted = true;
            LoadAllBots(GetCrewBots());
        }

        private static void SaveAllBots(List<PLPlayer> crewBots)
        {
            try
            {
                List<BotSaveData> allData = new List<BotSaveData>();
                foreach (PLPlayer p in crewBots)
                    allData.Add(CreateSaveData(AIRegistry.Get(p)));

                if (allData.Count == 0)
                {
                    Debug.LogWarning("[CapBot] Save skipped: no bot data produced");
                    return;
                }

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(allData, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(SavePath, json);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[CapBot] Save failed: " + e.Message);
            }
        }

        private static void LoadAllBots(List<PLPlayer> crewBots)
        {
            if (!File.Exists(SavePath))
                return;

            try
            {
                string json = File.ReadAllText(SavePath);
                List<BotSaveData> savedBots =
                    Newtonsoft.Json.JsonConvert.DeserializeObject<List<BotSaveData>>(json);
                if (savedBots == null || savedBots.Count == 0)
                    return;

                // PlayerIDs are reassigned each session, so match by crew order
                // first (deterministic for the single-captain case) and fall
                // back to an ID match for larger crews.
                for (int i = 0; i < crewBots.Count && i < savedBots.Count; i++)
                {
                    BotSaveData data = savedBots[i];
                    if (data == null) continue;

                    PLPlayer p = crewBots[i];
                    if (p.GetPlayerID() == data.PlayerID || CountMatches(savedBots, data.PlayerID) == 1)
                    {
                        ApplySaveData(AIRegistry.Get(p), data);
                        Debug.Log("[CapBot] Loaded save data for bot " + p.GetPlayerName());
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[CapBot] Load failed: " + e.Message);
            }
        }

        private static int CountMatches(List<BotSaveData> savedBots, int playerID)
        {
            int count = 0;
            foreach (BotSaveData d in savedBots)
            {
                if (d != null && d.PlayerID == playerID) count++;
            }
            return count;
        }

        // -----------------------------
        // CREATE SAVE DATA
        // -----------------------------
        private static BotSaveData CreateSaveData(CaptainBot bot)
        {
            BotSaveData data = new BotSaveData();

            data.PlayerID = bot.Player.GetPlayerID();
            data.Role = bot.Role;

            data.Level = bot.Level;
            data.XP = bot.XP;

            BotPersonality p = PersonalityManager.Get(bot);
            data.Aggression = p.Aggression;
            data.Caution = p.Caution;
            data.Curiosity = p.Curiosity;
            data.Loyalty = p.Loyalty;

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

            BotPersonality p = PersonalityManager.Get(bot);
            p.Aggression = data.Aggression;
            p.Caution = data.Caution;
            p.Curiosity = data.Curiosity;
            p.Loyalty = data.Loyalty;

            foreach (Talents.Talent t in bot.TalentManager.Tree.Talents)
            {
                if (data.UnlockedTalents.Contains(t.Name))
                    t.Unlock();
            }
        }

        private static List<PLPlayer> GetCrewBots()
        {
            List<PLPlayer> bots = new List<PLPlayer>();
            if (PLServer.Instance == null) return bots;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0)
                    bots.Add(p);
            }
            return bots;
        }

        public static void Reset()
        {
            _loadAttempted = false;
            _nextAutosave = 0f;
        }
    }
}