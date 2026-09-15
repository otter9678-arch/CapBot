using PulsarModLoader;

namespace CapBot
{
    public class Mod : PulsarMod
    {
        public override string Version => "1.1.0";
        public override string Author => "Tom";
        public override string Name => "CapBot";
        public override string ShortDescription => "Adds an AI captain bot with leveling, personalities, and crew tools";

        public override string HarmonyIdentifier()
        {
            return "Tom.CapBot";
        }
    }
}