using System;
using System.IO;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Speed budgets are promises to players, checked on real machines: the reference PC and the owner's runs (owner,
    /// 2026-10-28: optimise for real users, not the GitHub cloud). A shared CI runner says nothing about a player's machine, so
    /// there the time is reported (on the run's summary page when over) and never fails; everywhere else the budget holds.
    /// </summary>
    static class Budget
    {
        public static readonly bool OnCi = Set("CI") || Set("GITHUB_ACTIONS");

        static bool Set(string name) => string.Equals(Environment.GetEnvironmentVariable(name), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>Asserts <paramref name="measuredMs"/> ≤ <paramref name="limitMs"/>, except on a CI runner, where it only reports.</summary>
        public static void AtMost(double measuredMs, double limitMs, string what)
        {
            TestContext.Progress.WriteLine($"{what}: {measuredMs:F2} ms (budget {limitMs:G} ms{(OnCi ? ", reported only on CI" : "")})");
            if (!OnCi)
            {
                Assert.That(measuredMs, Is.LessThanOrEqualTo(limitMs), what);
                return;
            }
            // The test runner keeps a passing test's output to itself, so the note goes on the run's summary page.
            string summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (measuredMs > limitMs && !string.IsNullOrEmpty(summary))
                File.AppendAllText(summary, $"- ⚠️ Speed budget, reported only (a shared CI runner isn't a player's machine): {what}: " +
                                            $"{measuredMs:F2} ms, over {limitMs:G} ms\n");
        }
    }
}
