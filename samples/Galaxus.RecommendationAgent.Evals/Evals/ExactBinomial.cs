// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using AgentEval.Evals.Meta;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// The exact one-sided binomial test that decides whether an observed rate is above a chance floor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plan item 1.4 / design N-4.</b> The <b>forced-choice</b> "is this arm above chance?" decision
/// used to be <c>rate &gt; floor</c>. At the sizes this corpus actually has that is not a test, it is
/// a comparison of a point estimate to a constant, and it says YES to observations that a fair coin
/// produces routinely. Measured against the shipped forced-choice floor of 1/12 over 12 personas:
/// </para>
/// <list type="table">
///   <listheader><term>observed</term><description>old rule · exact upper-tail p · verdict now</description></listheader>
///   <item><term>2 of 12</term><description>▲ ABOVE · p = 0.26400914 · <b>not above chance</b></description></item>
///   <item><term>3 of 12</term><description>▲ ABOVE · p = 0.07201153 · <b>not above chance</b></description></item>
///   <item><term>4 of 12</term><description>▲ ABOVE · p = 0.01383043 · <b>above chance — the boundary</b></description></item>
///   <item><term>7 of 12</term><description>▲ ABOVE · p = 0.00001515 · <b>above chance, and now it says how strongly</b></description></item>
/// </list>
/// <para>
/// So the change removes the unearned ticks and keeps the earned ones. ⚠ <b>Its direction is
/// un-flattering by construction</b> — nothing here can turn a ▼ into a ▲, because
/// <see cref="UpperTailP"/> is monotone: an arm that fails <c>rate &gt; floor</c> has
/// <c>p ≥ P(X ≥ ⌈floor·n⌉)</c>, which cannot be small. That one-sidedness is why this is a
/// correction and not a re-scoring.
/// </para>
/// <para>
/// ⚠ <b>SCOPE, corrected 2026-09-06 by review. This is NOT every ▲ in the suite, and the first
/// revision of this file said it was.</b> Three decision sites route through
/// <see cref="AboveChance"/>: <c>EvalPrinter.PrintForcedChoice</c>,
/// <c>EvalPrinter.InstrumentCaveat</c>, and Eval 03's <c>LatentCoveragePersonaDiscrimination</c>.
/// All three test a <b>forced choice</b> against an exact 1/N null. ⚠ <b>Corrected 2026-09-06 by the
/// stage-2 smoke: the first revision of this sentence called that "a genuine Bernoulli trial", and
/// on a multi-rep arm it is not one.</b> A persona's forced-choice cell is
/// <c>CoverageScore.Mean</c>'s average over reps, so it takes values in {0, ⅓, ⅔, 1}; the panel used
/// to integerise the mean of those means with <c>Math.Floor(rate × personas)</c> and hand the result
/// here as a success count. It is a Bernoulli trial only after the reduction
/// <c>PairedCoverageReport.ForcedChoiceTally</c> now applies and prints — majority of that persona's
/// reps, a split rep a loss — and the panel says how many cells were split so the reduction is
/// visible rather than assumed. <b>Four other ▲ producers are still <c>rate &gt; floor</c> and are deliberately NOT
/// converted here</b> — <c>CoverageScore.AboveOwnFloor</c>, <c>CoverageScore.AbovePrecisionFloor</c>
/// (which together drive the latent-coverage, recall@k, precision@k and k_live panels <i>and</i>
/// Eval 02's GATE 1, through <c>PairedCoverageReport.EveryPersonaAboveOwnFloor</c>) and Eval 02b's
/// two per-case markers. The reason is not oversight: latent coverage is a mean over gold tokens
/// whose random-draw floor is the mean of <i>per-token</i> hit probabilities, so its null is
/// Poisson-binomial, not binomial, and this class would answer a question it was not asked.
/// Converting them needs the right test AND a declared movement in GATE 1, which is a behaviour
/// change and its own plan item. Until then, a ▲ outside the forced-choice panel means
/// <c>rate &gt; floor</c> and nothing more.
/// </para>
/// <para>
/// <b>Computed in log space, deliberately.</b> The naive product form loses the tail entirely once
/// <c>n</c> passes a few hundred — the defect ADR-030 §4.4's <c>SignP_SurvivesLargeN</c> exists to
/// pin — and this suite's Eval 09 pairs at n in the dozens today but is not bounded there. <b>The
/// log-space tail itself is now <see cref="ExactTests.BinomialTailP"/>'s</b>; this file used to
/// carry a second copy of it, down to its own <c>LogChoose</c> and <c>LogFactorial</c>.
/// </para>
/// <para>
/// ⚠ <b>What is left here is the DECISION and the RENDERING, not the maths</b> — and that is the
/// honest division. The library ships no console renderer on purpose (ADR-030 §6.3), and
/// <see cref="AboveChance"/> is this suite's forced-choice cut, not a general one: the library's
/// nearest equivalent, <c>FloorComparison.AboveFloor</c>, additionally refuses a comparison that
/// could not have reached α at any observation, which is right for a floor test built from
/// <c>Observation</c> rows and is not what the three call sites below hand it.
/// </para>
/// <para>
/// ⚠ <b>ONE guard is kept sample-side, and it is a finding about the library.</b>
/// <see cref="UpperTailP"/> refuses <c>successes &gt; trials</c> with NaN;
/// <see cref="ExactTests.BinomialTailP"/> CLAMPS it into range instead, so
/// <c>BinomialTailP(13, 12, 1/12)</c> returns <c>P(X ≥ 12)</c> ≈ 1e-13 — the most confident ▲ the
/// panel could print, for an observation that cannot happen. A clamp turns a broken caller into a
/// result, and it does so in the flattering direction. Reported, not patched here: changing a
/// shipped library's tail behaviour is not a sample's call to make.
/// </para>
/// <para>
/// <b>What it is NOT.</b> It is not a correction for multiplicity: several arms are tested against
/// the same floor in one panel and no Bonferroni or Holm adjustment is applied. The honest reading
/// of a single ▲ has to carry the family-wise error rate, and that rate depends on how many arms
/// the panel actually tested — so it is COMPUTED by <see cref="FamilyWiseErrorRate"/> from the arm
/// count of the run, never quoted from a constant. ⚠ The first revision of this file quoted 0.23
/// for "five arms"; the shipped panel tests <b>six</b>, whose rate is 0.265. A hard-coded family
/// size understates the error rate whenever the panel grows, which is the flattering direction.
/// The adjustment itself belongs with the library's <c>ExactTests</c>, not in a printer.
/// </para>
/// </remarks>
public static class ExactBinomial
{
    /// <summary>
    /// The significance level every caller in this suite uses. One number, one place — and that
    /// place is now the library's <see cref="ExactTests.DefaultAlpha"/>, so the suite cannot drift
    /// from the α the library's own floor comparisons apply.
    /// </summary>
    public const double Alpha = ExactTests.DefaultAlpha;

