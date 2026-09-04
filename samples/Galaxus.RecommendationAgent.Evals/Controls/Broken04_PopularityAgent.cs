// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Negative control #4 — the popularity baseline. Ignores the customer entirely.
/// </summary>
/// <remarks>
/// <para>
/// It presents the five global bestsellers with resolving citations and never looks at the
/// persona. It exists to put an empirical number under one of Eval 02's floors, at the cost of one
/// no-LLM run.
/// </para>
/// <para>
/// <b>One correction to the design, and it is the difference between a number and a claim.</b> §C.2
/// pre-registers popularity coverage at exactly 0.00, but that figure is a property of a bestseller
/// list AUTHORED to carry zero latent tokens. This catalogue's <c>Catalogue.BestsellerSkus</c> is
/// DERIVED — rating count, then helpful votes, then id — so nothing guarantees the twelve
/// bestsellers avoid every persona's latent tokens. The design's 0.00 therefore does not transfer,
/// and quoting it would be self-flattery of exactly the kind the design itself warns against ("an
/// absent baseline is not a zero floor"). This arm's coverage is <b>MEASURED and printed</b>, and
/// whatever it turns out to be is the number the report carries.
/// </para>
/// </remarks>
public sealed class Broken04_PopularityAgent : IEvaluableAgent
{
    /// <summary>How many bestsellers it presents — the budget the canonical utterance declares.</summary>
    public const int PresentationCount = GalaxusDemoPrompts.CoverageCohortDeclaredK;

    /// <inheritdoc/>
    public string Name => nameof(Broken04_PopularityAgent);

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var catalogue = Catalogue.Default;
        var trace = new ScriptedTrace();

        foreach (string sku in catalogue.BestsellerSkus.Take(PresentationCount))
        {
            if (!catalogue.TryGet(sku, out var product) || product is null) continue;

            string? citation = Broken03_SingleShotWorkflow.FirstResolvingCitation(product);
            if (citation is null) continue;

            trace.Present(product.Id,
                $"One of our most popular products — {product.Name}.",
                citation,
                outOfStock: product.StockUnits == 0);
        }

        trace.Say("Our current bestsellers.");
        return Task.FromResult(trace.ToResponse());
    }

    /// <summary>The SKUs this arm presents, for the report.</summary>
    public static IReadOnlyList<string> Selection =>
        [.. Catalogue.Default.BestsellerSkus.Take(PresentationCount)];
}
