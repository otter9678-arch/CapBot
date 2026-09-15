using System.Collections.Generic;

namespace CapBot.AI
{
    public static class AIRegistry
    {
        private static readonly Dictionary<int, CaptainBot> Bots = new Dictionary<int, CaptainBot>();

        public static CaptainBot Get(PLPlayer player)
        {
            if (player == null) return null;

            int id = player.GetPlayerID();
            CaptainBot bot;
            if (!Bots.TryGetValue(id, out bot) || bot.Player != player)
                Bots[id] = bot = new CaptainBot(player);

            return bot;
        }

        // The bot flying with us in the captain seat (local perspective).
        public static CaptainBot GetLocalBot()
        {
            if (PLNetworkManager.Instance == null || PLNetworkManager.Instance.LocalPlayer == null)
                return null;

            PLPlayer local = PLNetworkManager.Instance.LocalPlayer;
            PLShipInfo ship = local.StartingShip;
            if (ship == null) return null;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0 && p.StartingShip == ship)
                    return Get(p);
            }
            return null;
        }
    }
}