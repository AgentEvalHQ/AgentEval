// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

public sealed class CachedPerformanceCostTests
{
    [Fact]
    public async Task RunCostBenchmark_CachedUsage_UsesFrozenRatesForEveryResponse()
    {
        var key = "cache-perf-" + Guid.NewGuid();
        ModelPricing.SetPricing(key, new(0.001m, 0.002m, 0.0001m));
        var agent = new CostAgent(_ =>
        {
            ModelPricing.SetPricing(key, new(100, 100, 100));
            return Response(key);
        });
        var result = await new PerformanceBenchmark(agent, new() { Verbose = false })
            .RunCostBenchmarkAsync(["a", "b"], key);
        Assert.Equal(0.0096m, result.EstimatedCostUSD);
        Assert.Equal(2, result.CostEstimates.Count);
        Assert.All(result.CostEstimates, c => Assert.Equal(TokenCostStatus.CacheAdjusted, c.Status));
    }

    [Fact]
    public async Task RunCostBenchmark_MixedReturnedModels_PricesEachAtItsOwnRate()
    {
        var first = "model-a-" + Guid.NewGuid();
        var second = "model-b-" + Guid.NewGuid();
        ModelPricing.SetPricing(first, new(0.001m, 0.002m, 0.0001m));
        ModelPricing.SetPricing(second, new(0.002m, 0.004m, 0.0002m));
        var agent = new CostAgent(prompt => Response(prompt == "a" ? first : second));
        var result = await new PerformanceBenchmark(agent, new() { Verbose = false })
            .RunCostBenchmarkAsync(["a", "b"], "unknown-configured-id");
        Assert.Equal(0.0144m, result.EstimatedCostUSD);
        Assert.Contains("mixed", result.ModelName);
        Assert.Equal(new[] { first, second }, result.CostEstimates.Select(c => c.ModelId));
    }

    [Fact]
    public async Task RunCostBenchmark_OneUnknownModel_DoesNotPriceWholeWorkloadAtFirstRate()
    {
        var agent = new CostAgent(prompt => Response(prompt == "a" ? "gpt-4o" : "unknown-model"));
        var result = await new PerformanceBenchmark(agent, new() { Verbose = false })
            .RunCostBenchmarkAsync(["a", "b"], "unknown-configured-id");
        Assert.Null(result.EstimatedCostUSD);
        Assert.NotNull(result.CostEstimates[0].Amount);
        Assert.Null(result.CostEstimates[1].Amount);
    }

    [Fact]
    public async Task RunCostBenchmark_TotalOverflows_RemainsUnpriced()
    {
        var key = "overflow-perf-" + Guid.NewGuid();
        ModelPricing.SetPricing(key, new(decimal.MaxValue / 2, 0, 0));
        var agent = new CostAgent(_ => new()
        {
            Text = "answer", ModelId = key,
            TokenUsage = new() { PromptTokens = 1000, CompletionTokens = 0, CachedInputTokenCount = 0 }
        });
        var result = await new PerformanceBenchmark(agent, new() { Verbose = false })
            .RunCostBenchmarkAsync(["a", "b", "c"], key);
        Assert.Null(result.EstimatedCostUSD);
        Assert.All(result.CostEstimates, c => Assert.NotNull(c.Amount));
    }

    [Fact]
    public async Task Evaluate_UnknownCacheAndFailedSibling_DoesNotTreatUndiscountedCostAsLowerBound()
    {
        var failedVisits = 0;
        var agent = new CostAgent(prompt =>
        {
            if (prompt == "fails" && ++failedVisits == 2) throw new IOException("scripted failure");
            return new() { Text = "answer", TokenUsage = new() { PromptTokens = 100000, CompletionTokens = 0 } };
        });
        var options = new PerformanceBenchmarkOptions
        {
            Verbose = false, DelayBetweenIterations = TimeSpan.Zero,
            EvaluateOptions = new()
            {
                CostModelName = "gpt-4o", MaxCostUSD = 0.2, LatencyIterationsPerPrompt = 1,
                WarmupIterations = 0, ThroughputConcurrentRequests = 0, ThroughputDuration = TimeSpan.FromMilliseconds(1)
            }
        };
        var result = await new PerformanceBenchmark(agent, options).EvaluateAsync(new("ignored",
            Metadata: new Dictionary<string, object> { ["prompts"] = new[] { "ok", "fails" } }));
        var cost = Assert.Single(result.Details.SubResults!, r => r.Metric.Key == "perf_cost");
        Assert.Equal("skipped", cost.Score.Label);
        Assert.Equal(0.125, cost.Details.Dimensions!["observed_cost_usd"], 8);
        Assert.False(cost.Score.Passed);
    }

    private static AgentResponse Response(string model) => new()
    {
        Text = "answer", ModelId = model,
        TokenUsage = new() { PromptTokens = 10000, CompletionTokens = 1000, CachedInputTokenCount = 8000 }
    };

    private sealed class CostAgent(Func<string, AgentResponse> respond) : IEvaluableAgent
    {
        public string Name => "cost agent";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(respond(prompt));
    }
}
