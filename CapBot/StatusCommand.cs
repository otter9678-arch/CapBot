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

        public override string[] UsageExamples() => new string[] { "/capbotstatus", "/capbotstatus personalities" };

        // P40: optional focused-section argument. No argument = the full
        // 128-line report (unchanged). A known section name echoes ONLY
        // that section's lines (the full report's tail is all the chat
        // scrollback shows, so mid-report sections like "personalities"
        // were unreachable on screen). Read-only either way.
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
            string section = (arguments != null) ? arguments.Trim() : null;
            if (section != null && section.Length == 0) section = null;
            if (section != null && !StatusHub.IsKnownSection(section))
            {
                PulsarModLoader.Utilities.Messaging.Echo(
                    PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(),
                    "Unknown section '" + section + "'. Sections: " + StatusHub.SectionList());
                return;
            }
            try
            {
                System.Collections.Generic.List<string> lines = (section == null)
                    ? StatusHub.Collect(TaskClock.NowMs)
                    : StatusHub.CollectSection(TaskClock.NowMs, section);
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