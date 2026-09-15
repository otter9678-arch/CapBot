using Org.BouncyCastle.Asn1.Cms;
using System;
using UnityEngine;

namespace CapBot.AI
{
    public static class AIUtils
    {
        public static bool IsValidCaptain(PLPlayer p)
        {
            return p != null &&
                   p.IsBot &&
                   p.TeamID == 0 &&
                   p.GetClassID() == 0 &&
                   PhotonNetwork.isMasterClient &&
                   p.StartingShip != null;
        }

        public static void UpdateBotState(CapBot bot) => bot.LastActionTime = Time.time;

        public override string ToString()
        {
            throw new NotImplementedException();
        }

        public override bool Equals(object obj)
        {
            throw new NotImplementedException();
        }

        public override int GetHashCode()
        {
            throw new NotImplementedException();
        }

        public override void Finalize()
        {
            throw new NotImplementedException();
        }
    }
}