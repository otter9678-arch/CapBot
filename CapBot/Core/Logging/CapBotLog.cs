using System;
using System.Collections.Generic;

namespace CapBot.Core.Logging
{
    // Central leveled logger (Phase 1). All CapBot diagnostics go through this
    // class so failures are visible and spam stays bounded. Backs onto PML's
    // Logger.Info — the only PML logging API verified by reflection.
    // Time source is Environment.TickCount so logging works even before the
    // Unity clock starts (e.g. inside the Mod constructor).
    internal static class CapBotLog
    {
        public enum Level { Trace = 0, Debug, Info, Warning, Error, Critical }

        // Subsystem tags (master plan PART 3). OLLAMA reserved for later phases.
        public const string CORE = "CORE";
        public const string CAPTAIN = "CAPTAIN";
        public const string CREW = "CREW";
        public const string TASK = "TASK";
        public const string CAPABILITY = "CAPABILITY";
        public const string MISSION = "MISSION";
        public const string NAVIGATION = "NAVIGATION";
        public const string COMBAT = "COMBAT";
        public const string ECONOMY = "ECONOMY";
        public const string RESEARCH = "RESEARCH";
        public const string PERSISTENCE = "PERSISTENCE";
        public const string NETWORK = "NETWORK";
        public const string COMPAT = "COMPAT";
        public const string UPDATER = "UPDATER";
        public const string UI = "UI";
        public const string EMERGENCY = "EMERGENCY"; // Phase 9: emergency director subsystem (additive)
        public const string DECISION = "DECISION";   // Phase 19: decision validator subsystem (additive)
        public const string OLLAMA = "OLLAMA";       // Phase 20: Ollama advisor subsystem (additive)
        public const string QWEN = "QWEN";           // Phase 21: crew advisor subsystem (additive)
        public const string PLANNING = "PLANNING";   // Phase 22: planning director subsystem (additive)

        private const int PerMessageIntervalMs = 8000; // same key: at most one line / 8 s
        private const int MaxMessagesPerWindow = 24;   // global flood guard
        private const int FloodWindowMs = 10000;
        private const int MaxTrackedKeys = 256;        // bounded key set (no unbounded collections)

        // Keyed by level|subsystem|message-template. Callers must keep msg text
        // static per call site (variable detail goes in the exception, not msg).
        private static readonly Dictionary<string, int> LastEmit = new Dictionary<string, int>();
        private static readonly Queue<int> RecentEmitTicks = new Queue<int>();

        public static bool IsVerbose()
        {
            try { return Config.VerboseLogging.Value; }
            catch { return false; }
        }

        public static void Trace(string sub, string msg) { Emit(Level.Trace, sub, msg, null); }
        public static void Trace(string sub, string msg, Exception ex) { Emit(Level.Trace, sub, msg, ex); }
        public static void Debug(string sub, string msg) { Emit(Level.Debug, sub, msg, null); }
        public static void Debug(string sub, string msg, Exception ex) { Emit(Level.Debug, sub, msg, ex); }
        public static void Info(string sub, string msg) { Emit(Level.Info, sub, msg, null); }
        public static void Info(string sub, string msg, Exception ex) { Emit(Level.Info, sub, msg, ex); }
        public static void Warning(string sub, string msg) { Emit(Level.Warning, sub, msg, null); }
        public static void Warning(string sub, string msg, Exception ex) { Emit(Level.Warning, sub, msg, ex); }
        public static void Error(string sub, string msg) { Emit(Level.Error, sub, msg, null); }
        public static void Error(string sub, string msg, Exception ex) { Emit(Level.Error, sub, msg, ex); }
        public static void Critical(string sub, string msg) { Emit(Level.Critical, sub, msg, null); }
        public static void Critical(string sub, string msg, Exception ex) { Emit(Level.Critical, sub, msg, ex); }

        private static void Emit(Level level, string sub, string msg, Exception ex)
        {
            try
            {
                if (level <= Level.Debug && !IsVerbose()) return;

                int now = Environment.TickCount;
                string key = (int)level + "|" + sub + "|" + msg;
                int last;
                if (LastEmit.TryGetValue(key, out last))
                {
                    if (UncheckedDelta(last, now) < PerMessageIntervalMs) return;
                    LastEmit[key] = now;
                }
                else
                {
                    if (LastEmit.Count >= MaxTrackedKeys) LastEmit.Clear();
                    LastEmit[key] = now;
                }

                while (RecentEmitTicks.Count > 0 && UncheckedDelta(RecentEmitTicks.Peek(), now) > FloodWindowMs)
                    RecentEmitTicks.Dequeue();
                if (RecentEmitTicks.Count >= MaxMessagesPerWindow) return;
                RecentEmitTicks.Enqueue(now);

                string line = "[CapBot:" + sub + "] [" + level.ToString().ToUpperInvariant() + "] " + msg;
                if (ex != null) line += " :: " + ex.GetType().Name + ": " + ex.Message;
                PulsarModLoader.Utilities.Logger.Info(line);
            }
            catch { /* the logger must never become the crash source */ }
        }

        // TickCount wraps after ~24.8 days; unchecked subtraction stays correct
        // for intervals far below the wrap period.
        private static int UncheckedDelta(int from, int to)
        {
            unchecked { return to - from; }
        }
    }
}