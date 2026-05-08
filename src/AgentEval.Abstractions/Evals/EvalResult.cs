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
    public static EvalResult Skipped(IEval eval, string reason) =>
        new(
            Metric: new(eval.Key, eval.Name, eval.Category, eval.Version),
            Score: new(0, null, "skipped", false, null, "none", null),
            Details: new(null, null, new[] { reason }, null, null),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
}
