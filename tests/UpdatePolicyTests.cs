// Dev-side unit tests for the Phase 30 secure updater policy (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1.
// No network I/O, no wall-clock reads: the policy domain is exercised
// directly (the engine's transport is production wiring).
//
// Covers the Phase 30 mandated scenarios:
//   UPD01 URL gate: HTTPS-only + allowlist (scheme/host/malformed refusals)
//   UPD02 URL gate: userinfo spoofing refused; case-insensitive host match
//   UPD03 payload shape: null/empty/too-small/too-large/non-MZ refused; MZ ok
//   UPD04 SHA-256 compute determinism + hex format
//   UPD05 SHA-256 verify: match/mismatch/no-digest/garbage-digest rules
//   UPD06 digest extraction from version JSON (present/absent/case)
//   UPD07 file-name defense: traversal/reserved/extension refusals + flattening
//   UPD08 verification chain end-to-end (shape+digest gates compose)
//   UPD09 known-answer digest (SHA-256 of "abc" reference vector)
//   UPD10 bounds are constants (documented limits stable)
using System;
using System.Collections.Generic;
using System.Text;
using CapBot.Core.Update;

namespace CapBot.TaskTests
{
    internal static class UpdatePolicyTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        // Minimal valid PE-shaped payload (MZ header + padding to MinDllBytes).
        private static byte[] FakeDll(int size)
        {
            if (size < 0) size = 0;
            byte[] b = new byte[Math.Max(UpdatePolicy.MinDllBytes, size)];
            b[0] = 0x4D;   // 'M'
            b[1] = 0x5A;   // 'Z'
            return b;
        }

