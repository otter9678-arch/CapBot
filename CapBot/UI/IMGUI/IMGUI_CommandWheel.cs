using CapBot.AI;
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
                CommandWheel.CommandWheelManager.Execute(CommandWheel.CommandWheelButton.CommandType.Follow);

            if (GUILayout.Button("Defend"))
                CommandWheel.CommandWheelManager.Execute(CommandWheel.CommandWheelButton.CommandType.Defend);

            if (GUILayout.Button("Attack"))
                CommandWheel.CommandWheelManager.Execute(CommandWheel.CommandWheelButton.CommandType.Attack);

            if (GUILayout.Button("Loot"))
                CommandWheel.CommandWheelManager.Execute(CommandWheel.CommandWheelButton.CommandType.Loot);

            if (GUILayout.Button("Board"))
                CommandWheel.CommandWheelManager.Execute(CommandWheel.CommandWheelButton.CommandType.Board);

            if (GUILayout.Button("Return To Ship"))
                CommandWheel.CommandWheelManager.Execute(CommandWheel.CommandWheelButton.CommandType.ReturnToShip);

            if (GUILayout.Button("Hold Position"))
                CommandWheel.CommandWheelManager.Execute(CommandWheel.CommandWheelButton.CommandType.Hold);

            if (GUILayout.Button("Explore"))
                CommandWheel.CommandWheelManager.Execute(CommandWheel.CommandWheelButton.CommandType.Explore);

            GUI.DragWindow();
        }
    }
}