using UnityEngine;

namespace CapBot.UI.TalentTree
{
    public class TalentTreeUI : MonoBehaviour
    {
        public static TalentTreeUI Instance;

        public GameObject PanelRoot;
        public Transform TierContainer;

        private void Awake()
        {
            Instance = this;
            PanelRoot.SetActive(false);
        }

        private void Update()
        {
            // Toggle with T
            if (Input.GetKeyDown(KeyCode.T))
                Toggle();
        }

        public void Toggle()
        {
            bool newState = !PanelRoot.activeSelf;
            PanelRoot.SetActive(newState);

            if (newState)
                TalentTreeManager.RefreshUI();
        }
    }
}