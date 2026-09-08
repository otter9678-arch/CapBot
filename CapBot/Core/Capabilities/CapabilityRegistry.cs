using System;
using System.Collections.Generic;
using CapBot.Core.Tasks;
using CapBot.Core.World;

namespace CapBot.Core.Capabilities
{
    // ---- Phase 7: capability registry ----------------------------------------
    //
    // Bounded, deterministic allowlist of the gameplay operations future
    // tasks/executors may request. The registry VALIDATES requests against
    // capability contracts; it never performs gameplay, never executes
    // tasks, never calls PULSAR APIs. Validation is a pure gate ladder:
    // unknown → disabled → malformed → actor → authority → target →
    // precondition → task mismatch → ownership → cooldown → P5 claim
    // conflict → P6 world-state freshness. First failure wins, so exactly
    // one deterministic outcome per request.
    //
    // Seams (all optional; production wires them at boot, tests inject):
    //   Func<bool> authorityProbe       — "is this process authoritative?"
    //   Func<int>  nowMsProvider        — time source (TaskClock.NowMs)
    //   Func<WorldSnapshot> worldProbe  — latest P6 snapshot (WorldStateService.Latest)
    //   Func<long, string, bool> claimProbe — P5 duplicate/claim conflict
    //                                     check: (taskId, actionId) → claimed-or-succeeded
    //
    // The registry itself holds NO world state and NO claim state — it
    // consults the P5/P6 layers through the seams (master prompt §6: world
    // ownership stays outside the registry).
    public static class CapabilityRegistry
    {
        public const int MaxCapabilities = 32;

        private static readonly Dictionary<string, CapabilityDescriptor> m_Capabilities =
            new Dictionary<string, CapabilityDescriptor>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> m_LastApprovedMs =
            new Dictionary<string, int>(StringComparer.Ordinal); // capabilityId -> last approved nowMs
        private static readonly Dictionary<string, bool> m_Disabled =
            new Dictionary<string, bool>(StringComparer.Ordinal);
        private static readonly object m_Lock = new object();

        // ---- seams -----------------------------------------------------------
        private static Func<bool> m_AuthorityProbe;          // null => authority never satisfied (fail-closed)
        private static Func<int> m_NowMsProvider;            // null => cooldowns disabled (tests)
        private static Func<WorldSnapshot> m_WorldProvider;  // null => world checks reject when required
        private static Func<long, string, bool> m_ClaimProbe; // null => no claim conflicts detected

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_AuthorityProbe = probe; }
        public static void SetNowMsProvider(Func<int> provider) { lock (m_Lock) m_NowMsProvider = provider; }
        public static void SetWorldProvider(Func<WorldSnapshot> provider) { lock (m_Lock) m_WorldProvider = provider; }
        public static void SetClaimProbe(Func<long, string, bool> probe) { lock (m_Lock) m_ClaimProbe = probe; }

        // Diagnostic hook: one line per validation decision (bridge attaches
        // at boot). Fired outside the lock, never throws into the caller.
        private static Action<string> m_OnDecision;
        public static void SetDecisionListener(Action<string> listener) { lock (m_Lock) m_OnDecision = listener; }

        private static void Emit(string line)
        {
            Action<string> l;
            lock (m_Lock) l = m_OnDecision;
            if (l != null) l(line);
        }

        // ---- registration ------------------------------------------------------
        // Duplicate IDs are rejected safely (false, no mutation). Unknown or
        // malformed descriptors never enter the registry.
        public static bool Register(CapabilityDescriptor capability)
        {
            if (capability == null) return false;
            if (!IsValidCapabilityId(capability.CapabilityId)) return false;
            lock (m_Lock)
            {
                if (m_Capabilities.ContainsKey(capability.CapabilityId)) return false;
                if (m_Capabilities.Count >= MaxCapabilities) return false;
                m_Capabilities[capability.CapabilityId] = capability;
                m_Disabled[capability.CapabilityId] = false;
            }
            Emit("CapabilityRegistered " + capability.CapabilityId);
            return true;
        }

