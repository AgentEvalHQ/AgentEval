// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// Everything the guardrails are allowed to measure a recommendation against. Assembled by the
/// caller from the catalogue and the code-derived interest map — never from the model's output.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape of this type is the point.</b> The artifact under test does not get to supply
/// the bar it is measured against: every set here comes from the seed data or from
/// deterministic code, and a model that invents a flattering spec value fails the check
/// HARDER, not softer.
/// </para>
/// <para>
/// <b>Why the guardrails take this and not the catalogue façade.</b> Depending on a bundle of
/// plain domain collections keeps this layer compilable and testable on its own, lets the eval
/// project drive the identical pipeline without constructing a catalogue, and means the
/// catalogue lane adapts to the guardrails rather than the guardrails to the catalogue. Design
/// §F writes the signature as <c>Apply(raw, catalogue, user, map)</c>; <see cref="Create"/> is
/// the two-line adapter for exactly that call.
/// </para>
/// </remarks>
public sealed record GuardrailContext
{
    /// <summary>The catalogue, keyed by <see cref="Product.Id"/>. The only authority on what exists.</summary>
    public required IReadOnlyDictionary<string, Product> ProductsBySku { get; init; }

    /// <summary>The customer this answer is for. Their market gates availability; their opt-out gates history.</summary>
    public required User User { get; init; }

    /// <summary>The CODE-derived interest map. A recommendation may only cite a label present here (§F.3).</summary>
    public required InterestMap InterestMap { get; init; }

    /// <summary>
    /// Purchase ids this customer may legitimately be shown as evidence: their own, minus the
    /// ones classified as gifts.
    /// </summary>
    public IReadOnlySet<string> UserPurchaseIds { get; init; } = Empty;

    /// <summary>
    /// Purchase ids ruled out as gifts. Tracked separately from "not this customer's" so a
    /// citation of Marco's console produces <see cref="GuardrailReasons.GiftPurchaseCited"/>
    /// rather than the vaguer foreign-id reason.
    /// </summary>
    public IReadOnlySet<string> GiftPurchaseIds { get; init; } = Empty;

    /// <summary>SKUs the customer already owns (gifts excluded — a gift is not something they own).</summary>
    public IReadOnlySet<string> OwnedProductIds { get; init; } = Empty;

    /// <summary>
    /// Leaf categories in which the customer owns a durable that is still inside its typical
    /// service life. Recommending another one is the "similar to your Vitamix ⇒ three more
    /// blenders" failure (§B.3).
    /// </summary>
    public IReadOnlySet<string> OwnedDurableLeafCategories { get; init; } = Empty;

    /// <summary>
    /// Category names flagged <see cref="Category.SensitiveInference"/> in the category tree.
    /// EMPTY IS A MEANINGFUL STATE: the category arm of the blocklist then has nothing to fire
    /// against, and says so in the ledger instead of passing quietly.
    /// </summary>
    public IReadOnlySet<string> SensitiveCategoryNames { get; init; } = Empty;

    /// <summary>
    /// Categories the customer put in play in this session — the ones they named outright, plus
    /// every element of a category path whose own words the customer's utterance raised. The
    /// narrow, explicit exemption from suppression.
    /// </summary>
    /// <remarks>
    /// The path expansion is not a convenience: the suppression arm walks a product's whole
    /// <see cref="Product.CategoryPath"/>, so exempting only the matching element exempts nothing.
    /// <see cref="Create"/> derives it; see the comment there.
    /// </remarks>
    public IReadOnlySet<string> ExplicitlyRequestedCategories { get; init; } = Empty;

    /// <summary>
    /// Special-category terms the CUSTOMER used in their own words this session. "I need a
    /// larger cuff for the blood-pressure monitor I already have" puts <c>blood pressure</c>
    /// here, and that is what turns a blocked inference into a served request (§F.5).
    /// </summary>
    public IReadOnlySet<string> SensitiveTopicsStatedInSession { get; init; } = Empty;

    /// <summary>Set false to disable the durable-upgrade suppression arm. Default true.</summary>
    public bool SuppressDurableUpgrades { get; init; } = true;

