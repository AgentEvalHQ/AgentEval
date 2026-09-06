// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// The OFFLINE arm of stage 1. Zero model calls: the map is the one
/// <see cref="InterestMapBuilder"/> derives, projected onto the loop's shape.
/// </summary>
/// <param name="catalogue">The catalogue façade.</param>
/// <param name="progress">Where the map panel goes.</param>
public sealed class DeterministicInterestMapper(Catalogue catalogue, IDiscoveryProgressSink progress)
    : IInterestMapperNode
{
    private readonly Catalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly IDiscoveryProgressSink _progress = progress ?? NullDiscoveryProgressSink.Instance;

    /// <inheritdoc />
    public ValueTask<DiscoveryState> MapAsync(DiscoveryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        DiscoveryInterestMapping.PopulateFromCode(state, _catalogue);
        PublishMap(state, _progress);

        return ValueTask.FromResult(state);
    }

    /// <summary>Publishes the interest-map panel: interests, anti-interests, constraints.</summary>
    /// <param name="state">The run state.</param>
    /// <param name="progress">The sink.</param>
    public static void PublishMap(DiscoveryState state, IDiscoveryProgressSink progress)
    {
        ArgumentNullException.ThrowIfNull(state);
        progress ??= NullDiscoveryProgressSink.Instance;

        var lines = new List<string>(state.Interests.Count + 2);
        foreach (var interest in state.Interests) lines.Add(DiscoveryProjection.InterestLine(interest));

        foreach (var anti in state.AntiInterests)
            lines.Add($"ANTI   —      —     {anti.Label}  ← {string.Join(", ", anti.EvidenceSignalIds)} \"{anti.Reason}\"");

        if (state.Constraints.Count > 0)
            lines.Add("CONSTRAINT   " + string.Join(" · ",
                state.Constraints.Select(c => $"{c.Kind}:{c.Value} ← {c.SourceSignalId}")));

        if (lines.Count == 0)
            lines.Add("(no interest could be derived — nothing to search for, and the loop will say so)");

        progress.Publish(DiscoveryEvent.InterestMap("InterestMapper", lines));
    }
}

