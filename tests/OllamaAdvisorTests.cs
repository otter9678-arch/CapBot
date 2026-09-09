// Phase 20: OllamaAdvisor domain tests — built incrementally, see bottom.
using System;
using System.Collections.Generic;
using System.Threading;
using CapBot.Core.World;
using CapBot.Core.Ollama;

namespace CapBot.TaskTests
{
    internal static class OllamaAdvisorTests
    {
        // ANCHOR: BODY

        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        // ---- harness ---------------------------------------------------------

        private sealed class VirtualClock { public int NowMs; }
        private static VirtualClock s_Clock;
        private static WorldSnapshot s_Snap;
        private static readonly List<string> s_Lines = new List<string>();
        private static FakeTransport s_Transport;

        // Fake transport: records the last request, returns a scripted body
        // (or null = transport fault). Optional signal for worker-completion
        // synchronization. No real network anywhere in the suite.
        // internal (Phase 21): CrewAdvisorTests reuses it unchanged.
        internal sealed class FakeTransport : OllamaAdvisor.ITransport
        {
            public string LastRequestJson;
            public int CallCount;
            public string ResponseBody = "{}";
            public int DelayMs;
            public bool UseRealThread = true; // false = run inline (deterministic)

            public string PostChatJson(string requestJson, int timeoutMs)
            {
                LastRequestJson = requestJson;
                // Interlocked (release): WaitForCall's Volatile.Read must also
                // observe LastRequestJson, not just the count.
                Interlocked.Increment(ref CallCount);
                if (DelayMs > 0) Thread.Sleep(DelayMs);
                return ResponseBody;
            }
        }

        private static void FreshSetup()
        {
            OllamaAdvisor.ResetForTests();
            s_Clock = new VirtualClock { NowMs = 400000 };
            s_Snap = FreshCalm(400000);
            s_Lines.Clear();
            s_Transport = new FakeTransport();
            OllamaAdvisor.SetAuthorityProbe(delegate { return true; });
            OllamaAdvisor.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            OllamaAdvisor.SetWorldProvider(delegate { return s_Snap; });
            OllamaAdvisor.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            OllamaAdvisor.SetTransport(s_Transport);
            OllamaAdvisor.ApplyConfig(true, 11434, 0);
        }

        private static void Advance(int ms) { s_Clock.NowMs += ms; }

        private static void Eval()
        {
            s_Lines.Clear();
            OllamaAdvisor.Evaluate(s_Clock.NowMs);
        }

        // Waits until the worker thread has parked its response (bounded).
        private static bool WaitForPark(int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                // Poll the advisor's park readback (not CallCount — the call
                // returns before the worker parks).
                if (OllamaAdvisor.HasPendingResponse()) return true;
                Thread.Sleep(10);
            }
            return OllamaAdvisor.HasPendingResponse();
        }

        // Waits until the worker thread has entered the transport call the
        // expected number of times (bounded). Use where the test must observe
        // an IN-FLIGHT request: the worker is inside PostChatJson (call
        // counted, response not yet parked), so the single-flight gate and
        // InFlightRequestMs are still held — a subsequent Eval must not
        // dispatch or consume. WaitForPark here would consume the slot and
        // break the in-flight semantics under test.
        private static bool WaitForCall(int expectedCount, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (Volatile.Read(ref s_Transport.CallCount) >= expectedCount) return true;
                Thread.Sleep(5);
            }
            return Volatile.Read(ref s_Transport.CallCount) >= expectedCount;
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // ---- snapshot builders -------------------------------------------------

        private static ShipSnapshot PlayerShip()
        {
            return new ShipSnapshot(1, "player", true, 0, false, 1f, 1f, false, 0, -1, 0, 10f, false);
        }

        private static CrewMemberSnapshot Crew(int id, string name, bool isCaptain, string tli)
        {
            return new CrewMemberSnapshot(id, name, true, isCaptain ? 0 : 1, 0, true, true, 1f, tli, isCaptain, -1);
        }

        private static WorldSnapshot FreshCalm(int timeMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(PlayerShip());
            List<CrewMemberSnapshot> crew = new List<CrewMemberSnapshot>();
            crew.Add(Crew(1, "captain-bot", true, "Bridge"));
            crew.Add(Crew(2, "bot1", false, "Bridge"));
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }

