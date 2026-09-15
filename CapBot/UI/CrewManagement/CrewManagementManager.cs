using UnityEngine;
using CapBot.AI;

namespace CapBot.UI.CrewManagement
{
    public static class CrewManagementManager
    {
        public static void Refresh()
        {
            if (CrewManagementUI.Instance == null)
                return;

            CrewManagementUI.Instance.Clear();

            foreach (PLPlayer p in PLServer.Instance.AllPlayers)
            {
                if (p != null && p.IsBot && p.TeamID == 0)
                {
                    CapBot bot = AIRegistry.Get(p);
                    AddBotEntry(bot);
                }
            }
        }

        private static void AddBotEntry(CapBot bot)
        {
            GameObject prefab = Resources.Load<GameObject>("CrewMemberEntryPrefab");
            GameObject obj = GameObject.Instantiate(prefab, CrewManagementUI.Instance.EntryContainer);

            CrewMemberEntryUI ui = obj.GetComponent<CrewMemberEntryUI>();
            ui.Init(bot);
        }
    }
}