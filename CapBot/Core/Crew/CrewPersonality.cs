using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;

namespace CapBot.Core.Crew
{
    // ---- Phase 11: crew personalities -----------------------------------------
    //
    // Bounded, DETERMINISTIC personality data for crew agents. A personality is
    // derived from the agent's stable identity (AgentId, "AGT:<hash8>") so the
    // same crew member always carries the same personality — across rejoins,
    // class changes, name changes, and process restarts within a session —
    // with zero per-frame cost and zero randomness.
    //
    // Boundaries (Phase 11 contract):
    //   - Personalities are DATA ONLY. Nothing in this layer reads PULSAR
    //     state, creates/claims/executes tasks, or influences the scheduler,
    //     recovery, claims, capabilities, or executor. Consumers (planning,
    //     role preferences, decision weighting) are LATER phases; they read
    //     this data through the registry lookups defined here.
    //   - No game-object references, no wall-clock reads (all nowMs values are
    //     explicit TaskClock-semantics arguments), no LINQ, no scene scans.
    //   - Every collection is bounded; every input is validated/clamped; all
    //     vocabularies are static and compared, never parsed or dispatched on.

    // The five fixed personality traits. The vocabulary is closed: no trait is
    // ever invented at runtime. Values are integers 0..100 (50 = neutral).
    public enum PersonalityTrait : byte
    {
        Discipline = 0,    // rule-following, procedure adherence
        Boldness = 1,      // risk acceptance
        Sociability = 2,   // cooperation / teamwork orientation
        Diligence = 3,     // persistence and thoroughness on tasks
        Adaptability = 4,  // flexibility under changing conditions
    }

    // One immutable personality record for exactly one agent. Created only by
    // PersonalityFactory and stored only in CrewPersonalityRegistry.
    public sealed class CrewPersonality
    {
        public const int MinTraitValue = 0;
        public const int MaxTraitValue = 100;

        public readonly string AgentId;      // the personality's owner (stable agent identity)
        public readonly string Archetype;    // static archetype vocabulary (PersonalityArchetypes)
        public readonly string Source;       // "derived" / "explicit" / "neutral"
        public readonly int DerivedTimeMs;   // stamp of record creation (TaskClock semantics)

        private readonly int[] m_Traits;     // indexed by (int)PersonalityTrait, always 0..100

        public CrewPersonality(string agentId, int[] traits, string archetype, string source, int derivedTimeMs)
        {
            AgentId = agentId;
            Archetype = archetype;
            Source = source;
            DerivedTimeMs = derivedTimeMs;
            m_Traits = new int[5];
            if (traits != null)
            {
                for (int i = 0; i < 5 && i < traits.Length; i++) m_Traits[i] = ClampTrait(traits[i]);
            }
        }

        public static int ClampTrait(int value)
        {
            if (value < MinTraitValue) return MinTraitValue;
            if (value > MaxTraitValue) return MaxTraitValue;
            return value;
        }

        // Trait read. Out-of-range trait queries return -1 (invalid query) —
        // never a fabricated value.
        public int Get(PersonalityTrait trait)
        {
            int i = (int)trait;
            if (i < 0 || i >= 5) return -1;
            return m_Traits[i];
        }

        // Bounded single-line diagnostic (deterministic, data only).
        public string ToLine()
        {
            return "personality " + AgentId
                + " disc=" + m_Traits[0]
                + " bold=" + m_Traits[1]
                + " soc=" + m_Traits[2]
                + " dil=" + m_Traits[3]
                + " adpt=" + m_Traits[4]
                + " arch=" + (Archetype ?? "-")
                + " src=" + (Source ?? "-");
        }
    }

    // Deterministic personality construction. Three sources:
    //   Derive    — identity-derived (stable FNV-1a of the AgentId); the
    //               "individual personality" source: each agent gets its own
    //               reproducible trait spread, no randomness involved.
    //   FromValues — explicit values (clamped 0..100); future phases may use
    //               this for hand-authored or persisted personalities.
    //   Neutral   — all-50 default (fail-safe for unknown agents).
    public static class PersonalityFactory
    {
        public const string SourceDerived = "derived";
        public const string SourceExplicit = "explicit";
        public const string SourceNeutral = "neutral";

