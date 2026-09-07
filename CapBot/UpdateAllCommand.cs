using PulsarModLoader.Chat.Commands.CommandRouter;
using CapBot.Core.Logging;

namespace CapBot
{
    // /updateall — checks every loaded PML mod's VersionLink, downloads newer DLLs,
    // stages them if the running DLL is locked, and reports PML's own status.
    internal class UpdateAllCommand : ChatCommand
    {
        public override string[] CommandAliases() => new string[] { "updateall" };

        public override string Description() => "CapBot: auto-update every installed mod (and checks PML)";

        public override string[] UsageExamples() => new string[] { "/updateall" };

        public override void Execute(string arguments)
        {
            // The chat router can invoke this before the local player exists (finding M7).
            if (PLNetworkManager.Instance == null || PLNetworkManager.Instance.LocalPlayer == null)
            {
                CapBotLog.Warning(CapBotLog.UPDATER, "/updateall ignored: no local player yet");
                return;
            }
            try
            {
                PulsarModLoader.Utilities.Messaging.Echo(PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(), ModUpdater.UpdateAll());
            }
            catch (System.Exception ex)
            {
                CapBotLog.Error(CapBotLog.UPDATER, "/updateall failed", ex);
            }
        }
    }
}