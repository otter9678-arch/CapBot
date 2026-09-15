using CapBot.AI;
using CapBot.Personality;
using System;
using UnityEngine;

namespace CapBot.UI.IMGUI
{
    public class IMGUI_DebugConsole : IMGUIBase
    {
        public static IMGUI_DebugConsole Instance = new IMGUI_DebugConsole();

        protected override string GetTitle() => "AI Debug Console";

        protected override void DrawWindow(int id)
        {
            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0)
                {
                    CapBot bot = AIRegistry.Get(p);
                    var pers = PersonalityManager.Get(bot);

                    GUILayout.Label($"=== {p.GetPlayerName()} ===");
                    GUILayout.Label($"Role: {bot.Role}");
                    GUILayout.Label($"Level: {bot.Level}  XP: {bot.XP}");
                    GUILayout.Label($"Agg: {pers.Aggression:F2}  Cau: {pers.Caution:F2}  Cur: {pers.Curiosity:F2}  Loy: {pers.Loyalty:F2}");
                    GUILayout.Label($"Behavior: {bot.CurrentBehavior}");
                    GUILayout.Space(8);
                }
            }

            GUI.DragWindow();
        }
    }
}