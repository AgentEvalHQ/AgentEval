// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Wraps an eval (typically an article composite) to run N judge sub-evals in parallel
/// against the same <see cref="EvalInput"/>. Each judge is itself an <see cref="IEval"/>.
/// Results are aggregated via the supplied <see cref="IAggregationStrategy"/>
/// (typically <see cref="WeightedMedianAggregation"/>).
/// </summary>
public sealed class MultiJudgeWrapper : IEval
{
    /// <inheritdoc/>
    public string Key { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string Category { get; }

    /// <inheritdoc/>
    public string Version { get; }

    /// <summary>The judge sub-evals that will each evaluate the same input.</summary>
    public IReadOnlyList<EvalComponent> Judges { get; }

    /// <summary>The aggregation strategy used to combine judge results.</summary>
    public IAggregationStrategy Aggregation { get; }

    /// <summary>Initialises a new <see cref="MultiJudgeWrapper"/>.</summary>
    public MultiJudgeWrapper(
        string key,
        string name,
        string category,
        string version,
        IReadOnlyList<EvalComponent> judges,
        IAggregationStrategy aggregation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(aggregation);
        if (judges.Count == 0)
            throw new ArgumentException("MultiJudgeWrapper must have at least one judge.", nameof(judges));

        Key = key;
        Name = name;
        Category = category;
        Version = version;
        Judges = judges;
        Aggregation = aggregation;
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var subTasks = Judges.Select(j => j.Eval.EvaluateAsync(input, ct)).ToArray();
        var subs = await Task.WhenAll(subTasks);

        var (score, severity) = Aggregation.Aggregate(subs, Judges);
        var cost = subs.Sum(s => s.Provenance.EstimatedCost);

        var label = severity switch
        {
            "critical" or "high" => "fail",
            "medium" => "warn",
            _ => "pass"
        };
        var passed = label == "pass";

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(score, null, label, passed, null, severity, null),
            Details: new(
                Dimensions: null,
                Evidence: null,
                Recommendations: null,
                SubResults: subs,
                AggregationStrategy: Aggregation.Name),
            Provenance: new("composite", null, null, null, null, cost, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
