// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.Cli.Output;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// T82 and T71: the CLI summary prints a cost only when every test was priced and a duration only over the tests that
/// were timed. The priced tests alone printed as "Cost: $x", and a timed run with no price as "Cost: $0.0000".
/// </summary>
[Collection("ConsoleTests")]
public class ConsoleReporterSummaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static TestResult Timed(string name, decimal? cost) => new()
    {
        TestName = name,
        Score = 90,
        Passed = true,
        Performance = new PerformanceMetrics { StartTime = T0, EndTime = T0.AddSeconds(1), EstimatedCost = cost },
    };

    private static string Summary(TestSummary summary)
    {
        var originalErr = Console.Error;
        var originalCulture = CultureInfo.CurrentCulture;
        using var sw = new StringWriter();
        Console.SetError(sw);
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            ConsoleReporter.WriteSummary(summary);
            return sw.ToString();
        }
        finally
        {
            Console.SetError(originalErr);
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void APartlyPricedRun_PrintsNotPriced_NotThePricedPartAsTheCost()
    {
        // Today: "Cost: $0.0050", the priced test alone.
        var output = Summary(new TestSummary("s", [Timed("a", 0.005m), Timed("b", null)]));

        Assert.Contains("Cost: not priced (1 of 2 tests have no price)", output);
        Assert.DoesNotContain("$0.0050", output);
    }

    [Fact]
    public void ATimedRunWithNoPrice_DoesNotPrintAZeroCost()
    {
        // Today: "Cost: $0.0000".
        var output = Summary(new TestSummary("s", [Timed("a", null), Timed("b", null)]));

        Assert.Contains("Cost: not priced (2 of 2 tests have no price)", output);
        Assert.DoesNotContain("$0.0000", output);
    }

    [Fact]
    public void AFullyPricedRun_PrintsItsTotal_AndAPricedZero()
    {
        // Guard: a complete total is the cost, and a priced $0 is a real $0.
        Assert.Contains("Cost: $0.0080", Summary(new TestSummary("s", [Timed("a", 0.005m), Timed("b", 0.003m)])));
        Assert.Contains("Cost: $0.0000", Summary(new TestSummary("s", [Timed("a", 0m)])));
    }

    [Fact]
    public void APartlyTimedRun_SaysTheDurationIsOverTheTimedTests()
    {
        // Today: "Duration: 1.0s", as if it covered both tests.
        var output = Summary(new TestSummary("s", [Timed("a", 0.005m), new TestResult { TestName = "b", Score = 90, Passed = true }]));

        Assert.Contains("Duration: 1.0s (over 1 timed)", output);
    }

    [Fact]
    public void AnEmptyRun_SaysNoTestsRan_NotThatAllPassed()
    {
        // Today: "All tests passed." for a run with no tests.
        var output = Summary(new TestSummary("s", []));

        Assert.Contains("No tests ran.", output);
        Assert.DoesNotContain("All tests passed.", output);
    }

    [Fact]
    public void NothingMeasured_HasNoScore()
    {
        // Guard: no score is printed for a run whose every test produced no verdict.
        var output = Summary(new TestSummary("s",
            [new TestResult { TestName = "a", Score = 0, Passed = false, JudgeFailed = true }]));

        Assert.Contains("Score: n/a (nothing measured)", output);
    }
}
