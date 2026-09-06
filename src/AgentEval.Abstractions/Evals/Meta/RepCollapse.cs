// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Meta;

/// <summary>
/// How N repetitions of the same (case, arm) become ONE observation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The unit of analysis is the case, not the rep.</b> Treating 3 reps of 12 cases as 36
/// independent observations adds no information — the reps share a case, a prompt and a corpus row —
/// but it shrinks every standard error by sqrt(3) and every p-value with it.
/// </para>
/// <para>
/// The collapse is MANDATORY and its strategy is DECLARED, because different strategies encode
/// different claims and <b>the flattering one must be visible in the code</b> rather than implied by
/// a default.
/// </para>
/// </remarks>
public enum RepCollapse
{
    /// <summary>The arithmetic mean of the reps. The default, and the weakest claim.</summary>
    Mean = 0,

    /// <summary>The median — robust to one pathological rep (a timeout, a truncated stream).</summary>
    Median = 1,

    /// <summary>Every rep passed. <b>"It does this every time"</b> — a reliability claim.</summary>
    All = 2,

    /// <summary>More than half the reps passed. A split is a loss, never half a win.</summary>
    Majority = 3,

    /// <summary>
    /// Any rep passed. <b>The flattering strategy</b> — it measures best-of-N, which is a different
    /// claim, and it rises with N for free. Permitted, always labelled "best-of-N".
    /// </summary>
    Any = 4,
}

/// <summary>
/// What the unit of analysis actually was, and what counting reps as independent would have cost.
/// </summary>
/// <param name="Cases">Distinct cases — the real n.</param>
/// <param name="TotalReps">Every repetition across those cases.</param>
/// <param name="MeanRepsPerCase">Reps per case, on average.</param>
/// <param name="Strategy">How the reps were collapsed.</param>
public sealed record ObservationUnit(int Cases, int TotalReps, double MeanRepsPerCase, RepCollapse Strategy)
{
    /// <summary>
    /// sqrt(mean reps per case) — the factor by which standard errors would have been UNDERSTATED
    /// had reps been counted as independent.
    /// </summary>
    /// <remarks>A number on the page, not a paragraph in a design document.</remarks>
    public double PseudoReplicationInflation => Math.Sqrt(Math.Max(MeanRepsPerCase, 0.0));

    /// <summary>
    /// True when the collapse is the one that rises with N for free, so a renderer can label it.
    /// </summary>
    public bool IsBestOfN => Strategy == RepCollapse.Any;

    /// <summary>Collapses one case's reps into a single value under <paramref name="strategy"/>.</summary>
    /// <param name="repValues">The repetition values. Must be non-empty.</param>
    /// <param name="strategy">The declared strategy.</param>
    /// <param name="passAt">
    /// The threshold a rep must reach to count as a pass, for the three pass-counting strategies.
    /// Ignored by <see cref="RepCollapse.Mean"/> and <see cref="RepCollapse.Median"/>.
    /// </param>
    /// <returns>
    /// The collapsed value. The pass-counting strategies return 1.0 or 0.0, so the result is a
    /// Bernoulli outcome an exact test can consume.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="repValues"/> is empty.</exception>
    public static double Collapse(IReadOnlyList<double> repValues, RepCollapse strategy, double passAt = 1.0)
    {
        ArgumentNullException.ThrowIfNull(repValues);
        if (repValues.Count == 0)
        {
            throw new ArgumentException(
                "No repetitions to collapse. An empty rep set is not a zero — it is a case that did not run.",
                nameof(repValues));
        }

        switch (strategy)
        {
            case RepCollapse.Mean:
                double sum = 0.0;
                foreach (double v in repValues) sum += v;
                return sum / repValues.Count;

            case RepCollapse.Median:
                var sorted = repValues.ToArray();
                Array.Sort(sorted);
                int mid = sorted.Length / 2;
                return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;

            default:
                int passed = 0;
                foreach (double v in repValues) if (v >= passAt) passed++;

                return strategy switch
                {
                    RepCollapse.All => passed == repValues.Count ? 1.0 : 0.0,
                    // A split rep is a LOSS. Half a win rounded up inside a significance test is
                    // exactly the integerisation defect this namespace refuses elsewhere.
                    RepCollapse.Majority => passed * 2 > repValues.Count ? 1.0 : 0.0,
                    RepCollapse.Any => passed > 0 ? 1.0 : 0.0,
                    _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown collapse strategy."),
                };
        }
    }
}
