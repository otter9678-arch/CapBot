namespace CapBot.Talents
{
    public static class ScienceTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent_ProgramBoost());
            tree.AddTalent(new Talent_SensorRange());
            tree.AddTalent(new Talent_AutoVirus());

            return tree;
        }

        private class Talent_ProgramBoost : Talent
        {
            public Talent_ProgramBoost()
            {
                Name = "Program Boost";
                Description = "Programs execute 20% faster.";
                Tier = 1;
            }

            protected override void OnUnlock()
            {
                PLServer.Instance.ProgramExecutionSpeed *= 1.2f;
            }
        }

        private class Talent_SensorRange : Talent
        {
            public Talent_SensorRange()
            {
                Name = "Sensor Range";
                Description = "Sensor range increased by 30%.";
                Tier = 2;
            }

            protected override void OnUnlock()
            {
                PLServer.Instance.SensorRangeMultiplier *= 1.3f;
            }
        }

        private class Talent_AutoVirus : Talent
        {
            public Talent_AutoVirus()
            {
                Name = "Auto Virus";
                Description = "Automatically deploy virus when enemy shields drop.";
                Tier = 3;
            }

            protected override void OnUnlock()
            {
                // Passive effect handled in AI
            }
        }
    }
}