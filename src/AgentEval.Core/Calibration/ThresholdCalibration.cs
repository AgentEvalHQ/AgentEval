// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;

namespace AgentEval.Calibration;

/// <summary>
/// One gold-labelled case: the score a judge or decision model gave it, and whether it is truly positive.
/// A higher score must mean "more likely positive".
/// </summary>
/// <param name="Score">The model's score for the case. Must be a finite number.</param>
/// <param name="Positive">The gold label.</param>
public readonly record struct ScoredCase(double Score, bool Positive);

/// <summary>What a decision cut is chosen to maximise.</summary>
public enum CutObjective
{
    /// <summary>The share of cases classified correctly.</summary>
    Accuracy = 0,

    /// <summary>
    /// The mean of the true-positive rate and the true-negative rate. On an imbalanced gold set, plain accuracy can be
    /// maximised by calling every case the majority class; balanced accuracy cannot.
    /// </summary>
    BalancedAccuracy = 1,
}

/// <summary>
/// The held-out check for a scored binary classifier: the cut is chosen on the training cases and applied unchanged
/// to the held-out cases, whose accuracy is the number to report.
/// </summary>
/// <param name="Cut">
/// The cut chosen on the training cases: a case is positive when its score is at or above it.
/// <see cref="double.NegativeInfinity"/> calls every case positive; <see cref="double.PositiveInfinity"/>, none.
/// </param>
/// <param name="Objective">What the cut maximised on the training cases.</param>
/// <param name="TrainingCount">Training cases.</param>
/// <param name="TrainingAccuracy">
/// Accuracy on the training cases at <paramref name="Cut"/>. Optimistic by construction: the cut was chosen to
/// maximise it on these same cases. Report <paramref name="HeldOutAccuracy"/>.
/// </param>
/// <param name="TrainingAuroc">AUROC on the training cases, or NaN when they hold one class only.</param>
/// <param name="HeldOutAccuracy">
/// Accuracy on the held-out cases at <paramref name="Cut"/>, with its 95% Wilson interval. Not measured
/// (<see cref="WilsonInterval.IsMeasured"/> false) when there are no held-out cases.
/// </param>
/// <param name="HeldOutAuroc">AUROC on the held-out cases, or NaN when they hold one class only or none.</param>
public sealed record HeldOutEvaluation(
    double Cut,
    CutObjective Objective,
    int TrainingCount,
    double TrainingAccuracy,
    double TrainingAuroc,
    WilsonInterval HeldOutAccuracy,
    double HeldOutAuroc);

