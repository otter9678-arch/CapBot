using System;
using System.Collections.Generic;

namespace CapBot.Core.Capabilities
{
    // ---- Phase 7: safe task capability contracts -----------------------------
    //
    // A capability is an ALLOWLISTED, TYPED OPERATION description — pure
    // data plus a validator delegate. It defines WHAT future tasks/executors
    // may request (the legal operations vocabulary), never HOW anything is
    // executed. The registry performs zero gameplay: it validates requests,
    // tracks cooldowns, and answers lookups. The Phase 8 executor will be
    // the only component allowed to ACT on a validated request, and even it
    // dispatches on CapabilityId via static, code-reviewed branches — the
    // registry never interprets strings as behavior.
    //
    // SECURITY BOUNDARY (master prompt): capabilities are contracts, not
    // executable instructions. There is no arbitrary C# generation, no
    // runtime compilation, no DLL loading, no shell/process execution, no
    // reflection-driven method invocation, and no interpretation of LLM /
    // game / chat / mission text as commands anywhere in this layer. Task
    // metadata and target references are untrusted data: validated as
    // bounded tokens or parsed as integers, then either accepted, rejected,
    // or hashed — never executed.

    // Authority model every capability must declare (master prompt §5):
    //   MasterOnly    — authoritative gameplay; only the host may perform it
    //                   (default for all gameplay-affecting capabilities).
    //   ClientRequest — clients may REQUEST, the master decides/executes
    //                   (vanilla's request→master-authoritative-response
    //                   pattern; routing arrives with the P8 executor).
    //   ClientOnly    — local-only effect on this peer (rare; nothing
    //                   authoritative).
    //   ReadOnly      — observation; no side effects at all.
    public enum CapabilityAuthority : byte
    {
        MasterOnly = 0,
        ClientRequest = 1,
        ClientOnly = 2,
        ReadOnly = 3,
    }

    // Danger classification (bounded, review-visible).
    public enum CapabilityDanger : byte
    {
        Benign = 0,       // no plausible harm (read-style or informational)
        Reversible = 1,   // gameplay effect that can be undone by another capability
        Irreversible = 2, // gameplay effect with no in-game undo path
    }

    // Whether a capability's effect can be undone.
    public enum CapabilityReversibility : byte
    {
        NotApplicable = 0, // no gameplay effect to reverse
        Reversible = 1,    // another registered capability undoes it
        Irreversible = 2,  // no undo path exists
    }

    // Request context handed to validation. Everything in it is data that
    // arrived from the task system or the caller; the registry treats it as
    // untrusted and validates every field.
    public sealed class CapabilityRequest
    {
        // The task requesting the operation (identity + ownership + metadata
        // are read; the registry never mutates tasks).
        public readonly long TaskId;
        public readonly string TaskType;
        public readonly string OwnerActorId;
        public readonly string TargetKind;
        public readonly string TargetId;

        // Raw capability argument (currently unused — reserved so a future
        // capability can carry a bounded, validated payload; untrusted).
        public readonly string Argument;

        public CapabilityRequest(long taskId, string taskType, string ownerActorId,
            string targetKind, string targetId, string argument)
        {
            TaskId = taskId;
            TaskType = taskType ?? string.Empty;
            OwnerActorId = ownerActorId ?? string.Empty;
            TargetKind = targetKind ?? string.Empty;
            TargetId = targetId ?? string.Empty;
            Argument = argument ?? string.Empty;
        }
    }

    // Validation outcome (deterministic, never throws).
    public enum CapabilityValidation : byte
    {
        Approved = 0,
        RejectedUnknownCapability = 1,
        RejectedDisabled = 2,
        RejectedInvalidRequest = 3,       // malformed task id / owner / target data
        RejectedActorNotAllowed = 4,      // owner not in AllowedOwners
        RejectedAuthority = 5,            // authority model not satisfied by this process
        RejectedTargetInvalid = 6,        // target kind/id fails the capability's rules
        RejectedPrecondition = 7,         // a declared precondition evaluated false
        RejectedTaskMismatch = 8,         // task type not in the capability's task types
        RejectedOwnershipMismatch = 9,    // requester does not own the task
        RejectedCooldown = 10,            // capability rate limit active
        RejectedClaimConflict = 11,       // P5: duplicate-execution / active claim conflict
        RejectedWorldStateStale = 12,     // P6: snapshot too old to validate against
        RejectedWorldStateMissing = 13,   // P6: no captured snapshot and one is required
        RejectedRegistryFull = 14,        // registration attempt beyond the bounded cap
    }

    // Target requirement vocabulary. TargetKind strings on tasks are free-form
    // tags; a capability accepts only the kinds it declares.
    public enum TargetRequirement : byte
    {
        None = 0,        // target ignored entirely
        SectorId = 1,    // TargetKind=="SECTOR", TargetId parses as int
        ShipId = 2,      // TargetKind=="SHIP", TargetId parses as int
        MissionId = 3,   // TargetKind=="MISSION", TargetId parses as int
        BoundedToken = 4 // TargetId must be a bounded static token [A-Za-z0-9_], <= 32
    }

