using CapBot.AI;

namespace CapBot.Networking
{
    public class NetworkState
    {
        public CapBotRole LastRole;
        public int LastLevel;
        public int LastXP;

        public float LastAggression;
        public float LastCaution;
        public float LastCuriosity;
        public float LastLoyalty;

        public NetworkState(CapBot bot)
        {
            Capture(bot);
        }

        public void Capture(CapBot bot)
        {
            LastRole = bot.Role;
            LastLevel = bot.Level;
            LastXP = bot.XP;

            var p = Personality.PersonalityManager.Get(bot);
            LastAggression = p.Aggression;
            LastCaution = p.Caution;
            LastCuriosity = p.Curiosity;
            LastLoyalty = p.Loyalty;
        }
    }
}