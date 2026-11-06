// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>
/// How <c>eval</c> records a case it did not measure: an error row with no score, never a pass or a failure, and in the
/// AEF run the state <c>not_measured</c> with a content-free reason.
/// </summary>
internal static class NotMeasuredCases
{
    /// <summary>The AEF reason of a case whose criteria were not graded because the run has no judge (no <c>--judge</c>).</summary>
    internal const string NoJudgeReason = "no judge was configured, so the case's criteria were not graded";

    /// <summary>Marks <paramref name="result"/> not measured, with <paramref name="mark"/> as its error.</summary>
    /// <param name="result">The case's (or trial's) result.</param>
    /// <param name="mark">The not-measured mark; never thrown.</param>
    internal static void Mark(TestResult result, Exception mark)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(mark);
        result.Passed = false;
        result.Score = 0;
        result.CriteriaResults = null;
        result.Failure = null;
        result.Error = mark;
        result.Details = "Error: " + mark.Message;
    }

    /// <summary>
    /// A case that declares evaluation criteria in a run without <c>--judge</c>: the harness only checked that the answer
    /// is not empty, so its pass says nothing about the criteria. Marks it not measured (the answer is kept), for every
    /// <c>eval</c> target.
    /// </summary>
    /// <param name="result">The case's (or trial's) result.</param>
    /// <param name="criteriaNotGraded">True when the case declares evaluation criteria and the run has no judge.</param>
    /// <returns>The case's AEF absence when it was marked; null when nothing applies or the result already has an error.</returns>
    internal static AefCaseAbsence? ReviewCriteria(TestResult result, bool criteriaNotGraded)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!criteriaNotGraded || result.Error is not null)
        {
            return null;
        }

        Mark(result, new CriteriaNotGradedException());
        return new AefCaseAbsence(AefState.NotMeasured, NoJudgeReason);
    }
}

/// <summary>
/// The not-measured mark of an <c>eval</c> case that declares evaluation criteria in a run without <c>--judge</c>: the
/// harness only checked that the answer is not empty, so no criterion was graded. Set as the case's
/// <see cref="TestResult.Error"/> (an error row, not a pass); never thrown.
/// </summary>
internal sealed class CriteriaNotGradedException : Exception
{
    /// <summary>Creates the mark.</summary>
    public CriteriaNotGradedException()
        : base("Not measured: the case declares evaluation criteria and no judge graded them (run eval with --judge to grade them). " +
            "The agent's answer is kept.")
    {
    }
}
