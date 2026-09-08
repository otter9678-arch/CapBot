using System;
using System.Collections.Generic;

namespace CapBot.Core.Compatibility
{
    // ---- Phase 27: compatibility manager ---------------------------------------
    // Central, bounded registry for inter-mod compatibility actions. Each action
    // is (name, gate mod names, install delegate): the manager gates on the
    // registered mod-detection seam and invokes the install delegate OUTSIDE all
    // locks, individually fail-safe. The manager owns NO compat behavior itself —
    // it only dispatches; every real guard (e.g. MoreBotsCompatPatch) keeps its
    // own implementation and its own idempotence guards.
    //
    // Ownership argument: this layer authors no tasks, mutates no pipeline state,
    // touches no Harmony targets, and performs no gameplay action. It is a
    // dispatch-and-audit surface so compat behavior has one observable place
    // (StatusLines is the P29 consumption surface). Install timing is owned by
    // the existing call site (/capbot spawn — the class-0 bot can only exist
    // from that point, and MoreBots' prefix must be treated before the first
    // GetAIData frame of the new bot).
    //
    // Fail-closed: with no provider seam set (or a faulting provider) NOTHING
    // installs — a compat action must never fire on an unknown mod state. The
    // provider seam is wired at boot to PML's IsModLoaded (try/catch → false).
    //
    // Pure C# domain: no PULSAR/PML/Harmony references (the P19 lesson — the
    // test runner compiles a narrow file set; the production seam lives in
    // Mod.cs).
    public static class CompatManager
    {
        private sealed class CompatAction
        {
            internal readonly string Name;
            internal readonly string[] ModNames;
            internal readonly Action Install;
            internal bool Installed;

            internal CompatAction(string name, string[] modNames, Action install)
            {
                Name = name;
                ModNames = modNames;
                Install = install;
            }
        }

        private const int MaxActions = 8;          // bounded registry (compat surface is tiny by nature)
        private const int MaxModsPerAction = 4;    // any-of gate; more than 4 aliases is a config smell
        private const int MaxStatusLines = 12;     // summary + up to one line per action + headroom

        private static readonly object m_Lock = new object();
        private static readonly List<CompatAction> m_Actions = new List<CompatAction>(MaxActions);
        private static Func<string, bool> m_IsLoadedProvider;
        private static Action<string> m_DecisionListener;

        // Counters (audited readbacks; P29 status surface).
        private static int m_InstallCalls;
        private static int m_InstalledCount;
        private static int m_SkippedCount;   // gate said not loaded (or unknown) — normal, not a fault
        private static int m_FaultCount;     // install delegate threw (the delegate's own logging reported it)
        private static int m_GateDenyCount;  // provider absent or faulting — deny-by-default
        private static int m_RefusedCount;   // RegisterAction rejected (null/empty name/mods/install, bounds)
        private static string m_LastSummary = "";

        // ---- seams ------------------------------------------------------------

        // Mod-detection seam. Provider contract: return true iff the named mod
        // is loaded. A faulting provider must be caught by the CALLER wiring
        // it (boot wraps PML in try/catch → false); the manager additionally
        // treats any provider exception as deny for that call.
        public static void SetIsLoadedProvider(Func<string, bool> provider)
        {
            lock (m_Lock) { m_IsLoadedProvider = provider; }
        }

        // Single-slot decision listener (the house LogBridge pattern — boot
        // attaches the COMPAT log bridge; tests attach a capture list).
        public static void SetDecisionListener(Action<string> listener)
        {
            lock (m_Lock) { m_DecisionListener = listener; }
        }

        // ---- registration -----------------------------------------------------

        // Registers a compat action. Duplicate name ⇒ idempotent no-op (true).
        // Bounds: MaxActions actions, MaxModsPerAction aliases each.
        public static bool RegisterAction(string name, string[] modNames, Action install)
        {
            if (string.IsNullOrEmpty(name)) { CountRefused(); return false; }
            if (install == null) { CountRefused(); return false; }
            if (modNames == null || modNames.Length == 0) { CountRefused(); return false; }
            lock (m_Lock)
            {
                for (int i = 0; i < m_Actions.Count; i++)
                {
                    if (string.Equals(m_Actions[i].Name, name, StringComparison.Ordinal)) return true;
                }
                if (m_Actions.Count >= MaxActions) { m_RefusedCount++; return false; }
                int mods = modNames.Length > MaxModsPerAction ? MaxModsPerAction : modNames.Length;
                string[] copy = new string[mods];
                for (int i = 0; i < mods; i++)
                {
                    if (string.IsNullOrEmpty(modNames[i])) { m_RefusedCount++; return false; }
                    copy[i] = modNames[i];
                }
                m_Actions.Add(new CompatAction(name, copy, install));
                return true;
            }
        }

        private static void CountRefused()
        {
            lock (m_Lock) { m_RefusedCount++; }
        }

        // ---- install ----------------------------------------------------------

