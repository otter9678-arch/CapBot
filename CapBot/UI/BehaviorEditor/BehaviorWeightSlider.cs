using UnityEngine;
using UnityEngine.UI;

namespace CapBot.UI.BehaviorEditor
{
    public class BehaviorWeightSlider : MonoBehaviour
    {
        public Text Label;
        public Slider Slider;

        private System.Action<float> _onChanged;

        public void Init(string label, float value, System.Action<float> onChanged)
        {
            Label.text = label;
            Slider.value = value;
            _onChanged = onChanged;

            Slider.onValueChanged.AddListener(OnSliderChanged);
        }

        private void OnSliderChanged(float value)
        {
            _onChanged?.Invoke(value);
        }
    }
}