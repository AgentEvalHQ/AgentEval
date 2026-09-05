// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// The deterministic half of the coverage gate — everything that must not be left to a model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pre-gate's asymmetry is the point.</b> It can REJECT for free; it can never APPROVE
/// for free. A cheap accept is precisely the rubber-stamp failure this whole design exists to
/// prevent, and a structurally empty interest is not something a judge gets discretion over.
/// </para>
/// <para>
/// <b>And the model cannot overrule it.</b> <see cref="Project"/> re-runs the starvation test
/// after projecting a verdict and forces <see cref="DiscoveryState.CoverageApproved"/> to false
/// if a DIRECT interest is still structurally empty — so "the reviewer approved round 1 every
/// time" cannot happen by prompt drift. Verify the wiring in BOTH directions: a reviewer that
/// never rejects and one that never approves are both faults, and both look fine on a single
/// happy-path run.
/// </para>
/// </remarks>
public static class CoverageVerdictProjection
{
    /// <summary>
    /// The cheap structural gate, run BEFORE the reviewer spends a token.
    /// </summary>
    /// <param name="state">The run state.</param>
    /// <returns>The DIRECT interests that no query has served at all, in map order.</returns>
    public static IReadOnlyList<Interest> Starved(DiscoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var starved = new List<Interest>();
        foreach (var interest in state.Interests)
        {
            if (interest.Kind != InterestKind.Direct) continue;

            var coverage = state.CoverageFor(interest.Id);
            if (coverage.QueriesRun.Count == 0) continue;           // not yet explored ≠ starved
            // ⚠ An interest that NAMES NOTHING is starved however much came back for it. A query
            //   with no content still returns a ranked list, and treating the top of that list as
            //   evidence is how a contentless utterance came to look covered (Luca / USR-LF-04,
            //   MEASUREMENT_STATUS §22). Neither threshold below has moved.
            if (coverage.AttributionVocabularyEmpty
                || coverage.CandidateProductIds.Count == 0
                || coverage.BestScore < DiscoveryState.MinCandidateScore)
            {
                starved.Add(interest);
            }
        }

        return starved;
    }

    /// <summary>
    /// Projects a verdict onto the state's routing fields, applying every structural constraint
    /// on the way in.
    /// </summary>
    /// <remarks>
    /// The verdict itself never travels an edge. This method is the only place it is read, and
    /// the edges read the fields it writes — which is what lets the verdict get as rich as it
    /// likes without touching the topology.
    /// </remarks>
    /// <param name="state">The run state, mutated in place.</param>
    /// <param name="verdict">The reviewer's verdict — model-authored or deterministic.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="progress">Where drops and proposals are published.</param>
    public static void Project(
        DiscoveryState state,
        CoverageVerdict verdict,
        Catalogue catalogue,
        IDiscoveryProgressSink progress)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(catalogue);
        progress ??= NullDiscoveryProgressSink.Instance;

        // The vocabulary is rebuilt from the MAPPER's interests only — see QueryVocabulary.
        var vocabulary = QueryVocabulary.Build(catalogue, state.Interests, state.SessionRequest);

        state.OpenGaps.Clear();
        state.ReviewNotes = verdict.Assessment ?? string.Empty;

        var covered = new HashSet<string>(verdict.CoveredInterestIds ?? [], StringComparer.Ordinal);

