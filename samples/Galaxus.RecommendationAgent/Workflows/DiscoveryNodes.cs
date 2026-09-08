// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// Stage 1 — turns a customer's signals into an interest map. One structured model call on the
/// live path, zero on the offline one.
/// </summary>
/// <remarks>
/// The mapper is never given the catalogue and its output has no product-id field, so a
/// hallucinated SKU is structurally impossible here rather than merely unlikely.
/// </remarks>
public interface IInterestMapperNode
{
    /// <summary>Populates <see cref="DiscoveryState.Interests"/>, anti-interests and constraints.</summary>
    /// <param name="state">The run state, mutated in place and returned.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<DiscoveryState> MapAsync(DiscoveryState state, CancellationToken cancellationToken);
}

/// <summary>
/// Stage 2 — runs one round's query plan. ZERO model calls, always: round 1's queries come from
/// the interest map and round 2+'s come from the reviewer's gaps.
/// </summary>
public interface IDiscoverySearchNode
{
    /// <summary>Executes the plan, ingests candidates, and increments the round counter.</summary>
    /// <param name="state">The run state, mutated in place and returned.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<DiscoveryState> RunRoundAsync(DiscoveryState state, CancellationToken cancellationToken);
}

/// <summary>
/// Stage 3 — the coverage gate. A deterministic pre-gate that can REJECT for free, then at most
/// one model call that may approve.
/// </summary>
public interface ICoverageReviewerNode
{
    /// <summary>Projects a verdict onto the state's routing fields. Never throws.</summary>
    /// <param name="state">The run state, mutated in place and returned.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<DiscoveryState> ReviewAsync(DiscoveryState state, CancellationToken cancellationToken);
}

/// <summary>Stage 4 — selects and orders from the candidate set. Post-checks run afterwards, in code.</summary>
public interface IRankerNode
{
    /// <summary>Populates <see cref="DiscoveryState.Ranked"/>.</summary>
    /// <param name="state">The run state, mutated in place and returned.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<DiscoveryState> RankAsync(DiscoveryState state, CancellationToken cancellationToken);
}

/// <summary>Stage 5 — renders the answer. Price and stock are read live here, never from model context.</summary>
public interface IPresenterNode
{
    /// <summary>Composes <see cref="DiscoveryState.FinalAnswer"/> and prints the customer-facing panel.</summary>
    /// <param name="state">The run state, mutated in place and returned.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<DiscoveryState> PresentAsync(DiscoveryState state, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the query plan for a round.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is the loop's actual argument.</b> Round 1's queries are written from the
/// interest map — that is, from the customer's own vocabulary, before a single catalogue record
/// has been seen. Round 2+'s queries are written from the reviewer's gaps, which were written
/// after seeing the category paths and attribute keys that came back. The difference between
/// <see cref="QueryPlanEntry.FromMap"/> and <see cref="QueryPlanEntry.FromGap"/> is printed on
/// every search line so the audience can watch the vocabulary change hands.
/// </para>
/// <para>
/// A single agent with a search tool can call it twice; what it cannot do is write its second
/// query against documents it has not retrieved yet. That is the honest claim, and it is narrow.
/// </para>
/// </remarks>
public static class DiscoveryQueryPlanner
{
    /// <summary>How many query terms one interest contributes to round 1.</summary>
    public const int MaxTermsPerInterest = 3;

    /// <summary>Candidates requested per query.</summary>
    public const int TopKPerQuery = 6;

