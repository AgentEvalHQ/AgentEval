// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli/tests/AgentEval.Cli.Tests/StochasticFlagTests.cs
// during the v1.1 CLI consolidation. The namespace was edited at port time; the tests that documented
// unvalidated --runs values and the stochastic path's silent export drop were rewritten when both were fixed.

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
    public void StochasticPath_DoesNotExport_AndNamesEveryExportOptionItIgnores()
    {
        // The stochastic path (--runs > 1) still writes no export: no exporter accepts a stochastic result, and
        // projecting one into EvaluationReport would read as a single run in the formats that drop its metadata.
        // What changed is that it says so, naming each option with the value given.
        var opts = new EvalOptions
        {
            Dataset = new FileInfo("test.yaml"),
            Model = "gpt-4o",
            Format = "csv",
            Output = new FileInfo("results.csv"),
            OutputDir = new DirectoryInfo("out"),
            Runs = 5,
        };

        var ignored = EvalCommand.ExportOptionsIgnoredByStochasticMode(opts);

        Assert.Equal(
            new[]
            {
                "--format csv",
                $"-o/--output {opts.Output!.FullName}",
                $"--output-dir {opts.OutputDir!.FullName}",
            },
            ignored.ToArray());
    }

    [Fact]
    public void StochasticPath_DefaultFormat_IsNamedOnlyWhenGivenExplicitly()
    {
        var defaulted = new EvalOptions { Dataset = new FileInfo("test.yaml"), Format = "json", Runs = 5 };
        var explicitJson = new EvalOptions { Dataset = new FileInfo("test.yaml"), Format = "json", FormatGiven = true, Runs = 5 };

        Assert.Empty(EvalCommand.ExportOptionsIgnoredByStochasticMode(defaulted));
        Assert.Equal(new[] { "--format json" }, EvalCommand.ExportOptionsIgnoredByStochasticMode(explicitJson).ToArray());
    }
}
