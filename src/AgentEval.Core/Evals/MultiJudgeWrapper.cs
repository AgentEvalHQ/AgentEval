// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Meta;

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

        // Nothing measured is no verdict — CompositeEval's ADR-030 rule, which this wrapper lacked. The aggregation
        // returns (0, "none") when every judge errored or skipped, and both verdict paths below read that as a pass, so
        // a panel none of whose judges answered passed its parent (#203 review, round 2 M-1). A partly measured panel
        // is unchanged: the judges that answered decide.
        if (!subs.Any(s => s.Score.CountsTowardAggregate()))
            return NoVerdict(subs, cost, allCacheHits);

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

        // A pass the panel cannot agree on is not a pass (#203 review, B6c-3). The aggregate passed, but a judge that
        // answered found a high or critical failure: the panel withholds its pass — warn, recorded as not measured, the
        // "could not attest" state every composite above already refuses to pass on — instead of passing with the
        // dissent's severity riding along (which failed a parent through a passing result: the inversion B6c-3 removed)
        // or letting one judge's verdict override the median (which would make the panel worst-judge-wins).
        // A dissent is judged by the PANEL's bar, not each judge's own: a judge that fails only a stricter threshold of
        // its own is not disagreeing with the panel. (Without a threshold the severity path already fails on high.)
        var severeDissent = label == "pass" && Threshold is { } bar
            ? subs.Where(s => s.Score.CountsTowardAggregate() && s.Score.Value < bar
                              && s.Score.Severity is "high" or "critical").ToArray()
            : [];
        string? dissentNote = null;
        var measurement = MeasurementState.Measured;
        if (severeDissent.Length > 0)
        {
            label = "warn";
            severity = SeverityRollup.Max(severeDissent.Select(s => s.Score.Severity));
            measurement = MeasurementState.NotMeasured;
            dissentNote = $"The panel's aggregate passes, but {severeDissent.Length} of {subs.Length} judges found a {severity} " +
                          "failure; a pass the panel cannot agree on is withheld.";
        }
        else if (label == "pass")
        {
            severity = "none";   // a milder dissent the aggregate absorbed stays absorbed
        }
        var passed = label == "pass";

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(score, null, label, passed, Threshold, severity, null) { Measurement = measurement },
            Details: new(
                Dimensions: null,
                Evidence: null,
                Recommendations: dissentNote is null ? null : [dissentNote],
                SubResults: subs,
                AggregationStrategy: Aggregation.Name)
            {
                Summary = dissentNote,
            },
            Provenance: new("composite", null, null, null, null, cost, allCacheHits),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // No judge produced a measurement: "error" when any errored, else "skipped" — and NotApplicable, which never blocks
    // a parent, only when every judge said the case cannot test this.
    private EvalResult NoVerdict(EvalResult[] subs, double cost, bool allCacheHits)
    {
        var errored = subs.Count(s => s.Score.Label == "error");
        var allInapplicable = subs.All(s => s.Score.CensusBucket() == MeasurementState.NotApplicable);
        var label = errored > 0 ? "error" : "skipped";
        var note = allInapplicable
            ? $"All {subs.Length} judge(s) were inapplicable — the case cannot test this — so no verdict is reported."
            : $"No judge produced a measurement ({errored} errored, {subs.Length - errored} skipped or not measured), " +
              "so no verdict is reported.";

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(0.0, null, label, false, Threshold, "none", null)
            {
                Measurement = allInapplicable ? MeasurementState.NotApplicable : MeasurementState.Measured,
            },
            Details: new(
                Dimensions: null,
                Evidence: null,
                Recommendations: [note],
                SubResults: subs,
                AggregationStrategy: Aggregation.Name)
            {
                Summary = note,
            },
            Provenance: new("composite", null, null, null, null, cost, allCacheHits),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
