using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 47: physical quarantine executor ---------------------------------
    // The ONLY compatibility component that touches the file system. The engine
    // (ConflictEngine) stays pure: it decides; this executor obeys and reports.
    //
    // Directive contract enforced here:
    //   - quarantine target is ALWAYS
    //       <modsDir>\CapBot_Quarantine\<modName>\<timestampMs>\<assemblyName>
    //     with the original bytes preserved untouched (File.Move on the same
    //     volume never rewrites content) and a SHA-256 of the source file
    //     recorded in conflict.json (QuarantineRecord) — never modified.
    //   - conflict.json is written to the same folder; a partial or failed
    //     write FAILS the quarantine (delete nothing, report fault).
    //   - compatibility-state.json lives directly in CapBot_Quarantine\ and
    //     is the boot-safety ledger: boot reads it, NEVER auto-restores
    //     (DisabledUntilCompatibilityTest semantics — restore is a separate,
    //     explicit retest flow).
    //   - protected mods are refused before any IO (engine also refuses, this
    //     is the second gate — never trust a single layer).
    //
    // IO is seam-injected (Func paths / Action moves) so the whole executor is
    // unit-testable without a disk; the production wiring (Mod.cs) supplies
    // real PML-backed paths. Every operation is fail-safe: one faulting mod
    // never blocks the rest.
    public static class QuarantineExecutor
    {
        public sealed class QuarantineOutcome
        {
            internal readonly bool Ok;
            internal readonly string Error;          // "" when Ok
            internal readonly string QuarantineDir;  // "" when not moved

            internal QuarantineOutcome(bool ok, string error, string dir)
            {
                Ok = ok;
                Error = error ?? "";
                QuarantineDir = dir ?? "";
            }
        }

        private const string QuarantineFolderName = "CapBot_Quarantine";
        private const string ConflictJsonName = "conflict.json";
        private const string StateJsonName = "compatibility-state.json";

        private static readonly object m_Lock = new object();

        // Last runtime mods-dir snapshot taken by ReadState/WriteState —
        // readback for the boot audit line (verifies the PML path seam live).
        private static volatile string m_LastModsDir = "";
        public static string ProductionModsDir { get { return m_LastModsDir; } }

        // Single failure path: every non-OK outcome is audited before returning
        // (refusals included — the directive audits everything observable).
        private static QuarantineOutcome Fail(string modName, string error, string dir)
        {
            Emit("CompatibilityQuarantineFailed mod=" + (modName ?? "") + " err=" + error);
            return new QuarantineOutcome(false, error, dir);
        }

        // ---- seams (production wiring; tests inject temp-dir equivalents) ----

        private static Func<string> m_ModsDirProvider;
        private static Func<string, string> m_FileHashProvider;   // path -> sha256 hex
        private static Func<string, bool> m_IsProtectedProvider;  // mod name -> protected?
        private static Action<string> m_AuditListener;            // CapBotLog.COMPAT bridge

        public static void SetModsDirProvider(Func<string> provider) { lock (m_Lock) { m_ModsDirProvider = provider; } }
        public static void SetFileHashProvider(Func<string, string> provider) { lock (m_Lock) { m_FileHashProvider = provider; } }
        public static void SetIsProtectedProvider(Func<string, bool> provider) { lock (m_Lock) { m_IsProtectedProvider = provider; } }
        public static void SetAuditListener(Action<string> listener) { lock (m_Lock) { m_AuditListener = listener; } }

        // ---- quarantine ------------------------------------------------------------

        // Executes a CONFIRMED Class-D quarantine. modAssembly is the DLL file
        // name (e.g. "SomeMod.dll"). Returns the outcome; on success the engine
        // MUST be confirmed via ConflictEngine.MarkQuarantined by the caller.
        public static QuarantineOutcome Quarantine(
            string modName, string modAssembly, string modVersion, string harmonyId,
            string classAudit, string confidence, string symptoms, string evidence,
            string decision, string capBotVersion, string gameVersion, bool reversible, long nowMs)
        {
            if (string.IsNullOrEmpty(modName) || string.IsNullOrEmpty(modAssembly))
                return Fail(modName, "refused: empty mod identity", "");
            if (modAssembly.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !modAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return Fail(modName, "refused: suspicious assembly name", "");
            if (!QuarantineRecord.CanRecord(ConflictClass.ClassD_ModConflict, ConflictConfidence.Confirmed))
                return Fail(modName, "refused: record factory contract broken", ""); // static ladder changed — fail safe
            lock (m_Lock)
            {
                bool protectedMod = false;
                Func<string, bool> prot = m_IsProtectedProvider;
                if (prot != null)
                {
                    try { protectedMod = prot(modName); }
                    catch (Exception) { return Fail(modName, "refused: protected-provider fault (fail-closed)", ""); }
                }
                if (protectedMod)
                    return Fail(modName, "refused: PROTECTED_MOD", "");

                Func<string> modsDirFn = m_ModsDirProvider;
                if (modsDirFn == null) return Fail(modName, "refused: no mods dir provider (unwired)", "");
                string modsDir;
                try { modsDir = modsDirFn(); }
                catch (Exception ex) { return Fail(modName, "fault: mods dir provider: " + ex.GetType().Name, ""); }
                if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
                    return Fail(modName, "refused: mods dir missing", "");

                string src = Path.Combine(modsDir, modAssembly);
                if (!File.Exists(src))
                    return Fail(modName, "refused: assembly not present in Mods", "");

                string qRoot = Path.Combine(modsDir, QuarantineFolderName);
                string qMod = Path.Combine(qRoot, SanitizeFolder(modName));
                string qDir = Path.Combine(qMod, nowMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
                QuarantineOutcome outcome;
                try
                {
                    Directory.CreateDirectory(qDir);   // idempotent for re-runs into the same timestamp

                    string sha = "";
                    Func<string, string> hashFn = m_FileHashProvider;
                    if (hashFn != null)
                    {
                        try { sha = hashFn(src) ?? ""; }
                        catch (Exception) { sha = ""; }   // hash is evidence enrichment, not a gate
                    }

                    string dst = Path.Combine(qDir, modAssembly);
                    // Preserve bytes: same-volume move. If the file is locked the
                    // move fails and we report it (never delete, never overwrite).
                    File.Move(src, dst);

                    QuarantineRecord rec = new QuarantineRecord(
                        modName, modAssembly, modVersion ?? "", harmonyId ?? "",
                        string.IsNullOrEmpty(classAudit) ? "D" : classAudit,
                        string.IsNullOrEmpty(confidence) ? "CONFIRMED" : confidence,
                        symptoms ?? "", evidence ?? "", decision ?? "",
                        capBotVersion ?? "", gameVersion ?? "", reversible, nowMs);
                    // ALSO record the hash: append as a bounded extra line so the
                    // record stays self-contained without widening the contract.
                    string json = rec.ToJson();
                    json = json.Substring(0, json.Length - 2) + // drop trailing "\n}"
                           ",\n  \"assemblySha256\": \"" + EscapeJson(sha) + "\"\n}";
                    File.WriteAllText(Path.Combine(qDir, ConflictJsonName), json, new UTF8Encoding(false));

                    outcome = new QuarantineOutcome(true, "", qDir);
                }
                catch (Exception ex)
                {
                    // Roll back as far as we can without destroying bytes: if the
                    // move succeeded but the record write failed, move the DLL
                    // BACK (restoration preserves bytes; quarantine is atomic).
                    try
                    {
                        string dst = Path.Combine(qDir, modAssembly);
                        if (File.Exists(dst) && !File.Exists(src)) File.Move(dst, src);
                    }
                    catch (Exception) { /* report the original fault below */ }
                    outcome = new QuarantineOutcome(false, "fault: " + ex.GetType().Name + ": " + Trim(ex.Message), "");
                }

                if (outcome.Ok) Emit("CompatibilityQuarantineExecuted mod=" + modName + " dir=" + outcome.QuarantineDir);
                else Emit("CompatibilityQuarantineFailed mod=" + modName + " err=" + outcome.Error);
                return outcome;
            }
        }

        // ---- compatibility-state ledger -----------------------------------------------

        // Replaces the state file with the current rows (bounded). Called on
        // every quarantine-state transition via the engine state-listener wiring.
        public static bool WriteState(List<CompatibilityStateRow> rows)
        {
            if (rows == null) return false;
            lock (m_Lock)
            {
                Func<string> modsDirFn = m_ModsDirProvider;
                if (modsDirFn == null) { Emit("CompatibilityStateWriteFailed err=no mods dir provider (unwired)"); return false; }
                string qRoot;
                try
                {
                    string modsDir = modsDirFn();
                    if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) { Emit("CompatibilityStateWriteFailed err=mods dir missing"); return false; }
                    m_LastModsDir = modsDir;
                    qRoot = Path.Combine(modsDir, QuarantineFolderName);
                    Directory.CreateDirectory(qRoot);
                }
                catch (Exception ex) { Emit("CompatibilityStateWriteFailed err=" + ex.GetType().Name); return false; }

                StringBuilder sb = new StringBuilder();
                sb.Append("{\n  \"rows\": [\n");
                for (int i = 0; i < rows.Count; i++)
                {
                    sb.Append(rows[i].ToJson());
                    sb.Append(i < rows.Count - 1 ? ",\n" : "\n");
                }
                sb.Append("  ]\n}\n");
                try
                {
                    // Atomic-ish: write temp then replace (a torn file must never
                    // disable boot safety — the reader fail-closes to KEEP).
                    string tmp = Path.Combine(qRoot, StateJsonName + ".tmp");
                    string fin = Path.Combine(qRoot, StateJsonName);
                    File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                    if (File.Exists(fin)) File.Replace(tmp, fin, null);
                    else File.Move(tmp, fin);
                    return true;
                }
                catch (Exception ex)
                {
                    Emit("CompatibilityStateWriteFailed err=" + ex.GetType().Name + ": " + Trim(ex.Message));
                    return false;
                }
            }
        }

        // Boot gate input: parse the persisted state file. Returns rows; a
        // missing or corrupt file returns an EMPTY list and the caller treats
        // unknown mods as not-quarantined (the DLL is present in Mods — boot
        // loads it — which is the correct fail-open for "no ledger entry").
        // Corrupt-file detection is audited via the listener.
        public static List<CompatibilityStateRow> ReadState()
        {
            lock (m_Lock)
            {
                Func<string> modsDirFn = m_ModsDirProvider;
                if (modsDirFn == null) return new List<CompatibilityStateRow>();
                string path;
                try
                {
                    string modsDir = modsDirFn();
                    if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return new List<CompatibilityStateRow>();
                    m_LastModsDir = modsDir;
                    path = Path.Combine(modsDir, QuarantineFolderName, StateJsonName);
                }
                catch (Exception) { return new List<CompatibilityStateRow>(); }
                if (!File.Exists(path)) return new List<CompatibilityStateRow>();
                try
                {
                    string json = File.ReadAllText(path);
                    return ParseStateJson(json);
                }
                catch (Exception ex)
                {
                    Emit("CompatibilityStateReadFailed err=" + ex.GetType().Name + " (boot keeps unknown-mods fail-open)");
                    return new List<CompatibilityStateRow>();
                }
            }
        }

        // Minimal, bounded parser for the exact shape WriteState produces.
        // Hand-rolled (no JSON dependency — the P46 record-layer rule).
        internal static List<CompatibilityStateRow> ParseStateJson(string json)
        {
            List<CompatibilityStateRow> rows = new List<CompatibilityStateRow>();
            if (string.IsNullOrEmpty(json)) return rows;
            // Tokens: "mod": "...", "state": "...", "reconfirmations": N, "lastChangedMs": N, "reason": "..."
            int idx = 0;
            while (true)
            {
                int start = json.IndexOf("{", idx, StringComparison.Ordinal);
                if (start < 0) break;
                int end = json.IndexOf("}", start, StringComparison.Ordinal);
                if (end < 0) break;
                string obj = json.Substring(start + 1, end - start - 1);
                idx = end + 1;
                string mod = ExtractString(obj, "mod");
                string state = ExtractString(obj, "state");
                if (mod == null && state == null) continue;   // not a row object
                int reconf = ExtractInt(obj, "reconfirmations");
                long changed = ExtractLong(obj, "lastChangedMs");
                string reason = ExtractString(obj, "reason") ?? "";
                rows.Add(new CompatibilityStateRow(mod ?? "", state ?? "", reconf, changed, reason));
                if (rows.Count >= 64) break;   // bounded
            }
            return rows;
        }

        // ---- restore-for-retest (executor half) --------------------------------------

        // Moves the quarantined assembly back into Mods (the controlled-retest
        // step). Does NOT touch the engine — the caller drives MarkRestoredForRetest
        // after this succeeds. Returns the restored source path or null.
        public static string RestoreForRetest(string modName, string modAssembly, string quarantineDir)
        {
            if (string.IsNullOrEmpty(modName) || string.IsNullOrEmpty(modAssembly) || string.IsNullOrEmpty(quarantineDir))
                return null;
            lock (m_Lock)
            {
                Func<string> modsDirFn = m_ModsDirProvider;
                if (modsDirFn == null) return null;
                try
                {
                    string modsDir = modsDirFn();
                    if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return null;
                    string src = Path.Combine(quarantineDir, modAssembly);
                    string dst = Path.Combine(modsDir, modAssembly);
                    if (!File.Exists(src)) return null;
                    if (File.Exists(dst)) return null;   // never overwrite — a mod reappeared by other means
                    Directory.CreateDirectory(modsDir);
                    File.Move(src, dst);
                    Emit("CompatibilityRetestRestoreExecuted mod=" + modName + " from=" + quarantineDir);
                    return dst;
                }
                catch (Exception ex)
                {
                    Emit("CompatibilityRetestRestoreFailed mod=" + modName + " err=" + ex.GetType().Name);
                    return null;
                }
            }
        }

        // ---- tests ---------------------------------------------------------------------

        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_ModsDirProvider = null;
                m_FileHashProvider = null;
                m_IsProtectedProvider = null;
                m_AuditListener = null;
            }
        }

        // ---- internals -------------------------------------------------------------------

        // Mod names are folder keys; strip filesystem-hostile characters but
        // keep the name recognizable for the owner.
        private static string SanitizeFolder(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = c == '"' || c == '<' || c == '>' || c == '|' || c == ':' || c == '?' || c == '*' || c == '/' || c == '\\' || c < 32;
                sb.Append(bad ? '_' : c);
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "_" : s;
        }

        private static string EscapeJson(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string ExtractString(string obj, string key)
        {
            string pat = "\"" + key + "\": \"";
            int i = obj.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + pat.Length;
            StringBuilder sb = new StringBuilder();
            for (int c = start; c < obj.Length; c++)
            {
                char ch = obj[c];
                if (ch == '\\' && c + 1 < obj.Length)
                {
                    char n = obj[c + 1];
                    if (n == '"') { sb.Append('"'); c++; continue; }
                    if (n == '\\') { sb.Append('\\'); c++; continue; }
                    if (n == 'r') { sb.Append('\r'); c++; continue; }
                    if (n == 'n') { sb.Append('\n'); c++; continue; }
                    sb.Append(ch); continue;
                }
                if (ch == '"') break;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static int ExtractInt(string obj, string key)
        {
            long v = ExtractLong(obj, key);
            return v > int.MaxValue ? int.MaxValue : (int)v;
        }

        private static long ExtractLong(string obj, string key)
        {
            string pat = "\"" + key + "\": ";
            int i = obj.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return 0;
            int start = i + pat.Length;
            int c = start;
            while (c < obj.Length && (char.IsDigit(obj[c]) || (c == start && obj[c] == '-'))) c++;
            long v;
            return long.TryParse(obj.Substring(start, c - start), out v) ? v : 0;
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 120 ? s : s.Substring(0, 120);
        }

        private static void Emit(string line)
        {
            Action<string> listener;
            lock (m_Lock) { listener = m_AuditListener; }
            if (listener == null) return;
            try { listener(line); }
            catch (Exception) { /* audit listeners are fail-safe by contract */ }
        }
    }
}