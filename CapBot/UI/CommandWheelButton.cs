using UnityEngine;
using UnityEngine.UI;

namespace CapBot.UI.CommandWheel
{
    public class CommandWheelButton : MonoBehaviour
    {
        public enum CommandType
        {
            Follow,
            Defend,
            Attack,
            Loot,
            Board,
            ReturnToShip,
            Hold,
            Explore
        }

        public CommandType Command;

        private void Start()
        {
            GetComponent<Button>().onClick.AddListener(OnClick);
        }

        private void OnClick()
        {
            CommandWheelManager.Execute(Command);
            CommandWheelUI.Instance.Close();
        }
    }
}