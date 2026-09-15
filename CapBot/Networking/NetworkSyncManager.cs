using CapBot.AI;
using CapBot.Personality;
using Photon;
using UnityEngine;

namespace CapBot.Networking
{
    public static class NetworkSyncManager
    {
        private static readonly System.Collections.Generic.Dictionary<int, NetworkState> States =
            new System.Collections.Generic.Dictionary<int, NetworkState>();

        private static float _lastSync = 0f;

        public static void Update()
        {
            if (Time.time - _lastSync < 0.25f) // 4 syncs per second
                return;

            _lastSync = Time.time;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0)
                {
                    CapBot bot = AIRegistry.Get(p);
                    SyncBot(bot);
                }
            }
        }

        private static void SyncBot(CapBot bot)
        {
            int id = bot.Player.GetPlayerID();

            if (!States.ContainsKey(id))
                States[id] = new NetworkState(bot);

            NetworkState last = States[id];
            DeltaSyncPacket packet = new DeltaSyncPacket();
            packet.PlayerID = id;

            // ROLE
            if (bot.Role != last.LastRole)
            {
                packet.RoleChanged = true;
                packet.NewRole = (int)bot.Role;
                last.LastRole = bot.Role;
            }

            // LEVEL
            if (bot.Level != last.LastLevel)
            {
                packet.LevelChanged = true;
                packet.NewLevel = bot.Level;
                last.LastLevel = bot.Level;
            }

            // XP
            if (bot.XP != last.LastXP)
            {
                packet.XPChanged = true;
                packet.NewXP = bot.XP;
                last.LastXP = bot.XP;
            }

            // PERSONALITY
            var p = PersonalityManager.Get(bot);
            if (p.Aggression != last.LastAggression ||
                p.Caution != last.LastCaution ||
                p.Curiosity != last.LastCuriosity ||
                p.Loyalty != last.LastLoyalty)
            {
                packet.PersonalityChanged = true;
                packet.Aggression = p.Aggression;
                packet.Caution = p.Caution;
                packet.Curiosity = p.Curiosity;
                packet.Loyalty = p.Loyalty;

                last.LastAggression = p.Aggression;
                last.LastCaution = p.Caution;
                last.LastCuriosity = p.Curiosity;
                last.LastLoyalty = p.Loyalty;
            }

            // If nothing changed, don't send
            if (!packet.RoleChanged &&
                !packet.LevelChanged &&
                !packet.XPChanged &&
                !packet.PersonalityChanged)
                return;

            // Send delta packet
            string json = JsonUtility.ToJson(packet);
            PLServer.Instance.photonView.RPC("CapBot_DeltaSync", PhotonTargets.Others, json);
        }

        // -----------------------------
        // RPC RECEIVER
        // -----------------------------
        [PunRPC]
        public static void CapBot_DeltaSync(string json)
        {
            DeltaSyncPacket packet = JsonUtility.FromJson<DeltaSyncPacket>(json);

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.GetPlayerID() == packet.PlayerID)
                {
                    CapBot bot = AIRegistry.Get(p);

                    if (packet.RoleChanged)
                        bot.Role = (CapBotRole)packet.NewRole;

                    if (packet.LevelChanged)
                        bot.Level = packet.NewLevel;

                    if (packet.XPChanged)
                        bot.XP = packet.NewXP;

                    if (packet.PersonalityChanged)
                    {
                        var pers = PersonalityManager.Get(bot);
                        pers.Aggression = packet.Aggression;
                        pers.Caution = packet.Caution;
                        pers.Curiosity = packet.Curiosity;
                        pers.Loyalty = packet.Loyalty;
                    }
                }
            }
        }
    }
}