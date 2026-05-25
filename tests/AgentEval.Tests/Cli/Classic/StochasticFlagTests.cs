// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/StochasticFlagTests.cs
// during the v1.1 CLI consolidation. Only the namespace was edited.

using AgentEval.Cli.Commands;
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

    [Fact]
    public void EvalOptions_Runs0_IsNotStochastic()
    {
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Runs = 0,
        };

        Assert.True(opts.Runs <= 1, "Runs=0 should NOT trigger stochastic evaluation path");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    [InlineData(int.MinValue)]
    public void EvalOptions_NegativeRuns_IsNotStochastic(int runs)
    {
        // Negative --runs values are not validated by the CLI; they fall through
        // to the standard single-run path (Runs <= 1). This documents that behavior.
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Runs = runs,
        };

        Assert.True(opts.Runs <= 1, $"Runs={runs} should NOT trigger stochastic evaluation path");
    }

    [Fact]
    public void EvalOptions_StochasticPath_DoesNotSupportExport()
    {
        // Documents the known gap: the stochastic path (--runs > 1) writes
        // statistics to stderr only. --format/--output are NOT used for
        // stochastic results. When this is fixed, this test should be updated
        // to verify export support.
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "json",
            Output = new FileInfo("results.json"),
            Runs = 5,
        };

        // The stochastic path is triggered but export flags are ignored.
        // Verify the options can express this combination (they can, but
        // ExecuteStochasticAsync does not use Format/Output).
        Assert.True(opts.Runs > 1);
        Assert.NotNull(opts.Output);
        Assert.Equal("json", opts.Format);
    }
}
