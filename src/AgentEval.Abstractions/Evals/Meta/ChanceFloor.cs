// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.Evals.Meta;

/// <summary>Whether a chance floor exists, and if not, why not. Never collapses to a number.</summary>
public enum FloorState
{
    /// <summary>The floor was derived from the corpus and the arm's declared budget.</summary>
    Derived = 0,

    /// <summary>
    /// No floor could be derived. <b>An absent floor is not a zero floor</b> — that is how a metric
    /// gets condemned at p = 0.70.
    /// </summary>
    NotDerivable = 1,
}

/// <summary>
/// k — the arm's DECLARED draw budget — and where that number came from.
/// </summary>
/// <param name="ArmId">The arm this budget belongs to.</param>
/// <param name="DeclaredDrawBudget">k, from a prompt constraint, a tool schema <c>maxItems</c>, a config key.</param>
/// <param name="BudgetSource">Where the number came from. <b>A k with no provenance is a k someone tuned.</b></param>
/// <remarks>
/// ADR-030 §4.3. The recorded defect: a deliberately implausible two-product dry-run stub read
/// ABOVE its own floor on 3 of 12 personas while a real arm at the identical rate read BELOW at
/// k = 12 — <b>the arm sized its own null</b>. Fixed-k is wrong the other way (one persona's floor
/// is 0.129 at k = 1 and 0.655 at k = 8). An arm that EXCEEDS its declared budget is a control
/// condition, not a floor question; silently re-deriving at the larger observed k is the defect.
/// </remarks>
public sealed record ArmProfile(string ArmId, int DeclaredDrawBudget, string BudgetSource);

