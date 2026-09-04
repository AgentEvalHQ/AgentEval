// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.ClientModel;

using Azure;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;
using Galaxus.RecommendationAgent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Demos;

/// <summary>
/// Demo 01 — Robin, the single all-in-one recommendation agent (design §E.3).
/// </summary>
/// <remarks>
/// <para>
/// The turn runs in four clearly separated phases, and the separation is the demo:
/// </para>
/// <list type="number">
///   <item><b>CODE derives the interest map.</b> <see cref="PurchaseIntentClassifier"/> and
///         <see cref="InterestMapBuilder"/> run before the model is constructed. Gift
///         suppression, the replenishment lane and the abstention gate are decided here,
///         deterministically, at zero token cost.</item>
///   <item><b>The MODEL searches and presents.</b> Its only sanctioned recommendation
///         channel is the <c>PresentRecommendation</c> tool call (design §0.5 / D-1) — a
///         product named only in prose is not shown and does not count.</item>
///   <item><b>CODE screens what it presented.</b> <see cref="GuardrailPipeline"/> verifies
///         every tool call against the catalogue and writes each drop to the ledger.</item>
///   <item><b>The ledger is printed.</b> Every §F mechanism, counted and named on screen.</item>
/// </list>
/// <para>
/// ⚠ <b>Two things this file deliberately does NOT do.</b> It does not parse the model's
/// final text for recommendations — §E.1's "return only this JSON object" contract is
/// deleted, and re-introducing a prose parser here would resurrect defect D-1. And it does
/// not repair a bad tool call before screening it: a repaired argument is a defect that can
/// never fire, which is a failure in the flattering direction.
/// </para>
/// <para>
/// ⏱️ Runtime: ~20–60 seconds against a live deployment (several model + tool round-trips).
/// <c>--offline</c> runs the deterministic half in well under a second and costs nothing.
/// </para>
/// </remarks>
public static class Demo01_RecommendationAgent
{
    /// <summary>The persona the demo opens on when <c>--user</c> is not given.</summary>
    public const string DefaultUserId = GalaxusDemoPrompts.NadiaUserId;

    /// <summary>The per-run tool-call cap opened around <c>RunAsync</c> (§F.9).</summary>
    public const int ToolCallCap = ToolCallBudget.DefaultMaxCalls;

    /// <summary>
    /// Minimum attribution score before a presented product is credited to a derived interest
    /// signal. Below it the recommendation carries NO user side and the pipeline drops it with
    /// <c>unknown_signal_label</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UNCALIBRATED, like every other threshold in this sample. It is set low on purpose: the
    /// failure it must catch is "the model presented something no derived interest explains",
    /// not "the model phrased its search differently from the label".
    /// </para>
    /// <para>
    /// <b>What it was observed to do</b>, over twelve product/persona pairs in the offline concept
    /// space. It rejects cleanly at the far end: the gaming headset scores below 0.20 against every
    /// signal of all three espresso/hiking personas, including Marco's — which is the product the
    /// gift trap must never surface. It attributes correctly in the middle: Marco's scale → "owns
    /// espresso machines but no espresso scale" (0.69), Nadia's ND filter → the 0.86 conjunction
    /// (0.53). And it is loose at the bottom: espresso cleaning tablets attributed to Nadia's
    /// "Trekking packs" at 0.26. That last one is caught by the SECOND filter rather than this one
    /// — its confidence lands at 0.39, under <see cref="ConfidenceBands.SecondaryThreshold"/>, and
    /// it is dropped. Two loose filters in series, both stated; neither is a measured probability.
    /// </para>
    /// </remarks>
    public const double AttributionFloor = 0.20;

    /// <summary>
    /// A consumable enters the replenishment tray once this fraction of its typical cadence has
    /// elapsed. 0.80 means "inside the last fifth of the cycle, or already overdue".
    /// </summary>
    public const double ReplenishmentDueFraction = 0.80;

    /// <summary>How many products the offline baseline arm presents per interest signal.</summary>
    private const int OfflineCandidatesPerSignal = 2;

    /// <summary>How many interest signals the offline baseline arm walks, strongest first.</summary>
    private const int OfflineSignalsUsed = 3;

    /// <summary>Runs the demo with every default: Nadia, personalization on, live model.</summary>
    public static Task RunAsync() => RunAsync(DefaultUserId, personalizationDisabled: false, offline: false);

