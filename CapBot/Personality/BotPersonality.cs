using System.Collections.Generic;

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
            Aggression = UnityEngine.Mathf.Clamp01(aggression);
            Caution = UnityEngine.Mathf.Clamp01(caution);
            Curiosity = UnityEngine.Mathf.Clamp01(curiosity);
            Loyalty = UnityEngine.Mathf.Clamp01(loyalty);
        }

        // Random variation for uniqueness
        public static BotPersonality RandomizeAround(BotPersonality baseP)
        {
            return new BotPersonality(
                baseP.Aggression + UnityEngine.Random.Range(-0.1f, 0.1f),
                baseP.Caution + UnityEngine.Random.Range(-0.1f, 0.1f),
                baseP.Curiosity + UnityEngine.Random.Range(-0.1f, 0.1f),
                baseP.Loyalty + UnityEngine.Random.Range(-0.1f, 0.1f)
            );
        }
    }
}