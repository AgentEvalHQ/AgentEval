// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>bench gdpr --runs N</c> (#203 review B6c-1, found by the mid-branch review). The verdict came from the majority
/// vote's SEVERITY, not its label: when no run counted (every run errored, or withheld its pass) the vote returned
/// <c>(0, "none")</c> and the runner reported PASS and exited 0; a majority of fail votes at medium severity read WARN.
/// </summary>
public class StochasticBenchRunnerTests
{
    // A leaf whose verdict for each successive run is scripted.
    private sealed class Scripted(params string[] perRun) : IEval
    {
        private int _run;
        public string Key => "leaf";
        public string Name => "leaf";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
        {
            var label = perRun[Math.Min(_run++, perRun.Length - 1)];
            if (label == "skipped")
                return Task.FromResult(EvalResult.Skipped(this, "did not run"));
            var value = label switch { "pass" => 1.0, "fail" => 0.5, _ => 0.0 };
            return Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version),
                new EvalScore(value, null, label, label == "pass", null, label == "fail" ? "medium" : "none", null),
                new(null, null, null, null, null),
                new("atomic-code", null, null, null, null, 0, false),
                DateTimeOffset.UtcNow));
        }
    }

    // A passing leaf whose cost for each successive run is scripted; null is a run whose judge was not priced.
    private sealed class Costed(params double?[] perRun) : IEval
    {
        private int _run;
        public string Key => "leaf";
        public string Name => "leaf";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version),
                new EvalScore(1.0, null, "pass", true, null, "none", null),
                new(null, null, null, null, null),
                new("atomic-llm", "judge", null, null, null, perRun[Math.Min(_run++, perRun.Length - 1)], false),
                DateTimeOffset.UtcNow));
    }

    private static Task<EvalResult> RunCosted(params double?[] perRun)
    {
        var benchmark = new CompositeEval("gdpr.test", "Test", "compliance.gdpr", "1.0.0",
            [new EvalComponent(new Costed(perRun), 1.0)], WeightedSumAggregation.Instance, threshold: 0.8);
        return StochasticBenchRunner.RunNAsync(new NullOutputStore(), new SubjectIdentity(SubjectKind.Agent, "s"),
            benchmark, new EvalInput("q", "r"), runs: perRun.Length);
    }

    [Fact]
    public async Task Cost_EveryRunPriced_IsTheSumOfTheRuns()
    {
        var result = await RunCosted(0.01, 0.02, 0.03);

        Assert.Equal(0.06, result.Provenance.EstimatedCost!.Value, precision: 10);
        Assert.Equal(0.06, StochasticBenchRunner.RollupTree(result.Details.SubResults!).Provenance.EstimatedCost!.Value, precision: 10);
    }

    [Fact]
    public async Task Cost_ARunNotPriced_MakesTheTotalNotPriced()
    {
        // Complete or absent: summing the priced runs read 0.02 as the cost of three runs.
        var result = await RunCosted(0.01, null, 0.01);

        Assert.Null(result.Provenance.EstimatedCost);
        Assert.Null(StochasticBenchRunner.RollupTree(result.Details.SubResults!).Provenance.EstimatedCost);
    }

    private static Task<EvalResult> Run(params string[] perRun)
    {
        var benchmark = new CompositeEval("gdpr.test", "Test", "compliance.gdpr", "1.0.0",
            [new EvalComponent(new Scripted(perRun), 1.0)], WeightedSumAggregation.Instance, threshold: 0.8);
        return StochasticBenchRunner.RunNAsync(new NullOutputStore(), new SubjectIdentity(SubjectKind.Agent, "s"),
            benchmark, new EvalInput("q", "r"), runs: perRun.Length);
    }

    [Fact]
    public async Task EveryRunErrored_IsAnError_NotAPass()
    {
        var result = await Run("error", "error", "error");

        Assert.Equal("error", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
    }

    [Fact]
    public async Task EveryRunSkipped_IsSkipped_NotAPass()
    {
        var result = await Run("skipped", "skipped", "skipped");

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task AMajorityOfFails_IsAFail_WhateverTheirSeverity()
    {
        // The fail votes carry "medium": the old severity-to-label mapping read that as warn.
        var result = await Run("fail", "fail", "pass");

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task APassThatRestsOnSomeOfTheRuns_IsAWarn_AndSaysSo()
    {
        var result = await Run("pass", "error", "error");

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains("2 of 3", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollupTree_PartialPass_PreservesWarningAtEveryNode()
    {
        var result = await Run("pass", "error", "error");
        var rollup = StochasticBenchRunner.RollupTree(result.Details.SubResults!);

        Assert.Equal("gdpr.test", rollup.Metric.Key);
        Assert.Equal(result.Score, rollup.Score);
        var child = Assert.Single(rollup.Details.SubResults!);
        Assert.Equal("leaf", child.Metric.Key);
        Assert.Equal("warn", child.Score.Label);
        Assert.Equal(1, child.Score.Value);
        Assert.Null(child.Provenance.JudgeModel);
    }

    [Fact]
    public async Task RollupTree_AllErrors_WithholdsEveryScore()
    {
        var result = await Run("error", "error");
        var rollup = StochasticBenchRunner.RollupTree(result.Details.SubResults!);

        Assert.Equal("error", rollup.Score.Label);
        Assert.Equal(MeasurementState.NotMeasured, rollup.Score.Measurement);
        Assert.Equal(MeasurementState.NotMeasured, Assert.Single(rollup.Details.SubResults!).Score.Measurement);
    }

    [Fact]
    public async Task EveryRunPassing_IsAPass()
    {
        var result = await Run("pass", "pass", "pass");

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }
}
