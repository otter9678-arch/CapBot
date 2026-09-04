using PulsarModLoader.Chat.Commands.CommandRouter;

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
            PulsarModLoader.Utilities.Messaging.Echo(PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(), ModUpdater.UpdateAll());
        }
    }
}