    /// <summary>The verification timestamp stamped onto every <see cref="PriceStockSnapshot"/>.</summary>
    public DateTimeOffset AsOfUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Delivery estimate, in days, stamped onto an in-stock snapshot.</summary>
    public int DeliveryEstimateDays { get; init; } = 2;

    /// <summary>
    /// True when the customer stated a need in this session — the second half of the
    /// abstention gate's condition (§F.8). Derived from the map rather than from the mere
    /// presence of an utterance: "Hi — what do you recommend for me?" is an utterance, not a
    /// need, which is precisely why Luca Ferrari abstains.
    /// </summary>
    public bool HasStatedNeedInSession =>
        InterestMap.Signals.Any(s => string.Equals(s.EvidenceKind, InterestEvidenceKinds.StatedInSession, StringComparison.Ordinal));

    /// <summary>
    /// Fails loudly on a wiring fault rather than quietly on every recommendation. If the map
    /// carries behaviour-derived signals but no purchase ids were supplied, every two-sided
    /// evidence check would drop its item and the ledger would look like a working guardrail
    /// instead of a missing argument — an extreme value produced by a wiring fault, not by the
    /// thing under test.
    /// </summary>
    /// <exception cref="InvalidOperationException">The context cannot support the checks it will be asked to perform.</exception>
    public void Validate()
    {
        bool hasBehaviouralSignal = InterestMap.Signals.Any(s =>
            !string.Equals(s.EvidenceKind, InterestEvidenceKinds.StatedInSession, StringComparison.Ordinal));

        if (hasBehaviouralSignal && UserPurchaseIds.Count == 0)
        {
            throw new InvalidOperationException(
                "GuardrailContext is mis-wired: the interest map carries behaviour-derived signals but UserPurchaseIds is empty, " +
                "so EvidenceRequiredFilter would drop every recommendation and the ledger would read as a clean guardrail. " +
                "Build the context with GuardrailContext.Create(...) and pass the classified purchase history.");
        }

        if (!string.Equals(InterestMap.UserId, User.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"GuardrailContext is mis-wired: the interest map belongs to '{InterestMap.UserId}' but the customer is '{User.Id}'.");
        }
    }

    /// <summary>
    /// Builds a context from the pieces the catalogue façade already has. This is the adapter
    /// for design §F's <c>Apply(raw, catalogue, user, map)</c> call shape.
    /// </summary>
    /// <param name="productsBySku">The catalogue, keyed by <see cref="Product.Id"/>.</param>
    /// <param name="user">The customer.</param>
    /// <param name="interestMap">The code-derived map for that customer.</param>
    /// <param name="classified">
    /// The customer's classified purchase history. Supplies the evidence, ownership and
    /// durable-horizon sets. Null or empty is legitimate only for a map with no
    /// behaviour-derived signals — <see cref="Validate"/> enforces that.
    /// </param>
    /// <param name="categories">The category tree; entries flagged <see cref="Category.SensitiveInference"/> are collected.</param>
    /// <param name="customerUtterance">
    /// What the customer actually typed. Scanned once for special-category terms, so a topic
    /// the customer raised is exempted from suppression.
    /// </param>
    /// <param name="explicitlyRequestedCategories">Categories the customer named by name.</param>
    /// <param name="asOf">The demo clock's "today", used for the durable-horizon test.</param>
    /// <param name="asOfUtc">The verification timestamp stamped onto price snapshots.</param>
    public static GuardrailContext Create(
        IReadOnlyDictionary<string, Product> productsBySku,
        User user,
        InterestMap interestMap,
        IReadOnlyList<ClassifiedPurchase>? classified = null,
        IEnumerable<Category>? categories = null,
        string? customerUtterance = null,
        IEnumerable<string>? explicitlyRequestedCategories = null,
        DateOnly? asOf = null,
        DateTimeOffset? asOfUtc = null)
    {
        ArgumentNullException.ThrowIfNull(productsBySku);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(interestMap);

        var today = asOf ?? DateOnly.FromDateTime((asOfUtc ?? DateTimeOffset.UtcNow).UtcDateTime);
        var lines = classified ?? [];

        var userPurchaseIds  = new HashSet<string>(StringComparer.Ordinal);
        var giftPurchaseIds  = new HashSet<string>(StringComparer.Ordinal);
        var ownedProductIds  = new HashSet<string>(StringComparer.Ordinal);
        var ownedDurableLeaf = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (line.IsGift)
            {
                giftPurchaseIds.Add(line.PurchaseId);
                continue;
            }

            userPurchaseIds.Add(line.PurchaseId);
            ownedProductIds.Add(line.Product.Id);

            if (!line.Product.IsConsumable &&
                line.Purchase.DaysSince(today) < InterestMapBuilder.DurableUpgradeHorizonDays)
            {
                ownedDurableLeaf.Add(line.Product.LeafCategory);
            }
        }

