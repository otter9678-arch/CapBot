using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CapBot.Core.Crew;
using CapBot.Core.Ollama;
using CapBot.Core.World;

namespace CapBot.Core.Qwen
{
    // ---- Phase 21: Crew advisor (Qwen integration, recommend-only) ----------
    //
    // The crew-domain completion of the P20 Ollama advisor: asks the SAME
    // local server (loopback-only, same port/model config) for ONE advisory
    // line about the crew picture, built from the CrewAgent hooks reserved
    // since Phase 10 (CREW_AGENTS.md:208 — Role/RoleName, LastTaskOutcome,
    // LastKnownTLIName) through the additive AgentViews() readback.
    //
    // Inheritance of contract from docs/OLLAMA_ADVISOR.md:
    //   - Advice is DATA: one bounded log line. It never creates, pauses,
    //     resumes, cancels, or claims any task; never calls TaskScheduler,
    //     TaskRecoveryManager, ExecutionClaims, CapabilityRegistry,
    //     DecisionValidator, or CrewAgentRegistry assignment APIs; never
    //     issues orders or moves bots.
    //   - Recommend-only + deny-by-default: inert unless the config toggle is
    //     on AND a transport is wired AND authority passes.
    //   - Loopback-only: transport hard-anchored to 127.0.0.1 (only the port
    //     and model are configurable — shared with P20).
    //   - House director pattern: static + DirectorState + m_Lock + 5
    //     fail-closed seams; Evaluate gate order mirrors OllamaAdvisor 1:1
    //     (authority => enabled+transport => consume => back-off => cadence =>
    //     snapshot fail-safe => single-flight => dispatch).
    //   - Worker/park/consume threading: dedicated background worker thread
    //     runs HTTP (cold load ~38-40 s makes sync on the game thread
    //     impossible), parks the raw body in a single-slot buffer
    //     (PendingResponseSet bool marker — a null body is a legitimate
    //     parked fault), game thread consumes on a later tick. Single-flight
    //     via Interlocked CAS. No auto-retry: the consume-eval legitimately
    //     opens the next cadence window (exactly one follow-up); hard faults
    //     arm a back-off ladder (3 consecutive => 120 s cooldown).
    //
    // Reuse (same assembly, internal): OllamaAdvisor.ExtractContent /
    // ValidateAdvice / ITransport / ClampPort / ClampModelIndex / ModelName.
    // The advice vocabulary prefix stays "ADVICE:" (the advisory data
    // contract is shared — the subsystem tag and log prefix differ).
    public static class CrewAdvisor
    {
        public const int MinRecheckMs = 8000;            // slower cadence than P20 (5s): crew picture changes slowly
        public const int MaxStaleSnapshotMs = 20000;     // same shared freshness standard as P9/P16-P20
        public const int MaxPendingLines = 4;
        public const int MaxAdviceLen = 240;
        public const int MaxPromptLen = 4000;
        public const int RequestTimeoutMs = 90000;
        public const int MaxConsecutiveFailures = 3;
        public const int CooldownAfterFailureMs = 120000;
        public const int MaxAgentsInPrompt = 12;         // prompt bound: first N agents (registry caps live at 32)

        public static readonly string[] KnownModels = new string[]
        {
            "qwen2.5:latest",   // default (P21 shares the P20 vocabulary)
            "qwen:latest",
            "qwen2.5-coder:latest",
            "qwen3:latest"      // thinking model; request carries "think":false (P36)
        };

        public const int ModelDefault = 0;

        // ---- seams (pluggable, fail-closed; null = inert) --------------------
        private static Func<bool> m_AuthorityProbe;      // null/fault => no-op
        private static Func<int> m_NowMsProvider;        // production: TaskClock.NowMs
        private static Func<WorldSnapshot> m_WorldProvider; // production: WorldStateService.Latest
        private static Action<string> m_DecisionListener;   // CrewAdvisorLogBridge attaches at boot
        private static OllamaAdvisor.ITransport m_Transport; // production: OllamaHttpTransport

