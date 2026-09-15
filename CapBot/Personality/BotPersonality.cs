using UnityEngine;

namespace CapBot.Personality
{
    public class BotPersonality
    {
        // Core personality traits (0.0 to 1.0)
        public float Aggression;   // How likely to attack, chase, push objectives
        public float Caution;      // How likely to avoid danger, retreat, heal
        public float Curiosity;    // How likely to explore, investigate, wander
        public float Loyalty;      // How strongly they follow captain orders

        public BotPersonality(float aggression, float caution, float curiosity, float loyalty)
        {
            Aggression = Mathf.Clamp01(aggression);
            Caution = Mathf.Clamp01(caution);
            Curiosity = Mathf.Clamp01(curiosity);
            Loyalty = Mathf.Clamp01(loyalty);
        }

        // Random variation for uniqueness
        public static BotPersonality RandomizeAround(BotPersonality baseP)
        {
            return new BotPersonality(
                baseP.Aggression + Random.Range(-0.1f, 0.1f),
                baseP.Caution + Random.Range(-0.1f, 0.1f),
                baseP.Curiosity + Random.Range(-0.1f, 0.1f),
                baseP.Loyalty + Random.Range(-0.1f, 0.1f)
            );
        }
    }
}