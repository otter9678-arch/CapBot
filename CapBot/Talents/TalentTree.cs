using System.Collections.Generic;

namespace CapBot.Talents
{
    public class TalentTree
    {
        public List<Talent> Talents = new List<Talent>();

        public void AddTalent(Talent t)
        {
            Talents.Add(t);
        }

        public IEnumerable<Talent> GetTier(int tier)
        {
            foreach (Talent t in Talents)
            {
                if (t.Tier == tier)
                    yield return t;
            }
        }
    }
}