/// <summary>
/// The OFFLINE arm of stage 3. The deterministic pre-gate is identical to the live path's; what
/// changes is who writes the verdict when the pre-gate does not fire.
/// </summary>
/// <remarks>
/// ⚠ This reviewer is a BASELINE, not a stand-in for the model. It reads only what the model
/// prompt promises the model — the ledger, the candidates it saw, and the catalogue's public
/// category names — but it applies fixed rules rather than judgement, so "the loop covered five
/// of five interests offline" is a statement about the loop's mechanics and not about the model.
/// </remarks>
/// <param name="catalogue">The catalogue façade.</param>
/// <param name="progress">Where the ledger and the pre-gate line go.</param>
public sealed class DeterministicCoverageReviewer(Catalogue catalogue, IDiscoveryProgressSink progress)
    : ICoverageReviewerNode
{
    private readonly Catalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly IDiscoveryProgressSink _progress = progress ?? NullDiscoveryProgressSink.Instance;

    /// <inheritdoc />
    public ValueTask<DiscoveryState> ReviewAsync(DiscoveryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        if (CoverageReviewGate.TryRejectCheaply(state, _catalogue, _progress)) return ValueTask.FromResult(state);

        var verdict = BuildVerdict(state, _catalogue);
        CoverageVerdictProjection.Project(state, verdict, _catalogue, _progress);
        CoverageVerdictProjection.PublishLedger(state, _progress, VerdictLine(state, verdict));

        return ValueTask.FromResult(state);
    }

    /// <summary>
    /// The deterministic verdict: mechanically-covered interests are covered, the rest get a gap
    /// written in the catalogue's own vocabulary, and one new interest may be proposed from
    /// review text.
    /// </summary>
    /// <param name="state">The run state.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    public static CoverageVerdict BuildVerdict(DiscoveryState state, Catalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);

        var covered = new List<string>();
        var gaps = new List<CoverageGap>();
        bool anyUnfixable = false;

        foreach (var interest in state.Interests)
        {
            var coverage = state.CoverageFor(interest.Id);

            // An interest NOBODY HAS SEARCHED YET is a gap, not a pass. This is the case the
            // reviewer creates for itself when it adds an interest mid-round, and treating it as
            // "nothing to report" would let a run approve a map it never actually explored —
            // silently, and in the flattering direction.
            if (coverage.QueriesRun.Count == 0)
            {
                if (interest.QueryTerms.Count == 0) { anyUnfixable = true; continue; }

                gaps.Add(new CoverageGap(
                    interest.Id,
                    "Never searched. This interest was added after the round's query plan was built, so no query " +
                    "has run for it yet — that is an absence of evidence, not evidence of coverage.",
                    interest.QueryTerms[0],
                    interest.CategoryHints.Count > 0 ? interest.CategoryHints[0] : null,
                    null));
                continue;
            }

            if (CatalogueDiscoverySearch.ClassifyCoverage(coverage) == CoverageStatus.Covered)
            {
                covered.Add(interest.Id);
                continue;
            }

            var gap = CoverageGapWriter.Write(state, catalogue, interest);
            if (gap is null) { anyUnfixable = true; continue; }
            gaps.Add(gap);
        }

        var proposal = ReviewSnippetInterestProposer.Propose(state, catalogue);

        var stopReason = gaps.Count > 0
            ? CoverageVerdict.GapsRemain
            : anyUnfixable ? CoverageVerdict.GapsUnresolvable : CoverageVerdict.CoverageSufficient;

        var assessment = string.Create(CultureInfo.InvariantCulture,
                $"{covered.Count} of {state.Interests.Count} interest(s) covered; {gaps.Count} gap(s) with a concrete next query")
            + (anyUnfixable ? "; at least one gap has no materially different query left" : string.Empty)
            + (proposal is null ? "." : "; one new interest proposed from review text.");

        return new CoverageVerdict(covered, gaps, proposal, stopReason, assessment);
    }

    /// <summary>
    /// The one-line verdict printed under the ledger rows.
    /// </summary>
    /// <remarks>
    /// It prints the reviewer's CLAIM and the code's RESOLUTION side by side, because they can
    /// legitimately differ: a claimed <c>COVERAGE_SUFFICIENT</c> is vetoed whenever an interest is
    /// starved or has never been searched. Printing only the claim would hide the veto, and a
    /// guardrail whose firing is invisible is indistinguishable from one that never fires.
    /// </remarks>
    /// <param name="state">The run state.</param>
    /// <param name="verdict">The verdict.</param>
    public static string VerdictLine(DiscoveryState state, CoverageVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(verdict);

        return $"{state.OpenGaps.Count} runnable gap(s) · {state.ReviewerInferredCount} reviewer-inferred interest(s) · " +
               $"reviewer says {verdict.StopReason} → resolved {state.ResolveStopReason()} · {verdict.Assessment}";
    }
}

/// <summary>
/// The cheap deterministic gate that runs before ANY reviewer — model or not — spends a token.
/// </summary>
/// <remarks>
/// It can reject for free. It can never approve for free. That asymmetry is the design's answer
/// to the most dangerous failure available here: a reviewer that rubber-stamps round 1 makes the
/// loop look identical to a single pass, so the eval reads "architecture doesn't help" when in
/// fact the CHECKER is broken — and it fails in the flattering direction, as a clean cheap run.
/// </remarks>
public static class CoverageReviewGate
{
    /// <summary>
    /// Raises deterministic gaps for structurally starved DIRECT interests and returns true when
    /// it did — in which case NO model call is made this round.
    /// </summary>
    /// <param name="state">The run state.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="progress">The sink.</param>
    public static bool TryRejectCheaply(DiscoveryState state, Catalogue catalogue, IDiscoveryProgressSink progress)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);
        progress ??= NullDiscoveryProgressSink.Instance;

        state.PreGateFiredThisRound = false;

        var starved = CoverageVerdictProjection.Starved(state);
        if (starved.Count == 0) return false;

        state.PreGateFiredThisRound = true;

        var lines = new List<string>(starved.Count);
        var gaps = new List<CoverageGap>(starved.Count);

        foreach (var interest in starved)
        {
            // ⚠ THE LINE NAMES THE REASON THIS INTEREST IS STARVED, not the only reason there used
            //   to be. Since the coverage gate stopped keying on the retriever's ranking there are
            //   two, and they have two different answers: an interest that NAMES NOTHING is starved
            //   with candidates well above the floor, and printing "no candidate above the score
            //   floor (0.0120)" beside two candidates at 0.5 sends the reader to the threshold —
            //   which is exactly the fix that must not be made.
            var starvedCoverage = state.CoverageFor(interest.Id);
            lines.Add(starvedCoverage.AttributionVocabularyEmpty
                ? $"{interest.Id} \"{interest.Label}\" NAMES NOTHING a product could be matched against — no attribute "
                + "hint, no category hint, no content word. "
                + string.Create(CultureInfo.InvariantCulture,
                      $"{starvedCoverage.CandidateProductIds.Count} candidate(s) came back at best score {starvedCoverage.BestScore:0.0000}; ")
                + "a query with no content still returns a ranked list, so that is not evidence. NOT a threshold problem."
                : $"{interest.Id} \"{interest.Label}\" has no candidate above the score floor " +
                  string.Create(CultureInfo.InvariantCulture, $"({DiscoveryState.MinCandidateScore:0.0000})"));

            var gap = CoverageGapWriter.Write(state, catalogue, interest);
            if (gap is not null) gaps.Add(gap);
        }

        progress.Publish(DiscoveryEvent.PreGate(state.DiscoveryRound, lines));

        var verdict = CoverageVerdict.Conservative(gaps,
            $"zero-candidate DIRECT interest — no model call made this round ({starved.Count} starved)");

        CoverageVerdictProjection.Project(state, verdict, catalogue, progress);
        CoverageVerdictProjection.PublishLedger(state, progress,
            $"{state.OpenGaps.Count} runnable gap(s) · pre-model gate fired · stop_reason = {verdict.StopReason}");

        return true;
    }
}

