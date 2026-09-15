namespace CapBot.Talents
{
    public static class WeaponsTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent_AmmoSaver());
            tree.AddTalent(new Talent_WeaponHeatReduction());
            tree.AddTalent(new Talent_AutoTargeting());

            return tree;
        }

        private class Talent_AmmoSaver : Talent
        {
            public Talent_AmmoSaver()
            {
                Name = "Ammo Saver";
                Description = "Weapons consume 20% less ammo.";
                Tier = 1;
            }

            protected override void OnUnlock()
            {
                PLServer.Instance.AmmoUsageMultiplier *= 0.8f;
            }
        }

        private class Talent_WeaponHeatReduction : Talent
        {
            public Talent_WeaponHeatReduction()
            {
                Name = "Heat Reduction";
                Description = "Weapons generate 25% less heat.";
                Tier = 2;
            }

            protected override void OnUnlock()
            {
                PLServer.Instance.WeaponHeatMultiplier *= 0.75f;
            }
        }

        private class Talent_AutoTargeting : Talent
        {
            public Talent_AutoTargeting()
            {
                Name = "Auto Targeting";
                Description = "Weapons auto-target enemies more accurately.";
                Tier = 3;
            }

            protected override void OnUnlock()
            {
                // Passive effect handled in AI
            }
        }
    }
}