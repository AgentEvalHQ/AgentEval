// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/StochasticFlagTests.cs
// during the v1.1 CLI consolidation. The namespace was edited at port time; the tests that documented
// unvalidated --runs values and the stochastic path's silent export drop were rewritten when both were fixed.

using AgentEval.Cli.Commands;
using AgentEval.Comparison;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// Tests for the --runs and --success-threshold stochastic evaluation flags (Item 5).
/// </summary>
public class StochasticFlagTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // EVAL OPTIONS DEFAULTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EvalOptions_Runs_DefaultsTo1()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
        };

        Assert.Equal(1, opts.Runs);
    }

    [Fact]
    public void EvalOptions_SuccessThreshold_DefaultsTo0Point8()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
        };

        Assert.Equal(0.8, opts.SuccessThreshold);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EVAL OPTIONS SETTING
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EvalOptions_Runs_CanBeSet()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Runs = 10,
        };

        Assert.Equal(10, opts.Runs);
    }

    [Fact]
    public void EvalOptions_SuccessThreshold_CanBeSet()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            SuccessThreshold = 0.95,
        };

        Assert.Equal(0.95, opts.SuccessThreshold);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public void EvalOptions_Runs_VariousValues(int runs)
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Runs = runs,
        };

        Assert.Equal(runs, opts.Runs);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.95)]
    [InlineData(1.0)]
    public void EvalOptions_SuccessThreshold_VariousValues(double threshold)
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            SuccessThreshold = threshold,
        };

        Assert.Equal(threshold, opts.SuccessThreshold);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMMAND OPTION VERIFICATION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EvalCommand_HasRunsOption()
    {
        var command = EvalCommand.Create();

        var runsOption = command.Options.FirstOrDefault(o =>
            o.Name.Contains("runs", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(runsOption);
        Assert.Contains("stochastic", runsOption.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvalCommand_HasSuccessThresholdOption()
    {
        var command = EvalCommand.Create();

        var thresholdOption = command.Options.FirstOrDefault(o =>
            o.Name.Contains("success", StringComparison.OrdinalIgnoreCase) ||
            o.Name.Contains("threshold", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(thresholdOption);
        Assert.Contains("threshold", thresholdOption.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvalCommand_RunsOption_IsIntType()
    {
        var command = EvalCommand.Create();
        var runsOption = command.Options.First(o =>
            o.Name.Contains("runs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(typeof(int), runsOption.ValueType);
    }

    [Fact]
    public void EvalCommand_SuccessThresholdOption_IsDoubleType()
    {
        var command = EvalCommand.Create();
        var thresholdOption = command.Options.First(o =>
            o.Name.Contains("success", StringComparison.OrdinalIgnoreCase) ||
            o.Name.Contains("threshold", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(typeof(double), thresholdOption.ValueType);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // STOCHASTIC MODE DETERMINATION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EvalOptions_StochasticFlagsCombine()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Runs = 20,
            SuccessThreshold = 0.95,
        };

        Assert.Equal(20, opts.Runs);
        Assert.Equal(0.95, opts.SuccessThreshold);
    }

    [Fact]
    public void EvalOptions_SingleRun_IsNotStochastic()
    {
        // When Runs = 1, we should use the standard eval path (not stochastic)
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Runs = 1,
        };

        // Stochastic mode only activates when Runs > 1
        Assert.Equal(1, opts.Runs);
        Assert.True(opts.Runs <= 1, "Runs=1 should use standard eval path");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    public void EvalOptions_MultipleRuns_IndicatesStochastic(int runs)
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Runs = runs,
        };

        Assert.True(opts.Runs > 1, $"Runs={runs} should trigger stochastic evaluation path");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    [InlineData(int.MinValue)]
    public void ValidateRuns_BelowOne_IsRejected(int runs)
    {
        // These used to fall through to the single-run path unvalidated (0 and negatives silently ran once).
        // ExecuteAsync now turns this message into exit 2 before anything else runs — see
        // EvalCommandTemperatureAndRunsTests.Eval_RunsBelowOne_IsAUsageError_BeforeAnythingElseIsChecked.
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Runs = runs,
        };

        var error = EvalCommand.ValidateRuns(opts);

        Assert.NotNull(error);
        Assert.Contains("--runs must be at least 1", error);
    }

    [Fact]
    public void ValidateRuns_SingleRun_IgnoresTheStochasticThreshold()
    {
        // At --runs 1 the threshold is not used, so it is not checked either.
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Format = "json",
            Runs = 1,
            SuccessThreshold = 5.0,
        };

        Assert.Null(EvalCommand.ValidateRuns(opts));
    }

    [Fact]
    public void StochasticReport_HasOneEntryPerTestCase_WithTheStochasticVerdictAndItsRuns()
    {
        // 3 of 5 runs passed against an 80% threshold: the test fails as a whole, and every format can see why.
        var runs = new[] { 90, 85, 40, 88, 30 }
            .Select((score, i) => new TestResult { TestName = "t", Score = score, Passed = score >= 50 })
            .ToList();
        var result = new StochasticResult(
            new TestCase { Name = "refund-policy", Input = "q" },
            runs,
            new StochasticStatistics(PassRate: 0.6, MeanScore: 66.6, MedianScore: 85, StandardDeviation: 28.1,
                MinScore: 30, MaxScore: 90, Percentile25: 40, Percentile75: 88, Percentile95: 90,
                ConfidenceInterval: new ConfidenceInterval(31.7, 101.5, 0.95), SampleSize: 5),
            new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8),
            Passed: false);

        var report = StochasticReport.Build(
            [result], "BatchEvaluation", runs: 5, successThreshold: 0.8, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            agentName: "gpt-4o", modelName: "gpt-4o");

        var test = Assert.Single(report.TestResults);
        Assert.Equal("refund-policy", test.Name);
        Assert.False(test.Passed);
        Assert.Equal(66.6, test.Score);
        Assert.Equal("3 of 5 runs passed (60.0%), below the 80% threshold.", test.Error);
        Assert.Equal(5, test.MetricScores[StochasticReport.RunsMetric]);
        Assert.Equal(3, test.MetricScores[StochasticReport.RunsPassedMetric]);
        Assert.Equal(60, test.MetricScores[StochasticReport.PassRateMetric], precision: 6);
        Assert.Equal(28.1, test.MetricScores[StochasticReport.ScoreSdMetric]);
        Assert.Contains("95% CI for the mean [31.7, 101.5]", test.Output);
        Assert.Contains("Run 3: fail, score 40", test.Output);

        Assert.Equal(1, report.FailedTests);
        Assert.Equal("BatchEvaluation (stochastic, 5 runs per test)", report.Name);
        Assert.Equal("5", report.Metadata["RunsPerTest"]);
        Assert.Equal("0.8", report.Metadata["SuccessThreshold"]);
    }

    [Fact]
    public void StochasticReport_AnErroredRun_IsListedWithItsError()
    {
        var runs = new List<TestResult>
        {
            new() { TestName = "t", Score = 90, Passed = true },
            new() { TestName = "t", Score = 0, Passed = false, Error = new TimeoutException("the model timed out") },
            new() { TestName = "t", Score = 92, Passed = true },
        };
        var result = new StochasticResult(
            new TestCase { Name = "t", Input = "q" },
            runs,
            new StochasticStatistics(PassRate: 2.0 / 3, MeanScore: 60.7, MedianScore: 90, StandardDeviation: 52.6,
                MinScore: 0, MaxScore: 92, Percentile25: 45, Percentile75: 91, Percentile95: 92,
                ConfidenceInterval: null, SampleSize: 3),
            new StochasticOptions(Runs: 3, SuccessRateThreshold: 0.6),
            Passed: true);

        var test = Assert.Single(StochasticReport.Build(
            [result], "BatchEvaluation", 3, 0.6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow).TestResults);

        Assert.True(test.Passed);
        Assert.Null(test.Error);
        Assert.Contains("Run 2: error: the model timed out", test.Output);
        Assert.DoesNotContain("CI for the mean", test.Output);   // no interval was computed, none is printed
    }
}
