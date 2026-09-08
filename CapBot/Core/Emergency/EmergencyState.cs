using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;

namespace CapBot.Core.Emergency
{
    // ---- Phase 9: emergency director — deterministic override layer ----------
    //
    // A PRIORITY OVERRIDE/DECISION layer: reads authoritative Phase 6 world
    // state, detects urgent threats with deterministic rules, and produces
    // emergency decisions (bounded data) plus emergency-priority tasks routed
    // through the EXISTING contracts — CapBotTask lifecycle (P2), recovery
    // (P3), scheduler (P4), claims (P5), capability registry (P7), executor
    // (P8). It is NOT a brain, NOT Captain Brain 2.0, NOT an LLM planner:
    // no Ollama/Qwen, no natural-language decisions, no chat/NPC/mission-text
    // parsing anywhere in this layer.
    //
    // CORE PRINCIPLE — deterministic and fail-safe:
    //   - Emergency handling never requires an LLM. Every decision is a pure
    //     function of (verified snapshot data, bounded rule table, clock).
    //   - If the director cannot PROVE an emergency is real (missing/stale/
    //     contradictory/invalid world data), it fails SAFE: it marks the
    //     situation uncertain, logs it, and takes NO destructive action.
    //   - Detection inputs are verified PULSAR surfaces only (documented in
    //     docs/EMERGENCY.md); no telemetry is invented.
    //
    // BOUNDARIES (all inherited from P2–P8, none bypassed):
    //   - The director never executes a capability. It creates/queues tasks
    //     through CapBotTask + TaskRegistry and lets the scheduler grant and
    //     the P8 executor claim/validate/execute them.
    //   - Preemption is REQUESTED through the scheduler's existing policy-gated
    //     path (metadata Preemptible=true on emergency tasks + priority
    //     ordering); the director never pauses or fails other systems' tasks.
    //   - No RPCs, no gameplay mutation, no reflection-as-execution, no
    //     runtime compilation, no arbitrary method invocation.
    //
    // Multiplayer: the director is master-side only (the driver gates Tick on
    // the authority seam). Clients never produce emergency tasks.

    // ---- severity -----------------------------------------------------------
    // Deterministic severity ladder; ordinal order IS precedence. Emergency
    // task priority is derived from severity (PriorityForSeverity), so the
    // scheduler's existing effective-priority ordering + preemption margin
    // handles "higher emergency classes outrank lower-priority tasks" without
    // the director touching scheduler internals.
    public enum EmergencySeverity : byte
    {
        None = 0,
        Warning = 1,
        Elevated = 2,
        Severe = 3,
        Critical = 4,
    }

    // ---- emergency types (bounded static vocabulary) ------------------------
    // ONLY types whose detection inputs are VERIFIED PULSAR APIs (Assembly-
    // CSharp reflection + compile-proven shipped reads) ship a detection rule.
    // IDs are bounded tokens ([A-Za-z0-9_], <= 32) — the same vocabulary as
    // capability ids and action kinds — so they can never carry behavior.
    public enum EmergencyType : byte
    {
        None = 0,
        CriticalHull = 1,
        CriticalCrewHealth = 2,
        Fire = 3,
        ReactorCritical = 4,
        DangerousCombat = 5,
        ImminentDeath = 6,
        NavigationFailure = 7,
        FuelCritical = 8,
        CoolantCritical = 9,
        ObjectiveCritical = 10,
        // WarpFailure: warp state IS readable (InWarp/WarpChargeStage) but no
        // verified rule separates "failing" from "normal charging" yet —
        // deliberately NOT detected in this phase (documented unsupported).
    }

    // ---- emergency states + legal transitions -------------------------------
    // Normal -> Monitoring -> Warning -> Emergency -> Critical -> Recovery ->
    // Normal. Escalation is monotone upward within one evaluation; de-
    // escalation only passes through Recovery. Illegal transitions are
    // rejected (never applied), so a bad rule or a flapping snapshot cannot
    // corrupt the state machine. Hysteresis: every transition requires its
    // configured dwell time (MinDwellMs per transition) — tiny state changes
    // cannot oscillate the machine.
    public enum EmergencyState : byte
    {
        Normal = 0,
        Monitoring = 1,
        Warning = 2,
        Emergency = 3,
        Critical = 4,
        Recovery = 5,
    }