        // ── gaps ────────────────────────────────────────────────────────────────────
        foreach (var gap in verdict.Gaps ?? [])
        {
            var interest = state.FindInterest(gap.InterestId);
            if (interest is null) continue;                    // a gap for an interest nobody has
            covered.Remove(gap.InterestId);

            var coverage = state.CoverageFor(gap.InterestId);
            coverage.LastGapReason = gap.WhyUncovered;
            coverage.Status = coverage.CandidateProductIds.Count == 0
                ? CoverageStatus.Uncovered
                : CoverageStatus.Partial;

            // §0.5 / D-3 — the query is filtered STRUCTURALLY, whoever wrote it.
            var query = vocabulary.FilterQuery(gap.NextQuery, $"gap {gap.InterestId}", state.DroppedQueryTerms);
            if (query is null)
            {
                coverage.LastGapReason =
                    (gap.WhyUncovered ?? string.Empty) +
                    " · the proposed next query was refused by the vocabulary constraint, so this gap is not runnable";
                continue;                                       // an unrunnable gap is not a gap
            }

            // A repeated query is not a plan.
            if (coverage.QueriesRun.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                coverage.LastGapReason =
                    (gap.WhyUncovered ?? string.Empty) + " · the proposed next query repeats one already run";
                continue;
            }

            var category = vocabulary.ResolveCategory(gap.NextCategory);
            var attributes = vocabulary.FilterAttributes(gap.NextAttributes, $"gap {gap.InterestId}", state.DroppedQueryTerms);

            state.OpenGaps.Add(new CoverageGap(
                gap.InterestId, gap.WhyUncovered ?? string.Empty, query, category,
                attributes.Count > 0 ? attributes : null));
        }

        // ── covered ─────────────────────────────────────────────────────────────────
        foreach (var interestId in covered)
        {
            var coverage = state.CoverageFor(interestId);

            // The reviewer may DOWNGRADE a mechanically-covered row; it may not upgrade a
            // structurally empty one. "We found products in roughly the right category" is not
            // coverage, and neither is a claim about a row with nothing in it.
            coverage.Status = coverage.CandidateProductIds.Count == 0
                ? CoverageStatus.Uncovered
                : CoverageStatus.Covered;
        }

        // ── the mid-run proposal, and the §0.5 / D-3 control on it ──────────────────
        if (verdict.NewInterest is { } proposal)
            TryAcceptProposal(state, proposal, vocabulary, progress);

        // ── approval ────────────────────────────────────────────────────────────────
        bool approved = verdict.IsSufficient && state.OpenGaps.Count == 0;

        // ── Two structural vetoes. The reviewer does not get to approve over either. ─
        //
        // (1) A DIRECT interest that has been searched and came back with nothing.
        var starved = Starved(state);
        if (approved && starved.Count > 0)
        {
            approved = false;
            state.ReviewNotes += $" · APPROVAL VETOED IN CODE: {string.Join(", ", starved.Select(i => i.Id))} " +
                                 "has no candidate at all, and a cheap accept is the failure this gate exists to prevent";
        }

        // (2) An interest NOBODY HAS SEARCHED. This is not hypothetical: the reviewer creates
        //     exactly this state whenever it adds an interest after the round's query plan was
        //     built. Absence of evidence is not coverage, and approving here would end the run
        //     one round before the interest that justified the loop was ever explored.
        var unexplored = state.Interests
            .Where(i => state.CoverageFor(i.Id).QueriesRun.Count == 0)
            .ToList();

        if (approved && unexplored.Count > 0)
        {
            approved = false;
            state.ReviewNotes += $" · APPROVAL VETOED IN CODE: {string.Join(", ", unexplored.Select(i => i.Id))} " +
                                 "has never been searched";
        }

