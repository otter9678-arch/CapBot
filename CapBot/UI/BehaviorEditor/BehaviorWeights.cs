namespace CapBot.AI.BehaviorWeights
{
    [System.Serializable]
    public class BehaviorWeights
    {
        public float Combat = 1.0f;
        public float ShipManagement = 1.0f;
        public float Planet = 1.0f;
        public float Idle = 1.0f;

        public float AggressionBias = 1.0f;
        public float CautionBias = 1.0f;
        public float CuriosityBias = 1.0f;
        public float LoyaltyBias = 1.0f;
    }
}