    public static class EmergencyStates
    {
        // Legal transition table (symmetric between adjacent rungs; Recovery
        // is the ONLY re-entry to Normal). First match wins.
        public static bool CanTransition(EmergencyState from, EmergencyState to)
        {
            switch (from)
            {
                case EmergencyState.Normal: return to == EmergencyState.Monitoring;
                case EmergencyState.Monitoring: return to == EmergencyState.Warning || to == EmergencyState.Normal;
                case EmergencyState.Warning: return to == EmergencyState.Emergency || to == EmergencyState.Recovery;
                case EmergencyState.Emergency: return to == EmergencyState.Critical || to == EmergencyState.Recovery;
                case EmergencyState.Critical: return to == EmergencyState.Recovery;
                case EmergencyState.Recovery: return to == EmergencyState.Normal;
                default: return false;
            }
        }

        // Human-readable rejection reason; empty string when legal.
        public static string IllegalReason(EmergencyState from, EmergencyState to)
        {
            if (CanTransition(from, to)) return string.Empty;
            return "illegal emergency state transition " + from + " -> " + to;
        }
    }

    // ---- precedence hierarchy (the user's 9-class order) ---------------------
    // Higher class value outranks lower. Emergency task priorities are derived
    // from the class so the scheduler's normal ordering implements the
    // override — no scheduler redesign. Normal (non-emergency) work ranges
    // 1..8; every emergency class starts above that band.
    public static class EmergencyPrecedence
    {
        // Class constants (bounded, documented; not parsed from data).
        public const int ClassMaintenance = 1;      // 8. maintenance
        public const int ClassEconomy = 2;          // 9. economy / research / exploration
        public const int ClassMission = 3;          // 6. mission preservation
        public const int ClassNavigation = 4;       // 5. navigation safety
        public const int ClassCombat = 5;           // 4. combat survival
        public const int ClassCatastrophe = 6;      // 3. immediate catastrophic emergency
        public const int ClassShipSurvival = 7;     // 2. critical ship survival
        public const int ClassCrewSurvival = 8;     // 1. crew survival (highest)

        // Base priority for an emergency task of this class. Priorities are
        // spaced so even a max-aged normal task (priority + 5 aging bonus)
        // cannot outrank the lowest emergency class.
        public const int BasePriorityOffset = 100;

        // Deterministic mapping: type -> precedence class.
        public static int ClassFor(EmergencyType type)
        {
            switch (type)
            {
                case EmergencyType.CriticalHull: return ClassShipSurvival;
                case EmergencyType.CriticalCrewHealth: return ClassCrewSurvival;
                case EmergencyType.Fire: return ClassCatastrophe;
                case EmergencyType.ReactorCritical: return ClassCatastrophe;
                case EmergencyType.DangerousCombat: return ClassCombat;
                case EmergencyType.ImminentDeath: return ClassCrewSurvival;
                case EmergencyType.NavigationFailure: return ClassNavigation;
                case EmergencyType.FuelCritical: return ClassNavigation;
                case EmergencyType.CoolantCritical: return ClassCatastrophe;
                case EmergencyType.ObjectiveCritical: return ClassMission;
                default: return ClassEconomy;
            }
        }

        // Deterministic priority: severity adds a bounded bump inside the
        // class so a Critical fire outranks an Elevated fire, while the class
        // remains dominant. Total stays far below any other class's floor.
        public static int PriorityFor(EmergencyType type, EmergencySeverity severity)
        {
            int bump = 0;
            if (severity == EmergencySeverity.Severe) bump = 1;
            else if (severity == EmergencySeverity.Critical) bump = 2;
            return BasePriorityOffset + ClassFor(type) * 10 + bump;
        }
    }

    // ---- emergency decision (bounded immutable data) ------------------------
    // A structured description of one detected emergency. It is DATA ONLY —
    // carried, logged, hashed into identities; never parsed into behavior.
    // The director does NOT execute the RequiredCapability: the decision is
    // realized as a lifecycle task, and the executor owns dispatch exactly
    // as it does for every other task.
    public sealed class EmergencyDecision
    {
        public const int MaxReasonLength = 200;
        public const int MaxTargetLength = 64;