        state.CoverageApproved = approved;
    }

    /// <summary>
    /// Applies every structural constraint to a reviewer-proposed interest, and either adds it or
    /// refuses it with a recorded reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four constraints, all mechanical: the query terms must survive
    /// <see cref="QueryVocabulary"/>; at most
    /// <see cref="DiscoveryState.MaxReviewerInferredInterests"/> per run; the cited product must
    /// be a candidate this run actually retrieved; and the confidence is clamped to
    /// <see cref="DiscoveryState.ReviewerInferredConfidenceCeiling"/> whatever the model wrote.
    /// If no term survives, the proposal is REFUSED — an interest with no runnable query is a
    /// label, not a plan, and one whose only queries were injected is exactly the thing D-3 is
    /// about.
    /// </para>
    /// <para>
    /// ⚠ <b>The vocabulary filter runs FIRST, before every other refusal, and that ordering is
    /// deliberate.</b> §0.5 / D-3 requires the drop to be <i>recorded</i>, not merely to happen.
    /// When the filter ran last, a proposal refused earlier for an unrelated reason — the
    /// per-run cap spent, an uncited evidence product, a special-category label — swallowed its
    /// injected terms with no ledger line at all, and the console panel printed an empty ledger
    /// beside the words "an empty ledger is a RESULT, not a pass". An empty ledger produced by an
    /// earlier refusal is precisely the reading that panel warns against, so the refusal that
    /// D-3 is about is now always the one that gets written down. Ordering cannot make a
    /// proposal ACCEPTED that would otherwise have been refused: every check below still runs and
    /// still returns null.
    /// </para>
    /// </remarks>
    /// <param name="state">The run state.</param>
    /// <param name="proposal">The model's proposal.</param>
    /// <param name="vocabulary">The allowed vocabulary.</param>
    /// <param name="progress">Where the outcome is published.</param>
    /// <returns>The accepted interest, or null.</returns>
    public static Interest? TryAcceptProposal(
        DiscoveryState state,
        ProposedInterest proposal,
        QueryVocabulary vocabulary,
        IDiscoveryProgressSink progress)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(vocabulary);
        progress ??= NullDiscoveryProgressSink.Instance;

        // ── §0.5 / D-3, FIRST: whatever else refuses this proposal, the injected terms are
        //    recorded and printed. See the remarks for why the ordering is load-bearing.
        int before = state.DroppedQueryTerms.Count;
        var terms = vocabulary.Filter(proposal.QueryTerms, $"proposed interest \"{proposal.Label}\"", state.DroppedQueryTerms);

        for (int i = before; i < state.DroppedQueryTerms.Count; i++)
            progress.Publish(DiscoveryEvent.QueryTermDropped(state.DiscoveryRound, state.DroppedQueryTerms[i]));

        // Every exit below records a ProposalOutcome, accepted or refused. A proposal that leaves
        // no trace is a denominator nobody can see, and the D-3 ledger is meaningless without one.
        if (state.ReviewerInferredCount >= DiscoveryState.MaxReviewerInferredInterests)
        {
            return Refuse(state, proposal, terms, progress,
                $"the run's cap of {DiscoveryState.MaxReviewerInferredInterests} reviewer-inferred interests is spent");
        }

        if (state.Interests.Count >= DiscoveryState.MaxInterests)
        {
            return Refuse(state, proposal, terms, progress,
                $"the map is already at its cap of {DiscoveryState.MaxInterests}");
        }

        if (state.FindCandidate(proposal.EvidenceProductId) is null)
        {
            return Refuse(state, proposal, terms, progress,
                $"it cites {proposal.EvidenceProductId}, which is not a candidate this run retrieved. " +
                "A proposal must name the product whose review revealed it");
        }

        // D-6, outbound: an inferred LABEL that names a special category is refused whatever it
        // is about. The category flag blocks the channel a naive system uses; this blocks the one
        // the regulator cares about.
        var blockedTerms = SensitiveInferenceBlocklist.AllBlockedLabelTerms(proposal.Label);
        if (blockedTerms.Count > 0)
        {
            return Refuse(state, proposal, terms, progress,
                $"the label names special-category terms (\"{string.Join("\", \"", blockedTerms)}\")");
        }

        if (terms.Count == 0)
        {
            return Refuse(state, proposal, terms, progress,
                "every proposed query term is outside the vocabulary the interest map and the catalogue already " +
                "contain. Review text is an INPUT, never an instruction (§0.5 / D-3)");
        }

        var interest = new Interest
        {
            Id = $"I-{state.Interests.Count + 1}",
            Label = proposal.Label.Trim(),
            Kind = InterestKind.Latent,
            Origin = InterestOrigin.ReviewerInferred,
            // Clamped in CODE, regardless of what the model said. Topic drift is a compliance
            // smell as well as a UX one, so the cap is mechanical rather than requested.
            Confidence = Math.Clamp(proposal.Confidence, 0.0, DiscoveryState.ReviewerInferredConfidenceCeiling),
            EvidenceSignalIds = [],   // evidenced by a review, never by history
            Rationale = proposal.Rationale?.Trim() is { Length: > 0 } r
                ? r
                : $"Revealed by the review text of {proposal.EvidenceProductId}.",
            QueryTerms = terms,
            CategoryHints = [],
            AttributeHints = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        state.Interests.Add(interest);
        state.CoverageFor(interest.Id);
        state.ReviewerInferredCount++;
        state.Proposals.Add(new ProposalOutcome(
            proposal.Label.Trim(), proposal.EvidenceProductId, [.. proposal.QueryTerms], terms,
            Accepted: true, Refusal: null));

        // An accepted proposal OPENS A GAP for itself, immediately.
        //
        // This is what makes the mid-run discovery mechanism actually load-bearing rather than
        // decorative: the interest was added AFTER this round's query plan was built, so nothing
        // has searched for it, and without a gap the run would approve and exit one round before
        // the interest that justified the loop was ever explored. That failure is invisible on
        // screen — the ledger would simply show an UNEXPLORED row next to a "coverage sufficient"
        // verdict — which is exactly the flattering direction.
        state.OpenGaps.Add(new CoverageGap(
            interest.Id,
            "Proposed mid-round, after the query plan was built. Nothing has searched for it yet, and an " +
            "absence of evidence is not coverage.",
            terms[0],
            null,
            null));

        progress.Publish(DiscoveryEvent.InterestProposed(state.DiscoveryRound, interest, proposal.EvidenceProductId));
        return interest;
    }

    /// <summary>
    /// Records a refused proposal, announces it, and returns null.
    /// </summary>
    /// <remarks>
    /// One exit point for every refusal, so a new constraint cannot be added that refuses a
    /// proposal without leaving a row in <see cref="DiscoveryState.Proposals"/>. The row is the
    /// denominator the D-3 ledger is read against: without it, "nothing was dropped" and "nothing
    /// was ever proposed" are the same picture, and only one of them is a control that ran.
    /// </remarks>
    /// <param name="state">The run state.</param>
    /// <param name="proposal">The refused proposal.</param>
    /// <param name="keptTerms">Terms that survived the vocabulary constraint, possibly none.</param>
    /// <param name="progress">The sink.</param>
    /// <param name="reason">Why it was refused, phrased to follow "REFUSED — ".</param>
    private static Interest? Refuse(
        DiscoveryState state,
        ProposedInterest proposal,
        IReadOnlyList<string> keptTerms,
        IDiscoveryProgressSink progress,
        string reason)
    {
        state.Proposals.Add(new ProposalOutcome(
            proposal.Label.Trim(), proposal.EvidenceProductId, [.. proposal.QueryTerms], keptTerms,
            Accepted: false, Refusal: reason));

        progress.Publish(DiscoveryEvent.Degraded("CoverageReviewer",
            $"proposal \"{proposal.Label}\" REFUSED — {reason}"));

        return null;
    }

    /// <summary>Publishes the ledger panel and the verdict line for one round.</summary>
    /// <param name="state">The run state.</param>
    /// <param name="progress">The sink.</param>
    /// <param name="verdictLine">The line printed under the rows.</param>
    public static void PublishLedger(DiscoveryState state, IDiscoveryProgressSink progress, string verdictLine)
    {
        ArgumentNullException.ThrowIfNull(state);
        progress ??= NullDiscoveryProgressSink.Instance;

        var rows = new List<string>(state.Interests.Count);
        foreach (var interest in state.Interests)
            rows.Add(DiscoveryProjection.CoverageLine(interest, state.CoverageFor(interest.Id)));

        progress.Publish(DiscoveryEvent.CoverageLedger(state.DiscoveryRound, rows, verdictLine));
    }
}

