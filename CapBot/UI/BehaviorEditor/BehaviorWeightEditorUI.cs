using UnityEngine;

namespace CapBot.UI.BehaviorEditor
{
    public class BehaviorWeightEditorUI : MonoBehaviour
    {
        public static BehaviorWeightEditorUI Instance;

        public GameObject PanelRoot;
        public Transform SliderContainer;

        private void Awake()
        {
            Instance = this;
            PanelRoot.SetActive(false);
        }

        private void Update()
        {
            // Toggle with F2
            if (Input.GetKeyDown(KeyCode.F2))
                Toggle();
        }

        public void Toggle()
        {
            bool newState = !PanelRoot.activeSelf;
            PanelRoot.SetActive(newState);

            if (newState)
                BehaviorWeightManagerUI.Refresh();
        }

        public void Clear()
        {
            foreach (Transform child in SliderContainer)
                Destroy(child.gameObject);
        }
    }
}