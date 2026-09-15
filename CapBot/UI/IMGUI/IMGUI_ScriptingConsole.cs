using CapBot.AI;
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

            if (GUILayout.Button("Run") && !string.IsNullOrEmpty(input))
            {
                output += "\n> " + input;
                output += "\n" + ExecuteCommand(input);
                input = "";
            }

            GUI.DragWindow();
        }

        // Minimal command interpreter: status + role switching.
        private string ExecuteCommand(string cmd)
        {
            CapBot.AI.CaptainBot bot = CapBot.AI.AIRegistry.GetLocalBot();
            if (bot == null)
                return "No CapBot on this ship.";

            string[] parts = cmd.Trim().Split(' ');
            switch (parts[0].ToLowerInvariant())
            {
                case "status":
                    return $"Role={bot.Role} Level={bot.Level} XP={bot.XP} Behavior={bot.CurrentBehavior}";

                case "role":
                    if (parts.Length > 1 && System.Enum.TryParse(parts[1], true, out CapBotRole role))
                    {
                        bot.Role = role;
                        return $"Role set to {role}";
                    }
                    return "Usage: role <Captain|Engineer|Weapons|Science|Pilot>";

                default:
                    return $"Unknown command '{parts[0]}'. Try: status, role";
            }
        }
    }
}