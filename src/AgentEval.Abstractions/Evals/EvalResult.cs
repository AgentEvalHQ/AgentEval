// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>The unified result produced by any eval, atomic or composite.</summary>
public sealed record EvalResult(
    EvalMetadata Metric,
    EvalScore Score,
    EvalDetails Details,
    EvalProvenance Provenance,
    DateTimeOffset EvaluatedAt)
{
    private readonly TimeSpan? _observedDuration;

    /// <summary>The elapsed interval observed for this evaluation boundary, or null when it was not recorded.</summary>
    /// <remarks>
    /// Transient writer context, excluded from legacy result JSON. Zero is an observed duration, not an unknown.
    /// A parent or trial rollup must record its own interval rather than borrow or sum child observations.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? ObservedDuration
    {
        get => _observedDuration;
        init
        {
            if (value is { } duration && duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(ObservedDuration), value,
                    "Expected: a nonnegative observed elapsed interval. Actual: negative duration. Suggestions: record the actual boundary interval or leave it unknown. Because: elapsed evidence cannot precede its start.");
            _observedDuration = value;
        }
    }

    /// <summary>The input observed at this result's evaluation boundary, when recorded.</summary>
    /// <remarks>
    /// Transient context for output writers, excluded from result JSON to avoid implicit prompt capture.
    /// Writers explicitly project it under their content-capture policy. A deserialized result has no observation;
    /// callers must not reconstruct one from an outer composite's input. An observation with null fields records
    /// an explicit unknown (for example, trials with different stimuli).
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public EvalInputObservation? Observation { get; init; }

    /// <summary>Agreed comparison facts retained by an aggregate independently of its execution provenance.</summary>
    /// <remarks>Transient writer context, explicitly projected into persisted comparison facts.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public AgentEval.Output.ComparabilityFacts? ComparisonObservation { get; init; }

    /// <summary>Creates a skipped result for <paramref name="eval"/> with a human-readable <paramref name="reason"/>.</summary>
    /// <param name="eval">The eval that did not run.</param>
    /// <param name="reason">
    /// Why it did not run. Written to <b>both</b> <see cref="EvalDetails.Summary"/> and
    /// <see cref="EvalDetails.Recommendations"/>.
    /// </param>
    /// <remarks>
    /// ADR-030 Slice 1.5 (defect D13). The reason used to go to <c>Recommendations</c> only, so every
    /// renderer that reads <c>Summary</c> — the field named for exactly this purpose — printed a bare
    /// <c>n/a</c> with no reason beside it. A blank cell where the explanation belongs is the shape
    /// ADR-030 §4.2's rendering rule exists to forbid: it reads as "nothing to say" when the truth is
    /// "nobody carried it across". <c>Recommendations</c> keeps its entry so no existing reader loses
    /// the text; this is additive on both the object and the persisted document (<c>details.summary</c>
    /// is already in schema v1 and already nullable).
    /// </remarks>
    public static EvalResult Skipped(IEval eval, string reason)
    {
        ArgumentNullException.ThrowIfNull(eval);

        return new(
            Metric: new(eval.Key, eval.Name, eval.Category, eval.Version),
            Score: new(0, null, "skipped", false, null, "none", null),
            Details: new(null, null, new[] { reason }, null, null) { Summary = reason },
            Provenance: new("skipped", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}

/// <summary>Input facts observed by an evaluator; null fields mean unknown, never an outer-input fallback.</summary>
/// <param name="Query">The actual query received by the evaluator, or null when no single query is known.</param>
/// <param name="SubjectModel">The reported subject model, with the declared model used only when none was reported.</param>
public sealed record EvalInputObservation(string? Query, string? SubjectModel);