/// <summary>
/// What an arm that understands nothing scores. Derived from the corpus and the arm's DECLARED
/// budget; it never sees a measurement.
/// </summary>
/// <param name="Kind">Which derivation produced it.</param>
/// <param name="State">Derived, or not derivable.</param>
/// <param name="RawValue">The point value. Read it through <see cref="Value"/>, which refuses when undefined.</param>
/// <param name="IntervalHigh">The upper bound when the floor was ESTIMATED; null when exact.</param>
/// <param name="Draws">k, as declared.</param>
/// <param name="PoolSize">The pool the draw came from.</param>
/// <param name="Derivation">One sentence naming the pool, the favourable set and k. Never empty.</param>
/// <remarks>
/// <para>
/// <b>An absent floor is not a zero floor.</b> <see cref="Value"/> THROWS when the floor is not
/// derived, so a caller cannot average an absence into a mean.
/// </para>
/// <para>
/// <b>An estimated floor carries its own uncertainty.</b> Analytic floors leave
/// <see cref="IntervalHigh"/> null. Empirical floors carry a Clopper-Pearson upper bound, and
/// comparisons must clear THAT (<see cref="ComparisonBar"/>), not <see cref="Value"/> — comparing
/// an observed rate to a point estimate computed from the same corpus is the co-moving-operands
/// failure.
/// </para>
/// </remarks>
public sealed record ChanceFloor(
    string Kind,
    FloorState State,
    double RawValue,
    double? IntervalHigh,
    int Draws,
    int PoolSize,
    string Derivation)
{
    /// <summary>Kind constant: at least one favourable member in a k-draw without replacement.</summary>
    public const string KindAtLeastOneHit = "hypergeometric-at-least-one";

    /// <summary>Kind constant: no forbidden member in a k-draw without replacement.</summary>
    public const string KindAvoidsAll = "hypergeometric-avoids-all";

    /// <summary>Kind constant: one uniform choice among N alternatives.</summary>
    public const string KindUniformChoice = "uniform-choice";

    /// <summary>Kind constant: the base rate of the positive class.</summary>
    public const string KindPriorRate = "prior-rate";

    /// <summary>Kind constant: measured by running an input-blind policy.</summary>
    public const string KindEmpiricalPolicy = "empirical-policy";

    /// <summary>Kind constant: no floor could be derived.</summary>
    public const string KindNotDerivable = "not-derivable";

    /// <summary>The floor's value. THROWS when it was not derived.</summary>
    /// <exception cref="InvalidOperationException">The floor is <see cref="FloorState.NotDerivable"/>.</exception>
    public double Value => State is FloorState.Derived
        ? RawValue
        : throw new InvalidOperationException(
            $"Chance floor not derived, so it has no value: {Derivation}. An absent floor is not a zero floor — "
            + "averaging an absence into a mean is how a metric gets condemned at p = 0.70.");

    /// <summary>The number a comparison must clear: the interval's upper bound when estimated.</summary>
    /// <exception cref="InvalidOperationException">The floor is <see cref="FloorState.NotDerivable"/>.</exception>
    public double ComparisonBar => IntervalHigh ?? Value;

    /// <summary>True when the bar is an interval bound rather than an exact value.</summary>
    public bool WasEstimated => IntervalHigh is not null;

    /// <summary>
    /// P(at least one of <paramref name="favourable"/> is drawn) in a k-draw without replacement.
    /// </summary>
    /// <param name="poolSize">N — the pool actually drawn from.</param>
    /// <param name="favourable">The favourable members of that pool.</param>
    /// <param name="draws">k, from the arm's DECLARED budget — never from its observed output.</param>
    /// <returns>A derived floor, or <see cref="NotDerivable"/> when the question is not askable.</returns>
    public static ChanceFloor AtLeastOneHit(int poolSize, int favourable, int draws)
    {
        if (poolSize <= 0 || draws <= 0)
        {
            return NotDerivable(string.Create(CultureInfo.InvariantCulture,
                $"an at-least-one floor needs a non-empty pool and a positive k; got pool {poolSize}, k {draws}"));
        }

        int f = Math.Clamp(favourable, 0, poolSize);
        int k = Math.Min(draws, poolSize);

        // P(miss all) = C(N-f, k) / C(N, k), computed as a product so nothing overflows.
        double miss = 1.0;
        for (int i = 0; i < k; i++)
        {
            miss *= (poolSize - f - i) / (double)(poolSize - i);
            if (miss <= 0.0) { miss = 0.0; break; }
        }

        return new(KindAtLeastOneHit, FloorState.Derived, Math.Clamp(1.0 - miss, 0.0, 1.0), null, k, poolSize,
            string.Create(CultureInfo.InvariantCulture,
                $"at least one of {f} favourable member(s) in a {k}-draw without replacement from a pool of {poolSize}"));
    }

    /// <summary>
    /// P(none of <paramref name="forbidden"/> is drawn) in a k-draw without replacement — the floor
    /// for an avoidance metric, where doing nothing scores well.
    /// </summary>
    /// <param name="poolSize">N — the pool actually drawn from.</param>
    /// <param name="forbidden">The members that must not appear.</param>
    /// <param name="draws">k, from the arm's DECLARED budget.</param>
    /// <returns>A derived floor, or <see cref="NotDerivable"/> when the question is not askable.</returns>
    /// <remarks>
    /// ⚠ This floor is high by construction and rises as k falls: an arm that presents nothing
    /// scores 1.000 against it. That is exactly why it must be printed — an avoidance rate with no
    /// floor beside it reads as a safety result when it is a silence result.
    /// </remarks>
    public static ChanceFloor AvoidsAll(int poolSize, int forbidden, int draws)
    {
        if (poolSize <= 0 || draws <= 0)
        {
            return NotDerivable(string.Create(CultureInfo.InvariantCulture,
                $"an avoids-all floor needs a non-empty pool and a positive k; got pool {poolSize}, k {draws}"));
        }

        int f = Math.Clamp(forbidden, 0, poolSize);
        int k = Math.Min(draws, poolSize);

        double avoid = 1.0;
        for (int i = 0; i < k; i++)
        {
            avoid *= (poolSize - f - i) / (double)(poolSize - i);
            if (avoid <= 0.0) { avoid = 0.0; break; }
        }

        return new(KindAvoidsAll, FloorState.Derived, Math.Clamp(avoid, 0.0, 1.0), null, k, poolSize,
            string.Create(CultureInfo.InvariantCulture,
                $"none of {f} forbidden member(s) in a {k}-draw without replacement from a pool of {poolSize}"));
    }

    /// <summary>One uniform choice among <paramref name="alternatives"/>.</summary>
    /// <param name="alternatives">How many alternatives were on offer.</param>
    /// <returns>A derived floor of 1/N, or <see cref="NotDerivable"/> below two alternatives.</returns>
    /// <remarks>
    /// Fewer than two alternatives is NOT a floor of 1.0 — it is a forced choice with nothing to
    /// choose between, and scoring an arm against it says nothing about the arm.
    /// </remarks>
    public static ChanceFloor UniformChoice(int alternatives) =>
        alternatives < 2
            ? NotDerivable(
                string.Create(CultureInfo.InvariantCulture, $"a uniform choice needs at least two alternatives; got {alternatives}.")
                + " One alternative is not a floor of 1.0, it is a question with one answer")
            : new(KindUniformChoice, FloorState.Derived, 1.0 / alternatives, null, 1, alternatives,
                string.Create(CultureInfo.InvariantCulture, $"one uniform choice among {alternatives} alternatives"));

    /// <summary>The base rate of the positive class — what "always say yes" scores.</summary>
    /// <param name="positives">Positive cases in the corpus.</param>
    /// <param name="total">All cases in the corpus.</param>
    /// <returns>A derived floor, or <see cref="NotDerivable"/> on an empty corpus.</returns>
    public static ChanceFloor PriorRate(int positives, int total) =>
        total <= 0
            ? NotDerivable("a prior rate needs a non-empty corpus")
            : new(KindPriorRate, FloorState.Derived, Math.Clamp(positives / (double)total, 0.0, 1.0), null, 0, total,
                string.Create(CultureInfo.InvariantCulture,
                    $"the base rate of the positive class, {positives} of {total}"));

    /// <summary>
    /// A floor MEASURED by running an input-blind policy.
    /// </summary>
    /// <param name="successes">Successes the blind policy scored.</param>
    /// <param name="trials">Trials it was run over.</param>
    /// <param name="policiesConsidered">
    /// How many constant policies were considered. <b>MANDATORY.</b> "The best constant policy" is a
    /// MAXIMUM over a family, and a maximum selected on the same corpus the agent is scored on is
    /// optimistically biased.
    /// </param>
    /// <param name="heldOutFrom">
    /// The split the constant was chosen on. Required once more than one policy was considered.
    /// </param>
    /// <returns>An estimated floor carrying its own Clopper-Pearson upper bound.</returns>
    /// <exception cref="ArgumentException">
    /// More than one policy was considered and no held-out split was named.
    /// </exception>
    /// <remarks>
    /// The recorded instance: a ceiling TYPED as 8 and MEASURED at 10. Selection over a family on
    /// the scored corpus is how you get 10. Because the value is an estimate, its
    /// <see cref="IntervalHigh"/> is populated and <see cref="ComparisonBar"/> returns THAT — a
    /// comparison against the point estimate would be comparing two numbers computed from the same
    /// rows.
    /// </remarks>
    public static ChanceFloor Empirical(int successes, int trials, int policiesConsidered, string? heldOutFrom = null)
    {
        if (policiesConsidered < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policiesConsidered),
                "At least one policy must have been considered; a floor nobody ran is not empirical.");
        }

        if (policiesConsidered > 1 && string.IsNullOrWhiteSpace(heldOutFrom))
        {
            throw new ArgumentException(
                $"{policiesConsidered} policies were considered, so this floor is a MAXIMUM over a family. "
                + "A maximum selected on the same corpus the arm is scored on is optimistically biased — name the "
                + $"held-out split the constant was chosen on in '{nameof(heldOutFrom)}', or consider one policy.",
                nameof(heldOutFrom));
        }

        if (trials <= 0)
        {
            return NotDerivable("an empirical floor needs at least one trial of the blind policy");
        }

        int s = Math.Clamp(successes, 0, trials);
        var interval = ExactTests.ClopperPearson(s, trials);

        return new(KindEmpiricalPolicy, FloorState.Derived, s / (double)trials, interval.High, 0, trials,
            string.Create(CultureInfo.InvariantCulture, $"an input-blind policy scored {s} of {trials}; {policiesConsidered} policy/policies considered")
                + (string.IsNullOrWhiteSpace(heldOutFrom) ? string.Empty : $", constant chosen on '{heldOutFrom}'")
                + string.Create(CultureInfo.InvariantCulture, $". ESTIMATED — comparisons clear the interval bound {interval.High:F4}, not the point {s / (double)trials:F4}"));
    }

    /// <summary>Not derivable. The reason is mandatory and prints where the floor would have.</summary>
    /// <param name="reason">Why no floor exists. Never empty.</param>
    /// <returns>A floor whose <see cref="Value"/> throws.</returns>
    public static ChanceFloor NotDerivable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(KindNotDerivable, FloorState.NotDerivable, double.NaN, null, 0, 0, reason);
    }
}

