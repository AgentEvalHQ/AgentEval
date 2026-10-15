// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Models;

namespace AgentEval.Benchmarks;

/// <summary>The resolved workload and policy used by one performance evaluation, before agent calls.</summary>
public sealed class PerformanceEvaluationPlan
{
    /// <summary>Ordered workload; throughput uses the first prompt.</summary>
    public IReadOnlyList<string> Prompts { get; }
    /// <summary>Delay between latency iterations.</summary>
    public TimeSpan DelayBetweenIterations { get; }
    /// <summary>P99 latency scoring budget in milliseconds.</summary>
    public double P99LatencyThresholdMs { get; }
    /// <summary>Measured latency iterations per prompt.</summary>
    public int LatencyIterationsPerPrompt { get; }
    /// <summary>Warmup iterations per prompt.</summary>
    public int WarmupIterations { get; }
    /// <summary>Throughput scoring target.</summary>
    public double MinThroughputRps { get; }
    /// <summary>Concurrent throughput workers.</summary>
    public int ThroughputConcurrentRequests { get; }
    /// <summary>Throughput measurement window.</summary>
    public TimeSpan ThroughputDuration { get; }
    /// <summary>Cost scoring budget in USD.</summary>
    public double MaxCostUSD { get; }
    /// <summary>Initial pricing key; response model IDs are the fallback.</summary>
    public string CostModelName { get; }
    /// <summary>Composite pass threshold.</summary>
    public double CompositePassThreshold { get; }
    /// <summary>Frozen pricing lookup used by this evaluation.</summary>
    public ModelPricingSnapshot Pricing { get; }

    internal PerformanceEvaluationPlan(List<string> prompts, PerformanceBenchmarkOptions options, string agentName)
    {
        var eval = options.EvaluateOptions ?? PerformanceBenchmarkEvaluateOptions.Default;
        Prompts = prompts.AsReadOnly();
        DelayBetweenIterations = options.DelayBetweenIterations;
        P99LatencyThresholdMs = eval.P99LatencyThresholdMs;
        LatencyIterationsPerPrompt = eval.LatencyIterationsPerPrompt;
        WarmupIterations = eval.WarmupIterations;
        MinThroughputRps = eval.MinThroughputRps;
        ThroughputConcurrentRequests = eval.ThroughputConcurrentRequests;
        ThroughputDuration = eval.ThroughputDuration == default ? TimeSpan.FromSeconds(10) : eval.ThroughputDuration;
        MaxCostUSD = eval.MaxCostUSD;
        CostModelName = eval.CostModelName ?? agentName;
        CompositePassThreshold = eval.CompositePassThreshold;
        Pricing = ModelPricing.CaptureSnapshot();
    }

    internal PerformanceBenchmarkEvaluateOptions ToOptions() => new()
    {
        P99LatencyThresholdMs = P99LatencyThresholdMs, LatencyIterationsPerPrompt = LatencyIterationsPerPrompt,
        WarmupIterations = WarmupIterations, MinThroughputRps = MinThroughputRps,
        ThroughputConcurrentRequests = ThroughputConcurrentRequests, ThroughputDuration = ThroughputDuration,
        MaxCostUSD = MaxCostUSD, CostModelName = CostModelName, CompositePassThreshold = CompositePassThreshold,
    };
}
