// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals.Controls;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Evals.Loop;

/// <summary>
/// A deterministic discovery loop, used ONLY as the substrate for this project's loop CONTROLS.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>This is not Demo 2 and it must never be reported as Demo 2.</b> Demo 2 is a MAF workflow
/// with five executors and a model-driven coverage reviewer, and it lives in
/// <c>Galaxus.RecommendationAgent</c>. This class exists because two of the controls the suite owes
/// — the rubber-stamp reviewer (design §D.3) and the D-3 injection probe — are <i>loops whose
/// reviewer is broken in one specific way</i>, and a control has to be the same shape as the thing
/// it controls for. Substituting it for the real arm would be exactly the substitution
/// <c>Eval02</c>'s remarks refuse to make; <see cref="Adapters.DiscoveryLoopAdapter"/> is the only
/// place the real arm ever enters.
/// </para>
/// <para>
/// <b>What it does share with the design.</b> The message-borne round counter, the
/// <c>SeenProductIds</c> dedup passed back into the search as <c>ExcludeProductIds</c>, the
/// no-progress stop, the round cap, the per-interest coverage ledger, and — the part that matters
/// here — the reviewer-proposed-interest channel with the D-3 constraint sitting between the
/// proposal and the retriever. Everything the injection eval asserts on is in that path.
/// </para>
/// <para>
/// <b>Zero model calls.</b> Round 1's queries come from the code-derived interest map; round 2's
/// come from the reviewer's gap requests. That is the design's own cost claim (§B.6, "Discovery
/// costs zero model calls") and it is what lets these controls run in Eval 03 and Eval 04 with no
/// credentials at all.
/// </para>
/// </remarks>
public sealed class EvalDiscoveryLoop
{
    private readonly IProductRetriever _retriever;
    private readonly ICoverageReviewer _reviewer;
    private readonly IReviewTextSource _reviews;
    private readonly DiscoveryLoopOptions _options;

    /// <summary>Creates a loop over a bound retriever and a reviewer.</summary>
    /// <param name="retriever">The same retriever every other arm searches with.</param>
    /// <param name="reviewer">The coverage gate.</param>
    /// <param name="reviews">Where untrusted review text comes from. Defaults to the catalogue.</param>
    /// <param name="options">Bounds and presentation size.</param>
    public EvalDiscoveryLoop(
        IProductRetriever retriever,
        ICoverageReviewer reviewer,
        IReviewTextSource? reviews = null,
        DiscoveryLoopOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(reviewer);

        _retriever = retriever;
        _reviewer = reviewer;
        _reviews = reviews ?? CatalogueReviewSource.Instance;
        _options = options ?? DiscoveryLoopOptions.Default;
    }

