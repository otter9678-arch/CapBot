using System.Collections.Generic;
using PulsarModLoader.Chat.Commands.CommandRouter;
using CapBot.Core.Compatibility;
using CapBot.Core.Logging;

namespace CapBot
{
    // /capbotcompat — read-only per-mod compatibility readback from the P46
    // conflict engine. Vocabulary per the standing directive: Loaded /
    // Compatible / Conflict / AutoDisabled(=quarantined) / Quarantined (+
    // reason). With no argument lists every tracked mod. Read-only: the
    // command never classifies, quarantines, or restores anything — it only
    // echoes the engine's existing verdicts.
    internal class CompatCommand : ChatCommand
    {
        public override string[] CommandAliases() => new string[] { "capbotcompat" };

        public override string Description() => "CapBot: per-mod conflict-engine compatibility status";

        public override string[] UsageExamples() => new string[] { "/capbotcompat", "/capbotcompat ExpandedGalaxy" };

        public override void Execute(string arguments)
        {
            if (PLNetworkManager.Instance == null || PLNetworkManager.Instance.LocalPlayer == null)
            {
                CapBotLog.Warning(CapBotLog.CORE, "/capbotcompat ignored: no local player yet");
                return;
            }
            if (!PhotonNetwork.isMasterClient)
            {
                PulsarModLoader.Utilities.Messaging.Notification("Must be host to see CapBot compat status!");
                return;
            }
            string mod = (arguments != null) ? arguments.Trim() : null;
            if (mod != null && mod.Length == 0) mod = null;
            try
            {
                List<string> lines = new List<string>();
                if (mod != null)
                {
                    lines.Add(mod + ": " + ConflictEngine.CompatStatus(mod));
                }
                else
                {
                    lines.Add("ConflictEngine: safeMode=" + (ConflictEngine.SafeMode ? "yes" : "no") +
                              " tracked=" + ConflictEngine.TrackedModCount);
                    foreach (PulsarModLoader.PulsarMod loaded in PulsarModLoader.ModManager.Instance.GetAllMods())
                    {
                        if (loaded == null || string.IsNullOrEmpty(loaded.Name)) continue;
                        lines.Add(loaded.Name + ": " + ConflictEngine.CompatStatus(loaded.Name));
                    }
                }
                for (int i = 0; i < lines.Count; i++)
                {
                    PulsarModLoader.Utilities.Messaging.Echo(
                        PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(), lines[i]);
                }
            }
            catch (System.Exception ex)
            {
                CapBotLog.Error(CapBotLog.CORE, "/capbotcompat failed", ex);
            }
        }
    }
}