/// <summary>
/// Writes the next query for an uncovered interest USING THE CATALOGUE'S OWN VOCABULARY.
/// </summary>
/// <remarks>
/// <para>
/// This is the deterministic stand-in for the reviewer's <c>next_query</c> / <c>next_category</c>
/// / <c>next_attributes</c> fields, and it is restricted to the same two inputs the prompt gives
/// the model: the candidates it ACTUALLY SAW, and the catalogue's public category names.
/// </para>
/// <para>
/// ⚠ <b>It is a BASELINE, not a simulation of the model.</b> Read the offline arm as "what the
/// loop's mechanics do with no model at all", never as "what the model would have done". The two
/// numbers are not interchangeable and the console says so.
/// </para>
/// </remarks>
public static class CoverageGapWriter
{
    /// <summary>
    /// Builds a runnable gap for one interest, or null when no materially different query exists.
    /// </summary>
    /// <param name="state">The run state.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="interest">The uncovered interest.</param>
    public static CoverageGap? Write(DiscoveryState state, Catalogue catalogue, Interest interest)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(interest);

        var coverage = state.CoverageFor(interest.Id);

        // ⚠ There is no materially different query for an interest that names nothing. Both
        //   repairs below narrow or re-word an existing query using the interest's own terms, and
        //   an interest with an EMPTY attribution vocabulary has none to narrow with — so writing
        //   a gap for it would send the loop round again to re-rank the same arbitrary list. NULL
        //   here is what makes the reviewer report GAPS_UNRESOLVABLE in round 1, which is the
        //   honest answer to "Hi — what do you recommend for me?" from a customer with one
        //   purchase: ask, do not guess.
        if (coverage.AttributionVocabularyEmpty)
        {
            coverage.LastGapReason =
                "This interest names nothing a product could be matched against — no attribute hint, no category "
              + "hint, and no content word in the customer's own words. A query with no content still returns a "
              + "ranked list, so 'something came back' is not evidence here.";
            return null;
        }

