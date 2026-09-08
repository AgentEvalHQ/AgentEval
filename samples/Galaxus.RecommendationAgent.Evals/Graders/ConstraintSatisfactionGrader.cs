// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// One arm's answer to one stated need, scored as constraint-satisfaction PRECISION.
/// </summary>
/// <param name="Presented">Distinct SKUs presented (a repeat presentation of one SKU counts once).</param>
/// <param name="Phantom">Presented SKUs not in the catalogue. They count as presented and unsatisfied.</param>
/// <param name="Satisfied">Presented SKUs that satisfy every constraint of at least one slot.</param>
/// <param name="Precision"><c>Satisfied / Presented</c>; 0.0 when nothing was presented (see <paramref name="Silent"/>).</param>
/// <param name="Silent">
/// True when the arm presented nothing. On an APPLICABLE case that is a fail, not an abstention:
/// the customer stated a need with a right answer in the catalogue, so 0/0 is scored as 0 and
/// flagged, never as NaN and never as a pass.
/// </param>
/// <param name="SlotsCovered">How many of the case's slots at least one presented item satisfied.</param>
/// <param name="SlotTotal">How many slots the case has.</param>
/// <param name="PresentedSkus">The distinct SKUs, in presentation order.</param>
/// <param name="SatisfiedSkus">The satisfying subset, in presentation order.</param>
public readonly record struct ConstraintScore(
    int Presented,
    int Phantom,
    int Satisfied,
    double Precision,
    bool Silent,
    int SlotsCovered,
    int SlotTotal,
    IReadOnlyList<string> PresentedSkus,
    IReadOnlyList<string> SatisfiedSkus)
{
    /// <summary>Means several repetitions into one observation. Reps collapse BEFORE any comparison.</summary>
    /// <param name="reps">One or more scores of the same arm on the same case.</param>
    public static ConstraintScore Mean(IReadOnlyList<ConstraintScore> reps)
    {
        ArgumentNullException.ThrowIfNull(reps);
        if (reps.Count == 0) throw new ArgumentException("No repetitions to average.", nameof(reps));

        return new ConstraintScore(
            Presented: (int)Math.Round(reps.Average(r => (double)r.Presented)),
            Phantom: reps.Sum(r => r.Phantom),
            Satisfied: (int)Math.Round(reps.Average(r => (double)r.Satisfied)),
            Precision: reps.Average(r => r.Precision),
            Silent: reps.All(r => r.Silent),
            SlotsCovered: (int)Math.Round(reps.Average(r => (double)r.SlotsCovered)),
            SlotTotal: reps[0].SlotTotal,
            PresentedSkus: reps[0].PresentedSkus,
            SatisfiedSkus: reps[0].SatisfiedSkus);
    }
}

/// <summary>
/// Scores a presented list against a stated need's codified constraints, and derives the
/// chance floor that list is compared to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precision, on purpose — the channel Eval 02 has none of.</b> Latent coverage is a RECALL
/// measure and rises with k, so Eval 02's headline compared a five-item control against a
/// three-item agent and called it a sign test. Precision is k-invariant in expectation: a uniform
/// draw of ANY size from the catalogue scores <c>|S| / N</c>, so arms that present three items
/// and arms that present twelve are compared on one scale without a per-k floor.
/// </para>
/// <para>
/// <b>The floor is stated and executed.</b> <see cref="UniformDrawFloor"/> is the closed form;
/// Eval 02b also runs <c>Broken06_ConstraintBlindRecommender</c> — an actual uniform draw — and
/// prints both, because a floor that is only declared is a floor nobody has checked.
/// </para>
/// </remarks>
public static class ConstraintSatisfactionGrader
{
    /// <summary>Every catalogue product that satisfies the case — the gold, derived, never typed.</summary>
    /// <param name="testCase">The case.</param>
    public static IReadOnlyList<Product> SatisfyingSet(StatedNeedCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        var catalogue = Catalogue.Default;
        var customer = UserProfiles.Require(testCase.PersonaId);

        return
        [
            .. catalogue.All
                .Where(p => testCase.IsSatisfiedBy(p, customer, catalogue))
                .OrderBy(p => testCase.FirstSatisfiedSlot(p, customer, catalogue))
                .ThenBy(p => p.PriceChf)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
        ];
    }

