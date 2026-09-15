using UnityEngine;
using UnityEngine.UI;
using CapBot.Talents;

namespace CapBot.UI.TalentTree
{
    public class TalentButtonUI : MonoBehaviour
    {
        public Text NameText;
        public Image Icon;
        public Image Border;
        public Button Button;

        private Talent _talent;

        public void Init(Talent talent)
        {
            _talent = talent;
            NameText.text = talent.Name;

            Button.onClick.AddListener(OnClick);
            Refresh();
        }

        public void Refresh()
        {
            Border.color = _talent.IsUnlocked ? Color.green : Color.red;
        }

        private void OnClick()
        {
            TalentTooltipUI.Instance.Show(_talent, transform.position);
        }
    }
}