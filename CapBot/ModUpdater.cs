using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;

namespace CapBot
{
    // In-game auto-updater. Walks every loaded PML mod, fetches each mod's
    // VersionLink JSON ({ "Version": "...", "DownloadLink": "..." }) exactly like
    // PML's own ModUpdateCheck, and downloads newer DLLs. Loaded DLLs are file-locked
    // by the OS, so a download that cannot overwrite is staged as "<name>.dll.update"
    // and applied by the boot-time swap below on a later launch.
    internal static class ModUpdater
    {
        private const string UA = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0.4606.71 Safari/537.36";

        private class VersionFile
        {
            public string Version;
            public string DownloadLink;
        }

        // Runs when CapBot's Mod class initializes (before most game systems; swaps
        // any staged updates whose DLL is not currently locked).
        internal static void ApplyStagedUpdates()
        {
            try
            {
                string modsDir = PulsarModLoader.ModManager.GetModsDir();
                if (!Directory.Exists(modsDir)) return;
                List<string> applied = new List<string>();
                foreach (string staged in Directory.GetFiles(modsDir, "*.update"))
                {
                    string target = Path.Combine(Path.GetDirectoryName(staged), Path.GetFileNameWithoutExtension(staged));
                    // Path.GetFileNameWithoutExtension strips only the last ext; staged name is "X.dll.update".
                    if (!target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        target = Path.Combine(Path.GetDirectoryName(staged), Path.GetFileNameWithoutExtension(staged) + ".dll");
                    try
                    {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(staged, target);
                        applied.Add(Path.GetFileName(target));
                    }
                    catch { /* locked or transient; try next launch */ }
                }
                if (applied.Count > 0)
                    PulsarModLoader.Utilities.Logger.Info("CapBot applied staged mod updates: " + string.Join(", ", applied.ToArray()));
            }
            catch { }
        }

        // /updateall — check every loaded mod for a newer version and update it.
        internal static string UpdateAll()
        {
            StringBuilder report = new StringBuilder();
            int updated = 0, failed = 0, staged = 0, current = 0, noLink = 0;
            try
            {
                var mods = PulsarModLoader.ModManager.Instance.GetAllMods().ToList();
                report.AppendLine("Checking " + mods.Count + " mods...");
                foreach (PulsarModLoader.PulsarMod mod in mods)
                {
                    try
                    {
                        if (mod == null) continue;
                        string link = mod.VersionLink;
                        if (string.IsNullOrEmpty(link)) { noLink++; continue; }
                        string json;
                        using (WebClient wc = new WebClient())
                        {
                            wc.Headers.Add("user-agent", UA);
                            json = wc.DownloadString(link);
                        }
                        VersionFile vf = ParseVersionJson(json);
                        if (vf == null || string.IsNullOrEmpty(vf.Version) || string.IsNullOrEmpty(vf.DownloadLink))
                        {
                            report.AppendLine("[skip] " + mod.Name + ": bad version file");
                            failed++;
                            continue;
                        }
                        if (CompareVersions(mod.Version ?? "0.0.0", vf.Version) >= 0) { current++; continue; }

                        string target = mod.VersionInfo != null && !string.IsNullOrEmpty(mod.VersionInfo.FileName)
                            ? mod.VersionInfo.FileName
                            : Path.Combine(PulsarModLoader.ModManager.GetModsDir(), mod.Name + ".dll");
                        byte[] bytes;
                        using (WebClient wc = new WebClient())
                        {
                            wc.Headers.Add("user-agent", UA);
                            bytes = wc.DownloadData(vf.DownloadLink);
                        }
                        try
                        {
                            File.WriteAllBytes(target, bytes);
                            report.AppendLine("[OK] " + mod.Name + " -> " + vf.Version + " (restart to load)");
                            updated++;
                        }
                        catch
                        {
                            string stagedPath = target + ".update";
                            File.WriteAllBytes(stagedPath, bytes);
                            report.AppendLine("[staged] " + mod.Name + " -> " + vf.Version + " (applies on next launch)");
                            staged++;
                        }
                    }
                    catch (Exception e)
                    {
                        report.AppendLine("[fail] " + (mod != null ? mod.Name : "?") + ": " + e.Message);
                        failed++;
                    }
                }

                // PML itself: ModManager already compared the latest GitHub tag on boot.
                try
                {
                    var pmlInfo = PulsarModLoader.ModManager.Instance.PMLVersionInfo;
                    if (PulsarModLoader.ModManager.IsOldVersion)
                    {
                        report.AppendLine("PML update available: " + pmlInfo.FileVersion + " -> latest (https://github.com/PULSAR-Modders/pulsar-mod-loader/releases/latest)");
                        report.AppendLine("PML must be updated manually while the game is closed (boot-critical file).");
                        failed++;
                    }
                    else
                    {
                        report.AppendLine("PML " + pmlInfo.FileVersion + " is current.");
                    }
                }
                catch { }
            }
            catch (Exception e)
            {
                report.AppendLine("Update check error: " + e.Message);
            }
            report.AppendLine("Updated: " + updated + ", staged: " + staged + ", current: " + current + ", no update link: " + noLink + ", failed: " + failed);
            return report.ToString();
        }

        // Minimal JSON scrape so we don't need a JSON lib: grab "Version" and "DownloadLink".
        private static VersionFile ParseVersionJson(string json)
        {
            try
            {
                VersionFile vf = new VersionFile();
                vf.Version = ExtractJsonString(json, "Version");
                vf.DownloadLink = ExtractJsonString(json, "DownloadLink");
                if (vf.Version == null && vf.DownloadLink == null) return null;
                return vf;
            }
            catch { return null; }
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (json == null) return null;
            string needle = "\"" + key + "\"";
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

        // >= 0 when current >= remote, < 0 when remote is newer. Tolerates
        // non-numeric prefixes like "Alpha 1.2.0" by stripping to digits.
        internal static int CompareVersions(string current, string remote)
        {
            int[] a = ParseVer(current);
            int[] b = ParseVer(remote);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        private static int[] ParseVer(string s)
        {
            if (s == null) return new int[0];
            var parts = new List<int>();
            foreach (string token in s.Split('.', ' ', '-', '_'))
            {
                int v;
                string digits = new string(token.TakeWhile(char.IsDigit).ToArray());
                if (digits.Length > 0 && int.TryParse(digits, out v)) parts.Add(v);
                else if (parts.Count > 0) break;
            }
            return parts.ToArray();
        }
    }
}