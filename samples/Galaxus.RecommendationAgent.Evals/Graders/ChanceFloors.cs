// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// Chance floors DERIVED from this corpus at run time, never typed as a constant.
/// </summary>
/// <remarks>
/// <para>
/// A metric whose degenerate agent scores near the top of the scale is a decoration. Every number
/// this class produces answers one question: <i>what does an agent that understands nothing score
/// here?</i> — and it answers it by counting the actual catalogue rather than by quoting the
/// design document, because the design's figures were computed against a hypothetical 40-SKU pool
/// that this corpus is not.
/// </para>
/// <para>
/// <b>An absent baseline is not a zero floor.</b> The popularity baseline scores 0.00 only because
/// of how the bestseller list was authored; quoting that as "the floor" would be self-flattery.
/// The floor that matters is the random-draw one, and it is computed per persona from the real
/// eligible pool.
/// </para>
/// </remarks>
public static class ChanceFloors
{
    /// <summary>How many items a degenerate agent is assumed to present. Matches the demo's typical answer size.</summary>
    public const int DegenerateDrawSize = 5;

    /// <summary>
    /// Probability that a uniform draw of <paramref name="k"/> distinct items from
    /// <paramref name="poolSize"/> contains at least one of <paramref name="favourable"/> items:
    /// 1 - C(pool - favourable, k) / C(pool, k).
    /// </summary>
    /// <param name="poolSize">Total items available to draw from.</param>
    /// <param name="favourable">How many of them count as a hit.</param>
    /// <param name="k">How many are drawn.</param>
    public static double AtLeastOneHit(int poolSize, int favourable, int k)
    {
        if (poolSize <= 0 || k <= 0 || favourable <= 0) return 0.0;
        if (favourable >= poolSize) return 1.0;
        if (k >= poolSize) return 1.0;

        // Product form of C(pool - fav, k) / C(pool, k) — no factorials, no overflow.
        double missAll = 1.0;
        for (int i = 0; i < k; i++)
        {
            double numerator = poolSize - favourable - i;
            double denominator = poolSize - i;
            if (numerator <= 0) return 1.0;
            missAll *= numerator / denominator;
        }

        return 1.0 - missAll;
    }

    /// <summary>
    /// Probability that a uniform draw of <paramref name="k"/> items from
    /// <paramref name="poolSize"/> AVOIDS all <paramref name="forbidden"/> items — the floor for
    /// a suppression case.
    /// </summary>
    /// <param name="poolSize">Total items available.</param>
    /// <param name="forbidden">How many must be avoided.</param>
    /// <param name="k">How many are drawn.</param>
    public static double AvoidsAll(int poolSize, int forbidden, int k) =>
        1.0 - AtLeastOneHit(poolSize, forbidden, k);

    /// <summary>
    /// The floor for one suppression case, computed from the catalogue: how often a random-5
    /// agent avoids a department by luck alone.
    /// </summary>
    /// <param name="departmentName">A category path segment, e.g. <c>"Gaming"</c>.</param>
    /// <param name="k">Draw size.</param>
    public static (int PoolSize, int Blocked, double Floor) SuppressionFloor(
        string departmentName, int k = DegenerateDrawSize)
    {
        var catalogue = Catalogue.Default;
        int pool = catalogue.All.Count;
        int blocked = catalogue.All.Count(p =>
            p.CategoryPath.Contains(departmentName, StringComparer.OrdinalIgnoreCase));

        return (pool, blocked, AvoidsAll(pool, blocked, k));
    }

    /// <summary>
    /// The floor for defect class D5 on one product: how often a citation drawn uniformly from the
    /// catalogue-wide attribute vocabulary happens to resolve against that product.
    /// </summary>
    /// <param name="sku">The product a citation is being made about.</param>
    public static (int VocabularySize, int ProductTokens, double Floor) EvidenceFloor(string sku)
    {
        var catalogue = Catalogue.Default;

        var vocabulary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var product in catalogue.All)
            foreach (var token in catalogue.AttributesOf(product))
                vocabulary.Add(token);

        int owned = catalogue.TryGet(sku, out var target) && target is not null
            ? catalogue.AttributesOf(target).Count
            : 0;

