// Dev-side unit tests for the Phase 47 quarantine executor + boot gate (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1.
// Uses REAL temp directories (the executor is the sanctioned IO layer; the
// engine stays pure and is covered by ConflictEngineTests).
//
//   QE01 identity/shape refusals (empty name, non-dll, path characters)
//   QE02 unwired mods-dir provider refuses (fail-closed)
//   QE03 protected-mod refusal (second gate)
//   QE04 missing assembly refusal
//   QE05 successful quarantine: move + conflict.json + sha + audit line
//   QE06 record-write failure rolls the DLL back (quarantine is atomic)
//   QE07 state ledger write/read roundtrip incl. escaping
//   QE08 read missing ledger => empty (boot fail-open for unknown mods)
//   QE09 corrupt ledger => empty + bounded parse (no throw)
//   QE10 restore-for-retest: move back, refuse overwrite/missing
//   QE11 boot seeding: Quarantined/QuarantineAgain statuses + status vocabulary
//   QE12 seed precedence: live evidence wins over ledger seed
//   QE13 engine state readbacks (state text / reconfirms / tracked names)
//   QE14 end-to-end state-listener wiring rebuilds the ledger
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CapBot.Core.Compatibility;

namespace CapBot.TaskTests
{
    internal static class QuarantineExecutorTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        private static readonly List<string> s_Lines = new List<string>();

        private static string NewTempModsDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "capbot_qe_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteFakeModDll(string modsDir, string fileName, byte[] content)
        {
            string p = Path.Combine(modsDir, fileName);
            File.WriteAllBytes(p, content ?? new byte[] { 1, 2, 3, 4, 5 });
            return p;
        }

