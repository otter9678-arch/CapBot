using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;

namespace CapBot.Core.Crew
{
    // ---- Phase 10: crew agent model ------------------------------------------
    //
    // Pure C# representation of one crew member (bot or human) as a persistent
    // runtime agent record. This is the AGENT MODEL only — no personality, no
    // memory, no learning, no planning, no decision making, no execution. It
    // is the controlled, bounded data surface future phases (personality,
    // experience, memory, task assignment, role preferences, decision making)
    // build on.
    //
    // Identity: AgentId is "AGT:<hash8>" — a deterministic FNV-1a hash (via
    // ActionIdentity.ComputeStableHash, the same primitive EmergencyIdentity
    // uses) over the crew member's seed ("B|<playerId>" for bots, "H|<playerId>"
    // for humans). The id is stable for the crew member's lifetime, is
    // recomputed identically on rejoin, and never encodes mutable data (names,
    // classes). No game-object reference is ever stored: sector transitions
    // invalidate PLPlayer/PLBot references, so agents keep only bounded data
    // copied from the Phase 6 world snapshot.
    //
    // Boundaries (Phase 10 contract):
    //   - The agent NEVER stores or wraps a PLPlayer/PLBot/pawn reference.
    //   - The agent NEVER creates tasks, never claims, never executes
    //     capabilities, and never modifies PULSAR world state. Task fields
    //     are assignment metadata and observation results only.
    //   - All timestamps are explicit virtual-clock milliseconds (TaskClock
    //     semantics); no wall-clock reads, no DateTime.Now.

    // Bounded static role vocabulary mapped from PULSAR's verified class ids
    // (PULSAR_GAMEAI_RESEARCH §6, VERIFIED: 0 captain, 1 pilot, 2 scientist,
    // 3 weapons, 4 engineer). The game defines no class enum — ids are plain
    // Int32 named through the verified public channel
    // PLPlayer.GetClassNameFromID(Int32); that name is carried as DATA ONLY
    // (RoleName) and is never parsed or dispatched on. Unknown and Other are
    // defensive buckets for absent/out-of-table data — no roles are invented.
    public enum CrewRole
    {
        Unknown = 0,
        Captain = 1,
        Pilot = 2,
        Scientist = 3,
        Weapons = 4,
        Engineer = 5,
        Other = 6,
    }

    public static class CrewRoles
    {
        // Deterministic class-id -> role mapping. classId < 0 (unknown data)
        // maps to Unknown; any valid id outside the verified five maps to
        // Other rather than being mislabeled.
        public static CrewRole FromClassId(int classId)
        {
            switch (classId)
            {
                case 0: return CrewRole.Captain;
                case 1: return CrewRole.Pilot;
                case 2: return CrewRole.Scientist;
                case 3: return CrewRole.Weapons;
                case 4: return CrewRole.Engineer;
                default: return classId < 0 ? CrewRole.Unknown : CrewRole.Other;
            }
        }
    }

    // Lifecycle of one agent RECORD (not the game player object):
    //   Active   — present in the latest authoritative crew snapshot
    //   Inactive — previously seen, currently absent (stale reference,
    //              disconnect, bot removal) within the removal grace window
    //   Removed  — grace expired; record moved to bounded history and dropped
    //              from the live map (a later rejoin creates a fresh record
    //              under the same stable AgentId)
    public enum CrewAgentLifecycle
    {
        Active = 1,
        Inactive = 2,
        Removed = 3,
    }

    // One crew agent. Mutable domain object owned by CrewAgentRegistry: all
    // mutation happens inside the registry's lock during Sync or through its
    // explicit task-assignment APIs. Every field is data only.
    public sealed class CrewAgent
    {
        // ---- identity (stable for the crew member's lifetime) ----
        public readonly string AgentId;        // "AGT:<hash8>"
        public readonly int PlayerId;          // PULSAR identity reference (PLPlayer.GetPlayerID)
        public readonly bool IsBot;            // true = crew bot, false = human crew
        public int TeamId;                     // observed team (data only)

        // ---- role/class (raw game data + bounded vocabulary) ----
        public int ClassId;                    // raw game class id, -1 = unknown
        public CrewRole Role;                  // bounded vocabulary mapping
        public string RoleName;                // resolved data-only via the verified naming channel, null = unknown
        public string Name;                    // crew member name, <= 32 chars, data only
        public bool IsCaptain;                 // authoritative crew snapshot flag

        // ---- lifecycle ----
        public CrewAgentLifecycle Lifecycle;
        public readonly int CreatedTimeMs;
        public int LastSyncTimeMs;             // last snapshot confirm of presence
        public int AbsentSinceMs;              // -1 = present

        // ---- world-state association (cached observation, NOT ownership) ----
        // The Phase 6 snapshot remains the authoritative world observation;
        // agents cache a bounded last-seen location string only.
        public string LastKnownTLIName;        // <= 32 chars, null = unknown

        // ---- task relationship (assignment metadata + observation results) ----
        public long CurrentTaskId;             // 0 = none
        public string CurrentTaskType;         // static vocabulary text, data only
        public string CurrentTaskCapabilityId; // data-only copy of task metadata "CapabilityId"
        public int CurrentTaskAssignedMs;      // -1 = none
        public string LastTaskOutcome;         // COMPLETED/CANCELLED/EXPIRED/VANISHED
        public int LastTaskResultMs;           // -1 = none

        // ---- agent capabilities reference (future-phase integration point) ----
        // Bounded static-vocabulary capability ids (same charset/length rules
        // as CapabilityId). Data only: never dispatched on by this layer —
        // the P7 registry + P8 dispatcher remain the only execution path.
        public readonly List<string> CapabilityReferences = new List<string>();

        // ---- bounded diagnostics ----
        public long UpdateCount;
        public string LastChangeReason;        // short static text per call site

        // Constructed only by CrewAgentRegistry during Sync — identity and
        // birth stamp are immutable afterwards; everything else is registry-
        // mediated domain state (data only, defaults unknown/none).
        public CrewAgent(string agentId, int playerId, bool isBot, int createdTimeMs)
        {
            AgentId = agentId;
            PlayerId = playerId;
            IsBot = isBot;
            TeamId = 0;
            ClassId = -1;
            Role = CrewRole.Unknown;
            RoleName = null;
            Name = null;
            IsCaptain = false;
            Lifecycle = CrewAgentLifecycle.Active;
            CreatedTimeMs = createdTimeMs;
            LastSyncTimeMs = createdTimeMs;
            AbsentSinceMs = -1;
            LastKnownTLIName = null;
            CurrentTaskId = 0;
            CurrentTaskType = null;
            CurrentTaskCapabilityId = null;
            CurrentTaskAssignedMs = -1;
            LastTaskOutcome = null;
            LastTaskResultMs = -1;
            UpdateCount = 0;
            LastChangeReason = "created";
        }
    }
}