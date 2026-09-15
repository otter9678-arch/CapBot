using UnityEngine;

namespace CapBot.AI
{
    // Per-bot meta state: role, XP, talents, personality, behavior weights.
    public class CaptainBot
    {
        public PLPlayer Player { get; private set; }
        public CapBotRole Role { get; set; }
        public string CurrentBehavior = "Idle";

        public int Level = 1;
        public int XP;

        public float LastActionTime;
        public float LastOrderTime;
        public float LastMapUpdate;
        public float LastBlindJump;

        public Talents.TalentManager TalentManager;
        public Dialogue.VoiceProfile Voice;
        public Loadouts.BotLoadout Loadout;

        public BehaviorWeights Weights = new BehaviorWeights();

        public CaptainBot(PLPlayer player)
        {
            Player = player;
            Role = CapBotRole.Captain;

            float now = Time.time;
            LastActionTime = now;
            LastOrderTime = now;
            LastMapUpdate = now;
            LastBlindJump = now;

            TalentManager = new Talents.TalentManager(Talents.TalentLoadout.CreateForRole(Role));
            Voice = Dialogue.VoiceProfile.ForRole(Role);
            Loadout = Loadouts.LoadoutManager.CreateLoadout(Role);
        }
    }
}