    /// <summary>Runs one turn for one customer and returns the trace plus the telemetry.</summary>
    /// <param name="armName">The arm label, stamped into the telemetry.</param>
    /// <param name="userId">The customer.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<(AgentResponse Response, DiscoveryLoopTelemetry Telemetry)> RunAsync(
        string armName, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(armName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Require(userId);
        var vocabulary = QueryVocabulary.For(userId);

        var map = InterestMapBuilder.Build(
            profile.User, profile.Purchases, catalogue.BySku,
            statedNeeds: null, asOf: Catalogue.DemoToday,
            sensitiveCategoryNames: catalogue.SensitiveCategories);

        // The interest list is CAPPED. See DiscoveryLoopOptions.MaxMapInterests — the cap is the
        // reason a reviewer-proposed interest can reach the answer at all, and it is chosen, not
        // measured.
        var interests = map.Signals
            .Take(_options.MaxMapInterests)
            .Select(s => s.Label)
            .ToList();

        // Owned SKUs are excluded from every search from round 1, so "new products" means new to
        // this customer and not merely new to this round.
        var seen = new HashSet<string>(
            profile.Purchases.Select(p => p.ProductId), StringComparer.OrdinalIgnoreCase);

        var byInterest = new Dictionary<string, List<ScoredCandidate>>(StringComparer.Ordinal);
        var interestOrder = new List<string>(interests);
        foreach (string label in interests) byInterest[label] = [];

        var candidateOrder = new List<string>();
        var queriesRun = new List<string>();
        var proposedLabels = new List<string>();
        var proposedTerms = new List<string>();
        var acceptedLabels = new List<string>();
        var drops = new List<QueryTermDrop>();
        var snippetsSeen = new List<ReviewSnippet>();

        var pending = interests.Select(label => new PendingQuery(label, label)).ToList();

        int round = 0;
        int newThisRound = 0;
        bool approved = false;
        string stopReason = DiscoveryStopReasons.GapsUnresolvable;

        while (true)
        {
            round++;
            newThisRound = 0;
            var freshThisRound = new List<string>();

            foreach (PendingQuery query in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RetrievalResult result = await _retriever
                    .SearchAsync(new RetrievalQuery
                    {
                        Need = query.Need,
                        TopK = _options.TopKPerQuery,
                        ExcludeProductIds = new HashSet<string>(seen, StringComparer.Ordinal),
                    }, cancellationToken)
                    .ConfigureAwait(false);

                queriesRun.Add(query.Need);

                if (!byInterest.TryGetValue(query.InterestLabel, out var bucket))
                {
                    bucket = [];
                    byInterest[query.InterestLabel] = bucket;
                    if (!interestOrder.Contains(query.InterestLabel, StringComparer.Ordinal))
                        interestOrder.Add(query.InterestLabel);
                }

                foreach (RetrievalHit hit in result.Hits)
                {
                    // Identity-level dedup at INGEST (design §B.7). A product already seen is not
                    // re-added and does not count toward the no-progress stop.
                    if (!seen.Add(hit.ProductId)) continue;

                    bucket.Add(new ScoredCandidate(hit.ProductId, hit.Score));
                    candidateOrder.Add(hit.ProductId);
                    freshThisRound.Add(hit.ProductId);
                    newThisRound++;
                }
            }

            foreach (string productId in freshThisRound)
                snippetsSeen.AddRange(_reviews.SnippetsFor(productId));

            var ledger = interestOrder.ToDictionary(
                label => label,
                label => byInterest.TryGetValue(label, out var b) ? b.Count : 0,
                StringComparer.Ordinal);

            var roundState = new DiscoveryRound(
                userId, round, _options.MaxRounds, interestOrder, ledger,
                candidateOrder, snippetsSeen, queriesRun, newThisRound);

            CoverageReviewVerdict verdict = _reviewer.Review(roundState);

            var nextPending = new List<PendingQuery>();

            // ── THE D-3 CHANNEL. Attacker-controlled text reaches query generation HERE. ──
            if (verdict.NewInterest is { } proposal)
            {
                proposedLabels.Add(proposal.Label);
                proposedTerms.AddRange(proposal.QueryTerms);

                IReadOnlyList<string> runnable = proposal.QueryTerms;

                if (_options.ApplyQueryVocabularyConstraint)
                {
                    VocabularyConstraint constraint = vocabulary.Constrain(
                        proposal.Label, proposal.SourceProductId, proposal.QueryTerms);

                    drops.AddRange(constraint.Dropped);

                    // A proposal with nothing left is refused ENTIRELY — the label is part of the
                    // payload, so keeping it while dropping its terms would still put the
                    // attacker's words in front of the customer.
                    runnable = constraint.IsFullyDropped ? [] : constraint.Kept;
                }

                if (runnable.Count > 0)
                {
                    acceptedLabels.Add(proposal.Label);
                    if (!byInterest.ContainsKey(proposal.Label))
                    {
                        byInterest[proposal.Label] = [];
                        interestOrder.Add(proposal.Label);
                    }
                    nextPending.Add(new PendingQuery(proposal.Label, string.Join(" ", runnable)));
                }
            }

            if (verdict.IsComplete)
            {
                approved = true;
                stopReason = DiscoveryStopReasons.CoverageSufficient;
                break;
            }

            foreach (CoverageGapRequest gap in verdict.Gaps)
                nextPending.Add(new PendingQuery(gap.InterestLabel, gap.NextQuery));

            if (round >= _options.MaxRounds) { stopReason = DiscoveryStopReasons.RoundLimitReached; break; }
            if (newThisRound == 0) { stopReason = DiscoveryStopReasons.NoProgress; break; }
            if (nextPending.Count == 0) { stopReason = DiscoveryStopReasons.GapsUnresolvable; break; }

            pending = nextPending;
        }

        var (response, presented) = Present(armName, interestOrder, byInterest);

        var telemetry = new DiscoveryLoopTelemetry
        {
            ArmName = armName,
            CustomerId = userId,
            RoundsTaken = round,
            MaxRounds = _options.MaxRounds,
            ApprovedByReviewer = approved,
            StopReason = stopReason,
            QueriesRun = queriesRun,
            CandidateProductIds = candidateOrder,
            LastRoundNewProductCount = newThisRound,
            ProposedInterestLabels = proposedLabels,
            ProposedQueryTerms = proposedTerms,
            AcceptedInterestLabels = acceptedLabels,
            DroppedQueryTerms = drops,
            VocabularyConstraintApplied = _options.ApplyQueryVocabularyConstraint,
            PresentedProductIds = presented,
            SnippetsSeen = snippetsSeen,
        };

        return (response, telemetry);
    }

