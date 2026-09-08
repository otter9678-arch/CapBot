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
    }
}