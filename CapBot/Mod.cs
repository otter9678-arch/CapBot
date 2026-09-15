using PulsarModLoader;
using UnityEngine;
using CapBot.UI.IMGUI;

namespace CapBot
{
    public class Mod : PulsarMod
    {
        public override string Version => "1.0.0";
        public override string Author => "Tom";
        public override string Name => "CapBot";

        public override void OnApplicationStart()
        {
            var harmony = new HarmonyLib.Harmony("CapBot.Patch");
            harmony.PatchAll();
        }

        public override void OnGUI()
        {
            IMGUI_CommandWheel.Instance.Draw();
            IMGUI_TalentTree.Instance.Draw();
            IMGUI_DebugConsole.Instance.Draw();
            IMGUI_BehaviorEditor.Instance.Draw();
            IMGUI_ScriptingConsole.Instance.Draw();
            IMGUI_CrewManagement.Instance.Draw();
        }

        public override void Update()
        {
            if (Input.GetKeyDown(KeyCode.Q))
                IMGUI_CommandWheel.Instance.Toggle();

            if (Input.GetKeyDown(KeyCode.T))
                IMGUI_TalentTree.Instance.Toggle();

            if (Input.GetKeyDown(KeyCode.F1))
                IMGUI_DebugConsole.Instance.Toggle();

            if (Input.GetKeyDown(KeyCode.F2))
                IMGUI_BehaviorEditor.Instance.Toggle();

            if (Input.GetKeyDown(KeyCode.F3))
                IMGUI_ScriptingConsole.Instance.Toggle();

            if (Input.GetKeyDown(KeyCode.U))
                IMGUI_CrewManagement.Instance.Toggle();
        }
    }
}