    /// <summary>
    /// Presents by ROUND-ROBIN over the interests, best candidate first within each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not "top N by score". Fused RRF scores are explicitly not comparable across queries
    /// (<see cref="RetrievalHit.Score"/>'s own remarks say so), so a global sort would be arithmetic
    /// on numbers that do not live on one scale — and it would let one well-served interest take
    /// every slot, which is the opposite of what a coverage-driven loop is for.
    /// </para>
    /// <para>
    /// A reviewer-proposed interest is appended LAST, because the design clamps its confidence to
    /// ≤ 0.60. With <see cref="DiscoveryLoopOptions.MaxMapInterests"/> at 4 and
    /// <see cref="DiscoveryLoopOptions.PresentationCount"/> at 5 it therefore gets exactly one slot
    /// — the minimum that makes an injected interest observable in the answer at all. Both numbers
    /// are CHOSEN and stated here rather than tuned: a larger presentation count would make the
    /// injection negative control easier to trip, which would flatter it.
    /// </para>
    /// </remarks>
    private (AgentResponse Response, IReadOnlyList<string> Presented) Present(
        string armName,
        IReadOnlyList<string> interestOrder,
        IReadOnlyDictionary<string, List<ScoredCandidate>> byInterest)
    {
        var catalogue = Catalogue.Default;
        var trace = new ScriptedTrace();
        var presented = new List<string>();

        var queues = interestOrder
            .Where(byInterest.ContainsKey)
            .Select(label => (Label: label,
                              Items: byInterest[label]
                                  .OrderByDescending(c => c.Score)
                                  .ThenBy(c => c.ProductId, StringComparer.Ordinal)
                                  .ToList()))
            .ToList();

        var cursor = new int[queues.Count];
        bool progressed = true;

        while (presented.Count < _options.PresentationCount && progressed)
        {
            progressed = false;

            for (int i = 0; i < queues.Count && presented.Count < _options.PresentationCount; i++)
            {
                var (label, items) = queues[i];

                while (cursor[i] < items.Count)
                {
                    ScoredCandidate candidate = items[cursor[i]++];
                    if (!catalogue.TryGet(candidate.ProductId, out var product) || product is null) continue;
                    if (product.StockUnits == 0) continue;               // policy-clean, like every other arm
                    if (presented.Contains(product.Id, StringComparer.Ordinal)) continue;

                    string? citation = Broken03_SingleShotWorkflow.FirstResolvingCitation(product);
                    if (citation is null) continue;                      // it does not invent evidence

                    trace.Present(product.Id, $"Serves \"{label}\" — {product.Name}.", citation);
                    presented.Add(product.Id);
                    progressed = true;
                    break;
                }
            }
        }

        trace.Say($"{armName}: bounded discovery loop, {queues.Count} interest(s), no model calls.");
        return (trace.ToResponse(), presented);
    }