        // Materialise once: `categories` is walked twice below and an IEnumerable that is a LINQ
        // query would otherwise be evaluated twice.
        var categoryList = categories is null ? [] : categories.ToList();

        var sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categoryList)
            if (category.SensitiveInference)
                foreach (var element in category.Path)
                    sensitive.Add(element);

        var stated = SensitiveInferenceBlocklist.TermsMentionedIn(customerUtterance);

        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (explicitlyRequestedCategories is not null)
            foreach (var name in explicitlyRequestedCategories)
                if (!string.IsNullOrWhiteSpace(name))
                    requested.Add(name.Trim());

        // ── A customer who names the topic has named the PATH ────────────────────────
        //
        // The suppression arm walks a product's whole CategoryPath, so an exemption that covers
        // only the element whose words match the stated topic exempts nothing: GLX-9002 sits at
        // "Health & Personal Care > Blood pressure > Cuffs" and all three elements are tree-flagged,
        // while "blood pressure" matches only the middle one. MEASURED before this expansion:
        // Elena's stated need for a larger cuff still produced kept = 0.
        //
        // So a stated topic that matches ANY element of a category's path puts EVERY element of
        // that path in play. It is still narrow — it is the customer's own sentence that opened
        // the subtree, and a turn in which nothing was stated derives nothing here.
        //
        // ⚠ BLAST RADIUS, stated rather than discovered later. Elena's "I need a larger cuff for
        // the blood-pressure monitor I already have" opens the WHOLE Blood-pressure subtree:
        // "Health & Personal Care", "Blood pressure", "Cuffs" AND "Upper-arm monitors". The
        // monitor becomes presentable too, because she named it. MEASURED over every authored
        // prompt in this repository, exactly ONE opens anything: GalaxusDemoPrompts's
        // SensitiveStatedNeed. All five canonical persona prompts and the unsolicited
        // SensitiveInferenceProbe derive an EMPTY set, so the suppression case is untouched —
        // Elena's own demo run still drops GLX-9001, GLX-9002 and GLX-9004.
        foreach (var category in categoryList)
        {
            bool named = category.Path.Any(element =>
                SensitiveInferenceBlocklist.AllSpecialCategoryTerms(element).Any(stated.Contains));

            if (!named) continue;
            foreach (var element in category.Path) requested.Add(element);
        }

        return new GuardrailContext
        {
            ProductsBySku = productsBySku,
            User = user,
            InterestMap = interestMap,
            UserPurchaseIds = userPurchaseIds,
            GiftPurchaseIds = giftPurchaseIds,
            OwnedProductIds = ownedProductIds,
            OwnedDurableLeafCategories = ownedDurableLeaf,
            SensitiveCategoryNames = sensitive,
            ExplicitlyRequestedCategories = requested,
            SensitiveTopicsStatedInSession = stated,
            AsOfUtc = asOfUtc ?? DateTimeOffset.UtcNow,
        };
    }

    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>The result of running the pipeline: the cleaned answer, the ledger, and the verified figures.</summary>
