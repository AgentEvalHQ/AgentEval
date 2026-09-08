// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// The Ranker's deterministic post-checks. Code, run AFTER the model call — not instructions
/// handed to it before one.
/// </summary>
/// <remarks>
/// <para>
/// Each check REMOVES rather than down-ranks, and each removal is written to
/// <see cref="DiscoveryState.DroppedSkus"/> and printed. A guardrail whose firing leaves no
/// trace is indistinguishable from one that never fires — which is the shape this repository
/// keeps a standing rule about.
/// </para>
/// <para>
/// Nothing here repairs a bad selection. A repaired argument is a defect that can never fire,
/// and that fails in the flattering direction.
/// </para>
/// </remarks>
public static class DiscoveryPostChecks
{
    /// <summary>Runs containment, unnameable-interest, compatibility and anti-interest, in that order.</summary>
    /// <param name="state">The run state; <see cref="DiscoveryState.Ranked"/> is rewritten in place.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="progress">Where the drop lines go.</param>
    /// <returns>One console line per check, for the Ranker's trace.</returns>
    public static IReadOnlyList<string> Apply(
        DiscoveryState state,
        Catalogue catalogue,
        IDiscoveryProgressSink progress)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);
        progress ??= NullDiscoveryProgressSink.Instance;

        var lines = new List<string>(4);
        int proposed = state.Ranked.Count;

        var survivors = ProductContainmentCheck.Apply(state, out int uncontained);
        lines.Add($"SKU containment   {survivors.Count}/{proposed} selected id(s) present in the candidate set  ({uncontained} dropped)");

        // Second, so a hallucinated id is still reported as a hallucination rather than as an
        // attribution failure — and before compatibility, because an interest that names nothing
        // cannot be served by a compatible product either.
        int unnameableInterests = state.Interests.Count(InterestAttribution.NamesNothing);
        survivors = UnnameableInterestFilter.Apply(state, survivors, out int unnameable);
        lines.Add(unnameableInterests == 0
            ? "unnameable interest  ARM INAPPLICABLE — every interest on this map names something a product could be matched against (chance floor 1.0, not a pass)"
            : $"unnameable interest  {unnameableInterests} interest(s) name nothing  ({unnameable} dropped)");

        survivors = CompatibilityChecker.Apply(state, catalogue, survivors, out int incompatible);
        lines.Add(state.Constraints.Count == 0
            ? "compatibility     ARM INAPPLICABLE — this customer owns no device that constrains an accessory (chance floor 1.0, not a pass)"
            : $"compatibility     {state.Constraints.Count} constraint(s) enforced in code  ({incompatible} dropped)");

        survivors = AntiInterestFilter.Apply(state, catalogue, survivors, out int excluded);
        lines.Add(state.AntiInterests.Count == 0
            ? "anti-interest     ARM INAPPLICABLE — nothing was ruled out for this customer (chance floor 1.0, not a pass)"
            : $"anti-interest     {state.AntiInterests.Count} exclusion(s) enforced  ({excluded} dropped)");

        // Re-rank contiguously so the printed positions are 1..n with no holes.
        state.Ranked.Clear();
        for (int i = 0; i < survivors.Count; i++)
            state.Ranked.Add(survivors[i] with { Rank = i + 1 });

        foreach (var dropped in state.DroppedSkus)
            progress.Publish(DiscoveryEvent.SkuDropped(dropped));

        return lines;
    }
}

