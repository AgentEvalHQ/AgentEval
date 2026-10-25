// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Models;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

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

    // A content-free identity of the instrument, independent of which subject is measured. The complete frozen
    // price schedule is part of the scoring policy; the subject's lookup key is deliberately excluded.
    internal string ComparisonPolicyDigest { get; }

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
        ComparisonPolicyDigest = "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "agenteval.performance-policy/1",
            aggregation = "CapByWorst", weights = new[] { 1.0, 1.0, 1.0 },
            P99LatencyThresholdMs, MinThroughputRps, MaxCostUSD, CompositePassThreshold,
            LatencyIterationsPerPrompt, WarmupIterations, ThroughputConcurrentRequests,
            throughputDurationTicks = ThroughputDuration.Ticks, delayTicks = DelayBetweenIterations.Ticks,
            pricingPolicy = "initial-key-then-first-reported-model;exact-then-longest-substring;missing-skips-cost",
            rates = Pricing.Rates.Select(p => new
            {
                model = p.Key.ToLowerInvariant(),
                inputPer1K = p.Value.InputPer1K.ToString("G29", CultureInfo.InvariantCulture),
                outputPer1K = p.Value.OutputPer1K.ToString("G29", CultureInfo.InvariantCulture),
            }).ToArray(),
        }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }))).ToLowerInvariant();
    }

    internal PerformanceBenchmarkEvaluateOptions ToOptions() => new()
    {
        P99LatencyThresholdMs = P99LatencyThresholdMs, LatencyIterationsPerPrompt = LatencyIterationsPerPrompt,
        WarmupIterations = WarmupIterations, MinThroughputRps = MinThroughputRps,
        ThroughputConcurrentRequests = ThroughputConcurrentRequests, ThroughputDuration = ThroughputDuration,
        MaxCostUSD = MaxCostUSD, CostModelName = CostModelName, CompositePassThreshold = CompositePassThreshold,
    };
}
