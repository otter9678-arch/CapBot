using System;

namespace CapBot.Networking
{
    [Serializable]
    public class DeltaSyncPacket
    {
        public int PlayerID;

        public bool RoleChanged;
        public int NewRole;

        public bool LevelChanged;
        public int NewLevel;

        public bool XPChanged;
        public int NewXP;

        public bool PersonalityChanged;
        public float Aggression;
        public float Caution;
        public float Curiosity;
        public float Loyalty;
    }
}