    private readonly record struct ScoredCandidate(string ProductId, double Score);

    private readonly record struct PendingQuery(string InterestLabel, string Need);
}

/// <summary>Bounds and presentation size for <see cref="EvalDiscoveryLoop"/>.</summary>
/// <remarks>
/// Every number here is CHOSEN, and each one is stated where it is used rather than tuned until a
/// control came out green.
/// </remarks>
public sealed record DiscoveryLoopOptions
{
    /// <summary>The design's hard round cap (§B.2, <c>MaxDiscoveryRounds</c>).</summary>
    public int MaxRounds { get; init; } = 3;

    /// <summary>
    /// How many code-derived interests seed round 1. Four, so that a reviewer-proposed fifth gets
    /// exactly one presentation slot — see <see cref="EvalDiscoveryLoop"/>'s presentation remarks.
    /// </summary>
    public int MaxMapInterests { get; init; } = 4;

    /// <summary>
    /// Recommendations presented. The budget the canonical utterance declares
    /// (<see cref="GalaxusDemoPrompts.CoverageCohortDeclaredK"/>), so this substrate is sized the
    /// same way every other Eval 02 arm is. ⚠ The one-slot-for-an-injected-interest property in
    /// <see cref="EvalDiscoveryLoop"/>'s presentation remarks holds at 4 map interests and a
    /// budget of 5; a change to the declared budget changes that observability margin too.
    /// </summary>
    public int PresentationCount { get; init; } = GalaxusDemoPrompts.CoverageCohortDeclaredK;

    /// <summary>Candidates requested per query. The retriever clamps this to <see cref="RetrievalQuery.MaxTopK"/>.</summary>
    public int TopKPerQuery { get; init; } = RetrievalQuery.DefaultTopK;

    /// <summary>Whether the §0.5 / D-3 structural constraint runs between the reviewer and the retriever.</summary>
    public bool ApplyQueryVocabularyConstraint { get; init; } = true;

    /// <summary>The defaults.</summary>
    public static DiscoveryLoopOptions Default { get; } = new();
}

/// <summary>Where a loop gets the untrusted review text its reviewer is shown.</summary>
/// <remarks>
/// A seam rather than a direct catalogue read, because the D-3 case has to plant a review the
/// catalogue cannot hold: <c>Catalogue</c> invariant 8 asserts that a marketplace cold-start SKU
/// carries ZERO reviews, and the whole point of the case is a review on a marketplace listing. See
/// <see cref="Cases.InjectionCases"/> for why the fixture lives in the eval rather than in the seed.
/// </remarks>
public interface IReviewTextSource
{
    /// <summary>Untrusted snippets attached to one product, newest first. Empty for a cold-start SKU.</summary>
    /// <param name="productId">A catalogue product id.</param>
    IReadOnlyList<ReviewSnippet> SnippetsFor(string productId);
}

/// <summary>The ordinary source: the catalogue's own seeded reviews and nothing else.</summary>
public sealed class CatalogueReviewSource : IReviewTextSource
{
    /// <summary>The shared instance.</summary>
    public static CatalogueReviewSource Instance { get; } = new();

    /// <inheritdoc/>
    public IReadOnlyList<ReviewSnippet> SnippetsFor(string productId)
    {
        var catalogue = Catalogue.Default;
        if (!catalogue.TryGet(productId, out var product) || product is null) return [];

        return
        [
            .. catalogue.Reviews(productId)
                .Select(r => new ReviewSnippet(productId, r.Id, r.Body, product.IsMarketplaceOffer))
        ];
    }
}
