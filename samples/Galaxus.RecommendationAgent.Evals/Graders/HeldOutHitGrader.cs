// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// One arm's answer to one leave-one-out target, at the DECLARED budget k and at the arm's own k.
/// </summary>
/// <param name="PresentedRaw">Distinct SKUs the arm presented, before truncation.</param>
/// <param name="PresentedAtK">Distinct SKUs after truncation to the declared k.</param>
/// <param name="SkuHitAtK">The hidden SKU is among the first k.</param>
/// <param name="LeafHitAtK">A product in the hidden SKU's leaf is among the first k.</param>
/// <param name="SkuHitOwnK">The hidden SKU is anywhere in what the arm presented. k-CONFOUNDED; reported, never compared.</param>
/// <param name="LeafHitOwnK">A product in the hidden leaf is anywhere in what the arm presented. k-CONFOUNDED.</param>
/// <param name="Phantom">Presented SKUs not in the catalogue. They consume budget and cannot hit.</param>
/// <param name="TopK">The first k SKUs, in presentation order.</param>
public readonly record struct HitScore(
    int PresentedRaw,
    int PresentedAtK,
    bool SkuHitAtK,
    bool LeafHitAtK,
    bool SkuHitOwnK,
    bool LeafHitOwnK,
    int Phantom,
    IReadOnlyList<string> TopK)
{
    /// <summary>True when the arm presented nothing at all — silence, which is a miss and is flagged.</summary>
    public bool Silent => PresentedRaw == 0;
}

/// <summary>
/// Scores a presented list against a hidden next purchase — hit-rate@k over the SKU and over its
/// leaf category — at a k that is the SAME for every arm.
/// </summary>
/// <remarks>
/// <para>
/// <b>k comes from the declared budget, never from the arm.</b> Hit-rate@k is monotone in k, so
/// an arm that presents twelve items beats one that presents three by presenting more, whatever
/// either knows. Every arm's list is cut to <c>k</c> in presentation order before the hit is read,
/// and the floor is derived at that same k. The arm's own-k hit is carried alongside, labelled
/// k-confounded, so a reader can see what truncation removed — and cannot mistake it for the
/// comparison.
/// </para>
/// </remarks>
public static class HeldOutHitGrader
{
    /// <summary>Grades one presented list against one target at budget <paramref name="k"/>.</summary>
    /// <param name="target">The hold-out.</param>
    /// <param name="presented">The <c>PresentRecommendation</c> calls, from the tool trace.</param>
    /// <param name="k">The declared presentation budget.</param>
    public static HitScore Grade(HeldOutTarget target, IReadOnlyList<PresentedCall> presented, int k)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        var catalogue = Catalogue.Default;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        int phantom = 0;

        foreach (var call in presented)
        {
            string sku = call.Sku.Trim();
            if (sku.Length == 0 || !seen.Add(sku)) continue;
            ordered.Add(sku);
            if (!catalogue.TryGet(sku, out _)) phantom++;
        }

        var topK = ordered.Take(k).ToList();

        return new HitScore(
            PresentedRaw: ordered.Count,
            PresentedAtK: topK.Count,
            SkuHitAtK: topK.Any(s => IsTarget(target, s)),
            LeafHitAtK: topK.Any(s => IsTargetLeaf(target, s, catalogue)),
            SkuHitOwnK: ordered.Any(s => IsTarget(target, s)),
            LeafHitOwnK: ordered.Any(s => IsTargetLeaf(target, s, catalogue)),
            Phantom: phantom,
            TopK: topK);
    }

    private static bool IsTarget(HeldOutTarget target, string sku) =>
        string.Equals(sku, target.Target.Id, StringComparison.OrdinalIgnoreCase);

    private static bool IsTargetLeaf(HeldOutTarget target, string sku, Catalogue catalogue) =>
        catalogue.TryGet(sku, out var product) && product is not null
        && string.Equals(product.LeafCategory, target.TargetLeaf, StringComparison.Ordinal);
}
