using CapBot.AI;
using System;
using UnityEngine;

namespace CapBot.UI.IMGUI
{
    public class IMGUI_CrewManagement : IMGUIBase
    {
        public static IMGUI_CrewManagement Instance = new IMGUI_CrewManagement();

        protected override string GetTitle() => "Crew Management";

        protected override void DrawWindow(int id)
        {
            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0)
                {
                    CapBot bot = AIRegistry.Get(p);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{p.GetPlayerName()} ({bot.Role})  Lvl {bot.Level}");

                    if (GUILayout.Button("Talents"))
                        IMGUI_TalentTree.Instance.Toggle();

                    if (GUILayout.Button("Debug"))
                        IMGUI_DebugConsole.Instance.Toggle();

                    if (GUILayout.Button("Kick"))
                        PLServer.Instance.KickPlayer(p.GetPlayerID());

                    GUILayout.EndHorizontal();
                }
            }

            GUI.DragWindow();
        }
    }
}