    /// <summary>True when at least one catalogue product satisfies the case. False ⇒ NOT APPLICABLE, never a fail.</summary>
    /// <param name="testCase">The case.</param>
    public static bool IsApplicable(StatedNeedCase testCase) => SatisfyingSet(testCase).Count > 0;

    /// <summary>
    /// The expected precision of a uniform draw from the whole catalogue: <c>|S| / N</c>. Exact for
    /// any draw size by linearity of expectation, so it needs no k.
    /// </summary>
    /// <param name="testCase">The case.</param>
    public static double UniformDrawFloor(StatedNeedCase testCase)
    {
        int n = Catalogue.Default.All.Count;
        return n == 0 ? double.NaN : SatisfyingSet(testCase).Count / (double)n;
    }

    /// <summary>
    /// The variance of ONE uniform draw's precision at size <paramref name="k"/> — hypergeometric:
    /// <c>p (1 - p) (N - k) / ((N - 1) k)</c>. What the executed floor's band is built from.
    /// </summary>
    /// <param name="testCase">The case.</param>
    /// <param name="k">Draw size.</param>
    public static double UniformDrawVariance(StatedNeedCase testCase, int k)
    {
        int n = Catalogue.Default.All.Count;
        if (n <= 1 || k <= 0) return double.NaN;
        double p = UniformDrawFloor(testCase);
        return p * (1.0 - p) * (n - k) / ((n - 1.0) * k);
    }

    /// <summary>
    /// The standard deviation of the MEAN executed floor over <paramref name="cases"/>, each case
    /// averaged over <paramref name="draws"/> independent uniform draws of size <paramref name="k"/>:
    /// <c>sqrt(Σ Var_c(k) / draws) / |cases|</c>. The one band both Eval 02b's wiring panel and
    /// Eval 03's Broken06 row are built from, so the two panels cannot disagree about it.
    /// </summary>
    /// <param name="cases">The applicable cases.</param>
    /// <param name="k">Draw size.</param>
    /// <param name="draws">Draws per case.</param>
    /// <returns>NaN when there is nothing to average over.</returns>
    public static double UniformDrawSigmaOfMean(IReadOnlyList<StatedNeedCase> cases, int k, int draws)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (cases.Count == 0 || draws <= 0) return double.NaN;
        return Math.Sqrt(cases.Sum(c => UniformDrawVariance(c, k) / draws)) / cases.Count;
    }

    /// <summary>Grades what an arm presented against the case.</summary>
    /// <param name="testCase">The case.</param>
    /// <param name="presented">The <c>PresentRecommendation</c> calls, from the tool trace.</param>
    public static ConstraintScore Grade(StatedNeedCase testCase, IReadOnlyList<PresentedCall> presented)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(presented);

        var catalogue = Catalogue.Default;
        var customer = UserProfiles.Require(testCase.PersonaId);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presentedSkus = new List<string>();
        var satisfiedSkus = new List<string>();
        var slotsHit = new HashSet<int>();
        int phantom = 0;

        foreach (var call in presented)
        {
            string sku = call.Sku.Trim();
            if (sku.Length == 0 || !seen.Add(sku)) continue;
            presentedSkus.Add(sku);

            if (!catalogue.TryGet(sku, out var product) || product is null)
            {
                phantom++;
                continue;
            }

            int slot = testCase.FirstSatisfiedSlot(product, customer, catalogue);
            if (slot < 0) continue;

            satisfiedSkus.Add(product.Id);
            slotsHit.Add(slot);
        }

        bool silent = presentedSkus.Count == 0;
        double precision = silent ? 0.0 : satisfiedSkus.Count / (double)presentedSkus.Count;

        return new ConstraintScore(
            Presented: presentedSkus.Count,
            Phantom: phantom,
            Satisfied: satisfiedSkus.Count,
            Precision: precision,
            Silent: silent,
            SlotsCovered: slotsHit.Count,
            SlotTotal: testCase.Slots.Count,
            PresentedSkus: presentedSkus,
            SatisfiedSkus: satisfiedSkus);
    }
}