        double floor = vocabulary.Count == 0 ? 0.0 : owned / (double)vocabulary.Count;
        return (vocabulary.Count, owned, floor);
    }

    /// <summary>
    /// The expected latent coverage of a random-k agent for one persona, computed from the actual
    /// eligible pool rather than quoted from the design.
    /// </summary>
    /// <remarks>
    /// The pool is every catalogue product OUTSIDE the customer's owned leaf categories, because
    /// that is the only pool from which a token can be "served" at all under the new-category
    /// rule. For each gold token t carried by n(t) pool members, the probability a k-draw serves it
    /// is 1 - C(N - n(t), k) / C(N, k); the expected coverage is the mean of those over the gold
    /// set. Tokens no pool member carries contribute a hard zero, which is correct: they are
    /// unreachable, and <see cref="InterestCoverageGrader.UnreachableLatentTokens"/> names them.
    /// </remarks>
    /// <param name="gold">The persona's derived gold map.</param>
    /// <param name="k">Draw size.</param>
    public static (int PoolSize, double ExpectedLatent, double ExpectedManifest) RandomDrawFloor(
        GoldInterestMap gold, int k = DegenerateDrawSize)
    {
        ArgumentNullException.ThrowIfNull(gold);
        var catalogue = Catalogue.Default;

        var pool = catalogue.All
            .Where(p => !gold.OwnedCategories.Contains(p.LeafCategory))
            .ToList();

        double latent = 0.0;
        if (gold.Latent.Count > 0)
        {
            foreach (string token in gold.Latent)
            {
                // Carriers are counted over the SAME vocabulary the grader credits a hit from
                // (InterestMapGold.EligibleTokens). A floor computed over a wider vocabulary than
                // the metric can serve is a floor for a different metric.
                int carriers = pool.Count(p => InterestMapGold.EligibleTokens(p).Contains(token));
                latent += AtLeastOneHit(pool.Count, carriers, k);
            }
            latent /= gold.Latent.Count;
        }
        else
        {
            latent = double.NaN;
        }

        // Manifest coverage draws from the WHOLE catalogue, because a manifest category is by
        // definition one the customer already owns and is therefore outside the latent pool.
        double manifest;
        if (gold.Manifest.Count > 0)
        {
            manifest = 0.0;
            foreach (string leaf in gold.Manifest)
            {
                int carriers = catalogue.All.Count(p =>
                    string.Equals(p.LeafCategory, leaf, StringComparison.Ordinal));
                manifest += AtLeastOneHit(catalogue.All.Count, carriers, k);
            }
            manifest /= gold.Manifest.Count;
        }
        else
        {
            manifest = double.NaN;
        }

        return (pool.Count, latent, manifest);
    }

    /// <summary>
    /// The expected precision@k of a random-k agent for one persona — the floor for the precision
    /// channel, and NOT the same number as the recall floor above.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An item is RELEVANT when it sits outside the customer's owned leaf categories and carries at
    /// least one latent gold token (over <see cref="InterestMapGold.EligibleTokens"/>, the same
    /// vocabulary the grader credits). Let R be the number of such products in the eligible pool
    /// of N. A uniform draw of k distinct items from that pool has, at every slot, probability R/N
    /// of landing on a relevant item, so by linearity E[precision@k] = R/N — <b>independent of
    /// k</b>. That is the whole difference between the two channels: the recall floor RISES with k
    /// (more draws, more tokens covered by luck), the precision floor does not move, so an arm
    /// cannot buy precision by presenting more and cannot buy a lower precision bar by presenting
    /// less.
    /// </para>
    /// <para>
    /// The pool is the same eligible pool the recall floor draws from — every product outside the
    /// owned leaves — for the same reason: an item inside an owned leaf cannot be relevant under the
    /// new-category rule, so drawing from the whole catalogue would only lower the floor for a
    /// reason unrelated to the metric. Reported as the more demanding of the two.
    /// </para>
    /// </remarks>
    /// <param name="gold">The persona's derived gold map.</param>
    public static (int PoolSize, int RelevantCarriers, double ExpectedPrecision) RandomPrecisionFloor(GoldInterestMap gold)
    {
        ArgumentNullException.ThrowIfNull(gold);
        var catalogue = Catalogue.Default;

        var pool = catalogue.All
            .Where(p => !gold.OwnedCategories.Contains(p.LeafCategory))
            .ToList();

        if (gold.Latent.Count == 0) return (pool.Count, 0, double.NaN);
        if (pool.Count == 0) return (0, 0, 0.0);

        int relevant = pool.Count(p => InterestMapGold.EligibleTokens(p).Any(gold.Latent.Contains));
        return (pool.Count, relevant, relevant / (double)pool.Count);
    }
}
