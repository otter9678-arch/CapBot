namespace CapBot.Talents
{
    public static class PilotTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent_ManeuverBoost());
            tree.AddTalent(new Talent_ThrusterEfficiency());
            tree.AddTalent(new Talent_AutoEvasion());

            return tree;
        }

        private class Talent_ManeuverBoost : Talent
        {
            public Talent_ManeuverBoost()
            {
                Name = "Maneuver Boost";
                Description = "Turning speed increased by 20%.";
                Tier = 1;
            }

            protected override void OnUnlock()
            {
                PLServer.Instance.ManeuverMultiplier *= 1.2f;
            }
        }

        private class Talent_ThrusterEfficiency : Talent
        {
            public Talent_ThrusterEfficiency()
            {
                Name = "Thruster Efficiency";
                Description = "Thruster usage reduced by 25%.";
                Tier = 2;
            }

            protected override void OnUnlock()
            {
                PLServer.Instance.ThrusterUsageMultiplier *= 0.75f;
            }
        }

        private class Talent_AutoEvasion : Talent
        {
            public Talent_AutoEvasion()
            {
                Name = "Auto Evasion";
                Description = "Automatically dodge incoming fire.";
                Tier = 3;
            }

            protected override void OnUnlock()
            {
                // Passive effect handled in AI
            }
        }
    }
}