        var seenForInterest = new List<ProductCandidate>();

        foreach (var productId in coverage.CandidateProductIds)
            if (state.FindCandidate(productId) is { } candidate)
                seenForInterest.Add(candidate);

        return seenForInterest.Count > 0
            ? FromSeenCandidates(coverage, seenForInterest, interest)
            : FromCategoryNames(state, catalogue, coverage, interest);
    }

    /// <summary>
    /// The "right category, wrong items" repair: the query was too broad, so narrow it with the
    /// leaf path and an attribute pair that candidates actually in front of us demonstrate.
    /// </summary>
    private static CoverageGap? FromSeenCandidates(
        InterestCoverage coverage,
        IReadOnlyList<ProductCandidate> seen,
        Interest interest)
    {
        var modalLeaf = seen.GroupBy(c => c.CategoryPathText, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Count())
                            .ThenBy(g => g.Key, StringComparer.Ordinal)
                            .First();

        var leafName = modalLeaf.First().LeafCategory;

        // An attribute pair at least two SEEN candidates share. The prompt's own rule: use an
        // attribute a candidate you saw demonstrates exists on that category.
        var shared = modalLeaf
            .SelectMany(c => c.Attributes.Where(a => a.Contains('=', StringComparison.Ordinal)))
            .GroupBy(a => a, StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        IReadOnlyDictionary<string, string>? attributes = null;
        var querySuffix = string.Empty;

        if (shared is not null)
        {
            var parts = shared.Key.Split('=', 2);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                attributes = new Dictionary<string, string>(StringComparer.Ordinal) { [parts[0]] = parts[1] };
                querySuffix = " " + parts[1].Replace('-', ' ');
            }
        }

        var query = (leafName + querySuffix).Trim();
        if (coverage.QueriesRun.Contains(query, StringComparer.OrdinalIgnoreCase)) return null;

        return new CoverageGap(
            interest.Id,
            $"The right department came back but not enough of it: {seen.Count} candidate(s), best score " +
            string.Create(CultureInfo.InvariantCulture, $"{coverage.BestScore:0.0000}") +
            $". The QUERY was too broad — the candidates name \"{modalLeaf.Key}\", so use the catalogue's word for it, not the customer's.",
            query,
            modalLeaf.Key,
            attributes);
    }

    /// <summary>
    /// The "the words were wrong" repair: zero candidates, so re-ask using a category NAME the
    /// catalogue actually publishes. This is the vocabulary-transfer step in its purest form.
    /// </summary>
    private static CoverageGap? FromCategoryNames(
        DiscoveryState state,
        Catalogue catalogue,
        InterestCoverage coverage,
        Interest interest)
    {
        var wanted = new HashSet<string>(QueryVocabulary.Tokenize(interest.Label), StringComparer.Ordinal);
        if (wanted.Count == 0) return null;

        Category? best = null;
        int bestOverlap = 0;

        foreach (var category in catalogue.Categories)
        {
            if (category.AttributeSchema.Count == 0) continue;   // leaves only

            int overlap = 0;
            foreach (var element in category.Path)
                foreach (var token in QueryVocabulary.Tokenize(element))
                    if (wanted.Contains(token)) overlap++;

            if (overlap <= bestOverlap) continue;
            bestOverlap = overlap;
            best = category;
        }

        if (best is null || bestOverlap == 0)
        {
            coverage.LastGapReason =
                "zero candidates and no catalogue category shares a word with this interest — the CATALOGUE has " +
                "nothing here, which is a different failure from a bad query and is not fixable by searching again";
            return null;
        }

        var query = best.LeafName;
        if (coverage.QueriesRun.Contains(query, StringComparer.OrdinalIgnoreCase)) return null;

        return new CoverageGap(
            interest.Id,
            "Zero candidates — the WORDS were wrong, not the catalogue. The customer's phrasing matched nothing; " +
            $"the catalogue calls the nearest thing \"{string.Join(" > ", best.Path)}\".",
            query,
            string.Join(" > ", best.Path),
            null);
    }
}

