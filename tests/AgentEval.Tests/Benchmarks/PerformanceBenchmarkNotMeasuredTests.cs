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

    [Fact]
    public async Task EveryCallFails_TheCostResultIsNotMeasured_NotZero()
    {
        // The leaf was already skipped, but the public result still said $0 for a run that priced nothing.
        var cost = await Benchmark(new FailingAgent(failFirst: int.MaxValue)).RunCostBenchmarkAsync(["hi", "there"], "gpt-4o");

        Assert.Equal(0, cost.SuccessfulPrompts);
        Assert.Null(cost.EstimatedCostUSD);
    }

    [Fact]
    public async Task ResponsesWithoutUsage_CostIsNotMeasured_NotAZeroDollarPass()
    {
        // T13-7 item 3: a priced model whose responses carry no usage priced 0 tokens: $0, and the cost leaf read PASS 1.0.
        var agent = new NoUsageAgent();

        var cost = await Benchmark(agent).RunCostBenchmarkAsync(["hi"], "gpt-4o");
        var result = await Benchmark(agent).EvaluateAsync(new EvalInput("hi"));

        Assert.Null(cost.EstimatedCostUSD);
        Assert.Equal(1, cost.ResponsesWithoutUsage);
        var leaf = result.Details.SubResults!.Single(r => r.Metric.Key == "perf_cost");
        Assert.Equal("skipped", leaf.Score.Label);
        Assert.False(leaf.Score.Passed);
        Assert.False(leaf.Score.CountsTowardAggregate());
        Assert.False(leaf.Details.Dimensions!.ContainsKey("cost_usd"));
        Assert.Contains("reported no token usage", leaf.Details.Recommendations![0]);
    }

    [Fact]
    public async Task UsageReported_CostIsMeasured()
    {
        // Guard: a response with usage on a priced model is priced, and the leaf scores it.
        var cost = await Benchmark(new FailingAgent(failFirst: 0)).RunCostBenchmarkAsync(["hi"], "gpt-4o");
        var result = await Benchmark(new FailingAgent(failFirst: 0)).EvaluateAsync(new EvalInput("hi"));

        Assert.NotNull(cost.EstimatedCostUSD);
        Assert.Equal(0, cost.ResponsesWithoutUsage);
        var leaf = result.Details.SubResults!.Single(r => r.Metric.Key == "perf_cost");
        Assert.NotEqual("skipped", leaf.Score.Label);
        Assert.True(leaf.Details.Dimensions!.ContainsKey("cost_usd"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task RunCostBenchmarkAsync_FailedSibling_LeavesTotalUnknownAndRetainsObservations(int tokens)
    {
        var result = await Benchmark(new MixedCostAgent(tokens)).RunCostBenchmarkAsync(["ok", "fails"], "gpt-4o");

        Assert.Null(result.EstimatedCostUSD);
        Assert.Equal(2, result.TotalPrompts);
        Assert.Equal(1, result.SuccessfulPrompts);
        Assert.Single(result.Errors);
        Assert.Equal(tokens * 2, result.TotalTokens);
        Assert.Contains("observed", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task EvaluateAsync_CostStageFailure_WithholdsLeafAndCompositePass(int tokens)
    {
        // Latency sees each prompt once; throughput uses only "ok". Only the later cost-stage visit fails.
        var result = await Benchmark(new MixedCostAgent(tokens, failOnVisit: 2)).EvaluateAsync(
            new EvalInput("ignored", Metadata: new Dictionary<string, object?> { ["prompts"] = new[] { "ok", "fails" } }));

        var cost = result.Details.SubResults!.Single(leaf => leaf.Metric.Key == "perf_cost");
        Assert.False(cost.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, cost.Score.Measurement);
        Assert.Equal(1, cost.Details.Dimensions!["errors"]);
        Assert.False(cost.Details.Dimensions.ContainsKey("cost_usd"));
        Assert.Contains("failed", cost.Details.Recommendations![0], StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.Measurement);
        Assert.False(result.Details.Dimensions!.ContainsKey("cost_usd"));
    }

    [Fact]
    public async Task EvaluateAsync_ObservedCostAlreadyOverBudget_RetainsKnownFailureWithoutClaimingTotal()
    {
        var result = await Benchmark(new MixedCostAgent(1_000_000, failOnVisit: 2)).EvaluateAsync(
            new EvalInput("ignored", Metadata: new Dictionary<string, object?> { ["prompts"] = new[] { "ok", "fails" } }));

        var cost = result.Details.SubResults!.Single(leaf => leaf.Metric.Key == "perf_cost");
        Assert.False(cost.Details.Dimensions!.ContainsKey("cost_usd"));
        Assert.True(cost.Details.Dimensions["observed_cost_usd"] > 1.0);
        Assert.Equal("fail", cost.Score.Label);
        Assert.True(cost.Score.CountsTowardAggregate());
        Assert.False(result.Score.Passed);
        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Details.Dimensions!.ContainsKey("cost_usd"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EvaluateAsync_SuccessfulSiblingWithoutUsage_KnownBudgetFailureCannotPass(bool checkLeaf)
    {
        var result = await Benchmark(new PartlyReportedUsageAgent(1_000_000)).EvaluateAsync(
            new EvalInput("ignored", Metadata: new Dictionary<string, object?> { ["prompts"] = new[] { "ok", "no-usage" } }));

        var cost = result.Details.SubResults!.Single(leaf => leaf.Metric.Key == "perf_cost");
        Assert.False(cost.Details.Dimensions!.ContainsKey("cost_usd"));
        Assert.False(result.Details.Dimensions!.ContainsKey("cost_usd"));
        if (checkLeaf)
        {
            Assert.Equal("fail", cost.Score.Label);
            Assert.True(cost.Score.CountsTowardAggregate());
            Assert.True(cost.Details.Dimensions["observed_cost_usd"] > 1.0);
            Assert.Contains("lower bound", cost.Details.Recommendations![0]);
        }
        else
        {
            Assert.False(result.Score.Passed);
            Assert.Equal("fail", result.Score.Label);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public async Task EvaluateAsync_SuccessfulSiblingWithoutUsage_BelowBudgetKeepsUnknownTotal(int tokens)
    {
        var result = await Benchmark(new PartlyReportedUsageAgent(tokens)).EvaluateAsync(
            new EvalInput("ignored", Metadata: new Dictionary<string, object?> { ["prompts"] = new[] { "ok", "no-usage" } }));

        var cost = result.Details.SubResults!.Single(leaf => leaf.Metric.Key == "perf_cost");
        Assert.Equal("skipped", cost.Score.Label);
        Assert.Equal(MeasurementState.NotMeasured, cost.Score.Measurement);
        Assert.False(cost.Details.Dimensions!.ContainsKey("cost_usd"));
        Assert.False(result.Details.Dimensions!.ContainsKey("cost_usd"));
        Assert.Contains("reported no token usage", cost.Details.Recommendations![0]);
    }

    [Fact]
    public async Task RunCostBenchmarkAsync_CompleteRecordedZero_RemainsMeasuredZero()
    {
        var result = await Benchmark(new MixedCostAgent(0)).RunCostBenchmarkAsync(["ok"], "gpt-4o");
        var eval = await Benchmark(new MixedCostAgent(0)).EvaluateAsync(new EvalInput("ok"));

        Assert.Equal(0m, result.EstimatedCostUSD);
        var cost = eval.Details.SubResults!.Single(leaf => leaf.Metric.Key == "perf_cost");
        Assert.True(cost.Score.Passed);
        Assert.Equal(MeasurementState.Measured, cost.Score.Measurement);
        Assert.Equal(0, cost.Details.Dimensions!["cost_usd"]);
    }

    [Fact]
    public async Task RunCostBenchmarkAsync_CallerCancels_PropagatesCancellation()
    {
        using var source = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Benchmark(new CancelingAgent(source))
            .RunCostBenchmarkAsync(["ok"], "gpt-4o", source.Token));
    }

    private sealed class PartlyReportedUsageAgent(int tokens) : IEvaluableAgent
    {
        public string Name => "PartlyReportedUsageAgent";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse
            {
                Text = "ok", ModelId = "gpt-4o",
                TokenUsage = prompt == "no-usage" ? null : new TokenUsage { PromptTokens = tokens, CompletionTokens = tokens },
            });
    }

    private sealed class MixedCostAgent(int tokens, int failOnVisit = 1) : IEvaluableAgent
    {
        private int _failedPromptVisits;
        public string Name => "MixedCostAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (prompt == "fails" && Interlocked.Increment(ref _failedPromptVisits) >= failOnVisit)
                return Task.FromException<AgentResponse>(new HttpRequestException("503 after dispatch"));
            return Task.FromResult(new AgentResponse
            {
                Text = "ok", ModelId = "gpt-4o",
                TokenUsage = new TokenUsage { PromptTokens = tokens, CompletionTokens = tokens },
            });
        }
    }

    private sealed class CancelingAgent(CancellationTokenSource source) : IEvaluableAgent
    {
        public string Name => "CancelingAgent";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return Task.FromCanceled<AgentResponse>(cancellationToken);
        }
    }

    /// <summary>Answers every call, on a priced model, without reporting token usage.</summary>
    private sealed class NoUsageAgent : IEvaluableAgent
    {
        public string Name => "NoUsageAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = "ok", ModelId = "gpt-4o", TokenUsage = null });
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