        // Stable IDs: bounded static token [A-Za-z0-9_] — same vocabulary as
        // ActionIdentity's actionKind, so CapabilityId is embeddable in
        // action ids later without widening the parser.
        private static bool IsValidCapabilityId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > CapabilityDescriptor.MaxIdLength) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        // ---- lookup (cheap, deterministic) --------------------------------------
        public static bool IsRegistered(string capabilityId)
        {
            if (string.IsNullOrEmpty(capabilityId)) return false;
            lock (m_Lock) return m_Capabilities.ContainsKey(capabilityId);
        }

        // Exact-match snapshot; no per-call allocation of the dictionary.
        public static CapabilityDescriptor Get(string capabilityId)
        {
            if (string.IsNullOrEmpty(capabilityId)) return null;
            lock (m_Lock)
            {
                CapabilityDescriptor d;
                return m_Capabilities.TryGetValue(capabilityId, out d) ? d : null;
            }
        }

        public static int Count { get { lock (m_Lock) return m_Capabilities.Count; } }

        // Bounded point-in-time list of registered ids (diagnostics/tests).
        public static List<string> RegisteredIds()
        {
            lock (m_Lock) return new List<string>(m_Capabilities.Keys);
        }

        // ---- enable/disable ------------------------------------------------------
        // A disabled capability stays registered (lookup still answers) but
        // validation refuses every request. Unknown capabilities can never be
        // enabled or disabled.
        public static bool SetEnabled(string capabilityId, bool enabled)
        {
            if (string.IsNullOrEmpty(capabilityId)) return false;
            lock (m_Lock)
            {
                if (!m_Capabilities.ContainsKey(capabilityId)) return false;
                m_Disabled[capabilityId] = !enabled;
                return true;
            }
        }

        public static bool IsEnabled(string capabilityId)
        {
            if (string.IsNullOrEmpty(capabilityId)) return false;
            lock (m_Lock)
            {
                bool disabled;
                return m_Capabilities.ContainsKey(capabilityId) && !(m_Disabled.TryGetValue(capabilityId, out disabled) && disabled);
            }
        }

        // ---- validation -----------------------------------------------------------
        // The gate ladder. Deterministic: same inputs (+ same seam state) ⇒
        // same outcome. Never throws; never mutates tasks; never performs
        // gameplay. taskType is taken from the request (callers pass the
        // live task's type — the ownership gate below requires it match the
        // registry's copy).
        public static CapabilityValidation Validate(string capabilityId, CapabilityRequest request, CapBotTask task)
        {
            if (string.IsNullOrEmpty(capabilityId)) return Reject(request, CapabilityValidation.RejectedUnknownCapability, "null capability id");
            CapabilityDescriptor d;
            lock (m_Lock)
            {
                if (!m_Capabilities.TryGetValue(capabilityId, out d)) return Reject(request, CapabilityValidation.RejectedUnknownCapability, capabilityId);
            }
            return ValidateDescriptor(d, request, task);
        }

        // Overload for pre-resolved descriptors (same ladder, skips lookup).
        public static CapabilityValidation Validate(CapabilityDescriptor d, CapabilityRequest request, CapBotTask task)
        {
            if (d == null) return Reject(request, CapabilityValidation.RejectedUnknownCapability, "null descriptor");
            return ValidateDescriptor(d, request, task);
        }

        private static CapabilityValidation ValidateDescriptor(CapabilityDescriptor d, CapabilityRequest request, CapBotTask task)
        {
            // 1) malformed request data (before anything else — untrusted input)
            if (request == null) return Reject(request, CapabilityValidation.RejectedInvalidRequest, "null request");
            if (task == null) return Reject(request, CapabilityValidation.RejectedInvalidRequest, "null task");
            if (task.TaskId <= 0) return Reject(request, CapabilityValidation.RejectedInvalidRequest, "invalid task id");
            if (string.IsNullOrEmpty(task.OwnerActorId) || task.OwnerActorId.Length > 64)
                return Reject(request, CapabilityValidation.RejectedInvalidRequest, "invalid owner id");
            if (request.TaskId <= 0)
                return Reject(request, CapabilityValidation.RejectedInvalidRequest, "invalid request task id");
            if (string.IsNullOrEmpty(request.OwnerActorId) || request.OwnerActorId.Length > 64)
                return Reject(request, CapabilityValidation.RejectedInvalidRequest, "invalid request owner");

            // 2) disabled (registered but switched off)
            if (!IsEnabled(d.CapabilityId)) return Reject(request, CapabilityValidation.RejectedDisabled, d.CapabilityId);

            // 3) actor allowlist
            if (d.AllowedOwners.Count > 0 && !ContainsExact(d.AllowedOwners, task.OwnerActorId))
                return Reject(request, CapabilityValidation.RejectedActorNotAllowed, d.CapabilityId + " owner=" + task.OwnerActorId);

            // 4) authority model
            if (!IsAuthoritySatisfied(d.Authority))
                return Reject(request, CapabilityValidation.RejectedAuthority, d.CapabilityId + " requires " + d.Authority);

            // 5) target requirements (kind + shape, then extra validator)
            if (!IsTargetAcceptable(d, request))
                return Reject(request, CapabilityValidation.RejectedTargetInvalid, d.CapabilityId + " target=" + request.TargetKind + ":" + request.TargetId);

            // 6) declared precondition
            if (d.Precondition != null && !SafePredicate(d.Precondition, request))
                return Reject(request, CapabilityValidation.RejectedPrecondition, d.CapabilityId);

            // 7) task-type allowlist
            if (d.TaskTypes.Count > 0 && !ContainsExact(d.TaskTypes, task.TaskType))
                return Reject(request, CapabilityValidation.RejectedTaskMismatch, d.CapabilityId + " taskType=" + task.TaskType);

            // 8) ownership: the task object presented must BE the registered
            //    live task (identity equality by TaskId, still non-terminal —
            //    TaskRegistry.Get also resolves history, so a terminal task
            //    found there must not authorize anything), the request must
            //    reference the task it was validated against, and the
            //    request's claimed owner must match the live task's owner.
            CapBotTask live = TaskRegistry.Get(task.TaskId);
            if (live == null || !live.Equals(task) || live.IsTerminal)
                return Reject(request, CapabilityValidation.RejectedOwnershipMismatch, d.CapabilityId + " task #" + task.TaskId + " not live");
            if (request.TaskId != task.TaskId)
                return Reject(request, CapabilityValidation.RejectedOwnershipMismatch, d.CapabilityId + " request task #" + request.TaskId + " != task #" + task.TaskId);
            if (!string.Equals(live.OwnerActorId, request.OwnerActorId, StringComparison.Ordinal))
                return Reject(request, CapabilityValidation.RejectedOwnershipMismatch, d.CapabilityId + " owner=" + request.OwnerActorId + " vs task owner=" + live.OwnerActorId);

            // 9) cooldown
            Func<int> nowProvider;
            lock (m_Lock) nowProvider = m_NowMsProvider;
            int nowMs = -1;
            if (nowProvider != null)
            {
                try { nowMs = nowProvider(); }
                catch (Exception) { nowMs = -1; } // clock fault = cooldown checks disabled this call
            }
            if (d.CooldownMs > 0 && nowMs >= 0)
            {
                lock (m_Lock)
                {
                    int last;
                    if (m_LastApprovedMs.TryGetValue(d.CapabilityId, out last)
                        && unchecked(nowMs - last) < d.CooldownMs)
                        return Reject(request, CapabilityValidation.RejectedCooldown, d.CapabilityId + " " + unchecked(nowMs - last) + "ms < " + d.CooldownMs + "ms");
                }
            }

            // 10) P5 duplicate-execution / claim conflict (via seam)
            Func<long, string, bool> claimProbe;
            lock (m_Lock) claimProbe = m_ClaimProbe;
            if (claimProbe != null)
            {
                string actionId = ActionIdentity.MakeActionId(
                    task.TaskId, ActionKindFor(d), AttemptEpochFor(task), request.TargetId);
                if (actionId == null)
                    return Reject(request, CapabilityValidation.RejectedInvalidRequest, "action identity unbuildable");
                bool conflict;
                try { conflict = claimProbe(task.TaskId, actionId); }
                catch (Exception) { conflict = true; } // seam fault = fail-closed
                if (conflict) return Reject(request, CapabilityValidation.RejectedClaimConflict, d.CapabilityId + " " + actionId);
            }

            // 11) P6 world-state freshness (via seam)
            Func<WorldSnapshot> worldProvider;
            lock (m_Lock) worldProvider = m_WorldProvider;
            if (d.RequiresFreshWorldState)
            {
                WorldSnapshot snap = null;
                if (worldProvider != null)
                {
                    try { snap = worldProvider(); }
                    catch (Exception) { snap = null; }
                }
                if (snap == null || snap.IsNeverCaptured)
                    return Reject(request, CapabilityValidation.RejectedWorldStateMissing, d.CapabilityId);
                int now = nowMs >= 0 ? nowMs : TaskClock.NowMs;
                if (unchecked(now - snap.SnapshotTimeMs) > WorldStateService.MaxSnapshotAgeMs)
                    return Reject(request, CapabilityValidation.RejectedWorldStateStale, d.CapabilityId);
            }

            // Approved — stamp cooldown with the caller's clock.
            if (d.CooldownMs > 0 && nowMs >= 0)
            {
                lock (m_Lock) m_LastApprovedMs[d.CapabilityId] = nowMs;
            }
            Emit("CapabilityApproved " + d.CapabilityId + " #" + task.TaskId + " owner=" + task.OwnerActorId);
            return CapabilityValidation.Approved;
        }

        // The action kind an approved request would carry into P5 identity:
        // the CapabilityId itself (same validated vocabulary — a CapabilityId
        // IS a valid actionKind by construction).
        private static string ActionKindFor(CapabilityDescriptor d) { return d.CapabilityId; }

        private static int AttemptEpochFor(CapBotTask task) { return task.RetryCount; }

        // Authority probe: null probe = fail-closed. MasterOnly/ClientRequest
        // require an authoritative process; ClientOnly/ReadOnly always pass
        // (no authoritative effect).
        private static bool IsAuthoritySatisfied(CapabilityAuthority authority)
        {
            if (authority == CapabilityAuthority.ClientOnly || authority == CapabilityAuthority.ReadOnly) return true;
            Func<bool> probe;
            lock (m_Lock) probe = m_AuthorityProbe;
            return probe != null && SafeInvoke(probe);
        }

        private static bool SafeInvoke(Func<bool> probe)
        {
            try { return probe(); }
            catch (Exception) { return false; } // seam fault = fail-closed
        }

        private static bool IsTargetAcceptable(CapabilityDescriptor d, CapabilityRequest request)
        {
            if (d.TargetReq == TargetRequirement.None) return true;

            // Kind allowlist (exact match).
            if (d.AllowedTargetKinds.Count > 0 && !ContainsExact(d.AllowedTargetKinds, request.TargetKind))
                return false;

            // Id shape per requirement. TargetId is untrusted data.
            string id = request.TargetId;
            if (string.IsNullOrEmpty(id)) return false;
            if (d.TargetReq == TargetRequirement.SectorId || d.TargetReq == TargetRequirement.ShipId || d.TargetReq == TargetRequirement.MissionId)
            {
                int parsed;
                if (!int.TryParse(id, out parsed) || parsed < 0) return false;
            }
            else if (d.TargetReq == TargetRequirement.BoundedToken)
            {
                if (id.Length > ActionIdentity.MaxActionKindLength) return false;
                for (int i = 0; i < id.Length; i++)
                {
                    char c = id[i];
                    bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                    if (!ok) return false;
                }
            }

            // Extra validator (pure; fault = reject).
            if (d.TargetValidator != null && !SafePredicate(d.TargetValidator, request)) return false;
            return true;
        }

        private static bool SafePredicate(Predicate<CapabilityRequest> p, CapabilityRequest r)
        {
            try { return p(r); }
            catch (Exception) { return false; } // validator fault = reject (fail-closed)
        }

        private static bool ContainsExact(IReadOnlyList<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // Deterministic diagnostics: per-capability "id|enabled|lastApprovedMsAgo".
        public static List<string> StatusLines(int nowMs)
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                foreach (KeyValuePair<string, CapabilityDescriptor> kv in m_Capabilities)
                {
                    bool disabled = false;
                    m_Disabled.TryGetValue(kv.Key, out disabled);
                    int last;
                    string ago = "-";
                    if (m_LastApprovedMs.TryGetValue(kv.Key, out last) && nowMs >= 0)
                        ago = unchecked(nowMs - last).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    lines.Add(kv.Key + "|" + (disabled ? "disabled" : "enabled") + "|" + kv.Value.Authority + "|approvedAgo=" + ago);
                }
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        private static CapabilityValidation Reject(CapabilityRequest request, CapabilityValidation outcome, string detail)
        {
            string owner = request != null ? request.OwnerActorId : "?";
            long id = request != null ? request.TaskId : 0;
            Emit("CapabilityRejected " + outcome + " cap=" + (detail ?? "?") + " task=#" + id + " owner=" + owner);
            return outcome;
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Capabilities.Clear();
                m_LastApprovedMs.Clear();
                m_Disabled.Clear();
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_ClaimProbe = null;
                m_OnDecision = null;
            }
        }
    }
}