/// <summary>
/// Proposes at most one new interest per round out of the review text on this round's candidates.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism a single retrieval pass structurally cannot have: an interest that did
/// not exist in the map until a verified-purchase review of a product retrieved for a DIFFERENT
/// interest revealed it.
/// </para>
/// <para>
/// ⚠ It is also the §0.5 / D-3 attack surface, and this class deliberately does NOT sanitise its
/// own output: it proposes terms straight out of the snippet, and
/// <see cref="CoverageVerdictProjection.TryAcceptProposal"/> filters them against
/// <see cref="QueryVocabulary"/>. Sanitising here as well would make the control unable to fire
/// on the offline path, and an arm that cannot fire is not a passing arm.
/// </para>
/// </remarks>
public static class ReviewSnippetInterestProposer
{
    /// <summary>How many terms a proposal carries.</summary>
    public const int MaxProposedTerms = 4;

    /// <summary>
    /// Builds a proposal from the round's observed review snippets, or null when nothing in them
    /// suggests a use the map does not already contain.
    /// </summary>
    /// <param name="state">The run state.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    public static ProposedInterest? Propose(DiscoveryState state, Catalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);

        if (state.ReviewerInferredCount >= DiscoveryState.MaxReviewerInferredInterests) return null;
        if (state.ObservedSignals.Count == 0) return null;

        // Everything the map already knows about. A "new" use has to be new relative to this.
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var interest in state.Interests)
        {
            foreach (var token in QueryVocabulary.Tokenize(interest.Label)) known.Add(token);
            foreach (var term in interest.QueryTerms)
                foreach (var token in QueryVocabulary.Tokenize(term)) known.Add(token);
        }

        ObservedSignal? bestSignal = null;
        List<string> bestNovel = [];

        foreach (var signal in state.ObservedSignals)
        {
            var novel = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var token in QueryVocabulary.Tokenize(signal.Snippet))
            {
                if (token.Length < 4) continue;
                if (known.Contains(token)) continue;
                if (QueryVocabulary.NeutralTokens.Contains(token)) continue;
                if (!seen.Add(token)) continue;
                novel.Add(token);
            }

            if (novel.Count <= bestNovel.Count) continue;
            bestNovel = novel;
            bestSignal = signal;
        }

        if (bestSignal is null || bestNovel.Count == 0) return null;

        var candidate = state.FindCandidate(bestSignal.ProductId);
        if (candidate is null) return null;

        // The label is composed from the product's own leaf category — a customer-facing string
        // must not be assembled out of words a stranger wrote.
        var label = $"{candidate.LeafCategory} — a use revealed by review text";

        return new ProposedInterest(
            label,
            0.55,
            candidate.ProductId,
            $"A verified-purchase review of {candidate.ProductId}, retrieved for {bestSignal.ForInterestId}, " +
            "names a use the interest map did not contain.",
            [.. bestNovel.Take(MaxProposedTerms)]);
    }
}
