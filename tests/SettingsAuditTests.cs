// NOT part of the shipped mod: compiled by tests\run_tests.ps1. Covers the
// P53 settings-audit contract (master prompt §15–19): the SettingsAudit
// truth table is single-sourced, so the tests pin its completeness (every
// declared SaveValue classified), its honesty (the 6 grep-verified dead
// knobs stay DEAD), and its internal consistency (no row both LIVE and
// DEAD; effective text matches runtime).
using System;
using System.Collections.Generic;
using CapBot.Core.Diagnostics;

namespace CapBot.TaskTests
{
    internal static class SettingsAuditTests
    {
        private static int s_Passed;
        private static int s_Failed;

        internal static int LastPassed { get { return s_Passed; } }

        private static void Check(bool condition, string name)
        {
            if (condition) { s_Passed++; Console.WriteLine("PASS " + name); }
            else { s_Failed++; Console.WriteLine("FAIL " + name); }
        }

        // The audit's expected shape (grep evidence, CAPBOT_AUDIT.md H4):
        //   LIVE  = CaptainBotEnabled/SmartAIEnabled/MissionAutoDetectEnabled
        //           (Autonomy.OnTick), ModUpdaterEnabled (Mod.cs boot),
        //           VerboseLogging (CapBotLog), Ollama*/Qwen* (advisors).
        //   DEAD  = AutoAssignCaptain, AIReactionSpeed, AIAccuracy,
        //           CombatEngageRange, CombatDisengageHealth,
        //           MinCreditsReserve — no consumer anywhere in the mod.
        private static readonly string[] ExpectedLive = new string[]
        {
            "CaptainBotEnabled", "SmartAIEnabled", "MissionAutoDetectEnabled",
            "ModUpdaterEnabled", "VerboseLogging",
            "OllamaAdvisorEnabled", "OllamaModel", "OllamaPort", "QwenAdvisorEnabled",
        };

        internal static int Run()
        {
            // ---- SA01: completeness + exact classification -------------------
            Check(SettingsAudit.Rows.Length == ExpectedLive.Length + SettingsAudit.DeadSettings.Length,
                "SA01 table size = live + dead (" + SettingsAudit.Rows.Length + ")");
            foreach (string s in ExpectedLive)
            {
                SettingsAudit.SettingRow row = SettingsAudit.GetRow(s);
                Check(row != null && row.Runtime == SettingsAudit.RuntimeLive,
                    "SA01b " + s + " classified LIVE with consumer");
                Check(row == null || row.Consumer.IndexOf("NONE", StringComparison.Ordinal) != 0,
                    "SA01c " + s + " names its consumer (not NONE)");
            }
            foreach (string s in SettingsAudit.DeadSettings)
            {
                SettingsAudit.SettingRow row = SettingsAudit.GetRow(s);
                Check(row != null && row.Runtime == SettingsAudit.RuntimeDead,
                    "SA02 " + s + " classified DEAD");
                Check(row != null && row.Consumer.StartsWith("NONE", StringComparison.Ordinal),
                    "SA02b " + s + " reports consumer NONE (honest dead)");
                Check(row != null && row.Effective.StartsWith("not wired", StringComparison.Ordinal),
                    "SA02c " + s + " effective says not wired");
            }

            // ---- SA03: internal consistency -----------------------------------
            for (int i = 0; i < SettingsAudit.Rows.Length; i++)
            {
                SettingsAudit.SettingRow row = SettingsAudit.Rows[i];
                bool isDead = row.Runtime == SettingsAudit.RuntimeDead;
                Check(!(isDead && row.Effective.StartsWith("wired", StringComparison.Ordinal)),
                    "SA03 " + row.Name + " DEAD row never claims wired effective");
                Check(!(!isDead && row.Effective.StartsWith("not wired", StringComparison.Ordinal)),
                    "SA03b " + row.Name + " non-DEAD row never claims not-wired");
                Check(!string.IsNullOrEmpty(row.Consumer), "SA03c " + row.Name + " consumer text present");
            }
            // Row names unique (fixed order preserved).
            for (int i = 0; i < SettingsAudit.Rows.Length; i++)
                for (int j = i + 1; j < SettingsAudit.Rows.Length; j++)
                    Check(!string.Equals(SettingsAudit.Rows[i].Name, SettingsAudit.Rows[j].Name, StringComparison.Ordinal),
                        "SA03d row names unique: " + SettingsAudit.Rows[i].Name);

            // ---- SA04: dead-count readback -------------------------------------
            Check(SettingsAudit.DeadCount() == 6, "SA04 exactly 6 dead knobs");
            Check(SettingsAudit.GetRow(null) == null, "SA04b null lookup safe");
            Check(SettingsAudit.GetRow("NotASetting") == null, "SA04c unknown lookup safe");

            Console.WriteLine("");
            Console.WriteLine("SUMMARY passed=" + s_Passed + " failed=" + s_Failed);
            return s_Failed;
        }
    }
}