        public const int MaxAgentIdLength = 32; // AgentId is "AGT:<hash8>" (12 chars); 32 = bounded headroom

        public static bool IsValidAgentId(string agentId)
        {
            if (string.IsNullOrEmpty(agentId) || agentId.Length != 12) return false;
            return agentId.StartsWith("AGT:", StringComparison.Ordinal);
        }

        // Identity-derived personality. null on invalid agentId.
        public static CrewPersonality Derive(string agentId, int nowMs)
        {
            if (!IsValidAgentId(agentId)) return null;
            int[] traits = new int[5];
            traits[0] = TraitFromHash(ComputeTraitHash(agentId, "DISCIPLINE"));
            traits[1] = TraitFromHash(ComputeTraitHash(agentId, "BOLDNESS"));
            traits[2] = TraitFromHash(ComputeTraitHash(agentId, "SOCIABILITY"));
            traits[3] = TraitFromHash(ComputeTraitHash(agentId, "DILIGENCE"));
            traits[4] = TraitFromHash(ComputeTraitHash(agentId, "ADAPTABILITY"));
            return new CrewPersonality(agentId, traits,
                PersonalityArchetypes.Assign(traits), SourceDerived, nowMs);
        }

        // Explicit personality from raw values (clamped). null on invalid agentId.
        public static CrewPersonality FromValues(string agentId,
            int discipline, int boldness, int sociability, int diligence, int adaptability, int nowMs)
        {
            if (!IsValidAgentId(agentId)) return null;
            int[] traits = new int[5]
            {
                CrewPersonality.ClampTrait(discipline),
                CrewPersonality.ClampTrait(boldness),
                CrewPersonality.ClampTrait(sociability),
                CrewPersonality.ClampTrait(diligence),
                CrewPersonality.ClampTrait(adaptability),
            };
            return new CrewPersonality(agentId, traits,
                PersonalityArchetypes.Assign(traits), SourceExplicit, nowMs);
        }

        // Neutral all-50 personality. null on invalid agentId.
        public static CrewPersonality Neutral(string agentId, int nowMs)
        {
            if (!IsValidAgentId(agentId)) return null;
            int[] traits = new int[5] { 50, 50, 50, 50, 50 };
            return new CrewPersonality(agentId, traits,
                PersonalityArchetypes.Assign(traits), SourceNeutral, nowMs);
        }

        // Trait seed: the agent's stable id plus a static trait salt. Same
        // primitive as AgentId/EmergencyIdentity (ActionIdentity.ComputeStableHash)
        // — deterministic across sessions and platforms, never string.GetHashCode.
        private static uint ComputeTraitHash(string agentId, string traitSalt)
        {
            return ActionIdentity.ComputeStableHash("P|" + agentId + "|" + traitSalt);
        }

        // Byte-scale a hash word into 0..100 uniformly.
        private static int TraitFromHash(uint h)
        {
            uint b = (h >> 24) & 0xFFu;
            return (int)((b * 100u) / 255u);
        }
    }

    // Static archetype vocabulary derived from the trait spread. A trait is
    // "dominant" at >= DominantThreshold (70); ties resolve to the FIRST trait
    // in PersonalityTrait enum order (fixed, deterministic). Agents with no
    // dominant trait are "BALANCED".
    public static class PersonalityArchetypes
    {
        public const int DominantThreshold = 70;

        public const string Sentinel = "SENTINEL";      // dominant Discipline
        public const string Vanguard = "VANGUARD";      // dominant Boldness
        public const string Coordinator = "COORDINATOR";// dominant Sociability
        public const string Technician = "TECHNICIAN";  // dominant Diligence
        public const string Adapter = "ADAPTER";        // dominant Adaptability
        public const string Balanced = "BALANCED";      // no dominant trait

        public static string Assign(int[] traits)
        {
            if (traits == null || traits.Length < 5) return Balanced;
            for (int i = 0; i < 5; i++)
            {
                if (traits[i] >= DominantThreshold)
                {
                    switch (i)
                    {
                        case 0: return Sentinel;
                        case 1: return Vanguard;
                        case 2: return Coordinator;
                        case 3: return Technician;
                        default: return Adapter;
                    }
                }
            }
            return Balanced;
        }
    }

