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

    /// <summary>
    /// The ONE <see cref="AgentEval.Output.RunStats"/> bucket a check's score is counted in: not measured (skipped,
    /// errored, inapplicable, or a composite that withheld its pass) → <see cref="RunStatsBucket.Skipped"/>; else a
    /// <c>warn</c> → <see cref="RunStatsBucket.Warnings"/>; else <see cref="EvalScore.Passed"/> decides.
    /// </summary>
    /// <remarks>
    /// One exclusive chain, so the buckets always add up to the total (#203 review, B8). The runners used to count four
    /// independent predicates — a <c>warn</c> that was not measured was both a warning and skipped, a passed one both
    /// passed and skipped — and a contrived leaf set summed to 6 of 4; and the single-composite commands filed a
    /// skipped or errored result under Failed.
    /// </remarks>
    public static RunStatsBucket StatsBucket(this EvalScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        if (score.CensusBucket() != MeasurementState.Measured) return RunStatsBucket.Skipped;
        if (string.Equals(score.Label, "warn", StringComparison.Ordinal)) return RunStatsBucket.Warnings;
        return score.Passed ? RunStatsBucket.Passed : RunStatsBucket.Failed;
    }

    /// <summary>Counts a set of check scores into <see cref="AgentEval.Output.RunStats"/>, each in its one
    /// <see cref="StatsBucket"/>.</summary>
    /// <param name="scores">One score per check.</param>
    /// <returns>Stats whose four buckets add up to <c>Total</c>.</returns>
    public static AgentEval.Output.RunStats ToRunStats(this IEnumerable<EvalScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);

        int total = 0, passed = 0, failed = 0, warnings = 0, skipped = 0;
        foreach (var score in scores)
        {
            total++;
            switch (score.StatsBucket())
            {
                case RunStatsBucket.Passed: passed++; break;
                case RunStatsBucket.Failed: failed++; break;
                case RunStatsBucket.Warnings: warnings++; break;
                default: skipped++; break;
            }
        }

        return new AgentEval.Output.RunStats(total, passed, failed, warnings, skipped);
    }

    /// <summary>
    /// The status a report shows for a result: <c>PASS</c>, <c>WARN</c> or <c>FAIL</c> for a measured verdict;
    /// <c>ERROR</c> when it produced no verdict because the judge or its input failed; <c>SKIPPED</c> when nothing was
    /// measured (skipped or inapplicable).
    /// </summary>
    /// <remarks>
    /// One rule for every report (#203 review, B9b). The agentic, GDPR and EU AI Act reports each mapped every label but
    /// pass and warn to FAIL, so a judge that answered off its rubric's scale — or did not answer — read as an agent that
    /// failed: "FAIL 0%", "Review failures in …", and a run with nothing measured read "FAIL (score 100%)". An unknown
    /// label is read by the score's own pass flag.
    /// </remarks>
    public static string ReportStatus(this EvalScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        return score.Label.ToLowerInvariant() switch
        {
            "pass" => "PASS",
            "warn" => "WARN",
            "fail" => "FAIL",
            "error" => "ERROR",
            "skipped" or "inapplicable" => "SKIPPED",
            _ => score.Passed ? "PASS" : "FAIL",
        };
    }

    /// <summary>
    /// The verdict a run summary can carry (its schema allows <c>PASS</c>, <c>WARN</c>, <c>FAIL</c>, <c>PENDING</c>): the
    /// root's measured verdict; otherwise <c>PENDING</c> when no check was measured and <c>WARN</c> when some were — a run
    /// whose verdict errored is not a pass, and it is not a measured failure either (B9b).
    /// </summary>
    public static string RunVerdict(this EvalScore root, AgentEval.Output.RunStats stats)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(stats);

        return root.Label.ToLowerInvariant() switch
        {
            "pass" => "PASS",
            "warn" => "WARN",
            "fail" => "FAIL",
            _ => stats.Passed + stats.Failed + stats.Warnings == 0 ? "PENDING" : "WARN",
        };
    }

    /// <summary>
    /// Combines two <see cref="ReportStatus"/> values for a group (a category, a pillar): FAIL, then ERROR, then WARN win;
    /// a mix of PASS and SKIPPED is WARN — a group that passed on part of its checks is not a clean pass.
    /// </summary>
    public static string CombineReportStatus(string a, string b)
    {
        if (a == b) return a;
        if (a == "FAIL" || b == "FAIL") return "FAIL";
        if (a == "ERROR" || b == "ERROR") return "ERROR";
        return "WARN";
    }
}

/// <summary>The four buckets of <see cref="AgentEval.Output.RunStats"/>; see <see cref="EvalScoreExtensions.StatsBucket"/>.</summary>
public enum RunStatsBucket
{
    /// <summary>Measured and passed.</summary>
    Passed,

    /// <summary>Measured and failed.</summary>
    Failed,

    /// <summary>Measured, with a <c>warn</c> label.</summary>
    Warnings,

    /// <summary>Not measured: skipped, errored, inapplicable, or a withheld composite.</summary>
    Skipped,
}
