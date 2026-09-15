using System.Collections.Generic;

namespace CapBot.AI
{
    public static class AIRegistry
    {
        private static readonly Dictionary<int, CapBot> Bots = new Dictionary<int, CapBot>();

        public static CapBot Get(PLPlayer player)
        {
            if (!Bots.ContainsKey(player.GetPlayerID()))
                Bots[player.GetPlayerID()] = new CapBot(player);

            return Bots[player.GetPlayerID()];
        }
    }
}
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CapBot.AI
{
    internal class AIRegistry
    {
    }
}