    /// <summary>
    /// Builds the plan for the round that is about to run.
    /// </summary>
    /// <param name="state">The run state.</param>
    /// <returns>At most <see cref="DiscoveryState.MaxQueriesPerRound"/> entries, deterministically ordered.</returns>
    public static IReadOnlyList<QueryPlanEntry> BuildPlan(DiscoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var plan = new List<QueryPlanEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Round 2+ : the reviewer's gaps come FIRST and are the reason we are here ──
        foreach (var gap in state.OpenGaps)
        {
            if (plan.Count >= DiscoveryState.MaxQueriesPerRound) break;
            if (string.IsNullOrWhiteSpace(gap.NextQuery)) continue;
            if (!seen.Add(Key(gap.InterestId, gap.NextQuery))) continue;

            plan.Add(new QueryPlanEntry(
                gap.InterestId, gap.NextQuery.Trim(), gap.NextCategory, gap.NextAttributes, QueryPlanEntry.FromGap));
        }

        // ── Any interest with no query yet — including one the reviewer just added ────
        //
        // ⚠ A reviewer-added interest's terms came out of REVIEW TEXT on a candidate this run had
        // already retrieved, so they are not the customer's pre-retrieval vocabulary and must not
        // be labelled as if they were. That is what QueryPlanEntry.FromProposal is for.
        foreach (var interest in state.Interests.OrderByDescending(i => i.Confidence)
                                                .ThenBy(i => i.Id, StringComparer.Ordinal))
        {
            var coverage = state.CoverageFor(interest.Id);
            var origin = interest.Origin == InterestOrigin.ReviewerInferred
                ? QueryPlanEntry.FromProposal
                : QueryPlanEntry.FromMap;

            // An interest that already has queries behind it is only re-searched through a GAP.
            // Re-running round 1's terms is precisely the round that adds only tokens.
            if (coverage.QueriesRun.Count > 0) continue;

            var category = interest.CategoryHints.Count > 0 ? interest.CategoryHints[0] : null;

            int taken = 0;
            foreach (var term in interest.QueryTerms)
            {
                if (plan.Count >= DiscoveryState.MaxQueriesPerRound) break;
                if (taken >= MaxTermsPerInterest) break;
                if (string.IsNullOrWhiteSpace(term)) continue;
                if (!seen.Add(Key(interest.Id, term))) continue;

                plan.Add(new QueryPlanEntry(
                    interest.Id,
                    term.Trim(),
                    taken == 0 ? category : null,   // the category hint gates the FIRST term only,
                                                    // so a wrong hint cannot silently empty the interest
                    taken == 0 && interest.AttributeHints.Count > 0 ? interest.AttributeHints : null,
                    origin));
                taken++;
            }
        }

        return plan;
    }

    private static string Key(string interestId, string query) =>
        interestId + "|" + query.Trim();
}

/// <summary>
/// Shared, catalogue-owned helpers the nodes use to turn products into candidates and back.
/// </summary>
public static class DiscoveryProjection
{
    /// <summary>
    /// Builds a candidate from a catalogue record and the query that surfaced it.
    /// </summary>
    /// <remarks>
    /// Review snippets are truncated to <see cref="DiscoveryState.ReviewSnippetCharacterBudget"/>
    /// and the truncation is announced IN BAND, so a model reading a clipped snippet knows it was
    /// clipped rather than inferring from an odd ending.
    /// </remarks>
    /// <param name="catalogue">The catalogue.</param>
    /// <param name="product">The retrieved product.</param>
    /// <param name="score">The fused retrieval score.</param>
    /// <param name="interestId">The interest whose query surfaced it.</param>
    /// <param name="query">The query text.</param>
    public static ProductCandidate ToCandidate(
        Catalogue catalogue,
        Product product,
        double score,
        string interestId,
        string query)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(product);

        var snippets = new List<string>(DiscoveryState.MaxReviewSnippetsPerCandidate);
        var reviewIds = new List<string>(DiscoveryState.MaxReviewSnippetsPerCandidate);

        foreach (var review in catalogue.ReviewsFor(product.Id)
                                        .OrderByDescending(r => r.HelpfulVotes)
                                        .ThenByDescending(r => r.PostedOn)
                                        .Take(DiscoveryState.MaxReviewSnippetsPerCandidate))
        {
            snippets.Add(Clip(review.Body, DiscoveryState.ReviewSnippetCharacterBudget));
            reviewIds.Add(review.Id);
        }

        return new ProductCandidate(
            product.Id,
            product.Name,
            product.CategoryPath,
            catalogue.AttributesOf(product),
            score,
            interestId,
            query,
            snippets,
            reviewIds,
            product.RatingCount,
            product.RatingAverage);
    }

    /// <summary>
    /// Truncates to a budget and SAYS SO. A silently clipped snippet is a lie about what the
    /// reviewer was shown.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="budget">Character budget.</param>
    public static string Clip(string? text, int budget)
    {
        var value = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= budget
            ? value
            : value[..Math.Max(0, budget - 22)].TrimEnd() + " …[snippet truncated]";
    }