/// <summary>
/// Anti-hallucination, structurally: a recommended SKU must be one that legitimate retrieval
/// actually put in the candidate set.
/// </summary>
/// <remarks>
/// Combined with "the model may only pick from what discovery returned", a hallucinated SKU
/// stops being statistically unlikely and becomes impossible. Note the honest boundary: this
/// check cannot see an injected interest whose SKU came back through a real search — that is
/// what <see cref="QueryVocabulary"/> is for, and the two controls sit at different layers on
/// purpose (design §0.5 / D-3).
/// </remarks>
public static class ProductContainmentCheck
{
    /// <summary>Removes every ranked item whose product id is not a retrieved candidate.</summary>
    /// <param name="state">The run state; drops are appended to <see cref="DiscoveryState.DroppedSkus"/>.</param>
    /// <param name="dropped">How many were removed.</param>
    public static IReadOnlyList<RankedRecommendation> Apply(DiscoveryState state, out int dropped)
    {
        ArgumentNullException.ThrowIfNull(state);

        var kept = new List<RankedRecommendation>(state.Ranked.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dropped = 0;

        foreach (var item in state.Ranked)
        {
            if (state.FindCandidate(item.ProductId) is null)
            {
                state.DroppedSkus.Add(new DroppedSku(item.ProductId,
                    "not in the candidate set — the model may only select from what discovery retrieved. " +
                    "Removed, not down-ranked: a product id nobody retrieved is a hallucination, not a near miss"));
                dropped++;
                continue;
            }

            if (!seen.Add(item.ProductId))
            {
                state.DroppedSkus.Add(new DroppedSku(item.ProductId, "selected more than once in the same turn"));
                dropped++;
                continue;
            }

            if (state.OwnedProductIds.Contains(item.ProductId))
            {
                state.DroppedSkus.Add(new DroppedSku(item.ProductId,
                    "the customer already owns it. Recommending it back to them is not a recommendation"));
                dropped++;
                continue;
            }

            kept.Add(item);
        }

        return kept;
    }
}

/// <summary>
/// An interest that NAMES NOTHING cannot be served by anything, so nothing may be presented for
/// it — whatever the retriever returned when the loop asked on its behalf.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists: the gate was fixed and the tray was not (plan item 8.18).</b>
/// <c>aae2024d</c> stopped the COVERAGE gate keying on the retriever's ranking: an interest whose
/// attribution vocabulary is empty is UNCOVERED and STARVED however many candidates came back and
/// however well they scored, so the reviewer reports <c>GAPS_UNRESOLVABLE</c> and the loop does
/// not go round again. That fixed the LOOP. It did not fix the ANSWER: the candidates retrieved in
/// round 1, <i>before</i> the gate ran, are already in
/// <see cref="DiscoveryState.Candidates"/>, and the Ranker reads the candidate set — not the
/// coverage ledger. MEASURED at <c>41cd09a2</c> on <c>--real-vectors</c>: Luca Ferrari
/// (<c>USR-LF-04</c>) — one order line and the contentless utterance <i>"Hi — what do you
/// recommend for me?"</i> — went from five presented products to <b>two</b>, and two is not zero.
/// A customer who named nothing was still shown a tray.
/// </para>
/// <para>
/// <b>It screens the INTEREST, not the candidate.</b> The narrow, unambiguous case is the one that
/// gates: nothing the customer said can be matched against anything, so every product credited to
/// that interest is arbitrary by construction. The WIDER case — a candidate that carries nothing a
/// nameable interest names — is measured and printed on every ledger
/// (<see cref="InterestCoverage.AttributableProductIds"/>) and deliberately NOT gated here: it
/// flips four of Eval 07's five personas and removes the corpus's only approved exit, which is a
/// decision about what the shipped demo answers rather than a defect fix (plan item 8.21).
/// </para>
/// <para>
/// <b>It runs where BOTH rankers pass.</b> <see cref="DeterministicRanker"/> and the model Ranker
/// both end in <see cref="DiscoveryPostChecks.Apply"/>, so a model that selects a product for an
/// unnameable interest is refused by the same code that refuses the deterministic arm's selection.
/// Placing it inside either ranker would leave the other one open.
/// </para>
/// </remarks>
public static class UnnameableInterestFilter
{
    /// <summary>Removes every ranked item credited to an interest that names nothing.</summary>
    /// <param name="state">The run state; drops are appended to <see cref="DiscoveryState.DroppedSkus"/>.</param>
    /// <param name="candidates">The survivors of the previous check.</param>
    /// <param name="dropped">How many were removed.</param>
    public static IReadOnlyList<RankedRecommendation> Apply(
        DiscoveryState state,
        IReadOnlyList<RankedRecommendation> candidates,
        out int dropped)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(candidates);

        var kept = new List<RankedRecommendation>(candidates.Count);
        dropped = 0;

        foreach (var item in candidates)
        {
            var interest = state.FindInterest(item.InterestId);

            // An interest id that does not resolve is somebody else's defect — the model Ranker
            // drops those itself, with its own reason. Screening it a second time here would
            // attribute it to this check.
            if (interest is null || !InterestAttribution.NamesNothing(interest))
            {
                kept.Add(item);
                continue;
            }

            state.DroppedSkus.Add(new DroppedSku(item.ProductId,
                $"selected for \"{interest.Label}\", which names nothing a product could be matched against — no "
              + "attribute hint, no category hint, no content word. A query with no content still returns a ranked "
              + "list, so this product is what the index happened to rank first and not an answer to anything the "
              + "customer said. Removed, not down-ranked: there is no position at which an arbitrary product "
              + "becomes a recommendation"));
            dropped++;
        }

        return kept;
    }
}