/// <param name="Cleaned">The answer as it will be rendered.</param>
/// <param name="Ledger">Every drop, demotion and inapplicable arm.</param>
/// <param name="VerifiedPrices">
/// Price and stock read from the catalogue at render time, keyed by product id. The RENDERER
/// prints these; the model never states a price (§F.4).
/// </param>
public sealed record GuardrailOutcome(
    RecommendationSet Cleaned,
    GuardrailLedger Ledger,
    IReadOnlyDictionary<string, PriceStockSnapshot> VerifiedPrices);

/// <summary>What the guardrails decided about one <c>PresentRecommendation</c> tool call.</summary>
public enum PresentationDecision
{
    /// <summary>Show it in the primary tray.</summary>
    Accept,

    /// <summary>Show it under <c>also consider</c>, with the note the reason carries.</summary>
    Demote,

    /// <summary>Do not show it at all.</summary>
    Reject
}

/// <summary>The verdict on one presentation, with the ledger reason and a human-readable justification.</summary>
/// <param name="Decision">Accept, demote, or reject.</param>
/// <param name="Reason">One of <see cref="GuardrailReasons"/>, or <c>"ok"</c> on a clean accept.</param>
/// <param name="Detail">The justification, printed verbatim.</param>
public sealed record PresentationVerdict(PresentationDecision Decision, string Reason, string Detail)
{
    /// <summary>The reason token used when nothing was wrong.</summary>
    public const string Ok = "ok";

    /// <summary>A clean accept.</summary>
    public static PresentationVerdict Accepted { get; } =
        new(PresentationDecision.Accept, Ok, "grounded, evidenced, in stock and inside the customer's market");
}

/// <summary>
/// The ordered composition of every guardrail (§F). Mechanical, not prompted: the system prompt
/// restates these rules for cooperation, this pipeline enforces them for correctness.
/// </summary>
/// <remarks>
/// <para>Stage order, and why it is this order:</para>
/// <list type="number">
///   <item><see cref="CatalogueGroundingFilter"/> — an id that does not exist cannot be checked for anything else.</item>
///   <item><see cref="EvidenceRequiredFilter"/> — the two-sided check needs a resolved product.</item>
///   <item><see cref="SensitiveInferenceBlocklist"/> — special-category screening, before anything is priced.</item>
///   <item><see cref="ConfidenceBands"/> — routing between trays.</item>
///   <item><see cref="PriceStockRefresher"/> — LAST, so it only pays to verify survivors.</item>
/// </list>
/// <para>
/// Every stage removes rather than down-ranks. Combined with "the model may only pick from
/// retrieved candidates", a hallucinated SKU stops being statistically unlikely and becomes
/// structurally impossible.
/// </para>
/// </remarks>
public static class GuardrailPipeline
{
    /// <summary>Runs every stage in order and returns the cleaned answer with its ledger.</summary>
    /// <param name="raw">The assembled answer, before any filter.</param>
    /// <param name="context">The catalogue-derived bar every check measures against.</param>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is mis-wired — see <see cref="GuardrailContext.Validate"/>.</exception>
    public static GuardrailOutcome Apply(RecommendationSet raw, GuardrailContext context)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();

        var ledger = new GuardrailLedger();
        ledger.RecordInput(raw.PresentedCount);

        var set = CatalogueGroundingFilter.Apply(raw, context, ledger);
        set = EvidenceRequiredFilter.Apply(set, context, ledger);
        set = SensitiveInferenceBlocklist.Apply(set, context, ledger);
        set = ConfidenceBands.Apply(set, context, ledger);

        var (cleaned, verified) = PriceStockRefresher.Apply(set, context, ledger);
        ledger.RecordOutput(cleaned.PresentedCount);

