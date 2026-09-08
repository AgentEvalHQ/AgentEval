// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Evals.Cases;

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// The null distribution GATE 1 has never been tested against: what latent coverage a UNIFORM draw
/// actually produces, simulated, rather than what it produces ON AVERAGE.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>The defect this exists to make visible.</b> GATE 1's shipped predicate is
/// <c>Latent &gt; LatentFloor</c> (<c>CoverageScore.AboveOwnFloor</c>), and <c>LatentFloor</c> is
/// <see cref="ChanceFloors.RandomDrawFloor"/> — the ANALYTIC MEAN of the null. Comparing an observed
/// value to a null's mean is not a test: a coin flip clears it half the time. A test needs the null's
/// SPREAD, and the only honest way to get it here is to draw.
/// </para>
/// <para>
/// ⚠ <b>ADR-030 Q6 is answered "yes on the principle, STAGED in execution", and this is the staging.</b>
/// Nothing here gates anything. It reports what the binding test WOULD say, beside what the shipped
/// gate DOES say, so the movement can be published with its date and its cause before any default
/// changes. The one thing that must not happen is this quietly becoming the gate.
/// </para>
/// <para>
/// 🔴 <b>Why the null is a MEAN OF REPS and not a single draw.</b> The observed cell is a mean of the
/// arm's repetitions, so the null must be a mean of the same number of draws or the two are not the
/// same statistic. Averaging shrinks the null's spread, so a single-draw null is too WIDE and admits
/// too few — ADR-030 §9 measured 9 of 12 for a single draw against 10 of 12 once the rep-averaging is
/// modelled. Getting this wrong is conservative rather than flattering, which is the safer error, but
/// it is still the wrong statistic.
/// </para>
/// <para>
/// ⚠ <b>And why NOT <c>ExactBinomial.AboveChance(LatentServed, LatentTotal, LatentFloor)</c>, which
/// is what Slice 2.6's acceptance originally named.</b> <c>LatentServed</c> is <c>Math.Round</c> of a
/// rep-mean, so that call integerises the statistic before testing it — the same defect corrected at
/// <c>9407cfbd</c>. Measured on this corpus it flips <c>USR-PB-11</c> on the rounding alone:
/// <b>p = 0.0629 (not above) against a simulated 0.0019 (well above)</b>. Shipping the binomial
/// substitution would ship a known verdict flip.
/// </para>
/// </remarks>
public static class SimulatedLatentNull
{
    /// <summary>
    /// The seed. FIXED, and printed with every result: a Monte-Carlo p-value that changes between
    /// runs is not a reproducible measurement.
    /// </summary>
    public const int Seed = 20260907;

    /// <summary>How many null samples to draw. Each sample is a mean of <c>reps</c> draws.</summary>
    public const int DefaultSamples = 200_000;

    /// <summary>One persona's answer, with everything a reader needs to check it.</summary>
    /// <param name="Observed">The arm's latent coverage on this persona.</param>
    /// <param name="PoolSize">Products a uniform draw could have picked from.</param>
    /// <param name="Draws">k — the declared draw size.</param>
    /// <param name="Reps">How many draws each null sample averages, matching the observed cell.</param>
    /// <param name="Samples">Null samples drawn.</param>
    /// <param name="AtLeastObserved">Null samples that reached <paramref name="Observed"/>.</param>
    /// <param name="PValue">The Monte-Carlo tail. Never zero — see the remarks.</param>
    public readonly record struct PersonaNull(
        double Observed, int PoolSize, int Draws, int Reps, int Samples, int AtLeastObserved, double PValue)
    {
        /// <summary>Whether the observation clears the simulated null at α = 0.05.</summary>
        public bool AboveNull => PValue <= 0.05;
    }

