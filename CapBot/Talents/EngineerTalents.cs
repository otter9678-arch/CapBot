namespace CapBot.Talents
{
    public static class EngineerTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent
            {
                Name = "Repair Boost",
                Description = "Repairs systems 20% faster.",
                Tier = 1
            });
            tree.AddTalent(new Talent
            {
                Name = "Coolant Efficiency",
                Description = "Coolant lasts 25% longer.",
                Tier = 2
            });
            tree.AddTalent(new Talent
            {
                Name = "Auto-Extinguish",
                Description = "Rushes to extinguish nearby fires.",
                Tier = 3
            });

            return tree;
        }
    }
}