        public static void SetAuthorityProbe(Func<bool> probe) { lock (m_Lock) m_AuthorityProbe = probe; }
        public static void SetNowMsProvider(Func<int> provider) { lock (m_Lock) m_NowMsProvider = provider; }
        public static void SetWorldProvider(Func<WorldSnapshot> provider) { lock (m_Lock) m_WorldProvider = provider; }
        public static void SetDecisionListener(Action<string> listener) { lock (m_Lock) m_DecisionListener = listener; }
        public static void SetTransport(OllamaAdvisor.ITransport transport) { lock (m_Lock) m_Transport = transport; }

        private static readonly object m_Lock = new object();

        private sealed class DirectorState
        {
            public bool Enabled;
            public int Port;
            public int ModelIndex;

            public int InFlightRequestMs = -1;
            public int LastEvalMs = -1;
            public string PendingResponse;
            public int PendingResponseMs;
            public bool PendingResponseSet;

            public long RequestsSent;
            public long RequestsFailed;
            public long RequestsSucceeded;
            public long AdviceAccepted;
            public long AdviceRejected;
            public long UncertainPasses;
            public long BackoffBlocks;
            public int ConsecutiveFailures;
            public int BackoffUntilMs = -1;
            public string LastAdvice = string.Empty;
            public int LastAdviceMs = -1;
            public string LastFailureReason;
        }

        private static DirectorState s_State = new DirectorState();
        private static int s_WorkerRunning;              // 0/1 single-flight gate (Interlocked)

        // ---- config (polled every tick from Patch.cs; no config read off-thread) ----
        public static void ApplyConfig(bool enabled, int port, int modelIndex)
        {
            lock (m_Lock)
            {
                s_State.Enabled = enabled;
                s_State.Port = OllamaAdvisor.ClampPort(port);
                s_State.ModelIndex = OllamaAdvisor.ClampModelIndex(modelIndex);
            }
        }

        // ---- readbacks (diagnostics/tests) -------------------------------------
        public static int GetPort() { lock (m_Lock) return s_State.Port; }
        public static int GetModelIndex() { lock (m_Lock) return s_State.ModelIndex; }
        public static bool GetEnabled() { lock (m_Lock) return s_State.Enabled; }
        public static long GetRequestsSent() { lock (m_Lock) return s_State.RequestsSent; }
        public static long GetRequestsFailed() { lock (m_Lock) return s_State.RequestsFailed; }
        public static long GetRequestsSucceeded() { lock (m_Lock) return s_State.RequestsSucceeded; }
        public static long GetAdviceAccepted() { lock (m_Lock) return s_State.AdviceAccepted; }
        public static long GetAdviceRejected() { lock (m_Lock) return s_State.AdviceRejected; }
        public static long GetUncertainPasses() { lock (m_Lock) return s_State.UncertainPasses; }
        public static long GetBackoffBlocks() { lock (m_Lock) return s_State.BackoffBlocks; }
        public static string GetLastAdvice() { lock (m_Lock) return s_State.LastAdvice; }
        public static string GetLastFailureReason() { lock (m_Lock) return s_State.LastFailureReason; }
        public static bool HasPendingResponse()
        {
            lock (m_Lock) return s_State.InFlightRequestMs >= 0 && s_State.PendingResponseSet;
        }

        public static List<string> Lines()
        {
            List<string> lines = new List<string>(MaxPendingLines);
            lock (m_Lock)
            {
                lines.Add("CrewAdvice: enabled=" + (s_State.Enabled ? "yes" : "no")
                    + " port=" + s_State.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " model=" + OllamaAdvisor.ModelName(s_State.ModelIndex)
                    + " sent=" + s_State.RequestsSent
                    + " ok=" + s_State.RequestsSucceeded
                    + " failed=" + s_State.RequestsFailed
                    + " accepted=" + s_State.AdviceAccepted
                    + " rejected=" + s_State.AdviceRejected);
                lines.Add("CrewAdvice: uncertain=" + s_State.UncertainPasses
                    + " backoffBlocks=" + s_State.BackoffBlocks
                    + " lastFailure=" + (s_State.LastFailureReason ?? "-"));
            }
            return lines;
        }

