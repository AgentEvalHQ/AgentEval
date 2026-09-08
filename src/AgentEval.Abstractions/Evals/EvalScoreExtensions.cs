// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Meta;

namespace AgentEval.Evals;

/// <summary>
/// The one place that decides whether a score is a real quality signal (ADR-030 Slice 1.2).
/// </summary>
/// <remarks>
/// <para>
/// All five aggregation strategies used to repeat <c>r.Score.Label is not ("skipped" or "error")</c>
/// in five files with two different comment vintages — and one of them,
/// <c>CapByWorstAggregation</c>, had drifted to <c>Label != "skipped"</c> alone. That asymmetry was
/// safe only because every "error" leaf in the tree happens to carry severity <c>none</c>; nothing
/// enforced it. Routing every strategy through one predicate is what lets a new neutral state be
/// added once instead of five times, and it closes the drift as a side effect.
/// </para>
/// </remarks>
public static class EvalScoreExtensions
{
    /// <summary>
    /// The SINGLE authority on whether a score contributes to an aggregate: its value belongs in a
    /// mean, its severity belongs in a rollup, and its pass/fail belongs in a cap.
    /// </summary>
    /// <param name="score">The score to test.</param>
    /// <returns>
    /// <see langword="true"/> only when the score is a real measurement AND its label is not one of
    /// the neutral infra labels.
    /// </returns>
    /// <remarks>
    /// Reads <b>both</b> operands on purpose. ADR-030 §4.2 leaves <c>Label</c> deliberately
    /// unguarded — it is a free string that historical artifacts round-trip through, and a guard on
    /// it would reject documents that are merely old rather than wrong — so a mislabelled score must
    /// not be able to leak into an aggregate through the label alone, nor through the state alone.
    /// </remarks>
    public static bool CountsTowardAggregate(this EvalScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        return score.Measurement == MeasurementState.Measured
            && score.Label is not ("skipped" or "error" or "inapplicable");
    }

    /// <summary>
    /// Classifies a score into the three-way census bucket, reading <b>both</b> the state and the
    /// label for the reason given on <see cref="CountsTowardAggregate"/>.
    /// </summary>
    /// <param name="score">The score to classify.</param>
    /// <returns>The measurement state this score should be counted under.</returns>
    /// <remarks>
    /// The label mapping is what keeps the census honest before ADR-030 Slice 1.4 lands: today
    /// <c>EvalResult.Skipped</c> still leaves <see cref="EvalScore.Measurement"/> at
    /// <see cref="MeasurementState.Measured"/> — writing <c>NotMeasured</c> there would emit a
    /// <c>measurement</c> field that schema v1's <c>additionalProperties: false</c> rejects — so
    /// <c>skipped</c> / <c>error</c> are recognised by label until the schema catches up. When it
    /// does, this method's answer does not change.
    /// </remarks>
    public static MeasurementState CensusBucket(this EvalScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        if (score.Measurement != MeasurementState.Measured) return score.Measurement;

        return score.Label switch
        {
            "inapplicable" => MeasurementState.NotApplicable,
            "skipped" or "error" => MeasurementState.NotMeasured,
            _ => MeasurementState.Measured,
        };
    }

    /// <summary>Counts a set of scores into an <see cref="ObservationCensus"/>.</summary>
    /// <param name="scores">The scores to census.</param>
    /// <returns>The three-way census. Never <see langword="null"/>.</returns>
    /// <remarks>
    /// One-way by construction (ADR-030 §3.2): the meta types know nothing about
    /// <see cref="EvalScore"/>, and this adapter lives on the AgentEval side of the line.
    /// </remarks>
    public static ObservationCensus Census(this IEnumerable<EvalScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        int measured = 0, notApplicable = 0, notMeasured = 0;
        foreach (var score in scores)
        {
            switch (score.CensusBucket())
            {
                case MeasurementState.NotApplicable: notApplicable++; break;
                case MeasurementState.NotMeasured: notMeasured++; break;
                default: measured++; break;
            }
        }

        return new ObservationCensus(measured, notApplicable, notMeasured);
    }
}
