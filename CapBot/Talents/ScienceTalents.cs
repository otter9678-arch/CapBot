namespace CapBot.Talents
{
    public static class ScienceTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent
            {
                Name = "Program Boost",
                Description = "Programs execute 20% faster.",
                Tier = 1
            });
            tree.AddTalent(new Talent
            {
                Name = "Sensor Range",
                Description = "Sensor range increased by 30%.",
                Tier = 2
            });
            tree.AddTalent(new Talent
            {
                Name = "Auto Virus",
                Description = "Deploys virus when enemy shields drop.",
                Tier = 3
            });

            return tree;
        }
    }
}