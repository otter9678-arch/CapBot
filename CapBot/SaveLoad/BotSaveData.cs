using System;
using System.Collections.Generic;
using CapBot.AI;

namespace CapBot.SaveLoad
{
    [Serializable]
    public class BotSaveData
    {
        public int PlayerID;
        public CapBotRole Role;

        public int Level;
        public int XP;

        public float Aggression;
        public float Caution;
        public float Curiosity;
        public float Loyalty;

        public List<string> UnlockedTalents = new List<string>();

        // Runtime-only: which PLPlayer consumed this record during load.
        [NonSerialized]
        public PLPlayer ClaimedBy;
    }
}