/// <summary>
/// An arm TESTED against a floor. A per-case <c>value &gt; floor</c> is a disposition; this is the test.
/// </summary>
/// <param name="ArmId">The arm.</param>
/// <param name="Successes">Cases the arm succeeded on.</param>
/// <param name="Trials">Measured cases — <b>never</b> the total, which would dilute the denominator.</param>
/// <param name="FloorUsed">The bar actually cleared: <see cref="ChanceFloor.ComparisonBar"/>.</param>
/// <param name="FloorWasEstimated">Whether that bar is an interval bound.</param>
/// <param name="PValue">One-sided binomial upper tail against <paramref name="FloorUsed"/>.</param>
/// <param name="ObservedRate">The exact interval around the observed rate.</param>
/// <param name="Census">What went into the denominator, and what was excluded.</param>
/// <param name="MinimumAttainableP">The smallest p this design could ever have produced.</param>
public sealed record FloorComparison(
    string ArmId,
    int Successes,
    int Trials,
    double FloorUsed,
    bool FloorWasEstimated,
    double PValue,
    (double Low, double High) ObservedRate,
    ObservationCensus Census,
    double MinimumAttainableP)
{
    /// <summary>No observation at this n could have reached α. A property of the DESIGN.</summary>
    public bool UnderpoweredByConstruction => MinimumAttainableP > ExactTests.DefaultAlpha;

    /// <summary>
    /// A DIRECTION, never a verdict. False when the comparison could not have reached α whatever
    /// the arm did.
    /// </summary>
    public bool AboveFloor =>
        !double.IsNaN(PValue) && PValue <= ExactTests.DefaultAlpha && !UnderpoweredByConstruction;

    /// <summary>
    /// Tests one arm's observations against a floor with the exact one-sided binomial tail.
    /// </summary>
    /// <param name="observations">
    /// Every observation for the arm, already collapsed to one per case. <b>Measured values must be
    /// 0.0 or 1.0</b> — see the remarks.
    /// </param>
    /// <param name="armId">The arm being tested. Observations for other arms are ignored.</param>
    /// <param name="floor">The floor. A <see cref="FloorState.NotDerivable"/> floor yields a NaN p, never a pass.</param>
    /// <returns>The comparison, with its census, its interval and its minimum attainable p.</returns>
    /// <exception cref="ArgumentException">
    /// A MEASURED observation carries a value that is neither 0 nor 1.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Non-applicable and non-measured observations are EXCLUDED from the denominator</b>, not
    /// scored as failures. They are counted in <see cref="Census"/>, which is what stops a mean over
    /// 3 of 12 rendering identically to a mean over 12 of 12.
    /// </para>
    /// <para>
    /// <b>Why a fractional value is refused rather than rounded.</b> A binomial tail is a statement
    /// about Bernoulli trials. Feeding it a per-case MEAN — the average of several repetitions, or a
    /// coverage fraction — and rounding to a success count integerises the statistic before testing
    /// it, and that is not a rounding nicety: measured on this repository's own coverage corpus, one
    /// case's rep-mean of 0.778 tested as "2 of 3" reads p = 0.063 (NOT above) where the correct
    /// null reads p = 0.002 (well above) — a per-case verdict flip caused entirely by the rounding.
    /// Collapse reps with <see cref="RepCollapse"/> to a 0/1 outcome first, and if the quantity is
    /// genuinely continuous then a binomial tail is the wrong test and this method refuses to
    /// pretend otherwise.
    /// </para>
    /// </remarks>
    public static FloorComparison Compute(
        IReadOnlyList<Observation> observations, string armId, ChanceFloor floor)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentException.ThrowIfNullOrWhiteSpace(armId);
        ArgumentNullException.ThrowIfNull(floor);

        int measured = 0, notApplicable = 0, notMeasured = 0, successes = 0;

        foreach (var observation in observations)
        {
            if (!string.Equals(observation.ArmId, armId, StringComparison.Ordinal)) continue;

            switch (observation.State)
            {
                case MeasurementState.NotApplicable: notApplicable++; continue;
                case MeasurementState.NotMeasured: notMeasured++; continue;
                default: break;
            }

            if (observation.Value is not (0.0 or 1.0))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Case '{observation.CaseId}' carries a measured value of {observation.Value}.")
                    + " A binomial tail is a statement about Bernoulli trials. Collapse reps to a 0/1 outcome first "
                    + $"({nameof(RepCollapse)}), or use a test appropriate to a continuous statistic. Rounding it to a "
                    + "success count integerises the statistic before testing it, which is a per-case verdict flip in "
                    + "this repository's own measured record.",
                    nameof(observations));
            }

            measured++;
            if (observation.Value == 1.0) successes++;
        }

        var census = new ObservationCensus(measured, notApplicable, notMeasured);
        double bar = floor.State is FloorState.Derived ? floor.ComparisonBar : double.NaN;

        return new(
            ArmId: armId,
            Successes: successes,
            Trials: measured,
            FloorUsed: bar,
            FloorWasEstimated: floor.WasEstimated,
            PValue: ExactTests.BinomialTailP(successes, measured, bar),
            ObservedRate: ExactTests.ClopperPearson(successes, measured),
            Census: census,
            MinimumAttainableP: measured <= 0
                ? 1.0
                : ExactTests.BinomialTailP(measured, measured, double.IsNaN(bar) ? 0.5 : bar));
    }
}