/// <summary>
/// Compatibility is a CODE check against the devices the customer owns, not a hope expressed in
/// a prompt.
/// </summary>
/// <remarks>
/// <para>
/// Constraints are derived from the <c>compat:</c> tags on the customer's own non-gift
/// purchases. The gate fires on a CONFLICT WITHIN A FAMILY, never on a bare mismatch:
/// </para>
/// <list type="bullet">
///   <item>A candidate declaring no <c>compat:</c> tag is not an accessory and passes untouched.</item>
///   <item>A candidate declaring a value in a family the customer does not constrain passes:
///         nothing is known about it, and dropping on "not known to fit" is not a fact.</item>
///   <item>A candidate declaring a value in a family the customer DOES constrain, with a
///         DIFFERENT value, is dropped. <c>54mm-portafilter</c> against an owned
///         <c>58mm-portafilter</c> is not a near miss.</item>
/// </list>
/// <para>
/// The family is the last hyphen-separated token of the tag value — <c>portafilter</c>,
/// <c>mount</c>, <c>tamper</c>, <c>jug</c>. It is read off the tag vocabulary rather than
/// hand-listed, so a family added to the catalogue later is enforced without editing this class.
/// </para>
/// <para>
/// ⚠ MEASURED, and the reason this rule is not the simpler one: a bare "must share a value with
/// something the customer owns" gate dropped a lens hood and a camera strap (<c>compat:camera-body</c>)
/// for a customer who owns a camera body, because the OWNER side of that relationship is tagged
/// <c>compat:sony-e-mount</c>. Two different sides of one relationship are not two mismatched
/// standards, and a guardrail that fires there is a guardrail nobody will keep switched on.
/// </para>
/// <para>
/// ⚠ The arm reports itself INAPPLICABLE when the customer owns nothing that constrains an
/// accessory. A compatibility check with no constraint to enforce has a chance floor of 1.0 and
/// proves nothing; saying so beats banking a clean sheet it did not earn.
/// </para>
/// </remarks>
public static class CompatibilityChecker
{
    /// <summary>The tag prefix that declares a compatibility fact.</summary>
    public const string CompatTagPrefix = "compat";

    /// <summary>
    /// Derives the customer's constraints from their non-gift purchase history.
    /// </summary>
    /// <param name="classified">The classified purchase lines.</param>
    /// <param name="market">The customer's market, recorded as a constraint in its own right.</param>
    public static IReadOnlyList<CompatibilityConstraint> Derive(
        IReadOnlyList<ClassifiedPurchase> classified,
        string market)
    {
        ArgumentNullException.ThrowIfNull(classified);

        var constraints = new List<CompatibilityConstraint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in classified)
        {
            if (line.IsGift) continue;   // a gift constrains a different person's accessories

            foreach (var tag in line.Product.Tags)
            {
                if (!InterestMapBuilder.TrySplitTag(tag, out var prefix, out var suffix)) continue;
                if (!string.Equals(prefix, CompatTagPrefix, StringComparison.Ordinal)) continue;
                if (!seen.Add(suffix)) continue;

                constraints.Add(new CompatibilityConstraint(CompatTagPrefix, suffix, line.PurchaseId));
            }
        }

        if (!string.IsNullOrWhiteSpace(market))
            constraints.Add(new CompatibilityConstraint("market", market.Trim(), "profile"));

