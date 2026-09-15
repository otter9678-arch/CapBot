using UnityEngine;

namespace CapBot.AI
{
    public class CapBot
    {
        public PLPlayer Player { get; private set; }
        public CapBotRole Role { get; set; }

        public float LastActionTime;
        public float LastOrderTime;
        public float LastMapUpdate;
        public float LastBlindJump;

        public CapBot(PLPlayer player)
        {
            Player = player;
            Role = CapBotRole.Captain;

            LastActionTime = Time.time;
            LastOrderTime = Time.time;
            LastMapUpdate = Time.time;
            LastBlindJump = Time.time;
        }

        public BehaviorWeights.BehaviorWeights Weights = new BehaviorWeights.BehaviorWeights();

    }
}