        public static List<string> StatusLines()
        {
            List<string> lines = new List<string>();
            lock (m_Lock)
            {
                lines.Add("CrewAdvisor: enabled=" + (s_State.Enabled ? "yes" : "no")
                    + " model=" + OllamaAdvisor.ModelName(s_State.ModelIndex)
                    + " sent=" + s_State.RequestsSent
                    + " accepted=" + s_State.AdviceAccepted
                    + " rejected=" + s_State.AdviceRejected);
            }
            return lines;
        }

        // ---- per-tick evaluation (called from the WorldTick postfix guard) ----
        //
        // Gate order mirrors OllamaAdvisor.Evaluate exactly (P20 house shape):
        // 1 authority (deny-by-default) -> 2 enabled+transport -> 3 consume
        // completed response (lines fire immediately) -> 4 back-off ->
        // 5 cadence -> 6 snapshot fail-safe (fail-open) -> 7 single-flight ->
        // 8 dispatch (prompt built on the game thread, worker runs HTTP).
        public static int Evaluate(int nowMs)
        {
            Func<bool> authorityProbe;
            Func<WorldSnapshot> worldProvider;
            Action<string> listener;
            OllamaAdvisor.ITransport transport;
            bool enabled;
            int port;
            int modelIndex;
            lock (m_Lock)
            {
                authorityProbe = m_AuthorityProbe;
                worldProvider = m_WorldProvider;
                listener = m_DecisionListener;
                transport = m_Transport;
                enabled = s_State.Enabled;
                port = s_State.Port;
                modelIndex = s_State.ModelIndex;
            }

            // 1) Authority: deny-by-default (unset or faulting probe = no-op).
            if (authorityProbe == null) return 0;
            bool isAuth;
            try { isAuth = authorityProbe(); } catch (Exception) { return 0; }
            if (!isAuth) return 0;

            // 2) Optional by default: config off OR transport unwired => inert.
            if (!enabled || transport == null) return 0;

            List<string> pending = new List<string>(MaxPendingLines);
            int emitted = 0;

            // 3) Consume a completed response FIRST (bounded, no lock held
            // across HTTP; the worker never takes this lock — see RunWorker).
            string responseText = null;
            bool responsePresent = false;
            int completedMs = -1;
            lock (m_Lock)
            {
                if (s_State.PendingResponseSet)
                {
                    responseText = s_State.PendingResponse;
                    responsePresent = true;
                    completedMs = s_State.PendingResponseMs;
                    s_State.PendingResponse = null;
                    s_State.PendingResponseMs = 0;
                    s_State.PendingResponseSet = false;
                    s_State.InFlightRequestMs = -1;
                }
            }
            if (responsePresent)
            {
                ConsumeResponse(responseText, completedMs, modelIndex, pending, ref emitted);
                // Fire consumed-response lines IMMEDIATELY (before any early
                // return below) so advice is never held hostage by the
                // cadence/back-off gates.
                for (int i = 0; i < pending.Count && i < MaxPendingLines; i++)
                {
                    if (listener != null) listener(pending[i]);
                }
                pending.Clear();
            }

            // 4) Back-off window (silent: steady-state, not an event).
            int backoffUntil;
            lock (m_Lock) backoffUntil = s_State.BackoffUntilMs;
            if (backoffUntil >= 0 && unchecked(nowMs - backoffUntil) < 0)
            {
                lock (m_Lock) s_State.BackoffBlocks++;
                return emitted;
            }

            // 5) Cadence gate (wrap-safe unchecked subtraction).
            lock (m_Lock)
            {
                if (s_State.LastEvalMs >= 0 && unchecked(nowMs - s_State.LastEvalMs) < MinRecheckMs)
                    return emitted;
                s_State.LastEvalMs = nowMs;
            }

            // 6) Snapshot fail-safe (fail-open on uncertainty): same shared
            // freshness standard as P9/P16-P20. The world snapshot gates the
            // crew picture; the registry's own AgentViews are read after.
            Func<WorldSnapshot> world = worldProvider;
            WorldSnapshot snapshot = null;
            if (world != null)
            {
                try { snapshot = world(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured
                || unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs
                || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0
                || !snapshot.GameStarted)
            {
                lock (m_Lock) s_State.UncertainPasses++;
                return emitted; // silently wait for a fresh snapshot
            }

            // 7) Single-flight gate + dispatch (one request at a time).
            int inFlight;
            lock (m_Lock) inFlight = s_State.InFlightRequestMs;
            if (inFlight >= 0) return emitted;

            if (Interlocked.CompareExchange(ref s_WorkerRunning, 1, 0) != 0) return emitted;

            // Build the crew prompt on the game thread (pure registry readbacks).
            string requestJson;
            try { requestJson = BuildRequestJson(modelIndex); }
            catch (Exception)
            {
                s_WorkerRunning = 0;
                return emitted;
            }
            if (requestJson == null)
            {
                s_WorkerRunning = 0;
                return emitted;
            }

            lock (m_Lock)
            {
                s_State.InFlightRequestMs = nowMs;
                s_State.RequestsSent++;
            }

            // Dedicated worker thread: touches no game state. Captures the
            // validated port/model (never re-read config off-thread).
            int portCapture = port;
            int modelCapture = modelIndex;
            string promptCapture = requestJson;
            try
            {
                Thread worker = new Thread(delegate()
                {
                    RunWorker(transport, promptCapture, portCapture, modelCapture);
                });
                worker.IsBackground = true;
                worker.Name = "CapBot-CrewAdvisor";
                worker.Start();
            }
            catch (Exception)
            {
                s_WorkerRunning = 0;
                RecordFailure("worker thread refused", nowMs, pending, ref emitted);
            }

            // Fire any pending lines (failure lines from the dispatch path).
            for (int i = 0; i < pending.Count && i < MaxPendingLines; i++)
            {
                if (listener != null) listener(pending[i]);
            }
            return emitted;
        }

        // Worker-thread body: HTTP via the transport seam, then park the raw
        // body in the single-slot buffer (brief m_Lock hold, reference-only).
        private static void RunWorker(OllamaAdvisor.ITransport transport, string requestJson, int port, int modelIndex)
        {
            string body = null;
            try { body = transport.PostChatJson(requestJson, RequestTimeoutMs); }
            catch (Exception) { body = null; }

            lock (m_Lock)
            {
                s_State.PendingResponse = body;
                s_State.PendingResponseMs = Environment.TickCount;
                s_State.PendingResponseSet = true;
            }
        }

        // ---- response consumption (game thread) --------------------------------
        private static void ConsumeResponse(
            string responseText, int completedMs, int modelIndex,
            List<string> pending, ref int emitted)
        {
            s_WorkerRunning = 0;

            // Same shared advisory data contract as P20: message.content
            // extraction + ADVICE validation (control chars rejected).
            string content = OllamaAdvisor.ExtractContent(responseText);
            string advice;
            bool accepted = OllamaAdvisor.ValidateAdvice(content, out advice);
            lock (m_Lock)
            {
                if (accepted)
                {
                    s_State.RequestsSucceeded++;
                    s_State.ConsecutiveFailures = 0;
                    s_State.BackoffUntilMs = -1;
                    s_State.AdviceAccepted++;
                    s_State.LastAdviceMs = completedMs;
                    s_State.LastAdvice = advice;
                    pending.Add("CrewAdvice model=" + OllamaAdvisor.ModelName(modelIndex) + " advice=" + advice);
                }
                else
                {
                    s_State.AdviceRejected++;
                    if (content == null) s_State.LastFailureReason = "unparseable response";
                    else s_State.LastFailureReason = "advice rejected";
                    pending.Add("CrewAdviceInvalid model=" + OllamaAdvisor.ModelName(modelIndex)
                        + " reason=" + s_State.LastFailureReason);
                }
                emitted = pending.Count;
            }
        }

        private static void RecordFailure(string reason, int nowMs, List<string> pending, ref int emitted)
        {
            lock (m_Lock)
            {
                s_State.RequestsFailed++;
                s_State.ConsecutiveFailures++;
                s_State.LastFailureReason = reason;
                if (s_State.ConsecutiveFailures >= MaxConsecutiveFailures)
                {
                    s_State.BackoffUntilMs = nowMs + CooldownAfterFailureMs;
                    s_State.ConsecutiveFailures = 0;
                }
            }
            pending.Add("CrewAdviceFailed reason=" + reason);
            emitted = pending.Count;
        }

        // ---- request building (game thread, pure registry readbacks) -----------

        // Builds the /api/chat JSON request from the crew picture: the P10
        // CrewAgent hooks (Role/RoleName, LastTaskOutcome, LastKnownTLIName)
        // via the additive AgentViews() readback. Bounded: first
        // MaxAgentsInPrompt agents, names/TLIs truncated, unknown sentinels.
        internal static string BuildRequestJson(int modelIndex)
        {
            string model = OllamaAdvisor.ModelName(modelIndex);
            if (model == null) return null;

            List<CrewAgentRegistry.AgentView> agents = CrewAgentRegistry.AgentViews();
            int active = 0;
            int bots = 0;
            int humans = 0;
            int captains = 0;
            int withOutcome = 0;
            int withLocation = 0;
            int withRoleName = 0;
            StringBuilder p = new StringBuilder(MaxPromptLen);
            p.Append("Crew roster:");
            for (int i = 0; i < agents.Count && i < MaxAgentsInPrompt; i++)
            {
                CrewAgentRegistry.AgentView v = agents[i];
                if (v == null) continue;
                if (v.Lifecycle == CrewAgentLifecycle.Active) active++;
                if (v.IsBot) bots++; else humans++;
                if (v.IsCaptain) captains++;
                if (!string.IsNullOrEmpty(v.LastTaskOutcome)) withOutcome++;
                if (!string.IsNullOrEmpty(v.LastKnownTLIName)) withLocation++;
                if (!string.IsNullOrEmpty(v.RoleName)) withRoleName++;

                p.Append(" [");
                p.Append(v.IsBot ? "bot" : "human");
                if (v.IsCaptain) p.Append(" captain");
                p.Append(" role=").Append(v.Role >= CrewRole.Unknown && v.Role <= CrewRole.Other
                    ? v.Role.ToString() : "unknown");
                p.Append(" roleName=").Append(string.IsNullOrEmpty(v.RoleName)
                    ? "unknown" : EscapeJson(Truncate(v.RoleName, 16)));
                p.Append(" name=").Append(string.IsNullOrEmpty(v.Name)
                    ? "unknown" : EscapeJson(Truncate(v.Name, 16)));
                p.Append(" lastLoc=").Append(string.IsNullOrEmpty(v.LastKnownTLIName)
                    ? "unknown" : EscapeJson(Truncate(v.LastKnownTLIName, 16)));
                p.Append(" lastOutcome=").Append(string.IsNullOrEmpty(v.LastTaskOutcome)
                    ? "unknown" : v.LastTaskOutcome);
                p.Append(']');
            }
            p.Append("; counts: active=").Append(active)
             .Append(" bots=").Append(bots)
             .Append(" humans=").Append(humans)
             .Append(" captains=").Append(captains)
             .Append(" withLastOutcome=").Append(withOutcome)
             .Append(" withLastLocation=").Append(withLocation)
             .Append(" withRoleName=").Append(withRoleName);

            string sys = "You are a crew-management advisor for a spaceship crew game. "
                + "You receive read-only crew observations. Reply with EXACTLY one line in the form "
                + "ADVICE: <action> (<reason>, max 12 words). You never issue commands; "
                + "your output is advisory data only and a deterministic system decides everything.";

            StringBuilder sb = new StringBuilder(MaxPromptLen + 512);
            sb.Append("{\"model\":\"").Append(EscapeJson(model)).Append('"');
            sb.Append(",\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(EscapeJson(sys)).Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(EscapeJson(p.ToString())).Append("\"}");
            sb.Append("],\"stream\":false");
            sb.Append(",\"keep_alive\":\"30m\"");
            if (OllamaAdvisor.IsRequestingModelThinking(model)) sb.Append(",\"think\":false");
            sb.Append(",\"options\":{\"num_predict\":48,\"temperature\":0.2}}");
            return sb.ToString();
        }

        private static string EscapeJson(string text)
        {
            if (text == null) return string.Empty;
            StringBuilder sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(c)) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen);
        }

        // Test/dev isolation only. Never call in game code.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                s_State = new DirectorState();
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_DecisionListener = null;
                m_Transport = null;
            }
            s_WorkerRunning = 0;
        }
    }
}