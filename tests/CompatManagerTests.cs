// Dev-side unit tests for the Phase 27 compatibility manager (pure C#).
// NOT part of the shipped mod: compiled separately by tests\run_tests.ps1.
// Time is virtual by construction: the manager carries no clock — install
// ordering is deterministic from registration order alone.
//
// Covers the Phase 27 mandated scenarios:
//   CM01 deny-by-default (no provider / faulting provider => nothing installs)
//   CM02 gate semantics (none loaded => skip; any alias loaded => install)
//   CM03 idempotence (second InstallAll skips installed actions)
//   CM04 handler fail-safety (faulting action does not block the others)
//   CM05 bounded registration + duplicate-safe + null refusal
//   CM06 status lines + readbacks
//   CM07 determinism (same setup => same counters/lines)
//   CM08 registration-order install order
//   CM09 gate-unavailable line + gateDeny counter
//   CM10 MoreBots production-shape mirror (delegate self-idempotence layering)
using System;
using System.Collections.Generic;
using CapBot.Core.Compatibility;

namespace CapBot.TaskTests
{
    internal static class CompatManagerTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        private static readonly List<string> s_Lines = new List<string>();

        private static void FreshSetup()
        {
            CompatManager.ResetForTests();
            s_Lines.Clear();
            CompatManager.SetDecisionListener(delegate (string line) { s_Lines.Add(line); });
        }

