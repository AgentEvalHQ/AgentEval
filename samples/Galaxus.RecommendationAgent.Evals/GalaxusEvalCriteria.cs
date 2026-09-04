// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// ADVISORY criteria only. <b>No gate in this project reads this file.</b>
/// </summary>
/// <remarks>
/// <para>
/// It exists because a reviewer will reasonably ask "what would an LLM judge say?", and the answer
/// should be available without being load-bearing. Everything here is a hypothesis about the agent,
/// not a measurement of it: there is no gold set, no inter-rater agreement, and no calibration run
/// anywhere in this repository for these criteria.
/// </para>
/// <para>
/// <b>Why they are not wired into <c>TestCase.EvaluationCriteria</c>.</b> Supplying criteria flips
/// <c>MAFEvaluationHarness</c> into the judge branch and <c>TestResult.Passed</c> becomes the
/// judge's holistic number. Both evals construct a harness with NO evaluator, so that branch is
/// unreachable by construction rather than by configuration. If you want the advisory pass, run
/// Eval 01 with <c>--judge</c>, which uses
/// <see cref="Graders.RecommendationJustificationJudge"/> — one axis, three buckets, and an
/// instrument-failure column.
/// </para>
/// <para>
/// <b>Deliberately absent: fluency, coherence and tone.</b> A metric whose degenerate agent scores
/// 0.95 is a decoration. Free points carry no information, and printing them next to a
/// hard-won 14-of-14 would dilute the only numbers on the page that mean anything.
/// </para>
/// </remarks>
public static class GalaxusEvalCriteria
{
    /// <summary>
    /// The advisory rubric a reviewer might want to see applied to a whole turn. Never gated,
    /// never averaged into anything, and never printed without the sentence above.
    /// </summary>
    public static IReadOnlyList<string> Advisory { get; } =
    [
        "Every recommendation names at least one specific past purchase of this customer, by purchase id, "
      + "rather than referring vaguely to 'your history'.",

        "The covering note says what was NOT recommended and why — a gift-derived interest, a replenishment "
      + "item, or an inference the agent declined to make.",

        "No sentence states a price, a discount, a stock level or a delivery date. The interface prints "
      + "verified figures; any figure in the model's own text is wrong by construction.",

        "The answer is written in the customer's own language, and the reasoning does not depend on which "
      + "language the question arrived in.",

        "The agent says plainly that it recommends only and that the customer decides.",

        "Where the agent is unsure, it says so in that recommendation's reason instead of presenting it with "
      + "the same confidence as the rest.",
    ];

    /// <summary>
    /// The single axis the one implemented judge actually verifies. Stated here so the difference
    /// between "what we could ask a judge" and "what we do ask a judge" is visible in one place.
    /// </summary>
    public const string JudgedAxis =
        "Does the recommendation reason make only claims that are supported by the catalogue's product record "
      + "or the customer's own order history?";
}
