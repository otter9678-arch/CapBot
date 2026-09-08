using System;
using System.Collections.Generic;
using CapBot.Core.Capabilities;
using CapBot.Core.Tasks;

namespace CapBot.Core.Executor
{
    // ---- Phase 8: execution result contract ---------------------------------
    //
    // The single outcome vocabulary every executor attempt reports. One
    // deterministic result per attempt; no attempt ever throws across the
    // seam. Outcomes map to lifecycle as follows (TaskExecutor):
    //   Success          -> task completes
    //   FailureRetryable -> task fails; Phase 3 recovery owns retry policy
    //   FailurePermanent -> task fails; Phase 3 recovery owns abandon policy
    //   Rejected         -> a gate refused the attempt before/around the
    //                       capability action; task resolves through the
    //                       lifecycle contract (never silent)
    //   Unavailable      -> the safe pathway itself is not available right
    //                       now (no dispatcher, missing game surface)
    //   Cancelled        -> the attempt was aborted without performing the
    //                       capability action
    public enum ExecutionOutcome : byte
    {
        Success = 0,
        FailureRetryable = 1,
        FailurePermanent = 2,
        Rejected = 3,
        Unavailable = 4,
        Cancelled = 5,
    }

    // Immutable-ish result holder: outcome + bounded reason + bounded
    // diagnostic metadata. Everything is data — never code, never executed.
    // Metadata keys/values are bounded so a misbehaving dispatcher cannot
    // grow memory; AddMetadata silently refuses overflow (diagnostics only).
    public sealed class ExecutionResult
    {
        public const int MaxReasonLength = 200;        // matches CapBotTask reason truncation
        public const int MaxMetadataEntries = 8;
        public const int MaxMetadataKeyLength = 32;
        public const int MaxMetadataValueLength = 128;

        public readonly ExecutionOutcome Outcome;
        public readonly string Reason;

        private readonly Dictionary<string, string> m_Metadata =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Metadata { get { return m_Metadata; } }

        private ExecutionResult(ExecutionOutcome outcome, string reason)
        {
            Outcome = outcome;
            Reason = Truncate(reason);
        }

        public static ExecutionResult Success(string reason)
        {
            return new ExecutionResult(ExecutionOutcome.Success, reason);
        }

        public static ExecutionResult FailureRetryable(string reason)
        {
            return new ExecutionResult(ExecutionOutcome.FailureRetryable, reason);
        }

        public static ExecutionResult FailurePermanent(string reason)
        {
            return new ExecutionResult(ExecutionOutcome.FailurePermanent, reason);
        }

        public static ExecutionResult Rejected(string reason)
        {
            return new ExecutionResult(ExecutionOutcome.Rejected, reason);
        }

        public static ExecutionResult Unavailable(string reason)
        {
            return new ExecutionResult(ExecutionOutcome.Unavailable, reason);
        }

        public static ExecutionResult Cancelled(string reason)
        {
            return new ExecutionResult(ExecutionOutcome.Cancelled, reason);
        }

        public bool IsSuccess { get { return Outcome == ExecutionOutcome.Success; } }

        // Bounded builder-style diagnostic attachment. Returns false when the
        // entry was refused (bad key or bounds reached) — never throws.
        public bool AddMetadata(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxMetadataKeyLength) return false;
            if (m_Metadata.Count >= MaxMetadataEntries) return false;
            m_Metadata[key] = TruncateValue(value);
            return true;
        }

        public string GetMetadata(string key)
        {
            string v;
            return m_Metadata.TryGetValue(key, out v) ? v : null;
        }

        // Returns a derived result carrying one extra diagnostic entry
        // (original is untouched). Silent no-op on refusal (bounded).
        public ExecutionResult WithMeta(string key, string value)
        {
            AddMetadata(key, value);
            return this;
        }

        // Deterministic bounded single line for logging/dashboards.
        public string ToStatusLine()
        {
            return "outcome=" + Outcome + " reason=" + (Reason ?? "-")
                + " meta=" + m_Metadata.Count;
        }

        private static string Truncate(string s)
        {
            if (s == null) return null;
            return s.Length <= MaxReasonLength ? s : s.Substring(0, MaxReasonLength);
        }

        private static string TruncateValue(string s)
        {
            if (s == null) return string.Empty;
            return s.Length <= MaxMetadataValueLength ? s : s.Substring(0, MaxMetadataValueLength);
        }
    }

    // ---- The execution seam ---------------------------------------------------
    //
    // The ONLY pathway from an approved task to a gameplay action. The game-
    // facing implementation (PulsarCapabilityDispatcher) dispatches on
    // CapabilityId via static, code-reviewed branches that call VERIFIED
    // PULSAR APIs only.
    //
    // SECURITY BOUNDARY (Phase 8 master prompt): implementations must never
    // interpret arbitrary strings as commands, dispatch via reflection or
    // method-name lookup, compile or load code at runtime, spawn processes,
    // or execute task metadata / LLM / chat / mission text as behavior.
    // Target ids and arguments arrive already validated by the Phase 7
    // registry; dispatch may further validate against verified game data
    // and must refuse (Rejected/Unavailable) rather than guess.
    public interface ICapabilityDispatcher
    {
        // Performs EXACTLY ONE registered capability action for an
        // already-validated request. Must never throw (TaskExecutor wraps
        // faults as FailureRetryable), never mutate task lifecycle (the
        // executor owns transitions), and never touch the P5 claim/ledger
        // state (the executor records outcomes).
        ExecutionResult Dispatch(CapabilityDescriptor capability, CapabilityRequest request, CapBotTask task);
    }
}