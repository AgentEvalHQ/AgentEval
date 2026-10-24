// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Cli.Commands;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Comparison;

public sealed class StochasticStatisticsNullableTests
{
    private static readonly TestCase Case = new() { Name = "statistics", Input = "q" };
    private static TestResult Measured(int score) => new() { TestName = "t", Score = score, Passed = true };
    private static TestResult Missing() => new() { TestName = "t", JudgeFailed = true };
    private static Task<StochasticResult> Run(bool analysis, params TestResult[] runs) =>
        new StochasticRunner(new ScriptedHarness(runs)).RunStochasticTestAsync(
            new UnusedAgent(), Case, new StochasticOptions(Runs: runs.Length, EnableStatisticalAnalysis: analysis));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_NoMeasuredRuns_HasNoStatistics(bool analysis)
    {
        var result = await Run(analysis, Missing(), Missing(), Missing());
        Assert.Null(result.Statistics);
        Assert.Contains("N/A", TableFormatter.ToTableString(result, "t", new OutputOptions { UseColors = false }));
    }

    [Fact]
    public async Task Run_OneMeasuredRun_HasMeanButNoSpreadOrInterval()
    {
        var result = await Run(true, Measured(90), Missing(), Missing());
        Assert.NotNull(result.Statistics);
        Assert.Equal(90, result.Statistics.MeanScore);
        Assert.Null(result.Statistics.StandardDeviation);
        Assert.Null(result.Statistics.ConfidenceInterval);
    }

    [Fact]
    public async Task Run_AnalysisDisabled_LeavesUncomputedStatisticsAbsent()
    {
        var result = await Run(false, Measured(50), Measured(90), Measured(70));
        Assert.NotNull(result.Statistics);
        Assert.Equal(70, result.Statistics.MeanScore);
        Assert.Null(result.Statistics.StandardDeviation);
        Assert.Null(result.Statistics.Percentile25);
        Assert.Null(result.Statistics.Percentile75);
        Assert.Null(result.Statistics.Percentile95);
        Assert.Null(result.Statistics.ConfidenceInterval);
    }

    [Theory]
    [InlineData("sd")]
    [InlineData("cv")]
    [InlineData("p95")]
    [InlineData("ci")]
    public async Task Assert_AnalysisDisabled_FailsAsNotMeasured(string assertion)
    {
        var result = await Run(false, Measured(50), Measured(90), Measured(70));
        var check = new StochasticAssertions(result);
        var error = Assert.Throws<StochasticAssertionException>(() =>
        {
            switch (assertion)
            {
                case "sd": check.HaveStandardDeviationAtMost(100, "release gate"); break;
                case "cv": check.HaveCoefficientOfVariationAtMost(1, "release gate"); break;
                case "p95": check.HavePercentile95AtLeast(0, "release gate"); break;
                default: check.HaveConfidenceIntervalLowerBoundAtLeast(0, "release gate"); break;
            }
        });
        Assert.Contains("not measured", error.Message);
        Assert.Contains("release gate", error.Message);
    }

