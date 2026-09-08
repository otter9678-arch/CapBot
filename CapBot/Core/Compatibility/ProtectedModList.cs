using System.Collections.Generic;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 46: protected mods ---------------------------------------------
    // The standing directive's NEVER-AUTO-REMOVE list: game/runtime/infra
    // assemblies are structurally unquarantinable — ConflictEngine refuses
    // conflict classification for any mod on this list (refusal=PROTECTED_MOD)
    // regardless of symptoms. Name comparison is case-insensitive Ordinal and
    // covers both PML display names and assembly/file names (minus .dll).
    //
    // Pure C# domain (the P19 lesson): no PULSAR/PML references.
    public static class ProtectedModList
    {
        private static readonly string[] Protected = new string[]
        {
            // Game + framework assemblies (never mods — quarantining any of
            // these bricks the game or the loader).
            "Assembly-CSharp",
            "Assembly-CSharp-firstpass",
            "PulsarModLoader",
            "BepInEx",
            "0Harmony",
            "Harmony",
            "HarmonyRuntime",
            "UnityEngine",
            "UnityEngine.UI",
            "UnityEngine.CoreModule",
            "Cinemachine",
            "AstarPathfindingProject",
            "Behave",
            "BehaveRuntime",
            "Steamworks.NET",
            "Mono.Cecil",
            "MonoMod",
            "MonoMod.RuntimeDetour",
            "mscorlib",
            "netstandard",
            "ACTk",
            "CodeStage.AntiCheat",

            // CapBot itself + its support assemblies: the conflict engine must
            // never be able to vote its own host out of the Mods folder, and
            // the diagnostic baseline is dev-owned.
            "CapBot",
            "CapBotBaseline",

            // Quality Improver is a separate mod by the same original author —
            // per the master prompt it is NEVER merged or auto-disabled.
            "QualityImprover",
            "Quality Improver"
        };

        private static readonly HashSet<string> ProtectedSet = BuildSet();

        private static HashSet<string> BuildSet()
        {
            HashSet<string> set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Protected.Length; i++) set.Add(Protected[i]);
            return set;
        }

        // True when the given mod name (or assembly/file name, with or without
        // the .dll extension) is on the protected list.
        public static bool IsProtected(string modName)
        {
            if (string.IsNullOrEmpty(modName)) return false;
            if (ProtectedSet.Contains(modName)) return true;
            // File-name forms: "SomeMod.dll" → "SomeMod".
            if (modName.Length > 4 &&
                modName[modName.Length - 4] == '.' &&
                (char.ToLowerInvariant(modName[modName.Length - 3]) == 'd') &&
                (char.ToLowerInvariant(modName[modName.Length - 2]) == 'l') &&
                (char.ToLowerInvariant(modName[modName.Length - 1]) == 'l'))
            {
                return ProtectedSet.Contains(modName.Substring(0, modName.Length - 4));
            }
            return false;
        }

        public static int Count { get { return ProtectedSet.Count; } }
    }
}