using System.Collections.Generic;
using PulsarModLoader.Chat.Commands.CommandRouter;
using CapBot.Core.Logging;
using CapBot.Core.Ollama;

namespace CapBot
{
    // /capbotollama — read-only Ollama connection diagnostics (P51, owner
    // mandate). Echoes the model-probe truth (availability + exact probe
    // error), the startup /api/chat self-test result, and the advisor
    // counters (requests / success / failure / timeout / average latency /
    // queue depth / last error). Read-only: no request is dispatched, no
    // state mutated — everything shown is already-recorded evidence.
    internal class OllamaCommand : ChatCommand
    {
        public override string[] CommandAliases() => new string[] { "capbotollama" };

        public override string Description() => "CapBot: Ollama/Qwen3 connection diagnostics";

        public override string[] UsageExamples() => new string[] { "/capbotollama" };

        public override void Execute(string arguments)
        {
            if (PLNetworkManager.Instance == null || PLNetworkManager.Instance.LocalPlayer == null)
            {
                CapBotLog.Warning(CapBotLog.CORE, "/capbotollama ignored: no local player yet");
                return;
            }
            if (!PhotonNetwork.isMasterClient)
            {
                PulsarModLoader.Utilities.Messaging.Notification("Must be host to see CapBot Ollama status!");
                return;
            }
            try
            {
                List<string> lines = new List<string>();
                // Connection truth: the P44 probe result (availability) and
                // the P51 self-test result. Nothing is re-probed here.
                foreach (string diag in OllamaAdvisor.ModelDiagnosticLines())
                    lines.Add(diag);
                foreach (string selfTest in OllamaAdvisor.SelfTestDiagnosticLines())
                    lines.Add(selfTest);
                lines.Add("OllamaEndpoint=127.0.0.1:" + OllamaAdvisor.GetPort().ToString());
                lines.Add("OllamaApi=" + (OllamaAdvisor.SelfTestDone
                    ? (OllamaAdvisor.SelfTestError.Length == 0 ? "READY" : "ERROR") : "NOT-TESTED"));
                lines.Add("OllamaRequests=" + OllamaAdvisor.GetRequestsSent().ToString()
                    + " Successful=" + OllamaAdvisor.GetRequestsSucceeded().ToString()
                    + " Failed=" + OllamaAdvisor.GetRequestsFailed().ToString()
                    + " Timeouts=" + OllamaAdvisor.GetRequestsTimeouts().ToString());
                long avg = OllamaAdvisor.GetLatencyAverageMs();
                lines.Add("OllamaAvgLatency=" + (avg >= 0 ? avg.ToString() + "ms" : "-")
                    + " Queue=" + OllamaAdvisor.GetQueueDepth().ToString());
                string lastFailure = OllamaAdvisor.GetLastFailureReason();
                lines.Add("OllamaLastError=" + (string.IsNullOrEmpty(lastFailure) ? "-" : lastFailure));
                lines.Add("OllamaAdvice=" + (OllamaAdvisor.GetLastAdvice().Length > 0
                    ? OllamaAdvisor.GetLastAdvice() : "-"));
                for (int i = 0; i < lines.Count; i++)
                {
                    PulsarModLoader.Utilities.Messaging.Echo(
                        PLNetworkManager.Instance.LocalPlayer.GetPhotonPlayer(), lines[i]);
                }
            }
            catch (System.Exception ex)
            {
                CapBotLog.Error(CapBotLog.CORE, "/capbotollama failed", ex);
            }
        }
    }
}