    private static StochasticResult Empty() => new(Case, [],
        new StochasticStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, null, 1), new(), false);

    [Fact]
    public void Assert_NoRuns_FailsAsNotMeasuredEvenWithSuppliedStatistics()
    {
        var error = Assert.Throws<StochasticAssertionException>(() =>
            new StochasticAssertions(Empty()).HaveMeanScoreAtLeast(0));
        Assert.Contains("not measured", error.Message);
    }

    [Fact]
    public void Model_NoRuns_HasNoPassRate() =>
        Assert.Null(new ModelResult("m", "m", Empty(), null, null, null).PassRate);

    [Fact]
    public void Report_NoRuns_OmitsPassRate()
    {
        var report = Report(Empty());
        Assert.False(report.TestResults.Single().MetricScores!.ContainsKey(StochasticReport.PassRateMetric));
        Assert.Null(report.TestResults.Single().Score);
        Assert.Null(report.OverallScore);
        Assert.Contains("n/a", report.TestResults.Single().Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CreateStatistics_MismatchedSamples_RejectsInvalidPassRate(int count)
    {
        Assert.Throws<ArgumentException>(() => StatisticsCalculator.CreateStatistics(
            Enumerable.Repeat(90, count).ToArray(), Enumerable.Repeat(true, count + 1).ToArray()));
    }

    [Fact]
    public void Assert_MissingStatisticsWithMeasuredRuns_FailsAsNotMeasured()
    {
        var result = new StochasticResult(Case, [Measured(90)], null, new(), false);
        var error = Assert.Throws<StochasticAssertionException>(() =>
            new StochasticAssertions(result).HaveMeanScoreAtLeast(0, "release gate"));
        Assert.Contains("not measured", error.Message);
        Assert.Contains("release gate", error.Message);
    }

    [Fact]
    public async Task Report_OneMeasuredRun_OmitsSpreadAndZeroInterval()
    {
        var result = await Run(true, Measured(90), Missing(), Missing());
        var test = Report(result).TestResults.Single();
        Assert.False(test.MetricScores!.ContainsKey(StochasticReport.ScoreSdMetric));
        Assert.DoesNotContain("[0.0, 0.0]", test.Output);
        var comparison = new ModelComparisonResult(Case,
            [new ModelResult("m", "m", result, null, null, null)], [], null!, new());
        Assert.DoesNotContain("[0.00, 0.00]", comparison.ToStatisticsTable());
        Assert.Contains("n/a", comparison.ToStatisticsTable());
    }

    private static EvaluationReport Report(StochasticResult result) => StochasticReport.Build(
        [result], "suite", result.IndividualResults.Count, 0.8, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void Table_IndependentMetricWithoutOverallScore_KeepsItsOwnSampleCount()
    {
        var run = new TestResult
        {
            TestName = "t", JudgeFailed = true,
            MetricResults = [MetricResult.Pass("code_example", 80)],
        };
        var result = new StochasticResult(Case, [run], null, new(), false);
        var table = TableFormatter.ToTableString(result, "t", new OutputOptions { UseColors = false });
        var row = table.Split('\n').Single(line => line.Contains("code_example", StringComparison.Ordinal));
        Assert.EndsWith("1", row.Trim());
        Assert.Contains("80.00", row);
    }

    [Theory]
    [InlineData("mean")]
    [InlineData("median")]
    [InlineData("percentile")]
    [InlineData("passRate")]
    [InlineData("statistics")]
    [InlineData("distribution")]
    [InlineData("factory")]
    public void Calculate_EmptySample_HasNoReading(string statistic)
    {
        IStatisticsCalculator calculator = new DefaultStatisticsCalculator();
        object? value = statistic switch
        {
            "mean" => calculator.Mean([]),
            "median" => calculator.Median([]),
            "percentile" => calculator.Percentile([], 95),
            "passRate" => calculator.CalculatePassRate([]),
            "statistics" => calculator.CreateStatistics([], []),
            "distribution" => calculator.CreateDistribution(Array.Empty<double>()),
            _ => DistributionStatisticsFactory.Create(Array.Empty<double>()),
        };
        Assert.Null(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Calculate_InsufficientSample_HasNoSpreadOrInterval(int count)
    {
        var sample = Enumerable.Repeat(90.0, count).ToArray();
        Assert.Null(StatisticsCalculator.StandardDeviation(sample));
        Assert.Null(StatisticsCalculator.CalculateConfidenceInterval(sample));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Statistics_ZeroSampleSize_IsRejectedByConstructionAndCopy(bool copy)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => copy
            ? new StochasticStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, null, 1) with { SampleSize = 0 }
            : new StochasticStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => copy
            ? new DistributionStatistics(0, 0, 0, 0, 0, 0, 0, 1) with { SampleSize = 0 }
            : new DistributionStatistics(0, 0, 0, 0, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70)]
    public async Task Run_MeasuredRepeatedValues_PreservesZeroSpread(int value)
    {
        var result = await Run(true, Measured(value), Measured(value), Measured(value));
        Assert.NotNull(result.Statistics);
        Assert.Equal(value, result.Statistics.MeanScore);
        Assert.Equal(0, result.Statistics.StandardDeviation);
        Assert.NotNull(result.Statistics.ConfidenceInterval);
        Assert.Equal(value, result.Statistics.ConfidenceInterval.Lower);
    }

    private sealed class ScriptedHarness(IReadOnlyList<TestResult> runs) : IEvaluationHarness
    {
        private int _next;
        public Task<TestResult> RunEvaluationAsync(IEvaluableAgent agent, TestCase testCase,
            EvaluationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(runs[Interlocked.Increment(ref _next) - 1]);
    }

    private sealed class UnusedAgent : IEvaluableAgent
    {
        public string Name => "offline";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The scripted harness never invokes the agent.");
    }
}