/// <summary>
/// The OFFLINE arm of stage 4. Selects from the candidate set by
/// <c>interest confidence × retrieval score</c> — two independent pieces of evidence multiplied,
/// not one of them ignored.
/// </summary>
/// <param name="catalogue">The catalogue façade.</param>
/// <param name="progress">Where the post-check lines go.</param>
public sealed class DeterministicRanker(Catalogue catalogue, IDiscoveryProgressSink progress) : IRankerNode
{
    private readonly Catalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly IDiscoveryProgressSink _progress = progress ?? NullDiscoveryProgressSink.Instance;

    /// <summary>How many products one interest may contribute, so a single interest cannot fill the tray.</summary>
    public const int MaxPerInterest = 3;

    /// <inheritdoc />
    public ValueTask<DiscoveryState> RankAsync(DiscoveryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        state.Ranked.Clear();
        state.Ranked.AddRange(Select(state, _catalogue));
        state.SelectionWasDeterministic = true;

        var lines = DiscoveryPostChecks.Apply(state, _catalogue, _progress);
        _progress.Publish(DiscoveryEvent.Ranked(lines));

        return ValueTask.FromResult(state);
    }

    /// <summary>The deterministic selection, before the post-checks run.</summary>
    /// <param name="state">The run state.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    public static IReadOnlyList<RankedRecommendation> Select(DiscoveryState state, Catalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);

        var perInterest = new Dictionary<string, int>(StringComparer.Ordinal);
        var ranked = new List<RankedRecommendation>();

        var ordered = state.Candidates
            .Select(candidate => (Candidate: candidate, Interest: state.FindInterest(candidate.MatchedInterestId)))
            .Where(pair => pair.Interest is not null)
            .OrderByDescending(pair => pair.Interest!.Confidence * pair.Candidate.SearchScore)
            .ThenBy(pair => pair.Candidate.ProductId, StringComparer.Ordinal);

        foreach (var (candidate, interest) in ordered)
        {
            if (ranked.Count >= DiscoveryState.MaxRankedRecommendations) break;

            perInterest.TryGetValue(interest!.Id, out int taken);
            if (taken >= MaxPerInterest) continue;

            if (!catalogue.TryGet(candidate.ProductId, out var product) || product is null) continue;

            var groundingKey = GroundingKey(product);
            if (groundingKey is null) continue;   // nothing citable ⇒ nothing presentable

            // Incremented only once a slot is actually TAKEN: charging the quota for a candidate
            // that was skipped would silently shrink what an interest may contribute.
            perInterest[interest.Id] = taken + 1;

            ranked.Add(new RankedRecommendation(
                ranked.Count + 1,
                candidate.ProductId,
                interest.Id,
                $"Retrieved for the derived interest \"{interest.Label}\". {interest.Rationale} " +
                $"Selected by the deterministic arm, with no model call.",
                [groundingKey],
                candidate.ReviewIds.Count > 0 ? [candidate.ReviewIds[0]] : [],
                Confidence(interest, candidate)));
        }

