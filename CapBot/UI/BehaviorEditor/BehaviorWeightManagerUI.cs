using UnityEngine;
using CapBot.AI;

namespace CapBot.UI.BehaviorEditor
{
    public static class BehaviorWeightManagerUI
    {
        public static void Refresh()
        {
            if (BehaviorWeightEditorUI.Instance == null)
                return;

            BehaviorWeightEditorUI.Instance.Clear();

            CapBot bot = AIRegistry.GetLocalBot();
            var w = bot.Weights;

            AddSlider("Combat Weight", w.Combat, v => w.Combat = v);
            AddSlider("Ship Management Weight", w.ShipManagement, v => w.ShipManagement = v);
            AddSlider("Planet Weight", w.Planet, v => w.Planet = v);
            AddSlider("Idle Weight", w.Idle, v => w.Idle = v);

            AddSlider("Aggression Bias", w.AggressionBias, v => w.AggressionBias = v);
            AddSlider("Caution Bias", w.CautionBias, v => w.CautionBias = v);
            AddSlider("Curiosity Bias", w.CuriosityBias, v => w.CuriosityBias = v);
            AddSlider("Loyalty Bias", w.LoyaltyBias, v => w.LoyaltyBias = v);
        }

        private static void AddSlider(string label, float value, System.Action<float> onChanged)
        {
            GameObject prefab = Resources.Load<GameObject>("BehaviorWeightSliderPrefab");
            GameObject obj = GameObject.Instantiate(prefab, BehaviorWeightEditorUI.Instance.SliderContainer);

            BehaviorWeightSlider slider = obj.GetComponent<BehaviorWeightSlider>();
            slider.Init(label, value, onChanged);
        }
    }
}