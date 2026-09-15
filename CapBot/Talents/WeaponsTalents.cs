namespace CapBot.Talents
{
    public static class WeaponsTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent
            {
                Name = "Ammo Saver",
                Description = "Weapons consume 20% less ammo.",
                Tier = 1
            });
            tree.AddTalent(new Talent
            {
                Name = "Heat Reduction",
                Description = "Weapons generate 25% less heat.",
                Tier = 2
            });
            tree.AddTalent(new Talent
            {
                Name = "Auto Targeting",
                Description = "Turrets track enemies more accurately.",
                Tier = 3
            });

            return tree;
        }
    }
}