        return constraints;
    }

    /// <summary>Drops ranked accessories that cannot pair with what the customer owns.</summary>
    /// <param name="state">The run state; drops are appended to <see cref="DiscoveryState.DroppedSkus"/>.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="items">The survivors so far.</param>
    /// <param name="dropped">How many were removed.</param>
    public static IReadOnlyList<RankedRecommendation> Apply(
        DiscoveryState state,
        Catalogue catalogue,
        IReadOnlyList<RankedRecommendation> items,
        out int dropped)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(items);

        dropped = 0;

        // family → the values the customer's own hardware declares in that family
        var ownedByFamily = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var constraint in state.Constraints)
        {
            if (!string.Equals(constraint.Kind, CompatTagPrefix, StringComparison.Ordinal)) continue;

            var family = FamilyOf(constraint.Value);
            if (family.Length == 0) continue;

            if (!ownedByFamily.TryGetValue(family, out var values))
                ownedByFamily[family] = values = new HashSet<string>(StringComparer.Ordinal);

            values.Add(constraint.Value);
        }

        var market = state.Market;
        var kept = new List<RankedRecommendation>(items.Count);

        foreach (var item in items)
        {
            if (!catalogue.TryGet(item.ProductId, out var product) || product is null)
            {
                kept.Add(item);   // containment already ran; an unresolvable id cannot reach here
                continue;
            }

            if (!product.IsAvailableIn(market))
            {
                state.DroppedSkus.Add(new DroppedSku(item.ProductId,
                    $"cannot ship to {market}. Unlike a restock, that is not a matter of waiting"));
                dropped++;
                continue;
            }

            var declared = CompatValues(product);
            if (declared.Count == 0 || ownedByFamily.Count == 0) { kept.Add(item); continue; }

            string? conflictFamily = null;
            string? conflictValue = null;

            foreach (var value in declared.Order(StringComparer.Ordinal))
            {
                var family = FamilyOf(value);
                if (family.Length == 0) continue;
                if (!ownedByFamily.TryGetValue(family, out var ownedValues)) continue;   // family unconstrained
                if (ownedValues.Contains(value)) { conflictFamily = null; break; }       // it fits

                conflictFamily = family;
                conflictValue = value;
            }

            if (conflictFamily is null) { kept.Add(item); continue; }

            state.DroppedSkus.Add(new DroppedSku(item.ProductId,
                $"declares {CompatTagPrefix}:{conflictValue}, and this customer's own hardware is " +
                $"{string.Join(", ", ownedByFamily[conflictFamily].Order(StringComparer.Ordinal).Select(v => CompatTagPrefix + ":" + v))}. " +
                $"Same \"{conflictFamily}\" family, different standard — a code check against their own hardware, not a hope"));
            dropped++;
        }

        return kept;
    }

    /// <summary>
    /// The mutually-exclusive family a compat value belongs to: the last hyphen-separated token.
    /// </summary>
    /// <remarks>
    /// Read off the tag vocabulary rather than hand-listed, so a standard added to the catalogue
    /// later is enforced without editing this class. <c>54mm-portafilter</c> and
    /// <c>58mm-portafilter</c> land in <c>portafilter</c>; <c>sony-e-mount</c> lands in
    /// <c>mount</c>; a single-token value like <c>switch2</c> is its own family.
    /// </remarks>
    /// <param name="compatValue">A normalised <c>compat:</c> tag suffix.</param>
    public static string FamilyOf(string? compatValue)
    {
        if (string.IsNullOrWhiteSpace(compatValue)) return string.Empty;

        var value = compatValue.Trim();
        int dash = value.LastIndexOf('-');
        return dash >= 0 && dash < value.Length - 1 ? value[(dash + 1)..] : value;
    }

    /// <summary>The <c>compat:</c> values a product declares, normalised.</summary>
    /// <param name="product">A catalogue record.</param>
    public static HashSet<string> CompatValues(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in product.Tags)
            if (InterestMapBuilder.TrySplitTag(tag, out var prefix, out var suffix)
                && string.Equals(prefix, CompatTagPrefix, StringComparison.Ordinal))
                values.Add(suffix);

        return values;
    }
}

/// <summary>
/// Drops anything matching something the customer has told us not to recommend — a return with a
/// stated reason, or a purchase the classifier ruled a gift.
/// </summary>
/// <remarks>
/// A return is a signal, and it is the one a naive collaborative filter treats as noise. The same
/// instinct as rewarding a click at −1 for an unsubscribe: engagement is not the objective.
/// </remarks>
public static class AntiInterestFilter
{
    /// <summary>Removes ranked items whose category matches an anti-interest.</summary>
    /// <param name="state">The run state; drops are appended to <see cref="DiscoveryState.DroppedSkus"/>.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="items">The survivors so far.</param>
    /// <param name="dropped">How many were removed.</param>
    public static IReadOnlyList<RankedRecommendation> Apply(
        DiscoveryState state,
        Catalogue catalogue,
        IReadOnlyList<RankedRecommendation> items,
        out int dropped)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(items);

        dropped = 0;
        var kept = new List<RankedRecommendation>(items.Count);

        foreach (var item in items)
        {
            if (!catalogue.TryGet(item.ProductId, out var product) || product is null) { kept.Add(item); continue; }

            AntiInterest? matched = null;
            foreach (var anti in state.AntiInterests)
            {
                foreach (var element in product.CategoryPath)
                {
                    if (!string.Equals(element, anti.Label, StringComparison.OrdinalIgnoreCase)) continue;
                    matched = anti;
                    break;
                }
                if (matched is not null) break;
            }

            if (matched is null) { kept.Add(item); continue; }

            state.DroppedSkus.Add(new DroppedSku(item.ProductId,
                $"matches the ANTI signal \"{matched.Label}\" ({matched.Reason})"));
            dropped++;
        }

        return kept;
    }
}
