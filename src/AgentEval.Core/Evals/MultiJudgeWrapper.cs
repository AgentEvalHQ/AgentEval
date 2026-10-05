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
        // a panel none of whose judges answered passed its parent (#203 review, round 2 M-1).
        if (!subs.Any(s => s.Score.CountsTowardAggregate()))
            return NoVerdict(subs, cost, allCacheHits);

        // A partly measured panel honours each judge's Required, as a composite does (#203 review round 3, B10g): it
        // ignored it, so a GDPR/EU AuditGrade panel — every judge declared required — passed on one judge of three when
        // the other two errored. A required judge that errored leaves no verdict ("error") unless the panel fails even with
        // every such judge at its best (B10n); one that did not run otherwise withholds a pass. Optional judges never block.
        var requiredMissing = Judges.Zip(subs, (j, r) => (Judge: j, Result: r))
            .Where(p => p.Judge.Required && !p.Result.Score.CountsTowardAggregate()
                        && p.Result.Score.CensusBucket() != MeasurementState.NotApplicable)
            .ToArray();
        var requiredErrored = requiredMissing.Where(p => p.Result.Score.Label == "error").Select(p => p.Result.Metric.Key).ToArray();
        var requiredNotRun = requiredMissing.Where(p => p.Result.Score.Label != "error").Select(p => p.Result.Metric.Key).ToArray();

        // Honour the optional Threshold parameter (when supplied) — falls
        // back to the severity-driven verdict matrix otherwise. Mirrors
        // CompositeEval's behaviour so consumers can pick whichever shape
        // is right for their use case.
        var label = PanelLabel(score, severity);

        // A pass the panel cannot agree on is not a pass (#203 review, B6c-3). The aggregate passed, but a judge that
        // answered found a high or critical failure: the panel withholds its pass — warn, recorded as not measured, the
        // "could not attest" state every composite above already refuses to pass on — instead of passing with the
        // dissent's severity riding along (which failed a parent through a passing result: the inversion B6c-3 removed)
        // or letting one judge's verdict override the median (which would make the panel worst-judge-wins).
        // A dissent is judged by the PANEL's bar, not each judge's own: a judge that fails only a stricter threshold of
        // its own is not disagreeing with the panel. Without a threshold the bar is the judge's own verdict: the severity
        // path does not fail on a high dissent the aggregation outvotes (majority vote: 2 passes and 1 critical failure
        // read pass; the comment here used to claim otherwise — B10g).
        var severeDissent = label == "pass"
            ? subs.Where(s => s.Score.CountsTowardAggregate()
                              && (Threshold is { } bar ? s.Score.Value < bar : !s.Score.Passed)
                              && s.Score.Severity is "high" or "critical").ToArray()
            : [];
        string? dissentNote = null;
        var measurement = MeasurementState.Measured;
        if (severeDissent.Length > 0)
        {
            label = "warn";
            var dissent = SeverityRollup.Max(severeDissent.Select(s => s.Score.Severity));
            severity = "medium";   // a warn means medium everywhere; the dissent's own severity is named below (B6c-6)
            measurement = MeasurementState.NotMeasured;
            dissentNote = $"The panel's aggregate passes, but {severeDissent.Length} of {subs.Length} judges found a {dissent} " +
                          "failure; a pass the panel cannot agree on is withheld.";
        }
        else if (label == "pass")
        {
            severity = "none";   // a milder dissent the aggregate absorbed stays absorbed
        }

        string? requiredNote = null;
        if (requiredErrored.Length > 0)
        {
            // The answering judges decide a failure only when the panel fails even if every required judge that errored
            // had passed perfectly (#203 review round 4, B10n): re-aggregated with them at 1.0. The heuristic it replaces —
            // a high or critical failure without a threshold — was not decided under MajorityVote, where the missing
            // judge's vote could flip the majority, and missed a threshold the answering judges cannot reach.
            var bestCase = Judges.Zip(subs, (j, r) => j.Required && r.Score.Label == "error"
                    ? r with { Score = new EvalScore(1.0, null, "pass", true, r.Score.Threshold, "none", null) }
                    : r)
                .ToArray();
            var (bestScore, bestSeverity) = Aggregation.Aggregate(bestCase, Judges);
            var decided = label == "fail" && PanelLabel(bestScore, bestSeverity) == "fail";
            if (!decided)
            {
                label = "error";
                severity = "none";
                measurement = MeasurementState.Measured;   // "error" is its own not-measured state (CensusBucket)
                requiredNote = $"Required judge(s) produced no verdict: {string.Join(", ", requiredErrored)}; the panel " +
                               "reports no verdict on the judges that answered.";
            }
            else
            {
                // A decided failure still says which judge is missing (review round 5 L-5, B10x): it said nothing.
                requiredNote = $"Required judge(s) produced no verdict: {string.Join(", ", requiredErrored)}; the judges " +
                               $"that answered fail the panel even if {(requiredErrored.Length == 1 ? "it" : "they")} had passed.";
            }
        }
        else if (requiredNotRun.Length > 0 && label == "pass")
        {
            label = "warn";
            severity = "none";
            measurement = MeasurementState.NotMeasured;
            requiredNote = $"Required judge(s) that did not run: {string.Join(", ", requiredNotRun)}; a pass cannot rest " +
                           "on the judges that answered, so it is withheld.";
        }
        if (requiredNote is not null)
            dissentNote = dissentNote is null ? requiredNote : dissentNote + " " + requiredNote;
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

    // The panel's verdict path: the threshold when one is set, else the severity rule.
    private string PanelLabel(double score, string severity) => Threshold is { } t
        ? (score >= t ? "pass" : "fail")
        : severity switch
        {
            "critical" or "high" => "fail",
            "medium" => "warn",
            _ => "pass"
        };

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
