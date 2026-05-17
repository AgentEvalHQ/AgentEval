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
/// <remarks>
/// <para>
/// <b>Threshold vs. MajorityVote interaction (Phase-8 Task 8.6).</b>
/// When <see cref="Threshold"/> is supplied AND the aggregation strategy is
/// <c>MajorityVoteAggregation</c>, the threshold check takes precedence over
/// the majority-vote verdict matrix. Concretely:
/// </para>
/// <list type="bullet">
///   <item>3 judges vote pass + aggregate score 0.80, threshold 0.85 → label
///         is <c>"fail"</c> because <c>0.80 &lt; 0.85</c>. The pass-majority is
///         overridden by the threshold gate.</item>
///   <item>2 judges fail + 1 pass, mean score 0.45, threshold null → label is
///         <c>"fail"</c> via the severity-driven verdict (the majority-vote
///         winner is "fail" → severity from winning voters).</item>
///   <item>3 judges warn at score 0.78, threshold 0.70 → label is <c>"pass"</c>
///         because the threshold is met. The "warn"-majority severity is
///         preserved on the result.Score.Severity but the verdict label is
///         pass — useful for "soft warnings under a strict gate" semantics.</item>
/// </list>
/// <para>
/// Rule of thumb: set <see cref="Threshold"/> only when the gate is the
/// SCORE; leave it <c>null</c> when the gate is the MAJORITY LABEL.
/// Mixing both is supported but the threshold always wins on the label.
/// </para>
/// </remarks>
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

    /// <summary>Optional pass threshold (0..1). When set, the composite
    /// passes iff <c>aggregated score &gt;= Threshold</c>; otherwise the
    /// verdict is severity-driven. Mirrors <see cref="CompositeEval"/>.</summary>
    public double? Threshold { get; }

    /// <summary>Initialises a new <see cref="MultiJudgeWrapper"/>.</summary>
    public MultiJudgeWrapper(
        string key,
        string name,
        string category,
        string version,
        IReadOnlyList<EvalComponent> judges,
        IAggregationStrategy aggregation,
        double? threshold = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(aggregation);
        if (judges.Count == 0)
            throw new ArgumentException("MultiJudgeWrapper must have at least one judge.", nameof(judges));
        if (threshold is { } t && (!double.IsFinite(t) || t < 0 || t > 1))
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold,
                "Threshold must be a finite value in [0, 1] (NaN, +Infinity, and -Infinity are rejected).");

        Key = key;
        Name = name;
        Category = category;
        Version = version;
        Threshold = threshold;
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

        // Use CostRollup so cost + cache-hit semantics match CompositeEval —
        // previously every panel run reported `CacheHit: false` even when
        // every judge was a cache hit.
        var (cost, allCacheHits) = CostRollup.Aggregate(subs);

        // Honour the optional Threshold parameter (when supplied) — falls
        // back to the severity-driven verdict matrix otherwise. Mirrors
        // CompositeEval's behaviour so consumers can pick whichever shape
        // is right for their use case.
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
            Provenance: new("composite", null, null, null, null, cost, allCacheHits),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
