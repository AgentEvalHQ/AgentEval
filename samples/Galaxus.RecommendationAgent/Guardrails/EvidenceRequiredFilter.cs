// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// Stage 3 of <see cref="GuardrailPipeline"/>: the two-sided evidence check (§F.3). One side
/// points at the CUSTOMER, the other at the PRODUCT, and both are verified against data the
/// model did not write. A recommendation that cannot produce both sides is DROPPED, not
/// down-ranked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Note the shape of this check.</b> The artifact under test does not get to supply the bar
/// it is measured against. The interest label must be one the CODE derived; the purchase ids
/// must be ones the customer actually has; the attribute key and value must be the catalogue's
/// own. A model that invents a flattering spec value fails the check <i>harder</i>, not softer —
/// which is the opposite of what happens when a grader scores plausibility.
/// </para>
/// <para>
/// <b>The one principled exception, and why it is not a loophole.</b> §F.3 as written requires
/// <c>UserPurchaseIds</c> to be non-empty. That is right for a behaviour-derived interest and
/// wrong for a stated one: under the personalization opt-out (§F.6) there IS no history — the
/// tool refuses it — so a recommendation serving a need the customer just typed has no purchase
/// id it could honestly cite. Requiring one would either kill the opt-out path or teach the
/// model to fabricate ids. So the rule is inverted per signal kind: a
/// <see cref="InterestEvidenceKinds.StatedInSession"/> signal must cite NO purchase ids, and any
/// other kind must cite at least one. Both directions are enforced, so neither is a way through.
/// </para>
/// <para>
/// A cited purchase that the classifier ruled a GIFT is rejected separately from a purchase
/// belonging to someone else: both are wrong, but "you bought a games console" as evidence for
/// Marco's own interests is a specific, nameable failure and deserves its own reason token.
/// </para>
/// </remarks>
public static class EvidenceRequiredFilter
{
    /// <summary>
    /// Verifies one recommendation's two-sided evidence against the catalogue and the derived
    /// interest map.
    /// </summary>
    /// <param name="rec">The recommendation to verify.</param>
    /// <param name="context">The catalogue-derived bar.</param>
    /// <param name="reason">One of <see cref="GuardrailReasons"/> on failure; empty on success.</param>
    /// <param name="detail">The human-readable justification on failure; empty on success.</param>
    /// <returns>True when every clause of §F.3 holds.</returns>
    public static bool TryVerify(RecommendationDto rec, GuardrailContext context, out string reason, out string detail)
    {
        ArgumentNullException.ThrowIfNull(rec);
        ArgumentNullException.ThrowIfNull(context);

        reason = string.Empty;
        detail = string.Empty;

        if (!context.ProductsBySku.TryGetValue(rec.ProductId, out var product))
        {
            reason = GuardrailReasons.Ungrounded;
            detail = "does not resolve in the catalogue";
            return false;
        }

        var evidence = rec.Evidence;

        // ── user side ───────────────────────────────────────────────────────────────
        var signal = context.InterestMap.FindSignal(evidence.UserSignalLabel);
        if (signal is null)
        {
            reason = GuardrailReasons.UnknownSignalLabel;
            detail = $"cites the interest \"{evidence.UserSignalLabel}\", which is not in the code-derived interest map. " +
                     "A recommendation may only cite an interest the CODE derived, never one the model invented";
            return false;
        }

        bool statedInSession = string.Equals(signal.EvidenceKind, InterestEvidenceKinds.StatedInSession, StringComparison.Ordinal);

        if (statedInSession)
        {
            if (evidence.UserPurchaseIds.Count > 0)
            {
                reason = GuardrailReasons.StatedNeedCitesHistory;
                detail = $"cites {evidence.UserPurchaseIds.Count} purchase id(s) for an interest the customer stated in this session. " +
                         "A stated need is evidenced by the sentence, not by history — and under the personalization opt-out there is no history to cite";
                return false;
            }
        }
        else
        {
            if (evidence.UserPurchaseIds.Count == 0)
            {
                reason = GuardrailReasons.MissingEvidence;
                detail = $"cites the behaviour-derived interest \"{signal.Label}\" but no purchase id evidences it";
                return false;
            }

            foreach (var purchaseId in evidence.UserPurchaseIds)
            {
                if (context.GiftPurchaseIds.Contains(purchaseId))
                {
                    reason = GuardrailReasons.GiftPurchaseCited;
                    detail = $"cites {purchaseId}, which the classifier ruled a GIFT. It is a signal about a different person, and it carries interest weight 0";
                    return false;
                }

                if (!context.UserPurchaseIds.Contains(purchaseId))
                {
                    reason = GuardrailReasons.ForeignPurchaseId;
                    detail = $"cites {purchaseId}, which does not belong to {context.User.Id}";
                    return false;
                }
            }
        }

        // ── product side ────────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(evidence.ProductAttributeKey))
        {
            reason = GuardrailReasons.AttributeNotFound;
            detail = "carries no product-side attribute key. Evidence pointing only at the customer is not two-sided";
            return false;
        }

        if (!product.TryGetAttributeValue(evidence.ProductAttributeKey, out var catalogueValue))
        {
            reason = GuardrailReasons.AttributeNotFound;
            detail = $"cites attribute \"{evidence.ProductAttributeKey}\", which exists in neither the Specs nor the Tags of {product.Id}";
            return false;
        }

        var stated = Product.NormalizeAttributeToken(evidence.ProductAttributeValue);
        var actual = Product.NormalizeAttributeToken(catalogueValue);

        if (!string.Equals(stated, actual, StringComparison.Ordinal))
        {
            reason = GuardrailReasons.AttributeValueMismatch;
            detail = $"states \"{evidence.ProductAttributeValue}\" for \"{evidence.ProductAttributeKey}\", " +
                     $"but the catalogue says \"{catalogueValue}\". An invented value fails harder, not softer";
            return false;
        }

        if (evidence.ReviewId is { Length: > 0 } reviewId && !product.ReviewIds.Contains(reviewId))
        {
            reason = GuardrailReasons.ReviewNotFound;
            detail = $"cites review {reviewId}, which does not exist or belongs to a different product";
            return false;
        }

        // ── the compact citation the tool channel actually carries (defect class D5) ─
        var citation = evidence.Citation;
        if (!citation.Resolves(product))
        {
            reason = GuardrailReasons.UnresolvableEvidence;
            detail = $"the citation '{citation}' does not resolve against {product.Id}'s own catalogue record";
            return false;
        }

        return true;
    }

    /// <summary>Drops every recommendation whose two-sided evidence fails verification.</summary>
    /// <param name="set">The answer so far.</param>
    /// <param name="context">The catalogue-derived bar.</param>
    /// <param name="ledger">The ledger every drop is written to.</param>
    public static RecommendationSet Apply(RecommendationSet set, GuardrailContext context, GuardrailLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);

        return set with
        {
            Recommendations = Filter(set.Recommendations, context, ledger),
            AlsoConsider = Filter(set.AlsoConsider, context, ledger)
        };
    }

    private static IReadOnlyList<RecommendationDto> Filter(
        IReadOnlyList<RecommendationDto> items,
        GuardrailContext context,
        GuardrailLedger ledger)
    {
        var kept = new List<RecommendationDto>(items.Count);

        foreach (var item in items)
        {
            if (TryVerify(item, context, out var reason, out var detail)) { kept.Add(item); continue; }
            ledger.Drop(GuardrailStage.EvidenceRequired, reason, item.ProductId, detail);
        }

        return kept;
    }
}