        internal static int Run()
        {
            // ---- UPD01: URL gate ----------------------------------------------------
            Check(UpdatePolicy.CheckDownloadUrl("https://github.com/x/y/releases/download/v1/x.dll") == UpdatePolicy.UrlVerdict.Allowed,
                "UPD01 github https allowed");
            Check(UpdatePolicy.CheckDownloadUrl("https://objects.githubusercontent.com/path/x.dll") == UpdatePolicy.UrlVerdict.Allowed,
                "UPD01 release-asset cdn allowed");
            Check(UpdatePolicy.CheckDownloadUrl("http://github.com/x/y") == UpdatePolicy.UrlVerdict.RefusedScheme,
                "UPD01 http refused");
            Check(UpdatePolicy.CheckDownloadUrl("ftp://github.com/x.dll") == UpdatePolicy.UrlVerdict.RefusedScheme,
                "UPD01 ftp refused");
            Check(UpdatePolicy.CheckDownloadUrl("file:///C:/Windows/x.dll") == UpdatePolicy.UrlVerdict.RefusedScheme,
                "UPD01 file scheme refused");
            Check(UpdatePolicy.CheckDownloadUrl("https://evil.example.com/x.dll") == UpdatePolicy.UrlVerdict.RefusedHost,
                "UPD01 unknown host refused");
            Check(UpdatePolicy.CheckDownloadUrl(null) == UpdatePolicy.UrlVerdict.RefusedMalformed, "UPD01 null refused");
            Check(UpdatePolicy.CheckDownloadUrl("") == UpdatePolicy.UrlVerdict.RefusedMalformed, "UPD01 empty refused");
            Check(UpdatePolicy.CheckDownloadUrl("not a url") == UpdatePolicy.UrlVerdict.RefusedMalformed,
                "UPD01 garbage refused");
            Check(UpdatePolicy.CheckDownloadUrl("https://RAW.GITHUBUSERCONTENT.COM/x") == UpdatePolicy.UrlVerdict.Allowed,
                "UPD01 host match case-insensitive");

            // ---- UPD02: userinfo + host boundary --------------------------------------
            Check(UpdatePolicy.CheckDownloadUrl("https://user:pass@github.com/x.dll") == UpdatePolicy.UrlVerdict.RefusedMalformed,
                "UPD02 userinfo refused");
            Check(UpdatePolicy.CheckDownloadUrl("https://github.com.evil.com/x.dll") == UpdatePolicy.UrlVerdict.RefusedHost,
                "UPD02 suffix-spoof host refused (exact match only)");
            Check(UpdatePolicy.CheckDownloadUrl("https://api.github.com/rate_limit") == UpdatePolicy.UrlVerdict.Allowed,
                "UPD02 api host allowed");
            Check(UpdatePolicy.CheckDownloadUrl("HTTPS://WWW.GITHUB.COM/x") == UpdatePolicy.UrlVerdict.Allowed,
                "UPD02 www host allowed case-insensitive");

            // ---- UPD03: payload shape ---------------------------------------------------
            Check(UpdatePolicy.ValidateDllBytes(null) == "null", "UPD03 null refused");
            Check(UpdatePolicy.ValidateDllBytes(new byte[0]) == "too-small", "UPD03 empty refused");
            Check(UpdatePolicy.ValidateDllBytes(new byte[100]) == "too-small", "UPD03 tiny refused");
            Check(UpdatePolicy.ValidateDllBytes(new byte[UpdatePolicy.MaxDllBytes + 1]) == "too-large",
                "UPD03 over-max refused");
            byte[] notMz = new byte[UpdatePolicy.MinDllBytes];
            notMz[0] = 0x7B; notMz[1] = 0x22;   // "{\"" — a JSON error page
            Check(UpdatePolicy.ValidateDllBytes(notMz) == "not-mz", "UPD03 json error page refused");
            byte[] html = Encoding.ASCII.GetBytes("<html>403 Forbidden</html>" + new string('x', UpdatePolicy.MinDllBytes));
            Check(UpdatePolicy.ValidateDllBytes(html) == "not-mz", "UPD03 html error page refused");
            Check(UpdatePolicy.ValidateDllBytes(FakeDll(4096)) == null, "UPD03 MZ payload accepted");

            // ---- UPD04: SHA-256 determinism ----------------------------------------------
            byte[] payload = FakeDll(2048);
            string h1 = UpdatePolicy.ComputeSha256(payload);
            string h2 = UpdatePolicy.ComputeSha256(payload);
            Check(h1 != null && h1.Length == 64, "UPD04 digest 64 hex chars");
            Check(h1 == h2, "UPD04 deterministic");
            bool lowerHex = true;
            for (int i = 0; i < h1.Length; i++)
            {
                char c = h1[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) { lowerHex = false; break; }
            }
            Check(lowerHex, "UPD04 lowercase hex");
            byte[] other = FakeDll(2049);
            Check(UpdatePolicy.ComputeSha256(payload) != UpdatePolicy.ComputeSha256(appendOne(payload)),
                "UPD04 different payload => different digest");

            // ---- UPD05: verify rules -------------------------------------------------------
            Check(UpdatePolicy.VerifySha256(payload, h1), "UPD05 matching digest passes");
            Check(UpdatePolicy.VerifySha256(payload, h1.ToUpperInvariant()), "UPD05 case-insensitive match");
            string wrong = h1.Substring(0, 63) + (h1[63] == '0' ? "1" : "0");
            Check(!UpdatePolicy.VerifySha256(payload, wrong), "UPD05 mismatched digest refuses");
            Check(!UpdatePolicy.VerifySha256(payload, "not-a-digest"), "UPD05 malformed digest refuses (never bypasses)");
            Check(!UpdatePolicy.VerifySha256(payload, new string('a', 63)), "UPD05 short digest refuses");
            Check(UpdatePolicy.VerifySha256(payload, null), "UPD05 no published digest => allowed (documented residual)");
            Check(UpdatePolicy.VerifySha256(payload, ""), "UPD05 empty published digest => allowed");
            Check(!UpdatePolicy.VerifySha256(null, h1), "UPD05 null payload refuses");

            // ---- UPD06: digest extraction ----------------------------------------------------
            string json = "{ \"Version\": \"1.2.3\", \"DownloadLink\": \"https://github.com/x/y.dll\", \"Sha256\": \"" + h1 + "\" }";
            Check(UpdatePolicy.TryGetShaFromVersionJson(json) == h1, "UPD06 sha extracted");
            Check(UpdatePolicy.TryGetShaFromVersionJson("{ \"Version\": \"1.0\" }") == null, "UPD06 absent => null");
            Check(UpdatePolicy.TryGetShaFromVersionJson(null) == null, "UPD06 null json => null");
            Check(UpdatePolicy.TryGetShaFromVersionJson("{ \"sha256\": \"" + h1 + "\" }") == h1, "UPD06 case-insensitive key");

            // ---- UPD07: file-name defense (strict: path-shaped input refused) ------
            Check(UpdatePolicy.SanitizeDllFileName("SomeMod.dll") == "SomeMod.dll", "UPD07 plain name passes");
            Check(UpdatePolicy.SanitizeDllFileName("..\\..\\evil.dll") == null, "UPD07 traversal refused");
            Check(UpdatePolicy.SanitizeDllFileName("..\\evil.dll") == null, "UPD07 parent ref refused");
            Check(UpdatePolicy.SanitizeDllFileName("sub\\dir\\mod.dll") == null, "UPD07 nested path refused (strict)");
            Check(UpdatePolicy.SanitizeDllFileName("sub/mod.dll") == null, "UPD07 forward-slash path refused");
            Check(UpdatePolicy.SanitizeDllFileName("C:\\evil\\x.dll") == null, "UPD07 drive path refused");
            Check(UpdatePolicy.SanitizeDllFileName("mod.txt") == null, "UPD07 non-dll extension refused");
            Check(UpdatePolicy.SanitizeDllFileName("") == null, "UPD07 empty refused");
            Check(UpdatePolicy.SanitizeDllFileName(null) == null, "UPD07 null refused");
            Check(UpdatePolicy.SanitizeDllFileName("mod<DLL>.dll") == null, "UPD07 reserved char refused");
            Check(UpdatePolicy.SanitizeDllFileName(new string('a', 97) + ".dll") == null, "UPD07 over-length refused");

            // ---- UPD08: chain composition ------------------------------------------------------
            byte[] good = FakeDll(4096);
            string goodSha = UpdatePolicy.ComputeSha256(good);
            // All gates pass:
            Check(UpdatePolicy.CheckDownloadUrl("https://github.com/m/m.dll") == UpdatePolicy.UrlVerdict.Allowed
                && UpdatePolicy.ValidateDllBytes(good) == null
                && UpdatePolicy.VerifySha256(good, goodSha)
                && UpdatePolicy.SanitizeDllFileName("m.dll") != null,
                "UPD08 full chain passes for honest payload");
            // Tampered payload vs published digest:
            byte[] tampered = FakeDll(2048);
            tampered[100] ^= 0xFF;
            string published = UpdatePolicy.ComputeSha256(good);
            Check(UpdatePolicy.ValidateDllBytes(tampered) == null && !UpdatePolicy.VerifySha256(tampered, goodSha),
                "UPD08 shape passes but digest REFUSES tampered payload");
            // Error-page payload vs no digest:
            byte[] page = Encoding.ASCII.GetBytes("<html>proxy error</html>" + new string('x', UpdatePolicy.MinDllBytes));
            Check(UpdatePolicy.ValidateDllBytes(page) == "not-mz",
                "UPD08 error page refused at shape gate (before digest)");

            // ---- UPD09: reference vector -------------------------------------------------------
            // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
            string abc = UpdatePolicy.ComputeSha256(Encoding.ASCII.GetBytes("abc"));
            Check(abc == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                "UPD09 SHA-256 reference vector matches");

            // ---- UPD10: bounds stable -----------------------------------------------------------
            Check(UpdatePolicy.MaxDllBytes == 32 * 1024 * 1024, "UPD10 MaxDllBytes=32MB");
            Check(UpdatePolicy.MinDllBytes == 1024, "UPD10 MinDllBytes=1K");
            Check(UpdatePolicy.AllowedHosts.Length == 5, "UPD10 allowlist bounded (5 hosts)");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        private static byte[] appendOne(byte[] b)
        {
            byte[] c = new byte[b.Length + 1];
            Array.Copy(b, c, b.Length);
            c[b.Length] = 0;
            return c;
        }
    }
}