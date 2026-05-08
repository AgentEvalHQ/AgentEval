// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Aggregates multiple component evals (atomic or nested composites) into a single scored result.
/// </summary>
public sealed class CompositeEval : IEval
{
    /// <inheritdoc/>
    public string Key { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string Category { get; }

    /// <inheritdoc/>
    public string Version { get; }

    /// <summary>The component evals that make up this composite.</summary>
    public IReadOnlyList<EvalComponent> Components { get; }

    /// <summary>The aggregation strategy used to combine component results.</summary>
    public IAggregationStrategy Aggregation { get; }

    /// <summary>
    /// Optional pass threshold (0..1). When set, <c>score &gt;= Threshold</c> is required to pass.
    /// When <c>null</c>, the verdict is severity-driven per the matrix in <see cref="EvaluateAsync"/>.
    /// </summary>
    public double? Threshold { get; }

    /// <summary>Initialises a new <see cref="CompositeEval"/>.</summary>
    public CompositeEval(
        string key,
        string name,
        string category,
        string version,
        IReadOnlyList<EvalComponent> components,
        IAggregationStrategy aggregation,
        double? threshold = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(aggregation);
        if (components.Count == 0)
            throw new ArgumentException("Composite must have at least one component.", nameof(components));

        Key = key;
        Name = name;
        Category = category;
        Version = version;
        Components = components;
        Aggregation = aggregation;
        Threshold = threshold;
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Run all sub-evals in parallel (no throttle in Phase 1).
        var subTasks = Components.Select(c => c.Eval.EvaluateAsync(input, ct)).ToArray();
        var subs = await Task.WhenAll(subTasks);

        var (score, severity) = Aggregation.Aggregate(subs, Components);
        var (cost, allCacheHits) = CostRollup.Aggregate(subs);

        // Verdict matrix:
        //   Threshold set       -> score >= threshold ? pass : fail
        //   Threshold null      -> severity is { high|critical -> fail, medium -> warn, _ -> pass }
        // "warn" is a soft fail: passed = false but label distinguishes from a hard fail.
        var label = Threshold is { } t
            ? (score >= t ? "pass" : "fail")
            : severity switch
            {
                "critical" or "high" => "fail",
                "medium" => "warn",
                _ => "pass"
            };
        var passed = label == "pass";

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(score, null, label, passed, Threshold, severity, null),
            Details: new(
                Dimensions: null,
                Evidence: null,
                Recommendations: null,
                SubResults: subs,
                AggregationStrategy: Aggregation.Name),
            Provenance: new(
                Type: "composite",
                JudgeModel: null,
                PromptId: null,
                PromptHash: null,
                TokensUsed: null,
                EstimatedCost: cost,
                CacheHit: allCacheHits),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
