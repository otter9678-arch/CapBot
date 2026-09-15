namespace CapBot.Loadouts
{
    public class WeaponsLoadout : BotLoadout
    {
        public override void Initialize()
        {
            PreferredWeapons.Add("Phase Pistol");
            PreferredWeapons.Add("Heavy Beam Pistol");
            PreferredWeapons.Add("Burst Rifle");

            PreferredTools.Add("Ammo Clip");
            PreferredTools.Add("Grenade");

            PreferredUtility.Add("Shield Booster");
        }
    }
}