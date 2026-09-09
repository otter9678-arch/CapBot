using PulsarModLoader.Chat.Commands.CommandRouter;
using CapBot.Core.Logging;
using CapBot.Core.Tasks;

namespace CapBot
{
    // /capbotmission — read-only mission diagnostics (master prompt §33):
    // the mission director's tracked missions + the P52 lifecycle FSM state
    // + the P52 return-policy signals. Host-only like /capbotstatus: the
    // pipeline state it reports is master-authoritative process-local state.
    // Read-only: no state is created, mutated, or re-derived.
    internal class MissionCommand : ChatCommand
    {
        public override string[] CommandAliases() => new string[] { "capbotmission" };

        public override string Description() => "CapBot: mission tracking + lifecycle + return-policy report";

        public override string[] UsageExamples() => new string[] { "/capbotmission" };

        public override void Execute(string arguments)
        {
            if (PLNetworkManager.Instance == null || PLNetworkManager.Instance.LocalPlayer == null)
            {
                CapBotLog.Warning(CapBotLog.MISSION, "/capbotmission ignored: no local player yet");
                return;
            }
            if (!PhotonNetwork.isMasterClient)
            {
                PulsarModLoader.Utilities.Messaging.Notification("Must be host to see CapBot mission status!");
                return;
            }
            try
            {
                PulsarModLoader.Utilities.Messaging.Echo(
                    PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(),
                    "== CapBot missions (director) ==");
                foreach (string line in CapBot.Core.Missions.MissionDirector.StatusLines())
                    Echo(line);
                foreach (string line in CapBot.Core.Missions.MissionDirector.Lines())
                    Echo(line);
                Echo("== CapBot missions (lifecycle FSM) ==");
                foreach (string line in CapBot.Core.Missions.MissionLifecycle.StatusLines())
                    Echo(line);
                foreach (string line in CapBot.Core.Missions.MissionLifecycle.Lines())
                    Echo(line);
            }
            catch (System.Exception ex)
            {
                CapBotLog.Error(CapBotLog.MISSION, "/capbotmission failed", ex);
            }
        }

        private static void Echo(string line)
        {
            PulsarModLoader.Utilities.Messaging.Echo(
                PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(), line);
        }
    }
}