    /// <summary>
    /// Simulates the null for one persona and returns the Monte-Carlo tail.
    /// </summary>
    /// <param name="gold">The persona's gold interest map — the same one the floor is derived from.</param>
    /// <param name="observedLatent">The arm's observed latent coverage.</param>
    /// <param name="draws">k, the declared draw size.</param>
    /// <param name="reps">Repetitions the observed cell averages. Must be at least 1.</param>
    /// <param name="samples">Null samples to draw.</param>
    /// <returns>The result, or <see langword="null"/> when the persona is unscorable.</returns>
    /// <remarks>
    /// <para>
    /// The pool, the token vocabulary and the hit rule are taken from
    /// <see cref="ChanceFloors.RandomDrawFloor"/> and <see cref="InterestMapGold.EligibleTokens"/> —
    /// the SAME ones the metric and the analytic floor use. A null over a different vocabulary is a
    /// null for a different metric.
    /// </para>
    /// <para>
    /// ⚠ <b>The p-value is <c>(1 + hits) / (1 + samples)</c>, never <c>hits / samples</c>.</b> Zero
    /// hits in 200,000 draws does not mean p = 0; it means p is smaller than this simulation can
    /// resolve, and reporting 0 would claim a certainty the method cannot deliver.
    /// </para>
    /// </remarks>
    public static PersonaNull? For(
        GoldInterestMap gold,
        double observedLatent,
        int draws = ChanceFloors.DegenerateDrawSize,
        int reps = 1,
        int samples = DefaultSamples)
    {
        ArgumentNullException.ThrowIfNull(gold);
        ArgumentOutOfRangeException.ThrowIfLessThan(reps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(draws, 1);

        if (gold.LatentIsEmpty || double.IsNaN(observedLatent)) return null;

        // The SAME pool the analytic floor draws from: everything the persona does not already own.
        var pool = Catalogue.Default.All
            .Where(p => !gold.OwnedCategories.Contains(p.LeafCategory))
            .ToList();

        if (pool.Count == 0 || draws > pool.Count) return null;

        // Pre-compute, per product, which latent tokens it carries. The inner loop then touches no
        // strings at all — 200,000 samples × reps × k product lookups has to be cheap or nobody runs it.
        var latent = gold.Latent.ToList();
        var carriers = new bool[pool.Count][];
        for (int i = 0; i < pool.Count; i++)
        {
            var tokens = InterestMapGold.EligibleTokens(pool[i]);
            carriers[i] = [.. latent.Select(tokens.Contains)];
        }

        // DevSkim: ignore all
        // Deterministic by REQUIREMENT, not by oversight. This is a Monte-Carlo null for a
        // statistical test, not a security function: a cryptographic RNG would make the p-value
        // change between runs, and a p-value that changes between runs is not a measurement. The
        // seed is a published constant for exactly that reason (see Seed's remarks).
        var random = new Random(Seed);
        var indices = Enumerable.Range(0, pool.Count).ToArray();
        var covered = new bool[latent.Count];
        int atLeastObserved = 0;

        // A strict > would call a null sample that TIES the observation a miss, which understates the
        // tail. The convention throughout this repository is that a tie counts against the claim.
        const double Tolerance = 1e-9;

        for (int sample = 0; sample < samples; sample++)
        {
            double total = 0.0;

            for (int rep = 0; rep < reps; rep++)
            {
                Array.Clear(covered);

                // Partial Fisher–Yates: k swaps give a uniform k-subset without replacement, which is
                // what the arm's budget is. Sampling WITH replacement would model a different draw.
                for (int d = 0; d < draws; d++)
                {
                    int j = random.Next(d, indices.Length);
                    (indices[d], indices[j]) = (indices[j], indices[d]);

                    var row = carriers[indices[d]];
                    for (int t = 0; t < covered.Length; t++)
                    {
                        covered[t] |= row[t];
                    }
                }

                int hits = 0;
                for (int t = 0; t < covered.Length; t++) if (covered[t]) hits++;
                total += hits / (double)latent.Count;
            }

            if (total / reps >= observedLatent - Tolerance) atLeastObserved++;
        }

        return new PersonaNull(
            Observed: observedLatent,
            PoolSize: pool.Count,
            Draws: draws,
            Reps: reps,
            Samples: samples,
            AtLeastObserved: atLeastObserved,
            PValue: (1.0 + atLeastObserved) / (1.0 + samples));
    }
}
