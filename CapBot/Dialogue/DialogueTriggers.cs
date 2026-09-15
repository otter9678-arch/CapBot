using CapBot.AI;

namespace CapBot.Dialogue
{
    public static class DialogueTriggers
    {
        public static void OnCombatStart(CapBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.CombatStart);
        }

        public static void OnKill(CapBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.CombatKill);
        }

        public static void OnLowHealth(CapBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.LowHealth);
        }

        public static void OnRepair(CapBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.Repairing);
        }

        public static void OnScan(CapBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.Scanning);
        }

        public static void OnExplore(CapBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.Exploring);
        }

        public static void OnIdle(CapBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.Idle);
        }
    }
}