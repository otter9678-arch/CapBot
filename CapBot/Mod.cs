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
            // Phase 7: attach capability-registry logging, register the
            // built-in capability catalog and wire production seams. This is
            // contracts only — nothing validates, executes or performs
            // gameplay yet: no executor exists, the authority seam stays
            // fail-closed (claims deny-by-default) until Phase 8 wires it
            // to master-client state, and the scheduler is still inert.
            CapBot.Core.Capabilities.CapabilityLogBridge.Ensure();
            CapBot.Core.Capabilities.RegisteredCapabilities.RegisterBuiltIns();
            CapBot.Core.Capabilities.RegisteredCapabilities.AttachProductionSeams();
            // Phase 8: attach executor logging, attach the static
            // code-reviewed capability dispatcher and wire the claims
            // authority policy to master-client state (fail-closed: any
            // fault querying PhotonNetwork denies authority — clients never
            // execute; the vanilla request->master pattern is untouched).
            // The layer remains INERT until tasks exist: nothing creates
            // tasks until the P9+ directors, and the tick driver only runs
            // host-side (Patch.cs WorldTick gate).
            CapBot.Core.Executor.ExecutorLogBridge.Ensure();
            CapBot.Core.Executor.TaskExecutor.SetDispatcher(new CapBot.Core.Executor.PulsarCapabilityDispatcher());
            ExecutionClaims.SetAuthorityPolicy(delegate
            {
                try { return PhotonNetwork.isMasterClient; }
                catch (System.Exception) { return false; }
            });
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