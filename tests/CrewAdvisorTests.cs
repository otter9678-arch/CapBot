// Phase 21: CrewAdvisor domain tests — mirror the OllamaAdvisorTests (P20)
// conventions: deny-by-default seams, virtual clock, snapshot fail-safe,
// worker park/call synchronization, bounded counters, reset determinism.
using System;
using System.Collections.Generic;
using System.Threading;
using CapBot.Core.World;
using CapBot.Core.Crew;
using CapBot.Core.Ollama;
using CapBot.Core.Qwen;

namespace CapBot.TaskTests
{
    internal static class CrewAdvisorTests
    {
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
        private static OllamaAdvisorTests.FakeTransport s_Transport;

        // NOTE: reuses OllamaAdvisorTests.FakeTransport (same assembly,
        // internal). ResetForTests on BOTH advisors keeps the advisors from
        // sharing state (each holds its own DirectorState + seams).

        private static void FreshSetup()
        {
            CrewAdvisor.ResetForTests();
            OllamaAdvisor.ResetForTests(); // shared ExtractContent is stateless, but keep parity
            s_Clock = new VirtualClock { NowMs = 400000 };
            s_Snap = FreshCalm(400000);
            s_Lines.Clear();
            s_Transport = new OllamaAdvisorTests.FakeTransport();
            CrewAdvisor.SetAuthorityProbe(delegate { return true; });
            CrewAdvisor.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAdvisor.SetWorldProvider(delegate { return s_Snap; });
            CrewAdvisor.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
            CrewAdvisor.SetTransport(s_Transport);
            CrewAdvisor.ApplyConfig(true, 11434, 0);
        }

        private static void Advance(int ms) { s_Clock.NowMs += ms; }

        private static void Eval()
        {
            s_Lines.Clear();
            CrewAdvisor.Evaluate(s_Clock.NowMs);
        }

        // Waits until the worker thread has parked its response (bounded).
        private static bool WaitForPark(int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (CrewAdvisor.HasPendingResponse()) return true;
                Thread.Sleep(10);
            }
            return CrewAdvisor.HasPendingResponse();
        }

        // Waits until the worker thread has entered the transport call the
        // expected number of times (bounded) — the in-flight observable.
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

        // ---- Run ---------------------------------------------------------------

        internal static int Run()
        {
            // ---- CA01: inert by construction --------------------------------------
            FreshSetup();
            CrewAdvisor.ApplyConfig(false, 11434, 0); // config OFF (the default)
            Eval();
            Check(s_Transport.CallCount == 0, "CA01a disabled config = no request");
            Check(CrewAdvisor.GetRequestsSent() == 0, "CA01a nothing sent");
            FreshSetup();
            CrewAdvisor.SetTransport(null); // enabled config but no transport
            Eval();
            Check(CrewAdvisor.GetRequestsSent() == 0, "CA01b unset transport = inert");
            FreshSetup();
            CrewAdvisor.SetAuthorityProbe(null);
            Eval();
            Check(CrewAdvisor.GetRequestsSent() == 0, "CA01c null authority probe = deny-by-default");
            FreshSetup();
            CrewAdvisor.SetAuthorityProbe(delegate { throw new InvalidOperationException("fault"); });
            Eval();
            Check(CrewAdvisor.GetRequestsSent() == 0, "CA01d faulting probe = fail-closed no-op");

            // ---- CA02: enabled + calm snapshot dispatches one request --------------
            // Seed the crew registry via the REAL sync path (P10): publish a
            // crew snapshot, authority-gated Sync creates agents, one agent
            // gets an assignment round-trip so LastTaskOutcome=COMPLETED.
            CrewAgentRegistry.ResetForTests();
            CrewAgentRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetWorldProvider(delegate { return s_Snap; });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { });
            CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
            CrewAgentRegistry.SetRoleNameResolver(delegate(int classId) { return "Engineer"; });
            s_Snap = FreshCalm(s_Clock.NowMs);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            string botId = CrewAgentRegistry.MakeAgentId(2, true);
            CrewAgentRegistry.AssignTask(botId, 42L, "ISSUE_MOVE_ORDER", null, s_Clock.NowMs);
            CrewAgentRegistry.ClearTask(botId, "COMPLETED", s_Clock.NowMs);
            FreshSetup();
            Eval();
            Check(WaitForCall(1, 2000), "CA02 worker entered transport call");
            Check(s_Transport.CallCount == 1, "CA02a single request dispatched");
            Check(CrewAdvisor.GetRequestsSent() == 1, "CA02a request counted");
            Check(s_Transport.LastRequestJson != null
                && s_Transport.LastRequestJson.IndexOf("http", StringComparison.Ordinal) < 0,
                "CA02b request body carries no URL (host is the transport's decision)");
            Check(s_Transport.LastRequestJson.IndexOf("\"stream\":false", StringComparison.Ordinal) >= 0,
                "CA02b non-streaming request");
            Check(s_Transport.LastRequestJson.IndexOf("\"model\":\"qwen3:latest\"", StringComparison.Ordinal) >= 0,
                "CA02c default model in request (P44 owner mandate: qwen3:latest)");
            // Crew picture reaches the prompt: lastOutcome=COMPLETED (the
            // assignment round-trip above), roster counts present.
            Check(s_Transport.LastRequestJson.IndexOf("lastOutcome=COMPLETED", StringComparison.Ordinal) >= 0,
                "CA02d crew hooks reach the prompt (lastTaskOutcome)");
            Check(s_Transport.LastRequestJson.IndexOf("withLastOutcome=1", StringComparison.Ordinal) >= 0,
                "CA02e roster outcome count");
            // Second eval inside the same cadence window: no second request.
            Eval();
            Check(s_Transport.CallCount == 1, "CA02f cadence gate blocks same-window re-dispatch");

