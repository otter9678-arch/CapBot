using CapBot.AI;
using UnityEngine;

namespace CapBot.Talents
{
    // Mod-side talent effects. The game exposes no global multiplier fields,
    // so effects adjust the bot's own behavior weights / personality instead.
    public static class TalentEffects
    {
        public static void Apply(Talent talent)
        {
            Debug.Log($"[CapBot] Talent unlocked: {talent.Name}");
            // Personality/weight effects are read dynamically by the AI layer
            // through IsTalentActive checks; nothing to mutate here.
        }

        public static bool IsTalentActive(CaptainBot bot, string talentName)
        {
            if (bot?.TalentManager?.Tree == null) return false;
            foreach (Talent t in bot.TalentManager.Tree.Talents)
            {
                if (t.Name == talentName && t.IsUnlocked) return true;
            }
            return false;
        }
    }
}