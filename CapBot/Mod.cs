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
            // Phase 16: attach economy-director logging and wire its seams.
            // The director is REPORT-ONLY (Phase 16 contract): it tracks the
            // credits picture, shop-sector presence, fuel/coolant affordability
            // and warp-toll inaffordability from the P6 snapshot (+ the Phase 16
            // additive unit-price capture) and emits bounded diagnostics — it
            // creates NO tasks (no economy capability exists in the P7 catalog,
            // and a task without CapabilityId metadata fails at start per the
            // P8 executor contract). The shop-sector classifier rides the same
            // compile-proven ESectorVisualIndication list shipped Patch.cs uses
            // to gate its own shop behavior; deny-by-default keeps clients
            // silent.
            CapBot.Core.Economy.EconomyLogBridge.Ensure();
            CapBot.Core.Economy.EconomyDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Economy.EconomyDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Economy.EconomyDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            CapBot.Core.Economy.EconomyDirector.SetShopSectorClassifier(delegate (int visualIndication)
            {
                ESectorVisualIndication v = (ESectorVisualIndication)visualIndication;
                return v == ESectorVisualIndication.GENERAL_STORE || v == ESectorVisualIndication.EXOTIC1 || v == ESectorVisualIndication.EXOTIC2 || v == ESectorVisualIndication.EXOTIC3 || v == ESectorVisualIndication.EXOTIC4
                    || v == ESectorVisualIndication.EXOTIC5 || v == ESectorVisualIndication.EXOTIC6 || v == ESectorVisualIndication.EXOTIC7 || v == ESectorVisualIndication.AOG_HUB || v == ESectorVisualIndication.GENTLEMEN_START || v == ESectorVisualIndication.CORNELIA_HUB
                    || v == ESectorVisualIndication.COLONIAL_HUB || v == ESectorVisualIndication.WD_START || v == ESectorVisualIndication.SPACE_SCRAPYARD || v == ESectorVisualIndication.FLUFFY_FACTORY_01 || v == ESectorVisualIndication.FLUFFY_FACTORY_02 || v == ESectorVisualIndication.FLUFFY_FACTORY_03 || v == ESectorVisualIndication.SPACE_CAVE_2;
            });
            // Phase 17: attach combat-director logging and wire its seams.
            // The director is REPORT-ONLY by MANDATE (Phase 17 contract): a
            // combat-adjacent capability EXISTS (SET_CAPTAIN_TARGET, P7) but
            // its authorship is owned by the P9 emergency director and by the
            // legacy captain tick. The director tracks the engagement picture
            // (hostile set, combat-level gap label, warp-combat, under-fire,
            // boarders) from the P6 snapshot (+ the Phase 17 additive
            // InvadersOnboard/took-damage-recently capture) and emits bounded
            // diagnostics — it creates NO tasks, never fires, never sets
            // targets, and never assigns severity. Deny-by-default authority
            // keeps clients silent.
            CapBot.Core.Combat.CombatLogBridge.Ensure();
            CapBot.Core.Combat.CombatDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Combat.CombatDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Combat.CombatDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            // Phase 18: attach captain-deliberation ("Captain Brain 2.0") logging
            // and wire its seams. The director is a CONSUMER of the P6 snapshot
            // and the P9/P14/P15/P16/P17 director readbacks that authors EXACTLY
            // ONE task family (CAPTAIN_DELIB) bound to EXACTLY ONE capability —
            // ISSUE_MOVE_ORDER, the only capability with zero in-tree authors
            // (ownership argument in CaptainDirector.cs). Tasks flow through the
            // scheduler (P4) / claims (P5) / capability validation (P7) /
            // executor (P8) pipeline — the director never RPCs, never executes,
            // and only authors under a fail-closed calm gate (no P9 emergency,
            // no P14 plan, no P17 combat record, no hostiles/boarders/warp).
            // Deny-by-default authority keeps clients silent.
            CapBot.Core.Captain.CaptainLogBridge.Ensure();
            CapBot.Core.Captain.CaptainDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Captain.CaptainDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Captain.CaptainDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            // ---- Phase 19: decision validator (diagnostics-only pre-dispatch screen) ----
            // Ownership scope: reviews QUEUED capability-bound tasks BEFORE
            // scheduler Tick; emits bounded diagnostics only — it never
            // mutates lifecycle (P3 recovery owns cancel/fail/pause) and
            // never re-runs P7/P3/P5 gates. Fail-open on uncertainty,
            // fail-closed on action (holds no task records).
            CapBot.Core.Validation.DecisionLogBridge.Ensure();
            CapBot.Core.Validation.DecisionValidator.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Validation.DecisionValidator.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Validation.DecisionValidator.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            // ---- Phase 20: Ollama advisor (optional, sandboxed, RECOMMEND-ONLY) ----
            // Ownership scope: asks a LOCAL Ollama server (loopback only — the
            // host is hard-anchored to 127.0.0.1, only the port/model are
            // configurable) for one advisory line about the crew picture and
            // logs it. The advice is DATA ONLY: it never creates, queues,
            // cancels or mutates any task, never feeds the P9/P14/P18
            // deterministic decisions, and never reaches a capability or the
            // executor. Off by default (Config.OllamaAdvisorEnabled=false);
            // with the transport seam unset the advisor is inert by
            // construction. HTTP runs on advisor worker threads (never the
            // Unity main thread; requests are hard-timeout bounded and
            // loopback-only — the ModUpdater C1 pattern is deliberately
            // inverted). Deterministic rules always override the advisor.
            CapBot.Core.Ollama.AdvisorLogBridge.Ensure();
            CapBot.Core.Ollama.OllamaAdvisor.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Ollama.OllamaAdvisor.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Ollama.OllamaAdvisor.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            CapBot.Core.Ollama.OllamaAdvisor.SetTransport(new CapBot.Core.Ollama.OllamaHttpTransport(Config.OllamaPort.Value));
            CapBot.Core.Ollama.OllamaAdvisor.ApplyConfig(
                Config.OllamaAdvisorEnabled, Config.OllamaPort.Value, Config.OllamaModel.Value);
            // ---- Phase 21: crew advisor (Qwen integration, RECOMMEND-ONLY) ----
            // Crew-domain completion of the P20 advisor: reads the P10 crew
            // agent hooks (Role/RoleName, LastTaskOutcome, LastKnownTLIName)
            // through the additive AgentViews() readback and asks the SAME
            // local server (loopback-only, shared port/model config) for one
            // advisory line about the crew picture. The advice is DATA ONLY:
            // it never assigns tasks (CrewAgentRegistry assignment APIs are
            // untouched), never mutates any task pipeline state, never feeds
            // the deterministic directors. Off by default
            // (Config.QwenAdvisorEnabled=false); inert with no transport;
            // deterministic rules always override the advisor.
            CapBot.Core.Qwen.CrewAdvisorLogBridge.Ensure();
            CapBot.Core.Qwen.CrewAdvisor.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Qwen.CrewAdvisor.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Qwen.CrewAdvisor.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            CapBot.Core.Qwen.CrewAdvisor.SetTransport(new CapBot.Core.Ollama.OllamaHttpTransport(Config.OllamaPort.Value));
            CapBot.Core.Qwen.CrewAdvisor.ApplyConfig(Config.QwenAdvisorEnabled, Config.OllamaPort.Value, Config.OllamaModel.Value);
            // ---- Phase 22: planning director (deterministic situation assessment) ----
            // Ownership scope: a bounded deterministic CONSUMER of the P6
            // snapshot that tracks planning situations as data (premise drift,
            // calm-gated mission-work episode) and emits bounded decision
            // lines. It authors NOTHING in Phase 22 — no CapBotTask.Create /
            // Try* / Register calls, no scheduler/recovery/claims/validator
            // invocations; it reads the same public readbacks the P18 calm
            // gate already reads. Always-on by construction (no config
            // toggle — the P18 deterministic-director precedent); the
            // deny-by-default authority seam keeps clients silent, and P23
            // (dynamic task creation) builds on this layer.
            CapBot.Core.Planning.PlanningLogBridge.Ensure();
            CapBot.Core.Planning.PlanningDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Planning.PlanningDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Planning.PlanningDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            // ---- Phase 23: mission work director (dynamic task creation) ----
            // The authoring stage of the planning arc: consumes the P22
            // MISSIONWORK episode surface and authors EXACTLY ONE task family
            // bound to EXACTLY ONE capability — ISSUE_MOVE_ORDER (the
            // ownership argument was re-audited this phase; see
            // docs/MISSION_WORK_DIRECTOR.md). Authoring flows through the
            // P2-P8 pipeline only (Create -> SetMetadata -> Register ->
            // TryQueue); the director never RPCs and never touches scheduler,
            // claims, or other systems' tasks. Always-on by construction (no
            // config toggle — the P18/P22 deterministic-director precedent);
            // the deny-by-default authority seam keeps clients silent.
            CapBot.Core.Planning.MissionWorkLogBridge.Ensure();
            CapBot.Core.Planning.MissionWorkDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Planning.MissionWorkDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Planning.MissionWorkDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
            // ---- Phase 24: adjustment observer (bounded outcome readback) ----
            // The self-adjustment phase as DATA: polls the pipeline's PUBLIC
            // readbacks (TaskRegistry live snapshot, CaptainDirector /
            // PlanningDirector bounded counters) on its own cadence and emits
            // bounded recommend-only signals (CHURN / STARVE / DRIFT). It
            // authors NOTHING, mutates NO task, and never touches another
            // phase's knobs — P3 recovery owns every lifecycle decision. No
            // config toggle (the P18/P22/P23 deterministic-director
            // precedent); the deny-by-default authority seam keeps clients
            // silent. Poll-based by construction: every listener seam in the
            // tree is single-slot and boot-occupied by LogBridges.
            CapBot.Core.Adjustment.AdjustmentLogBridge.Ensure();
            CapBot.Core.Adjustment.AdjustmentDirector.SetAuthorityProbe(ExecutionClaims.IsAuthoritative);
            CapBot.Core.Adjustment.AdjustmentDirector.SetNowMsProvider(delegate { return TaskClock.NowMs; });
            CapBot.Core.Adjustment.AdjustmentDirector.SetWorldProvider(delegate { return CapBot.Core.World.WorldStateService.Latest; });
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