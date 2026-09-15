using UnityEngine;

namespace CapBot.Dialogue
{
    [System.Serializable]
    public class VoiceLine
    {
        public string Text;
        public AudioClip Clip;

        public VoiceLine(string text, AudioClip clip = null)
        {
            Text = text;
            Clip = clip;
        }
    }
}