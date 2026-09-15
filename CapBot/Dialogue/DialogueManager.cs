using CapBot.AI;
using CapBot.Personality;
using UnityEngine;

namespace CapBot.Dialogue
{
    public static class DialogueManager
    {
        private static float _lastLineTime = 0f;

        public static void Play(CaptainBot bot, VoiceLine line)
        {
            if (Time.time - _lastLineTime < 2f)
                return; // prevent spam

            _lastLineTime = Time.time;

            // Text output via game chat notification
            PulsarModLoader.Utilities.Messaging.Notification(
                $"{bot.Player.GetPlayerName()}: {line.Text}");

            // Audio output
            if (line.Clip != null && bot.Player.GetPawn() != null)
            {
                AudioSource.PlayClipAtPoint(line.Clip, bot.Player.GetPawn().transform.position);
            }
        }

        public static void PlayRandom(CaptainBot bot, System.Collections.Generic.List<VoiceLine> list)
        {
            if (list == null || list.Count == 0)
                return;

            // Personality weighting
            float curiosity = PersonalityManager.Curiosity(bot);
            float aggression = PersonalityManager.Aggression(bot);

            // Curious bots speak more often
            if (Random.value > Mathf.Lerp(0.2f, 0.8f, curiosity))
                return;

            // Aggressive bots speak more in combat
            if (bot.CurrentBehavior == "Combat" && Random.value > aggression)
                return;

            VoiceLine line = list[Random.Range(0, list.Count)];
            Play(bot, line);
        }
    }
}