    /// <summary>
    /// The domain <see cref="InterestMap"/> the guardrail pipeline measures against, projected
    /// from the loop's running interest map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Projecting rather than re-deriving is what keeps the two-sided evidence check honest ACROSS
    /// the loop: an interest the reviewer added in round 2 is a bar the answer can legitimately be
    /// measured against, and an interest nobody derived at all still is not.
    /// </para>
    /// <para>
    /// The evidence KIND is derived, not carried: an interest with no purchase evidence — a
    /// reviewer-inferred one, or an in-session stated need — maps to
    /// <see cref="InterestEvidenceKinds.StatedInSession"/>, which is exactly the kind whose
    /// recommendations must cite NO purchase ids. A latent one maps to
    /// <see cref="InterestEvidenceKinds.CoPurchaseContext"/>, a direct one to
    /// <see cref="InterestEvidenceKinds.CategoryDepth"/>.
    /// </para>
    /// </remarks>
    /// <param name="state">The run state.</param>
    public static InterestMap ToDomainInterestMap(DiscoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var signals = new List<InterestSignal>(state.Interests.Count);

        foreach (var interest in state.Interests)
        {
            var kind = interest.EvidenceSignalIds.Count == 0
                ? InterestEvidenceKinds.StatedInSession
                : interest.Kind == InterestKind.Latent
                    ? InterestEvidenceKinds.CoPurchaseContext
                    : InterestEvidenceKinds.CategoryDepth;

            signals.Add(new InterestSignal(
                interest.Label,
                Math.Clamp(interest.Confidence, 0.0, 1.0),
                interest.EvidenceSignalIds,
                kind));
        }

        return new InterestMap(
            state.CustomerId,
            signals,
            ExcludedBecauseGift: [],
            RoutedToReplenishment: [],
            PersonalizationEnabled: state.PersonalizationConsent);
    }

    /// <summary>The console line for one interest on the map panel.</summary>
    /// <param name="interest">The interest.</param>
    public static string InterestLine(Interest interest)
    {
        ArgumentNullException.ThrowIfNull(interest);

        var evidence = interest.EvidenceSignalIds.Count > 0
            ? "← " + string.Join(", ", interest.EvidenceSignalIds)
            : "← in-session / review text";

        return string.Create(CultureInfo.InvariantCulture,
            $"{interest.Id,-4} {interest.Kind.ToString().ToUpperInvariant(),-6} {interest.Confidence:0.00}  " +
            $"{Fit(interest.Label, 44)} {evidence}");
    }

    /// <summary>The console line for one coverage-ledger row: a bar, a count and a status.</summary>
    /// <param name="interest">The interest.</param>
    /// <param name="coverage">Its ledger row.</param>
    public static string CoverageLine(Interest interest, InterestCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(interest);
        ArgumentNullException.ThrowIfNull(coverage);

        // ⚠ The bar is the ATTRIBUTABLE count and the credited count is printed beside it whenever
        //   the two differ. A ledger that shows only "2 candidate(s)" next to UNCOVERED reads as a
        //   broken gate; showing "2 credited, 0 attributable" says what happened.
        //
        // ⚠ AND THE BAR IS NOT THE STATUS. ClassifyCoverage decides UNCOVERED / COVERED from the
        //   candidate count, the score floor and whether the interest names anything at all — NOT
        //   from this count. So an empty bar beside COVERED is a real and deliberate sight: it is
        //   USR-NB-01's "Headlamps", covered on six candidates of which zero is a headlamp. That
        //   disagreement is the finding (InterestCoverage.AttributableProductIds), and hiding it by
        //   drawing the bar from the credited count instead would be hiding it.
        int attributable = coverage.AttributableProductIds.Count;
        int filled = Math.Clamp(attributable * 2, 0, 10);
        var bar = new string('█', filled) + new string('░', 10 - filled);

        var reason = coverage.LastGapReason is { Length: > 0 } why ? "  " + Fit(why, 46) : string.Empty;
        string credited = attributable == coverage.CandidateProductIds.Count
            ? string.Empty
            : $" of {coverage.CandidateProductIds.Count} credited";

        return string.Create(CultureInfo.InvariantCulture,
            $"{interest.Id,-4} {bar}  {attributable,2} attributable{credited}  " +
            $"{coverage.Status.ToString().ToUpperInvariant(),-10}{reason}");
    }

    private static string Fit(string text, int width) =>
        text.Length <= width ? text.PadRight(width) : text[..Math.Max(0, width - 1)] + "…";
}
