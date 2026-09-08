using System;
using PulsarModLoader.SaveData;
using CapBot.Core.Logging;
using CapBot.Core.Persistence;

namespace CapBot
{
    // ---- Phase 28: PML save-slot adapter for the crew data layers ---------------
    //
    // Thin production wiring between PML's save system and the pure-C#
    // CrewPersistence domain. PML auto-discovers PMLSaveData subclasses in the
    // mod assembly (the proven CapBotLearningSave pattern — no registration
    // call, saved/loaded by PML per save slot). The legacy Autonomy.Learning
    // XP slot ("CapBotLearning") is UNTOUCHED; this adds a second slot
    // ("CapBotCrewData") for the P12/P13/P11-matured crew data layers.
    //
    // Fail-safe both ways: a faulting Save returns an empty blob (PML stores
    // it; Load treats empty as a no-op), and a faulting Load never throws and
    // never leaves partial state (Decode is all-or-nothing; Restore is
    // insert-only per row). All logging through the PERSISTENCE subsystem.
    public class CapBotCrewDataSave : PMLSaveData
    {
        public override uint VersionID => 1u;
        public override string Identifier() => "CapBotCrewData";

        public override byte[] SaveData()
        {
            try
            {
                CrewPersistence.CrewDataSnapshot snap = CrewPersistence.Capture();
                byte[] bytes = CrewPersistence.Encode(snap);
                if (bytes == null)
                {
                    CapBotLog.Warning(CapBotLog.PERSISTENCE, "Crew data encode refused (over-bound payload) — nothing saved");
                    return new byte[0];
                }
                CapBotLog.Info(CapBotLog.PERSISTENCE, "CrewDataSaved exp=" + snap.Experience.Count
                    + " matured=" + snap.MaturedPersonalities.Count
                    + " mem=" + snap.Memories.Count
                    + " bytes=" + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return bytes;
            }
            catch (Exception ex)
            {
                CapBotLog.Error(CapBotLog.PERSISTENCE, "Crew data save failed", ex);
                return new byte[0];
            }
        }

        public override void LoadData(byte[] Data, uint VersionID)
        {
            try
            {
                if (Data == null || Data.Length == 0) return;
                CrewPersistence.CrewDataSnapshot snap = CrewPersistence.Decode(Data);
                if (snap == null)
                {
                    CapBotLog.Warning(CapBotLog.PERSISTENCE,
                        "Crew data save unreadable (bad magic/version/truncated) — starting fresh; live state untouched");
                    return;
                }
                int inserted = CrewPersistence.Restore(snap);
                CapBotLog.Info(CapBotLog.PERSISTENCE, "CrewDataLoaded inserted=" + inserted
                    + " exp=" + snap.Experience.Count
                    + " matured=" + snap.MaturedPersonalities.Count
                    + " mem=" + snap.Memories.Count);
            }
            catch (Exception ex)
            {
                CapBotLog.Error(CapBotLog.PERSISTENCE, "Crew data load failed", ex);
            }
        }
    }
}