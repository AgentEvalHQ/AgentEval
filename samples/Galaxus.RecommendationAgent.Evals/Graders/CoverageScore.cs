// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// One arm's coverage of one persona's gold interest map.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two channels, two floors, never one number.</b> <see cref="Latent"/> is RECALL — the share
/// of latent gold tokens the answer served — and it is monotone in k: an arm that presents more
/// covers more by luck alone, which is why its floor is derived at the arm's k and rises with it.
/// <see cref="PrecisionAtK"/> is the other channel: of the k slots the customer was promised, how
/// many carried an item that actually serves a latent interest. Its floor is R/N and does not move
/// with k at all. An arm that presents twelve items to fill five slots can raise the first number;
/// it cannot raise the second. Reading either alone is reading half the answer.
/// </para>
/// </remarks>
/// <param name="Latent">Latent coverage (recall) in [0,1], or NaN when the gold set was empty.</param>
/// <param name="Manifest">Manifest coverage in [0,1], or NaN when the gold set was empty.</param>
/// <param name="LatentServed">Latent tokens served.</param>
/// <param name="LatentTotal">Latent tokens in the gold set.</param>
/// <param name="ManifestServed">Manifest categories served.</param>
/// <param name="ManifestTotal">Manifest categories in the gold set.</param>
/// <param name="PresentedCount">
/// How many recommendations were SCORED. Equal to what the arm presented when no budget was
/// declared; equal to min(<paramref name="DeclaredK"/>, presented) when the answer was cut to a
/// declared budget. The floor is derived at this number.
/// </param>
/// <param name="NewCategoryCount">How many of them reached a category the customer has not bought from.</param>
/// <param name="PhantomCount">
/// How many presented SKUs were not in the catalogue. Recorded here too, so a run whose coverage
/// looks fine but is built on invented ids cannot be read as a good result.
/// </param>
/// <param name="LatentFloor">
/// The random-draw RECALL floor for THIS arm, computed at k = <paramref name="PresentedCount"/>
/// rather than at a fixed 5. NaN when the gold set was empty.
/// </param>
/// <param name="ForcedChoice">
/// 1 when this persona's own gold scored STRICTLY highest of every persona's gold on this same
/// answer, 0 when it did not, NaN when fewer than two personas were scorable. See
/// <see cref="InterestCoverageGrader.ForcedChoice"/> — this is the arm of the metric that cannot
/// be saturated by chance.
/// </param>
/// <param name="DeclaredK">
/// The presentation budget every arm was given, when the answer was cut to one; 0 when the score
/// is at the arm's own presentation count. Two scores at different <c>DeclaredK</c> are different
/// quantities and must never be paired.
/// </param>
/// <param name="PresentedBeforeCut">
/// How many items the arm actually emitted before any truncation. When this exceeds
/// <paramref name="PresentedCount"/> the arm over-filled its budget and the surplus was not scored;
/// when it is below <paramref name="DeclaredK"/> the arm under-filled it.
/// </param>
/// <param name="RelevantCount">
/// Distinct presented items (within the scored k) that sit in a new category AND carry at least
/// one latent gold token. The numerator of both precision numbers.
/// </param>
/// <param name="PrecisionAtK">
/// <paramref name="RelevantCount"/> / <paramref name="DeclaredK"/> — precision over the SLOTS the
/// customer was promised (the trec_eval convention: a slot left empty is a miss, and a silent
/// answer scores 0, never NaN). NaN when no budget was declared or the gold set was empty.
/// </param>
/// <param name="PrecisionOfPresented">
/// <paramref name="RelevantCount"/> / <paramref name="PresentedCount"/> — precision over the items
/// the arm actually showed. Flatters a terse arm and is undefined for a silent one (NaN), which is
/// why it is a secondary figure and <paramref name="PrecisionAtK"/> is the channel.
/// </param>
/// <param name="PrecisionFloor">
/// The expected precision of a uniform random draw from the eligible pool, R/N — the same for every
/// k. NaN when the gold set was empty.
/// </param>
/// <param name="KUniformAcrossReps">
/// True when every repetition folded into this score presented the same number of items. A
/// rep-averaged <paramref name="PresentedCount"/> is a ROUNDED MEAN, and "equal k" between two arms
/// is only literally true when both are uniform; the comparison rule reads this flag.
/// </param>
public readonly record struct CoverageScore(
    double Latent,
    double Manifest,
    int LatentServed,
    int LatentTotal,
    int ManifestServed,
    int ManifestTotal,
    int PresentedCount,
    int NewCategoryCount,
    int PhantomCount,
    double LatentFloor = double.NaN,
    double ForcedChoice = double.NaN,
    int DeclaredK = 0,
    int PresentedBeforeCut = -1,
    int RelevantCount = 0,
    double PrecisionAtK = double.NaN,
    double PrecisionOfPresented = double.NaN,
    double PrecisionFloor = double.NaN,
    bool KUniformAcrossReps = true)
{
    /// <summary>True when latent coverage is defined (the gold set was non-empty).</summary>
    public bool IsScorable => !double.IsNaN(Latent);

    /// <summary>True when the arm was given a budget and this score is its top-k cut.</summary>
    public bool IsAtDeclaredK => DeclaredK > 0;

    /// <summary>True when the arm presented nothing at all. Silence is a fact to print, never a pass.</summary>
    public bool IsSilent => PresentedCount == 0;

    /// <summary>True when a declared budget was given and the arm emitted fewer items than it.</summary>
    public bool UnderFilledBudget => IsAtDeclaredK && PresentedBeforeCut >= 0 && PresentedBeforeCut < DeclaredK;

    /// <summary>True when a declared budget was given and the arm emitted more items than it — the surplus was cut.</summary>
    public bool OverFilledBudget => IsAtDeclaredK && PresentedBeforeCut > DeclaredK;

    /// <summary>
    /// True when latent coverage cleared THIS arm's own floor. False when it did not, and null
    /// when either number is undefined — an undecidable comparison is never a pass.
    /// </summary>
    public bool? AboveOwnFloor =>
        double.IsNaN(Latent) || double.IsNaN(LatentFloor) ? null : Latent > LatentFloor;

    /// <summary>
    /// True when precision@k cleared the R/N floor. False when it did not, null when either number
    /// is undefined. A silent arm has precision 0 against a positive floor: false, not null.
    /// </summary>
    public bool? AbovePrecisionFloor =>
        double.IsNaN(PrecisionAtK) || double.IsNaN(PrecisionFloor) ? null : PrecisionAtK > PrecisionFloor;

    /// <summary>
    /// Means one arm's repetitions into a single per-case observation.
    /// </summary>
    /// <remarks>
    /// <b>Reps collapse into ONE observation before pairing.</b> The unit of analysis is the case,
    /// not the rep. Treating three reps of three personas as nine independent observations is
    /// pseudo-replication and inflates any significance claim by a factor of sqrt(3).
    /// </remarks>
    /// <param name="reps">One or more repetition scores for the same arm and persona.</param>
    /// <exception cref="ArgumentException">The reps are empty, or were graded at different declared budgets.</exception>
    public static CoverageScore Mean(IReadOnlyList<CoverageScore> reps)
    {
        ArgumentNullException.ThrowIfNull(reps);
        if (reps.Count == 0) throw new ArgumentException("No repetitions to average.", nameof(reps));

        // Reps cut at different budgets are different quantities; averaging them would print one
        // number for two metrics.
        if (reps.Any(r => r.DeclaredK != reps[0].DeclaredK))
            throw new ArgumentException("Repetitions were graded at different declared budgets and cannot be averaged.", nameof(reps));

        return new CoverageScore(
            Latent: reps.Average(r => r.Latent),
            Manifest: reps.Average(r => r.Manifest),
            LatentServed: (int)Math.Round(reps.Average(r => (double)r.LatentServed)),
            LatentTotal: reps[0].LatentTotal,
            ManifestServed: (int)Math.Round(reps.Average(r => (double)r.ManifestServed)),
            ManifestTotal: reps[0].ManifestTotal,
            PresentedCount: (int)Math.Round(reps.Average(r => (double)r.PresentedCount)),
            NewCategoryCount: (int)Math.Round(reps.Average(r => (double)r.NewCategoryCount)),
            PhantomCount: reps.Sum(r => r.PhantomCount),

            // The floor moves with k, and k is whatever the arm actually presented in each rep,
            // so the rep-averaged score is compared against the rep-averaged floor. Averaging the
            // scores while pinning the floor at one rep's k is how a verbose rep gets graded
            // against a terse rep's bar.
            LatentFloor: reps.Average(r => r.LatentFloor),
            ForcedChoice: reps.Average(r => r.ForcedChoice),
            DeclaredK: reps[0].DeclaredK,
            PresentedBeforeCut: reps.All(r => r.PresentedBeforeCut < 0)
                ? -1
                : (int)Math.Round(reps.Where(r => r.PresentedBeforeCut >= 0).Average(r => (double)r.PresentedBeforeCut)),
            RelevantCount: (int)Math.Round(reps.Average(r => (double)r.RelevantCount)),
            PrecisionAtK: reps.Average(r => r.PrecisionAtK),
            PrecisionOfPresented: reps.Average(r => r.PrecisionOfPresented),
            PrecisionFloor: reps.Average(r => r.PrecisionFloor),
            KUniformAcrossReps: reps.All(r => r.KUniformAcrossReps && r.PresentedCount == reps[0].PresentedCount));
    }

    /// <summary>An unscorable score, for a persona whose gold set is empty.</summary>
    public static CoverageScore NotScorable(int presented) =>
        new(double.NaN, double.NaN, 0, 0, 0, 0, presented, 0, 0);
}
