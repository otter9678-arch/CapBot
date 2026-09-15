namespace CapBot.Talents
{
    // Data-only talent record; effects are interpreted by TalentEffects.
    public class Talent
    {
        public string Name = "";
        public string Description = "";
        public int Tier = 1;
        public bool IsUnlocked = false;

        public void Unlock()
        {
            if (IsUnlocked) return;
            IsUnlocked = true;
            TalentEffects.Apply(this);
        }
    }
}