using CapBot.AI;
using System;
using UnityEngine;

namespace CapBot.UI.IMGUI
{
    public class IMGUI_BehaviorEditor : IMGUIBase
    {
        public static IMGUI_BehaviorEditor Instance = new IMGUI_BehaviorEditor();

        protected override string GetTitle() => "Behavior Weight Editor";

        protected override void DrawWindow(int id)
        {
            CapBot bot = AIRegistry.GetLocalBot();
            if (bot == null)
            {
                GUILayout.Label("No local bot.");
                GUI.DragWindow();
                return;
            }

            var w = bot.Weights;

            GUILayout.Label("Behavior Weights");

            w.Combat = Slider("Combat", w.Combat);
            w.ShipManagement = Slider("Ship Mgmt", w.ShipManagement);
            w.Planet = Slider("Planet", w.Planet);
            w.Idle = Slider("Idle", w.Idle);

            GUILayout.Space(10);
            GUILayout.Label("Personality Biases");

            w.AggressionBias = Slider("Agg Bias", w.AggressionBias);
            w.CautionBias = Slider("Cau Bias", w.CautionBias);
            w.CuriosityBias = Slider("Cur Bias", w.CuriosityBias);
            w.LoyaltyBias = Slider("Loy Bias", w.LoyaltyBias);

            GUI.DragWindow();
        }

        private float Slider(string label, float value)
        {
            GUILayout.Label($"{label}: {value:F2}");
            return GUILayout.HorizontalSlider(value, 0f, 3f);
        }
    }
}