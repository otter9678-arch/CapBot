using PulsarModLoader;
using PulsarModLoader.Chat.Commands.CommandRouter;
using CapBot.Core.Logging;
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Assembly-CSharp")]
namespace CapBot
{
    public class Mod : PulsarMod
    {
        public override string Version => "Alpha 1.2.2";

        public override string Author => "otter9678-arch (original by pokegustavo; Alpha 1.2.x expansion inspired by the PULSAR-Modders community tools: PML by the PULSAR-Modders team, Better AI/Quality Improver/Exotic Components by pokegustavo, Talents framework by Mest/TheRealMesteven, TalentsModPerformanceImprovement by OnHyex)";

        public override string ShortDescription => "Adds a bot as the captain";

        public override string Name => "CapBot";

        public override string HarmonyIdentifier()
        {
            return "pokegustavo.CapBot";
        }

        public Mod()
        {
            // Boot-time: apply any mod DLLs staged by a previous /updateall run.
            ModUpdater.ApplyStagedUpdates();
            // Optional always-on check (off by default; /updateall works regardless).
            if (Config.ModUpdaterEnabled)
            {
                try
                {
                    string report = ModUpdater.UpdateAll();
                    CapBotLog.Info(CapBotLog.UPDATER, "Auto-update report:\n" + report);
                }
                catch (System.Exception ex)
                {
                    CapBotLog.Error(CapBotLog.UPDATER, "Auto-update check failed", ex);
                }
            }
        }
    }
}