        // Installs every registered action whose gate mod is loaded. Called
        // once per /capbot spawn (the only moment a class-0 bot can come to
        // exist). Actions already installed by this manager are skipped
        // (idempotence at the manager level; the delegates are additionally
        // self-idempotent). Emits one bounded summary line; per-action lines
        // only for installs and faults.
        public static void InstallAll()
        {
            CompatAction[] snapshot;
            lock (m_Lock)
            {
                m_InstallCalls++;
                snapshot = m_Actions.ToArray();
            }

            int installed = 0;
            int skipped = 0;
            bool gateUnavailable = false;
            for (int i = 0; i < snapshot.Length; i++)
            {
                CompatAction a = snapshot[i];
                bool alreadyInstalled;
                lock (m_Lock)
                {
                    // Re-check under lock against the live registry: a
                    // concurrent InstallAll may have installed it already.
                    alreadyInstalled = m_Actions.IndexOf(a) >= 0 && a.Installed;
                }
                if (alreadyInstalled) { continue; }

                bool loaded = false;
                bool providerFaulted = false;
                for (int m = 0; m < a.ModNames.Length; m++)
                {
                    bool v;
                    if (!TryQueryLoaded(a.ModNames[m], out v)) { providerFaulted = true; break; }
                    if (v) { loaded = true; break; }
                }
                if (providerFaulted || !loaded)
                {
                    skipped++;
                    if (providerFaulted) { lock (m_Lock) { m_GateDenyCount++; } gateUnavailable = true; }
                    continue;
                }

                try
                {
                    a.Install();
                    a.Installed = true;
                    installed++;
                    Emit("CompatInstalled name=" + a.Name);
                }
                catch (Exception ex)
                {
                    // One faulting action cannot block the others; the detail
                    // report belongs to the action itself (production actions
                    // log via CapBotLog.Error). Counted here.
                    lock (m_Lock) { m_FaultCount++; }
                    Emit("CompatActionFaulted name=" + a.Name + " err=" + ex.GetType().Name);
                }
            }

            lock (m_Lock)
            {
                m_InstalledCount += installed;
                m_SkippedCount += skipped;
                m_LastSummary = "CompatInstall actions=" + snapshot.Length +
                                " installed=" + installed +
                                " skipped=" + skipped;
            }
            if (gateUnavailable) Emit("CompatGateUnavailable (deny-by-default)");
            Emit(m_LastSummary);
        }

        private static bool TryQueryLoaded(string modName, out bool loaded)
        {
            Func<string, bool> provider;
            lock (m_Lock) { provider = m_IsLoadedProvider; }
            if (provider == null) { loaded = false; return false; }
            try { loaded = provider(modName); return true; }
            catch (Exception) { loaded = false; return false; }
        }

        // ---- readbacks --------------------------------------------------------

        public static int ActionCount { get { lock (m_Lock) { return m_Actions.Count; } } }
        public static int InstallCalls { get { lock (m_Lock) { return m_InstallCalls; } } }
        public static int InstalledActions { get { lock (m_Lock) { return m_InstalledCount; } } }
        public static int SkippedActions { get { lock (m_Lock) { return m_SkippedCount; } } }
        public static int FaultCount { get { lock (m_Lock) { return m_FaultCount; } } }
        public static int GateDenyCount { get { lock (m_Lock) { return m_GateDenyCount; } } }
        public static int RefusedRegistrations { get { lock (m_Lock) { return m_RefusedCount; } } }
        public static int MaxActionsBound { get { return MaxActions; } }
        public static string LastSummary { get { lock (m_Lock) { return m_LastSummary; } } }

        // Bounded status surface (P29 consumption; ≤ MaxStatusLines).
        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                lines.Add("Compat: actions=" + m_Actions.Count +
                          " installed=" + m_InstalledCount +
                          " skipped=" + m_SkippedCount +
                          " faults=" + m_FaultCount +
                          " gateDeny=" + m_GateDenyCount +
                          " refused=" + m_RefusedCount);
                for (int i = 0; i < m_Actions.Count && lines.Count < MaxStatusLines; i++)
                {
                    CompatAction a = m_Actions[i];
                    string mods = "";
                    for (int m = 0; m < a.ModNames.Length; m++)
                    {
                        if (m > 0) mods += ",";
                        mods += a.ModNames[m];
                    }
                    lines.Add("Compat: " + a.Name + " mods=" + mods +
                              " installed=" + (a.Installed ? "yes" : "no"));
                }
            }
            return lines;
        }

        // ---- tests ------------------------------------------------------------

        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                m_Actions.Clear();
                m_IsLoadedProvider = null;
                m_DecisionListener = null;
                m_InstallCalls = 0;
                m_InstalledCount = 0;
                m_SkippedCount = 0;
                m_FaultCount = 0;
                m_GateDenyCount = 0;
                m_RefusedCount = 0;
                m_LastSummary = "";
            }
        }

        // ---- internals --------------------------------------------------------

        private static void Emit(string line)
        {
            Action<string> listener;
            lock (m_Lock) { listener = m_DecisionListener; }
            if (listener == null) return;
            try { listener(line); }
            catch (Exception) { /* decision listeners are fail-safe by contract */ }
        }
    }
}