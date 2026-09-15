using UnityEngine;
using UnityEngine.UI;
using CapBot.AI;
using CapBot.Personality;

namespace CapBot.UI.CrewManagement
{
    public class CrewMemberEntryUI : MonoBehaviour
    {
        public Text NameText;
        public Dropdown RoleDropdown;
        public Text LevelText;
        public Slider XPBar;
        public Text PersonalityText;

        public Button TalentButton;
        public Button DebugButton;
        public Button RenameButton;
        public Button FireButton;

        private CapBot _bot;

        public void Init(CapBot bot)
        {
            _bot = bot;

            NameText.text = bot.Player.GetPlayerName();
            LevelText.text = $"Level {bot.Level}";
            XPBar.value = bot.XP / (float)(bot.Level * 100);

            var p = PersonalityManager.Get(bot);
            PersonalityText.text =
                $"Agg {p.Aggression:F2} | Cau {p.Caution:F2} | Cur {p.Curiosity:F2} | Loy {p.Loyalty:F2}";

            // Populate role dropdown
            RoleDropdown.ClearOptions();
            RoleDropdown.AddOptions(new System.Collections.Generic.List<string>
            {
                "Engineer", "Weapons", "Science", "Pilot"
            });

            RoleDropdown.value = (int)bot.Role;
            RoleDropdown.onValueChanged.AddListener(OnRoleChanged);

            TalentButton.onClick.AddListener(OnTalentClicked);
            DebugButton.onClick.AddListener(OnDebugClicked);
            RenameButton.onClick.AddListener(OnRenameClicked);
            FireButton.onClick.AddListener(OnFireClicked);
        }

        private void OnRoleChanged(int index)
        {
            _bot.Role = (CapBotRole)index;
        }

        private void OnTalentClicked()
        {
            UI.TalentTree.TalentTreeUI.Instance.Toggle();
        }

        private void OnDebugClicked()
        {
            UI.DebugConsole.AIDebugConsole.Instance.Toggle();
        }

        private void OnRenameClicked()
        {
            // Simple rename popup
            string newName = "Bot_" + Random.Range(1000, 9999);
            _bot.Player.SetPlayerName(newName);
            NameText.text = newName;
        }

        private void OnFireClicked()
        {
            // Remove bot from crew
            PLServer.Instance.KickPlayer(_bot.Player.GetPlayerID());
            Destroy(gameObject);
        }
    }
}