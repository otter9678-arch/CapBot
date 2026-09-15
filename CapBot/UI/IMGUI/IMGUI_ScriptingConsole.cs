using System;
using UnityEngine;

namespace CapBot.UI.IMGUI
{
    public class IMGUI_ScriptingConsole : IMGUIBase
    {
        public static IMGUI_ScriptingConsole Instance = new IMGUI_ScriptingConsole();

        private string input = "";
        private string output = "";

        protected override string GetTitle() => "AI Scripting Console";

        protected override void DrawWindow(int id)
        {
            GUILayout.Label("Output:");
            output = GUILayout.TextArea(output, GUILayout.Height(250));

            GUILayout.Label("Command:");
            input = GUILayout.TextField(input);

            if (GUILayout.Button("Run"))
            {
                output += "\n> " + input;
                output += "\n" + UI.ScriptingConsole.AIScriptingInterpreter.Execute(input);
                input = "";
            }

            GUI.DragWindow();
        }
    }
}