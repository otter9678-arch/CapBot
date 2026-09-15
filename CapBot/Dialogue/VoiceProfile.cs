using System.Collections.Generic;
using CapBot.AI;

namespace CapBot.Dialogue
{
    public class VoiceProfile
    {
        public List<VoiceLine> CombatStart = new List<VoiceLine>();
        public List<VoiceLine> CombatKill = new List<VoiceLine>();
        public List<VoiceLine> LowHealth = new List<VoiceLine>();
        public List<VoiceLine> Repairing = new List<VoiceLine>();
        public List<VoiceLine> Scanning = new List<VoiceLine>();
        public List<VoiceLine> Exploring = new List<VoiceLine>();
        public List<VoiceLine> Idle = new List<VoiceLine>();

        public static VoiceProfile ForRole(CapBotRole role)
        {
            VoiceProfile p = new VoiceProfile();

            switch (role)
            {
                case CapBotRole.Engineer:
                    p.CombatStart.Add(new VoiceLine("Uh—guys? We've got company!"));
                    p.Repairing.Add(new VoiceLine("Hold on, patching this up!"));
                    p.LowHealth.Add(new VoiceLine("I need a medkit, now!"));
                    break;

                case CapBotRole.Weapons:
                    p.CombatStart.Add(new VoiceLine("Targets acquired. Let's dance."));
                    p.CombatKill.Add(new VoiceLine("Hostile neutralized."));
                    p.LowHealth.Add(new VoiceLine("I can still fight!"));
                    break;

                case CapBotRole.Science:
                    p.Scanning.Add(new VoiceLine("Running a quick analysis..."));
                    p.CombatStart.Add(new VoiceLine("This is suboptimal."));
                    p.LowHealth.Add(new VoiceLine("Vitals dropping—this is bad!"));
                    break;

                case CapBotRole.Pilot:
                    p.CombatStart.Add(new VoiceLine("Strapping in!"));
                    p.LowHealth.Add(new VoiceLine("Shields failing!"));
                    p.Exploring.Add(new VoiceLine("Area looks clear."));
                    break;

                default:
                    p.Idle.Add(new VoiceLine("All quiet on the bridge."));
                    p.CombatStart.Add(new VoiceLine("Battle stations!"));
                    break;
            }

            return p;
        }
    }
}