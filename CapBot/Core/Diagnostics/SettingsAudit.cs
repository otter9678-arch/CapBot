using System.Collections.Generic;

namespace CapBot.Core.Diagnostics
{
    // ---- P53: the settings-audit truth table (§15–19) --------------------------
    //
    // Single source of truth for every SaveValue CapBot declares: LIVE (a
    // compile-verified gameplay consumer exists), GATED (consumed only when
    // another setting enables its consumer), or DEAD (the menu shows it, the
    // value persists, but NOTHING reads it — audit H4, grep-verified).
    //
    // The /capbotsettings command renders this table; the SettingsAuditTests
    // suite pins it (a knob that gains a consumer without flipping its row
    // here FAILS the drift check; a knob reported LIVE without a consumer
    // FAILS the invented-liveness check). Pure C#: no game/PML references,
    // never throws, bounded.
    public static class SettingsAudit
    {
        public const string RuntimeLive = "LIVE";
        public const string RuntimeDead = "DEAD";
        public const string RuntimeGated = "GATED";

        public sealed class SettingRow
        {
            public readonly string Name;        // stable SaveValue key
            public readonly string Runtime;     // LIVE / DEAD / GATED
            public readonly string Consumer;    // where the value is read, or NONE
            public readonly string Effective;   // what actually gates behavior

            public SettingRow(string name, string runtime, string consumer, string effective)
            {
                Name = name; Runtime = runtime; Consumer = consumer; Effective = effective;
            }
        }

        // The audit table in fixed config order (matches Config.cs field
        // order so the report reads the same as the menu).
        public static readonly SettingRow[] Rows = new SettingRow[]
        {
            new SettingRow("CaptainBotEnabled", RuntimeLive,
                "Autonomy.OnTick (talents/research/inventory)", "wired"),
            new SettingRow("SmartAIEnabled", RuntimeLive,
                "Autonomy.OnTick (SmartItemUse)", "wired"),
            new SettingRow("MissionAutoDetectEnabled", RuntimeLive,
                "Autonomy.OnTick (economy/campaign/missions)", "wired"),
            new SettingRow("AutoAssignCaptain", RuntimeDead,
                "NONE (no consumer; captain bot spawn is class-driven)", "not wired (no consumer)"),
            new SettingRow("ModUpdaterEnabled", RuntimeLive,
                "Mod boot (ModUpdater.UpdateAll, off by default)", "wired"),
            new SettingRow("VerboseLogging", RuntimeLive,
                "CapBotLog level gate", "wired"),
            new SettingRow("OllamaAdvisorEnabled", RuntimeLive,
                "OllamaAdvisor.ApplyConfig -> Evaluate gate", "wired"),
            new SettingRow("OllamaModel", RuntimeLive,
                "advisors request model (P44 owner pin: qwen3:latest at boot)", "wired"),
            new SettingRow("OllamaPort", RuntimeLive,
                "OllamaAdvisor endpoint (clamped 1..65535)", "wired"),
            new SettingRow("QwenAdvisorEnabled", RuntimeLive,
                "CrewAdvisor.ApplyConfig -> Evaluate gate", "wired"),
            new SettingRow("AIReactionSpeed", RuntimeDead,
                "NONE (legacy never read it)", "not wired (no consumer)"),
            new SettingRow("AIAccuracy", RuntimeDead,
                "NONE (legacy never read it)", "not wired (no consumer)"),
            new SettingRow("CombatEngageRange", RuntimeDead,
                "NONE (P17: range logic out of scope, no verified position read)", "not wired (no consumer)"),
            new SettingRow("CombatDisengageHealth", RuntimeDead,
                "NONE (legacy blind-jump flee uses hardcoded 0.2 hull floor)", "not wired (no consumer)"),
            new SettingRow("MinCreditsReserve", RuntimeDead,
                "NONE (P16 reports legacy hardcoded reserve 2500 as DATA)", "not wired (no consumer)"),
        };

        // Dead-knob keys (the drift/coverage contract the tests pin).
        public static readonly string[] DeadSettings = new string[]
        {
            "AutoAssignCaptain", "AIReactionSpeed", "AIAccuracy",
            "CombatEngageRange", "CombatDisengageHealth", "MinCreditsReserve",
        };

        public static SettingRow GetRow(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < Rows.Length; i++)
                if (string.Equals(Rows[i].Name, name, System.StringComparison.Ordinal)) return Rows[i];
            return null;
        }

        public static int DeadCount()
        {
            int n = 0;
            for (int i = 0; i < Rows.Length; i++)
                if (Rows[i].Runtime == RuntimeDead) n++;
            return n;
        }
    }
}