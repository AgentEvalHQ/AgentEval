// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Models;

/// <summary>
/// T71 and T82: a batch average, duration or cost that nobody measured is null and says so, never a placeholder 0 that
/// reaches a reader; a measured or recorded zero stays a real 0. The scripted harness below hands out run results as
/// inputs to this arithmetic; it grades nothing.
/// </summary>
public class NullableBatchAggregateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static TestResult Scored(string name, int score, bool passed = true) =>
        new() { TestName = name, Score = score, Passed = passed };

    // As MAFEvaluationHarness writes a case whose judge produced no verdict.
    private static TestResult JudgeFailed(string name) =>
        new() { TestName = name, Score = 0, Passed = false, JudgeFailed = true, Details = "The judge produced no verdict" };

    // As MAFEvaluationHarness writes a case whose model call threw: no performance data.
    private static TestResult Errored(string name) =>
        new() { TestName = name, Score = 0, Passed = false, Error = new HttpRequestException("429 Too Many Requests") };

    private static TestResult Timed(string name, double seconds, decimal? cost = null, int score = 90) => new()
    {
        TestName = name,
        Score = score,
        Passed = true,
        Performance = new PerformanceMetrics { StartTime = T0, EndTime = T0.AddSeconds(seconds), EstimatedCost = cost },
    };

    // ── TestSummary ───────────────────────────────────────────────────────────

    [Fact]
    public void AverageScore_IsNull_WhenNothingWasMeasured()
    {
        // Today: 0, a placeholder only HasMeasuredScore told apart from a measured 0.
        var summary = new TestSummary("s", [JudgeFailed("a"), Errored("b")]);

        Assert.Null(summary.AverageScore);
        Assert.False(summary.HasMeasuredScore);
    }

    [Fact]
    public void AverageScore_KeepsAMeasuredZero()
    {
        // Guard: a judge that scored 0 measured a 0.
        var summary = new TestSummary("s", [Scored("a", 0, passed: false), JudgeFailed("b")]);

        Assert.Equal(0.0, summary.AverageScore);
    }

    [Fact]
    public void TotalDuration_IsNull_WhenNoTestWasTimed()
    {
        // Today: TimeSpan.Zero, which reads as an instant run.
        var summary = new TestSummary("s", [Scored("a", 90)]);

        Assert.Null(summary.TotalDuration);
    }

    [Fact]
    public void TotalDuration_SumsTheTimedTestsOnly()
    {
        // Guard: an untimed test adds nothing; the timed ones still add up.
        var summary = new TestSummary("s", [Timed("a", 2), Scored("b", 90), Timed("c", 1)]);

        Assert.Equal(TimeSpan.FromSeconds(3), summary.TotalDuration);
    }

    [Fact]
    public void AllPassed_IsFalse_ForAnEmptySuite()
    {
        // Today: true, so an empty run read "All tests passed."
        Assert.False(new TestSummary("s", []).AllPassed);
    }

    [Fact]
    public void AllPassed_IsTrue_WhenEveryTestPassed()
    {
        // Guard.
        Assert.True(new TestSummary("s", [Scored("a", 90), Scored("b", 80)]).AllPassed);
    }

    // ── TestSummary.TotalCost (T82): complete or absent ───────────────────────

    [Fact]
    public void TotalCost_IsNull_WhenATestIsNotPriced_AndNamesIt()
    {
        // Today: 0.005, the priced test alone, printed by the CLI as the run's cost.
        var summary = new TestSummary("s", [Timed("a", 1, cost: 0.005m), Timed("b", 1, cost: null)]);

        Assert.Null(summary.TotalCost);
        Assert.Equal("b", Assert.Single(summary.UnpricedTests));
        Assert.Contains("1 of 2 tests were not priced (b)", summary.TotalCostNotPricedReason);
    }

    [Fact]
    public void TotalCost_IsNull_WhenATestHasNoPerformanceData()
    {
        // A run that threw carries no performance data: what it spent is unknown, so the total is too.
        var summary = new TestSummary("s", [Timed("a", 1, cost: 0.005m), Errored("b")]);

        Assert.Null(summary.TotalCost);
        Assert.Equal("b", Assert.Single(summary.UnpricedTests));
    }

    [Fact]
    public void TotalCost_IsNull_WhenNoTestIsPriced()
    {
        // Today: 0, a timed run with no price for the model.
        var summary = new TestSummary("s", [Timed("a", 1), Timed("b", 1)]);

        Assert.Null(summary.TotalCost);
        Assert.Equal("no test was priced (no performance data, or no price for the model).", summary.TotalCostNotPricedReason);
    }

    [Fact]
    public void TotalCost_IsNull_ForAnEmptySuite()
    {
        // Today: 0.
        var summary = new TestSummary("s", []);

        Assert.Null(summary.TotalCost);
        Assert.NotNull(summary.TotalCostNotPricedReason);
    }

    [Fact]
    public void TotalCost_SumsWhenEveryTestIsPriced_AndKeepsAPricedZero()
    {
        // Guard: complete totals, a priced $0 among them, are the total.
        var priced = new TestSummary("s", [Timed("a", 1, cost: 0.005m), Timed("b", 1, cost: 0.003m)]);
        var free = new TestSummary("s", [Timed("a", 1, cost: 0m)]);

        Assert.Equal(0.008m, priced.TotalCost);
        Assert.Null(priced.TotalCostNotPricedReason);
        Assert.Empty(priced.UnpricedTests);
        Assert.Equal(0m, free.TotalCost);
    }

    // ── ToEvaluationReport ────────────────────────────────────────────────────

    [Fact]
    public void Report_OverallScore_IsNull_WhenNothingWasMeasured()
    {
        // Today: 0, copied from AverageScore's placeholder.
        var report = new TestSummary("s", [JudgeFailed("a"), Errored("b")]).ToEvaluationReport();

        Assert.Null(report.OverallScore);
    }

    [Fact]
    public void Report_PassRate_IsNull_ForAnEmptyReport()
    {
        // Today: 0%.
        var report = new TestSummary("s", []).ToEvaluationReport();

        Assert.Null(report.PassRate);
    }

    [Fact]
    public void Report_Duration_IsNull_WhenNoTestWasTimed()
    {
        // Today: two UtcNow reads a few ticks apart, a timed run of about 0.
        var report = new TestSummary("s", [Scored("a", 90)]).ToEvaluationReport();

        Assert.Null(report.Duration);
        Assert.Null(new EvaluationReport().Duration);
    }

    [Fact]
    public void Report_Duration_IsTheTimedSpan()
    {
        // Guard.
        var report = new TestSummary("s", [Timed("a", 2)]).ToEvaluationReport();

        Assert.Equal(TimeSpan.FromSeconds(2), report.Duration);
    }

    [Fact]
    public void Report_TestDuration_IsNull_WhenNotTimed()
    {
        // Today: 0 ms.
        var report = new TestSummary("s", [Scored("a", 90), Timed("b", 1.5)]).ToEvaluationReport();

        Assert.Null(report.TestResults.Single(t => t.Name == "a").DurationMs);
        Assert.Equal(1500L, report.TestResults.Single(t => t.Name == "b").DurationMs);
    }

    [Fact]
    public void Report_NotMeasuredTest_HasNoScore()
    {
        // Today: the judge-failed case carried Score 0 into every exporter.
        var report = new TestSummary("s", [Scored("a", 90), JudgeFailed("d")]).ToEvaluationReport();

        Assert.Null(report.TestResults.Single(t => t.Name == "d").Score);
        Assert.Equal(90.0, report.TestResults.Single(t => t.Name == "a").Score);
    }

    [Fact]
    public void Report_Metadata_HasNoPartialCost_ButSaysWhy()
    {
        // Today: TotalCost "0.005000", the priced test alone.
        var report = new TestSummary("s", [Timed("a", 1, cost: 0.005m), Timed("b", 1)]).ToEvaluationReport();

        Assert.False(report.Metadata.ContainsKey("TotalCost"));
        Assert.Contains("not priced", report.Metadata["TotalCostNotPriced"]);
    }

    [Fact]
    public void Report_Metadata_KeepsAPricedZeroCost()
    {
        // Today: a priced $0 was dropped by a "> 0" check, as if nothing was priced.
        var report = new TestSummary("s", [Timed("a", 1, cost: 0m)]).ToEvaluationReport();

        Assert.Equal("0.000000", report.Metadata["TotalCost"]);
    }

    // ── StochasticResult ──────────────────────────────────────────────────────

    private static StochasticResult Stochastic(params TestResult[] runs) => new(
        new TestCase { Name = "t", Input = "q" },
        runs,
        runs.Any(StochasticResult.IsMeasured) ? new StochasticStatistics(PassRate: 1, MeanScore: 90, MedianScore: 90, StandardDeviation: 0, MinScore: 90,
            MaxScore: 90, Percentile25: 90, Percentile75: 90, Percentile95: 90, ConfidenceInterval: null,
            SampleSize: runs.Count(StochasticResult.IsMeasured)) : null,
        new StochasticOptions(Runs: runs.Length, SuccessRateThreshold: 0.6),
        Passed: true);

    private static TestResult WithTools(string name, params string[] calls)
    {
        var usage = new ToolUsageReport();
        for (var i = 0; i < calls.Length; i++)
            usage.AddCall(new ToolCallRecord { Name = calls[i], CallId = $"{name}-{i}" });
        return new TestResult { TestName = name, Score = 90, Passed = true, ToolUsage = usage };
    }

    [Fact]
    public void DurationStats_IsNull_WhenNoRunRecordedTiming()
    {
        // Today: an all-zero record with SampleSize 0, a mean of 0 ms.
        var result = Stochastic(Scored("a", 90), Scored("b", 90), Scored("c", 90));

        Assert.Null(result.DurationStats);
    }

    [Fact]
    public void ToolCallCountStats_IsNull_WhenNoRunTrackedTools()
    {
        // Today: an all-zero record with SampleSize 0.
        var result = Stochastic(Scored("a", 90), Scored("b", 90), Scored("c", 90));

        Assert.Null(result.ToolCallCountStats);
    }

    [Fact]
    public void StochasticTotalDuration_IsNull_WhenNoRunWasTimed()
    {
        // Today: TimeSpan.Zero.
        var result = Stochastic(Scored("a", 90), Scored("b", 90), Scored("c", 90));

        Assert.Null(result.TotalDuration);
    }

    [Fact]
    public void StochasticTotalDuration_SumsTheTimedRuns()
    {
        // Guard.
        var result = Stochastic(Timed("a", 2), Errored("b"), Timed("c", 1));

        Assert.Equal(TimeSpan.FromSeconds(3), result.TotalDuration);
        Assert.Equal(2, result.DurationStats!.SampleSize);
    }

    [Fact]
    public void ToolUsageSummary_CountsOnlyRunsThatTrackedTools()
    {
        // One run called Calc, one tracked tools and called none, one did not track tools. Today the untracked run counted
        // as a run with 0 calls: SampleSize 3, call rate 1/3, mean 1/3.
        var result = Stochastic(WithTools("a", "Calc"), WithTools("b"), Scored("c", 90));

        var summary = result.GetToolUsageSummary("Calc");

        Assert.Equal(2, summary.CallCountStats!.SampleSize);
        Assert.Equal(0.5, summary.CallRate);
        Assert.Equal(0.5, summary.CallCountStats.Mean);
        Assert.Equal(0.0, summary.ErrorRate);
        Assert.Equal(1, summary.TotalCalls);
        Assert.False(summary.AllRunsCalled);
    }

    [Fact]
    public void ToolUsageSummary_HasNoRates_WhenNoRunTrackedTools()
    {
        // Today: a 0% call rate over 3 runs, none of which tracked tools.
        var result = Stochastic(Scored("a", 90), Scored("b", 90), Scored("c", 90));

        var summary = result.GetToolUsageSummary("Calc");

        Assert.Null(summary.CallCountStats);
        Assert.Null(summary.CallRate);
        Assert.Null(summary.ErrorRate);
        Assert.False(summary.AllRunsCalled);
    }

    [Fact]
    public void PrintToolSummary_SaysNA_WhenNoRunTrackedTools()
    {
        // Today: "Call Rate: 0% (0/3 runs)".
        var result = Stochastic(Scored("a", 90), Scored("b", 90), Scored("c", 90));
        using var writer = new StringWriter();

        TableFormatter.PrintToolSummary(result.GetToolUsageSummary("Calc"), new OutputOptions { Writer = writer, UseColors = false });

        var output = writer.ToString();
        Assert.Contains("N/A (no run tracked tool usage)", output);
        Assert.DoesNotContain("0%", output);
    }

    [Fact]
    public void PrintToolSummary_RatesAreOverTheRunsThatTrackedTools()
    {
        var result = Stochastic(WithTools("a", "Calc"), WithTools("b"), Scored("c", 90));
        using var writer = new StringWriter();

        TableFormatter.PrintToolSummary(result.GetToolUsageSummary("Calc"), new OutputOptions { Writer = writer, UseColors = false });

        Assert.Contains("50% (1/2 runs that tracked tools)", writer.ToString());
    }

    [Fact]
    public void FastestAndSlowestRun_AreNull_WhenNoRunWasTimed()
    {
        // Today: FirstOrDefault() over no timed run gave a (null, 0 ms) tuple, which is not null, so the performance summary
        // printed "Fastest: 0ms" and "Slowest: 0ms" for runs nobody timed.
        var result = Stochastic(Scored("a", 90), Scored("b", 90), Scored("c", 90));

        Assert.Null(result.FastestRun);
        Assert.Null(result.SlowestRun);
    }

    [Fact]
    public void FastestAndSlowestRun_AreTheTimedExtremes()
    {
        // Guard.
        var result = Stochastic(Timed("a", 2), Errored("b"), Timed("c", 1));

        Assert.Equal("c", result.FastestRun!.Value.Result.TestName);
        Assert.Equal("a", result.SlowestRun!.Value.Result.TestName);
    }

    [Fact]
    public void PrintPerformanceSummary_SaysNA_WhenNoRunWasTimed()
    {
        // With DurationStats null the range line says so; no fastest or slowest run is made up.
        var result = Stochastic(Scored("a", 90), Scored("b", 90), Scored("c", 90));
        using var writer = new StringWriter();

        TableFormatter.PrintPerformanceSummary(result, new OutputOptions { Writer = writer, UseColors = false });

        var output = writer.ToString();
        Assert.Contains("Range:   N/A (no run recorded a duration)", output);
        Assert.DoesNotContain("Fastest", output);   // today "[FAST] Fastest: 0ms"
        Assert.DoesNotContain("Slowest", output);
    }

    [Fact]
    public void TheComparisonTable_SaysNA_ForAModelWithNoTimedRun()
    {
        // Guard: the duration columns (Model | Pass% | Score | Dur(ms) | DurVar | ...) read N/A, never a 0 ms mean.
        var untimed = Stochastic(Scored("a", 90), Scored("b", 90), Scored("c", 90));
        var timed = Stochastic(Timed("a", 1), Timed("b", 1), Timed("c", 1));

        var table = TableFormatter.ToComparisonTableString(
            [("Untimed", untimed), ("Timed", timed)], new OutputOptions { UseColors = false });

        var untimedCells = table.Split('\n').Single(l => l.Contains("Untimed", StringComparison.Ordinal)).Split('|');
        var timedCells = table.Split('\n').Single(l => l.Contains("Timed", StringComparison.Ordinal)).Split('|');
        Assert.Equal("N/A", untimedCells[3].Trim());
        Assert.Equal("N/A", untimedCells[4].Trim());
        Assert.Equal("1000", timedCells[3].Trim());
    }

    [Fact]
    public void TheTable_ShowsARecordedZeroTokenAndCostRow()
    {
        // Today: a "> 0" check dropped the rows, as if no run had reported tokens or a price.
        var run = new TestResult
        {
            TestName = "t", Score = 90, Passed = true,
            Performance = new PerformanceMetrics
            {
                StartTime = T0, EndTime = T0.AddSeconds(1), PromptTokens = 0, CompletionTokens = 0, EstimatedCost = 0m,
            },
        };
        var result = Stochastic(run, run, run);

        var table = TableFormatter.ToTableString(result, "t", new OutputOptions { UseColors = false });

        Assert.Contains("Total Tokens", table);
        Assert.Contains("Cost ($)", table);
    }

    [Fact]
    public void TheTable_PassRateRow_CountsEveryRun()
    {
        // The pass rate is over every run (an errored one counts against it), so its Samples column is every run. Today it
        // showed the measured count, 2, beside a rate taken over 3.
        var result = Stochastic(Scored("a", 90), Errored("b"), Scored("c", 90));

        var table = TableFormatter.ToTableString(result, "t", new OutputOptions { UseColors = false });

        var row = table.Split('\n').Single(l => l.Contains("Pass Rate", StringComparison.Ordinal));
        Assert.EndsWith(" 3", row.TrimEnd());
    }

    // ── The CLI stochastic export and model comparison ────────────────────────

    private static Task<StochasticResult> RunAsync(params TestResult[] runs) =>
        new StochasticRunner(new ScriptedHarness(runs)).RunStochasticTestAsync(
            new NamedAgent("agent"), new TestCase { Name = "refund-policy", Input = "q" },
            new StochasticOptions(Runs: runs.Length, SuccessRateThreshold: 0.6));

    [Fact]
    public async Task StochasticReport_NothingMeasured_HasNoScoreOrDuration()
    {
        // Today: OverallScore 0, the test's Score 0 and DurationMs 0.
        var result = await RunAsync(Errored("a"), Errored("b"), Errored("c"));

        var report = StochasticReport.Build([result], "BatchEvaluation", 3, 0.6, T0, T0.AddSeconds(3));

        Assert.Null(report.OverallScore);
        var test = Assert.Single(report.TestResults);
        Assert.Null(test.Score);
        Assert.Null(test.DurationMs);
        Assert.True(test.NotMeasured);
    }

    [Fact]
    public async Task StochasticReport_MeasuredRuns_KeepTheirMeanAndTime()
    {
        // Guard.
        var result = await RunAsync(Timed("a", 1, score: 80), Timed("b", 2, score: 90), Errored("c"));

        var report = StochasticReport.Build([result], "BatchEvaluation", 3, 0.6, T0, T0.AddSeconds(3));

        var test = Assert.Single(report.TestResults);
        Assert.Equal(85.0, test.Score);
        Assert.Equal(3000L, test.DurationMs);
        Assert.Equal(85.0, report.OverallScore);
    }

    [Fact]
    public async Task ModelResult_HasNoMeanOrSpread_WhenNoRunWasMeasured()
    {
        // Today: MeanScore 0 and StandardDeviation 0, a perfectly consistent model that scored nothing.
        var result = await RunAsync(Errored("a"), JudgeFailed("b"), Errored("c"));

        var model = new ModelResult("m", "M", result, AverageLatency: null, AverageCost: null, TotalCost: null);

        Assert.Null(model.MeanScore);
        Assert.Null(model.StandardDeviation);
    }

    [Fact]
    public async Task ModelResult_KeepsTheMeanOfTheMeasuredRuns()
    {
        // Guard.
        var result = await RunAsync(Scored("a", 80), Scored("b", 90), Errored("c"));

        var model = new ModelResult("m", "M", result, AverageLatency: null, AverageCost: null, TotalCost: null);

        Assert.Equal(85.0, model.MeanScore);
        Assert.NotNull(model.StandardDeviation);
    }

    /// <summary>Hands out the scripted run results in order; the agent is never called.</summary>
    private sealed class ScriptedHarness(IReadOnlyList<TestResult> runs) : IEvaluationHarness
    {
        private int _next;

        public Task<TestResult> RunEvaluationAsync(
            IEvaluableAgent agent, TestCase testCase, EvaluationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(runs[Interlocked.Increment(ref _next) - 1]);
    }

    private sealed class NamedAgent(string name) : IEvaluableAgent
    {
        public string Name => name;

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the scripted harness never calls the agent");
    }
}
