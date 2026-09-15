using CapBot.UI.CommandWheel; // for CommandType
using CapBot.UI.CommandWheel; // your existing CommandWheelManager
using UnityEngine;

namespace CapBot.UI.IMGUI
{
    public class IMGUI_CommandWheel : IMGUIBase
    {
        public static IMGUI_CommandWheel Instance = new IMGUI_CommandWheel();

        protected override string GetTitle() => "CapBot Commands";

        protected override void DrawWindow(int id)
        {
            GUILayout.Label("Issue orders to all CapBots:");

            if (GUILayout.Button("Follow"))
                CommandWheelManager.Execute(CommandWheelButton.CommandType.Follow);

            if (GUILayout.Button("Defend"))
                CommandWheelManager.Execute(CommandWheelButton.CommandType.Defend);

            if (GUILayout.Button("Attack"))
                CommandWheelManager.Execute(CommandWheelButton.CommandType.Attack);

            if (GUILayout.Button("Loot"))
                CommandWheelManager.Execute(CommandWheelButton.CommandType.Loot);

            if (GUILayout.Button("Board"))
                CommandWheelManager.Execute(CommandWheelButton.CommandType.Board);

            if (GUILayout.Button("Return To Ship"))
                CommandWheelManager.Execute(CommandWheelButton.CommandType.ReturnToShip);

            if (GUILayout.Button("Hold Position"))
                CommandWheelManager.Execute(CommandWheelButton.CommandType.Hold);

            if (GUILayout.Button("Explore"))
                CommandWheelManager.Execute(CommandWheelButton.CommandType.Explore);

            GUI.DragWindow();
        }
    }
}