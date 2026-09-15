namespace CapBot.Loadouts
{
    public class ScienceLoadout : BotLoadout
    {
        public override void Initialize()
        {
            PreferredTools.Add("Scanner");
            PreferredTools.Add("Research Sample");
            PreferredTools.Add("Virus Injector");

            PreferredUtility.Add("Medkit");
            PreferredUtility.Add("Stimulant");
        }
    }
}