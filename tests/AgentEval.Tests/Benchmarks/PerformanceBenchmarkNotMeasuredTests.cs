// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// An agent whose calls fail did not produce a latency or a cost: the P99 of no returned call is not a perfect pass,
/// and the $0 of no response is not a cheap one (T13-7).
/// </summary>
public class PerformanceBenchmarkNotMeasuredTests
{
    private static PerformanceBenchmark Benchmark(IEvaluableAgent agent, int latencyIterations = 1) =>
        new(agent, new PerformanceBenchmarkOptions
        {
            Verbose = false,
            DelayBetweenIterations = TimeSpan.Zero,
            EvaluateOptions = new PerformanceBenchmarkEvaluateOptions
            {
                P99LatencyThresholdMs = 60_000,
                LatencyIterationsPerPrompt = latencyIterations,
                WarmupIterations = 0,
                MinThroughputRps = 0.01,
                ThroughputConcurrentRequests = 1,
                ThroughputDuration = TimeSpan.FromMilliseconds(50),
                MaxCostUSD = 1.0,
                CompositePassThreshold = 0.6,
                CostModelName = "gpt-4o",   // a priced model, so only the failed calls can leave cost unmeasured
            },
        });

    [Fact]
    public async Task EveryCallFails_LatencyIsAnError_NotAPerfectPass()
    {
        var result = await Benchmark(new FailingAgent(failFirst: int.MaxValue)).EvaluateAsync(new EvalInput("hi"));

        var latency = result.Details.SubResults!.Single(r => r.Metric.Key == "perf_latency");
        Assert.Equal("error", latency.Score.Label);
        Assert.False(latency.Score.Passed);
        Assert.False(latency.Score.CountsTowardAggregate());
        Assert.Equal(MeasurementState.NotMeasured, latency.Score.Measurement);
        Assert.Equal(1, latency.Details.Dimensions!["errors"]);
        Assert.False(result.Details.Dimensions!.ContainsKey("p99_ms"));
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task EveryCallFails_CostIsNotMeasured_NotAZeroDollarPass()
    {
        var result = await Benchmark(new FailingAgent(failFirst: int.MaxValue)).EvaluateAsync(new EvalInput("hi"));

        var cost = result.Details.SubResults!.Single(r => r.Metric.Key == "perf_cost");
        Assert.Equal("skipped", cost.Score.Label);
        Assert.False(cost.Score.Passed);
        Assert.False(cost.Details.Dimensions!.ContainsKey("cost_usd"));
        Assert.False(result.Details.Dimensions!.ContainsKey("cost_usd"));
        Assert.Contains("none of", cost.Details.Recommendations![0]);
    }

    [Fact]
    public async Task SomeIterationsFail_LatencyWithholdsItsPass_AndCountsTheErrors()
    {
        // The first two of three latency calls fail; the third returns at once, well inside the budget.
        var result = await Benchmark(new FailingAgent(failFirst: 2), latencyIterations: 3).EvaluateAsync(new EvalInput("hi"));

        var latency = result.Details.SubResults!.Single(r => r.Metric.Key == "perf_latency");
        Assert.NotEqual("pass", latency.Score.Label);
        Assert.False(latency.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, latency.Score.Measurement);
        Assert.Equal(2, latency.Details.Dimensions!["errors"]);
        Assert.Equal(1, latency.Details.Dimensions["successful"]);

        // The composite cannot pass on a latency it did not measure.
        Assert.False(result.Score.Passed);
        Assert.Equal("warn", result.Score.Label);
    }

    [Fact]
    public async Task NoCallFails_LatencyStillPasses()
    {
        // Guard: a clean run is measured and passes as before, with no error counts added.
        var result = await Benchmark(new FailingAgent(failFirst: 0), latencyIterations: 2).EvaluateAsync(new EvalInput("hi"));

        var latency = result.Details.SubResults!.Single(r => r.Metric.Key == "perf_latency");
        Assert.Equal("pass", latency.Score.Label);
        Assert.Equal(MeasurementState.Measured, latency.Score.Measurement);
        Assert.False(latency.Details.Dimensions!.ContainsKey("errors"));
        Assert.True(result.Details.Dimensions!.ContainsKey("p99_ms"));
    }

    /// <summary>Fails its first <c>failFirst</c> calls (an unreachable endpoint), then answers with usage.</summary>
    private sealed class FailingAgent(int failFirst) : IEvaluableAgent
    {
        private int _calls;

        public string Name => "FailingAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) <= failFirst)
            {
                return Task.FromException<AgentResponse>(new HttpRequestException("503 Service Unavailable"));
            }

            return Task.FromResult(new AgentResponse
            {
                Text = "ok",
                ModelId = "gpt-4o",
                TokenUsage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 },
            });
        }
    }
}
