using CapBot.AI;

namespace CapBot.Dialogue
{
    public static class DialogueTriggers
    {
        public static void OnCombatStart(CaptainBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.CombatStart);
        }

        public static void OnKill(CaptainBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.CombatKill);
        }

        public static void OnLowHealth(CaptainBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.LowHealth);
        }

        public static void OnRepair(CaptainBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.Repairing);
        }

        public static void OnScan(CaptainBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.Scanning);
        }

        public static void OnExplore(CaptainBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.Exploring);
        }

        public static void OnIdle(CaptainBot bot)
        {
            DialogueManager.PlayRandom(bot, bot.Voice.Idle);
        }
    }
}