    /// <summary>
    /// P(X ≥ <paramref name="successes"/>) for X ~ Binomial(<paramref name="trials"/>,
    /// <paramref name="chance"/>), the exact one-sided upper tail.
    /// </summary>
    /// <param name="successes">Observed successes. Clamped into 0..trials.</param>
    /// <param name="trials">Trials. Zero trials give NaN — an empty denominator is not a result.</param>
    /// <param name="chance">The null rate. Outside (0, 1) gives NaN.</param>
    /// <returns>The upper-tail probability, or NaN when the question is not askable.</returns>
    /// <remarks>
    /// The tail is <see cref="ExactTests.BinomialTailP"/>'s. The three guards in front of it are
    /// this suite's, and the third one is the one that matters: the library CLAMPS
    /// <paramref name="successes"/> into <c>0..trials</c> where this refuses it, so an impossible
    /// observation would come back as a very small p rather than as "no answer". A caller that
    /// counted wrong must not be handed the panel's most confident tick. The other two agree with
    /// the library exactly — zero trials is NaN there too, and a clamped zero success count returns
    /// 1.0 — and are kept only so the contract reads at the point of use.
    /// </remarks>
    public static double UpperTailP(int successes, int trials, double chance)
    {
        if (trials <= 0) return double.NaN;
        if (double.IsNaN(chance) || chance <= 0.0 || chance >= 1.0) return double.NaN;
        if (successes > trials) return 0.0;

        return ExactTests.BinomialTailP(successes, trials, chance);
    }

