// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Negative control #3 — the discovery loop with the loop taken out. The control that can take the
/// Eval 02 win away.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is, honestly.</b> The design's <c>Broken03_SingleShotWorkflow</c> is "the identical
/// workflow graph with <c>MaxDiscoveryAttempts = 1</c> and the coverage reviewer stubbed to
/// <c>IsComplete = true</c>". Demo 2 — the discovery workflow — <b>does not exist in this
/// repository</b>, so that control cannot be built as written and pretending otherwise would be
/// exactly the kind of claim this suite exists to prevent. What is built instead is the thing the
/// control was FOR: a deterministic arm that does one retrieval pass from one query and stops, with
/// no second look at what the first pass failed to cover.
/// </para>
/// <para>
/// It builds a single search need from the customer's dominant non-gift department, takes the top
/// five hits, and presents them with citations copied out of the catalogue so they resolve. It is
/// grounded, it is cited, it is policy-clean — and it never asks "which interests did I not
/// cover?", which is the only question the loop exists to ask.
/// </para>
/// <para>
/// <b>The assertion.</b> Its latent coverage MUST be low — at or near the derived random-draw floor
/// — and it MUST NOT beat the live agent. If a single pass with no loop matches the agent, the loop
/// is not load-bearing and the money slide is void. That outcome is a result, not a failure of the
/// eval, and Eval 02 reports it rather than hiding it.
/// </para>
/// </remarks>
public sealed class Broken03_SingleShotWorkflow : IEvaluableAgent
{
    /// <summary>
    /// How many hits the single pass presents — the budget the canonical utterance DECLARES,
    /// never a local literal. No Eval 02 arm sizes itself.
    /// </summary>
    public const int PresentationCount = GalaxusDemoPrompts.CoverageCohortDeclaredK;

    private readonly IProductRetriever _retriever;

    /// <summary>Creates the control over an already-built retriever.</summary>
    /// <param name="retriever">The same retriever the live agent's tools are bound to.</param>
    public Broken03_SingleShotWorkflow(IProductRetriever retriever)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
    }

    /// <inheritdoc/>
    public string Name => nameof(Broken03_SingleShotWorkflow);

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var catalogue = Catalogue.Default;
        string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;
        var profile = UserProfiles.Find(userId);

        string need = SingleNeedFor(userId);

        var trace = new ScriptedTrace()
            .Call("GetUserProfile", Args(("userId", userId)))
            .Call("GetPurchaseHistory", Args(("userId", userId), ("months", 24)))
            .Call("SearchProductsByMeaning", Args(("need", need), ("topK", 8)));

        var owned = profile is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : profile.Purchases.Select(p => p.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        RetrievalResult result = await _retriever
            .SearchAsync(new RetrievalQuery
            {
                Need = need,
                TopK = RetrievalQuery.MaxTopK,
                ExcludeProductIds = owned,
            }, cancellationToken)
            .ConfigureAwait(false);

        int presented = 0;
        foreach (var hit in result.Hits)
        {
            if (presented >= PresentationCount) break;
            if (!catalogue.TryGet(hit.ProductId, out var product) || product is null) continue;
            if (product.StockUnits == 0) continue;      // policy-clean: it does not claim stock it lacks

            string? citation = FirstResolvingCitation(product);
            if (citation is null) continue;             // policy-clean: it does not invent evidence

            trace.Call("GetProductDetails", Args(("productId", product.Id)));
            trace.Present(product.Id,
                $"Matches what you already buy in this area — {product.Name}.",
                citation);
            presented++;
        }

        trace.Say("One pass, no follow-up. Here is what the first search returned.");
        return trace.ToResponse();
    }

    /// <summary>
    /// The single search need this arm runs. Built from the customer's dominant non-gift department
    /// — the query a one-shot recommender would compose — and nothing else.
    /// </summary>
    /// <param name="userId">Customer id.</param>
    public static string SingleNeedFor(string userId)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Find(userId);
        if (profile is null || profile.Purchases.Count == 0)
            return "popular products this customer might like";

        var classified = PurchaseIntentClassifier.ClassifyAll(
            profile.Purchases, catalogue.BySku, Catalogue.DemoToday);

        var dominant = classified
            .Where(c => c.Intent != PurchaseIntent.Gift)
            .GroupBy(c => c.Product.RootCategory, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        return dominant is null
            ? "popular products this customer might like"
            : $"more {dominant.Key.ToLowerInvariant()} equipment for someone who already owns "
            + string.Join(", ", dominant.Select(c => c.Product.Name).Distinct(StringComparer.Ordinal).Take(3));
    }

    /// <summary>
    /// The first citation from the catalogue that provably resolves against a product. The BAR
    /// comes from the corpus here too — the control does not get to invent one just because it is
    /// a control.
    /// </summary>
    /// <param name="product">A catalogue product.</param>
    public static string? FirstResolvingCitation(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var catalogue = Catalogue.Default;

        foreach (var review in catalogue.Reviews(product.Id))
            return EvidenceRef.Review(review.Id).ToString();

        foreach (string token in catalogue.AttributesOf(product).OrderBy(t => t, StringComparer.Ordinal))
            return EvidenceRef.Attribute(token).ToString();

        return null;
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var map = new Dictionary<string, object?>(pairs.Length, StringComparer.Ordinal);
        foreach (var (key, value) in pairs) map[key] = value;
        return map;
    }
}