        public readonly string EmergencyId;          // deterministic event identity (EID:<type>:<key>)
        public readonly EmergencyType EmergencyType;
        public readonly EmergencySeverity Severity;
        public readonly int DetectedAtMs;
        public readonly string AffectedActor;        // e.g. "SHIP", "CAPTAIN", "BOT:<id>" — data only
        public readonly long AffectedTaskId;         // task the director created/updated (0 = none)
        public readonly int Priority;                // derived precedence (EmergencyPrecedence)
        public readonly string Reason;               // bounded static-ish detection reason (truncated)
        public readonly string RequiredCapability;   // registered CapabilityId or "" (advisory; executor still validates)
        public readonly string TargetReference;      // opaque data (never parsed as commands)
        public readonly string AuthorityRequirement; // "MasterOnly" / "ReadOnly" — data only
        public readonly string Preconditions;        // bounded note for humans/logs
        public readonly int ExpirationMs;            // absolute clock ms after which the decision is stale
        public readonly int EscalationState;         // raw EmergencyState at emission
        public readonly string RecoveryPolicy;       // static vocabulary: "LIFECYCLE" (P3 recovery owns retry/abandon)

        public EmergencyDecision(
            string emergencyId, EmergencyType emergencyType, EmergencySeverity severity,
            int detectedAtMs, string affectedActor, long affectedTaskId, int priority,
            string reason, string requiredCapability, string targetReference,
            string authorityRequirement, string preconditions, int expirationMs,
            int escalationState, string recoveryPolicy)
        {
            EmergencyId = emergencyId ?? string.Empty;
            EmergencyType = emergencyType;
            Severity = severity;
            DetectedAtMs = detectedAtMs;
            AffectedActor = Truncate(affectedActor, MaxTargetLength);
            AffectedTaskId = affectedTaskId;
            Priority = priority;
            Reason = Truncate(reason, MaxReasonLength);
            RequiredCapability = requiredCapability ?? string.Empty;
            TargetReference = Truncate(targetReference, MaxTargetLength);
            AuthorityRequirement = authorityRequirement ?? string.Empty;
            Preconditions = Truncate(preconditions, MaxReasonLength);
            ExpirationMs = expirationMs;
            EscalationState = escalationState;
            RecoveryPolicy = recoveryPolicy ?? string.Empty;
        }

        public string ToStatusLine()
        {
            return "EMERGENCY " + EmergencyId + " type=" + EmergencyType + " sev=" + Severity
                + " pri=" + Priority + " task=" + AffectedTaskId
                + " actor=" + (AffectedActor ?? "?")
                + " esc=" + EscalationState;
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }

    // ---- active emergency record (deduplication) -----------------------------
    // One live emergency per deterministic identity. While active, the same
    // underlying emergency NEVER re-creates tasks: re-evaluations refresh the
    // record's LastSeenMs and severity at most. Identities are deterministic
    // (type + stable-hash of the affected subject), so repeated detection
    // passes and even host migrations cannot create duplicate floods.
    public sealed class ActiveEmergency
    {
        public readonly string EmergencyId;
        public readonly EmergencyType EmergencyType;
        public readonly long TaskId;                 // the emergency task created for it
        public readonly int FirstSeenMs;
        public int LastSeenMs;                       // refreshed on re-detection
        public EmergencySeverity Severity;           // may escalate; de-escalation is handled by state machine
        public int TaskCreatedMs;
        // P39 no-progress breaker bookkeeping lives in the director's
        // SuppressionGate (keyed by EmergencyId, survives record resolution
        // so a resolve-without-fix cycle accumulates evidence across tasks).
        // The record itself stays a pure data holder.

        public ActiveEmergency(string emergencyId, EmergencyType type, long taskId, int firstSeenMs, EmergencySeverity severity)
        {
            EmergencyId = emergencyId;
            EmergencyType = type;
            TaskId = taskId;
            FirstSeenMs = firstSeenMs;
            LastSeenMs = firstSeenMs;
            Severity = severity;
            TaskCreatedMs = firstSeenMs;
        }
    }

    // ---- deterministic identity helper --------------------------------------
    // "<EID:<type>>:<hash8>" from the emergency type + a small stable key
    // (subject identity, e.g. ship id or crew player id). Reuses ActionIden-
    // tity's FNV-1a so hashes are identical across the mod's identity layers.
    public static class EmergencyIdentity
    {
        public const int MaxKeyLength = 64;

        public static string MakeEmergencyId(EmergencyType type, string subjectKey)
        {
            string key = subjectKey ?? string.Empty;
            if (key.Length > MaxKeyLength) key = key.Substring(0, MaxKeyLength);
            uint h = ActionIdentity.ComputeStableHash(((int)type).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + key);
            return "EID:" + type.ToString().ToUpperInvariant() + ":" + h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}