    // The per-capability contract. Immutable after construction; every field
    // is either static text, a bounded vocabulary value, or a pure delegate.
    public sealed class CapabilityDescriptor
    {
        public const int MaxIdLength = 32;
        public const int MaxNameLength = 64;
        public const int MaxDescriptionLength = 256;
        public const int MaxAllowedOwners = 8;
        public const int MaxTaskTypes = 8;
        public const int MaxTargetKinds = 8;

        // Stable identity: bounded static token [A-Za-z0-9_], used for exact
        // lookup (Ordinal) and logging. Never parsed into code paths by the
        // registry itself — dispatch on it is the executor's job (static
        // branches, code-reviewed).
        public readonly string CapabilityId;
        public readonly string Name;
        public readonly string Description;

        // Who may request it. Empty list = no owner restriction (any valid
        // task owner) — still gated by authority + ownership + preconditions.
        public readonly IReadOnlyList<string> AllowedOwners;

        // Multiplayer authority model (default MasterOnly for gameplay).
        public readonly CapabilityAuthority Authority;

        // Danger + reversibility classification.
        public readonly CapabilityDanger Danger;
        public readonly CapabilityReversibility Reversibility;

        // Task-type allowlist (empty = any task type may request it).
        public readonly IReadOnlyList<string> TaskTypes;

        // Target rules.
        public readonly TargetRequirement TargetReq;
        public readonly IReadOnlyList<string> AllowedTargetKinds; // exact-match kinds when TargetReq != None

        // Rate limiting: minimum ms between APPROVED requests (0 = none).
        // Bounded per-capability state; enforced by the registry.
        public readonly int CooldownMs;

        // Must the P6 world snapshot be fresh (<= MaxSnapshotAgeMs) for
        // validation? Read-only capabilities set this false.
        public readonly bool RequiresFreshWorldState;

        // Pure validators (no side effects, no clock reads, no game access —
        // inputs only). Null = always true.
        public readonly Predicate<CapabilityRequest> Precondition;   // declared gameplay/task preconditions
        public readonly Predicate<CapabilityRequest> TargetValidator; // extra target validation beyond kind/id shape

        // The verified PULSAR API/RPC this capability documents (static text
        // for the contract table + docs; the executor owns the actual call).
        public readonly string VerifiedApi;

        // Logging category (CapBotLog subsystem tag name — static vocabulary).
        public readonly string LoggingCategory;

        public CapabilityDescriptor(
            string capabilityId, string name, string description,
            IEnumerable<string> allowedOwners, CapabilityAuthority authority,
            CapabilityDanger danger, CapabilityReversibility reversibility,
            IEnumerable<string> taskTypes, TargetRequirement targetReq,
            IEnumerable<string> allowedTargetKinds, int cooldownMs,
            bool requiresFreshWorldState,
            Predicate<CapabilityRequest> precondition,
            Predicate<CapabilityRequest> targetValidator,
            string verifiedApi, string loggingCategory)
        {
            CapabilityId = capabilityId;
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            List<string> owners = new List<string>();
            if (allowedOwners != null)
            {
                foreach (string o in allowedOwners)
                {
                    if (string.IsNullOrEmpty(o)) continue;
                    if (owners.Count >= MaxAllowedOwners) break;
                    owners.Add(o);
                }
            }
            AllowedOwners = owners;
            Authority = authority;
            Danger = danger;
            Reversibility = reversibility;
            List<string> types = new List<string>();
            if (taskTypes != null)
            {
                foreach (string t in taskTypes)
                {
                    if (string.IsNullOrEmpty(t) || types.Count >= MaxTaskTypes) continue;
                    types.Add(t);
                    if (types.Count >= MaxTaskTypes) break;
                }
            }
            TaskTypes = types;
            TargetReq = targetReq;
            List<string> kinds = new List<string>();
            if (allowedTargetKinds != null)
            {
                foreach (string k in allowedTargetKinds)
                {
                    if (string.IsNullOrEmpty(k) || kinds.Count >= MaxTargetKinds) continue;
                    kinds.Add(k);
                    if (kinds.Count >= MaxTargetKinds) break;
                }
            }
            AllowedTargetKinds = kinds;
            CooldownMs = cooldownMs < 0 ? 0 : cooldownMs;
            RequiresFreshWorldState = requiresFreshWorldState;
            Precondition = precondition;
            TargetValidator = targetValidator;
            VerifiedApi = verifiedApi ?? string.Empty;
            LoggingCategory = loggingCategory ?? string.Empty;
        }

        // Deterministic one-line contract summary (bounded, logging-safe).
        public string ToContractLine()
        {
            return CapabilityId + " auth=" + Authority + " danger=" + Danger
                + " reversibility=" + Reversibility + " cooldownMs=" + CooldownMs
                + " owners=" + AllowedOwners.Count + " taskTypes=" + TaskTypes.Count
                + " freshWorld=" + (RequiresFreshWorldState ? "1" : "0")
                + " api=" + VerifiedApi;
        }
    }
}