        private static void FreshSetup(string modsDir)
        {
            ConflictEngine.ResetForTests();
            QuarantineExecutor.ResetForTests();
            s_Lines.Clear();
            QuarantineExecutor.SetAuditListener(delegate (string line) { s_Lines.Add(line); });
            if (modsDir != null) QuarantineExecutor.SetModsDirProvider(delegate { return modsDir; });
            QuarantineExecutor.SetFileHashProvider(delegate (string path) { return "DEADBEEF"; });
            QuarantineExecutor.SetIsProtectedProvider(delegate (string modName) { return false; });
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        internal static int Run()
        {
            RunCore();
            Console.WriteLine("");
            Console.WriteLine("QUARANTINE_EXECUTOR_TESTS passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        private static void RunCore()
        {
            // ---- QE01: identity/shape refusals ----
            FreshSetup(null);
            ConflictEngine.SetModProfile("BadMod", false, false, false, false, false, false);
            QuarantineExecutor.QuarantineOutcome o;
            o = QuarantineExecutor.Quarantine("", "BadMod.dll", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 1L);
            Check(!o.Ok && o.Error.IndexOf("empty mod identity", StringComparison.Ordinal) >= 0, "QE01 empty mod name refused");
            o = QuarantineExecutor.Quarantine("BadMod", "", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 1L);
            Check(!o.Ok && o.Error.IndexOf("empty mod identity", StringComparison.Ordinal) >= 0, "QE01 empty assembly refused");
            o = QuarantineExecutor.Quarantine("BadMod", "BadMod.exe", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 1L);
            Check(!o.Ok && o.Error.IndexOf("suspicious assembly name", StringComparison.Ordinal) >= 0, "QE01 non-dll refused");
            o = QuarantineExecutor.Quarantine("BadMod", "..\\evil.dll", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 1L);
            Check(!o.Ok && o.Error.IndexOf("suspicious assembly name", StringComparison.Ordinal) >= 0, "QE01 path traversal refused");

            // ---- QE02: unwired provider refuses ----
            FreshSetup(null);   // no mods dir provider
            o = QuarantineExecutor.Quarantine("BadMod", "BadMod.dll", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 1L);
            Check(!o.Ok && o.Error.IndexOf("unwired", StringComparison.Ordinal) >= 0, "QE02 unwired mods dir refuses (fail-closed)");
            Check(HasLineContaining("CompatibilityQuarantineFailed mod=BadMod"), "QE02 failure audited");

            // ---- QE03: protected-mod second gate ----
            string dir3 = NewTempModsDir();
            try
            {
                FreshSetup(dir3);
                WriteFakeModDll(dir3, "PulsarModLoader.dll", new byte[] { 9 });
                QuarantineExecutor.SetIsProtectedProvider(delegate (string m) { return m == "PulsarModLoader"; });
                o = QuarantineExecutor.Quarantine("PulsarModLoader", "PulsarModLoader.dll", "0.12.3", "pml", "D", "CONFIRMED", "s", "e", "d", "P47", "g", false, 1L);
                Check(!o.Ok && o.Error.IndexOf("PROTECTED_MOD", StringComparison.Ordinal) >= 0, "QE03 protected mod refused before IO");
                Check(File.Exists(Path.Combine(dir3, "PulsarModLoader.dll")), "QE03 protected file untouched");
                // provider fault also refuses (fail-closed)
                QuarantineExecutor.SetIsProtectedProvider(delegate (string m) { throw new InvalidOperationException("boom"); });
                o = QuarantineExecutor.Quarantine("BadMod", "BadMod.dll", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 1L);
                Check(!o.Ok && o.Error.IndexOf("fail-closed", StringComparison.Ordinal) >= 0, "QE03 protected-provider fault refuses");
            }
            finally { Directory.Delete(dir3, true); }

            // ---- QE04: missing assembly refuses ----
            string dir4 = NewTempModsDir();
            try
            {
                FreshSetup(dir4);
                o = QuarantineExecutor.Quarantine("BadMod", "BadMod.dll", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 1L);
                Check(!o.Ok && o.Error.IndexOf("not present", StringComparison.Ordinal) >= 0, "QE04 missing assembly refused");
            }
            finally { Directory.Delete(dir4, true); }

            // ---- QE05: successful quarantine ----
            string dir5 = NewTempModsDir();
            try
            {
                FreshSetup(dir5);
                byte[] payload = new byte[] { 77, 90, 1, 2, 3, 4, 5, 6 };
                WriteFakeModDll(dir5, "BadMod.dll", payload);
                ConflictEngine.SetModProfile("BadMod", false, false, false, false, false, false);
                ConflictEngine.RecordSymptom("BadMod", SymptomKind.ExceptionStorm, "owner.bad", "NRE|PLShipInfo.Update|BadMod", 1000, 100L, 900L);
                ConflictDecision d = ConflictEngine.Evaluate("BadMod", 1000L);   // no A/B ⇒ observe; state stays None
                Check(d != null && d.Action == Remediation.Observe, "QE05 pre-state observe (no A/B)");
                // Simulate the CONFIRMED evidence path via ledger-free direct state:
                // the executor does not require engine state (the caller gates on the
                // engine decision); exercise the physical half end-to-end.
                o = QuarantineExecutor.Quarantine("BadMod", "BadMod.dll", "1.0", "owner.bad",
                    "D", "CONFIRMED", "exception storm count=1000", "A/B: removal removes, reintroduction reproduces",
                    "AUTO-QUARANTINE per standing directive", "P47", "v1.2.10", true, 555L);
                Check(o.Ok, "QE05 quarantine succeeds");
                string expectedDir = Path.Combine(dir5, "CapBot_Quarantine", "BadMod", "555");
                Check(o.QuarantineDir == expectedDir, "QE05 dir shape Mods\\CapBot_Quarantine\\<mod>\\<ts>");
                string moved = Path.Combine(expectedDir, "BadMod.dll");
                Check(File.Exists(moved), "QE05 assembly moved");
                Check(!File.Exists(Path.Combine(dir5, "BadMod.dll")), "QE05 source gone");
                byte[] movedBytes = File.ReadAllBytes(moved);
                bool bytesEqual = movedBytes.Length == payload.Length;
                for (int i = 0; bytesEqual && i < payload.Length; i++) bytesEqual = movedBytes[i] == payload[i];
                Check(bytesEqual, "QE05 bytes preserved (File.Move never rewrites)");
                string json = File.ReadAllText(Path.Combine(expectedDir, "conflict.json"));
                Check(json.IndexOf("\"mod\": \"BadMod\"", StringComparison.Ordinal) > 0, "QE05 conflict.json carries mod");
                Check(json.IndexOf("\"assemblySha256\": \"DEADBEEF\"", StringComparison.Ordinal) > 0, "QE05 conflict.json carries sha256");
                Check(json.IndexOf("\"confidence\": \"CONFIRMED\"", StringComparison.Ordinal) > 0, "QE05 conflict.json carries confidence");
                Check(HasLineContaining("CompatibilityQuarantineExecuted mod=BadMod"), "QE05 execution audited");
            }
            finally { Directory.Delete(dir5, true); }

            // ---- QE06: record-write failure rolls the DLL back ----
            string dir6 = NewTempModsDir();
            try
            {
                FreshSetup(dir6);
                WriteFakeModDll(dir6, "BadMod.dll", new byte[] { 5, 4, 3 });
                // Pre-place conflict.json as a DIRECTORY at the same timestamp path:
                // CreateDirectory(qDir) is idempotent, then WriteAllText onto a
                // directory throws → the rollback must move the DLL back.
                string qDir = Path.Combine(dir6, "CapBot_Quarantine", "BadMod", "777");
                Directory.CreateDirectory(Path.Combine(qDir, "conflict.json"));
                o = QuarantineExecutor.Quarantine("BadMod", "BadMod.dll", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 777L);
                Check(!o.Ok && o.Error.IndexOf("fault:", StringComparison.Ordinal) >= 0, "QE06 write failure reported as fault");
                Check(File.Exists(Path.Combine(dir6, "BadMod.dll")), "QE06 rollback restored the DLL (quarantine is atomic)");
                Check(!File.Exists(Path.Combine(qDir, "BadMod.dll")), "QE06 no half-quarantined copy left in the quarantine dir");
            }
            finally { Directory.Delete(dir6, true); }

            // ---- QE07: state ledger roundtrip ----
            string dir7 = NewTempModsDir();
            try
            {
                FreshSetup(dir7);
                List<CompatibilityStateRow> rows = new List<CompatibilityStateRow>();
                rows.Add(new CompatibilityStateRow("BadMod", "Quarantined", 1, 100L, "confirmed class D"));
                rows.Add(new CompatibilityStateRow("Quote\"Mod", "QuarantineAgain", 2, 200L, "reason with \"quotes\" and\r\nnewlines"));
                Check(QuarantineExecutor.WriteState(rows), "QE07 ledger write ok");
                List<CompatibilityStateRow> back = QuarantineExecutor.ReadState();
                Check(back.Count == 2, "QE07 roundtrip row count");
                Check(back[0].ModName == "BadMod" && CompatibilityStateRow.BootMustKeepQuarantined(back[0].QuarantineState),
                    "QE07 row 0 roundtrips + boot gate keeps it");
                Check(back[1].ModName == "Quote\"Mod" && back[1].Reason.IndexOf("\r\n", StringComparison.Ordinal) >= 0,
                    "QE07 escaping survives the roundtrip (evidence verbatim)");
            }
            finally { Directory.Delete(dir7, true); }

            // ---- QE08/QE09: ledger read edges ----
            string dir8 = NewTempModsDir();
            try
            {
                FreshSetup(dir8);
                Check(QuarantineExecutor.ReadState().Count == 0, "QE08 missing ledger reads empty (boot fail-open)");
                Directory.CreateDirectory(Path.Combine(dir8, "CapBot_Quarantine"));
                File.WriteAllText(Path.Combine(dir8, "CapBot_Quarantine", "compatibility-state.json"),
                    "{ rows: [ { BROKEN", new UTF8Encoding(false));
                Check(QuarantineExecutor.ReadState().Count == 0, "QE09 corrupt ledger reads empty without throw");
                // bounded parse: 100 row objects -> max 64
                StringBuilder big = new StringBuilder();
                big.Append("{\n  \"rows\": [\n");
                for (int i = 0; i < 100; i++)
                {
                    big.Append("  { \"mod\": \"m" + i + "\", \"state\": \"Quarantined\", \"reconfirmations\": 0, \"lastChangedMs\": 0, \"reason\": \"\" }");
                    big.Append(i < 99 ? ",\n" : "\n");
                }
                big.Append("  ]\n}");
                List<CompatibilityStateRow> parsed = QuarantineExecutor.ParseStateJson(big.ToString());
                Check(parsed.Count == 64, "QE09 parse bounded at 64 rows");
                Check(parsed[63].ModName == "m63", "QE09 parse order preserved");
            }
            finally { try { Directory.Delete(dir8, true); } catch (Exception) { } }

            // ---- QE10: restore-for-retest ----
            string dir10 = NewTempModsDir();
            try
            {
                FreshSetup(dir10);
                WriteFakeModDll(dir10, "BadMod.dll", new byte[] { 1, 1, 1 });
                QuarantineExecutor.QuarantineOutcome q = QuarantineExecutor.Quarantine("BadMod", "BadMod.dll", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 888L);
                Check(q.Ok, "QE10 setup quarantine ok");
                string back = QuarantineExecutor.RestoreForRetest("BadMod", "BadMod.dll", q.QuarantineDir);
                Check(back != null && File.Exists(Path.Combine(dir10, "BadMod.dll")), "QE10 restore moves the DLL back");
                Check(HasLineContaining("CompatibilityRetestRestoreExecuted mod=BadMod"), "QE10 restore audited");
                string again = QuarantineExecutor.RestoreForRetest("BadMod", "BadMod.dll", q.QuarantineDir);
                Check(again == null, "QE10 refuse when destination exists (never overwrite)");
                string missing = QuarantineExecutor.RestoreForRetest("BadMod", "BadMod.dll", Path.Combine(dir10, "nope"));
                Check(missing == null, "QE10 refuse when source missing");
            }
            finally { Directory.Delete(dir10, true); }

            // ---- QE11/QE12/QE13: engine boot seeding + readbacks ----
            ConflictEngine.ResetForTests();
            ConflictEngine.SetModProfile("LedgerMod", false, false, false, false, false, false);
            Check(ConflictEngine.SeedQuarantineState("LedgerMod", "Quarantined", 1, 10L), "QE11 ledger seed accepted");
            Check(ConflictEngine.CompatStatus("LedgerMod").IndexOf("Quarantined", StringComparison.Ordinal) == 0, "QE11 seeded mod reports Quarantined");
            Check(ConflictEngine.SeedQuarantineState("LedgerMod", "QuarantineAgain", 2, 20L) == false, "QE12 seed refused from non-None (live state wins)");
            ConflictEngine.SetModProfile("LiveMod", false, false, false, false, false, false);
            ConflictEngine.RecordSymptom("LiveMod", SymptomKind.ExceptionStorm, "o", "k", 1, 1L, 2L);
            ConflictEngine.RecordComparison("LiveMod", "ab", true, false, true, 3L);
            ConflictEngine.Evaluate("LiveMod", 10L);
            Check(ConflictEngine.QuarantineStateText("LiveMod") == "QuarantineRecommended", "QE13 live evidence produced a recommendation");
            Check(ConflictEngine.SeedQuarantineState("LiveMod", "Quarantined", 0, 30L) == false, "QE12 seed refused when live evidence exists");
            Check(ConflictEngine.SeedQuarantineState("UnknownStateMod", "SomeWeirdState", 0, 10L) == false, "QE11 unknown state text refused (fail-safe)");
            Check(ConflictEngine.QuarantineStateText("LedgerMod") == "Quarantined", "QE13 state text readback");
            Check(ConflictEngine.QuarantineAgainCount("LedgerMod") == 1, "QE13 reconfirm count seeded");
            List<string> names = ConflictEngine.TrackedModNames();
            Check(names.Count == 2 && names.IndexOf("LedgerMod") >= 0 && names.IndexOf("LiveMod") >= 0 && names.IndexOf("UnknownStateMod") < 0,
                "QE13 tracked-name snapshot (unknown-state refusal creates no record)");
            // Boot gate parity: a ledger row the boot gate keeps must seed to a state the engine treats as quarantined.
            Check(CompatibilityStateRow.BootMustKeepQuarantined(ConflictEngine.QuarantineStateText("LedgerMod")),
                "QE11 boot gate agrees with seeded state text");

            // ---- QE14: end-to-end state-listener wiring rebuilds the ledger ----
            string dir14 = NewTempModsDir();
            try
            {
                FreshSetup(dir14);
                ConflictEngine.ResetForTests();
                ConflictEngine.SetModProfile("E2EMod", false, false, false, false, false, false);
                // Wire exactly what Mod.cs wires: state listener → rebuild ledger from snapshots.
                ConflictEngine.SetStateListener(delegate (string modName, ConflictEngine.QuarantineState newState)
                {
                    List<CompatibilityStateRow> rows = new List<CompatibilityStateRow>();
                    List<string> tracked = ConflictEngine.TrackedModNames();
                    for (int i = 0; i < tracked.Count; i++)
                    {
                        string st = ConflictEngine.QuarantineStateText(tracked[i]);
                        if (st == "" || st == "None" || st == "QuarantineRecommended") continue;
                        rows.Add(new CompatibilityStateRow(tracked[i], st, ConflictEngine.QuarantineAgainCount(tracked[i]), 0L, "listener rebuild"));
                    }
                    QuarantineExecutor.WriteState(rows);
                });
                // Drive the full CONFIRMED path: symptom + A/B + reintroduction.
                ConflictEngine.RecordSymptom("E2EMod", SymptomKind.ExceptionStorm, "o", "k", 5, 1L, 2L);
                ConflictEngine.RecordComparison("E2EMod", "ab", true, false, true, 3L);
                ConflictDecision d = ConflictEngine.Evaluate("E2EMod", 10L);
                Check(d != null && d.Action == Remediation.Quarantine, "QE14 full causality ⇒ QUARANTINE decision");
                // The physical move + engine confirm.
                WriteFakeModDll(dir14, "E2EMod.dll", new byte[] { 7, 7, 7 });
                QuarantineExecutor.QuarantineOutcome q = QuarantineExecutor.Quarantine("E2EMod", "E2EMod.dll", "1", "h", "D", "CONFIRMED", "s", "e", "d", "P47", "g", true, 99L);
                Check(q.Ok, "QE14 executor moved the DLL");
                Check(ConflictEngine.MarkQuarantined("E2EMod", 100L), "QE14 engine confirmed the move");
                Check(ConflictEngine.QuarantineStateText("E2EMod") == "Quarantined", "QE14 state advanced to Quarantined");
                // The listener must have written the ledger.
                List<CompatibilityStateRow> ledger = QuarantineExecutor.ReadState();
                Check(ledger.Count == 1 && ledger[0].ModName == "E2EMod" && ledger[0].QuarantineState == "Quarantined",
                    "QE14 listener wiring rebuilt the ledger on the transition");
                // New session boot: reset the engine (simulating reboot), seed from ledger.
                ConflictEngine.ResetForTests();
                ConflictEngine.SetModProfile("E2EMod", false, false, false, false, false, false);
                List<CompatibilityStateRow> ledger2 = QuarantineExecutor.ReadState();
                bool seeded = false;
                for (int i = 0; i < ledger2.Count; i++)
                {
                    if (ledger2[i].ModName == "E2EMod")
                        seeded = ConflictEngine.SeedQuarantineState(ledger2[i].ModName, ledger2[i].QuarantineState, ledger2[i].Reconfirmations, 200L);
                }
                Check(seeded && ConflictEngine.CompatStatus("E2EMod").IndexOf("Quarantined", StringComparison.Ordinal) == 0,
                    "QE14 reboot: ledger re-seeds the quarantine (boot never auto-restores)");
            }
            finally { try { Directory.Delete(dir14, true); } catch (Exception) { } }

            ConflictEngine.ResetForTests();
            QuarantineExecutor.ResetForTests();
        }
    }
}