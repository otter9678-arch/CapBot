using CapBot.AI;

namespace CapBot.Talents
{
    // Builds the talent tree matching a bot's role.
    public static class TalentLoadout
    {
        public static TalentTree CreateForRole(CapBotRole role)
        {
            switch (role)
            {
                case CapBotRole.Pilot: return PilotTalents.Create();
                case CapBotRole.Engineer: return EngineerTalents.Create();
                case CapBotRole.Weapons: return WeaponsTalents.Create();
                case CapBotRole.Science: return ScienceTalents.Create();
                default: return CaptainTalents.Create();
            }
        }
    }

    public static class CaptainTalents
    {
        public static TalentTree Create()
        {
            TalentTree tree = new TalentTree();

            tree.AddTalent(new Talent
            {
                Name = "Cool Head",
                Description = "Orders issued faster under pressure.",
                Tier = 1
            });
            tree.AddTalent(new Talent
            {
                Name = "Tactician",
                Description = "Better target prioritization in combat.",
                Tier = 2
            });
            tree.AddTalent(new Talent
            {
                Name = "Veteran Captain",
                Description = "Crew follows orders more reliably.",
                Tier = 3
            });

            return tree;
        }
    }
}