// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// Candidate-set containment (§8.1 B-6a): the model may only present a product that a retrieval
/// route in THIS turn actually returned. Demo 2 has had this check since it was built; Demo 1 —
/// the arm an interviewer runs first — did not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Existence is not containment.</b> <see cref="CatalogueGroundingFilter"/> answers "does this
/// id exist?"; this stage answers "did anything put it in front of you?". They come apart exactly
/// where it matters: a model that recalls a plausible Galaxus SKU from pre-training, or that
/// permutes a digit of an id it did see, produces a REAL product it never retrieved. The grounding
/// filter waves that through, because the id resolves.
/// </para>
/// <para>
/// <b>Widening came first, and it had to.</b> Only the three semantic tools recorded provenance,
/// so a product reached through <c>BrowseCategory</c> or <c>GetProductDetails</c> — both of which
/// the system prompt explicitly permits — legitimately arrived with none. Enforcing containment
/// against the provenance map alone would have dropped correctly-reasoned recommendations for the
/// route they were found by, which is a guardrail firing on its own wiring. The candidate set is
/// therefore recorded by EVERY retrieval route (see <c>GalaxusTools.RecordCandidates</c>), and the
/// offline baseline arm records its own search hits into the same set.
/// </para>
/// <para>
/// ⚠ <b>The empty set is not an empty candidate set.</b> When
/// <see cref="GuardrailContext.CandidateProductIds"/> is null the turn ran without a recorder
/// attached — the eval lane driving the pipeline directly, for instance — and this stage reports
/// itself INAPPLICABLE rather than dropping everything. Dropping everything would be an extreme
/// value produced by a wiring fault, dressed up as a working guardrail: the exact shape this
/// project keeps a rule about. A non-null but EMPTY set is different and is enforced: it means a
/// recorder was attached and nothing was retrieved, so nothing may be presented.
/// </para>
/// </remarks>
public static class CandidateContainmentFilter
{
    /// <summary>True when <paramref name="productId"/> is in the recorded candidate set.</summary>
    /// <param name="productId">The id the model presented.</param>
    /// <param name="context">The catalogue-derived bar, carrying the recorded candidate set.</param>
    /// <returns>True when the set is untracked (nothing to enforce) or contains the id.</returns>
    public static bool IsContained(string? productId, GuardrailContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CandidateProductIds is not { } candidates) return true;
        return !string.IsNullOrWhiteSpace(productId) && candidates.Contains(productId.Trim());
    }

    /// <summary>Removes every presented item that no retrieval route in this turn returned.</summary>
    /// <param name="set">The answer so far.</param>
    /// <param name="context">The catalogue-derived bar.</param>
    /// <param name="ledger">The ledger every drop and note is written to.</param>
    public static RecommendationSet Apply(RecommendationSet set, GuardrailContext context, GuardrailLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);

        if (context.CandidateProductIds is null)
        {
            ledger.Note(GuardrailStage.CandidateContainment, GuardrailReasons.ArmInapplicable, "candidate containment",
                "no candidate set was handed to the pipeline on this turn, so THIS stage had nothing to enforce "
              + "against (chance floor 1.0 — not a pass): a real SKU that was never retrieved would have passed it "
              + "unchallenged. A caller that enforces containment upstream — Demo 2's ContainmentFilter does — still "
              + "sees this line, because it is a statement about this stage and not about the turn");
            return set;
        }

        return set with
        {
            Recommendations = Filter(set.Recommendations, context, ledger),
            AlsoConsider    = Filter(set.AlsoConsider,    context, ledger)
        };
    }

    private static IReadOnlyList<RecommendationDto> Filter(
        IReadOnlyList<RecommendationDto> items,
        GuardrailContext context,
        GuardrailLedger ledger)
    {
        var kept = new List<RecommendationDto>(items.Count);
        var candidates = context.CandidateProductIds!;

        foreach (var item in items)
        {
            if (IsContained(item.ProductId, context)) { kept.Add(item); continue; }

            ledger.Drop(GuardrailStage.CandidateContainment, GuardrailReasons.OutsideCandidateSet, item.ProductId,
                $"resolves in the catalogue but no search, browse or details call in this turn returned it "
              + $"({candidates.Count} candidate(s) were retrieved). The model may only select from what retrieval "
              + "put in front of it — existence is not containment");
        }

        return kept;
    }
}
