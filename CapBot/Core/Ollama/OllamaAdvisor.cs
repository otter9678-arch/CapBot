using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CapBot.Core.Tasks;
using CapBot.Core.World;

namespace CapBot.Core.Ollama
{
    // ---- Phase 20: Ollama advisor (optional, sandboxed, RECOMMEND-ONLY) ------
    //
    // A bounded advisory layer that asks a LOCAL Ollama server (loopback only)
    // for one recommendation line about the crew-gather picture and emits it
    // as diagnostics. The advice is DATA:
    //   - it NEVER creates, queues, cancels, prioritizes or mutates any task;
    //   - it NEVER touches CapabilityRequest fields, task metadata, the
    //     dispatcher, or any PULSAR API;
    //   - it NEVER feeds P9/P14 deterministic decisions (their contracts
    //     exclude LLMs entirely);
    //   - deterministic rules ALWAYS override — the advisor only adds a
    //     bounded log line.
    //
    // Security posture (audit C1 anti-pattern, deliberately inverted):
    //   - endpoint hard-anchored to 127.0.0.1 (constant); the ONLY network
    //     knob is the port (config, 1..65535 clamped); no host string, no
    //     URL from config, no remote hosts, no DNS names;
    //   - no ModUpdater reuse: no boot-time network calls, no WebClient;
    //     HttpClient with hard timeouts, worker threads only;
    //   - deny-by-default: unset transport seam = the advisor does nothing;
    //     config default is OFF.
    //
    // Threading model (measured 2026-09-08 against Ollama 0.33.3):
    //   cold model load ~40 s, warm prompt-cached inference ~125-240 ms —
    //   categorically never synchronous inside the Unity main-thread tick.
    //   Each request runs on its own dedicated worker thread (one in flight
    //   max); the result lands in a lock-protected single-slot buffer and is
    //   consumed by the NEXT Evaluate pass on the game thread. The worker
    //   thread touches NO Unity/Photon/game APIs — only the pure snapshot
    //   text prepared on the game thread and the loopback HTTP endpoint.
    //
    // House director pattern (P9/P14/P15/P16/P17/P18/P19): public static
    // class + private DirectorState + m_Lock; 5 fail-closed seams (the 4
    // standard ones + ITransport); cadence-gated Evaluate(nowMs); snapshot
    // fail-safe (fail-open on uncertainty); bounded work; pending lines
    // fired after lock release; ResetForTests clears state AND seams.

    public static class OllamaAdvisor
    {
        // ---- thresholds (deterministic, documented in docs) ----
        public const int MinRecheckMs = 5000;         // decision cadence (director pattern)
        public const int MaxStaleSnapshotMs = 20000;  // the one shared director freshness standard
        public const int MaxPendingLines = 4;
        public const int MaxAdviceLen = 240;          // bounded advice text (validated)
        public const int MaxPromptLen = 4000;         // bounded request payload
        public const int RequestTimeoutMs = 90000;    // hard HttpClient timeout (covers cold load)
        public const int PortDefault = 11434;
        public const int PortMin = 1;
        public const int PortMax = 65535;
        public const int ModelDefault = 0;            // index into the bounded model table
        public const int MaxConsecutiveFailures = 3;  // advisor backs off after this many
        public const int CooldownAfterFailureMs = 120000; // 2 min back-off window

        // Bounded model table (no SaveValue<string>: model selection is a
        // config int index into this fixed vocabulary, cycle button in menu).
        // P44 (owner mandate): index 0 — the DEFAULT — is qwen3:latest. The
        // owner's Ollama model is qwen3:latest; no silent substitution to
        // qwen:latest / qwen2.5-coder is ever done (requests carry exactly
        // the configured model name).
        public static readonly string[] KnownModels = new string[]
        {
            "qwen3:latest",       // 0 — OWNER MANDATE default (thinking model; request carries "think":false)
            "qwen2.5:latest",     // 1 — legacy default (7.6B, measured warm ~240 ms)
            "qwen:latest",        // 2 — smallest (4B, measured warm ~125 ms)
            "qwen2.5-coder:latest" // 3 — coder variant (present locally)
        };

        // Thinking models burn ordinary completion tokens on a reasoning
        // trace before emitting the actual answer; with the advisor's small
        // completion budget that starves the answer entirely (live-verified
        // 2026-09-08: qwen3:latest returned empty content at num_predict 48
        // AND 256, done_reason "length", all tokens in message.thinking).
        // Ollama's per-request "think":false disables the trace for models
        // that support it; Ollama ignores it for models that do not.
        private static readonly string[] ThinkingModels = new string[]
        {
            "qwen3:latest"
        };