        return ranked;
    }

    /// <summary>
    /// The routing number, and what it is NOT.
    /// </summary>
    /// <remarks>
    /// It is not a model self-report and it is not a probability. It is the mean of the interest's
    /// derived strength and the candidate's rank position within its own interest — both code-owned,
    /// both UNCALIBRATED against outcomes. It routes between two trays and makes no other claim.
    /// </remarks>
    /// <param name="interest">The interest the candidate is credited to.</param>
    /// <param name="candidate">The candidate.</param>
    public static double Confidence(Interest interest, ProductCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(interest);
        ArgumentNullException.ThrowIfNull(candidate);

        // RRF scores are small positive numbers with no upper bound of interest; squashing keeps
        // the second operand inside 0..1 without pretending it is a probability.
        double retrieval = candidate.SearchScore <= 0
            ? 0.0
            : candidate.SearchScore / (candidate.SearchScore + DiscoveryState.MinCandidateScore);

        return Math.Clamp((interest.Confidence + retrieval) / 2.0, 0.0, 1.0);
    }

    /// <summary>
    /// The first authored spec key on a product — a key <see cref="Product.TryGetAttributeValue"/>
    /// resolves, so the two-sided evidence check has a product side that is a catalogue fact.
    /// </summary>
    /// <param name="product">The catalogue record.</param>
    public static string? GroundingKey(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        foreach (var (key, _) in product.Specs) return key;
        return product.Tags.Count > 0 ? product.Tags[0] : null;
    }
}

/// <summary>
/// The OFFLINE arm of stage 5. Runs the SHIPPED guardrail pipeline over the loop's selection and
/// prints the same panels Demo 1 prints, so the two demos are visibly one system.
/// </summary>
/// <param name="catalogue">The catalogue façade.</param>
/// <param name="progress">Where the live price/stock line goes.</param>
/// <param name="print">
/// False screens and composes the answer exactly as usual but writes no customer-facing panels.
/// Used by the termination probes, which run the loop six times and care about the STATE the
/// Presenter produces, not about six copies of the same tray on the console.
/// </param>
public sealed class DeterministicPresenter(Catalogue catalogue, IDiscoveryProgressSink progress, bool print = true)
    : IPresenterNode
{
    private readonly Catalogue _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
    private readonly IDiscoveryProgressSink _progress = progress ?? NullDiscoveryProgressSink.Instance;
    private readonly bool _print = print;

    /// <inheritdoc />
    public ValueTask<DiscoveryState> PresentAsync(DiscoveryState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        DiscoveryPresentation.Render(state, _catalogue, _progress, modelProse: null, print: _print);
        return ValueTask.FromResult(state);
    }
}

