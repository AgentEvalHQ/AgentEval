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
/// <summary>
/// One advisory criterion, and whether an answer that recommends NOTHING satisfies it without
/// asserting anything.
/// </summary>
/// <param name="Text">The criterion as it is sent to the judge.</param>
/// <param name="VacuousOnAnAnswerWithNoRecommendations">
/// <see langword="true"/> when the criterion quantifies over the set of presented recommendations,
/// so an answer that presents none meets it by the arithmetic of the empty set.
/// </param>
/// <remarks>
/// <para>
/// ⚠ <b>This flag is an INPUT-side property of the criterion's logic, and that is the whole point.</b>
/// Eval 09's judge panel used to infer vacuity from the RESULT — <c>floor met rate ≥ 0.999</c> — which
/// is the same shape as reading applicability out of an outcome instead of out of the case. Measured
/// on the 2026-09-05 paid run, that inference was wrong on two of the three rows it fired on: the
/// contentless floor arm met criteria 3 and 5 because its answer <b>says those things in so many
/// words</b>, by design, not because an empty answer satisfies them. Labelling those rows "vacuous"
/// discounted a real finding — on criterion 5 the workflow scored 0.000 against a floor that had
/// EARNED 1.000, at p = 0.0005.
/// </para>
/// <para>
/// A high floor and a vacuous criterion are two different facts. They are now printed as two
/// different facts, and a DISAGREEMENT between them is itself informative: a criterion declared
/// vacuous whose floor comes back 0.000 says the judge did not read it vacuously, which is a
/// calibration observation nobody had.
/// </para>
/// </remarks>
public sealed record JudgedCriterion(string Text, bool VacuousOnAnAnswerWithNoRecommendations);

public static class GalaxusEvalCriteria
{
    /// <summary>
    /// The advisory rubric a reviewer might want to see applied to a whole turn, each entry carrying
    /// its own vacuity declaration. Never gated, never averaged into anything, and never printed
    /// without the sentence above.
    /// </summary>
    public static IReadOnlyList<JudgedCriterion> AdvisoryCriteria { get; } =
    [
        // ⚠ VACUOUS BY LOGIC, and NOT restated here. "Every recommendation …" over an empty set is
        //   true. Measured on the paid run the floor arm scored 0.000 on it, so the judge did not
        //   read it vacuously — the declaration and the measurement disagree, and both are printed.
        //   Restating it would move a shipped number with no measurement behind the new one.
        new("Every recommendation names at least one specific past purchase of this customer, by purchase id, "
          + "rather than referring vaguely to 'your history'.", true),

        new("The covering note says what was NOT recommended and why — a gift-derived interest, a replenishment "
          + "item, or an inference the agent declined to make.", false),

        new("No sentence states a price, a discount, a stock level or a delivery date. The interface prints "
          + "verified figures; any figure in the model's own text is wrong by construction.", false),

        // ⚠ RESTATED 2026-09-06 (plan item 8.16 #5). The superseded wording was
        //   "The answer is written in the customer's own language, and the reasoning does not depend
        //    on which language the question arrived in."
        //   — a conjunction whose second half quantifies over "the reasoning", so an answer with no
        //   reasoning met it by the arithmetic of the empty set. On the 2026-09-05 paid run the
        //   contentless floor arm scored 1.000 on it and BOTH live arms scored 0.000: a floor that
        //   cannot lose does not make an arm's 0.000 harsh, it makes it uninterpretable, which is
        //   the same reason this eval already excludes cells from an arm that presented nothing.
        //   The restatement adds an EXISTENTIAL: the criterion is now unmeetable without a reason
        //   being present. ⚠ It changes the text sent to the judge, so the paid run's criterion-4
        //   numbers describe a different rubric and are superseded, not corrected — confirming the
        //   new ones needs a judged run.
        new("At least one recommendation reason is present, and every recommendation reason is written in the "
          + "customer's own language; an answer that gives no recommendation reasons does NOT meet this "
          + "criterion. The reasoning must also not depend on which language the question arrived in.", false),

        new("The agent says plainly that it recommends only and that the customer decides.", false),

        // ⚠ VACUOUS BY LOGIC, and NOT restated here, for the same reason as criterion 1: "Where the
        //   agent is unsure …" is a conditional over recommendations and is true when there are
        //   none. Measured floor 0.000 — declaration and measurement disagree, and both print.
        new("Where the agent is unsure, it says so in that recommendation's reason instead of presenting it with "
          + "the same confidence as the rest.", true),
    ];

    /// <summary>
    /// The criterion TEXTS, in order — the form the judge and the harness consume.
    /// </summary>
    /// <remarks>
    /// Projected from <see cref="AdvisoryCriteria"/> rather than duplicated. A second copy is how a
    /// rubric acquires two vintages, and this repository has already shipped that defect once.
    /// </remarks>
    public static IReadOnlyList<string> Advisory { get; } = [.. AdvisoryCriteria.Select(c => c.Text)];

    /// <summary>
    /// The exact wording criterion 4 carried before 2026-09-06, frozen so the control that proves
    /// the restatement can hold a specimen it did not write today.
    /// </summary>
    /// <remarks>
    /// A control that only ever sees the corrected text cannot show it is able to reject the broken
    /// one. This constant is that control's positive specimen, and it is a historical fact rather
    /// than a fixture: it is what the 2026-09-05 paid run sent to the judge.
    /// </remarks>
    public const string SupersededLanguageCriterion =
        "The answer is written in the customer's own language, and the reasoning does not depend on which "
      + "language the question arrived in.";

    /// <summary>
    /// The single axis the one implemented judge actually verifies. Stated here so the difference
    /// between "what we could ask a judge" and "what we do ask a judge" is visible in one place.
    /// </summary>
    public const string JudgedAxis =
        "Does the recommendation reason make only claims that are supported by the catalogue's product record "
      + "or the customer's own order history?";
}
