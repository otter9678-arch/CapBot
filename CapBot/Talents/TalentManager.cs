using UnityEngine;

namespace CapBot.Talents
{
    public class TalentManager
    {
        public int Level { get; private set; } = 1;
        public int XP { get; private set; } = 0;

        public TalentTree Tree { get; private set; }

        public TalentManager(TalentTree tree)
        {
            Tree = tree;
        }

        public void AddXP(int amount)
        {
            XP += amount;

            int needed = Level * 100;
            if (XP >= needed)
            {
                XP -= needed;
                Level++;
                UnlockTier(Level);
            }
        }

        private void UnlockTier(int tier)
        {
            foreach (Talent t in Tree.GetTier(tier))
            {
                t.Unlock();
            }
        }
    }
}