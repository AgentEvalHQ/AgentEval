// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Calibration;

/// <summary>
/// THE SPLIT. Named here, in code, BEFORE anything is fitted — and deliberately in its own file so
/// the commit that introduces it can be read on its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The unit is the PERSONA, not the case.</b> Every one of the three space-dependent thresholds
/// cuts a distribution whose rows are produced by a customer's derived interest map — the queries a
/// map issues, the signal labels it carries, the confidences those signals produce against a
/// product. Two rows from the same customer are not independent, so a case-level split would leak
/// the fit slice into the held-out slice through the shared map. Splitting on the customer is the
/// only split that closes that channel.
/// </para>
/// <para>
/// <b>The rule, stated before the number.</b> The HELD-OUT slice is exactly
/// <see cref="Personas.DemoPersonaIds"/> — the four customers whose trays the demos print. The FIT
/// slice is every other authored customer: Elena plus the nine-strong Eval 02 cohort.
/// </para>
/// <para>
/// <b>Why that direction and not the other.</b> The failure this project keeps a rule about is
/// "the number that makes the trays look right". Putting the four printed personas in the HELD-OUT
/// slice makes that failure structurally unavailable: no threshold derived here can have been
/// steered by Nadia's, Marco's, Sofia's or Luca's output, because none of their rows is in the
/// population any cut is taken from. The convenient split — fit on the personas you can see — is
/// the one that would have let the trays vote on their own thresholds.
/// </para>
/// <para>
/// <b>What the held-out slice is FOR.</b> It answers one question and no other: does the operating
/// point derived on the fit slice still hold on customers the derivation never saw? It is scored
/// once, after the cuts are fixed. It is never swept, and no cut is ever moved because a held-out
/// number came back unflattering — that move would convert the held-out slice into a second fit
/// slice and there would then be no held-out slice at all.
/// </para>
/// <para>
/// ⚠ <b>Declared limits of this split, three of them.</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>The held-out slice is FOUR customers, and one of them abstains.</b> Luca
///     (<c>USR-LF-04</c>) has a single order line, so <c>InterestMap.HasEnoughSignal</c> is false,
///     the abstention gate fires before retrieval and he contributes no rows to any of the three
///     populations. The effective held-out slice is THREE customers. That is small, it is stated
///     rather than rounded away, and it bounds every held-out number below to "consistent with"
///     rather than "confirms".
///   </item>
///   <item>
///     <b>Fit and held-out share a catalogue.</b> Both slices score against the same 99 products,
///     so the product side of every cosine is common to both. This split isolates the CUSTOMER, not
///     the catalogue; a threshold that is an artefact of these 99 products would survive it.
///   </item>
///   <item>
///     <b>The evals still score all fourteen.</b> Eval 02's coverage cells, Eval 02b's cases and
///     Eval 02c's held-out targets read every persona, the four demo personas included. A threshold
///     derived on the fit slice and then applied everywhere therefore does touch numbers that are
///     reported elsewhere — what this split guarantees is that the derivation did not READ them,
///     which is the property that separates calibration from tuning. It does not make the eval
///     numbers independent of the calibration.
///   </item>
/// </list>
/// </remarks>
public static class CalibrationSplit
{
    /// <summary>
    /// The customers the cuts are DERIVED from. Ten ids, none of them printed by a demo.
    /// </summary>
    public static IReadOnlyList<string> Fit { get; } =
    [
        Personas.ElenaUserId,
        .. Personas.CohortPersonaIds,
    ];

    /// <summary>
    /// The customers the cuts are TESTED on and never fitted on: the four demo personas.
    /// </summary>
    public static IReadOnlyList<string> HeldOut { get; } = Personas.DemoPersonaIds;

    /// <summary>True when <paramref name="personaId"/> is in <see cref="Fit"/>.</summary>
    /// <param name="personaId">A customer id.</param>
    public static bool IsFit(string personaId) =>
        Fit.Contains(personaId, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="personaId"/> is in <see cref="HeldOut"/>.</summary>
    /// <param name="personaId">A customer id.</param>
    public static bool IsHeldOut(string personaId) =>
        HeldOut.Contains(personaId, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Proves the split is a PARTITION of the authored cohort — disjoint, exhaustive, non-empty on
    /// both sides — and throws with the offending ids when it is not.
    /// </summary>
    /// <remarks>
    /// A split that silently overlapped would report a held-out number computed partly on fit rows,
    /// and it would report it in the flattering direction. This runs before the first population is
    /// collected, every time, rather than living in a test that a calibration run does not execute.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The two slices are not a partition of <see cref="Personas.AllPersonaIds"/>.</exception>
    public static void SelfCheck()
    {
        var fit  = new HashSet<string>(Fit, StringComparer.OrdinalIgnoreCase);
        var held = new HashSet<string>(HeldOut, StringComparer.OrdinalIgnoreCase);
        var all  = new HashSet<string>(Personas.AllPersonaIds, StringComparer.OrdinalIgnoreCase);

        if (fit.Count != Fit.Count || held.Count != HeldOut.Count)
            throw new InvalidOperationException("A calibration slice lists the same customer twice.");

        var overlap = fit.Intersect(held, StringComparer.OrdinalIgnoreCase).ToArray();
        if (overlap.Length > 0)
            throw new InvalidOperationException(
                $"Fit and held-out overlap on {string.Join(", ", overlap)} — every held-out number would be "
              + "partly fitted, and in the flattering direction.");

        var union = new HashSet<string>(fit, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(held);

        var missing = all.Except(union, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"The split does not cover {string.Join(", ", missing)}. A customer in neither slice is a customer "
              + "whose rows could be quietly added to whichever slice needed them.");

        var unknown = union.Except(all, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException($"The split names customers the catalogue does not have: {string.Join(", ", unknown)}.");

        if (fit.Count == 0 || held.Count == 0)
            throw new InvalidOperationException("Both slices must be non-empty.");
    }
}