    /// <summary>
    /// Runs one customer turn end to end.
    /// </summary>
    /// <param name="userId">One of <see cref="Personas.AllPersonaIds"/>. Null selects <see cref="DefaultUserId"/>.</param>
    /// <param name="personalizationDisabled">
    /// The <c>--no-personalization</c> toggle (§F.6). Flips <see cref="User.PersonalizationEnabled"/>
    /// to false for this run: <c>GetPurchaseHistory</c> and <c>GetInterestMap</c> then return a typed
    /// refusal, and the turn runs on the customer's stated need alone.
    /// </param>
    /// <param name="offline">
    /// The <c>--offline</c> toggle. Skips the model entirely and lets the deterministic retrieval +
    /// guardrail path select the products. This is the baseline arm, printed as such — it is what the
    /// system produces with ZERO model calls, and it exists because a claim about what the agent adds
    /// is worthless without it (design §0.5 / D-4).
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task RunAsync(
        string? userId,
        bool personalizationDisabled,
        bool offline,
        CancellationToken cancellationToken = default)
    {
        PrintHeader();

        // ── Resolve the persona ───────────────────────────────────────────────
        var id = string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId.Trim();
        var seeded = UserProfiles.Find(id);
        if (seeded is null)
        {
            PrintUnknownPersona(id);
            return;
        }

        var catalogue = Catalogue.Default;
        var profile   = seeded.WithPersonalization(!personalizationDisabled);
        var prompt    = Personas.CanonicalPromptFor(profile.Id);

        // The tools read the customer through UserProfiles unless an override is registered.
        // The seed itself is never mutated, so an opted-in and an opted-out run can happen in
        // one process without one quietly rewriting the other's ground truth.
        GalaxusTools.ClearProfileOverrides();
        if (personalizationDisabled) GalaxusTools.OverrideProfile(profile);

        // ── Phase 1: CODE derives everything the model is not allowed to decide ──
        var classified = profile.User.PersonalizationEnabled
            ? PurchaseIntentClassifier.ClassifyAll(profile.Purchases, catalogue.BySku, Personas.DemoToday)
            : [];

        // Same call the GetInterestMap tool makes, argument for argument. If these two ever
        // drift, the model is shown one map and graded against another — and the evidence
        // check would start failing for a reason that has nothing to do with the model.
        // Under the opt-out the builder does not read history at all (§F.6); the customer's
        // own sentence is passed as the only stated need, which is what keeps the turn useful
        // instead of collapsing it into an abstention.
        var map = InterestMapBuilder.Build(
            profile.User,
            profile.Purchases,
            catalogue.BySku,
            statedNeeds: profile.User.PersonalizationEnabled ? null : [prompt],
            asOf: Personas.DemoToday,
            sensitiveCategoryNames: catalogue.SensitiveCategories);

        var context = GuardrailContext.Create(
            catalogue.BySku,
            profile.User,
            map,
            classified,
            categories: catalogue.Categories,
            customerUtterance: prompt,
            asOf: Personas.DemoToday);

        var replenishment = BuildReplenishmentLane(map, classified, catalogue);

        PrintRequest(profile, prompt, personalizationDisabled);

        // ── Retrieval seam ────────────────────────────────────────────────────
        var retriever = await BuildRetrieverAsync(catalogue, cancellationToken).ConfigureAwait(false);
        GalaxusTools.Bind(retriever);
        GalaxusTools.AssertBound();
        PrintRetrievalBanner(retriever);

        // ── Phase 2: the model presents (or the offline arm stands in for it) ──
        IReadOnlyList<PresentedRecommendation> presented;
        IReadOnlyDictionary<string, IReadOnlyList<string>> provenance;
        int toolCallsUsed;
        string? budgetSummary = null;
        string? robinSaid = null;

        if (offline)
        {
            PrintOfflineBanner();
            (presented, provenance) = await RunOfflineBaselineAsync(map, context, retriever, catalogue, cancellationToken)
                .ConfigureAwait(false);
            toolCallsUsed = RecommendationPrinter.OmitToolCalls;
        }
        else
        {
            if (!Config.IsConfigured)
            {
                PrintMissingCredentials();
                return;
            }

            Config.PrintAzureTarget();
            Console.WriteLine();

            var run = await RunAgentAsync(profile, prompt, cancellationToken).ConfigureAwait(false);
            if (run is null) return;   // the failure was already printed in full

            presented     = run.Presented;
            provenance    = run.Provenance;
            toolCallsUsed = run.ToolCallsUsed;
            budgetSummary = run.BudgetSummary;
            robinSaid     = run.Text;
        }

        // ── Phase 3: CODE screens what was presented ──────────────────────────
        var (raw, preLedgerDrops) = Assemble(presented, provenance, map, catalogue, replenishment);

        var outcome = GuardrailPipeline.ApplyWithAbstentionGate(raw, context);
        var ledger  = outcome.Ledger;

        // Drops decided before the pipeline (duplicate presentations) are replayed into the
        // one ledger the panel prints, so a single number tells the whole story.
        foreach (var drop in preLedgerDrops) ledger.Drop(drop.Stage, drop.Reason, drop.Subject, drop.Detail);

        // The denominator is what the MODEL presented, not what survived assembly. Anything
        // else would quietly shrink the denominator every time a drop happened, which is the
        // diluted-denominator failure this project keeps a rule about.
        ledger.RecordInput(presented.Count);
        ledger.GiftExcluded  = map.ExcludedBecauseGift.Count;
        ledger.ToolCallsUsed = Math.Max(0, toolCallsUsed);
        ledger.ToolCallCap   = toolCallsUsed >= 0 ? ToolCallCap : 0;

        NoteDerivedUserSide(ledger, offline);

        // ── Phase 4: print ────────────────────────────────────────────────────
        Console.WriteLine();
        RecommendationPrinter.PrintAnswer(profile.User, map, classified, outcome, toolCallsUsed,
            toolCallsUsed >= 0 ? ToolCallCap : RecommendationPrinter.OmitToolCalls);

        PrintPresentationAudit(presented, outcome.Cleaned);
        if (budgetSummary is not null) PrintBudgetNote(budgetSummary);
        if (robinSaid is not null) PrintRobinsProse(robinSaid);
    }

    // ── The agent run ─────────────────────────────────────────────────────────

    /// <summary>What one live agent run produced. Null from <see cref="RunAgentAsync"/> means it failed and said so.</summary>
    private sealed record AgentRun(
        IReadOnlyList<PresentedRecommendation> Presented,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Provenance,
        int ToolCallsUsed,
        string BudgetSummary,
        string? Text);

