using PulsarModLoader;
using PulsarModLoader.Chat.Commands.CommandRouter;
using CapBot.Core.Logging;
using CapBot.Core.Tasks;
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
            // Phase 2: attach task-lifecycle logging (infrastructure only; no
            // gameplay routes through the task system yet).
            TaskLogBridge.Ensure();
            // Phase 3: attach recovery logging (policy layer only; the recovery
            // manager stays inert until Phase 6 supplies a real world probe and
            // a tick driver — no gameplay routes through recovery yet).
            RecoveryLogBridge.Ensure();
            // Phase 4: attach scheduler logging (orchestration only; the
            // scheduler stays inert until Phase 8 drives Tick host-side — it
            // executes nothing and decides nothing about gameplay).
            SchedulerLogBridge.Ensure();
            // Phase 5: attach execution-claim logging (protection layer only;
            // claims are deny-by-default until Phase 8 wires the authority
            // policy to master-client state — nothing can claim or execute).
            ClaimLogBridge.Ensure();
            // Phase 6: attach world-state logging and the read-oriented world
            // observation layer. The service is dormant until the frame tick
            // (WorldTick patch) calls Refresh; the snapshot probe is attached
            // as recovery's real world probe but recovery still has no tick
            // driver — no gameplay routes through world state yet.
            CapBot.Core.World.WorldLogBridge.Ensure();
            CapBot.Core.World.WorldStateService.SetSource(new CapBot.Core.World.PulsarWorldSource());
            TaskRecoveryManager.Probe = new CapBot.Core.World.WorldSnapshotProbe();
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