/// <summary>
/// Turns the loop's selection into the shipped <see cref="RecommendationSet"/>, screens it with
/// the shipped <see cref="GuardrailPipeline"/>, and prints it with the shipped
/// <see cref="RecommendationPrinter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reuse is not laziness here: the loop's answer is measured against exactly the bar Demo 1's
/// answer is measured against, so the comparison the whole demo rests on is not confounded by two
/// different renderers and two different guardrail suites.
/// </para>
/// <para>
/// <b>Price and stock are read HERE</b>, at render time, by <see cref="PriceStockRefresher"/>
/// inside the pipeline — never from model context. Nothing upstream of this method has ever been
/// given a price.
/// </para>
/// </remarks>
public static class DiscoveryPresentation
{
    /// <summary>Screens, prints, and writes <see cref="DiscoveryState.FinalAnswer"/>.</summary>
    /// <param name="state">The run state.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="progress">The sink.</param>
    /// <param name="modelProse">The Presenter model's prose, or null on the offline path.</param>
    /// <param name="print">False screens and composes without writing the customer-facing panels.</param>
    public static GuardrailOutcome Render(
        DiscoveryState state,
        Catalogue catalogue,
        IDiscoveryProgressSink progress,
        string? modelProse,
        bool print = true)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);
        progress ??= NullDiscoveryProgressSink.Instance;

        var profile = UserProfiles.Find(state.CustomerId);
        var user = profile is null
            ? new User(state.CustomerId, state.CustomerId, state.Language, state.Market, state.PersonalizationConsent, Personas.DemoToday)
            : profile.User with { PersonalizationEnabled = state.PersonalizationConsent };

        var classified = profile is not null && state.PersonalizationConsent
            ? PurchaseIntentClassifier.ClassifyAll(profile.Purchases, catalogue.BySku, Personas.DemoToday)
            : [];

        var domainMap = DiscoveryProjection.ToDomainInterestMap(state);

        var context = GuardrailContext.Create(
            catalogue.BySku,
            user,
            domainMap,
            classified,
            categories: catalogue.Categories,
            customerUtterance: state.SessionRequest,
            asOf: Personas.DemoToday);

        var raw = BuildSet(state, catalogue, domainMap);

        progress.Publish(DiscoveryEvent.Presented(
            $"live price and stock read for {raw.PresentedCount} SKU(s) at render time — never from model context"));

        // Apply, NOT ApplyWithAbstentionGate: the abstention gate is Demo 1's PRE-SEARCH control
        // and it cannot fire on a turn that has already retrieved. Running it here would add an
        // arm with a chance floor of 1.0, and a gate that cannot fire is not a passing gate.
        var outcome = GuardrailPipeline.Apply(raw, context);

        outcome.Ledger.Note(GuardrailStage.AbstentionGate, GuardrailReasons.ArmInapplicable, "—",
            "the §F.8 abstention gate is a PRE-SEARCH control and does not apply to a loop that has already " +
            "retrieved. The loop's equivalent is its stop reason, printed in the run summary");

        // The arms below can only fail on a claim a MODEL wrote. Which claims those are depends on
        // whether the RANKER produced the selection — not on whether the Presenter produced prose.
        // Conflating the two would make a live run whose prose call timed out report "no model ran".
        if (state.SelectionWasDeterministic)
        {
            outcome.Ledger.Note(GuardrailStage.EvidenceRequired, GuardrailReasons.ArmInapplicable,
                state.ModelCalls == 0 ? "offline arm" : "ranker fell back",
                (state.ModelCalls == 0
                    ? "no model ran on this turn: "
                    : "the Ranker fell back to the deterministic selection, so ") +
                "the citations and reason strings below were composed from the catalogue by this file, and the " +
                "evidence, grounding, sensitive_prose and stated_price arms cannot fail on this path. This panel " +
                "measures the loop's MECHANICS, not the agent");
        }

        // ── What the customer was SHOWN, recorded by the node that showed it. ───────
        //
        // Recorded BEFORE the printing branch on purpose: the screened set is a fact about the
        // run, not about whether anyone was watching. Recording it inside `if (print)` would make
        // a headless caller — the eval lane's loop arm, the termination probes — see an empty
        // answer and read it as an abstention.
        state.Presented.Clear();
        foreach (var item in outcome.Cleaned.AllPresented)
        {
            state.Presented.Add(new PresentedItem(
                item.ProductId,
                item.WhyThis,
                item.Evidence.Citation.ToString(),
                OutOfStock: outcome.VerifiedPrices.TryGetValue(item.ProductId, out var price) && price.StockUnits == 0,
                item.Confidence));
        }

        if (print)
        {
            RecommendationPrinter.PrintAnswer(
                user, domainMap, classified, outcome,
                RecommendationPrinter.OmitToolCalls, RecommendationPrinter.OmitToolCalls);

            PrintShortfall(state);
        }

        state.FinalAnswer = modelProse is { Length: > 0 }
            ? modelProse
            : ComposeAnswer(state, catalogue, outcome);

        return outcome;
    }

    /// <summary>
    /// Projects the loop's ranked selection onto the shipped answer shape.
    /// </summary>
    /// <remarks>
    /// The product side of each evidence record is resolved FROM THE CATALOGUE for the key the
    /// Ranker cited. A key the catalogue does not carry is passed through unrepaired, so the
    /// pipeline drops the item with <c>attribute_not_found</c> — repairing it would be repairing
    /// the artifact under test.
    /// </remarks>
    /// <param name="state">The run state.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="domainMap">The projected interest map the evidence must cite.</param>
    public static RecommendationSet BuildSet(DiscoveryState state, Catalogue catalogue, InterestMap domainMap)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(domainMap);

        var items = new List<RecommendationDto>(state.Ranked.Count);

        foreach (var ranked in state.Ranked)
        {
            var interest = state.FindInterest(ranked.InterestId);
            if (interest is null) continue;

            var signal = domainMap.FindSignal(interest.Label);
            IReadOnlyList<string> citedPurchaseIds =
                signal is null || interest.EvidenceSignalIds.Count == 0 ? [] : interest.EvidenceSignalIds;

            var key = ranked.GroundingAttributeKeys.Count > 0 ? ranked.GroundingAttributeKeys[0] : string.Empty;
            var value = key;

            if (catalogue.TryGet(ranked.ProductId, out var product) && product is not null &&
                product.TryGetAttributeValue(key, out var catalogueValue) && catalogueValue is not null)
            {
                value = catalogueValue;
            }

            var reviewId = ranked.GroundingReviewIds.Count > 0 ? ranked.GroundingReviewIds[0] : null;

            items.Add(new RecommendationDto(
                ranked.ProductId,
                ranked.WhyThis,
                new EvidenceDto(interest.Label, citedPurchaseIds, key, value, reviewId),
                ranked.Confidence));
        }

        return RecommendationSet.Empty with
        {
            InterestMap = [.. domainMap.Signals.Select(InterestSignalDto.From)],
            Recommendations = items
        };
    }

    /// <summary>
    /// The shortfall section. On the exhaustion path the loop degrades to a PARTIAL answer: it
    /// says what it did not cover, why, and hands it to a human.
    /// </summary>
    /// <param name="state">The run state.</param>
    public static void PrintShortfall(DiscoveryState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var uncovered = state.UncoveredInterests();
        if (!state.IsPartialAnswer && uncovered.Count == 0) return;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  ⚠  Not covered in this session");

        foreach (var interest in uncovered)
        {
            var coverage = state.CoverageFor(interest.Id);
            Console.WriteLine($"     • {interest.Id}  {interest.Label} — searched {coverage.QueriesRun.Count} time(s), " +
                              $"{coverage.CandidateProductIds.Count} candidate(s) credited, " +
                              $"{coverage.AttributableProductIds.Count} of them carrying anything this interest names"
                            + (coverage.AttributionVocabularyEmpty
                                ? " (⚠ and this interest names NOTHING a product could be matched against)"
                                : ""));
            if (coverage.LastGapReason is { Length: > 0 } reason)
                Console.WriteLine($"       {reason}");
        }

        Console.WriteLine($"     Stop reason: {state.StopReason}. Handing this to a human: [ask the community] [advisor chat]");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>Composes the deterministic customer-facing answer text.</summary>
    /// <param name="state">The run state.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="outcome">The screened answer.</param>
    public static string ComposeAnswer(DiscoveryState state, Catalogue catalogue, GuardrailOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(outcome);

        var builder = new StringBuilder();

        foreach (var interest in state.Interests)
        {
            var forInterest = outcome.Cleaned.AllPresented
                .Where(item => state.Ranked.Any(r =>
                    string.Equals(r.ProductId, item.ProductId, StringComparison.Ordinal) &&
                    string.Equals(r.InterestId, interest.Id, StringComparison.Ordinal)))
                .ToList();

            if (forInterest.Count == 0) continue;

            builder.Append(interest.Kind == InterestKind.Latent
                ? string.Create(CultureInfo.InvariantCulture,
                    $"You didn't ask for these — here's why we think they fit  ⟨inferred, {interest.Confidence:0.00}⟩ · {interest.Label}")
                : $"Because of {interest.Label}");
            builder.AppendLine();

            foreach (var item in forInterest)
            {
                var name = catalogue.TryGet(item.ProductId, out var product) && product is not null
                    ? product.Name
                    : item.ProductId;
                builder.AppendLine($"  · {name} ({item.ProductId}) — {item.WhyThis}");
            }

            builder.AppendLine();
        }

        // ⚠ THE FOOTNOTE IS A FOOTNOTE TO A TRAY, and with no tray there is nothing for it to be a
        //   footnote to. When the loop presents nothing, the customer-facing account of that is the
        //   shortfall section — the interest, why it could not be served, and a handover to a human
        //   — not a bare list of rejected SKUs. Emitting the list alone would turn an abstention
        //   into a several-hundred-character answer, which is the shape plan item 8.18 exists to
        //   remove: a customer who named nothing must be shown nothing, and "shown nothing" has to
        //   be measurable as a zero-length answer rather than as a shorter one.
        if (state.DroppedSkus.Count > 0 && builder.Length > 0)
        {
            builder.AppendLine("Deliberately not shown");
            foreach (var dropped in state.DroppedSkus)
                builder.AppendLine($"  · {dropped.ProductId} — {dropped.Reason}");
        }

        return builder.ToString();
    }
}