    // Deterministic role-affinity scoring: how well a personality fits a crew
    // role. DATA ONLY — a preference anchor for later phases (role preferences,
    // task assignment); nothing here dispatches, schedules, or executes.
    //
    // Weights are fixed per role, always summing to exactly 100, so
    // Score() is bounded 0..100 with plain integer math. Unknown/Other roles
    // use a uniform 20/20/20/20/20 table (no role preference expressible).
    public static class RoleAffinity
    {
        // Trait order in every weight table: Discipline, Boldness, Sociability,
        // Diligence, Adaptability.
        private static readonly int[] CaptainWeights = new int[5] { 40, 20, 30, 5, 5 };
        private static readonly int[] PilotWeights = new int[5] { 20, 30, 10, 5, 35 };
        private static readonly int[] ScientistWeights = new int[5] { 25, 5, 10, 40, 20 };
        private static readonly int[] WeaponsWeights = new int[5] { 30, 40, 5, 20, 5 };
        private static readonly int[] EngineerWeights = new int[5] { 30, 5, 5, 45, 15 };
        private static readonly int[] UniformWeights = new int[5] { 20, 20, 20, 20, 20 };

        // Weight table for a role (defensive copy — callers may not mutate the
        // static tables). Unknown for out-of-range roles.
        public static int[] Weights(CrewRole role)
        {
            int[] table;
            switch (role)
            {
                case CrewRole.Captain: table = CaptainWeights; break;
                case CrewRole.Pilot: table = PilotWeights; break;
                case CrewRole.Scientist: table = ScientistWeights; break;
                case CrewRole.Weapons: table = WeaponsWeights; break;
                case CrewRole.Engineer: table = EngineerWeights; break;
                default: table = UniformWeights; break;
            }
            return new int[5] { table[0], table[1], table[2], table[3], table[4] };
        }

        // Affinity of a personality for a role, 0..100. -1 when the personality
        // is null (invalid query) — never a fabricated score.
        public static int Score(CrewPersonality personality, CrewRole role)
        {
            if (personality == null) return -1;
            int[] w = Weights(role);
            int sum = 0;
            for (int i = 0; i < 5; i++)
            {
                int trait = personality.Get((PersonalityTrait)i);
                if (trait < 0) return -1;
                sum += trait * w[i];
            }
            return sum / 100;
        }
    }

    // ---- Phase 11: personality registry ----------------------------------------
    //
    // Bounded registry of CrewPersonality records keyed by the SAME stable
    // AgentId the Phase 10 CrewAgentRegistry uses. Pure data lookup surface:
    // derivation is on-demand (no tick driver, no per-frame work, no world
    // reads). The registry holds NO PULSAR references and performs NO task,
    // claim, or execution operations.
    //
    // Bounded: at most MaxPersonalities (32) records — matching the P10 agent
    // bound (MaxAgents) — deterministic refusal when full; replacement of an
    // existing agentId is allowed and counted, never a size change.
    public static class CrewPersonalityRegistry
    {
        public const int MaxPersonalities = 32;          // = CrewAgentRegistry.MaxAgents
        public const int MaxArchetypeLength = 32;        // archetype tokens are <= 11 chars

        private sealed class RegistryState
        {
            public readonly Dictionary<string, CrewPersonality> Personalities =
                new Dictionary<string, CrewPersonality>(StringComparer.Ordinal);
            public long Assigned;
            public long Replaced;
            public long Refused;
            public long Derivations;
        }

        private static readonly RegistryState S = new RegistryState();
        private static readonly object m_Lock = new object();
        private static Action<string> m_OnDecision;      // PersonalityLogBridge attaches at boot

        public static void SetDecisionListener(Action<string> listener)
        {
            lock (m_Lock) m_OnDecision = listener;
        }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // ---- readback (diagnostics/tests) ------------------------------------
        public static int Count { get { lock (m_Lock) return S.Personalities.Count; } }
        public static long AssignedCount { get { lock (m_Lock) return S.Assigned; } }
        public static long ReplacedCount { get { lock (m_Lock) return S.Replaced; } }
        public static long RefusedCount { get { lock (m_Lock) return S.Refused; } }
        public static long DerivationCount { get { lock (m_Lock) return S.Derivations; } }

