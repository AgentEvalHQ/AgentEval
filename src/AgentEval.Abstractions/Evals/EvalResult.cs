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
