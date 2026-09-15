using CapBot.AI;
using CapBot.Networking;
using HarmonyLib;
using UnityEngine;

namespace CapBot
{
    // Drives the CapBot meta-layer (XP, sync, UI hotkeys, IMGUI) from game hooks,
    // because PML mods have no Unity lifecycle of their own.
    [HarmonyPatch(typeof(PLGlobal), "Update")]
    internal static class CapBotTicker
    {
        private static GameObject _host;

        internal static void EnsureHost()
        {
            if (_host != null) return;
            _host = new GameObject("CapBot_TickerHost");
            Object.DontDestroyOnLoad(_host);
            _host.AddComponent<CapBotTickerBehaviour>();
        }

        static void Postfix()
        {
            EnsureHost();
            NetworkSyncManager.PollEvents();
            XPEvents.PollGameEvents();
        }
    }

    [HarmonyPatch(typeof(PLGlobal), "EnterNewGame")]
    internal static class CapBotSessionReset
    {
        static void Postfix()
        {
            NetworkSyncManager.Reset();
            XPEvents.Reset();
        }
    }

    public class CapBotTickerBehaviour : MonoBehaviour
    {
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6)) UI.IMGUI.IMGUI_CommandWheel.Instance.Toggle();
            if (Input.GetKeyDown(KeyCode.F7)) UI.IMGUI.IMGUI_TalentTree.Instance.Toggle();
            if (Input.GetKeyDown(KeyCode.F8)) UI.IMGUI.IMGUI_DebugConsole.Instance.Toggle();
            if (Input.GetKeyDown(KeyCode.F2)) UI.IMGUI.IMGUI_BehaviorEditor.Instance.Toggle();
            if (Input.GetKeyDown(KeyCode.F3)) UI.IMGUI.IMGUI_ScriptingConsole.Instance.Toggle();
            if (Input.GetKeyDown(KeyCode.U)) UI.IMGUI.IMGUI_CrewManagement.Instance.Toggle();
        }

        private void OnGUI()
        {
            UI.IMGUI.IMGUI_CommandWheel.Instance.Draw();
            UI.IMGUI.IMGUI_TalentTree.Instance.Draw();
            UI.IMGUI.IMGUI_DebugConsole.Instance.Draw();
            UI.IMGUI.IMGUI_BehaviorEditor.Instance.Draw();
            UI.IMGUI.IMGUI_ScriptingConsole.Instance.Draw();
            UI.IMGUI.IMGUI_CrewManagement.Instance.Draw();
        }

        private void OnDestroy()
        {
            // Harmony postfix will recreate the host on the next game frame.
        }
    }
}