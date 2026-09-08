using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CapBot.Core.Update
{
    // ---- Phase 30: secure updater policy ----------------------------------------
    //
    // Pure C# verification domain for the mod updater (audit C1/M5). The
    // production engine (ModUpdater.cs) routes every download decision through
    // this layer; the tests exercise the full policy without PML/game/network
    // references (the P19 narrow-compile lesson).
    //
    // Verification chain (ALL must pass before any byte reaches the Mods dir):
    //   1. URL: HTTPS only (http/ftp/file schemes refused) + host on a bounded
    //      allowlist + no userinfo component. A hostile or MITM-able link is
    //      refused before any connection.
    //   2. Payload shape: non-empty, bounded size, MZ header (a PE image) —
    //      truncated/HTML-error-page/zero-byte responses never install.
    //   3. Digest: when the version JSON carries a Sha256 field, the downloaded
    //      bytes MUST match it (refuse on mismatch AND on missing ability to
    //      verify). When the publisher did not ship a digest, install is
    //      allowed on 1+2 alone — the residual risk is documented, and the
    //      discipline is "publish the digest with the version file".
    //   4. File-name defense: staged/applied names are flattened to bare
    //      filenames (path traversal like "..\..\x.dll" is refused).
    //
    // No wall-clock reads, no network I/O here — the engine owns transport;
    // this layer owns policy.
    public static class UpdatePolicy
    {
        public const int MaxDllBytes = 32 * 1024 * 1024;   // 32 MB hard bound (largest known PML mod is < 2 MB)
        public const int MinDllBytes = 1024;               // a real mod DLL is never smaller

        // Bounded HTTPS host allowlist (case-insensitive exact host match).
        // GitHub + GitHub release-asset CDN covers the PML ecosystem's
        // distribution pattern (VersionLink JSON points at GitHub).
        public static readonly string[] AllowedHosts = new string[]
        {
            "github.com",
            "www.github.com",
            "raw.githubusercontent.com",
            "api.github.com",
            "objects.githubusercontent.com",
        };

        // ---- URL policy ---------------------------------------------------------

        public enum UrlVerdict
        {
            Allowed = 0,
            RefusedScheme = 1,      // not https
            RefusedHost = 2,        // host not on the allowlist
            RefusedMalformed = 3,   // unparsable / userinfo / empty host
        }

        // Full check: scheme + host + structural sanity. Never throws.
        public static UrlVerdict CheckDownloadUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return UrlVerdict.RefusedMalformed;
            Uri uri;
            try { uri = new Uri(url, UriKind.Absolute); }
            catch (Exception) { return UrlVerdict.RefusedMalformed; }
            if (uri.Scheme != Uri.UriSchemeHttps) return UrlVerdict.RefusedScheme;
            // Userinfo ("https://user:pass@host/") is a spoofing vector.
            if (!string.IsNullOrEmpty(uri.UserInfo)) return UrlVerdict.RefusedMalformed;
            string host = uri.Host;
            if (string.IsNullOrEmpty(host)) return UrlVerdict.RefusedMalformed;
            for (int i = 0; i < AllowedHosts.Length; i++)
            {
                if (string.Equals(host, AllowedHosts[i], StringComparison.OrdinalIgnoreCase))
                {
                    return UrlVerdict.Allowed;
                }
            }
            return UrlVerdict.RefusedHost;
        }

        public static string VerdictName(UrlVerdict v)
        {
            switch (v)
            {
                case UrlVerdict.Allowed: return "allowed";
                case UrlVerdict.RefusedScheme: return "scheme";
                case UrlVerdict.RefusedHost: return "host";
                case UrlVerdict.RefusedMalformed: return "malformed";
                default: return "unknown";
            }
        }

        // ---- payload policy -----------------------------------------------------

        // Structural payload check. Returns null when acceptable, otherwise a
        // short refusal reason (data only, never parsed — compared/logged).
        public static string ValidateDllBytes(byte[] bytes)
        {
            if (bytes == null) return "null";
            if (bytes.Length < MinDllBytes) return "too-small";
            if (bytes.Length > MaxDllBytes) return "too-large";
            // MZ: a Windows PE image starts with 0x4D 0x5A ("MZ"). A JSON
            // error page, an HTML login page, or a truncated response never
            // starts with it.
            if (bytes[0] != 0x4D || bytes[1] != 0x5A) return "not-mz";
            return null;
        }

        // ---- digest -------------------------------------------------------------

        // SHA-256 hex digest of the payload (lowercase, 64 chars).
        public static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null) return null;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        // Verifies the payload against the publisher's digest. Rules:
        //   expected null/empty  => true  (nothing published to verify against;
        //                           residual risk documented in UpdatePolicy.cs)
        //   malformed expected   => false (a garbage digest refuses, never bypasses)
        //   mismatch             => false
        public static bool VerifySha256(byte[] bytes, string expectedHex)
        {
            if (bytes == null) return false;
            if (string.IsNullOrEmpty(expectedHex)) return true;
            string clean = expectedHex.Trim();
            if (clean.Length != 64) return false;
            for (int i = 0; i < clean.Length; i++)
            {
                char c = clean[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            string actual = ComputeSha256(bytes);
            return string.Equals(actual, clean, StringComparison.OrdinalIgnoreCase);
        }

        // ---- file-name defense ----------------------------------------------------

        // Accepts ONLY a bare DLL filename. A path-shaped name (any separator
        // or drive colon) is a publisher red flag and is REFUSED, not
        // flattened — suspicious input is never normalized into acceptance.
        // Returns null when the name is unsafe. Never throws.
        public static string SanitizeDllFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            if (fileName.IndexOf('\\') >= 0) return null;
            if (fileName.IndexOf('/') >= 0) return null;
            if (fileName.IndexOf(':') >= 0) return null;
            if (fileName.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
            if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return null;
            // Reserved-character sanity: the engine writes into Mods\<name>.
            for (int i = 0; i < fileName.Length; i++)
            {
                char c = fileName[i];
                bool bad = c == '<' || c == '>' || c == '|' || c == '?' || c == '*' || c == '"' || (c >= 0 && c < 32);
                if (bad) return null;
            }
            if (fileName.Length > 96) return null;
            return fileName;
        }

        // ---- version-JSON digest extraction -----------------------------------------

        // Extracts a "Sha256" field from the version JSON (same tolerant scrape
        // style as the engine's Version/DownloadLink extraction). null when absent.
        public static string TryGetShaFromVersionJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string needle = "\"Sha256\"";
            int k = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return null;
            int colon = json.IndexOf(':', k + needle.Length);
            if (colon < 0) return null;
            int open = json.IndexOf('"', colon + 1);
            if (open < 0) return null;
            int close = json.IndexOf('"', open + 1);
            if (close < 0) return null;
            return json.Substring(open + 1, close - open - 1);
        }
    }
}