    /// <summary>
    /// The decision every caller shares: is <paramref name="successes"/> of
    /// <paramref name="trials"/> above <paramref name="chance"/> at <see cref="Alpha"/>?
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Every FORCED-CHOICE "▲ above chance" must route through THIS method</b> — one copy of
    /// the rule is what lets the control row test the printer rather than a paraphrase of it. It is
    /// <b>not</b> every ▲ in the suite; the class remark above names the four markers still on
    /// <c>rate &gt; floor</c> and why converting them is a separate item.
    /// </para>
    /// <para>
    /// ⚠ <b>An undecidable comparison is not a pass and not a failure.</b> Zero trials give
    /// <c>P</c> = NaN and <c>Above</c> = false, and a caller that renders that as ▼ is telling a
    /// reader an arm LOST when it was never asked. Render NaN as "?" — the same convention
    /// <c>CoverageScore.AboveOwnFloor</c> already uses for an undefined comparison.
    /// </para>
    /// </remarks>
    /// <param name="successes">Observed successes.</param>
    /// <param name="trials">Trials.</param>
    /// <param name="chance">The null rate.</param>
    /// <returns>Whether it is above chance, and the exact p-value behind that answer.</returns>
    public static (bool Above, double P) AboveChance(int successes, int trials, double chance)
    {
        // ⚠ More successes than trials is not "certainly above chance", it is a broken caller.
        //   P(X >= 13 | n = 12) is 0, and returning that would print the most confident ▲ in the
        //   panel for an impossible observation — a wrong answer in the flattering direction.
        if (successes > trials) return (false, double.NaN);

        double p = UpperTailP(successes, trials, chance);
        return (!double.IsNaN(p) && p <= Alpha, p);
    }

    /// <summary>
    /// The probability that at least one of <paramref name="tests"/> independent tests at
    /// <see cref="Alpha"/> rejects a true null — the family-wise error rate a multi-arm panel
    /// carries when no correction is applied.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Computed from the run's own arm count, never quoted from a constant.</b> The first
    /// revision of this panel hard-coded <i>"with five arms … ≈ 0.23"</i> while printing six arms,
    /// whose rate is 0.265. A hard-coded family size can only ever understate the error rate as a
    /// panel grows, and understating it is the flattering direction: it makes a lone ▲ look safer
    /// than the panel it came from. Reported, not corrected — the correction belongs with ADR-030
    /// Slice 2.3's <c>ExactTests</c>.
    /// </remarks>
    /// <param name="tests">How many arms were actually tested. Fewer than two gives NaN.</param>
    /// <returns>1 − (1 − α)^tests, or NaN when there is no family.</returns>
    public static double FamilyWiseErrorRate(int tests) =>
        tests < 2 ? double.NaN : 1.0 - Math.Pow(1.0 - Alpha, tests);

    /// <summary>Renders a p-value for a console panel, never rounding a small one to zero.</summary>
    /// <param name="p">The p-value, possibly NaN.</param>
    public static string FormatP(double p) =>
        double.IsNaN(p) ? "p n/a"
        : p < 0.0001 ? "p < 0.0001"
        : string.Create(CultureInfo.InvariantCulture, $"p = {p:0.0000}");
}
