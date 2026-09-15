using CapBot.AI;
using UnityEngine;

namespace CapBot.UI.IMGUI
{
    public class IMGUI_TalentTree : IMGUIBase
    {
        public static IMGUI_TalentTree Instance = new IMGUI_TalentTree();

        protected override string GetTitle() => "Talent Tree";

        protected override void DrawWindow(int id)
        {
            CaptainBot bot = AIRegistry.GetLocalBot();
            if (bot == null)
            {
                GUILayout.Label("No CapBot on this ship.");
                GUI.DragWindow();
                return;
            }

            Talents.TalentTree tree = bot.TalentManager.Tree;

            GUILayout.Label($"Bot: {bot.Player.GetPlayerName()}");
            GUILayout.Label($"Level: {bot.Level}");
            GUILayout.Space(10);

            foreach (Talents.Talent t in tree.Talents)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Tier {t.Tier}: {t.Name}");

                if (t.IsUnlocked)
                    GUILayout.Label("Unlocked");
                else if (GUILayout.Button("Unlock"))
                    t.Unlock();

                GUILayout.EndHorizontal();
            }

            GUI.DragWindow();
        }
    }
}