using System.Collections.Generic;

namespace CapBot.Loadouts
{
    public abstract class BotLoadout
    {
        public List<string> PreferredWeapons = new List<string>();
        public List<string> PreferredTools = new List<string>();
        public List<string> PreferredUtility = new List<string>();

        public abstract void Initialize();

        public bool WantsItem(string itemName)
        {
            return PreferredWeapons.Contains(itemName) ||
                   PreferredTools.Contains(itemName) ||
                   PreferredUtility.Contains(itemName);
        }
    }
}