    /// <summary>
    /// The session header sent ahead of the customer's own words.
    /// </summary>
    /// <remarks>
    /// ⚠ Without this the agent does not know WHO it is serving. Observed on the first live run:
    /// the model guessed <c>GetUserProfile("current")</c>, the tool refused (correctly — there is
    /// no such customer, and a silent fallback to a real one would produce a plausible, wrong
    /// demo), and the turn ended after a single call. The customer's utterance is kept BYTE
    /// IDENTICAL to <see cref="GalaxusDemoPrompts"/> in its own message, because the eval lane
    /// grades against those exact strings — the identity belongs in a header, not spliced into
    /// the sentence a person actually typed.
    /// </remarks>
    private static string SessionHeader(CustomerProfile profile) =>
        $"[session] You are serving customer id {profile.Id} — market {profile.Market}, language {profile.Language}. "
      + "Pass that id to GetUserProfile, GetPurchaseHistory and GetInterestMap. Never substitute another customer, "
      + "and never invent an id. The next message is the customer speaking.";

    private static async Task<AgentRun?> RunAgentAsync(CustomerProfile profile, string prompt, CancellationToken cancellationToken)
    {
        Console.WriteLine($"  Creating {RecommendationAgentFactory.AgentName} — eleven read-only tools, asserted at construction...\n");

        ChatClientAgent agent;
        try
        {
            // Throws if the registered set differs from the eleven-name allow-list in EITHER
            // direction (§F.1 / A-1). A mutating tool cannot be added by accident: the app
            // fails to start rather than shipping a surface nobody re-checked.
            agent = RecommendationAgentFactory.Create();
        }
        catch (InvalidOperationException ex)
        {
            PrintGenericFailure("Tool-surface invariant", "The registered tool set is not the read-only allow-list. "
                + "This is the guardrail working, not a bug in it — fix the array in RecommendationAgentFactory.", ex);
            return null;
        }

        var session  = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, SessionHeader(profile)),
            new ChatMessage(ChatRole.User, prompt)
        };

        RecommendationPrinter.PrintTraceHeader();

        // Both scopes are AsyncLocal and both must wrap the run: the budget bounds the spend
        // (§F.9), the capture records the PresentRecommendation calls verbatim (§0.5 / D-1).
        using var budget  = ToolCallBudget.BeginScope(ToolCallCap);
        using var capture = GalaxusTools.BeginRunCapture();

        AgentResponse? response;
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            response = await agent.RunAsync(messages, session, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException azureEx)
        {
            PrintAzureFailure(azureEx.Status, azureEx.ErrorCode, azureEx.Message, azureEx.StackTrace);
            return null;
        }
        catch (ClientResultException clientEx)
        {
            PrintAzureFailure(clientEx.Status, errorCode: null, clientEx.Message, clientEx.StackTrace);
            return null;
        }
        catch (TaskCanceledException timeoutEx)
        {
            PrintGenericFailure("Timeout",
                "Azure did not answer inside the SDK's HTTP timeout — usually throttling or a long content-filter check.",
                timeoutEx);
            return null;
        }
        catch (Exception ex)
        {
            PrintGenericFailure($"{ex.GetType().Name} (agent run failed)",
                "Most often a tool method threw, or the model returned a malformed tool call. The inner exception names the tool.",
                ex);
            return null;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;

        // Read the run-scoped state BEFORE the scopes are disposed — outside them both
        // collections read empty, and an empty capture is indistinguishable from a model
        // that presented nothing.
        var presented  = GalaxusTools.PresentedInCurrentRun;
        var provenance = GalaxusTools.RetrievalProvenanceInCurrentRun;
        var used       = ToolCallBudget.Used;          // REFUSABLE calls only — presentations are not in it
        var summary    = ToolCallBudget.Summary;       // every counter against its own cap, for the footer

        RecommendationPrinter.PrintTraceFooter();
        PrintToolTrace(response, elapsed, summary);

        return new AgentRun(presented, provenance, used, summary, response.Text);
    }

    // ── The offline baseline arm ──────────────────────────────────────────────

    /// <summary>
    /// Selects products with NO model call: for each derived interest signal, search by meaning
    /// and take the top candidates the guardrails would accept.
    /// </summary>
    /// <remarks>
    /// This is a baseline, not a simulation of the agent. It is here because "the LLM found the
    /// cross-category match" is an empty claim until something without an LLM has tried the same
    /// query — design §0.5 / D-4 names the missing arm, and an absent baseline is not a zero floor.
    /// Its evidence citations are read straight from the catalogue, so the evidence arm cannot
    /// fail on this path; the ledger says so rather than banking a clean sheet it did not earn.
    /// </remarks>
    private static async Task<(IReadOnlyList<PresentedRecommendation> Presented,
                               IReadOnlyDictionary<string, IReadOnlyList<string>> Provenance)>
        RunOfflineBaselineAsync(
            InterestMap map,
            GuardrailContext context,
            IProductRetriever retriever,
            Catalogue catalogue,
            CancellationToken cancellationToken)
    {
        var presented  = new List<PresentedRecommendation>();
        var provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var taken      = new HashSet<string>(StringComparer.Ordinal);

        var exclude = new HashSet<string>(context.OwnedProductIds, StringComparer.Ordinal);

        foreach (var signal in map.Signals.OrderByDescending(s => s.Strength).Take(OfflineSignalsUsed))
        {
            var query = RetrievalQuery.For(signal.Label) with
            {
                TopK = OfflineCandidatesPerSignal + 4,
                Market = context.User.Market,
                ExcludeProductIds = exclude
            };

            var result = await retriever.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"   🔎 SearchProductsByMeaning(\"{Clip(signal.Label, 62)}\") → {result.Count} candidate(s)");

            var kept = 0;
            foreach (var hit in result.Hits)
            {
                if (kept >= OfflineCandidatesPerSignal) break;
                if (!taken.Add(hit.ProductId)) continue;
                if (!catalogue.TryGet(hit.ProductId, out var product) || product is null) continue;

                var citation = catalogue.AttributesOf(product)
                                        .OrderBy(a => a, StringComparer.Ordinal)
                                        .FirstOrDefault();
                if (citation is null) continue;

                presented.Add(new PresentedRecommendation(
                    product.Id,
                    $"Retrieved for the derived interest \"{signal.Label}\". Selected by the baseline arm, with no model call.",
                    EvidenceRef.AttributePrefix + citation,
                    OutOfStock: product.StockUnits == 0));

                provenance[product.Id] = [signal.Label];
                kept++;
                Console.WriteLine($"   ⭐ PresentRecommendation(\"{product.Id}\", evidence=\"attr:{citation}\")");
            }
        }

        Console.WriteLine();
        return (presented, provenance);
    }

    // ── Assembly: tool calls → RecommendationSet ──────────────────────────────

    /// <summary>A drop decided before the pipeline ran, replayed into its ledger afterwards.</summary>
    private sealed record PreDrop(GuardrailStage Stage, string Reason, string Subject, string Detail);

    /// <summary>
    /// Builds the answer from the <c>PresentRecommendation</c> calls — the ONLY sanctioned channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Read this before quoting the evidence numbers.</b> The frozen tool signature carries
    /// only the PRODUCT side of §F.3's two-sided evidence: <c>evidence</c> is one
    /// <c>attr:</c> / <c>review:</c> citation and there is no argument for the user side. So the
    /// user side is DERIVED here — from retrieval provenance, i.e. from which search need
    /// actually surfaced the SKU — and not written by the model.
    /// </para>
    /// <para>
    /// That has a consequence worth stating out loud rather than discovering later: on this path
    /// the user-side arm of <see cref="EvidenceRequiredFilter"/> can only fail one way — when NO
    /// derived interest explains the product at all. It cannot catch a model that cites the wrong
    /// purchase, because the model was never given the chance to cite one. The discriminating
    /// checks in this demo are the ones that read the model's own arguments: catalogue grounding,
    /// citation resolution, the stated-price scan, the sensitive-prose scan and the out-of-stock
    /// acknowledgement. <see cref="NoteDerivedUserSide"/> writes that into the ledger.
    /// </para>
    /// </remarks>
    private static (RecommendationSet Raw, IReadOnlyList<PreDrop> Drops) Assemble(
        IReadOnlyList<PresentedRecommendation> presented,
        IReadOnlyDictionary<string, IReadOnlyList<string>> provenance,
        InterestMap map,
        Catalogue catalogue,
        IReadOnlyList<ReplenishmentDto> replenishment)
    {
        var recommendations = new List<RecommendationDto>();
        var drops           = new List<PreDrop>();
        var seen            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in presented)
        {
            var sku = item.Sku.Trim();

            if (!seen.Add(sku))
            {
                drops.Add(new PreDrop(GuardrailStage.CatalogueGrounding, GuardrailReasons.DuplicatePresentation, sku,
                    "presented more than once in this turn; only the first call is shown"));
                continue;
            }

            catalogue.TryGet(sku, out var product);

            var needs  = provenance.TryGetValue(sku, out var recorded) ? recorded : [];
            var signal = AttributeSignal(needs, product, map);

            var (key, value) = ResolveProductSide(product, item);

            recommendations.Add(new RecommendationDto(
                sku,
                item.Reason,
                new EvidenceDto(
                    signal?.Label ?? string.Empty,
                    // A stated-in-session interest is evidenced by the sentence, never by history
                    // (§F.3, and under the §F.6 opt-out there IS no history to cite).
                    signal is not null && !IsStatedInSession(signal) ? signal.EvidencePurchaseIds : [],
                    key,
                    value,
                    item.Citation is { Kind: EvidenceRefKind.Review } review ? review.Token : null),
                Confidence(signal, product)));
        }

        var raw = RecommendationSet.Empty with
        {
            InterestMap     = [.. map.Signals.Select(InterestSignalDto.From)],
            Recommendations = recommendations,
            Replenishment   = replenishment
        };

        return (raw, drops);
    }

    /// <summary>
    /// Credits a presented SKU to the derived interest signal whose label best matches the search
    /// needs that surfaced it. Returns null when nothing clears <see cref="AttributionFloor"/> —
    /// which drops the item, because a product no derived interest explains is exactly the case
    /// §F.3 exists to remove.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Match</b> is the better of two legs. The concept-space cosine is the one that matters:
    /// it connects "owns whole beans and vacuum canisters but no grinder" to a search for "a burr
    /// grinder for espresso at home" with not one shared word. Token overlap is the fallback for a
    /// need whose vocabulary the concept lexicon does not cover, where the cosine is legitimately
    /// 0 for everything.
    /// </para>
    /// <para>
    /// <b>Ranking</b> among the signals that clear the floor is <c>match × strength</c> — two
    /// independent pieces of evidence multiplied, not one of them ignored. Ranking on match alone
    /// (the first cut of this method did) systematically credited products to whichever
    /// leaf-category signal happened to share the query's nouns: a live run credited trekking
    /// poles to "Trekking packs" (0.52) rather than to the 0.86 conjunction that is the entire
    /// point of Nadia's case, and the weak strength then pushed genuinely good items under the
    /// 0.45 confidence floor. Both numbers now come from the same criterion, which is what they
    /// should always have done.
    /// </para>
    /// </remarks>
    private static InterestSignal? AttributeSignal(IReadOnlyList<string> needs, Product? product, InterestMap map)
    {
        if (map.Signals.Count == 0) return null;

        // Only the three SEMANTIC tools record provenance. A product the model reached through
        // BrowseCategory — which the system prompt explicitly permits — arrives with none, and
        // treating "no provenance" as "no attribution" made those a GUARANTEED drop: observed
        // live, a correctly-reasoned Brewista scale removed with unknown_signal_label because of
        // how it was found rather than what it was. The fallback measures the PRODUCT's own
        // embedding document against the labels instead, in the same concept space. It restores
        // nothing that should fail: a product no derived interest explains still scores below
        // the floor and is still dropped.
        var probes = needs.Count > 0
            ? needs
            : product is not null ? [EmbeddingDocument.ForProduct(product)] : (IReadOnlyList<string>)[];

        if (probes.Count == 0) return null;

        InterestSignal? best = null;
        double bestRank = 0.0;

        foreach (var signal in map.Signals)
        {
            var label  = ConceptEmbeddingSource.Instance.Embed(signal.Label);
            var tokens = ContentTokens(signal.Label);

            double match = 0.0;
            foreach (var probe in probes)
            {
                double cosine  = EmbeddingVectors.DotOfUnitVectors(label, ConceptEmbeddingSource.Instance.Embed(probe));
                double overlap = Overlap(tokens, ContentTokens(probe));
                match = Math.Max(match, Math.Max(cosine, overlap));
            }

            // The floor is on the MATCH, never on the rank: a very strong interest must not be
            // able to buy its way past a query it does not explain.
            if (match < AttributionFloor) continue;

            double rank = match * signal.Strength;

            // Ties break on label, ordinal, so identical runs produce identical ledgers. A demo
            // whose numbers move between two runs of the same input teaches the audience to
            // distrust every other number on the screen.
            if (rank > bestRank ||
                (rank == bestRank && best is not null && string.CompareOrdinal(signal.Label, best.Label) < 0))
            {
                bestRank = rank;
                best = signal;
            }
        }

        return best;
    }

    /// <summary>
    /// Maps the model's evidence citation back to the <c>(key, value)</c> pair it names, so the
    /// product side of <see cref="EvidenceDto"/> restates a catalogue fact rather than inventing one.
    /// </summary>
    /// <remarks>
    /// When the citation names nothing, the raw token is passed through unchanged and the pipeline
    /// drops the item with <c>attribute_not_found</c>. Substituting a real attribute for a bad one
    /// would be repairing the artifact under test — the citation is the model's claim, and a claim
    /// that resolves to nothing has to stay unresolved.
    /// </remarks>
    private static (string Key, string Value) ResolveProductSide(Product? product, PresentedRecommendation item)
    {
        var raw = item.Evidence?.Trim() ?? string.Empty;
        if (product is null) return (raw, raw);

        if (item.Citation is not { } citation) return (raw, raw);

        if (citation.Kind == EvidenceRefKind.Review)
        {
            // A review citation carries no attribute, and the tool has no second argument for
            // one. The product side is read from the catalogue's authored spec order; the
            // discriminating fact for this citation is still the model's own review id, checked
            // verbatim against Product.ReviewIds.
            var first = product.Specs.FirstOrDefault();
            return first.Key is { Length: > 0 } ? (first.Key, first.Value) : (raw, raw);
        }

        var token = citation.Token;

        foreach (var (key, value) in product.Specs)
        {
            var k = Product.NormalizeAttributeToken(key);
            var v = Product.NormalizeAttributeToken(value);
            if (string.Equals(k, token, StringComparison.Ordinal) ||
                string.Equals(v, token, StringComparison.Ordinal) ||
                string.Equals($"{k}={v}", token, StringComparison.Ordinal))
            {
                return (key, value);
            }
        }

        foreach (var tag in product.Tags)
        {
            if (string.Equals(Product.NormalizeAttributeToken(tag), token, StringComparison.Ordinal))
                return (tag, tag);

            var colon = tag.IndexOf(':');
            if (colon > 0 && colon < tag.Length - 1 &&
                string.Equals(Product.NormalizeAttributeToken(tag[(colon + 1)..]), token, StringComparison.Ordinal))
            {
                // The WHOLE tag, not the suffix: TryGetAttributeValue resolves a whole tag and
                // Product.Attributes contains it, so the assembled evidence stays verifiable on
                // both sides. The suffix alone satisfies only one of the two.
                return (tag, tag);
            }
        }

        return (raw, raw);
    }

    /// <summary>
    /// The routing number printed on each card and banded by <see cref="ConfidenceBands"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is NOT the model's self-reported confidence. The <c>PresentRecommendation</c> tool has
    /// no confidence argument, deliberately: self-reported LLM confidence is uncalibrated until
    /// somebody measures it, and §F.7 would rather route on something the code can point at.
    /// </para>
    /// <para>
    /// So this is the mean of two code-derived quantities — the strength of the interest signal
    /// the product was credited to, and the concept-space fit between the product's own embedding
    /// document and that signal's label. Both are unmeasured against outcomes. It is a routing
    /// heuristic for the two trays and nothing more; the reliability curve of this number against
    /// a gold set belongs to the eval lane, and until that runs no claim is made about it.
    /// </para>
    /// </remarks>
    private static double Confidence(InterestSignal? signal, Product? product)
    {
        if (signal is null || product is null) return 0.0;

        var fit = EmbeddingVectors.DotOfUnitVectors(
            ConceptEmbeddingSource.Instance.Embed(signal.Label),
            ConceptEmbeddingSource.Instance.Embed(EmbeddingDocument.ForProduct(product)));

        return Math.Clamp((signal.Strength + Math.Max(0.0, fit)) / 2.0, 0.0, 1.0);
    }

    // ── The replenishment lane ────────────────────────────────────────────────

    /// <summary>
    /// Builds the repeat-buy tray from the purchases the classifier routed to replenishment.
    /// </summary>
    /// <remarks>
    /// Its own tray, never a discovery (§B.3, Sofia): recommending the cartridges somebody has
    /// bought five times is not a recommendation. An item appears once it is inside the last
    /// <see cref="ReplenishmentDueFraction"/> of its cadence, or already overdue.
    /// </remarks>
    private static IReadOnlyList<ReplenishmentDto> BuildReplenishmentLane(
        InterestMap map,
        IReadOnlyList<ClassifiedPurchase> classified,
        Catalogue catalogue)
    {
        if (map.RoutedToReplenishment.Count == 0) return [];

        var routed = new HashSet<string>(map.RoutedToReplenishment, StringComparer.Ordinal);
        var lane   = new List<ReplenishmentDto>();

        var byProduct = classified
            .Where(c => routed.Contains(c.PurchaseId))
            .GroupBy(c => c.Product.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byProduct)
        {
            var latest = group.OrderByDescending(c => c.Purchase.PurchasedOn)
                              .ThenBy(c => c.PurchaseId, StringComparer.Ordinal)
                              .First();

            if (!catalogue.TryGet(group.Key, out var product) || product is null) continue;

            var cadence = product.TypicalReplenishDays ?? 0;
            if (cadence <= 0) continue;

            var elapsed = latest.Purchase.DaysSince(Personas.DemoToday);
            if (elapsed < cadence * ReplenishmentDueFraction) continue;

            lane.Add(new ReplenishmentDto(product.Id, elapsed, cadence, latest.Because));
        }

        return lane
            .OrderBy(r => r.DaysUntilDue)
            .ThenBy(r => r.ProductId, StringComparer.Ordinal)
            .ToList();
    }

    // ── Retrieval composition ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the retriever the three semantic tools search through.
    /// </summary>
    /// <remarks>
    /// The offline <see cref="ConceptEmbeddingSource"/> is the default on purpose: it is
    /// deterministic, needs no key, and genuinely retrieves by meaning.
    /// <see cref="PrecomputedEmbeddingSource"/> is NOT reached for first — with no committed
    /// asset it yields <c>denseAvailable = false</c>, and the demo would silently lose the
    /// cross-category match that is its entire point.
    /// </remarks>
    private static async Task<IProductRetriever> BuildRetrieverAsync(Catalogue catalogue, CancellationToken cancellationToken)
        => await HybridRetriever.BuildAsync(catalogue.All, ConceptEmbeddingSource.Instance, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);

    // ── Ledger annotations ────────────────────────────────────────────────────

    /// <summary>
    /// Records, in the ledger itself, which arms could not have failed on this turn.
    /// </summary>
    /// <remarks>
    /// A clean ledger is only evidence when every arm on it was able to fire. Writing the
    /// inapplicable arms down beside the counts is what stops the panel from being read as a
    /// score — see <see cref="GuardrailLedger.HasInapplicableArm"/>, which makes the renderer
    /// print the warning in red.
    /// </remarks>
    private static void NoteDerivedUserSide(GuardrailLedger ledger, bool offline)
    {
        ledger.Note(GuardrailStage.EvidenceRequired, GuardrailReasons.ArmInapplicable, "user-side evidence",
            "the PresentRecommendation signature carries only the PRODUCT side, so the user side is DERIVED from "
          + "retrieval provenance. It can fail only when no derived interest explains the product at all — it cannot "
          + "catch a wrongly cited purchase, because the model was never asked to cite one. Do not read its silence as a pass");

        // ⚠ The PRODUCT side has two silent arms on this path as well, and leaving them unnamed
        // let a two-sided check read as a clean sheet on a turn where only one side could fire.
        //
        //   attribute_value_mismatch: ResolveProductSide fills BOTH ProductAttributeKey and
        //   ProductAttributeValue out of the catalogue's own record, so the comparison the filter
        //   makes is x == x. It cannot fail while the model has no argument for a value.
        //
        //   unresolvable_evidence: EvidenceDto.Citation is REBUILT from ProductAttributeKey, which
        //   TryGetAttributeValue resolved one branch above. It re-asserts a fact already checked.
        //
        // The discriminating product-side arm that DOES fire is attribute_not_found: the citation
        // is the model's own verbatim argument, and a token the catalogue does not carry fails there.
        ledger.Note(GuardrailStage.EvidenceRequired, GuardrailReasons.ArmInapplicable, "product-side value + citation",
            "attribute_value_mismatch and unresolvable_evidence cannot fire on this path. The tool carries ONE "
          + "evidence string, so the product-side key AND value are both resolved from the catalogue before the "
          + "check runs, and the compact citation is rebuilt from the key that resolution already verified. The "
          + "product-side arm that IS discriminating is attribute_not_found, on the model's verbatim citation. "
          + "Do not read these two arms' silence as a pass");

        if (offline)
        {
            ledger.Note(GuardrailStage.EvidenceRequired, GuardrailReasons.ArmInapplicable, "offline baseline arm",
                "no model ran: the citations below were read straight from the catalogue and the reason strings were "
              + "composed by this file, so the evidence, grounding, sensitive_prose and stated_price arms cannot fail "
              + "on this path either. The already_owned arm is likewise pre-empted — the offline selector passes the "
              + "customer's owned SKUs as a retrieval exclusion, so nothing owned ever reaches the filter. This panel "
              + "measures the BASELINE, not the agent");
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Dumps every tool invocation in call order with its result preview, exactly as
    /// <c>Demo01_TravelAgent</c> does — the fastest way to see where a run broke.
    /// </summary>
    private static void PrintToolTrace(AgentResponse response, TimeSpan elapsed, string budgetSummary)
    {
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();

        var resultsByCallId = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .GroupBy(r => r.CallId)
            .ToDictionary(g => g.Key, g => g.First());

        // Every counter against ITS OWN cap. The first version printed one "budget N of 24" and
        // that number counted presentations as searches — see ToolCallBudget's remarks.
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  📊 Tool trace — {calls.Count} call(s) in {elapsed.TotalSeconds:0.0}s · {budgetSummary}");
        Console.ResetColor();

        if (calls.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("     ⚠  No tools invoked — the model answered from prompt context only. It therefore");
            Console.WriteLine("        presented nothing, since PresentRecommendation is the only channel. Usually the");
            Console.WriteLine("        deployment does not support function calling, or the run was cut short.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        for (var i = 0; i < calls.Count; i++)
        {
            var preview = resultsByCallId.TryGetValue(calls[i].CallId, out var r)
                ? Clip(Flatten(r.Result?.ToString()), 110)
                : "(no result returned — tool may have errored)";
            Console.WriteLine($"     [{i + 1,2}] {calls[i].Name}  →  {preview}");
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Reconciles what the model presented against what the customer will see, item by item.
    /// </summary>
    /// <remarks>
    /// Printed because the two counts differing is the interesting event, and a panel that shows
    /// only the survivors hides it. Zero presentations gets its own loud line: an agent that says
    /// nothing must never read the same as an agent that answered well.
    /// </remarks>
    private static void PrintPresentationAudit(IReadOnlyList<PresentedRecommendation> presented, RecommendationSet cleaned)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─── Channel audit (PresentRecommendation calls → cards) ──────────────────");
        Console.ResetColor();

        if (presented.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠  The agent made ZERO PresentRecommendation calls.");
            Console.WriteLine(cleaned.Abstained
                ? "     The abstention gate had already fired, so this is the expected shape for this persona."
                : "     Nothing was recommended and the gate did NOT fire — this is a MISS, not a cautious answer.\n"
                + "     A product named only in the prose below is not a recommendation; the channel is the tool call.");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        var survived = cleaned.AllPresented.Select(r => r.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in presented)
        {
            var kept = survived.Contains(item.Sku.Trim());
            Console.ForegroundColor = kept ? ConsoleColor.DarkGray : ConsoleColor.Yellow;
            Console.WriteLine($"     {(kept ? "✓" : "✗")} {item.Sku,-10} evidence={Clip(item.Evidence, 34),-34} "
                            + $"outOfStock={item.OutOfStock.ToString().ToLowerInvariant()}");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"     {presented.Count} presented → {cleaned.PresentedCount} shown");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Explains the accounting behind the ledger's "tool calls N of 24" line, every run.
    /// </summary>
    /// <remarks>
    /// The ledger's figure is REFUSABLE calls only: the three semantic and seven structured tools.
    /// <c>PresentRecommendation</c> is the answer channel — counted separately, never refused and
    /// never charged to a cap (§F.9), because a spent budget must bound the spend, not silence the
    /// answer. Identical repeats within the turn are replayed from memory and charged to nothing.
    /// Printed on every live run because the 2026-09-04 run's "24 of 24 — stopped on its own" was
    /// a saturated counter that had been charging presentations against the search cap, and a
    /// reader had no way to see that from the one number.
    /// </remarks>
    private static void PrintBudgetNote(string budgetSummary)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ℹ  budget accounting: {budgetSummary}");
        Console.WriteLine("     'refusable' is the ledger's tool-call figure: the ten retrieval and lookup tools, capped.");
        Console.WriteLine("     Presentations are the answer channel — counted, never refused, never charged to a cap.");
        Console.WriteLine("     'replays' are identical repeats answered from this turn's memory at no cost (§F.9).");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintRobinsProse(string? text)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─── What Robin wrote ─────────────────────────────────────────────────────");
        Console.WriteLine("  (prose only. Nothing here is parsed: the cards above were built from the");
        Console.WriteLine("   PresentRecommendation tool calls, which is the one sanctioned channel.)");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine();
        Console.WriteLine(string.IsNullOrWhiteSpace(text) ? "  (empty)" : Indent(text));
        Console.ResetColor();
        Console.WriteLine();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Demo 01 — Robin, the Galaxus recommendation agent (single agent)           ║
║   Code derives the interests · the model searches · code screens the answer  ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintRequest(CustomerProfile profile, string prompt, bool personalizationDisabled)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Customer : {profile.DisplayName} ({profile.Id}) · {profile.Market} · {profile.Language} · "
                        + $"{profile.PurchaseCount} purchase line(s)");
        Console.ResetColor();

        if (personalizationDisabled)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  🔒 personalization OFF — GetPurchaseHistory and GetInterestMap will REFUSE. The");
            Console.WriteLine("     history is not filtered or summarised; it is not read. The turn runs on what");
            Console.WriteLine("     the customer says in this conversation (§F.6).");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"\n  Request  : \"{prompt}\"\n");
        Console.ResetColor();
    }

    private static void PrintRetrievalBanner(IProductRetriever retriever)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Retrieval: {retriever.Name} over {retriever.ProductCount} products · "
                        + $"dense leg {(retriever.DenseAvailable ? "available" : "UNAVAILABLE")}");
        Console.ResetColor();

        if (retriever is HybridRetriever { DenseAvailable: false } hybrid)
            RecommendationPrinter.PrintDegradedRetrievalNotice(hybrid.DenseUnavailableReason);

        Console.WriteLine();
    }

    private static void PrintOfflineBanner()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"  ┌──────────────────────────────────────────────────────────────────────────┐
  │  OFFLINE — no model call was made.                                       │
  │  Everything below was produced by the deterministic path alone: the       │
  │  interest map, one search per signal, and the guardrail pipeline. This is │
  │  the BASELINE arm, not the agent. Compare it with a live run before       │
  │  believing any claim about what the model adds.                          │
  └──────────────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintUnknownPersona(string userId)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ Unknown customer '{userId}'.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("     Known personas:");
        foreach (var id in Personas.AllPersonaIds)
        {
            var profile = UserProfiles.Require(id);
            Console.WriteLine($"       {id}  {profile.DisplayName}");
        }
        Console.WriteLine("\n     No fallback is applied on purpose: running the wrong persona's prompt against the");
        Console.WriteLine("     right persona's history produces a plausible, wrong demo.");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping the live run — Azure OpenAI credentials required.

     Set the following environment variables and try again:
       AZURE_OPENAI_ENDPOINT
       AZURE_OPENAI_API_KEY
       AZURE_OPENAI_DEPLOYMENT          (optional, defaults to gpt-5-mini)

     Or run the deterministic half with no key at all:
       dotnet run --project samples/Galaxus.RecommendationAgent -- 1 --offline
");
        Console.ResetColor();
    }

    private static void PrintAzureFailure(int status, string? errorCode, string message, string? stackTrace)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ Azure OpenAI request failed (HTTP {status})");
        if (!string.IsNullOrEmpty(errorCode)) Console.WriteLine($"     ErrorCode: {errorCode}");
        Console.WriteLine($"     Message:   {message}");

        var hint = status switch
        {
            400        => "Bad request. A 'response cut off / content filter' message means Azure's content-safety policy trimmed the output mid-tool-call.",
            401 or 403 => "Auth / quota. Re-check AZURE_OPENAI_API_KEY and that the deployment has function calling enabled.",
            404        => "Deployment not found. Verify AZURE_OPENAI_DEPLOYMENT names a deployment in this resource.",
            408        => "Azure timed out reading the request.",
            429        => "Rate-limited. The run succeeds on retry once the bucket refills.",
            >= 500     => "Azure-side server error. Usually transient — retry.",
            _          => null,
        };
        if (hint is not null) Console.WriteLine($"     Hint:      {hint}");
        if (!string.IsNullOrEmpty(stackTrace)) Console.WriteLine($"\n     Stack trace:\n{stackTrace}");
        Console.ResetColor();
    }

    private static void PrintGenericFailure(string kind, string hint, Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ Run failed — {kind}");
        Console.WriteLine($"     Message:  {ex.Message}");
        Console.WriteLine($"     Hint:     {hint}");
        if (ex.InnerException is not null)
            Console.WriteLine($"     Inner:    {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        Console.WriteLine($"\n     Stack trace:\n{ex.StackTrace}");
        Console.ResetColor();
    }

    // ── Small pure helpers ────────────────────────────────────────────────────

    private static bool IsStatedInSession(InterestSignal signal) =>
        string.Equals(signal.EvidenceKind, InterestEvidenceKinds.StatedInSession, StringComparison.Ordinal);

    /// <summary>
    /// Content tokens for the fallback attribution leg: lower-cased words of three characters or
    /// more, with a crude trailing-s / -es / -ing / -ed strip. Crude on purpose and documented as
    /// such — it is a fallback for vocabulary the concept lexicon does not cover, not a stemmer.
    /// </summary>
    private static HashSet<string> ContentTokens(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        foreach (var raw in text.ToLowerInvariant().Split(
                     [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '"', '\'', '/', '-', '—'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 3) continue;
            var token = raw;
            if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 5) token = token[..^3];
            else if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 4) token = token[..^2];
            else if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 4) token = token[..^2];
            else if (token.EndsWith('s') && token.Length > 3) token = token[..^1];
            tokens.Add(token);
        }

        return tokens;
    }

    private static double Overlap(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0.0;
        var shared = left.Count(right.Contains);
        return (double)shared / left.Count;
    }

    private static string Flatten(string? text) =>
        (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static string Clip(string? text, int max)
    {
        var value = text ?? string.Empty;
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine,
            text.Replace("\r\n", "\n").Split('\n').Select(line => "  " + line));
}