        private static bool IsThinkingModel(string model)
        {
            if (model == null) return false;
            for (int i = 0; i < ThinkingModels.Length; i++)
                if (string.Equals(model, ThinkingModels[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // Internal accessor for sibling advisors sharing the model vocabulary
        // (CrewAdvisor/P21) so their request builders stay in lockstep.
        internal static bool IsRequestingModelThinking(string model)
        {
            return IsThinkingModel(model);
        }

        // ---- P44: owner-mandated model identity + availability probe ---------
        //
        // The owner's Ollama model is qwen3:latest (index 0). Requests carry
        // EXACTLY the configured model name — never a silent substitute. The
        // probe seam (wired in Mod.cs to a bounded loopback /api/tags GET)
        // reports availability truthfully; a fault records the EXACT error
        // text (mandate: "fail clearly and report the exact error"). No
        // automatic model switching exists anywhere in this stack.
        public static string RequiredModel { get { return KnownModels[0]; } }

        // Probe seam: model name -> null when available, else exact error.
        private static Func<string, string> m_ModelProbe;
        private static bool m_ModelAvailableKnown;   // false = never probed
        private static bool m_ModelAvailable;
        private static string m_ModelProbeError = string.Empty;

        public static void SetModelProbe(Func<string, string> probe)
        {
            lock (m_Lock) m_ModelProbe = probe;
        }

        // Runs the wired probe (thread-safe; bounded by the probe itself —
        // the production probe is a loopback GET with a hard timeout).
        // Called at startup (one-shot) and by tests with fakes.
        public static bool RunModelProbe()
        {
            Func<string, string> probe;
            lock (m_Lock) probe = m_ModelProbe;
            if (probe == null)
            {
                lock (m_Lock) { m_ModelAvailableKnown = false; m_ModelProbeError = "probe not wired"; }
                return false;
            }
            string error;
            try { error = probe(RequiredModel); }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            bool available = string.IsNullOrEmpty(error);
            lock (m_Lock)
            {
                m_ModelAvailableKnown = true;
                m_ModelAvailable = available;
                m_ModelProbeError = available ? string.Empty : (error ?? "unknown error");
            }
            return available;
        }

        // The mandated three-line startup diagnostic (+ exact error when
        // unavailable). ConfiguredModel is the owner requirement text;
        // RequestModel is what requests actually carry (the same string —
        // substitution is impossible by construction).
        public static List<string> ModelDiagnosticLines()
        {
            List<string> lines = new List<string>(4);
            string current;
            bool known, available;
            string error;
            lock (m_Lock)
            {
                current = ModelName(s_State.ModelIndex);
                known = m_ModelAvailableKnown;
                available = m_ModelAvailable;
                error = m_ModelProbeError;
            }
            lines.Add("OllamaConfiguredModel=" + RequiredModel);
            lines.Add("OllamaRequestModel=" + current);
            lines.Add("OllamaModelAvailable=" + (known ? (available ? "true" : "false") : "unknown"));
            if (known && !available) lines.Add("OllamaModelProbeError=" + error);
            return lines;
        }

        internal static bool ModelAvailabilityKnown { get { lock (m_Lock) return m_ModelAvailableKnown; } }
        internal static bool ModelAvailable { get { lock (m_Lock) return m_ModelAvailable; } }
        internal static string ModelProbeError { get { lock (m_Lock) return m_ModelProbeError; } }

        // ---- P51: startup /api/chat self-test (owner mandate) ----------------
        //
        // The P44 probe only proves /api/tags lists the model; the mandate
        // requires a HARMLESS one-shot /api/chat round-trip at startup that
        // proves the full POST path (model loads, JSON returns, content
        // extracts). Harmless = a pure-echo system prompt, no game state, no
        // gameplay action. Runs on the caller's thread (Mod.cs launches it on
        // a background thread after the model probe — never the Unity main
        // thread). Results land in the same seam-state pattern as the probe.
        private static Func<int, string> m_ChatSelfTest;   // port -> raw body | null
        private static bool m_SelfTestDone;                // false = never run
        private static string m_SelfTestError = string.Empty;

        public static void SetChatSelfTest(Func<int, string> selfTest)
        {
            lock (m_Lock) m_ChatSelfTest = selfTest;
        }

        // Runs the wired self-test against the REQUIRED model (qwen3:latest
        // by construction — the request builder is the production one).
        // Called once at startup; never blocks the game thread.
        public static bool RunChatSelfTest(int port)
        {
            Func<int, string> selfTest;
            lock (m_Lock) selfTest = m_ChatSelfTest;
            if (selfTest == null)
            {
                lock (m_Lock) { m_SelfTestDone = false; m_SelfTestError = "self-test not wired"; }
                return false;
            }
            string body;
            try { body = selfTest(port); }
            catch (Exception ex) { body = null; RecordSelfTestRaw(ex); return false; }
            bool ok;
            if (body != null)
            {
                // The self-test accepts ANY parseable chat response with a
                // content string (it is not an ADVICE-grammar test): the
                // point is transport+model+parser shape, not vocabulary.
                ok = ExtractContent(body) != null;
            }
            else ok = false;
            lock (m_Lock)
            {
                m_SelfTestDone = true;
                m_SelfTestError = ok
                    ? string.Empty
                    : (body == null ? "transport returned no body (timeout or unreachable)" : "response parsed but no message.content found");
            }
            return ok;
        }

        private static void RecordSelfTestRaw(Exception ex)
        {
            string msg = "self-test threw " + ex.GetType().Name + ": " + ex.Message;
            lock (m_Lock) m_SelfTestError = msg;
        }

        public static List<string> SelfTestDiagnosticLines()
        {
            List<string> lines = new List<string>(2);
            lock (m_Lock)
            {
                lines.Add("OllamaSelfTest=" + (m_SelfTestDone ? (m_SelfTestError.Length == 0 ? "pass" : "fail") : "not-run"));
                if (m_SelfTestDone && m_SelfTestError.Length > 0) lines.Add("OllamaSelfTestError=" + m_SelfTestError);
            }
            return lines;
        }

        internal static bool SelfTestDone { get { lock (m_Lock) return m_SelfTestDone; } }
        internal static string SelfTestError { get { lock (m_Lock) return m_SelfTestError; } }

        public const string TargetKindNone = "NONE";   // advisory prompts carry no game target

        // ---- transport seam (fail-closed: unset = advisor does nothing) ----
        public interface ITransport
        {
            // Synchronous loopback call; returns the raw response body or
            // null on any failure. Implementations MUST respect the timeout
            // and MUST NOT touch game APIs. Called on a worker thread only.
            string PostChatJson(string requestJson, int timeoutMs);
        }

        // ---- seams ----
        private static Func<bool> m_AuthorityProbe;
        private static Func<int> m_NowMsProvider;
        private static Func<WorldSnapshot> m_WorldProvider;
        private static Action<string> m_DecisionListener;
        private static ITransport m_Transport;

        private static readonly object m_Lock = new object();
        private static int s_WorkerRunning;          // 0/1 single-flight gate (Interlocked)

        private sealed class DirectorState
        {
            public int LastEvalMs;                   // cadence gate
            public int Port = PortDefault;           // validated config mirror
            public int ModelIndex = ModelDefault;    // validated config mirror
            public bool Enabled;                     // config mirror (checked on game thread)
            public int InFlightRequestMs = -1;       // -1 = no request in flight
            public bool PendingResponseSet;          // parked-response present (worker-written)
            public string PendingResponse;           // single-slot result buffer (null = transport fault)
            public string PendingOutcome = "ok";     // worker classification: ok | fault | timeout
            public int PendingLatencyMs;             // measured transport round-trip (worker-written)
            public int PendingResponseMs;            // completion stamp (worker-written)
            public long RequestsSent;
            public long RequestsFailed;
            public long RequestsSucceeded;
            public long RequestsTimeouts;
            public long LatencySumMs;
            public long LatencySamples;
            public long AdviceAccepted;
            public long AdviceRejected;
            public long UncertainPasses;
            public long BackoffBlocks;
            public int ConsecutiveFailures;
            public int BackoffUntilMs = -1;
            public int LastAdviceMs = -1;
            public string LastAdvice = string.Empty;
            public string LastFailureReason = string.Empty;
        }

        private static readonly DirectorState s_State = new DirectorState();

        // ---- seams (house pattern) ----

        public static void SetAuthorityProbe(Func<bool> probe)
        {
            lock (m_Lock) { m_AuthorityProbe = probe; }
        }

        public static void SetNowMsProvider(Func<int> provider)
        {
            lock (m_Lock) { m_NowMsProvider = provider; }
        }

        public static void SetWorldProvider(Func<WorldSnapshot> provider)
        {
            lock (m_Lock) { m_WorldProvider = provider; }
        }

        public static void SetDecisionListener(Action<string> listener)
        {
            lock (m_Lock) { m_DecisionListener = listener; }
        }

        // Production transport; null disables the advisor entirely (inert).
        public static void SetTransport(ITransport transport)
        {
            lock (m_Lock) { m_Transport = transport; }
        }

        // ---- config mirror (validated, polled from Config on game thread) ----

        public static void ApplyConfig(bool enabled, int port, int modelIndex)
        {
            lock (m_Lock)
            {
                s_State.Enabled = enabled;
                s_State.Port = ClampPort(port);
                s_State.ModelIndex = ClampModelIndex(modelIndex);
            }
        }

        public static int ClampPort(int port)
        {
            if (port < PortMin) return PortDefault;
            if (port > PortMax) return PortDefault;
            return port;
        }

        public static int ClampModelIndex(int index)
        {
            if (index < 0) return ModelDefault;
            if (index >= KnownModels.Length) return ModelDefault;
            return index;
        }

        // ---- the game-thread pass -------------------------------------------
        //
        // Gate order: authority → config-enabled → transport → back-off →
        // cadence → snapshot fail-safe (fail-open on uncertainty) →
        // consume-response → maybe-dispatch. Called from the WorldTick
        // Postfix after the captain block. Returns number of lines emitted.
        public static int Evaluate(int nowMs)
        {
            Func<bool> authorityProbe;
            Func<WorldSnapshot> worldProvider;
            Action<string> listener;
            ITransport transport;
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

            // Authority: deny-by-default (unset or faulting probe = no-op).
            if (authorityProbe == null) return 0;
            bool isAuth;
            try { isAuth = authorityProbe(); } catch (Exception) { return 0; }
            if (!isAuth) return 0;

            // Optional by default: config off OR transport unwired => inert.
            if (!enabled || transport == null) return 0;

            List<string> pending = new List<string>(MaxPendingLines);
            int emitted = 0;

            // ---- consume a completed response FIRST (bounded, no lock held
            // across HTTP; the worker never takes this lock — see Dispatch). ----
            string responseText = null;
            string outcomeText = "ok";
            int latencyMs = 0;
            bool responsePresent = false;
            int completedMs = -1;
            lock (m_Lock)
            {
                // PendingResponseSet (not the null body) marks a completed
                // transport call — a null body is a legitimate parked fault.
                if (s_State.PendingResponseSet)
                {
                    responseText = s_State.PendingResponse;
                    outcomeText = s_State.PendingOutcome;
                    latencyMs = s_State.PendingLatencyMs;
                    responsePresent = true;
                    completedMs = s_State.PendingResponseMs;
                    s_State.PendingResponse = null;
                    s_State.PendingOutcome = "ok";
                    s_State.PendingLatencyMs = 0;
                    s_State.PendingResponseMs = 0;
                    s_State.PendingResponseSet = false;
                    s_State.InFlightRequestMs = -1;
                }
            }
            if (responsePresent)
            {
                ConsumeResponse(responseText, outcomeText, latencyMs, completedMs, nowMs, modelIndex, pending, ref emitted);
                // Fire consumed-response lines IMMEDIATELY (before any early
                // return below) so advice is never held hostage by the
                // cadence/back-off gates; reuse the buffer for the dispatch
                // path (failure lines) after.
                for (int i = 0; i < pending.Count && i < MaxPendingLines; i++)
                {
                    if (listener != null) listener(pending[i]);
                }
                pending.Clear();
            }

            // ---- back-off window ----
            int backoffUntil;
            lock (m_Lock) backoffUntil = s_State.BackoffUntilMs;
            if (backoffUntil >= 0 && unchecked(nowMs - backoffUntil) < 0)
            {
                lock (m_Lock) s_State.BackoffBlocks++;
                return emitted; // silent: back-off is steady-state, not an event
            }

            // ---- cadence gate ----
            lock (m_Lock)
            {
                if (s_State.LastEvalMs >= 0 && unchecked(nowMs - s_State.LastEvalMs) < MinRecheckMs)
                    return emitted;
                s_State.LastEvalMs = nowMs;
            }

            // ---- snapshot fail-safe (fail-open on uncertainty) ----
            WorldSnapshot snapshot = null;
            if (worldProvider != null)
            {
                try { snapshot = worldProvider(); } catch (Exception) { snapshot = null; }
            }
            if (snapshot == null || snapshot.IsNeverCaptured
                || unchecked(nowMs - snapshot.SnapshotTimeMs) > MaxStaleSnapshotMs
                || unchecked(nowMs - snapshot.SnapshotTimeMs) < 0
                || !snapshot.GameStarted)
            {
                lock (m_Lock) s_State.UncertainPasses++;
                return emitted; // silently wait for a fresh snapshot
            }

            // ---- single-flight gate + dispatch --------------------------------
            // One request at a time; the worker thread runs the HTTP call and
            // parks the raw body in the single-slot buffer. No prompt is
            // built unless a worker slot is actually acquired.
            int inFlight;
            lock (m_Lock) inFlight = s_State.InFlightRequestMs;
            if (inFlight >= 0) return emitted; // a request is already running

            if (Interlocked.CompareExchange(ref s_WorkerRunning, 1, 0) != 0) return emitted;

            // Build the prompt on the game thread (pure snapshot reads) —
            // bounded; unknown sentinels degrade to "unknown" fields.
            string requestJson;
            try { requestJson = BuildRequestJson(snapshot, modelIndex, nowMs); }
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
            // validated port/model (never re-read config off-thread) and the
            // DirectorState reference captured BEFORE the worker starts: the
            // worker parks into THAT object, so a reset (state object
            // swapped) can never receive a stale parked response (CA07b).
            DirectorState stateCapture = s_State;
            int portCapture = port;
            int modelCapture = modelIndex;
            string promptCapture = requestJson;
            try
            {
                Thread worker = new Thread(delegate()
                {
                    RunWorker(stateCapture, transport, promptCapture, portCapture, modelCapture);
                });
                worker.IsBackground = true; // never blocks game shutdown
                worker.Name = "CapBot-OllamaAdvisor";
                worker.Start();
            }
            catch (Exception)
            {
                // Thread creation refused (rare resource fault): release slot,
                // count as failed, enter back-off.
                s_WorkerRunning = 0;
                RecordFailure("worker thread refused", nowMs, pending, ref emitted);
            }

            // Fire any pending lines (house discipline: after lock release —
            // no lock is held here; pending was filled on this thread).
            for (int i = 0; i < pending.Count && i < MaxPendingLines; i++)
            {
                if (listener != null) listener(pending[i]);
            }
            return emitted;
        }

        // Worker-thread body: HTTP via the transport seam, then park the
        // classified result in the single-slot buffer. The worker NEVER
        // touches game state beyond the slot publish (under m_Lock,
        // reference-only). A null body is classified here: the transport's
        // own latency measurement distinguishes a hard TIMEOUT (the full
        // budget burned with nothing returned — treated as a request
        // failure, feeding the back-off ladder) from any other fault.

        private static void RunWorker(DirectorState state, ITransport transport, string requestJson, int port, int modelIndex)
        {
            string body = null;
            string outcome = "ok";
            int latency = 0;
            try
            {
                int startedMs = Environment.TickCount;
                body = transport.PostChatJson(requestJson, RequestTimeoutMs);
                latency = unchecked(Environment.TickCount - startedMs);
                if (body == null)
                {
                    // Distinguish timeout from other faults: the transport
                    // returned at/after its full budget with nothing = the
                    // bounded wait expired (server down or unresponsive).
                    outcome = (latency >= RequestTimeoutMs - TimeoutGraceMs) ? "timeout" : "fault";
                }
            }
            catch (Exception)
            {
                body = null;
                outcome = "fault";
            }

            // Park the classified result in the single-slot buffer. Brief
            // m_Lock hold (reference assignment only — never held across
            // HTTP); the game thread consumes the slot under the same lock,
            // so handoff is race-free. The single-flight gate
            // (s_WorkerRunning) stays set until the game thread consumes (or
            // ResetForTests clears it).
            lock (m_Lock)
            {
                state.PendingResponse = body;
                state.PendingOutcome = outcome;
                state.PendingLatencyMs = latency;
                state.PendingResponseMs = Environment.TickCount;
                state.PendingResponseSet = true;
            }
        }

        // Latency classification grace: a transport returning null at
        // >= RequestTimeoutMs - grace is classified as a timeout (the
        // bounded wait expired), anything sooner is a plain fault. Seamed
        // for tests (the 90 s real budget cannot be waited out in a unit
        // test; the test shrinks the effective timeout instead).
        public static int TimeoutGraceMs = 1000;

        // ---- response consumption (game thread) ------------------------------
        //
        // Extracts and validates the advice line from the raw response JSON,
        // then records it as DATA (bounded log line only). A parked null
        // body with outcome "timeout"/"fault" is a hard request failure:
        // counted in RequestsFailed (+ RequestsTimeouts for timeouts) and
        // fed into the back-off ladder (3 consecutive hard faults => 120 s
        // cooldown) so an OFFLINE Ollama is re-probed on a slow ladder, not
        // every cadence window.
        private static void ConsumeResponse(
            string responseText, string outcome, int latencyMs, int completedMs, int nowMs, int modelIndex,
            List<string> pending, ref int emitted)
        {
            // Worker slot bookkeeping: clear the parked slot (it was already
            // copied out by Evaluate before calling this method) and free the
            // single-flight gate.
            s_WorkerRunning = 0;

            if (responseText == null && outcome != "ok")
            {
                // Hard failure path (server down / timeout): failure counters
                // + back-off ladder. Latency still sampled (the budget was
                // actually spent). The back-off anchors on the GAME-THREAD
                // cadence clock (nowMs), never the worker's wall-clock stamp
                // (tests drive the cadence clock virtually).
                lock (m_Lock)
                {
                    s_State.RequestsFailed++;
                    if (outcome == "timeout") s_State.RequestsTimeouts++;
                    s_State.LatencySumMs += latencyMs;
                    s_State.LatencySamples++;
                    s_State.ConsecutiveFailures++;
                    s_State.LastFailureReason = outcome == "timeout"
                        ? "chat request timeout (" + RequestTimeoutMs + "ms budget)"
                        : "chat request fault (server unreachable or refused)";
                    if (s_State.ConsecutiveFailures >= MaxConsecutiveFailures)
                        s_State.BackoffUntilMs = unchecked(nowMs + CooldownAfterFailureMs);
                    pending.Add("OllamaAdviceFailed outcome=" + outcome
                        + " consecutive=" + s_State.ConsecutiveFailures
                        + " backoff=" + (s_State.BackoffUntilMs >= 0 ? "armed" : "not-armed"));
                    emitted = pending.Count;
                }
                return;
            }

            string content = ExtractContent(responseText);
            string advice;
            bool accepted = ValidateAdvice(content, out advice);
            lock (m_Lock)
            {
                s_State.LatencySumMs += latencyMs;
                s_State.LatencySamples++;
                if (accepted)
                {
                    s_State.RequestsSucceeded++;
                    s_State.ConsecutiveFailures = 0;
                    s_State.BackoffUntilMs = -1;
                    s_State.AdviceAccepted++;
                    s_State.LastAdviceMs = completedMs;
                    s_State.LastAdvice = advice;
                    pending.Add("OllamaAdvice model=" + ModelName(modelIndex) + " advice=" + advice);
                }
                else
                {
                    // A completed HTTP round-trip with unusable content is a
                    // soft failure: no back-off escalation (server answered).
                    s_State.AdviceRejected++;
                    if (content == null) s_State.LastFailureReason = "unparseable response";
                    else s_State.LastFailureReason = "advice rejected";
                    pending.Add("OllamaAdviceInvalid model=" + ModelName(modelIndex)
                        + " reason=" + s_State.LastFailureReason);
                }
                emitted = pending.Count;
            }
        }

        // Extracts "message"."content" from the Ollama /api/chat
        // non-streaming single-object JSON response (shape verified against
        // Ollama 0.33.3 on 2026-09-08: {"model":...,"message":{"role":
        // "assistant","content":"..."},"done":true,...}). Hand-rolled per the
        // audit guidance (L91): no JSON library is bundled; the ModUpdater
        // ExtractJsonString pattern (string-aware, escape-aware) is mirrored.
        internal static string ExtractContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            // Locate the "message" object (skip any "message" occurrences
            // inside string values by scanning for the key at depth 1 —
            // Ollama's chat response is flat, one message object).
            int msgKey = FindKeyIndex(json, "message");
            if (msgKey < 0) return null;
            int objStart = json.IndexOf('{', msgKey);
            if (objStart < 0) return null;

            // Extract "content" as a string value anywhere inside the message
            // object (role field comes first in the verified shape; content
            // is a JSON string possibly containing escapes).
            string content = ExtractStringAfterKey(json, objStart, "content");
            if (content == null)
            {
                // Fall back to a whole-response scan (some proxies reorder).
                content = ExtractStringAfterKey(json, 0, "content");
            }
            return content;
        }

        // Finds the index of "key": at a JSON object boundary (outside
        // string literals). Returns -1 if absent.
        private static int FindKeyIndex(string json, string key)
        {
            string needle = "\"" + key + "\"";
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"')
                {
                    // Candidate key check.
                    if (string.CompareOrdinal(json, i, needle, 0, needle.Length) == 0
                        && depth <= 1)
                    {
                        int j = i + needle.Length;
                        while (j < json.Length && (json[j] == ' ' || json[j] == ':')) j++;
                        if (j < json.Length) return i;
                    }
                    inString = true;
                }
                else if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            return -1;
        }

        // Extracts the string value following "key": inside the region
        // starting at regionStart. Handles \" \\ \/ \n \r \t \uXXXX escapes.
        private static string ExtractStringAfterKey(string json, int regionStart, string key)
        {
            string needle = "\"" + key + "\"";
            int i = regionStart;
            while (i < json.Length)
            {
                int hit = json.IndexOf(needle, i, StringComparison.Ordinal);
                if (hit < 0) return null;
                int j = hit + needle.Length;
                while (j < json.Length && json[j] == ' ') j++;
                if (j < json.Length && json[j] == ':')
                {
                    j++;
                    while (j < json.Length && json[j] == ' ') j++;
                    if (j < json.Length && json[j] == '"')
                    {
                        return ParseJsonString(json, j);
                    }
                }
                i = hit + needle.Length;
            }
            return null;
        }

        // Parses a JSON string starting at the opening quote. Returns the
        // decoded value, or null on malformed input.
        private static string ParseJsonString(string json, int openQuote)
        {
            StringBuilder sb = new StringBuilder(MaxAdviceLen + 16);
            int i = openQuote + 1;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    i++;
                    if (i >= json.Length) return null;
                    char e = json[i];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 >= json.Length) return null;
                            int code;
                            if (!int.TryParse(json.Substring(i + 1, 4),
                                System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out code)) return null;
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: return null; // unknown escape = malformed
                    }
                }
                else
                {
                    sb.Append(c);
                    if (sb.Length > MaxAdviceLen + 64) return null; // bounded
                }
                i++;
            }
            return null; // unterminated
        }

