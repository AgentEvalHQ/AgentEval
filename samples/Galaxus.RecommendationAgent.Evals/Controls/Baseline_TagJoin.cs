// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Arm D — the two-line SQL baseline the design says is missing (§0.5 / D-4). Zero model calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>This arm is not a competitor. It is a measurement of the metric.</b> Latent gold is derived
/// as "an attribute token shared by two or more purchases spanning two or more categories", and the
/// retrieval index embeds those same <c>context:</c> / <c>trip:</c> / <c>weight:</c> tags. Gold and
/// index derive from the same field, so latent coverage may be scoring whether a system can join
/// products on a tag it was indexed by — a <c>SELECT</c>, not an inference. The chance floor of a
/// random draw is computed elsewhere in this project, but nobody would ship random draws: the real
/// baseline is
/// </para>
/// <code>
/// SELECT sku FROM products
/// WHERE tags &amp;&amp; (SELECT shared_tags FROM customer_purchases WHERE user = ?)
///   AND leaf_category NOT IN (SELECT leaf_category FROM customer_purchases WHERE user = ?)
/// ORDER BY overlap DESC LIMIT 5;
/// </code>
/// <para>
/// which an interviewer constructs in thirty seconds. So it is built and run. <b>If it scores near
/// 1.0, latent coverage is a tag join and the headline metric does not license the claim the demo
/// wants to make.</b> That is a finding worth having, and the honest response to it is the one
/// stated in the report: the metric measures whether the SYSTEM can recover a planted inference, and
/// a join that already knows the rule recovers it trivially — so the interesting comparison is
/// agent-versus-single-pass, not agent-versus-oracle.
/// </para>
/// <para>
/// This arm is deliberately EXCLUDED from the sign test. It is an upper reference line, not an
/// entrant.
/// </para>
/// </remarks>
public sealed class Baseline_TagJoin : IEvaluableAgent
{
    /// <summary>How many products the join returns — the budget the canonical utterance declares.</summary>
    public const int PresentationCount = GalaxusDemoPrompts.CoverageCohortDeclaredK;

    /// <inheritdoc/>
    public string Name => nameof(Baseline_TagJoin);

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var catalogue = Catalogue.Default;
        string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;

        var trace = new ScriptedTrace();

        foreach (string sku in Join(userId, PresentationCount))
        {
            if (!catalogue.TryGet(sku, out var product) || product is null) continue;
            string? citation = Broken03_SingleShotWorkflow.FirstResolvingCitation(product);
            if (citation is null) continue;

            trace.Present(product.Id,
                $"Shares use-context tags with things you already own — {product.Name}.",
                citation,
                outOfStock: product.StockUnits == 0);
        }

        trace.Say("Tag join, no model.");
        return Task.FromResult(trace.ToResponse());
    }

    /// <summary>
    /// The join itself: products outside the customer's owned leaf categories, ranked by how many
    /// of the customer's cross-category shared tags they carry.
    /// </summary>
    /// <param name="userId">Customer id.</param>
    /// <param name="take">How many to return.</param>
    public static IReadOnlyList<string> Join(string userId, int take = PresentationCount)
    {
        var catalogue = Catalogue.Default;
        var gold = InterestMapGold.Derive(userId);

        if (gold.Latent.Count == 0) return [];

        return
        [
            .. catalogue.All
                .Where(p => !gold.OwnedCategories.Contains(p.LeafCategory))
                // Overlap counted over InterestMapGold.EligibleTokens — the same vocabulary the
                // grader scores with. An oracle that joined on a wider set than the metric reads
                // would be a different oracle from the one the report describes.
                .Select(p => (Product: p, Overlap: InterestMapGold.EligibleTokens(p).Count(gold.Latent.Contains)))
                .Where(x => x.Overlap > 0)
                .OrderByDescending(x => x.Overlap)
                .ThenByDescending(x => x.Product.RatingCount)
                .ThenBy(x => x.Product.Id, StringComparer.Ordinal)
                .Take(take)
                .Select(x => x.Product.Id)
        ];
    }
}
