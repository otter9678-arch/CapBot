using PulsarModLoader;
using PulsarModLoader.CustomGUI;
using UnityEngine;

namespace CapBot
{
    // Persistent, user-editable settings (PML SaveValue = saved per-mod automatically;
    // the menu is auto-discovered by PML's GUIMain via reflection).
    // Note: PULSAR's IMGUI lives in UnityEngine.IMGUIModule, which the csproj now references.
    internal static class Config
    {
        public static SaveValue<bool> CaptainBotEnabled = new SaveValue<bool>("CaptainBotEnabled", true);
        public static SaveValue<bool> SmartAIEnabled = new SaveValue<bool>("SmartAIEnabled", true);
        public static SaveValue<bool> MissionAutoDetectEnabled = new SaveValue<bool>("MissionAutoDetectEnabled", true);
        public static SaveValue<bool> AutoAssignCaptain = new SaveValue<bool>("AutoAssignCaptain", true);
        public static SaveValue<bool> ModUpdaterEnabled = new SaveValue<bool>("ModUpdaterEnabled", false);
        public static SaveValue<float> AIReactionSpeed = new SaveValue<float>("AIReactionSpeed", 0.1f);
        public static SaveValue<float> AIAccuracy = new SaveValue<float>("AIAccuracy", 0.85f);
        public static SaveValue<float> CombatEngageRange = new SaveValue<float>("CombatEngageRange", 50f);
        public static SaveValue<float> CombatDisengageHealth = new SaveValue<float>("CombatDisengageHealth", 0.2f);
        public static SaveValue<int> MinCreditsReserve = new SaveValue<int>("MinCreditsReserve", 500);

        // Mod-detection helpers (soft dependencies).
        private static bool? _betterAI;
        private static bool? _expandedGalaxy;
        private static bool? _exoticComponents;
        private static bool? _talentsMod;

        public static bool BetterAILoaded
        {
            get
            {
                if (!_betterAI.HasValue) _betterAI = SafeIsLoaded("Better AI") || SafeIsLoaded("BetterAI");
                return _betterAI.Value;
            }
        }
        public static bool ExpandedGalaxyLoaded
        {
            get
            {
                if (!_expandedGalaxy.HasValue) _expandedGalaxy = SafeIsLoaded("ExpandedGalaxy") || SafeIsLoaded("Expanded Galaxy");
                return _expandedGalaxy.Value;
            }
        }
        public static bool ExoticComponentsLoaded
        {
            get
            {
                if (!_exoticComponents.HasValue) _exoticComponents = SafeIsLoaded("Exotic Components") || SafeIsLoaded("ExoticComponents");
                return _exoticComponents.Value;
            }
        }
        public static bool TalentsModLoaded
        {
            get
            {
                if (!_talentsMod.HasValue) _talentsMod = SafeIsLoaded("Talents") || SafeIsLoaded("TalentsModPerformanceImprovement");
                return _talentsMod.Value;
            }
        }

        private static bool SafeIsLoaded(string name)
        {
            try { return PulsarModLoader.ModManager.Instance.IsModLoaded(name); }
            catch { return false; }
        }
    }

    internal class CapBotSettingsMenu : ModSettingsMenu
    {
        public override string Name() => "CapBot";

        public override void Draw()
        {
            if (GUILayout.Button("Captain Bot System: " + (Config.CaptainBotEnabled ? "Enabled" : "Disabled")))
                Config.CaptainBotEnabled.Value = !Config.CaptainBotEnabled;
            if (GUILayout.Button("Smart AI (all bots): " + (Config.SmartAIEnabled ? "Enabled" : "Disabled")))
                Config.SmartAIEnabled.Value = !Config.SmartAIEnabled;
            if (GUILayout.Button("Mission Auto-Detection: " + (Config.MissionAutoDetectEnabled ? "Enabled" : "Disabled")))
                Config.MissionAutoDetectEnabled.Value = !Config.MissionAutoDetectEnabled;
            if (GUILayout.Button("Auto-Assign Captain: " + (Config.AutoAssignCaptain ? "Enabled" : "Disabled")))
                Config.AutoAssignCaptain.Value = !Config.AutoAssignCaptain;
            if (GUILayout.Button("Mod Auto-Updater (every launch): " + (Config.ModUpdaterEnabled ? "Enabled" : "Disabled")))
                Config.ModUpdaterEnabled.Value = !Config.ModUpdaterEnabled;

            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUILayout.Label("AI Reaction Speed: " + Config.AIReactionSpeed.Value.ToString("0.0") + "s");
            Config.AIReactionSpeed.Value = GUILayout.HorizontalSlider(Config.AIReactionSpeed, 0.1f, 2f);
            GUILayout.Label("AI Accuracy: " + (Config.AIAccuracy * 100f).ToString("0") + "%");
            Config.AIAccuracy.Value = GUILayout.HorizontalSlider(Config.AIAccuracy, 0.3f, 1f);
            GUILayout.Label("Combat Engage Range: " + Config.CombatEngageRange.Value.ToString("0"));
            Config.CombatEngageRange.Value = GUILayout.HorizontalSlider(Config.CombatEngageRange, 10f, 120f);
            GUILayout.Label("Combat Disengage (flee) at hull: " + (Config.CombatDisengageHealth * 100f).ToString("0") + "%");
            Config.CombatDisengageHealth.Value = GUILayout.HorizontalSlider(Config.CombatDisengageHealth, 0.05f, 0.5f);
            GUILayout.Label("Min Credits Reserve: " + Config.MinCreditsReserve.Value);
            Config.MinCreditsReserve.Value = (int)GUILayout.HorizontalSlider(Config.MinCreditsReserve, 0, 20000);

            GUILayout.Space(8f);
            GUILayout.Label("Detected mods: " +
                (Config.BetterAILoaded ? "BetterAI " : "") +
                (Config.ExpandedGalaxyLoaded ? "ExpandedGalaxy " : "") +
                (Config.ExoticComponentsLoaded ? "ExoticComponents " : "") +
                (Config.TalentsModLoaded ? "Talents" : ""));
        }
    }
}