        // Validates and bounds the advice line. The model is told to answer
        // in the form "ADVICE: <action> (<reason>)"; anything else is
        // rejected as data (never executed, never reinterpreted).
        internal static bool ValidateAdvice(string content, out string advice)
        {
            advice = null;
            if (string.IsNullOrEmpty(content)) return false;
            string trimmed = content.Trim();
            if (trimmed.Length < 8) return false; // "ADVICE: x" minimum
            if (trimmed.Length > MaxAdviceLen) trimmed = trimmed.Substring(0, MaxAdviceLen);
            // Vocabulary gate: must start with the advised prefix.
            if (!trimmed.StartsWith("ADVICE:", StringComparison.OrdinalIgnoreCase)) return false;
            // Bounded character hygiene: no control characters (advice is
            // log text; newlines would inject log lines).
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (char.IsControl(c)) return false;
            }
            advice = trimmed;
            return true;
        }

        // ---- request building (game thread, pure snapshot reads) -----------

        // Builds the /api/chat JSON request. Hand-rolled serialization (no
        // JSON lib): the prompt is fully synthetic — every variable value is
        // either a bounded snapshot string (escaped through EscapeJson) or a
        // number. keep_alive keeps the model resident (cold load ~40 s would
        // otherwise thrash between advisor fires).
        internal static string BuildRequestJson(WorldSnapshot snapshot, int modelIndex, int nowMs)
        {
            string model = ModelName(modelIndex);
            if (model == null) return null;

            // Crew divergence summary (P18 substrate: the crew-gather picture).
            int bots = 0;
            int botsWithTli = 0;
            int divergent = 0;
            int unknown = 0;
            if (snapshot.Crew != null)
            {
                string captainTli = null;
                for (int i = 0; i < snapshot.Crew.Count && i < WorldSnapshot.MaxCrew; i++)
                {
                    CrewMemberSnapshot c = snapshot.Crew[i];
                    if (c == null || !c.IsCaptain) continue;
                    if (c.CurrentTLIName != null && c.CurrentTLIName.Length > 0) captainTli = c.CurrentTLIName;
                    break;
                }
                for (int i = 0; i < snapshot.Crew.Count && i < WorldSnapshot.MaxCrew; i++)
                {
                    CrewMemberSnapshot c = snapshot.Crew[i];
                    if (c == null || !c.IsBot || c.IsCaptain) continue;
                    bots++;
                    if (c.AliveKnown && c.Alive && c.CurrentTLIName != null && c.CurrentTLIName.Length > 0)
                    {
                        botsWithTli++;
                        if (captainTli != null && !string.Equals(c.CurrentTLIName, captainTli, StringComparison.Ordinal))
                            divergent++;
                    }
                    else if (!c.AliveKnown || c.CurrentTLIName == null || c.CurrentTLIName.Length == 0) unknown++;
                }
            }
            int hostiles = snapshot.Threats != null ? snapshot.Threats.KnownHostileShipIds.Count : -1;
            int invaders = snapshot.Threats != null ? snapshot.Threats.InvadersOnboardCount : -1;
            bool inWarp = snapshot.Navigation != null && snapshot.Navigation.InWarp;
            int sectorId = snapshot.Navigation != null ? snapshot.Navigation.CurrentSectorId : -1;
            string sectorName = snapshot.Navigation != null ? snapshot.Navigation.CurrentSectorName : null;
            float hull = (snapshot.Ships != null && snapshot.Ships.Count > 0 && snapshot.Ships[0] != null)
                ? snapshot.Ships[0].HullFraction : float.NaN;
            float reactor = snapshot.PlayerShipReactorTempFraction;

            StringBuilder p = new StringBuilder(MaxPromptLen);
            p.Append("Crew observations: bots=").Append(bots)
             .Append(" botsWithLocation=").Append(botsWithTli)
             .Append(" botsAwayFromCaptain=").Append(divergent)
             .Append(" unknownCrewData=").Append(unknown);
            p.Append("; hostiles=").Append(hostiles >= 0 ? hostiles.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown");
            p.Append("; boarders=").Append(invaders >= 0 ? invaders.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown");
            p.Append("; inWarp=").Append(inWarp ? "yes" : "no");
            p.Append("; sector=").Append(sectorId >= 0 ? sectorId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown");
            if (!string.IsNullOrEmpty(sectorName)) p.Append(" (").Append(EscapeJson(Truncate(sectorName, 40))).Append(')');
            p.Append("; fireCount=").Append(snapshot.PlayerShipFireCount >= 0 ? snapshot.PlayerShipFireCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown");
            if (!float.IsNaN(snapshot.PlayerShipReactorTempFraction))
                p.Append("; reactorTemp=").Append(snapshot.PlayerShipReactorTempFraction.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            if (snapshot.Ships != null && snapshot.Ships.Count > 0 && snapshot.Ships[0] != null
                && !float.IsNaN(snapshot.Ships[0].HullFraction))
                p.Append("; hullFraction=").Append(snapshot.Ships[0].HullFraction.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            if (snapshot.Resources != null && snapshot.Resources.FuelCapsules >= 0)
                p.Append("; fuelCapsules=").Append(snapshot.Resources.FuelCapsules.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (snapshot.Resources != null && snapshot.Resources.CoolantLevelPercent >= 0f && !float.IsNaN(snapshot.Resources.CoolantLevelPercent))
                p.Append("; coolantPercent=").Append(snapshot.Resources.CoolantLevelPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            if (snapshot.Resources != null && snapshot.Resources.Credits >= 0)
                p.Append("; credits=").Append(snapshot.Resources.Credits.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string prompt = p.ToString();

            string sys = "You are a crew-management advisor for a spaceship crew game. "
                + "You receive read-only observations. Reply with EXACTLY one line in the form "
                + "ADVICE: <action> (<reason>, max 12 words). You never issue commands; "
                + "your output is advisory data only and a deterministic system decides everything.";

            StringBuilder sb = new StringBuilder(MaxPromptLen + 512);
            sb.Append("{\"model\":\"").Append(EscapeJson(model)).Append('"');
            sb.Append(",\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(EscapeJson(sys)).Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(EscapeJson(prompt)).Append("\"}");
            sb.Append("],\"stream\":false");
            sb.Append(",\"keep_alive\":\"30m\"");
            if (IsThinkingModel(model)) sb.Append(",\"think\":false");
            sb.Append(",\"options\":{\"num_predict\":48,\"temperature\":0.2}}");
            return sb.ToString();
        }

        internal static string ModelName(int index)
        {
            int i = ClampModelIndex(index);
            if (i < 0 || i >= KnownModels.Length) return null;
            return KnownModels[i];
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
                        if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Truncate(string text, int max)
        {
            if (text == null) return null;
            return text.Length <= max ? text : text.Substring(0, max);
        }

        // ---- failure path (game thread) --------------------------------------

        private static void RecordFailure(string reason, int nowMs, List<string> pending, ref int emitted)
        {
            lock (m_Lock)
            {
                s_State.RequestsFailed++;
                s_State.ConsecutiveFailures++;
                s_State.LastFailureReason = reason;
                if (s_State.ConsecutiveFailures >= MaxConsecutiveFailures)
                {
                    s_State.BackoffUntilMs = unchecked(nowMs + CooldownAfterFailureMs);
                }
                pending.Add("OllamaAdviceFailed reason=" + reason);
                emitted = pending.Count;
            }
        }

        // ---- readbacks (bounded counters + status lines, house pattern) ----

        public static int GetPort() { lock (m_Lock) return s_State.Port; }
        public static int GetModelIndex() { lock (m_Lock) return s_State.ModelIndex; }
        public static bool GetEnabled() { lock (m_Lock) return s_State.Enabled; }
        public static long GetRequestsSent() { lock (m_Lock) return s_State.RequestsSent; }
        public static long GetRequestsFailed() { lock (m_Lock) return s_State.RequestsFailed; }
        public static long GetRequestsSucceeded() { lock (m_Lock) return s_State.RequestsSucceeded; }
        public static long GetRequestsTimeouts() { lock (m_Lock) return s_State.RequestsTimeouts; }
        public static long GetLatencyAverageMs()
        {
            lock (m_Lock)
            {
                return s_State.LatencySamples == 0 ? -1 : s_State.LatencySumMs / s_State.LatencySamples;
            }
        }
        public static long GetQueueDepth()
        {
            // Single-flight design: at most one request queued/running.
            lock (m_Lock) return s_State.InFlightRequestMs >= 0 ? 1 : 0;
        }
        public static long GetAdviceAccepted() { lock (m_Lock) return s_State.AdviceAccepted; }
        public static long GetAdviceRejected() { lock (m_Lock) return s_State.AdviceRejected; }
        public static long GetUncertainPasses() { lock (m_Lock) return s_State.UncertainPasses; }
        public static long GetBackoffBlocks() { lock (m_Lock) return s_State.BackoffBlocks; }
        public static string GetLastAdvice() { lock (m_Lock) return s_State.LastAdvice; }
        public static string GetLastFailureReason() { lock (m_Lock) return s_State.LastFailureReason; }

        // Testability readback: true when a worker has parked a response that
        // the next Evaluate pass will consume. Also true while a parked body
        // is null (transport fault parked as a completed empty response).
        internal static bool HasPendingResponse()
        {
            lock (m_Lock) return s_State.InFlightRequestMs >= 0 && s_State.PendingResponseSet;
        }

        public static List<string> Lines()
        {
            lock (m_Lock)
            {
                List<string> lines = new List<string>(4);
                lines.Add("sent=" + s_State.RequestsSent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " ok=" + s_State.RequestsSucceeded.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " failed=" + s_State.RequestsFailed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " accepted=" + s_State.AdviceAccepted.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " rejected=" + s_State.AdviceRejected.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("uncertain=" + s_State.UncertainPasses.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " backoffBlocks=" + s_State.BackoffBlocks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " lastFailure=" + (s_State.LastFailureReason.Length > 0 ? s_State.LastFailureReason : "-"));
                if (s_State.LastAdvice.Length > 0)
                    lines.Add("lastAdvice=" + s_State.LastAdvice);
                return lines;
            }
        }

        public static List<string> StatusLines()
        {
            lock (m_Lock)
            {
                long avgLatency = s_State.LatencySamples == 0
                    ? -1 : s_State.LatencySumMs / s_State.LatencySamples;
                List<string> lines = new List<string>(3);
                lines.Add("OllamaAdvisor: enabled=" + (s_State.Enabled ? "yes" : "no")
                    + " port=" + s_State.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " model=" + ModelName(s_State.ModelIndex)
                    + " sent=" + s_State.RequestsSent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " ok=" + s_State.RequestsSucceeded.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " failed=" + s_State.RequestsFailed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " timeouts=" + s_State.RequestsTimeouts.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " avgLatency=" + (avgLatency >= 0
                        ? avgLatency.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms" : "-")
                    + " queue=" + (s_State.InFlightRequestMs >= 0 ? "1" : "0")
                    + " accepted=" + s_State.AdviceAccepted.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " rejected=" + s_State.AdviceRejected.ToString(System.Globalization.CultureInfo.InvariantCulture));
                lines.Add("OllamaAdvisor: uncertain=" + s_State.UncertainPasses.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " backoffBlocks=" + s_State.BackoffBlocks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " lastFailure=" + (s_State.LastFailureReason.Length > 0 ? s_State.LastFailureReason : "-"));
                return lines;
            }
        }

        // Test/dev isolation only. Never call in game code. Also frees any
        // parked worker state and the single-flight gate.
        public static void ResetForTests()
        {
            lock (m_Lock)
            {
                s_State.LastEvalMs = 0;
                s_State.Port = PortDefault;
                s_State.ModelIndex = ModelDefault;
                s_State.Enabled = false;
                s_State.InFlightRequestMs = -1;
                s_State.PendingResponse = null;
                s_State.PendingResponseMs = 0;
                s_State.PendingResponseSet = false;
                s_State.RequestsSent = 0;
                s_State.RequestsFailed = 0;
                s_State.RequestsSucceeded = 0;
                s_State.RequestsTimeouts = 0;
                s_State.LatencySumMs = 0;
                s_State.LatencySamples = 0;
                s_State.AdviceAccepted = 0;
                s_State.AdviceRejected = 0;
                s_State.UncertainPasses = 0;
                s_State.BackoffBlocks = 0;
                s_State.ConsecutiveFailures = 0;
                s_State.BackoffUntilMs = -1;
                s_State.LastAdviceMs = -1;
                s_State.LastAdvice = string.Empty;
                s_State.LastFailureReason = string.Empty;
                m_AuthorityProbe = null;
                m_NowMsProvider = null;
                m_WorldProvider = null;
                m_DecisionListener = null;
                m_Transport = null;
                m_ModelProbe = null;
                m_ModelAvailableKnown = false;
                m_ModelAvailable = false;
                m_ModelProbeError = string.Empty;
                m_ChatSelfTest = null;
                m_SelfTestDone = false;
                m_SelfTestError = string.Empty;
            }
            s_WorkerRunning = 0;
        }
    }
}