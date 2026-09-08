// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Tools;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// The composition root for every eval in this project: binds the retriever once, opens the
/// per-run tool scopes, and hands back the retrieval mode so a degraded run is reported rather
/// than quietly scored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the retriever must be bound before ANY eval runs.</b> <c>GalaxusTools</c> is a static
/// tool surface with an explicit <c>Bind</c> seam; unbound, every semantic tool returns
/// <c>refused/retriever_unbound</c>. An agent that can never search would present nothing, and
/// "presented nothing" reads on an integrity report as a clean run on the four prohibition
/// cases. That is the flattering-direction failure this whole suite exists to catch, so the
/// binding is asserted, not assumed.
/// </para>
/// <para>
/// <b>Offline by construction, and the SPACE is selected in one place.</b> The source comes from
/// <see cref="EmbeddingSpace"/>, which resolves to <see cref="ConceptEmbeddingSource"/> by default
/// (authored concept vectors, deterministic, no API key) and to
/// <see cref="PrecomputedEmbeddingSource"/> over the committed <c>text-embedding-3-small</c> assets
/// under <c>--real-vectors</c>. Neither ever attaches a live fallback, so no eval can spend money
/// on an embedding call by accident. Which space was used is printed by
/// <see cref="EnsureBoundAsync"/> on the bind that builds the index, and reported by the
/// <c>AuthoredQueryPhraseRetrievability</c>
/// control, because a coverage number produced in the concept space and one produced in
/// <c>text-embedding-3-small</c> are not comparable and must never be tabulated together.
/// </para>
/// </remarks>
public static class EvalRuntime
{
    private static IProductRetriever? _retriever;

    /// <summary>The tool-call cap opened around every agent turn. Matches Demo 1's default.</summary>
    public const int ToolCallCap = ToolCallBudget.DefaultMaxCalls;

    /// <summary>The retriever bound to <c>GalaxusTools</c>, or null before <see cref="EnsureBoundAsync"/>.</summary>
    public static IProductRetriever? Retriever => _retriever;

    /// <summary>
    /// Builds the offline hybrid retriever and binds it to the tool surface. Idempotent: a
    /// second call reuses the first index rather than re-embedding the whole catalogue.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The bound retriever.</returns>
    /// <exception cref="InvalidOperationException">The dense leg came up unavailable.</exception>
    public static async Task<IProductRetriever> EnsureBoundAsync(CancellationToken ct = default)
    {
        if (_retriever is not null && GalaxusTools.IsBound) return _retriever;

        var catalogue = Catalogue.Default;
        var space     = EmbeddingSpace.Resolve(catalogue.All);

        // Printed on the ONE bind that builds the index, before any eval has scored anything. A
        // coverage cell from the concept space and one from text-embedding-3-small answer
        // different questions, and a report that does not name the space invites a reader to
        // tabulate them together.
        space.PrintBanner();

        HybridRetriever retriever = await HybridRetriever
            .BuildAsync(catalogue.All, space.Source, cancellationToken: ct)
            .ConfigureAwait(false);

        GalaxusTools.Bind(retriever);
        GalaxusTools.AssertBound();
        _retriever = retriever;

        // Wiring check in the direction that matters. A lexical-only retriever still returns
        // hits, so "the search worked" is not evidence that the semantic leg is alive — and the
        // whole cross-category claim rides on the dense leg. Fail loudly here rather than
        // quietly scoring a different system.
        if (!retriever.DenseAvailable)
        {
            throw new InvalidOperationException(
                "The dense retrieval leg is unavailable, so this run would score a lexical-only agent " +
                "while reporting a hybrid one. Refusing to run. " +
                HybridRetriever.DegradedBannerText);
        }

        return retriever;
    }

    /// <summary>
    /// Opens the per-turn tool scopes: the call budget and the presentation capture. Dispose
    /// after the agent turn completes.
    /// </summary>
    /// <remarks>
    /// Both scopes are <c>AsyncLocal</c>, so they flow into
    /// <c>MAFEvaluationHarness.RunEvaluationAsync</c> and on into the tool invocations it
    /// triggers. A fresh scope per turn is what stops one case's spend from silencing the next
    /// one's answer channel.
    /// </remarks>
    /// <param name="toolCallCap">The per-turn cap.</param>
    public static IDisposable BeginTurn(int toolCallCap = ToolCallCap) => new TurnScope(toolCallCap);

    private sealed class TurnScope : IDisposable
    {
        private readonly IDisposable _budget;
        private readonly IDisposable _capture;
        private bool _disposed;

        public TurnScope(int toolCallCap)
        {
            _budget = ToolCallBudget.BeginScope(toolCallCap);
            _capture = GalaxusTools.BeginRunCapture();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _capture.Dispose();
            _budget.Dispose();
        }
    }
}
