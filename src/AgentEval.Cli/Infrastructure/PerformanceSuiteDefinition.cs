// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Benchmarks;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Declares the prepared performance workload and scoring policy without observed measurements.</summary>
internal static class PerformanceSuiteDefinition
{
    internal static SuiteDefinitionManifest Create(string preset, PerformanceEvaluationPlan plan)
    {
        var inputs = new JsonObject();
        foreach (var type in new[] { typeof(PerformanceSuiteDefinition), typeof(PerformanceBenchmark),
            typeof(AgentEval.Evals.CapByWorstAggregation), typeof(AgentEval.Models.ModelPricing),
            typeof(SuiteDefinitionManifest), typeof(AgentEval.Results.Adapters.Native.AefCompositeRunWriter) })
            inputs["implementation." + type.Assembly.GetName().Name] = SuiteDefinitionManifest.Hash(File.ReadAllBytes(type.Assembly.Location));
        inputs["workload"] = SuiteDefinitionManifest.Hash(AefJsonWriter.Document(new JsonObject
        {
            ["prompts"] = new JsonArray(plan.Prompts.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        }));
        // Snapshot keys are canonical: equally long substring matches use ordinal, case-insensitive order.
        inputs["pricing"] = SuiteDefinitionManifest.Hash(AefJsonWriter.Document(new JsonObject
        {
            ["rates"] = new JsonArray(plan.Pricing.Rates.Select(p => (JsonNode?)new JsonObject
            {
                ["model"] = p.Key, ["inputPer1K"] = p.Value.InputPer1K, ["outputPer1K"] = p.Value.OutputPer1K,
            }).ToArray()),
        }));
        inputs["pricingKey"] = SuiteDefinitionManifest.Hash(Encoding.UTF8.GetBytes(plan.CostModelName));
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:perf/" + preset,
            ["definitionVersion"] = "performance-definition-2", ["frozen"] = true, ["inputs"] = inputs,
            ["caseIds"] = new JsonArray("perf_benchmark", "perf_latency", "perf_throughput", "perf_cost"),
            ["definition"] = new JsonObject
            {
                ["producer"] = "perf", ["promptCount"] = plan.Prompts.Count,
                ["delayTicks"] = plan.DelayBetweenIterations.Ticks,
                ["p99LatencyThresholdMs"] = plan.P99LatencyThresholdMs,
                ["latencyIterationsPerPrompt"] = plan.LatencyIterationsPerPrompt,
                ["warmupIterations"] = plan.WarmupIterations, ["minThroughputRps"] = plan.MinThroughputRps,
                ["throughputConcurrentRequests"] = plan.ThroughputConcurrentRequests,
                ["throughputDurationTicks"] = plan.ThroughputDuration.Ticks, ["maxCostUsd"] = plan.MaxCostUSD,
                ["compositePassThreshold"] = plan.CompositePassThreshold, ["aggregation"] = "CapByWorst",
                ["pricingPolicy"] = "initial-key-then-first-reported-model;exact-then-longest-substring;missing-skips-cost",
                ["trialsPerCase"] = 1,
            },
        }));
    }
}