        // Records an explicit personality for an agent. The record's AgentId
        // must match the key (identity integrity); replacement allowed.
        public static bool SetPersonality(string agentId, CrewPersonality personality, int nowMs)
        {
            if (!PersonalityFactory.IsValidAgentId(agentId) || personality == null
                || personality.AgentId != agentId)
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            if (!string.IsNullOrEmpty(personality.Archetype)
                && personality.Archetype.Length > MaxArchetypeLength)
            {
                lock (m_Lock) S.Refused++;
                return false;
            }
            lock (m_Lock)
            {
                bool replaced = S.Personalities.ContainsKey(agentId);
                S.Personalities[agentId] = personality;
                if (replaced) S.Replaced++; else S.Assigned++;
                Emit("PersonalityAssigned " + agentId
                    + " archetype=" + (personality.Archetype ?? "-")
                    + " src=" + (personality.Source ?? "-")
                    + (replaced ? " (replaced)" : ""));
                return true;
            }
        }

        // Derives and registers the identity-derived personality for an agent.
        // Returns the EXISTING record when one is already registered (never a
        // silent replacement); null on invalid agentId.
        public static CrewPersonality DeriveFor(string agentId, int nowMs)
        {
            if (!PersonalityFactory.IsValidAgentId(agentId))
            {
                lock (m_Lock) S.Refused++;
                return null;
            }
            CrewPersonality derived = null;
            lock (m_Lock)
            {
                CrewPersonality existing;
                if (S.Personalities.TryGetValue(agentId, out existing)) return existing;
                if (S.Personalities.Count >= MaxPersonalities)
                {
                    Emit("PersonalityRefused " + agentId + " (registry full)");
                    S.Refused++;
                    return null;
                }
                derived = PersonalityFactory.Derive(agentId, nowMs);
                if (derived == null) { S.Refused++; return null; }
                S.Personalities[agentId] = derived;
                S.Assigned++;
                S.Derivations++;
            }
            Emit("PersonalityAssigned " + agentId
                + " archetype=" + (derived.Archetype ?? "-")
                + " src=" + (derived.Source ?? "-"));
            return derived;
        }

        // Lookup without creation (null when absent or invalid).
        public static CrewPersonality Get(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return null;
            lock (m_Lock)
            {
                CrewPersonality p;
                return S.Personalities.TryGetValue(agentId, out p) ? p : null;
            }
        }

        // Removes a personality record (future-phase lifecycle integration).
        public static bool Remove(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return false;
            lock (m_Lock)
            {
                if (!S.Personalities.Remove(agentId)) return false;
            }
            Emit("PersonalityRemoved " + agentId);
            return true;
        }

        // Convenience: affinity score of an agent's personality for a role.
        // Derives-on-demand for unregistered agents; -1 on invalid agentId.
        public static int AffinityToRole(string agentId, CrewRole role, int nowMs)
        {
            CrewPersonality p = Get(agentId);
            if (p == null) p = DeriveFor(agentId, nowMs);
            return RoleAffinity.Score(p, role);
        }

        // Convenience: archetype of an agent's (possibly derived) personality.
        // Null on invalid agentId.
        public static string ArchetypeOf(string agentId, int nowMs)
        {
            CrewPersonality p = Get(agentId);
            if (p == null) p = DeriveFor(agentId, nowMs);
            return p == null ? null : p.Archetype;
        }

        // One bounded diagnostic line per record (deterministic order).
        public static List<string> Lines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CrewPersonality> kv in S.Personalities)
                {
                    lines.Add(kv.Value.ToLine());
                }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                lines.Add("personalities=" + S.Personalities.Count
                    + " assigned=" + S.Assigned + " replaced=" + S.Replaced
                    + " refused=" + S.Refused + " derivations=" + S.Derivations);
            }
            return lines;
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                S.Personalities.Clear();
                S.Assigned = 0;
                S.Replaced = 0;
                S.Refused = 0;
                S.Derivations = 0;
                m_OnDecision = null;
            }
        }
    }
}