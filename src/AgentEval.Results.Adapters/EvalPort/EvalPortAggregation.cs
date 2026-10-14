// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Results.Adapters.EvalPort;

/// <summary>Caller-supplied suite facts absent from a standalone ResultSet.</summary>
public sealed record EvalPortAggregationContext
{
    /// <summary>Suite default strategy. Null explicitly means the specification's all default.</summary>
    public string? Strategy { get; init; }
    /// <summary>Suite default cutoff for majority or weighted strategies.</summary>
    public double? Threshold { get; init; }
    /// <summary>
    /// Resolved suite grader weights. Each measured grader must be present for a weighted check;
    /// use 1 for a definition that omits its weight. Missing entries are not assumed equal-weight.
    /// </summary>
    public IReadOnlyDictionary<string, double> GraderWeights { get; init; } = new Dictionary<string, double>();
}

internal static class EvalPortAggregation
{
    internal static JsonObject Assess(JsonObject result, EvalPortAggregationContext? context)
    {
        var metadata = result["metadata"] as JsonObject;
        JsonObject policy;
        var declared = metadata?.ContainsKey("openeval.aggregation") == true;
        if (declared)
        {
            if (metadata!["openeval.aggregation"] is not JsonObject value) throw Refuse("aggregation must be an object");
            policy = value;
        }
        else if (context is not null)
        {
            policy = new() { ["strategy"] = context.Strategy ?? "all" };
            if (context.Threshold is { } threshold) policy["threshold"] = threshold;
        }
        else return Report("unverifiable", "Suite default policy was not supplied.");

        if (policy["strategy"] is not JsonValue strategyValue || !strategyValue.TryGetValue<string>(out var strategy))
            throw Refuse("aggregation strategy must be a string");
        if (strategy is not ("all" or "any" or "majority" or "weighted"))
            throw Refuse("unknown aggregation strategy");
        double? cutoff = null;
        if (policy.ContainsKey("threshold"))
        {
            if (policy["threshold"] is not JsonValue scalar || !scalar.TryGetValue<double>(out var number) ||
                !double.IsFinite(number) || number is < 0 or > 1) throw Refuse("aggregation threshold must be finite and within [0,1]");
            cutoff = number;
        }
        if (strategy == "weighted" && cutoff is null) throw Refuse("weighted aggregation requires a threshold");
        // An extension to the policy may change its semantics. Retain it without certifying an incomplete interpretation.
        if (policy.Any(p => p.Key is not ("strategy" or "threshold"))) return Report("unverifiable", "Policy has unknown fields.", strategy);
        var measured = result["grader_results"]!.AsArray().Where(g => g!["score"] is not null).Select(g => g!.AsObject()).ToArray();
        if (result["error"] is not null) return Report("unverifiable", "Execution error may override aggregation.", strategy);
        bool expected;
        double? aggregate = null;
        JsonObject? resolvedWeights = null;
        if (measured.Length == 0) expected = false;
        else if (strategy == "weighted")
        {
            var weights = new double[measured.Length];
            for (var i = 0; i < measured.Length; i++)
            {
                if (context is null || !context.GraderWeights.TryGetValue(measured[i]["grader_id"]!.GetValue<string>(), out weights[i]))
                    return Report("unverifiable", "Resolved suite grader weights were not supplied.", strategy);
                if (!double.IsFinite(weights[i]) || weights[i] < 0) throw Refuse("grader weights must be finite and nonnegative");
            }
            var scale = weights.Max();
            if (scale == 0) return Report("unverifiable", "Measured graders have zero total weight.", strategy);
            resolvedWeights = new();
            for (var i = 0; i < measured.Length; i++) resolvedWeights[measured[i]["grader_id"]!.GetValue<string>()] = weights[i];
            // Scaling prevents finite but very large weights from overflowing the numerator/denominator.
            var numerator = 0d; var denominator = 0d;
            for (var i = 0; i < weights.Length; i++)
            { var weight = weights[i] / scale; denominator += weight; numerator += weight * measured[i]["score"]!.GetValue<double>(); }
            aggregate = numerator / denominator;
            expected = aggregate >= cutoff!.Value;
        }
        else
        {
            var passed = measured.Count(g => g["passed"]!.GetValue<bool>());
            expected = strategy switch
            {
                "all" => passed == measured.Length,
                "any" => passed > 0,
                _ => (double)passed / measured.Length > (cutoff ?? 0.5),
            };
        }
        if (expected != result["passed"]!.GetValue<bool>()) throw Refuse("result passed flag contradicts the available aggregation policy and measured graders");
        var report = Report("verified", "Recorded result agrees with the available aggregation inputs.", strategy);
        report["expectedPassed"] = expected;
        report["measured"] = measured.Length;
        report["source"] = declared ? "result" : "caller-supplied-suite";
        if (aggregate is { } score) report["score"] = score;
        if (resolvedWeights is not null) report["callerSuppliedWeights"] = resolvedWeights;
        if (cutoff is { } thresholdValue) report["threshold"] = thresholdValue;
        return report;
    }

    private static JsonObject Report(string status, string reason, string? strategy = null) =>
        new() { ["status"] = status, ["reason"] = reason, ["strategy"] = strategy };
    private static AefInputException Refuse(string actual) => new(
        $"Expected: consistent EvalPort aggregation evidence. Actual: {actual}. Suggestions: correct the source declaration or supply resolved suite facts. Because: conversion must not certify a contradictory decision.");
}
