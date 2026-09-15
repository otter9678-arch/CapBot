namespace CapBot.Loadouts
{
    public class PilotLoadout : BotLoadout
    {
        public override void Initialize()
        {
            PreferredWeapons.Add("Phase Pistol");

            PreferredTools.Add("Thruster Module");
            PreferredTools.Add("Flight Manual");

            PreferredUtility.Add("Oxygen Canister");
        }
    }
}