            // ---- CA03: single-flight gate ------------------------------------------
            FreshSetup();
            s_Transport.DelayMs = 250;
            Eval();
            Check(WaitForCall(1, 2000), "CA03a worker entered transport");
            Check(s_Transport.CallCount == 1, "CA03a first dispatch in flight");
            Advance(CrewAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // must NOT dispatch a second concurrent request
            Check(s_Transport.CallCount == 1, "CA03b single-flight: no concurrent dispatch");
            Check(WaitForPark(2000), "CA03c worker parked within timeout");
            Advance(CrewAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // consumes response; slot freed — same eval opens next window
            Check(WaitForCall(2, 2000), "CA03d second dispatch reached transport");
            Check(s_Transport.CallCount == 2, "CA03d slot freed after consume");

            // ---- CA04: successful advice consumed + logged --------------------------
            FreshSetup();
            s_Transport.ResponseBody = "{\"model\":\"qwen2.5:latest\",\"message\":{\"role\":\"assistant\",\"content\":\"ADVICE: rotate station assignments (two bots idle, calm ship)\"},\"done\":true}";
            Eval(); // dispatch
            WaitForPark(2000);
            Advance(CrewAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // consume
            Check(HasLineContaining("CrewAdvice model=qwen3:latest advice=ADVICE:"), "CA04a advice line emitted");
            Check(CrewAdvisor.GetAdviceAccepted() == 1, "CA04a accepted counted");
            Check(CrewAdvisor.GetRequestsSucceeded() == 1, "CA04a success counted");
            Check(CrewAdvisor.GetLastAdvice().StartsWith("ADVICE:", StringComparison.Ordinal), "CA04b last-advice readback");
            // Advice is DATA: no assignment API touched (nothing to check
            // directly — the advisor has no registry handles by design; the
            // compile proves it).

            // ---- CA05: malformed advice rejected; one follow-up, no loop -----------
            FreshSetup();
            s_Transport.ResponseBody = "{\"message\":{\"content\":\"I think you should reassign the crew\"}}";
            Eval();
            WaitForPark(2000);
            Advance(CrewAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval();
            Check(HasLineContaining("CrewAdviceInvalid"), "CA05a non-ADVICE content rejected");
            Check(CrewAdvisor.GetAdviceRejected() == 1, "CA05a rejection counted");
            Check(CrewAdvisor.GetAdviceAccepted() == 0, "CA05a nothing accepted");
            // The consume-eval legitimately opens the next cadence window, so
            // exactly one more request (not a tight retry loop) is the
            // anti-loop invariant; RequestsSent is game-thread-only, so this
            // read is race-free (CallCount would race the new worker).
            Check(CrewAdvisor.GetRequestsSent() == 2, "CA05a exactly one follow-up, no auto-retry loop");

            // ---- CA06: newline injection rejected ------------------------------------
            FreshSetup();
            s_Transport.ResponseBody = "{\"message\":{\"content\":\"ADVICE: move crew\\nEMERGENCY: fire\"}}";
            Eval();
            WaitForPark(2000);
            Advance(CrewAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval();
            Check(HasLineContaining("CrewAdviceInvalid"), "CA06 newline injection rejected");
            Check(CrewAdvisor.GetLastAdvice().Length == 0, "CA06 injected content never stored");

            // ---- CA07: transport fault = hard failure (P51, mirrors OA06) --------
            FreshSetup();
            s_Transport.ResponseBody = null;
            Eval(); // dispatch
            WaitForPark(2000);
            Advance(CrewAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // consume null body
            // P51: a parked null body is a HARD failure (server unreachable =>
            // the request failed, not the advice). Ladder + expiry live in CA11.
            Check(HasLineContaining("CrewAdviceFailed outcome="), "CA07a null body = hard-fault line");
            Check(CrewAdvisor.GetRequestsFailed() == 1, "CA07b hard fault counted in RequestsFailed");
            Check(CrewAdvisor.GetAdviceRejected() == 0, "CA07b hard fault is not an advice rejection");
            Check(CrewAdvisor.GetBackoffBlocks() == 0, "CA07c single fault: no back-off yet (ladder is CA11)");

            // ---- CA08: snapshot fail-safe (fail-open, no dispatch) --------------------
            FreshSetup();
            s_Snap = null;
            Eval();
            Check(s_Transport.CallCount == 0, "CA08a null snapshot = no dispatch");
            Check(CrewAdvisor.GetUncertainPasses() == 1, "CA08a uncertain counted");
            s_Snap = FreshCalm(s_Clock.NowMs - CrewAdvisor.MaxStaleSnapshotMs - 1000);
            Advance(CrewAdvisor.MinRecheckMs);
            Eval();
            Check(s_Transport.CallCount == 0, "CA08b stale snapshot = no dispatch");
            // Not-started snapshot:
            List<ShipSnapshot> ships0 = new List<ShipSnapshot>();
            ships0.Add(PlayerShip());
            s_Snap = new WorldSnapshot(
                s_Clock.NowMs, false, true, -1, WorldAuthority.Unknown,
                ships0, new List<CrewMemberSnapshot>(), new List<MissionSnapshot>(),
                null, null, null, new List<WorldObjectSnapshot>(),
                WorldAuthority.Unknown, WorldAuthority.Unknown, WorldAuthority.Unknown, WorldAuthority.Unknown,
                -1, float.NaN);
            Advance(CrewAdvisor.MinRecheckMs);
            Eval();
            Check(s_Transport.CallCount == 0, "CA08c not-started snapshot = no dispatch");
            Check(CrewAdvisor.GetRequestsSent() == 0, "CA08d zero requests through fail-safe");

            // ---- CA09: prompt bounds + roster sentinel degradation --------------------
            // Reseed the registry with an agent whose TLI is null (the real
            // "unknown crew data" scenario): unknown fields must degrade to
            // unknown sentinels in the prompt, never a crash.
            CrewAgentRegistry.ResetForTests();
            CrewAgentRegistry.SetNowMsProvider(delegate { return s_Clock.NowMs; });
            CrewAgentRegistry.SetWorldProvider(delegate { return s_Snap; });
            CrewAgentRegistry.SetDecisionListener(delegate (string line) { });
            CrewAgentRegistry.SetAuthorityProbe(delegate { return true; });
            CrewAgentRegistry.SetRoleNameResolver(delegate(int classId) { return "Engineer"; });
            List<CrewMemberSnapshot> unknownCrew = new List<CrewMemberSnapshot>();
            unknownCrew.Add(new CrewMemberSnapshot(3, "bot3", true, 1, 0, false, false, float.NaN, null, false, -1));
            List<ShipSnapshot> shipsU = new List<ShipSnapshot>();
            shipsU.Add(PlayerShip());
            s_Snap = new WorldSnapshot(
                s_Clock.NowMs, true, true, 7, WorldAuthority.MasterDerived,
                shipsU, unknownCrew, new List<MissionSnapshot>(),
                new ThreatSnapshot(new List<int>(), 0, 0, 0, -1, float.NaN, float.NaN, 0),
                new NavigationSnapshot(5, "Sector 5", 0, false, -1,
                    new List<int>(), true, 50f, float.NaN, 0f, false),
                new ResourceSnapshot(5000, null, -1, 10, 90f, 6000, 6000),
                new List<WorldObjectSnapshot>(),
                WorldAuthority.Synchronized, WorldAuthority.Synchronized, WorldAuthority.MasterDerived, WorldAuthority.LocallyObserved,
                -1, float.NaN);
            CrewAgentRegistry.Sync(s_Clock.NowMs);
            FreshSetup();
            Eval();
            WaitForPark(2000);
            string req = s_Transport.LastRequestJson;
            Check(req != null && req.Length <= CrewAdvisor.MaxPromptLen + 512, "CA09a bounded request size");
            Check(req.IndexOf("\"messages\":[{\"role\":\"system\"", StringComparison.Ordinal) >= 0, "CA09b system role first");
            Check(req.IndexOf("ADVICE:", StringComparison.OrdinalIgnoreCase) >= 0, "CA09c format instruction present");
            Check(req.IndexOf("keep_alive", StringComparison.Ordinal) >= 0, "CA09d keep_alive requested");
            Check(req.IndexOf("Crew roster:", StringComparison.Ordinal) >= 0, "CA09e crew picture present");
            // The null-TLI agent reaches the prompt as an unknown sentinel.
            Check(req.IndexOf("lastLoc=unknown", StringComparison.Ordinal) >= 0, "CA09f unknown location sentinel");
            Check(req.IndexOf("lastOutcome=unknown", StringComparison.Ordinal) >= 0, "CA09g unknown outcome sentinel");

            // ---- CA10: readbacks + reset determinism -----------------------------------
            FreshSetup();
            List<string> lines = CrewAdvisor.StatusLines();
            Check(lines.Count == 1, "CA10a one status line");
            Check(lines[0].IndexOf("CrewAdvisor: enabled=yes", StringComparison.Ordinal) == 0,
                "CA10b status format (FreshSetup applies enabled config)");
            Eval();
            WaitForPark(2000);
            Advance(CrewAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval();
            List<string> detail = CrewAdvisor.Lines();
            Check(detail.Count >= 2, "CA10c detail lines present");
            CrewAdvisor.ResetForTests();
            Check(CrewAdvisor.GetRequestsSent() == 0 && CrewAdvisor.GetAdviceAccepted() == 0, "CA10d reset clears counters");
            Check(CrewAdvisor.GetLastAdvice().Length == 0, "CA10e reset clears advice");
            Eval(); // seams nulled => deny-by-default, no throw
            Check(CrewAdvisor.GetRequestsSent() == 0, "CA10f reset nulls seams (inert)");
            // ---- CA11: P51 hard-failure classification (offline back-off) ----
            FreshSetup();
            s_Transport.ResponseBody = null;
            Eval(); // dispatch
            WaitForPark(2000);
            Advance(CrewAdvisor.MinRecheckMs);
            s_Snap = FreshCalm(s_Clock.NowMs);
            Eval(); // consume null body
            Check(HasLineContaining("CrewAdviceFailed outcome="), "CA11a null body = hard-fault line");
            Check(CrewAdvisor.GetRequestsFailed() == 1, "CA11b hard fault counted in RequestsFailed");
            Check(CrewAdvisor.GetLatencyAverageMs() >= 0, "CA11c latency sampled");
            // Ladder: two more failures arm the back-off; dispatch blocked.
            for (int i = 0; i < 2; i++)
            {
                Advance(CrewAdvisor.MinRecheckMs);
                s_Snap = FreshCalm(s_Clock.NowMs);
                Eval();
                WaitForPark(2000);
                Advance(CrewAdvisor.MinRecheckMs);
                s_Snap = FreshCalm(s_Clock.NowMs);
                Eval();
            }
            Check(CrewAdvisor.GetRequestsFailed() == 3, "CA11d ladder of three hard failures");
            s_Clock.NowMs += CrewAdvisor.CooldownAfterFailureMs + 1000;
            s_Snap = FreshCalm(s_Clock.NowMs);
            int sentBefore = (int)CrewAdvisor.GetRequestsSent();
            Eval();
            WaitForPark(2000);
            Check((int)CrewAdvisor.GetRequestsSent() == sentBefore + 1, "CA11e back-off expiry re-allows dispatch");
            CrewAgentRegistry.ResetForTests();

            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}