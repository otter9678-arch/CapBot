namespace CapBot.Loadouts
{
    public class EngineerLoadout : BotLoadout
    {
        public override void Initialize()
        {
            PreferredTools.Add("Repair Gun");
            PreferredTools.Add("Fire Extinguisher");
            PreferredTools.Add("Coolant Canister");

            PreferredUtility.Add("Wrench");
            PreferredUtility.Add("Toolkit");
            PreferredUtility.Add("Research Material");
        }
    }
}