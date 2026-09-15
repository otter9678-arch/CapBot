using UnityEngine;
using CapBot.AI;
using CapBot.Talents;

namespace CapBot.UI.TalentTree
{
    public static class TalentTreeManager
    {
        public static void RefreshUI()
        {
            if (TalentTreeUI.Instance == null)
                return;

            Transform container = TalentTreeUI.Instance.TierContainer;

            // Clear old UI
            foreach (Transform child in container)
                GameObject.Destroy(child.gameObject);

            // Build new UI
            CapBot bot = AIRegistry.GetLocalBot();
            TalentTree tree = bot.TalentManager.Tree;

            foreach (Talent talent in tree.Talents)
            {
                GameObject prefab = Resources.Load<GameObject>("TalentButtonPrefab");
                GameObject obj = GameObject.Instantiate(prefab, container);

                TalentButtonUI ui = obj.GetComponent<TalentButtonUI>();
                ui.Init(talent);
            }
        }
    }
}