        return new GuardrailOutcome(cleaned, ledger, verified);
    }

    /// <summary>
    /// Runs the abstention gate (§F.8) and then, only if it did not fire, the pipeline. The
    /// gate is a cheap structural check and belongs BEFORE the model, not inside it: when it
    /// fires, no search has run and no tokens have been spent.
    /// </summary>
    /// <remarks>
    /// An abstention is NOT automatically a pass. Scored on a case that had a right answer it
    /// is a MISS, or the gate becomes a way to score well by saying nothing.
    /// </remarks>
    /// <param name="raw">The assembled answer, before any filter.</param>
    /// <param name="context">The catalogue-derived bar.</param>
    public static GuardrailOutcome ApplyWithAbstentionGate(RecommendationSet raw, GuardrailContext context)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(context);
        context.Validate();

        if (!ShouldAbstain(context, out var reason)) return Apply(raw, context);

        var ledger = new GuardrailLedger();
        ledger.RecordInput(raw.PresentedCount);
        ledger.Note(GuardrailStage.AbstentionGate, GuardrailReasons.Abstained, "—", reason);

        foreach (var item in raw.AllPresented)
            ledger.Drop(GuardrailStage.AbstentionGate, GuardrailReasons.Abstained, item.ProductId,
                "the gate fired before any search; nothing may be presented on this turn");

        ledger.RecordOutput(0);

        var abstained = RecommendationSet.Abstain(
            reason,
            ClarifyingQuestions(context),
            context.InterestMap.Signals.Select(InterestSignalDto.From).ToList());

        return new GuardrailOutcome(abstained, ledger, new Dictionary<string, PriceStockSnapshot>(StringComparer.Ordinal));
    }

    /// <summary>
    /// The abstention condition (§F.8): fewer than
    /// <see cref="InterestMap.MinimumSignalsToProceed"/> independent signals AND no need stated
    /// in this session.
    /// </summary>
    /// <param name="context">The catalogue-derived bar, carrying the map.</param>
    /// <param name="reason">The sentence printed to the customer and written to the ledger.</param>
    public static bool ShouldAbstain(GuardrailContext context, out string reason)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.InterestMap.HasEnoughSignal || context.HasStatedNeedInSession)
        {
            reason = string.Empty;
            return false;
        }

        reason = string.Create(CultureInfo.InvariantCulture,
            $"only {context.InterestMap.IndependentSignalCount} independent interest signal(s) at or above {InterestMap.IndependentSignalThreshold:0.00} — the threshold is {InterestMap.MinimumSignalsToProceed} — and no need was stated in this session. Two questions are cheaper and more honest than a guess.");

        return true;
    }

    /// <summary>
    /// The two questions asked instead of a guess. Specific and answerable — "what are you
    /// looking for?" is not a clarifying question, it is a shrug — and the second one is the
    /// gift classifier's own question, asked out loud when the history cannot answer it.
    /// </summary>
    /// <param name="context">The catalogue-derived bar, used for the customer's name.</param>
    public static IReadOnlyList<string> ClarifyingQuestions(GuardrailContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            "What are you shopping for right now — something specific you already have in mind, or ideas around something you already do?",
            "Is this for you, or for someone else? It changes the answer more than anything else you could tell me."
        ];
    }

    /// <summary>
    /// Screens ONE <c>PresentRecommendation</c> tool call, at the moment the model makes it.
    /// Same rules as the pipeline, applied to the single sanctioned channel (§0.5 / D-1), so a
    /// phantom SKU can be refused inside the tool instead of filtered out afterwards.
    /// </summary>
    /// <param name="presented">The tool call's arguments.</param>
    /// <param name="context">The catalogue-derived bar.</param>
    /// <param name="ledger">The ledger the verdict is written to.</param>
    /// <param name="alreadyPresented">SKUs already presented in this turn; pass null to skip the duplicate check.</param>
    public static PresentationVerdict Screen(
        PresentedRecommendation presented,
        GuardrailContext context,
        GuardrailLedger ledger,
        IReadOnlySet<string>? alreadyPresented = null)
    {
        ArgumentNullException.ThrowIfNull(presented);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);

        PresentationVerdict Reject(GuardrailStage stage, string reason, string detail)
        {
            ledger.Drop(stage, reason, presented.Sku, detail);
            return new PresentationVerdict(PresentationDecision.Reject, reason, detail);
        }

        if (!context.ProductsBySku.TryGetValue(presented.Sku, out var product))
        {
            return Reject(GuardrailStage.CatalogueGrounding, GuardrailReasons.Ungrounded,
                $"'{presented.Sku}' does not resolve in the catalogue. A product id that does not exist is a hallucination, not a near miss");
        }

        if (alreadyPresented is not null && alreadyPresented.Contains(presented.Sku))
        {
            return Reject(GuardrailStage.CatalogueGrounding, GuardrailReasons.DuplicatePresentation,
                $"'{presented.Sku}' was already presented in this turn");
        }

        if (context.OwnedProductIds.Contains(presented.Sku))
        {
            return Reject(GuardrailStage.CatalogueGrounding, GuardrailReasons.AlreadyOwned,
                $"the customer already owns {product.Name}. Recommending it back to them is not a recommendation");
        }

        if (context.SuppressDurableUpgrades &&
            !product.IsConsumable &&
            context.OwnedDurableLeafCategories.Contains(product.LeafCategory))
        {
            return Reject(GuardrailStage.CatalogueGrounding, GuardrailReasons.DurableStillInHorizon,
                $"the customer already owns a {product.LeafCategory} still inside its typical service life");
        }

        // Both category arms consult the SAME exemption helper the pipeline stage uses, so the
        // tool-time screen and the after-the-fact filter can never disagree about whether the
        // customer opened a subtree themselves.
        foreach (var element in product.CategoryPath)
        {
            bool flaggedByTree = context.SensitiveCategoryNames.Contains(element);
            bool flaggedByName = SensitiveInferenceBlocklist.IsBlockedCategoryName(element);
            if (!flaggedByTree && !flaggedByName) continue;
            if (SensitiveInferenceBlocklist.IsExemptCategoryElement(element, context)) continue;

            return Reject(GuardrailStage.SensitiveInference, GuardrailReasons.SensitiveCategory,
                $"sits under \"{element}\", which may not be surfaced by inference"
              + (flaggedByTree ? " (flagged SensitiveInference in the category tree)" : ""));
        }

        // EVERY term minus the customer-raised ones, never the first match: a reason is not
        // exempt because ONE of the special-category terms in it happened to be customer-raised.
        var leakedProseTerms = SensitiveInferenceBlocklist.UnraisedSpecialCategoryTerms(
            presented.Reason, context.SensitiveTopicsStatedInSession);

        if (leakedProseTerms.Count > 0)
        {
            return Reject(GuardrailStage.SensitiveInference, GuardrailReasons.SensitiveProse,
                $"the reason says \"{string.Join("\", \"", leakedProseTerms)}\" — special categor(ies) the customer did not raise");
        }

        if (PriceStockRefresher.StatesAPrice(presented.Reason, out var priceToken))
        {
            return Reject(GuardrailStage.PriceStock, GuardrailReasons.StatedPrice,
                $"the reason states a price (\"{priceToken}\"). Price is read from the catalogue at render time and printed by the interface, never by the model");
        }

        if (!EvidenceRef.TryParse(presented.Evidence, out var citation))
        {
            return Reject(GuardrailStage.EvidenceRequired, GuardrailReasons.UnresolvableEvidence,
                $"evidence '{presented.Evidence}' is neither 'attr:<token>' nor 'review:<id>'. Plausible prose is not a citation");
        }

        if (!citation.Resolves(product))
        {
            return Reject(GuardrailStage.EvidenceRequired, GuardrailReasons.UnresolvableEvidence,
                $"evidence '{citation}' does not resolve against {product.Id}'s own catalogue record");
        }

        if (!product.IsAvailableIn(context.User.Market))
        {
            return Reject(GuardrailStage.PriceStock, GuardrailReasons.MarketUnavailable,
                $"cannot ship to {context.User.Market}");
        }

        if (product.StockUnits == 0)
        {
            var detail = presented.OutOfStock
                ? "out of stock, and the presentation says so — demoted to 'also consider' with the substitute note"
                : "out of stock, and the presentation did NOT say so — demoted, and the omission is recorded as a defect";

            ledger.Demote(GuardrailStage.PriceStock, GuardrailReasons.OutOfStock, presented.Sku, detail);
            return new PresentationVerdict(PresentationDecision.Demote, GuardrailReasons.OutOfStock, detail);
        }

        return PresentationVerdict.Accepted;
    }
}