        private static bool HasLineContaining(string fragment)
        {
            for (int i = 0; i < s_Lines.Count; i++)
            {
                if (s_Lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        internal static int Run()
        {
            // ---- CM01: deny-by-default -------------------------------------------
            FreshSetup();
            int neverInstalled = 0;
            Check(CompatManager.RegisterAction("a1", new string[] { "ModX" }, delegate { neverInstalled++; }),
                "CM01 registration with no provider succeeds");
            CompatManager.InstallAll();
            Check(neverInstalled == 0, "CM01 no provider => install denied");
            Check(CompatManager.GateDenyCount == 1, "CM01 gate-deny counted");
            Check(CompatManager.InstalledActions == 0, "CM01 nothing installed");
            Check(HasLineContaining("CompatGateUnavailable"), "CM01 gate-unavailable line emitted");
            // Faulting provider: same deny-by-default.
            CompatManager.SetIsLoadedProvider(delegate (string m) { throw new InvalidOperationException("fault"); });
            CompatManager.InstallAll();
            Check(neverInstalled == 0, "CM01 faulting provider => install denied");
            Check(CompatManager.GateDenyCount == 2, "CM01 second gate-deny counted");

            // ---- CM02: gate semantics --------------------------------------------
            FreshSetup();
            int gated = 0;
            string loadedMod = "";
            CompatManager.SetIsLoadedProvider(delegate (string m) { loadedMod = m; return m == "TheMod"; });
            CompatManager.RegisterAction("g1", new string[] { "NotLoaded", "TheMod" }, delegate { gated++; });
            CompatManager.InstallAll();
            Check(gated == 1, "CM02 any-alias-loaded => install");
            Check(loadedMod == "TheMod", "CM02 provider saw the second alias (first miss did not short-circuit)");
            CompatManager.RegisterAction("g2", new string[] { "NotLoaded" }, delegate { gated += 10; });
            CompatManager.InstallAll();
            Check(gated == 1, "CM02 none-loaded => skip (no fault)");
            // Skip accounting: call 2 skipped only g2 (g1 was skipped via the
            // already-installed continue, which is not a gate skip).
            Check(CompatManager.SkippedActions == 1, "CM02 skips counted");
            Check(CompatManager.InstalledActions == 1, "CM02 exactly one install total");

            // ---- CM03: idempotence ------------------------------------------------
            FreshSetup();
            int once = 0;
            CompatManager.SetIsLoadedProvider(delegate (string m) { return true; });
            CompatManager.RegisterAction("once", new string[] { "M" }, delegate { once++; });
            CompatManager.InstallAll();
            Check(once == 1, "CM03 first install fires");
            CompatManager.InstallAll();
            Check(once == 1, "CM03 second InstallAll skips (manager-level idempotence)");
            Check(CompatManager.InstallCalls == 2, "CM03 install-call counter tracks calls");

            // ---- CM04: fail-safety -------------------------------------------------
            FreshSetup();
            int afterFault = 0;
            CompatManager.SetIsLoadedProvider(delegate (string m) { return true; });
            CompatManager.RegisterAction("bad", new string[] { "M" }, delegate { throw new InvalidOperationException("boom"); });
            CompatManager.RegisterAction("good", new string[] { "M" }, delegate { afterFault++; });
            CompatManager.InstallAll();
            Check(afterFault == 1, "CM04 faulting action did not block the next action");
            Check(CompatManager.FaultCount == 1, "CM04 fault counted");
            Check(HasLineContaining("CompatActionFaulted name=bad"), "CM04 fault line emitted");
            Check(HasLineContaining("CompatInstalled name=good"), "CM04 surviving action installed line emitted");
            Check(CompatManager.InstalledActions == 1, "CM04 only the surviving action counted installed");

            // ---- CM05: bounded/duplicate-safe registration ------------------------
            FreshSetup();
            Check(CompatManager.RegisterAction("dup", new string[] { "M" }, delegate { }),
                "CM05 first registration accepted");
            Check(CompatManager.RegisterAction("dup", new string[] { "M" }, delegate { }),
                "CM05 duplicate name idempotent (true, no second entry)");
            Check(CompatManager.ActionCount == 1, "CM05 no duplicate stored");
            Check(!CompatManager.RegisterAction(null, new string[] { "M" }, delegate { }), "CM05 null name refused");
            Check(!CompatManager.RegisterAction("", new string[] { "M" }, delegate { }), "CM05 empty name refused");
            Check(!CompatManager.RegisterAction("n1", null, delegate { }), "CM05 null mods refused");
            Check(!CompatManager.RegisterAction("n2", new string[0], delegate { }), "CM05 empty mods refused");
            Check(!CompatManager.RegisterAction("n3", new string[] { "M" }, null), "CM05 null install refused");
            Check(!CompatManager.RegisterAction("n3", new string[] { null }, delegate { }), "CM05 null alias refused");
            Check(CompatManager.ActionCount == 1, "CM05 refusals did not register");
            Check(CompatManager.RefusedRegistrations == 6, "CM05 all refusals counted");
            for (int i = 0; i < 10; i++)
            {
                int n = i;
                CompatManager.RegisterAction("filler" + n, new string[] { "M" + n }, delegate { });
            }
            Check(CompatManager.ActionCount == CompatManager.MaxActionsBound,
                "CM05 cap reached exactly (1 + 7 fillers)");
            CompatManager.ResetForTests();
            Check(CompatManager.ActionCount == 0, "CM05 reset clears actions");

            // ---- CM06: status lines + readbacks -----------------------------------
            FreshSetup();
            List<string> empty = CompatManager.StatusLines();
            Check(empty.Count == 1 && empty[0].IndexOf("actions=0", StringComparison.Ordinal) >= 0,
                "CM06 empty status is a single counters line");
            CompatManager.SetIsLoadedProvider(delegate (string m) { return true; });
            CompatManager.RegisterAction("s1", new string[] { "A", "B" }, delegate { });
            CompatManager.RegisterAction("s2", new string[] { "C" }, delegate { });
            CompatManager.InstallAll();
            List<string> lines = CompatManager.StatusLines();
            Check(lines.Count == 3, "CM06 counters line + one line per action");
            Check(lines[1].IndexOf("s1 mods=A,B installed=yes", StringComparison.Ordinal) >= 0,
                "CM06 action line carries mods list + installed flag");
            Check(lines[2].IndexOf("s2 mods=C installed=yes", StringComparison.Ordinal) >= 0,
                "CM06 second action line present");
            Check(CompatManager.LastSummary.IndexOf("actions=2 installed=2 skipped=0", StringComparison.Ordinal) >= 0,
                "CM06 summary readback");

            // ---- CM07: determinism --------------------------------------------------
            FreshSetup();
            CompatManager.SetIsLoadedProvider(delegate (string m) { return true; });
            CompatManager.RegisterAction("d1", new string[] { "M" }, delegate { });
            CompatManager.InstallAll();
            List<string> first = CompatManager.StatusLines();
            FreshSetup();
            CompatManager.SetIsLoadedProvider(delegate (string m) { return true; });
            CompatManager.RegisterAction("d1", new string[] { "M" }, delegate { });
            CompatManager.InstallAll();
            List<string> again = CompatManager.StatusLines();
            Check(first.Count == again.Count, "CM07 same line count");
            for (int i = 0; i < first.Count; i++)
            {
                Check(first[i] == again[i], "CM07 identical line " + i);
            }

            // ---- CM08: registration order = install order --------------------------
            FreshSetup();
            List<string> order = new List<string>();
            CompatManager.SetIsLoadedProvider(delegate (string m) { return true; });
            CompatManager.RegisterAction("third", new string[] { "M" }, delegate { order.Add("third"); });
            CompatManager.RegisterAction("first", new string[] { "M" }, delegate { order.Add("first"); });
            CompatManager.RegisterAction("second", new string[] { "M" }, delegate { order.Add("second"); });
            CompatManager.InstallAll();
            Check(order.Count == 3 && order[0] == "third" && order[1] == "first" && order[2] == "second",
                "CM08 registration order preserved (not alphabetical, not priority-ordered)");

            // ---- CM09: gate-unavailable accounting ---------------------------------
            FreshSetup();
            CompatManager.RegisterAction("g9", new string[] { "M" }, delegate { });
            CompatManager.InstallAll(); // no provider
            CompatManager.SetIsLoadedProvider(delegate (string m) { return m == "M"; });
            CompatManager.InstallAll(); // gate now healthy
            Check(CompatManager.GateDenyCount == 1, "CM09 gate-deny counted once from the no-provider call");
            Check(HasLineContaining("CompatGateUnavailable"), "CM09 gate-unavailable line emitted once");
            Check(HasLineContaining("CompatInstalled name=g9"), "CM09 action installed after gate recovered");

            // ---- CM10: MoreBots production-shape mirror -----------------------------
            // Mirrors the real wiring: the production action is registered EXACTLY
            // ONCE at boot, its delegate is self-guarded (the
            // MoreBotsCompatPatch _installed/_triedInstall pattern), and manager-
            // level idempotence layers on top. Same-name re-registration is a
            // no-op that neither replaces nor duplicates the action.
            FreshSetup();
            int realWork = 0;
            bool triedInstall = false;
            CompatManager.SetIsLoadedProvider(delegate (string m) { return true; });
            CompatManager.RegisterAction("mb", new string[] { "MoreBots" }, delegate
            {
                if (triedInstall) return; // self-guard, MoreBotsCompatPatch pattern
                triedInstall = true;
                realWork++;
            });
            CompatManager.InstallAll();
            Check(realWork == 1, "CM10 guarded delegate installs once");
            CompatManager.InstallAll();
            Check(realWork == 1, "CM10 manager idempotence + delegate guard hold");
            Check(CompatManager.RegisterAction("mb", new string[] { "MoreBots" }, delegate { realWork += 10; }),
                "CM10 same-name re-registration idempotent");
            Check(CompatManager.ActionCount == 1, "CM10 still a single action");
            CompatManager.InstallAll();
            Check(realWork == 1, "CM10 re-registration did not replace or duplicate the action");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}