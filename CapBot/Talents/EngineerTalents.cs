using CapBot.AI;

namespace CapBot.Talents
{
    public static class EngineerTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent_RepairBoost());
            tree.AddTalent(new Talent_CoolantEfficiency());
            tree.AddTalent(new Talent_AutoExtinguish());

            return tree;
        }

        private class Talent_RepairBoost : Talent
        {
            public Talent_RepairBoost()
            {
                Name = "Repair Boost";
                Description = "Repairs are 20% faster.";
                Tier = 1;
            }

            protected override void OnUnlock()
            {
                PLServer.Instance.RepairSpeedMultiplier *= 1.2f;
            }
        }

        private class Talent_CoolantEfficiency : Talent
        {
            public Talent_CoolantEfficiency()
            {
                Name = "Coolant Efficiency";
                Description = "Coolant usage reduced by 25%.";
                Tier = 2;
            }

            protected override void OnUnlock()
            {
                PLServer.Instance.CoolantUsageMultiplier *= 0.75f;
            }
        }

        private class Talent_AutoExtinguish : Talent
        {
            public Talent_AutoExtinguish()
            {
                Name = "Auto-Extinguish";
                Description = "Automatically extinguish fires near the engineer.";
                Tier = 3;
            }

            protected override void OnUnlock()
            {
                // Passive effect handled in AI
            }
        }
    }
}