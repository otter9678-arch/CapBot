using System;
using CapBot.Core.Logging;
using CapBot.Core.Persistence;
using PulsarModLoader.SaveData;

namespace CapBot
{
    // ---- Phase 28: crew state persistence (PML save pipeline) -------------------
    //
    // The game-build transport for CrewSaveCodec: PML auto-discovers every
    // non-abstract PMLSaveData subclass in the mod assembly at mod-load time
    // (SaveDataManager.OnModLoaded — decompile-verified against the shipped
    // PulsarModLoader.dll: Activator.CreateInstance + SaveConfigs list) and
    // calls SaveData()/LoadData() from the game's own save-file IO path
    // (PML's SavePatch/LoadPatch transpilers hook PLSaveGameIO.SaveToFile/
    // LoadFromFile). No Mod.cs wiring, no new Harmony patch class (the 11-class
    // ceiling is untouched), no config toggle (the P18–P26
    // deterministic-director precedent).
    //
    // Contract: SaveData() must never throw (PML wraps the call, but a throw
    // would corrupt the whole save block); LoadData() must never throw and
    // must tolerate missing/foreign/version-mismatched data (PML delivers the
    // stored VersionID — CrewSaveCodec.SaveSchemaVersion checks are inside the
    // codec; version mismatch degrades to an empty-registry restore, never a
    // crash).
    //
    // Restore is TOLERANT and additive: rows restore through the registries'
    // validated restore hooks (CrewExperienceRegistry.RestoreRecord,
    // CrewMemorySystem.RestoreMemoryEntry) and the sanctioned personality
    // write path (CrewPersonalityRegistry.SetPersonality). Malformed input
    // restores what it can; the counts are logged once (CapBotLog.PERSISTENCE).
    public sealed class CrewSave : PMLSaveData
    {
        public override uint VersionID { get { return CrewSaveCodec.SaveSchemaVersion; } }

        public override string Identifier()
        {
            return "CapBotCrewState";
        }

        public override byte[] SaveData()
        {
            try
            {
                return CrewSaveCodec.SaveAll();
            }
            catch (Exception ex)
            {
                CapBotLog.Error(CapBotLog.PERSISTENCE, "Crew state save failed", ex);
                return new byte[0];
            }
        }

        public override void LoadData(byte[] data, uint versionID)
        {
            try
            {
                int restored = CrewSaveCodec.LoadAll(data);
                CapBotLog.Info(CapBotLog.PERSISTENCE, "Crew state restore: " + restored.ToString(System.Globalization.CultureInfo.InvariantCulture) + " records (schema " + versionID.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception ex)
            {
                CapBotLog.Error(CapBotLog.PERSISTENCE, "Crew state restore failed", ex);
            }
        }
    }
}