        private static WorldSnapshot StaleSnap(int oldMs)
        {
            return FreshCalm(oldMs); // SnapshotTimeMs far behind s_Clock.NowMs
        }

        private static WorldSnapshot NotStartedSnap(int timeMs)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(PlayerShip());
            return new WorldSnapshot(
                timeMs, false, true, -1, WorldAuthority.Unknown,
                ships, new List<CrewMemberSnapshot>(), new List<MissionSnapshot>(),
                null, null, null, new List<WorldObjectSnapshot>(),
                WorldAuthority.Unknown, WorldAuthority.Unknown, WorldAuthority.Unknown, WorldAuthority.Unknown,
                -1, float.NaN);
        }

        // ---- Run ---------------------------------------------------------------

        internal static int Run()
        {
            // ---- OA01: inert by construction (config off / transport unset) ----
            FreshSetup();
            OllamaAdvisor.ApplyConfig(false, 11434, 0); // config OFF (the default)
            Eval();
            Check(s_Transport.CallCount == 0, "OA01a disabled config = no request");
            Check(OllamaAdvisor.GetRequestsSent() == 0, "OA01a nothing sent");
            FreshSetup();
            OllamaAdvisor.SetTransport(null); // enabled config but no transport
            Eval();
            Check(OllamaAdvisor.GetRequestsSent() == 0, "OA01b unset transport = inert");
            FreshSetup();
            OllamaAdvisor.SetAuthorityProbe(null);
            Eval();
            Check(OllamaAdvisor.GetRequestsSent() == 0, "OA01c null authority probe = deny-by-default");
            FreshSetup();
            OllamaAdvisor.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
            Eval();
            Check(OllamaAdvisor.GetRequestsSent() == 0, "OA01d faulting probe = fail-closed no-op");

            // ---- OA02: enabled + calm snapshot dispatches one request -----------
            FreshSetup();
            Eval();
            Check(WaitForCall(1, 2000), "OA02 worker entered transport call");
            Check(s_Transport.CallCount == 1, "OA02a single request dispatched");
            Check(OllamaAdvisor.GetRequestsSent() == 1, "OA02a request counted");
            Check(s_Transport.LastRequestJson != null
                && s_Transport.LastRequestJson.IndexOf("http", StringComparison.Ordinal) < 0,
                "OA02b request body carries no URL (host is the transport's decision)");
            Check(s_Transport.LastRequestJson.IndexOf("\"stream\":false", StringComparison.Ordinal) >= 0,
                "OA02b non-streaming request");
            Check(s_Transport.LastRequestJson.IndexOf("\"model\":\"qwen3:latest\"", StringComparison.Ordinal) >= 0,
                "OA02c default model in request (P44 owner mandate: qwen3:latest)");
            // Second eval inside the same cadence window: no second request.
            Eval();
            Check(s_Transport.CallCount == 1, "OA02d cadence gate blocks same-window re-dispatch");

            // ---- OA03: single-flight gate ---------------------------------------
            FreshSetup();
            s_Transport.DelayMs = 250; // worker holds the slot briefly
            Eval(); // dispatches worker
            Check(WaitForCall(1, 2000), "OA03a worker entered transport");
            Check(s_Transport.CallCount == 1, "OA03a first dispatch in flight");
            Advance(OllamaAdvisor.MinRecheckMs); // open cadence window
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // must NOT dispatch a second concurrent request
            Check(s_Transport.CallCount == 1, "OA03b single-flight: no concurrent dispatch");
            Check(WaitForPark(2000), "OA03c worker parked within timeout");
            Advance(OllamaAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // consumes response; slot freed — the same eval dispatches
                    // the next cadence request (consume runs before the gates)
            Check(WaitForCall(2, 2000), "OA03d second dispatch reached transport");
            Check(s_Transport.CallCount == 2, "OA03d slot freed after consume");

            // ---- OA04: successful advice consumed + logged -----------------------
            FreshSetup();
            s_Transport.ResponseBody = "{\"model\":\"qwen2.5:latest\",\"message\":{\"role\":\"assistant\",\"content\":\"ADVICE: gather crew to bridge (divergence 1 bot, calm ship)\"},\"done\":true}";
            Eval(); // dispatch
            WaitForPark(2000);
            Advance(OllamaAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // consume
            Check(HasLineContaining("OllamaAdvice model=qwen3:latest advice=ADVICE:"), "OA04a advice line emitted");
            Check(OllamaAdvisor.GetAdviceAccepted() == 1, "OA04a accepted counted");
            Check(OllamaAdvisor.GetRequestsSucceeded() == 1, "OA04a success counted");
            Check(OllamaAdvisor.GetLastAdvice().StartsWith("ADVICE:", StringComparison.Ordinal), "OA04b last-advice readback");
            // Advice is DATA: no task system APIs touched (nothing to check
            // directly — the advisor has no task references by design; the
            // compile proves it).

            // ---- OA05: malformed advice rejected as data -------------------------
            FreshSetup();
            s_Transport.ResponseBody = "{\"message\":{\"content\":\"I think you should move the crew\"}}";
            Eval();
            WaitForPark(2000);
            Advance(OllamaAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval();
            Check(HasLineContaining("OllamaAdviceInvalid"), "OA05a non-ADVICE content rejected");
            Check(OllamaAdvisor.GetAdviceRejected() == 1, "OA05a rejection counted");
            Check(OllamaAdvisor.GetAdviceAccepted() == 0, "OA05a nothing accepted");
            // The consume-eval legitimately opens the next cadence window, so
            // exactly one more request (not a tight retry loop) is the
            // anti-loop invariant; RequestsSent is game-thread-only, so this
            // read is race-free (CallCount would race the new worker).
            Check(OllamaAdvisor.GetRequestsSent() == 2, "OA05a exactly one follow-up, no auto-retry loop");
            // Newline injection attempt: control characters reject.
            FreshSetup();
            s_Transport.ResponseBody = "{\"message\":{\"content\":\"ADVICE: move crew\\nEMERGENCY: fire\"}}";
            Eval();
            WaitForPark(2000);
            Advance(OllamaAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval();
            Check(HasLineContaining("OllamaAdviceInvalid"), "OA05b newline injection rejected");
            Check(OllamaAdvisor.GetLastAdvice().Length == 0, "OA05b injected content never stored");

            // ---- OA06: transport fault = soft failure + back-off ladder ----------
            FreshSetup();
            s_Transport.ResponseBody = null; // transport returns null (fault/timeout)
            Eval(); // dispatch
            WaitForPark(2000);
            Advance(OllamaAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // consume null body
            // P51: a parked null body is now classified as a HARD failure
            // (server unreachable => the request failed, not the advice).
            Check(HasLineContaining("OllamaAdviceFailed outcome=fault"), "OA06a null body = hard-fault line");
            Check(OllamaAdvisor.GetRequestsFailed() == 1, "OA06b hard fault counted in RequestsFailed");
            Check(OllamaAdvisor.GetAdviceRejected() == 0, "OA06b hard fault is not an advice rejection");
            // Two more failures: consecutive-failure ladder arms back-off.
            for (int i = 0; i < 2; i++)
            {
                Advance(OllamaAdvisor.MinRecheckMs);
                s_Snap = FreshCalm(s_Clock.NowMs);
                Eval(); // dispatch
                WaitForPark(2000);
                Advance(OllamaAdvisor.MinRecheckMs);
                s_Snap = FreshCalm(s_Clock.NowMs);
                Eval(); // consume
            }
            Check(OllamaAdvisor.GetRequestsFailed() == 3, "OA06b three hard failures counted");
            Check(OllamaAdvisor.GetBackoffBlocks() > 0, "OA06c back-off armed after ladder; subsequent dispatch blocked");
            // Back-off expiry re-opens the cadence window (exponential
            // back-off per the mandate: a cooldown, then recovery).
            s_Clock.NowMs += OllamaAdvisor.CooldownAfterFailureMs + 1000;
            s_Snap = FreshCalm(s_Clock.NowMs);
            int sentBeforeExpiry = (int)OllamaAdvisor.GetRequestsSent();
            Eval();
            WaitForPark(2000);
            Check((int)OllamaAdvisor.GetRequestsSent() == sentBeforeExpiry + 1,
                "OA06d back-off expiry re-allows dispatch");

            // ---- OA07: snapshot fail-safe (fail-open, no dispatch) ---------------
            FreshSetup();
            s_Snap = null;
            Eval();
            Check(s_Transport.CallCount == 0, "OA07a null snapshot = no dispatch");
            Check(OllamaAdvisor.GetUncertainPasses() == 1, "OA07a uncertain counted");
            s_Snap = StaleSnap(s_Clock.NowMs - OllamaAdvisor.MaxStaleSnapshotMs - 1000);
            Advance(OllamaAdvisor.MinRecheckMs);
            Eval();
            Check(s_Transport.CallCount == 0, "OA07b stale snapshot = no dispatch");
            s_Snap = NotStartedSnap(s_Clock.NowMs);
            Advance(OllamaAdvisor.MinRecheckMs);
            Eval();
            Check(s_Transport.CallCount == 0, "OA07c not-started snapshot = no dispatch");
            Check(OllamaAdvisor.GetRequestsSent() == 0, "OA07d zero requests through fail-safe");

            // ---- OA08: request prompt is bounded and well-formed -----------------
            FreshSetup();
            Eval();
            WaitForPark(2000);
            string req = s_Transport.LastRequestJson;
            Check(req != null && req.Length <= OllamaAdvisor.MaxPromptLen + 512, "OA08a bounded request size");
            Check(req.IndexOf("\"messages\":[{\"role\":\"system\"", StringComparison.Ordinal) >= 0, "OA08b system role first");
            Check(req.IndexOf("ADVICE:", StringComparison.OrdinalIgnoreCase) >= 0, "OA08c format instruction present");
            Check(req.IndexOf("keep_alive", StringComparison.Ordinal) >= 0, "OA08d keep_alive requested");
            // Crew divergence appears in the prompt (bot away from captain).
            Check(req.IndexOf("botsAwayFromCaptain=0", StringComparison.Ordinal) >= 0, "OA08e calm crew observations");

            // ---- OA09: divergent crew picture reaches the prompt -----------------
            FreshSetup();
            List<CrewMemberSnapshot> crew2 = new List<CrewMemberSnapshot>();
            crew2.Add(Crew(1, "captain-bot", true, "Bridge"));
            crew2.Add(Crew(2, "bot1", false, "Engineering"));
            s_Snap = BuildSnap(s_Clock.NowMs, crew2);
            Eval();
            WaitForPark(2000);
            Check(s_Transport.LastRequestJson.IndexOf("botsAwayFromCaptain=1", StringComparison.Ordinal) >= 0,
                "OA09 divergence counted in prompt");
            // Unknown crew data degrades to unknownCrewData, never a crash.
            FreshSetup();
            List<CrewMemberSnapshot> crew3 = new List<CrewMemberSnapshot>();
            crew3.Add(Crew(1, "captain-bot", true, "Bridge"));
            crew3.Add(new CrewMemberSnapshot(3, "bot3", true, 1, 0, false, false, float.NaN, null, false, -1));
            s_Snap = BuildSnap(s_Clock.NowMs, crew3);
            Eval();
            WaitForPark(2000);
            Check(s_Transport.LastRequestJson.IndexOf("unknownCrewData=1", StringComparison.Ordinal) >= 0,
                "OA09b unknown liveness degraded to unknown field");

            // ---- OA10: JSON extraction (shapes verified against 0.33.3) ----------
            string real = "{\"model\":\"qwen2.5:latest\",\"created_at\":\"2026-09-08T05:18:35.3191012Z\",\"message\":{\"role\":\"assistant\",\"content\":\"ADVICE: Issue move order to sector 6 (explore, maintain crew morale).\"},\"done\":true,\"done_reason\":\"stop\"}";
            Check(OllamaAdvisor.ExtractContent(real) == "ADVICE: Issue move order to sector 6 (explore, maintain crew morale).",
                "OA10a real response shape extracts content");
            string escaped = "{\"message\":{\"content\":\"ADVICE: hold (\\\"fuel\\\" low\\nwatch)\"}}";
            Check(OllamaAdvisor.ExtractContent(escaped) == "ADVICE: hold (\"fuel\" low\nwatch)",
                "OA10b escapes decoded");
            Check(OllamaAdvisor.ExtractContent(null) == null, "OA10c null json = null");
            Check(OllamaAdvisor.ExtractContent("") == null, "OA10c empty json = null");
            Check(OllamaAdvisor.ExtractContent("{\"message\":{\"role\":\"assistant\"}}") == null, "OA10d no content = null");
            Check(OllamaAdvisor.ExtractContent("not json at all") == null, "OA10e garbage = null");
            // A content key inside the PROMPT must not be mistaken for the
            // message's content (depth-1 message search).
            string tricky = "{\"message\":{\"content\":\"x\"},\"options\":{\"num_predict\":48}}";
            Check(OllamaAdvisor.ExtractContent(tricky) == "x", "OA10f sibling keys ignored");

            // ---- OA11: advice validation bounds ----------------------------------
            string ok;
            Check(OllamaAdvisor.ValidateAdvice("ADVICE: gather crew (calm)", out ok) && ok == "ADVICE: gather crew (calm)",
                "OA11a well-formed accepted");
            Check(OllamaAdvisor.ValidateAdvice("advice: gather crew", out ok), "OA11b case-insensitive prefix");
            Check(!OllamaAdvisor.ValidateAdvice("ADVI", out ok), "OA11c too short rejected");
            Check(!OllamaAdvisor.ValidateAdvice(null, out ok), "OA11d null rejected");
            Check(!OllamaAdvisor.ValidateAdvice("Sure thing: move", out ok), "OA11e wrong prefix rejected");
            string longAdvice = "ADVICE: " + new string('x', 500);
            Check(OllamaAdvisor.ValidateAdvice(longAdvice, out ok) && ok.Length == OllamaAdvisor.MaxAdviceLen,
                "OA11f overlong advice truncated to bound");
            Check(!OllamaAdvisor.ValidateAdvice("ADVICE: ok\nsecond line", out ok), "OA11g control char rejected");

            // ---- OA12: config clamps ---------------------------------------------
            Check(OllamaAdvisor.ClampPort(0) == OllamaAdvisor.PortDefault, "OA12a port 0 clamps to default");
            Check(OllamaAdvisor.ClampPort(-5) == OllamaAdvisor.PortDefault, "OA12b negative port clamps");
            Check(OllamaAdvisor.ClampPort(70000) == OllamaAdvisor.PortDefault, "OA12c port >65535 clamps");
            Check(OllamaAdvisor.ClampPort(11434) == 11434, "OA12d valid port preserved");
            Check(OllamaAdvisor.ClampModelIndex(-1) == 0, "OA12e negative model index clamps");
            Check(OllamaAdvisor.ClampModelIndex(99) == 0, "OA12f out-of-range index clamps");
            Check(OllamaAdvisor.ClampModelIndex(1) == 1, "OA12g valid index preserved");
            Check(OllamaAdvisor.KnownModels.Length == 4, "OA12h bounded model vocabulary");
            // ApplyConfig validation.
            FreshSetup();
            OllamaAdvisor.ApplyConfig(true, 99999, 42);
            Check(OllamaAdvisor.GetPort() == OllamaAdvisor.PortDefault, "OA12i bad port through ApplyConfig clamps");
            Check(OllamaAdvisor.GetModelIndex() == 0, "OA12j bad model index clamps");
            OllamaAdvisor.ApplyConfig(true, 8080, 1);
            Check(OllamaAdvisor.GetPort() == 8080 && OllamaAdvisor.GetModelIndex() == 1, "OA12k valid config applied");

            // ---- OA14: thinking-model request shape (P36 qwen3 live finding) -----
            FreshSetup();
            // P44: qwen3:latest is the DEFAULT (index 0); the vocabulary keeps
            // four entries with qwen3 first (owner mandate: no substitution).
            Check(OllamaAdvisor.KnownModels.Length == 4, "OA14a model vocabulary bounded");
            Check(OllamaAdvisor.KnownModels[0] == "qwen3:latest", "OA14i default model is qwen3:latest (owner mandate)");
            Check(OllamaAdvisor.ModelName(0) == "qwen3:latest", "OA14j index 0 resolves qwen3");
            Check(OllamaAdvisor.ModelName(1) == "qwen2.5:latest", "OA14k legacy model at index 1");
            // Request for a thinking model carries "think":false; legacy models do not.
            string q3 = OllamaAdvisor.BuildRequestJson(FreshCalm(1000), 0, 1000);
            Check(q3 != null && q3.IndexOf("\"think\":false", StringComparison.Ordinal) >= 0,
                "OA14d default (qwen3) request disables thinking");
            string legacy = OllamaAdvisor.BuildRequestJson(FreshCalm(1000), 1, 1000);
            Check(legacy != null && legacy.IndexOf("\"think\"", StringComparison.Ordinal) < 0,
                "OA14e legacy request carries no think flag");
            // Non-requesting accessor stays consistent for the sibling advisor.
            Check(OllamaAdvisor.IsRequestingModelThinking("qwen3:latest"), "OA14f thinking probe true for qwen3");
            Check(!OllamaAdvisor.IsRequestingModelThinking("qwen2.5:latest"), "OA14g thinking probe false for legacy");
            Check(!OllamaAdvisor.IsRequestingModelThinking(null), "OA14h thinking probe null-safe");

            // ---- OA15: P44 model identity probe (owner mandate) -------------------
            FreshSetup();
            Check(OllamaAdvisor.RequiredModel == "qwen3:latest", "OA15a required model is qwen3:latest");
            // Unwired probe: reported honestly as unknown.
            bool avail = OllamaAdvisor.RunModelProbe();
            Check(!avail && !OllamaAdvisor.ModelAvailabilityKnown, "OA15b unwired probe = unknown, not fabricated");
            // Available model -> true + mandated diagnostic lines.
            OllamaAdvisor.SetModelProbe(delegate (string m) { return m == "qwen3:latest" ? null : "wrong model probed"; });
            Check(OllamaAdvisor.RunModelProbe(), "OA15c available model reports true");
            List<string> diag = OllamaAdvisor.ModelDiagnosticLines();
            Check(diag.Count >= 3
                && diag[0] == "OllamaConfiguredModel=qwen3:latest"
                && diag[1] == "OllamaRequestModel=qwen3:latest"
                && diag[2] == "OllamaModelAvailable=true",
                "OA15d mandated diagnostic lines (available)");
            // Unavailable model -> false + EXACT error text surfaced.
            OllamaAdvisor.SetModelProbe(delegate (string m) { return "model 'qwen3:latest' not in local Ollama library (/api/tags)"; });
            Check(!OllamaAdvisor.RunModelProbe(), "OA15e unavailable model reports false");
            diag = OllamaAdvisor.ModelDiagnosticLines();
            Check(diag.Count == 4 && diag[2] == "OllamaModelAvailable=false"
                && diag[3].IndexOf("OllamaModelProbeError=", StringComparison.Ordinal) == 0
                && diag[3].IndexOf("not in local Ollama library", StringComparison.Ordinal) > 0,
                "OA15f exact probe error surfaced");
            // No automatic substitution: the probe never changes ModelIndex.
            Check(OllamaAdvisor.GetModelIndex() == 0, "OA15g probe never substitutes the model");

            // ---- OA13: readbacks + reset determinism ------------------------------
            FreshSetup();
            List<string> lines = OllamaAdvisor.StatusLines();
            Check(lines.Count == 2, "OA13a two status lines");
            Check(lines[0].IndexOf("OllamaAdvisor: enabled=yes port=11434 model=qwen3:latest", StringComparison.Ordinal) == 0,
                "OA13b status format");
            Eval();
            WaitForPark(2000);
            Advance(OllamaAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval();
            List<string> detail = OllamaAdvisor.Lines();
            Check(detail.Count >= 2, "OA13c detail lines present");
            OllamaAdvisor.ResetForTests();
            Check(OllamaAdvisor.GetRequestsSent() == 0 && OllamaAdvisor.GetAdviceAccepted() == 0, "OA13d reset clears counters");
            Check(OllamaAdvisor.GetLastAdvice().Length == 0, "OA13e reset clears advice");
            Eval(); // seams nulled → deny-by-default, no throw
            Check(OllamaAdvisor.GetRequestsSent() == 0, "OA13f reset nulls seams (inert)");

            // ---- OA16: P51 counters + self-test seam (owner mandate) -------------
            // Counters: latency + timeout classification.
            FreshSetup();
            OllamaAdvisor.TimeoutGraceMs = 900; // shrink the effective timeout for the test
            try
            {
                s_Transport.ResponseBody = null;
                s_Transport.DelayMs = 120; // returns null at ~120ms: a FAULT (below timeout threshold)
                Eval(); // dispatch
                WaitForPark(2000);
                Advance(OllamaAdvisor.MinRecheckMs);
                s_Snap = FreshCalm(s_Clock.NowMs);
                Eval(); // consume
                Check(OllamaAdvisor.GetRequestsTimeouts() == 0, "OA16a early null classified as fault, not timeout");
                Check(OllamaAdvisor.GetLatencyAverageMs() >= 0, "OA16b latency sampled on failure");
                long samples = OllamaAdvisor.GetLatencyAverageMs(); // ≥0 means samples exist
                // Timeout classification: a null body at/after the effective
                // budget (RequestTimeoutMs - grace) classifies as timeout.
                // The fake transport's DelayMs is used to cross the threshold:
                // RequestTimeoutMs(90000) - grace(900) = 89100 — not waitable.
                // Instead the grace seam makes a SHORT delay a timeout by
                // shrinking the effective budget via the same formula: set
                // grace so RequestTimeoutMs - grace ≈ 100ms.
                OllamaAdvisor.TimeoutGraceMs = RequestTimeoutMsOverride(); // effective budget ≈ 100ms
                s_Transport.DelayMs = 150; // 150ms > effective 100ms budget
                Eval(); // dispatch (fresh setup cleared gates? no — same session)
                WaitForPark(2000);
                Advance(OllamaAdvisor.MinRecheckMs);
                s_Snap = FreshCalm(s_Clock.NowMs);
                Eval(); // consume
                Check(OllamaAdvisor.GetRequestsTimeouts() >= 1, "OA16c late null classified as timeout");
            }
            finally
            {
                OllamaAdvisor.TimeoutGraceMs = 1000; // restore production grace
            }
            // Queue depth: 0 when idle. Drain the request the timeout
            // consume-Eval above dispatched (consume-before-gates re-dispatches
            // while the ladder is under 3): park -> advance -> consume. The
            // 3rd consecutive failure arms the back-off, so the drain Eval
            // dispatches nothing and the queue reads empty (expiry re-dispatch
            // is OA06d's job).
            WaitForPark(2000);
            Advance(OllamaAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval();
            Check(OllamaAdvisor.GetQueueDepth() == 0, "OA16d idle queue depth 0");
            // Self-test seam: unwired => honest not-wired failure.
            FreshSetup(); // also ResetForTests (clears self-test state)
            Check(!OllamaAdvisor.RunChatSelfTest(11434) && !OllamaAdvisor.SelfTestDone,
                "OA16e unwired self-test = not-wired, not fabricated");
            // Wired fake: body with message.content => pass.
            OllamaAdvisor.SetChatSelfTest(delegate(int port) { return "{\"message\":{\"content\":\"OK\"}}"; });
            Check(OllamaAdvisor.RunChatSelfTest(11434), "OA16f self-test pass on parseable body");
            List<string> stLines = OllamaAdvisor.SelfTestDiagnosticLines();
            Check(stLines.Count == 1 && stLines[0] == "OllamaSelfTest=pass", "OA16g self-test diagnostic line");
            // Wired fake: null body => fail + exact error line.
            OllamaAdvisor.ResetForTests();
            OllamaAdvisor.SetChatSelfTest(delegate(int port) { return null; });
            Check(!OllamaAdvisor.RunChatSelfTest(11434), "OA16h self-test fail on null body");
            stLines = OllamaAdvisor.SelfTestDiagnosticLines();
            Check(stLines.Count == 2 && stLines[0] == "OllamaSelfTest=fail"
                && stLines[1].IndexOf("OllamaSelfTestError=", StringComparison.Ordinal) == 0,
                "OA16i self-test error surfaced");
            // Wired fake: unparseable body (no content) => fail.
            OllamaAdvisor.ResetForTests();
            OllamaAdvisor.SetChatSelfTest(delegate(int port) { return "{\"done\":true}"; });
            Check(!OllamaAdvisor.RunChatSelfTest(11434), "OA16j self-test fail on body without content");

            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }

        // OA16 timeout-classification helper: RequestTimeoutMs is a const the
        // test cannot shrink, so the grace seam is set to
        // (RequestTimeoutMs - 100) making the effective budget ≈ 100 ms —
        // a fake transport DelayMs of 150 then classifies as timeout.
        private static int RequestTimeoutMsOverride()
        {
            return OllamaAdvisor.RequestTimeoutMs - 100;
        }

        // Snapshot builder with custom crew (ships/threats/nav/resources calm).
        private static WorldSnapshot BuildSnap(int timeMs, List<CrewMemberSnapshot> crew)
        {
            List<ShipSnapshot> ships = new List<ShipSnapshot>();
            ships.Add(PlayerShip());
            return new WorldSnapshot(
                timeMs, true, true, 7, WorldAuthority.MasterDerived,
                ships, crew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
        }
    }
}