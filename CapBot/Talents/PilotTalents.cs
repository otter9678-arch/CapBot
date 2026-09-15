namespace CapBot.Talents
{
    public static class PilotTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent
            {
                Name = "Maneuver Boost",
                Description = "Turning speed improved while piloting.",
                Tier = 1
            });
            tree.AddTalent(new Talent
            {
                Name = "Thruster Efficiency",
                Description = "Uses less reactor power while thrusting.",
                Tier = 2
            });
            tree.AddTalent(new Talent
            {
                Name = "Auto Evasion",
                Description = "Dodges incoming fire more often.",
                Tier = 3
            });

            return tree;
        }
    }
}