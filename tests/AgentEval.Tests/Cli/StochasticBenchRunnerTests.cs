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
    public async Task EveryRunPassing_IsAPass()
    {
        var result = await Run("pass", "pass", "pass");

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
    }
}
