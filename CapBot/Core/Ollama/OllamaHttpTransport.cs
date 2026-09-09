using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CapBot.Core.Ollama
{
    // ---- Phase 20: production Ollama HTTP transport --------------------------
    //
    // The ONLY network code in the advisor stack, and deliberately the
    // inverse of the audit's C1 finding (ModUpdater): the endpoint is
    // HARD-ANCHORED to loopback 127.0.0.1 — no host string, no URL, no DNS
    // name is ever accepted from config or anywhere else. The only knob is
    // the port (validated 1..65535, default 11434).
    //
    // HttpClient is constructed once (per-adapter reuse; net472 best
    // practice — one client per lifetime avoids socket exhaustion). Hard
    // timeout on every request. Runs exclusively on advisor worker threads
    // (never the Unity main thread; never touches game APIs).
    //
    // Runtime-verified shape (Ollama 0.33.3, probed 2026-09-08): POST
    // http://127.0.0.1:<port>/api/chat with a JSON body returns a single
    // JSON object (stream=false) whose message.content carries the reply.
    public sealed class OllamaHttpTransport : OllamaAdvisor.ITransport
    {
        public const string LoopbackHost = "127.0.0.1";
        public const string ChatPath = "/api/chat";

        private readonly HttpClient m_Client;
        private readonly int m_Port;

        public OllamaHttpTransport(int port)
        {
            m_Port = OllamaAdvisor.ClampPort(port);
            // Handler limits: loopback only, bounded connection pool.
            HttpClientHandler handler = new HttpClientHandler();
            handler.UseProxy = false;
            handler.AllowAutoRedirect = false;
            m_Client = new HttpClient(handler);
            m_Client.Timeout = TimeSpan.FromMilliseconds(OllamaAdvisor.RequestTimeoutMs);
            // No default headers beyond content-type; every request sets its own.
        }

        // Synchronous loopback call (advisor worker thread only). Returns
        // the response body, or null on ANY failure (transport never throws
        // across the seam — the advisor treats null as a soft failure).
        public string PostChatJson(string requestJson, int timeoutMs)
        {
            try
            {
                if (string.IsNullOrEmpty(requestJson)) return null;
                if (timeoutMs <= 0) timeoutMs = OllamaAdvisor.RequestTimeoutMs;

                Uri uri = new Uri("http://" + LoopbackHost + ":" + m_Port.ToString(CultureInfo.InvariantCulture) + ChatPath);
                using (StringContent content = new StringContent(
                    requestJson, Encoding.UTF8, "application/json"))
                {
                    // Per-call timeout: the linked cancellation token keeps a
                    // slow server from outliving the budget even though
                    // HttpClient.Timeout also applies.
                    using (CancellationTokenSource cts = new CancellationTokenSource(timeoutMs))
                    {
                        Task<HttpResponseMessage> sendTask =
                            m_Client.PostAsync(uri, content, cts.Token);
                        sendTask.Wait(cts.Token);
                        using (HttpResponseMessage response = sendTask.Result)
                        {
                            if (!response.IsSuccessStatusCode) return null;
                            Task<string> readTask = response.Content.ReadAsStringAsync();
                            readTask.Wait(cts.Token);
                            return readTask.Result;
                        }
                    }
                }
            }
            catch (Exception)
            {
                return null; // timeout/refused/aborted = soft failure
            }
        }

        // ---- P51: harmless /api/chat startup self-test (owner mandate) -------
        //
        // One-shot POST /api/chat with a pure-echo prompt against the
        // REQUIRED model — no game state, no gameplay action. Reuses this
        // transport's single HttpClient (no second client, no competing
        // transport). Returns the raw response body (null on failure); the
        // advisor classifies + records the result. Hard 90 s budget covers a
        // cold qwen3 model load (measured ~40 s). Worker/startup thread only.
        public string PostChatSelfTest(int port, string modelName)
        {
            string safeModel = string.IsNullOrEmpty(modelName) ? OllamaAdvisor.RequiredModel : modelName;
            string requestJson = "{\"model\":\"" + EscapeJson(safeModel) + "\""
                + ",\"messages\":[{\"role\":\"system\",\"content\":\"Health check. Reply with exactly: OK\"}"
                + ",{\"role\":\"user\",\"content\":\"Reply with exactly: OK\"}]"
                + ",\"stream\":false,\"keep_alive\":\"30m\""
                + (OllamaAdvisor.IsRequestingModelThinking(safeModel) ? ",\"think\":false" : "")
                + ",\"options\":{\"num_predict\":8,\"temperature\":0.0}}";
            try
            {
                Uri uri = new Uri("http://" + LoopbackHost + ":"
                    + OllamaAdvisor.ClampPort(port).ToString(CultureInfo.InvariantCulture) + ChatPath);
                using (CancellationTokenSource cts = new CancellationTokenSource(OllamaAdvisor.RequestTimeoutMs))
                {
                    Task<HttpResponseMessage> sendTask = m_Client.PostAsync(
                        uri, new StringContent(requestJson, Encoding.UTF8, "application/json"), cts.Token);
                    sendTask.Wait(cts.Token);
                    using (HttpResponseMessage response = sendTask.Result)
                    {
                        if (!response.IsSuccessStatusCode) return null;
                        Task<string> readTask = response.Content.ReadAsStringAsync();
                        readTask.Wait(cts.Token);
                        return readTask.Result;
                    }
                }
            }
            catch (Exception)
            {
                return null; // timeout/refused/aborted = soft failure
            }
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
                    default:
                        if (char.IsControl(c)) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ---- P44: one-shot model availability probe (owner mandate) ----------
        //
        // GET /api/tags (bounded loopback call) — null when the model is
        // listed, else the EXACT error text. Never substitutes a model; the
        // caller reports the error verbatim. Worker-thread or startup use
        // only (hard 5 s timeout — never blocks the game thread).
        public string ProbeModelAvailable(string modelName)
        {
            try
            {
                if (string.IsNullOrEmpty(modelName)) return "model name empty";
                Uri uri = new Uri("http://" + LoopbackHost + ":" + m_Port.ToString(CultureInfo.InvariantCulture) + "/api/tags");
                using (CancellationTokenSource cts = new CancellationTokenSource(5000))
                {
                    Task<HttpResponseMessage> sendTask = m_Client.GetAsync(uri, cts.Token);
                    sendTask.Wait(cts.Token);
                    using (HttpResponseMessage response = sendTask.Result)
                    {
                        if (!response.IsSuccessStatusCode)
                            return "HTTP " + (int)response.StatusCode + " from /api/tags";
                        Task<string> readTask = response.Content.ReadAsStringAsync();
                        readTask.Wait(cts.Token);
                        string body = readTask.Result;
                        if (!string.IsNullOrEmpty(body) && body.IndexOf("\"name\":\"" + modelName + "\"", StringComparison.Ordinal) >= 0)
                            return null;
                        return "model '" + modelName + "' not in local Ollama library (/api/tags)";
                    }
                }
            }
            catch (Exception ex)
            {
                // Exact error per the mandate: type + innermost message.
                Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                return ex.GetType().Name + ": " + inner.Message;
            }
        }
    }
}