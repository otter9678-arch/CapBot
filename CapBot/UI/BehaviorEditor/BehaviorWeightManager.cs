using CapBot.AI;
using CapBot.Personality;

namespace CapBot.UI.BehaviorEditor
{
    public static class BehaviorWeightManager
    {
        public static float GetWeightedScore(CapBot bot, string behavior)
        {
            var w = bot.Weights;
            var p = PersonalityManager.Get(bot);

            float score = behavior switch
            {
                "Combat" => w.Combat * p.Aggression * w.AggressionBias,
                "ShipManagement" => w.ShipManagement * p.Loyalty * w.LoyaltyBias,
                "Planet" => w.Planet * p.Curiosity * w.CuriosityBias,
                "Idle" => w.Idle * p.Caution * w.CautionBias,
                _ => 1f
            };

            return score;
        }
    }
}