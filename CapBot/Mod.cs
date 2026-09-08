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
            // Phase 9: attach emergency-director logging and wire its seams.
            // The director stays INERT until the tick driver (WorldTick) calls
            // Evaluate host-side; with the authority probe it is deny-by-default
            // (clients never produce emergency tasks). Emergency work still
            // routes through the scheduler (P4), claims (P5), capability
            // validation (P7) and the executor (P8) — nothing executes here.
            CapBot.Core.Emergency.EmergencyLogBridge.Ensure();
            CapBot.Core.Emergency.EmergencyDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Emergency.EmergencyDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Emergency.EmergencyDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            // Phase 10: attach crew-agent-registry logging and wire its seams.
            // The registry stays INERT until the tick driver (WorldTick) calls
            // Sync host-side; with the authority probe it is deny-by-default
            // (clients never build authoritative agent state). Agents observe
            // tasks read-only (TaskRegistry) and hold assignment metadata only
            // — no tasks are created, claimed, or executed here, and no PULSAR
            // world state is modified. Role names resolve through the game's
            // verified static public naming channel only (data only).
            CapBot.Core.Crew.CrewAgentLogBridge.Ensure();
            CapBot.Core.Crew.CrewAgentRegistry.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Crew.CrewAgentRegistry.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Crew.CrewAgentRegistry.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            CapBot.Core.Crew.CrewAgentRegistry.SetRoleNameResolver(delegate(int classId)
            {
                try { return PLPlayer.GetClassNameFromID(classId); }
                catch (System.Exception) { return null; }
            });
            // Phase 11: attach personality-registry logging. The personality
            // layer is INERT data-only: records are derived on demand from the
            // stable agent identity (no tick driver, no world reads, no task /
            // claim / execution influence). Consumers are later phases.
            CapBot.Core.Crew.PersonalityLogBridge.Ensure();
            // Phase 12: attach experience-registry logging. Experience accrues
            // only from the agent registry's ClearTask funnel (fail-safe, fired
            // outside the agent lock) and stays DATA ONLY — it never influences
            // scheduling, claims, execution, or personalities.
            CapBot.Core.Crew.ExperienceLogBridge.Ensure();
            // Phase 13: attach crew-memory logging and wire the recall clock
            // seam. Memory is bounded DATA ONLY — it records task outcomes
            // (via the ClearTask funnel, fail-safe outside the agent lock)
            // and locations/crew events (explicit APIs for later phases); it
            // never influences scheduling, claims, execution, personalities,
            // experience, or world state.
            CapBot.Core.Crew.MemoryLogBridge.Ensure();
            CapBot.Core.Crew.CrewMemorySystem.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            // Phase 14: attach navigation-recovery logging and wire its seams.
            // The director stays INERT until the tick driver (WorldTick) calls
            // Evaluate host-side; with the authority probe it is deny-by-default
            // (clients never produce recovery plans). Plans are DATA: tasks are
            // created through the registry and executed ONLY through the
            // scheduler (P4) / claims (P5) / capability validation (P7) /
            // executor (P8) pipeline — the director never executes a
            // capability, never RPCs, and never touches the vanilla navigation
            // stack (stuck-teleport and PLFlightAI remain vanilla-owned).
            CapBot.Core.Navigation.NavigationLogBridge.Ensure();
            CapBot.Core.Navigation.NavigationRecoveryDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Navigation.NavigationRecoveryDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Navigation.NavigationRecoveryDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            // Phase 15: attach mission-director logging and wire its seams.
            // The director is REPORT-ONLY (Phase 15 contract): it tracks mission
            // transitions from the P6 snapshot and emits bounded diagnostics —
            // it creates NO tasks (no mission capability exists in the P7
            // catalog, and a task without CapabilityId metadata fails at start
            // per the P8 executor contract). Deny-by-default authority keeps
            // clients silent.
            CapBot.Core.Missions.MissionLogBridge.Ensure();
            CapBot.Core.Missions.MissionDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Missions.MissionDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Missions.MissionDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
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