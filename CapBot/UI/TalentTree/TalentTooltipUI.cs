using UnityEngine;
using UnityEngine.UI;
using CapBot.Talents;

namespace CapBot.UI.TalentTree
{
    public class TalentTooltipUI : MonoBehaviour
    {
        public static TalentTooltipUI Instance;

        public GameObject Root;
        public Text Title;
        public Text Description;
        public Text Tier;

        private void Awake()
        {
            Instance = this;
            Root.SetActive(false);
        }

        public void Show(Talent talent, Vector3 pos)
        {
            Title.text = talent.Name;
            Description.text = talent.Description;
            Tier.text = $"Tier {talent.Tier}";

            Root.transform.position = pos + new Vector3(150, 0, 0);
            Root.SetActive(true);
        }

        public void Hide()
        {
            Root.SetActive(false);
        }
    }
}