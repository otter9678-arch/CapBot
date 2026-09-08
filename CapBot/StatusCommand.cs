using PulsarModLoader.Chat.Commands.CommandRouter;
using CapBot.Core.Diagnostics;
using CapBot.Core.Logging;
using CapBot.Core.Tasks;

namespace CapBot
{
    // /capbotstatus — read-only diagnostics: the StatusHub report (P2-P28
    // surfaces) echoed to the command's chat. Host-only by design: the
    // pipeline state it reports is master-authoritative process-local state
    // (a client's report would be all-zero and misleading). Read-only: no
    // state is created, mutated, or re-derived by this command.
    internal class StatusCommand : ChatCommand
    {
        public override string[] CommandAliases() => new string[] { "capbotstatus" };

        public override string Description() => "CapBot: task-pipeline and crew-layer status report";

        public override string[] UsageExamples() => new string[] { "/capbotstatus" };

        public override void Execute(string arguments)
        {
            if (PLNetworkManager.Instance == null || PLNetworkManager.Instance.LocalPlayer == null)
            {
                CapBotLog.Warning(CapBotLog.CORE, "/capbotstatus ignored: no local player yet");
                return;
            }
            if (!PhotonNetwork.isMasterClient)
            {
                PulsarModLoader.Utilities.Messaging.Notification("Must be host to see CapBot status!");
                return;
            }
            try
            {
                System.Collections.Generic.List<string> lines = StatusHub.Collect(TaskClock.NowMs);
                for (int i = 0; i < lines.Count; i++)
                {
                    PulsarModLoader.Utilities.Messaging.Echo(
                        PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(), lines[i]);
                }
            }
            catch (System.Exception ex)
            {
                CapBotLog.Error(CapBotLog.CORE, "/capbotstatus failed", ex);
            }
        }
    }
}