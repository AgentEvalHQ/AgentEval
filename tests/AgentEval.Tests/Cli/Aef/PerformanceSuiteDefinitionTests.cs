using AgentEval.Benchmarks;
using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Models;
using AgentEval.Results.Suites;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class PerformanceSuiteDefinitionTests
{
    [Fact]
    public async Task Capture_BeforeAgentCalls_HashesWorkloadAndDeclaresThreeChecks()
    {
        var first = await Capture(new(), "private workload");
        var same = await Capture(new(), "private workload");
        var changed = await Capture(new(), "different workload");
        Assert.Equal(first.Digest, same.Digest);
        Assert.NotEqual(first.Digest, changed.Digest);
        Assert.True(first.Frozen);
        Assert.Equal(new[] { "perf_cost", "perf_latency", "perf_throughput" }, first.CaseIds);
        Assert.DoesNotContain("private workload", first.ToJson().ToJsonString());
    }

    [Theory]
    [InlineData("latency-budget")]
    [InlineData("iterations")]
    [InlineData("warmup")]
    [InlineData("throughput-target")]
    [InlineData("workers")]
    [InlineData("duration")]
    [InlineData("cost-budget")]
    [InlineData("pricing-key")]
    [InlineData("composite-threshold")]
    [InlineData("delay")]
    public async Task Capture_ChangedEffectivePolicy_ChangesIdentity(string setting)
    {
        var baseline = await Capture(new(), "q");
        var options = new PerformanceBenchmarkOptions { EvaluateOptions = new() };
        var eval = options.EvaluateOptions;
        switch (setting)
        {
            case "latency-budget": eval.P99LatencyThresholdMs++; break;
            case "iterations": eval.LatencyIterationsPerPrompt++; break;
            case "warmup": eval.WarmupIterations++; break;
            case "throughput-target": eval.MinThroughputRps++; break;
            case "workers": eval.ThroughputConcurrentRequests++; break;
            case "duration": eval.ThroughputDuration += TimeSpan.FromSeconds(1); break;
            case "cost-budget": eval.MaxCostUSD++; break;
            case "pricing-key": eval.CostModelName = "gpt-4o"; break;
            case "composite-threshold": eval.CompositePassThreshold = 0.8; break;
            case "delay": options.DelayBetweenIterations += TimeSpan.FromMilliseconds(1); break;
        }
        Assert.NotEqual(baseline.Digest, (await Capture(options, "q")).Digest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluate_MutationAfterPreparation_UsesCapturedOptionsAndPricing(bool responseFallback)
    {
        var key = "perf-snapshot-" + Guid.NewGuid().ToString("N");
        ModelPricing.SetPricing(key, 0.01m, 0.02m);
        var opts = new PerformanceBenchmarkOptions
        {
            Verbose = false, DelayBetweenIterations = TimeSpan.Zero,
            EvaluateOptions = new()
            {
                CostModelName = responseFallback ? "no-matching-pricing-key" : key,
                LatencyIterationsPerPrompt = 1, WarmupIterations = 0,
                ThroughputDuration = TimeSpan.FromMilliseconds(1), ThroughputConcurrentRequests = 0, MaxCostUSD = 1,
            },
        };
        var agent = new CountingAgent { ResponseModel = key };
        SuiteDefinitionManifest? before = null;
        SuiteDefinitionManifest? after = null;
        var input = new EvalInput("q", Metadata: new Dictionary<string, object>
        {
            [PerformanceBenchmark.PlanPreparedMetadataKey] = (Action<PerformanceEvaluationPlan>)(plan =>
            {
                Assert.Equal(0, agent.Calls);
                before = PerformanceSuiteDefinition.Create("cost", plan);
                opts.EvaluateOptions.LatencyIterationsPerPrompt = 10;
                opts.EvaluateOptions.MaxCostUSD = 0.001;
                opts.DelayBetweenIterations = TimeSpan.FromHours(1);
                ModelPricing.SetPricing(key, 10m, 20m);
                Assert.Equal(0.01m, plan.Pricing.GetPricing(key)!.Value.InputPer1K);
                after = PerformanceSuiteDefinition.Create("cost", plan);
            }),
        });
        var result = await new PerformanceBenchmark(agent, opts).EvaluateAsync(input);
        Assert.Equal(before!.Digest, after!.Digest);
        Assert.Equal(2, agent.Calls); // one latency call and one cost call; no throughput workers
        var cost = Assert.Single(result.Details.SubResults!, r => r.Metric.Key == "perf_cost");
        Assert.Equal(0.03, cost.Details.Dimensions!["cost_usd"], 8);
        Assert.Equal(0.97, cost.Score.Value, 8);
        Assert.NotEqual(before.Digest, (await Capture(new() { EvaluateOptions = new() { CostModelName = key } }, "q")).Digest);
    }

    [Fact]
    public async Task Capture_OnlyPricingChanges_ChangesIdentity()
    {
        var key = "perf-pricing-" + Guid.NewGuid().ToString("N");
        ModelPricing.SetPricing(key, 1m, 2m);
        var options = new PerformanceBenchmarkOptions { EvaluateOptions = new() { CostModelName = key } };
        var before = await Capture(options, "q");
        ModelPricing.SetPricing(key, 2m, 3m);
        Assert.NotEqual(before.Digest, (await Capture(options, "q")).Digest);
    }

    private static async Task<SuiteDefinitionManifest> Capture(PerformanceBenchmarkOptions options, string prompt)
    {
        var agent = new CountingAgent();
        SuiteDefinitionManifest? definition = null;
        var input = new EvalInput(prompt, Metadata: new Dictionary<string, object>
        {
            [PerformanceBenchmark.PlanPreparedMetadataKey] = (Action<PerformanceEvaluationPlan>)(plan =>
            {
                definition = PerformanceSuiteDefinition.Create("latency", plan);
                throw new CaptureComplete();
            }),
        });
        await Assert.ThrowsAsync<CaptureComplete>(() => new PerformanceBenchmark(agent, options).EvaluateAsync(input));
        Assert.Equal(0, agent.Calls);
        return definition!;
    }

    private sealed class CaptureComplete : Exception;

    private sealed class CountingAgent : IEvaluableAgent
    {
        public string Name => "test-agent";
        public string? ResponseModel { get; init; }
        public int Calls { get; private set; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AgentResponse
            {
                Text = "observation", ModelId = ResponseModel,
                TokenUsage = new TokenUsage { PromptTokens = 1000, CompletionTokens = 1000 },
            });
        }
    }
}
