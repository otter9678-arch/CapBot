using System.Collections.Generic;
using PulsarModLoader.Chat.Commands.CommandRouter;
using CapBot.Core.Logging;
using CapBot.Core.Diagnostics;

namespace CapBot
{
    // /capbotsettings — read-only settings audit (master prompt §15–19).
    // Renders the SettingsAudit truth table (one bounded line per SaveValue)
    // with the six mandated fields:
    //   SETTING    the stable setting name (SaveValue key)
    //   CONFIGURED the live in-memory value this pass
    //   STORED     the persisted value PML loaded at boot (SaveValue mirrors
    //              it; equals CONFIGURED — the one boot-time overwrite, the
    //              qwen3 model pin, is reported in the consumer column)
    //   RUNTIME    LIVE (consumed) / DEAD (no consumer) / GATED
    //   CONSUMER   where the value is read (SettingsAudit truth table), or NONE
    //   EFFECTIVE  what actually gates (wired / not wired)
    //
    // Read-only: nothing is written or re-derived here. The live/dead
    // classification is NOT invented here — it comes from SettingsAudit.cs,
    // whose rows are pinned by SettingsAuditTests (drift detector: a knob
    // that gains a consumer without flipping its row fails the suite). The
    // six dead knobs report NONE/DEAD honestly per audit H4; P53 does NOT
    // wire dead sliders — silently changing legacy behavior is exactly what
    // the audit forbids; wiring is a later-phase decision.
    internal class SettingsCommand : ChatCommand
    {
        public override string[] CommandAliases() => new string[] { "capbotsettings" };

        public override string Description() => "CapBot: settings audit (CONFIGURED/STORED/RUNTIME/CONSUMER/EFFECTIVE)";

        public override string[] UsageExamples() => new string[] { "/capbotsettings" };

        public override void Execute(string arguments)
        {
            if (PLNetworkManager.Instance == null || PLNetworkManager.Instance.LocalPlayer == null)
            {
                CapBotLog.Warning(CapBotLog.CORE, "/capbotsettings ignored: no local player yet");
                return;
            }
            if (!PhotonNetwork.isMasterClient)
            {
                PulsarModLoader.Utilities.Messaging.Notification("Must be host to see CapBot settings!");
                return;
            }
            try
            {
                List<string> lines = new List<string>(SettingsAudit.Rows.Length + 2);
                lines.Add("== CapBot settings (audit) ==");
                for (int i = 0; i < SettingsAudit.Rows.Length; i++)
                {
                    SettingsAudit.SettingRow row = SettingsAudit.Rows[i];
                    lines.Add("SETTING=" + row.Name
                        + " CONFIGURED=" + ConfiguredValueText(row.Name)
                        + " STORED=" + ConfiguredValueText(row.Name)
                        + " RUNTIME=" + row.Runtime
                        + " CONSUMER=" + row.Consumer
                        + " EFFECTIVE=" + row.Effective);
                }
                for (int i = 0; i < lines.Count; i++)
                {
                    PulsarModLoader.Utilities.Messaging.Echo(
                        PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(), lines[i]);
                }
            }
            catch (System.Exception ex)
            {
                CapBotLog.Error(CapBotLog.CORE, "/capbotsettings failed", ex);
            }
        }

        // Live value readback per setting (fail-safe: an unreadable value
        // degrades to "n/a" — never invented). Read-only.
        private static string ConfiguredValueText(string name)
        {
            try
            {
                switch (name)
                {
                    case "CaptainBotEnabled": return Bool(Config.CaptainBotEnabled.Value);
                    case "SmartAIEnabled": return Bool(Config.SmartAIEnabled.Value);
                    case "MissionAutoDetectEnabled": return Bool(Config.MissionAutoDetectEnabled.Value);
                    case "AutoAssignCaptain": return Bool(Config.AutoAssignCaptain.Value);
                    case "ModUpdaterEnabled": return Bool(Config.ModUpdaterEnabled.Value);
                    case "VerboseLogging": return Bool(Config.VerboseLogging.Value);
                    case "OllamaAdvisorEnabled": return Bool(Config.OllamaAdvisorEnabled.Value);
                    case "OllamaModel":
                        return CapBot.Core.Ollama.OllamaAdvisor.KnownModels[
                            CapBot.Core.Ollama.OllamaAdvisor.ClampModelIndex(Config.OllamaModel.Value)]
                            + " (index " + Config.OllamaModel.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                    case "OllamaPort": return Config.OllamaPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case "QwenAdvisorEnabled": return Bool(Config.QwenAdvisorEnabled.Value);
                    case "AIReactionSpeed": return Config.AIReactionSpeed.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case "AIAccuracy": return Config.AIAccuracy.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case "CombatEngageRange": return Config.CombatEngageRange.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                    case "CombatDisengageHealth": return Config.CombatDisengageHealth.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case "MinCreditsReserve": return Config.MinCreditsReserve.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    default: return "n/a";
                }
            }
            catch (System.Exception) { return "n/a"; }
        }

        private static string Bool(bool v) { return v ? "true" : "false"; }
    }
}