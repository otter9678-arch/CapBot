using System;
using System.Collections.Generic;

namespace CapBot.Core.Tasks
{
    // Terminal outcome of a logical execution attempt, as remembered by the
    // idempotency ledger. Unknown = never seen (or evicted from the ring).
    public enum ActionOutcome : byte
    {
        Unknown = 0,
        Succeeded = 1,
        Failed = 2
    }

    // ---- Phase 5: deterministic action identity -----------------------------
    // Builds the safe, deterministic identity string future executors attach
    // to every gameplay action claim and result. The identity is DATA ONLY:
    // a bounded string of digits/letters and ':', '_' separators, derived
    // from task identity + action kind + attempt epoch + a 32-bit FNV-1a
    // hash of the opaque target reference. It never carries or references
    // executable content, is never parsed into code paths, and is never
    // dispatched on — it is compared byte-for-byte or logged.
    //
    // Determinism: the same (taskId, actionKind, attemptEpoch, targetKey)
    // always produces the same id within a session, so a repeated request
    // (duplicate scheduler pass, repeated RPC callback, retried executor)
    // collides with the original in the ledger and is rejected.
    public static class ActionIdentity
    {
        public const int MaxActionKindLength = 32;

        // FNV-1a 32-bit: deterministic across sessions and platforms (never
        // string.GetHashCode, which is not guaranteed stable). Used only for
        // identity hashing of bounded data — never for security.
        public static uint ComputeStableHash(string text)
        {
            uint h = 2166136261u;
            if (text == null) return h;
            for (int i = 0; i < text.Length; i++)
            {
                h ^= (uint)(text[i] & 0xFF);
                h *= 16777619u;
                h ^= (uint)((text[i] >> 8) & 0xFF);
                h *= 16777619u;
            }
            return h;
        }

        // Builds "<taskId>:<kind>:<epoch>:<hash8>" or null on invalid input.
        // actionKind is validated to a bounded static vocabulary of ASCII
        // letters/digits/underscore (e.g. "EXECUTE") — mission/NPC/chat text
        // can never shape it. targetKey (any opaque target reference,
        // possibly untrusted game text) is hashed, never embedded.
        public static string MakeActionId(long taskId, string actionKind, int attemptEpoch, string targetKey)
        {
            if (taskId <= 0) return null;
            if (string.IsNullOrEmpty(actionKind) || actionKind.Length > MaxActionKindLength) return null;
            for (int i = 0; i < actionKind.Length; i++)
            {
                char c = actionKind[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return null;
            }
            uint h = ComputeStableHash(taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + actionKind + "|" + attemptEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + (targetKey ?? string.Empty));
            string hex = h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            return taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + actionKind + ":" + attemptEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + hex;
        }
    }

    // ---- Phase 5: idempotency ledger ----------------------------------------
    // Bounded FIFO memory of action results. Its two jobs:
    //   1. duplicate-result rejection — once an action identity has a
    //      recorded outcome, repeating that outcome (duplicate completion,
    //      duplicate failure, stale RPC callback) is detected and ignored;
    //   2. duplicate-execution rejection — a claim whose attempt identity is
    //      already recorded as Succeeded is refused, so the same logical
    //      action can never be executed twice even across executors.
    //
    // Bounded: at most MaxEntries (256) entries, FIFO eviction of the oldest.
    // That preserves enough history to reject immediate duplicate requests
    // (the realistic duplicate window) while never growing unbounded.
    //
    // Outcome upgrade rule: Succeeded is sticky (never overwritten — the
    // first success is the logical truth and later results are duplicates);
    // Failed may be upgraded to Succeeded (a claim that recorded failure may
    // still legitimately resolve successfully within the same attempt).
    public sealed class ActionLedger
    {
        public const int MaxEntries = 256;

        private readonly Dictionary<string, LedgerEntry> m_Entries = new Dictionary<string, LedgerEntry>(StringComparer.Ordinal);
        private readonly Queue<string> m_Order = new Queue<string>();
        private readonly object m_Lock = new object();

        private sealed class LedgerEntry
        {
            public ActionOutcome Outcome;
            public int RecordedAtMs;
        }

        public int EntryCount { get { lock (m_Lock) return m_Entries.Count; } }

        // Current remembered outcome for an action id (Unknown when absent).
        public ActionOutcome Observe(string actionId)
        {
            if (string.IsNullOrEmpty(actionId)) return ActionOutcome.Unknown;
            lock (m_Lock)
            {
                LedgerEntry e;
                return m_Entries.TryGetValue(actionId, out e) ? e.Outcome : ActionOutcome.Unknown;
            }
        }

        // Records an outcome; returns false (and changes nothing) when the
        // effective outcome would not change (duplicate Succeeded, repeated
        // Failed, or Succeeded -> Failed downgrade). Returns true when the
        // entry is new or an existing Failed entry is upgraded to Succeeded.
        public bool RecordOutcome(string actionId, ActionOutcome outcome, int nowMs)
        {
            if (string.IsNullOrEmpty(actionId)) return false;
            if (outcome != ActionOutcome.Succeeded && outcome != ActionOutcome.Failed) return false;
            lock (m_Lock)
            {
                LedgerEntry e;
                if (m_Entries.TryGetValue(actionId, out e))
                {
                    if (e.Outcome == ActionOutcome.Succeeded) return false;   // first success is sticky
                    if (outcome == ActionOutcome.Failed) return false;        // repeated failure is a duplicate
                    e.Outcome = ActionOutcome.Succeeded;                      // Failed -> Succeeded upgrade
                    e.RecordedAtMs = nowMs;
                    return true;
                }
                m_Entries[actionId] = new LedgerEntry { Outcome = outcome, RecordedAtMs = nowMs };
                m_Order.Enqueue(actionId);
                while (m_Order.Count > MaxEntries)
                {
                    string evicted = m_Order.Dequeue();
                    m_Entries.Remove(evicted);
                }
                return true;
            }
        }

        // Test/dev isolation only. Never call in game code.
        public void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Entries.Clear();
                m_Order.Clear();
            }
        }
    }
}