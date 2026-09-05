// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// Stage 2 of the loop, and the only stage that is identical on the live and the offline paths:
/// it makes ZERO model calls, by design.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the fan-out lives, and why it is not in the graph.</b> The query count is
/// data-dependent (two to six interests, plus whatever the reviewer added), so a graph-level
/// fan-out would need a join executor, a message-identity scheme and an aggregation contract —
/// and the loop-back edge would have to target a node that no longer exists as a single point.
/// <c>Task.WhenAll</c> over the plan keeps ONE message type on every edge, which is exactly what
/// makes the loop-back a one-line <c>AddEdge</c>.
/// </para>
/// <para>
/// <b>Dedup is identity-level and it is also an exclusion.</b> <see cref="DiscoveryState.SeenProductIds"/>
/// is checked at ingest AND passed into retrieval as <see cref="RetrievalQuery.ExcludeProductIds"/>,
/// so round 2 does not spend its budget re-finding round 1. A presentational-only dedup — one that
/// keeps the duplicate and hides it at render — would make <see cref="DiscoveryState.LastRoundNewProductCount"/>
/// non-zero on a round that discovered nothing, and the no-progress stop would never fire.
/// </para>
/// <para>
/// <b>The round counter is incremented HERE, by the producer, at the end of a round that
/// completed.</b> A round that throws therefore does not consume budget.
/// </para>
/// </remarks>
/// <param name="catalogue">The catalogue façade.</param>
/// <param name="retriever">The retrieval seam — <c>HybridRetriever</c> in both demos.</param>
/// <param name="progress">Where the round's search trace goes.</param>
public sealed class CatalogueDiscoverySearch(
    Catalogue catalogue,
    IProductRetriever retriever,
    IDiscoveryProgressSink progress) : IDiscoverySearchNode
{
    private readonly Catalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly IProductRetriever _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
    private readonly IDiscoveryProgressSink _progress = progress ?? NullDiscoveryProgressSink.Instance;

    /// <inheritdoc />
    public async ValueTask<DiscoveryState> RunRoundAsync(DiscoveryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        int round = state.DiscoveryRound + 1;
        _progress.Publish(DiscoveryEvent.RoundStarted(round, state.MaxRounds));

        state.LastQueryPlan.Clear();
        state.LastQueryPlan.AddRange(DiscoveryQueryPlanner.BuildPlan(state));

        // Everything already seen, plus everything the customer owns. Both are exclusions on the
        // QUERY, not filters on the result: filtering after top-k silently returns fewer than k.
        var exclude = new HashSet<string>(state.SeenProductIds, StringComparer.Ordinal);
        exclude.UnionWith(state.OwnedProductIds);

        var plan = state.LastQueryPlan;

        // ── the fan-out: n concurrent searches, one message type, zero model calls ───
        var tasks = new Task<RetrievalResult>[plan.Count];
        for (int i = 0; i < plan.Count; i++)
            tasks[i] = ExecuteAsync(plan[i], state, exclude, cancellationToken);

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        int newIds = 0;
        int duplicates = 0;

        // Ingest in PLAN order, not completion order, so two runs of the same input produce the
        // same ledger. A demo whose numbers move between identical runs teaches the audience to
        // distrust every other number on the screen.
        for (int i = 0; i < plan.Count; i++)
        {
            var entry = plan[i];
            var result = results[i];

            var coverage = state.CoverageFor(entry.InterestId);
            coverage.QueriesRun.Add(entry.Query);

            // The interest this query was run FOR. Attribution is decided against it, not against
            // the score the retriever happened to return.
            var forInterest = state.FindInterest(entry.InterestId);
            if (forInterest is not null)
                coverage.AttributionVocabularyEmpty = InterestAttribution.Vocabulary(forInterest).Count == 0
                    && forInterest.AttributeHints.Count == 0
                    && forInterest.CategoryHints.Count == 0;

            // Named, not counted. The search line is published AFTER ingest so it can say WHICH
            // products this query discovered — "→ 6" tells an audience a query worked, it does
            // not tell them what the loop learned, and the vocabulary-transfer argument is
            // entirely about the second thing.
            var discovered = new List<string>();
            var newIdsForThisQuery = new List<string>();

            foreach (var hit in result.Hits)
            {
                if (hit.Score > coverage.BestScore) coverage.BestScore = hit.Score;

                // ⚠ Resolved BEFORE the seen-dedup, because the credit above happens before it
                //   too: a product already seen on another interest's query still gets credited
                //   here, so it must be screened here as well or the two ledgers disagree.
                bool known = _catalogue.TryGet(hit.ProductId, out var product) && product is not null;

                if (!coverage.CandidateProductIds.Contains(hit.ProductId, StringComparer.Ordinal))
                {
                    coverage.CandidateProductIds.Add(hit.ProductId);

                    // ── The coverage signal. Not the score. ──
                    if (known && forInterest is not null
                        && InterestAttribution.IsAttributable(_catalogue, forInterest, product!, out string why))
                    {
                        coverage.AttributableProductIds.Add(hit.ProductId);
                        coverage.AttributionReasons[hit.ProductId] = why;
                        if (double.IsNaN(coverage.BestAttributableScore) || hit.Score > coverage.BestAttributableScore)
                            coverage.BestAttributableScore = hit.Score;
                    }
                }

                if (!state.SeenProductIds.Add(hit.ProductId))
                {
                    duplicates++;
                    continue;
                }

                if (!known) continue;

                var candidate = DiscoveryProjection.ToCandidate(
                    _catalogue, product, hit.Score, entry.InterestId, entry.Query);

                state.Candidates.Add(candidate);
                newIds++;
                newIdsForThisQuery.Add(candidate.ProductId);
                discovered.Add($"{candidate.ProductId}  {candidate.Title}   ({candidate.CategoryPathText})");

                // The mid-run interest-discovery channel — and, per §0.5 / D-3, the attack
                // channel. Recorded as DATA here; nothing acts on it until the reviewer has
                // proposed something and QueryVocabulary has filtered it.
                foreach (var snippet in candidate.ReviewSnippets)
                    state.ObservedSignals.Add(new ObservedSignal(candidate.ProductId, snippet, entry.InterestId));
            }

            state.QueryLog.Add(new ExecutedQuery(round, entry, result.Count, newIdsForThisQuery));
            _progress.Publish(DiscoveryEvent.Search(round, entry, result.Count, discovered));

            coverage.Status = ClassifyCoverage(coverage);
        }

        // ── the counter, incremented by the PRODUCER at the end of a completed round ──
        state.DiscoveryRound = round;
        state.LastRoundNewProductCount = newIds;
        state.SearchesRun += plan.Count;

        _progress.Publish(DiscoveryEvent.RoundComplete(round, newIds, duplicates, state.Candidates.Count));

        // The gaps this round was run FROM are consumed. The reviewer writes the next set; if it
        // writes none, OpenGaps stays empty and NeedsMoreDiscovery is false — which is the
        // GapsUnresolvable termination, expressed as an absence rather than as a flag.
        state.OpenGaps.Clear();

        return state;
    }

    private Task<RetrievalResult> ExecuteAsync(
        QueryPlanEntry entry,
        DiscoveryState state,
        IReadOnlyCollection<string> exclude,
        CancellationToken cancellationToken)
    {
        var query = new RetrievalQuery
        {
            Need = entry.Query,
            CategoryPathPrefix = entry.CategoryPathPrefix,
            Market = state.Market,
            InStockOnly = false,          // stock is a RENDER-time fact; a restock is a wait, and
                                          // demoting is the substitute-suggestion case, not a drop
            TopK = DiscoveryQueryPlanner.TopKPerQuery,
            ExcludeProductIds = exclude,
            HardFilter = BuildAttributeFilter(entry.Attributes, _catalogue)
        };

        return _retriever.SearchAsync(query, cancellationToken).AsTask();
    }

    /// <summary>
    /// Turns a gap's <c>next_attributes</c> into a HARD pre-filter.
    /// </summary>
    /// <remarks>
    /// This is the mechanism the demo's central claim rests on: an attribute pair the reviewer
    /// could only have written after seeing a real catalogue record becomes a code-level gate on
    /// the next round's retrieval. It is a predicate over the catalogue's own token set, so a
    /// value the catalogue does not carry matches nothing and the round says so — rather than
    /// quietly widening back to a free-text search that looks like it worked.
    /// </remarks>
    /// <param name="attributes">Name/value pairs, already filtered by <see cref="QueryVocabulary"/>.</param>
    /// <param name="catalogue">
    /// Supplies the MEMOISED attribute-token set. <see cref="Product.Attributes"/> recomputes on
    /// every access by design, and this predicate runs once per candidate.
    /// </param>
    public static Func<Product, bool>? BuildAttributeFilter(
        IReadOnlyDictionary<string, string>? attributes,
        Catalogue? catalogue = null)
    {
        if (attributes is null || attributes.Count == 0) return null;

        var wanted = new List<(string Key, string Value, string Fused)>(attributes.Count);
        foreach (var (key, value) in attributes)
        {
            var k = Product.NormalizeAttributeToken(key);
            var v = Product.NormalizeAttributeToken(value);
            if (k.Length == 0 || v.Length == 0) continue;
            wanted.Add((k, v, $"{k}={v}"));
        }

        if (wanted.Count == 0) return null;

        return product =>
        {
            var tokens = catalogue is not null ? catalogue.AttributesOf(product) : product.Attributes;
            foreach (var (_, value, fused) in wanted)
                if (!tokens.Contains(fused) && !tokens.Contains(value))
                    return false;
            return true;
        };
    }

    /// <summary>
    /// The mechanical coverage status, from counts and scores alone.
    /// </summary>
    /// <remarks>
    /// It is deliberately NOT the reviewer's verdict — the reviewer may downgrade a mechanically
    /// "covered" row to a gap ("two hydration packs cover the bag half of the interest"), but it
    /// may not upgrade a structurally empty one. That asymmetry is the whole point of the
    /// pre-gate: a cheap accept is exactly the rubber-stamp failure this design exists to prevent.
    /// </remarks>
    /// <param name="coverage">The ledger row.</param>
    public static CoverageStatus ClassifyCoverage(InterestCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        if (coverage.QueriesRun.Count == 0) return CoverageStatus.Unexplored;

        // ⚠ AN INTEREST THAT NAMES NOTHING CANNOT BE COVERED BY ANYTHING.
        //
        // This is the gate that let Luca (USR-LF-04) through. His utterance — "Hi — what do you
        // recommend for me?" — carries no product content, so the interest built from it has an
        // EMPTY attribution vocabulary: no attribute hint, no category hint, and not one content
        // word once our own "stated this session: " label prefix is removed. The retriever still
        // returns its best match for a contentless query, because something is always top of a
        // ranked list, and the two rules below then counted those matches as coverage. When the
        // re-derived real-vectors dense floor let two arbitrary products through, 0 candidates and
        // GAPS_UNRESOLVABLE became 2 candidates, a second round and five recommendations — two of
        // them espresso accessories credited to an "Over-ear wireless" interest
        // (MEASUREMENT_STATUS §22).
        //
        // ⚠ THE THRESHOLDS BELOW ARE UNTOUCHED. MinCandidateScore is still 0.012 and the dense
        // floor is still the per-space derived one. Moving either to make one persona come out
        // right would fit a calibrated number to a result AND leave the gate asking the same wrong
        // question for everyone else. What changed is the question: an interest is not covered by
        // products that answer a query it never really asked.
        if (coverage.AttributionVocabularyEmpty) return CoverageStatus.Uncovered;

        if (coverage.CandidateProductIds.Count == 0) return CoverageStatus.Uncovered;
        if (coverage.BestScore < DiscoveryState.MinCandidateScore) return CoverageStatus.Uncovered;

        return coverage.CandidateProductIds.Count >= DiscoveryState.MinCandidatesForCoverage
            ? CoverageStatus.Covered
            : CoverageStatus.Partial;
    }
}
