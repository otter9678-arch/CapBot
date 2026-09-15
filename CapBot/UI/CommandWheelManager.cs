using CapBot.AI;
using UnityEngine;

namespace CapBot.UI.CommandWheel
{
    public static class CommandWheelManager
    {
        public static void Execute(CommandWheelButton.CommandType cmd)
        {
            if (PLServer.Instance == null || !PhotonNetwork.isMasterClient)
                return;

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0)
                {
                    CaptainBot bot = AIRegistry.Get(p);
                    ApplyCommand(bot, cmd);
                }
            }
        }

        private static void ApplyCommand(CaptainBot bot, CommandWheelButton.CommandType cmd)
        {
            switch (cmd)
            {
                case CommandWheelButton.CommandType.Follow:
                    bot.Role = CapBotRole.Pilot;
                    break;

                case CommandWheelButton.CommandType.Defend:
                    bot.Role = CapBotRole.Weapons;
                    break;

                case CommandWheelButton.CommandType.Attack:
                    bot.Role = CapBotRole.Weapons;
                    break;

                case CommandWheelButton.CommandType.Loot:
                    bot.Role = CapBotRole.Engineer;
                    break;

                case CommandWheelButton.CommandType.Board:
                    PLServer.Instance.CaptainSetOrderID(6);
                    break;

                case CommandWheelButton.CommandType.ReturnToShip:
                    PLServer.Instance.CaptainSetOrderID(1);
                    break;

                case CommandWheelButton.CommandType.Hold:
                    PLServer.Instance.CaptainSetOrderID(1);
                    break;

                case CommandWheelButton.CommandType.Explore:
                    PLServer.Instance.CaptainSetOrderID(13);
                    break;
            }

            bot.LastOrderTime = Time.time;
        }
    }
}