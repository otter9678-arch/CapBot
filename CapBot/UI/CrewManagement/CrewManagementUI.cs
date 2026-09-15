using UnityEngine;

namespace CapBot.UI.CrewManagement
{
    public class CrewManagementUI : MonoBehaviour
    {
        public static CrewManagementUI Instance;

        public GameObject PanelRoot;
        public Transform EntryContainer;

        private void Awake()
        {
            Instance = this;
            PanelRoot.SetActive(false);
        }

        private void Update()
        {
            // Toggle with U
            if (Input.GetKeyDown(KeyCode.U))
                Toggle();
        }

        public void Toggle()
        {
            bool newState = !PanelRoot.activeSelf;
            PanelRoot.SetActive(newState);

            if (newState)
                CrewManagementManager.Refresh();
        }

        public void Clear()
        {
            foreach (Transform child in EntryContainer)
                Destroy(child.gameObject);
        }
    }
}