using System.Collections.Generic;
using CapBot.AI;
using CapBot.Personality;
using UnityEngine;

namespace CapBot.Networking
{
    // Master → client meta-state sync over Photon RaiseEvent (event code 97).
    // Static [PunRPC] methods can never be invoked by the game's RPC dispatch,
    // which only resolves methods on PhotonView-attached behaviours.
    public static class NetworkSyncManager
    {
        private const byte EVENT_CODE = 97;

        private static readonly Dictionary<int, NetworkState> States =
            new Dictionary<int, NetworkState>();

        private static float _lastSync = 0f;
        private static bool _eventHooked;

        public static void PollEvents()
        {
            if (!_eventHooked)
            {
                PhotonNetwork.OnEventCall += OnPhotonEvent;
                _eventHooked = true;
            }

            if (PLServer.Instance == null || !PhotonNetwork.isMasterClient)
                return;

            if (Time.time - _lastSync < 0.25f) // 4 syncs per second
                return;

            _lastSync = Time.time;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0)
                {
                    CaptainBot bot = AIRegistry.Get(p);
                    SyncBot(bot);
                }
            }
        }

        public static void Reset()
        {
            States.Clear();
        }

        private static void SyncBot(CaptainBot bot)
        {
            int id = bot.Player.GetPlayerID();

            if (!States.TryGetValue(id, out NetworkState last))
                States[id] = last = new NetworkState(bot);

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
            BotPersonality p = PersonalityManager.Get(bot);
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

            // Send delta packet (JSON keeps the payload Photon-serializable)
            string json = JsonUtility.ToJson(packet);
            PhotonNetwork.RaiseEvent(EVENT_CODE, json, true, new RaiseEventOptions
            {
                Receivers = ReceiverGroup.Others
            });
        }

        // -----------------------------
        // EVENT RECEIVER
        // -----------------------------
        private static void OnPhotonEvent(byte eventCode, object content, int senderId)
        {
            if (eventCode != EVENT_CODE || content == null)
                return;

            DeltaSyncPacket packet = JsonUtility.FromJson<DeltaSyncPacket>((string)content);
            if (packet == null)
                return;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.GetPlayerID() == packet.PlayerID)
                {
                    CaptainBot bot = AIRegistry.Get(p);

                    if (packet.RoleChanged)
                        bot.Role = (CapBotRole)packet.NewRole;

                    if (packet.LevelChanged)
                        bot.Level = packet.NewLevel;

                    if (packet.XPChanged)
                        bot.XP = packet.NewXP;

                    if (packet.PersonalityChanged)
                    {
                        BotPersonality pers = PersonalityManager.Get(bot);
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