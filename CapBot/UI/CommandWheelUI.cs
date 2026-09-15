using UnityEngine;

namespace CapBot.UI.CommandWheel
{
    public class CommandWheelUI : MonoBehaviour
    {
        public static CommandWheelUI Instance;

        public GameObject WheelRoot;
        public bool IsOpen { get; private set; }

        private void Awake()
        {
            Instance = this;
            WheelRoot.SetActive(false);
        }

        private void Update()
        {
            // Toggle wheel with Q
            if (Input.GetKeyDown(KeyCode.Q))
                Toggle();
        }

        public void Toggle()
        {
            IsOpen = !IsOpen;
            WheelRoot.SetActive(IsOpen);

            if (IsOpen)
                Cursor.lockState = CursorLockMode.None;
            else
                Cursor.lockState = CursorLockMode.Locked;
        }

        public void Close()
        {
            IsOpen = false;
            WheelRoot.SetActive(false);
            Cursor.lockState = CursorLockMode.Locked;
        }
    }
}