/// <summary>
/// Calibration for a classifier that outputs a score rather than a verdict (an LLM judge's 0–100 score, a decision
/// model's probability): AUROC, which needs no cut, and accuracy at a cut chosen on one split and measured on another.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the split.</b> Choosing the cut that maximises accuracy and reporting that accuracy on the same cases
/// reports a maximum, which is biased upward, and more so on a small gold set. <see cref="EvaluateHeldOut"/> keeps the
/// two apart: the cut comes from the training cases only, and the held-out accuracy is computed once, at that cut.
/// </para>
/// <para>
/// <b>Undefined is not a number.</b> AUROC compares positives with negatives, so with only one class present it is
/// <see cref="double.NaN"/>, never 0.5. Like <see cref="AgreementMetrics.CohensKappa"/>, a <c>auroc &gt;= threshold</c>
/// gate then evaluates false.
/// </para>
/// </remarks>
public static class ThresholdCalibration
{
    /// <summary>
    /// The area under the ROC curve: the probability that a random positive case scores above a random negative case,
    /// counting a tie as half. 1 separates the classes perfectly, 0.5 is chance. <see cref="double.NaN"/> when
    /// <paramref name="cases"/> holds no positive or no negative case.
    /// </summary>
    /// <remarks>Computed from the Mann-Whitney rank sum with average ranks for tied scores, in O(n log n).</remarks>
    public static double Auroc(IReadOnlyList<ScoredCase> cases)
    {
        Validate(cases);
        var positives = cases.Count(c => c.Positive);
        var negatives = cases.Count - positives;
        if (positives == 0 || negatives == 0)
            return double.NaN;

        var sorted = cases.OrderBy(c => c.Score).ToList();
        double positiveRankSum = 0;
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1].Score == sorted[i].Score)
                j++;

            var averageRank = (i + j + 2) / 2.0;   // ranks are 1-based: positions i..j hold ranks i+1..j+1
            for (var k = i; k <= j; k++)
                if (sorted[k].Positive)
                    positiveRankSum += averageRank;
            i = j + 1;
        }

        return (positiveRankSum - positives * (positives + 1) / 2.0) / ((double)positives * negatives);
    }

    /// <summary>
    /// The cut that maximises <paramref name="objective"/> on <paramref name="cases"/>; a case is positive when its
    /// score is at or above the cut.
    /// </summary>
    /// <remarks>
    /// The candidates are the midpoints between consecutive distinct scores, plus <see cref="double.NegativeInfinity"/>
    /// (every case positive) and <see cref="double.PositiveInfinity"/> (none). Midpoints rather than the observed
    /// scores, so a new case that falls between two training scores is not decided by which of them happened to be
    /// sampled. When several cuts tie, the middle one of them is taken (the lower of the two middles for an even
    /// count), so the choice does not lean toward either extreme.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="cases"/> is empty, or holds a score that is not finite.</exception>
    public static double ChooseCut(IReadOnlyList<ScoredCase> cases, CutObjective objective = CutObjective.Accuracy)
    {
        Validate(cases);
        if (cases.Count == 0)
            throw new ArgumentException("A cut cannot be chosen on no cases.", nameof(cases));

        var distinct = cases.Select(c => c.Score).Distinct().Order().ToList();
        var candidates = new List<double>(distinct.Count + 1) { double.NegativeInfinity };
        for (var i = 1; i < distinct.Count; i++)
            candidates.Add(distinct[i - 1] + ((distinct[i] - distinct[i - 1]) / 2));
        candidates.Add(double.PositiveInfinity);

        var scored = candidates.Select(cut => (Cut: cut, Value: Objective(cases, cut, objective))).ToList();
        var best = scored.Max(s => s.Value);
        var tied = scored.Where(s => s.Value == best).Select(s => s.Cut).ToList();
        return tied[(tied.Count - 1) / 2];
    }

    /// <summary>
    /// Chooses the cut on <paramref name="training"/> and measures it, unchanged, on <paramref name="heldOut"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="training"/> is empty, or a score is not finite.</exception>
    public static HeldOutEvaluation EvaluateHeldOut(
        IReadOnlyList<ScoredCase> training,
        IReadOnlyList<ScoredCase> heldOut,
        CutObjective objective = CutObjective.Accuracy)
    {
        Validate(heldOut);
        var cut = ChooseCut(training, objective);
        var correctHeldOut = heldOut.Count(c => (c.Score >= cut) == c.Positive);

        return new HeldOutEvaluation(
            Cut: cut,
            Objective: objective,
            TrainingCount: training.Count,
            TrainingAccuracy: Objective(training, cut, CutObjective.Accuracy),
            TrainingAuroc: Auroc(training),
            HeldOutAccuracy: WilsonInterval.Compute(correctHeldOut, heldOut.Count),
            HeldOutAuroc: Auroc(heldOut));
    }

    /// <summary>
    /// Splits <paramref name="cases"/> into training and held-out sets, stratified by label, reproducibly for a given
    /// <paramref name="seed"/>. Each class contributes <c>round(count × heldOutFraction)</c> cases to the held-out set.
    /// </summary>
    /// <remarks>
    /// Fix the seed before looking at any result and report it with the numbers. Trying seeds until the held-out
    /// accuracy looks good turns the held-out set into a second training set.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="heldOutFraction"/> is not between 0 and 1.</exception>
    public static (IReadOnlyList<ScoredCase> Training, IReadOnlyList<ScoredCase> HeldOut) Split(
        IReadOnlyList<ScoredCase> cases, double heldOutFraction, int seed)
    {
        Validate(cases);
        if (double.IsNaN(heldOutFraction) || heldOutFraction <= 0 || heldOutFraction >= 1)
            throw new ArgumentOutOfRangeException(nameof(heldOutFraction), heldOutFraction,
                "The held-out fraction must be greater than 0 and less than 1.");

        var random = new Random(seed);
        var training = new List<ScoredCase>();
        var heldOut = new List<ScoredCase>();
        foreach (var label in new[] { true, false })
        {
            var group = cases.Where(c => c.Positive == label).ToList();
            for (var i = group.Count - 1; i > 0; i--)   // Fisher-Yates
            {
                var j = random.Next(i + 1);
                (group[i], group[j]) = (group[j], group[i]);
            }

            var take = (int)Math.Round(group.Count * heldOutFraction, MidpointRounding.AwayFromZero);
            heldOut.AddRange(group.Take(take));
            training.AddRange(group.Skip(take));
        }

        return (training, heldOut);
    }

    private static double Objective(IReadOnlyList<ScoredCase> cases, double cut, CutObjective objective)
    {
        int tp = 0, tn = 0, positives = 0;
        foreach (var c in cases)
        {
            var predicted = c.Score >= cut;
            if (c.Positive) positives++;
            if (predicted && c.Positive) tp++;
            else if (!predicted && !c.Positive) tn++;
        }

        if (objective == CutObjective.Accuracy)
            return cases.Count == 0 ? 0 : (double)(tp + tn) / cases.Count;

        // Balanced accuracy over the classes present: a class with no cases has no rate to average in.
        var negatives = cases.Count - positives;
        var rates = new List<double>(2);
        if (positives > 0) rates.Add((double)tp / positives);
        if (negatives > 0) rates.Add((double)tn / negatives);
        return rates.Count == 0 ? 0 : rates.Average();
    }

    private static void Validate(IReadOnlyList<ScoredCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        foreach (var c in cases)
            if (!double.IsFinite(c.Score))
                throw new ArgumentException($"Every score must be a finite number; found {c.Score}.", nameof(cases));
    }
}
