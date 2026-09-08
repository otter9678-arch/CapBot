using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;
using CapBot.Core.Capabilities;
using CapBot.Core.Executor;
using CapBot.Core.Crew;
using CapBot.Core.Adjustment;
using CapBot.Core.Learning;
using CapBot.Core.Compatibility;

namespace CapBot.Core.Diagnostics
{
    // ---- Phase 29: status hub ---------------------------------------------------
    //
    // Single aggregation point for the read-only status surfaces built across
    // P2-P28. Collect(nowMs) gathers bounded diagnostic lines from every
    // registry/director/monitor that exposes StatusLines()/Lines()/counters,
    // in a FIXED deterministic order, each source individually fail-safe (a
    // faulting surface produces one fault line and never blocks the others —
    // the P26 monitor-handler / P27 compat-action pattern).
    //
    // Ownership argument: this layer AUTHORS NOTHING and MUTATES NOTHING. It
    // reads public readbacks only (the same counters the P18 calm gate and
    // the P24 observer read). Every line is bounded; the whole report is
    // capped (MaxLines); no game/PML/Harmony references (pure C# domain —
    // the P19 narrow-compile lesson). Consumers: /capbotstatus (chat), the
    // settings-menu summary (Config.cs), and later phases' status needs.
    public static class StatusHub
    {
        public const int MaxLines = 128;   // hard cap on the whole report (bounded by construction)

        private static readonly List<string> m_Faults = new List<string>(4);

        // Collects the full bounded status report. nowMs feeds the surfaces
        // that take a timestamp (TaskRegistry/CapabilityRegistry). Never
        // throws; never fabricates values (a faulting source contributes one
        // "StatusFault" line instead of invented data).
        public static List<string> Collect(int nowMs)
        {
            List<string> lines = new List<string>(64);
            lock (m_Faults) m_Faults.Clear();

            // Header
            AddSource(lines, "hub", delegate
            {
                lines.Add("CapBot status (v" + StatusVersion() + ") lines<=" + MaxLines);
            });

            // ---- pipeline (P2-P8) ------------------------------------------------
            AddSource(lines, "registry", delegate
            {
                lines.Add("tasks live=" + TaskRegistry.LiveCount + " history=" + TaskRegistry.HistoryCount);
                AddAll(lines, TaskRegistry.StatusLines(nowMs));
            });
            AddSource(lines, "recovery", delegate
            {
                lines.Add("recovery tracked=" + TaskRecoveryManager.TrackedCount
                    + " stalledReports=" + TaskRecoveryManager.StallReportCount);
            });
            AddSource(lines, "scheduler", delegate
            {
                lines.Add("scheduler grants=" + TaskScheduler.ActiveGrantCount);
            });
            AddSource(lines, "claims", delegate
            {
                lines.Add("claims live=" + ExecutionClaims.LiveClaimCount
                    + " ledger=" + ExecutionClaims.Ledger.EntryCount);
            });
            AddSource(lines, "executor", delegate
            {
                lines.Add("executor ticks=" + TaskExecutor.TickCallCount
                    + " attempts=" + TaskExecutor.AttemptCount
                    + " enabled=" + (TaskExecutor.Enabled ? "yes" : "no"));
            });

            // ---- world (P6) -------------------------------------------------------
            AddSource(lines, "world", delegate
            {
                WorldSnapshotFreshness f = WorldStateService.GetFreshness(nowMs);
                lines.Add("world freshness=" + f);
            });

            // ---- directors (P9-P25) ----------------------------------------------
            AddAll(lines, SafeLines("emergency", delegate { return CapBot.Core.Emergency.EmergencyDirector.StatusLines(); }));
            AddAll(lines, SafeLines("agents", delegate { return CrewAgentRegistry.StatusLines(); }));
            AddAll(lines, SafeLines("personalities", delegate { return CrewPersonalityRegistry.StatusLines(); }));
            AddAll(lines, SafeLines("experience", delegate { return CrewExperienceRegistry.StatusLines(); }));
            AddAll(lines, SafeLines("memory", delegate { return CrewMemorySystem.StatusLines(); }));
            AddAll(lines, SafeLines("navigation", delegate { return CapBot.Core.Navigation.NavigationRecoveryDirector.StatusLines(); }));
            AddAll(lines, SafeLines("missions", delegate { return CapBot.Core.Missions.MissionDirector.StatusLines(); }));
            AddAll(lines, SafeLines("economy", delegate { return CapBot.Core.Economy.EconomyDirector.StatusLines(); }));
            AddAll(lines, SafeLines("combat", delegate { return CapBot.Core.Combat.CombatDirector.StatusLines(); }));
            AddAll(lines, SafeLines("captain", delegate { return CapBot.Core.Captain.CaptainDirector.StatusLines(); }));
            AddAll(lines, SafeLines("validator", delegate { return CapBot.Core.Validation.DecisionValidator.StatusLines(); }));
            AddAll(lines, SafeLines("planning", delegate { return CapBot.Core.Planning.PlanningDirector.StatusLines(); }));
            AddAll(lines, SafeLines("missionwork", delegate { return CapBot.Core.Planning.MissionWorkDirector.StatusLines(); }));
            AddAll(lines, SafeLines("adjustment", delegate { return AdjustmentDirector.StatusLines(); }));
            AddAll(lines, SafeLines("learning", delegate { return AdaptiveLearningDirector.StatusLines(); }));
            AddAll(lines, SafeLines("mpmonitor", delegate { return MultiplayerAuthorityMonitor.StatusLines(); }));

            // ---- advisors (P20/P21; recommend-only) ------------------------------
            AddAll(lines, SafeLines("ollama", delegate { return CapBot.Core.Ollama.OllamaAdvisor.StatusLines(); }));
            AddAll(lines, SafeLines("crewadvisor", delegate { return CapBot.Core.Qwen.CrewAdvisor.StatusLines(); }));

            // ---- compat (P27) ------------------------------------------------------
            AddAll(lines, SafeLines("compat", delegate { return CompatManager.StatusLines(); }));

            // Capability registry needs nowMs.
            AddAll(lines, SafeLines("capabilities", delegate { return CapabilityRegistry.StatusLines(nowMs); }));

            // Truncate to the hard cap (deterministic: first lines win).
            if (lines.Count > MaxLines)
            {
                lines.RemoveRange(MaxLines, lines.Count - MaxLines);
                lines.Add("(status truncated at " + MaxLines + " lines)");
            }
            return lines;
        }

        // Fixed identity for the header (no reflection, no assembly version —
        // the mod version string lives in Mod.cs and is not read here).
        private static string StatusVersion()
        {
            return "P29";
        }

        private static void AddSource(List<string> lines, string name, Action emit)
        {
            try { emit(); }
            catch (Exception ex)
            {
                lines.Add("StatusFault " + name + " err=" + ex.GetType().Name);
            }
        }

        private static List<string> SafeLines(string name, Func<List<string>> source)
        {
            try
            {
                List<string> got = source();
                return got != null ? got : new List<string>();
            }
            catch (Exception ex)
            {
                List<string> fault = new List<string>(1);
                fault.Add("StatusFault " + name + " err=" + ex.GetType().Name);
                return fault;
            }
        }

        private static void AddAll(List<string> lines, List<string> more)
        {
            if (more == null) return;
            for (int i = 0; i < more.Count; i++) lines.Add(more[i]);
        }
    }
}