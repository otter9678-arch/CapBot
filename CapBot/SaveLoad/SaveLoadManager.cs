using System.Collections.Generic;
using System.IO;
using System.Text;
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
        private static List<BotSaveData> _pendingSaves;
        private static HashSet<PLPlayer> _loadedBots = new HashSet<PLPlayer>();

        // Called from the meta-layer ticker. The game writes its own encrypted
        // save on exit with no hookable completion point, so bot meta-progression
        // (XP/talents/personality) persists via mod-side autosave instead.
        public static void Poll()
        {
            if (PLServer.Instance == null || !PhotonNetwork.isMasterClient)
            {
                Reset();
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

            ApplyPendingSaves(crewBots);

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

        public static void Reset()
        {
            _loadAttempted = false;
            _nextAutosave = 0f;
            _pendingSaves = null;
            _loadedBots.Clear();
        }

        // JSON is written/parsed by hand: both JsonUtility (null-list skip,
        // no diagnostics) and Newtonsoft (extra assembly resolution inside the
        // game's Mono runtime) proved fragile here, and the data shape is tiny.
        private static void SaveAllBots(List<PLPlayer> crewBots)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("[\n");
                bool first = true;
                foreach (PLPlayer p in crewBots)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    AppendBotJson(sb, CreateSaveData(AIRegistry.Get(p)));
                }
                sb.Append("\n]");

                File.WriteAllText(SavePath, sb.ToString());
                Debug.Log("[CapBot] Saved " + crewBots.Count + " bots");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[CapBot] Save failed: " + e.Message);
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
            // Role stays class-derived (CapBot ctor); older saves recorded
            // Role:0 for every bot and would overwrite the correct mapping.
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

        private static void AppendBotJson(StringBuilder sb, BotSaveData d)
        {
            sb.Append("  { \"PlayerID\": ").Append(d.PlayerID);
            sb.Append(", \"Role\": ").Append((int)d.Role);
            sb.Append(", \"Level\": ").Append(d.Level);
            sb.Append(", \"XP\": ").Append(d.XP);
            sb.Append(", \"Aggression\": ").Append(d.Aggression.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(", \"Caution\": ").Append(d.Caution.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(", \"Curiosity\": ").Append(d.Curiosity.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(", \"Loyalty\": ").Append(d.Loyalty.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(", \"UnlockedTalents\": [");
            for (int i = 0; i < d.UnlockedTalents.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append(EscapeJson(d.UnlockedTalents[i])).Append('"');
            }
            sb.Append("] }");
        }

        private static string EscapeJson(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void LoadAllBots(List<PLPlayer> crewBots)
        {
            if (!File.Exists(SavePath))
                return;

            try
            {
                string json = File.ReadAllText(SavePath);
                List<BotSaveData> savedBots = ParseBotsJson(json);
                if (savedBots == null || savedBots.Count == 0)
                    return;

                // Bots join one at a time over several frames, so stage the
                // parsed data and let Poll apply it as each bot shows up.
                _pendingSaves = savedBots;
                _loadedBots.Clear();
                ApplyPendingSaves(crewBots);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[CapBot] Load failed: " + e.Message);
            }
        }

        // Matches staged save data to bots that have appeared since load.
        // PlayerIDs are reassigned each session, so prefer an exact ID match
        // when it is unique, then fall back to consuming unclaimed records.
        private static void ApplyPendingSaves(List<PLPlayer> crewBots)
        {
            if (_pendingSaves == null || _pendingSaves.Count == 0)
                return;

            for (int i = crewBots.Count - 1; i >= 0; i--)
            {
                PLPlayer p = crewBots[i];
                if (p == null || _loadedBots.Contains(p))
                    continue;

                BotSaveData data = FindSaveData(_pendingSaves, p);
                if (data == null)
                    continue;

                ApplySaveData(AIRegistry.Get(p), data);
                _loadedBots.Add(p);
                data.ClaimedBy = p;
                Debug.Log("[CapBot] Loaded save data for bot " + p.GetPlayerName());
            }

            if (_loadedBots.Count >= _pendingSaves.Count)
                _pendingSaves = null;
        }

        private static BotSaveData FindSaveData(List<BotSaveData> savedBots, PLPlayer p)
        {
            int id = p.GetPlayerID();
            int matches = 0;
            BotSaveData idMatch = null;
            foreach (BotSaveData d in savedBots)
            {
                if (d != null && d.PlayerID == id)
                {
                    matches++;
                    idMatch = d;
                }
            }

            if (matches == 1)
                return idMatch;

            // Ambiguous or unmatched: consume the first record no bot has
            // claimed yet.
            foreach (BotSaveData d in savedBots)
            {
                if (d != null && d.ClaimedBy == null)
                    return d;
            }
            return null;
        }

        // Minimal reader for the exact shape written above. Tolerates
        // whitespace and trailing commas; no external parser dependency.
        private static List<BotSaveData> ParseBotsJson(string json)
        {
            List<BotSaveData> result = new List<BotSaveData>();
            int pos = 0;

            if (!SkipWs(json, ref pos) || json[pos] != '[') return result;

            while (++pos < json.Length)
            {
                SkipWs(json, ref pos);
                if (pos >= json.Length) break;
                if (json[pos] == ']') break;
                if (json[pos] != '{') break;

                BotSaveData bot = new BotSaveData();
                int objEnd = json.IndexOf('}', pos);
                if (objEnd < 0) break;

                string obj = json.Substring(pos + 1, objEnd - pos - 1);
                foreach (string pair in obj.Split(','))
                {
                    int colon = pair.IndexOf(':');
                    if (colon < 0) continue;
                    string key = pair.Substring(0, colon).Trim().Trim('"');
                    string raw = pair.Substring(colon + 1).Trim();
                    ApplyField(bot, key, raw);
                }
                result.Add(bot);
                pos = objEnd;
            }
            return result;
        }

        private static void ApplyField(BotSaveData bot, string key, string raw)
        {
            switch (key)
            {
                case "PlayerID": bot.PlayerID = ParseInt(raw); break;
                case "Role": bot.Role = (CapBotRole)ParseInt(raw); break;
                case "Level": bot.Level = ParseInt(raw); break;
                case "XP": bot.XP = ParseInt(raw); break;
                case "Aggression": bot.Aggression = ParseFloat(raw); break;
                case "Caution": bot.Caution = ParseFloat(raw); break;
                case "Curiosity": bot.Curiosity = ParseFloat(raw); break;
                case "Loyalty": bot.Loyalty = ParseFloat(raw); break;
                case "UnlockedTalents":
                    bot.UnlockedTalents.Clear();
                    foreach (string tok in SplitTopLevel(raw))
                    {
                        string t = tok.Trim().Trim('"');
                        if (t.Length > 0) bot.UnlockedTalents.Add(t);
                    }
                    break;
            }
        }

        private static int ParseInt(string raw)
        {
            int v;
            int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out v);
            return v;
        }

        private static float ParseFloat(string raw)
        {
            float v;
            float.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);
            return v;
        }

        private static bool SkipWs(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
            return pos < json.Length;
        }

        private static IEnumerable<string> SplitTopLevel(string raw)
        {
            raw = raw.Trim();
            if (raw.StartsWith("[")) raw = raw.Substring(1);
            if (raw.EndsWith("]")) raw = raw.Substring(0, raw.Length - 1);
            return raw.Length == 0 ? new string[0] : raw.Split(',');
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
    }
}