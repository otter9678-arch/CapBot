using System;
using CapBot.Core.Tasks;

namespace CapBot.Core.World
{
    // ---- Phase 6: recovery's real world probe (P3 deliverable) --------------
    //
    // Answers ITaskWorldProbe questions from the latest WorldSnapshot. The
    // contract (docs/TASK_RECOVERY.md): answers come from CURRENT truth,
    // nothing is cached here — every call re-reads the snapshot the service
    // already holds.
    //
    // Fail-open on uncertainty: recovery's destructive actions (Cancel/Fail)
    // must only fire on POSITIVE evidence. A stale, never-captured, or
    // insufficiently-populated snapshot therefore answers "valid/available"
    // — the stuck-rule and deadline rule still converge recovery without
    // probe input. This also keeps the probe safe while dormant (no tick
    // driver yet): an un-refreshed cache can never cancel healthy tasks.
    //
    // Answerable now (from bounded snapshot data):
    //   TargetKind SHIP    — ship id present in the captured ship list
    //   TargetKind MISSION — mission type id present in the captured missions
    //   OwnerActorId CAPTAIN / BOT:<id> — crew membership from the captured crew
    // Everything else (other target kinds, HOST, unknown owners, capability
    // checks — P7 owns capabilities) fail-opens. WorldInvalidatesTask stays
    // false: no invented premise semantics this phase.
    public sealed class WorldSnapshotProbe : ITaskWorldProbe
    {
        private readonly Func<WorldSnapshot> m_Provider;
        private readonly Func<int> m_NowMs;

        public WorldSnapshotProbe()
            : this(() => WorldStateService.Latest, () => TaskClock.NowMs)
        {
        }

        // Test seam: inject snapshot + time providers for determinism.
        public WorldSnapshotProbe(Func<WorldSnapshot> snapshotProvider, Func<int> nowMsProvider)
        {
            m_Provider = snapshotProvider ?? (() => WorldStateService.Latest);
            m_NowMs = nowMsProvider ?? (() => TaskClock.NowMs);
        }

        public bool TargetValid(CapBotTask task)
        {
            if (task == null) return true; // nothing to invalidate
            WorldSnapshot snap = GetUsableSnapshot();
            if (snap == null) return true; // fail-open: no usable view

            string kind = task.TargetKind;
            string id = task.TargetId;
            if (string.IsNullOrEmpty(id)) return true;

            if (kind == "SHIP")
            {
                int shipId;
                if (!TryParseInt(id, out shipId)) return true; // unparseable is data, not evidence
                if (snap.Ships.Count == 0) return true;        // empty view is not evidence of absence
                for (int i = 0; i < snap.Ships.Count; i++)
                    if (snap.Ships[i].ShipId == shipId) return true;
                return false;
            }

            if (kind == "MISSION")
            {
                int missionId;
                if (!TryParseInt(id, out missionId)) return true;
                if (snap.Missions.Count == 0) return true;     // empty view is not evidence of absence
                for (int i = 0; i < snap.Missions.Count; i++)
                    if (snap.Missions[i].MissionTypeId == missionId) return true;
                return false;
            }

            return true; // SECTOR / COMPONENT / unknown kinds: unverifiable from bounded data
        }

        public bool OwnerAvailable(string ownerActorId)
        {
            if (string.IsNullOrEmpty(ownerActorId)) return true;
            WorldSnapshot snap = GetUsableSnapshot();
            if (snap == null) return true; // fail-open

            // CAPTAIN / BOT:<id> are crew-membership questions; HOST and
            // unknown owner formats are not answerable from world data.
            bool isCaptainOwner = ownerActorId == "CAPTAIN";
            int botId = -1;
            if (!isCaptainOwner
                && ownerActorId.Length > 4
                && ownerActorId.StartsWith("BOT:", StringComparison.Ordinal)
                && !TryParseInt(ownerActorId.Substring(4), out botId))
            {
                return true; // BOT:<unparseable> is not evidence of anything
            }
            if (!isCaptainOwner && botId < 0) return true;

            if (snap.Crew.Count == 0) return true; // empty view is not evidence of absence
            for (int i = 0; i < snap.Crew.Count; i++)
            {
                CrewMemberSnapshot c = snap.Crew[i];
                if (isCaptainOwner ? c.IsCaptain : (c.IsBot && c.PlayerId == botId))
                {
                    // Found the owner; unavailable only with positive evidence.
                    if (c.AliveKnown && !c.Alive) return false;
                    return true;
                }
            }
            // Crew was populated but the owner is not in it — positive absence.
            return false;
        }

        // Capabilities are a P7 registry; nothing to check yet, so available.
        public bool CapabilityAvailable(CapBotTask task)
        {
            return true;
        }

        // No invented premise semantics: sector departure alone does not
        // invalidate a task whose target sector still exists. Recovery's other
        // rules (target/owner/deadline/stuck) carry this phase's answers.
        public bool WorldInvalidatesTask(CapBotTask task)
        {
            return false;
        }

        private WorldSnapshot GetUsableSnapshot()
        {
            WorldSnapshot snap;
            try { snap = m_Provider(); }
            catch (Exception) { return null; } // provider fault = fail-open
            if (snap == null || snap.IsNeverCaptured) return null;
            if (m_NowMs() - snap.SnapshotTimeMs > WorldStateService.MaxSnapshotAgeMs) return null; // stale = fail-open
            return snap;
        }

        private static bool TryParseInt(string s, out int value)
        {
            return int.TryParse(s, out value); // invariant culture for digits — fine on all game locales
        }
    }
}