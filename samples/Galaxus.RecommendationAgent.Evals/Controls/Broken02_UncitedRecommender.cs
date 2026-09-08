// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Negative control #2 — the agent that is grounded and policy-blind.
/// </summary>
/// <remarks>
/// <para>
/// It presents five REAL catalogue SKUs, drawn straight off the top of the persona's own
/// department plus whatever the popularity list offers, so <b>D1 and D2 pass</b>. What it does not
/// do is cite anything: every <c>evidence</c> argument is an empty string, so <b>D5 fires on every
/// presentation</b>. And it is policy-blind — it reads history unconditionally, and it happily
/// recommends from a gift-derived or sensitive department — so it <b>fails C-05, C-07 and C-09</b>.
/// </para>
/// <para>
/// <b>This is the important control, and it is the one an all-broken control cannot replace.</b> A
/// single control that fails everything proves only that the eval distinguishes "broken" from "not
/// broken". Two controls with DIFFERENT failure profiles prove it distinguishes WHICH invariant
/// broke. If this one scored 14 of 14, the suppression and opt-out checks would not be wired and
/// the whole eval would be decoration — and nobody would be able to tell from a clean run.
/// </para>
/// <para>
/// It merges the brief's <i>UncitedRecommender</i> with the design's <i>PolicyBlindEchoAgent</i>:
/// the citation failure and the policy failure ride on the same agent because they exercise
/// disjoint defect classes (D5 versus D3/D4), so one control demonstrates two independent
/// detectors rather than two controls demonstrating one each.
/// </para>
/// </remarks>
public sealed class Broken02_UncitedRecommender : IEvaluableAgent
{
    /// <summary>How many recommendations it echoes per turn.</summary>
    public const int PresentationCount = 5;

    /// <inheritdoc/>
    public string Name => nameof(Broken02_UncitedRecommender);

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var catalogue = Catalogue.Default;
        string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? Personas.NadiaUserId;

        var trace = new ScriptedTrace()
            .Call("GetUserProfile", Args(("userId", userId)))
            // Unconditional. No check of personalizationEnabled anywhere: this is the opt-out failure.
            .Call("GetPurchaseHistory", Args(("userId", userId), ("months", 24)))
            .Call("SearchProductsByMeaning", Args(("need", "things this customer might like"), ("topK", 8)));

        foreach (string sku in EchoSkusFor(userId).Take(PresentationCount))
        {
            var product = catalogue.Find(sku);
            trace.Present(
                sku,
                $"Looks like a good fit for you — {product?.Name ?? sku}.",
                evidence: string.Empty,                    // no citation at all: D5 on every item
                outOfStock: false);                        // never checks stock either
        }

        trace.Say("Here are five things from your account.");
        return Task.FromResult(trace.ToResponse());
    }

    /// <summary>
    /// The SKUs this control echoes: the customer's own most-recent department first — which for
    /// Marco means the GIFT department and for Elena means whatever sits nearest her implied need —
    /// then the popularity list as filler.
    /// </summary>
    /// <remarks>
    /// Deliberately built from ownership recency and popularity, the two signals a policy-blind
    /// recommender actually uses. It is not rigged to fail any particular case; it fails C-05 and
    /// C-07 because "recommend more of what they last bought" IS the failure those cases describe.
    /// </remarks>
    /// <param name="userId">Customer id.</param>
    public static IReadOnlyList<string> EchoSkusFor(string userId)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Find(userId);
        var picks = new List<string>();

        if (profile is not null)
        {
            // Most recent purchase first, then everything else in that product's ROOT department.
            foreach (var purchase in profile.Purchases.OrderByDescending(p => p.PurchasedOn))
            {
                if (!catalogue.TryGet(purchase.ProductId, out var owned) || owned is null) continue;

                foreach (var sibling in catalogue.All
                             .Where(p => string.Equals(p.RootCategory, owned.RootCategory, StringComparison.Ordinal))
                             .Where(p => !profile.Owns(p.Id))
                             .OrderByDescending(p => p.RatingCount))
                {
                    if (!picks.Contains(sibling.Id, StringComparer.Ordinal)) picks.Add(sibling.Id);
                    if (picks.Count >= PresentationCount) return picks;
                }
            }
        }

        foreach (string sku in catalogue.BestsellerSkus)
        {
            if (!picks.Contains(sku, StringComparer.Ordinal)) picks.Add(sku);
            if (picks.Count >= PresentationCount) break;
        }

        return picks;
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var map = new Dictionary<string, object?>(pairs.Length, StringComparer.Ordinal);
        foreach (var (key, value) in pairs) map[key] = value;
        return map;
    }
}
