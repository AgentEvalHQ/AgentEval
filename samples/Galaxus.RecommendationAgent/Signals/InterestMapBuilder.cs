// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;

namespace Galaxus.RecommendationAgent.Signals;

/// <summary>
/// Everything <see cref="InterestMapBuilder"/> derived, including the things it deliberately
/// did NOT emit. Returned by <see cref="InterestMapBuilder.BuildDetailed"/>.
/// </summary>
/// <remarks>
/// The three "what was withheld" lists exist because an empty result and an inapplicable rule
/// look identical from the outside. A map with no health interest is only evidence of a
/// working blocklist if you can see that a health label was actually offered and refused.
/// </remarks>
/// <param name="Map">The map itself — what the agent and the guardrails consume.</param>
/// <param name="Classified">Every resolvable order line with its derived intent and justification.</param>
/// <param name="SuppressedDurablePurchaseIds">
/// Lines that produced no interest of their own because they are a single, unreviewed durable
/// still inside its typical horizon (§B.3, Sofia's Vitamix and Luca's cable). They still feed
/// the cross-category conjunction — that is the whole point of Nadia's power bank.
/// </param>
/// <param name="BlockedSensitiveLabels">
/// Candidate labels the inbound special-category screen refused to emit (§F.5, §0.5 / D-6).
/// </param>
/// <param name="UnresolvedProductIds">
/// Order lines whose SKU is not in the catalogue. Non-empty means the persona seed and the
/// catalogue seed have drifted; surfaced rather than silently shrinking the history.
/// </param>
public sealed record InterestMapBuildResult(
    InterestMap Map,
    IReadOnlyList<ClassifiedPurchase> Classified,
    IReadOnlyList<string> SuppressedDurablePurchaseIds,
    IReadOnlyList<string> BlockedSensitiveLabels,
    IReadOnlyList<string> UnresolvedProductIds);

/// <summary>
/// Turns a classified purchase history into the code-derived <see cref="InterestMap"/> the
/// agent searches from. Pure, deterministic C#: no model call, no randomness, no clock read
/// that the caller did not supply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Four kinds of signal, and only one of them is interesting.</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Leaf depth</b> (<see cref="InterestEvidenceKinds.CategoryDepth"/> /
///     <see cref="InterestEvidenceKinds.ReviewAuthored"/>) — the DIRECT signals. A single
///     purchase states them. Cheap, and every recommender has them.
///   </item>
///   <item>
///     <b>Cross-category conjunction</b> (<see cref="InterestEvidenceKinds.CoPurchaseContext"/>)
///     — the LATENT signal, and the reason this demo exists. No single purchase implies it;
///     it is what a COMBINATION across categories implies. A 38 L pack, a headlamp, a power
///     bank and a merino layer are four unrelated objects until you notice that all four
///     documents say <i>multi-day, on foot, dawn starts, carried weight matters</i> — and the
///     camera body in a different department says the same thing. The signal is in the
///     combination, and <b>the combination has no keyword</b>.
///   </item>
///   <item>
///     <b>Capability gap</b> (<see cref="InterestEvidenceKinds.CapabilityGap"/>) — a required
///     companion class that is absent from the whole history. Six kilos of whole beans, a
///     vacuum canister, and no grinder. A collaborative filter cannot express the thing you
///     are MISSING; it only knows what similar users bought.
///   </item>
///   <item>
///     <b>Stated in session</b> (<see cref="InterestEvidenceKinds.StatedInSession"/>) — the
///     customer said it. The ONLY kind available when personalization is off (§F.6), and a
///     conversational agent is unusually good at working from it.
///   </item>
/// </list>
/// <para>
/// <b>Be honest about the mechanism (§B.2).</b> The cross-category link is not emergent magic.
/// It is engineered in one place: every product carries <c>context:</c> / <c>trip:</c> /
/// <c>weight:</c> / <c>skill:</c> tags, this class finds the tags that bridge two or more
/// ROOT categories over two or more purchases, and the embedding document composes the same
/// tags onto a dedicated <c>Use:</c> line. The model's only creative step is turning the
/// resulting label into a search need. Everything on either side of that step is code.
/// </para>
/// <para>
/// <b>Gifts contribute nothing, anywhere.</b> A gift-classified line is excluded from leaf
/// depth, from the conjunction, and from capability gaps, and its id is carried in
/// <see cref="InterestMap.ExcludedBecauseGift"/> so the console can PRINT the exclusion. A
/// guardrail you can watch fire, on a wrong answer the audience has already predicted, is
/// worth more than one you assert.
/// </para>
/// </remarks>
public static class InterestMapBuilder
{
    // ── strength model ───────────────────────────────────────────────────────────────

    /// <summary>Floor of a leaf-depth signal.</summary>
    public const double DepthSignalBase = 0.30;

    /// <summary>Ceiling of a leaf-depth signal. A direct signal never outranks a saturated conjunction.</summary>
    public const double DepthSignalMaximum = 0.82;

    /// <summary>Weight contributed per unit of summed interest weight, capped at <see cref="DepthWeightCap"/>.</summary>
    public const double DepthPerWeight = 0.14;

    /// <summary>Where summed interest weight stops adding to a leaf-depth signal.</summary>
    public const double DepthWeightCap = 3.00;

    /// <summary>Bonus when the customer authored a review in the leaf — a stronger ownership signal than the order line.</summary>
    public const double DepthReviewBonus = 0.06;

    /// <summary>Bonus scaled by how recent the most recent line in the leaf is.</summary>
    public const double DepthRecencyBonus = 0.04;

    /// <summary>Floor of a cross-category conjunction signal.</summary>
    public const double BridgingSignalBase = 0.40;

    /// <summary>
    /// Ceiling of a cross-category conjunction signal — reached only when the conjunction
    /// covers the whole history, spans three or more root categories, and rests on four or
    /// more bridging tags. Nadia Brunner's five purchases saturate all three (§B.3).
    /// </summary>
    public const double BridgingSignalMaximum = 0.86;

    /// <summary>Minimum purchases a tag must appear on before it can bridge anything.</summary>
    public const int MinimumBridgingPurchases = 2;

    /// <summary>
    /// Minimum ROOT categories a bridging tag must span. Two is the definition of
    /// cross-category: a tag confined to one department is depth, not a conjunction.
    /// </summary>
    public const int MinimumBridgingRootCategories = 2;

    /// <summary>Floor of a capability-gap signal.</summary>
    public const double CapabilityGapBase = 0.60;

    /// <summary>Added per distinct owned SKU that requires the missing companion class.</summary>
    public const double CapabilityGapPerOwner = 0.06;

    /// <summary>Ceiling of a capability-gap signal.</summary>
    public const double CapabilityGapMaximum = 0.80;

    /// <summary>
    /// Strength of an in-session stated need. High on purpose: history explains, the request
    /// decides.
    /// </summary>
    public const double StatedNeedStrength = 0.80;

    /// <summary>Longest stated-need label kept. Longer needs are truncated so the label stays citable (§F.3).</summary>
    public const int StatedNeedLabelMaxLength = 140;

    /// <summary>
    /// How long a durable is presumed to remain in service. A single, unreviewed purchase
    /// inside this horizon does not by itself constitute an interest — otherwise a 30-month-old
    /// blender becomes "interested in blenders" and the answer is three more blenders (§B.3).
    /// </summary>
    public const int DurableUpgradeHorizonDays = 1825;

    // ── tag vocabulary (the contract with CatalogueSeed) ─────────────────────────────

    /// <summary>
    /// Tag prefixes that describe a USE CONTEXT rather than a category synonym. Only these can
    /// bridge two departments; a <c>compat:</c> tag is a compatibility fact, not a context.
    /// </summary>
    public static readonly IReadOnlyList<string> ContextTagPrefixes =
        ["context", "trip", "weight", "skill", "season", "terrain", "style", "use"];

    /// <summary>Tag prefix declaring that a product NEEDS a companion class, e.g. <c>requires:grinder</c>.</summary>
    public const string RequiresTagPrefix = "requires";

    /// <summary>Tag prefix declaring that a product IS a companion class, e.g. <c>provides:grinder</c>.</summary>
    public const string ProvidesTagPrefix = "provides";

    /// <summary>
    /// Human phrases for context-tag suffixes, used to compose a readable conjunction label.
    /// A suffix absent from this map falls back to itself with dashes turned into spaces, so
    /// the builder never fails on a tag the seed invented — it just reads less well.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ContextPhrases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["multi-day"]      = "multi-day trips",
            ["hut-to-hut"]     = "hut-to-hut routes",
            ["on-foot"]        = "carried on foot",
            ["packable"]       = "carried weight is the binding constraint",
            ["lightweight"]    = "carried weight is the binding constraint",
            ["compact"]        = "packs small",
            ["golden-hour"]    = "dawn and dusk light",
            ["dawn-start"]     = "starts before sunrise",
            ["cold-start"]     = "cold early starts",
            ["weather-sealed"] = "weather that gets on the gear",
            ["all-weather"]    = "weather that gets on the gear",
            ["landscape"]      = "landscape work",
            ["travel"]         = "travel where every 100 g counts",
            ["enthusiast"]     = "an enthusiast's standards",
            ["beginner"]       = "getting started",
            ["espresso"]       = "espresso at home",
            ["morning"]        = "the morning routine",
            ["commute"]        = "the daily commute",
            ["winter"]         = "winter conditions",
            ["summer"]         = "summer conditions",
            ["durable"]        = "gear that lasts",

            // ── The narrow use contexts authored for the Eval 02 cohort ──────────────
            // Each is on at most six of the catalogue's products, which is what lets it
            // evidence a specific interest rather than a department. They are phrased here
            // for the same reason every other suffix is: a conjunction label the customer
            // reads should be a sentence, not a tag with the dashes taken out.
            // ⚠ hut-to-hut is NOT repeated here. It is already assigned 26 lines above, and an
            // indexer-form collection initializer silently overwrites a duplicate key instead of
            // throwing the way the {"k", v} form would — so the second assignment compiled, ran and
            // was invisible. The two spellings happened to be identical; the next duplicate need
            // not be, and it would be a silent behaviour change with no diagnostic anywhere.
            ["first-light"]            = "being out and working in first light",
            ["off-grid-power"]         = "days with no socket",
            ["dialling-in"]            = "dialling a shot in by measurement",
            ["latte-art"]              = "texturing milk properly",
            ["machine-care"]           = "keeping the machine in service",
            ["whole-bean"]             = "buying beans whole",
            ["soft-water-brewing"]     = "brewing with treated water",
            ["prep-and-store"]         = "cooking ahead and storing it",
            ["dark-commute"]           = "commuting in the dark",
            ["wet-road"]               = "wet roads",
            ["winter-base-miles"]      = "training through the winter",
            ["desk-listening"]         = "listening at the desk",
            ["two-channel-room"]       = "a two-channel room",
            ["travel-listening"]       = "listening away from home",
            ["couch-co-op"]            = "four people on one sofa",
            ["handheld-away"]          = "playing handheld away from the dock",
            ["late-night-session"]     = "late sessions without waking the flat",
            ["street-walkaround"]      = "walking a city with one lens",
            ["card-to-edit"]           = "getting the day's cards onto a laptop",
            ["carry-on-only"]          = "travelling carry-on only",
            ["mountain-running"]       = "running on mountain trails",
            ["steep-ascents"]          = "steep ascents",
            ["effort-tracking"]        = "measuring the effort rather than guessing it",
            ["hand-ground"]            = "grinding by hand",
            ["weigh-every-shot"]       = "weighing every dose and yield",
            ["small-kitchen-espresso"] = "espresso in a small kitchen",
            ["long-exposure-water"]    = "long exposures on moving water",
            ["blue-hour"]              = "the blue hour",
            ["wide-vistas"]            = "wide vistas",
            ["multi-room-music"]       = "music in more than one room",
            ["late-evening-volume"]    = "listening late without the volume",
            ["dock-and-play"]          = "docking to whatever screen is there",
            ["bikepacking"]            = "bikepacking",
            ["self-supported"]         = "going out self-supported",
            ["all-day-riding"]         = "all-day rides",
        };

    /// <summary>How many phrases a conjunction label may name before it stops being citable.</summary>
    public const int MaximumLabelPhrases = 3;

    /// <summary>
    /// The largest share of the catalogue that may carry a context-tag suffix for that suffix to
    /// bridge anything. A tag most of the catalogue carries is a stopword, not a context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>CHOSEN, not measured — and the measurement that forced it is worth writing down.</b>
    /// Bridging tags were ordered by purchase count alone and the label was cut at
    /// <see cref="MaximumLabelPhrases"/>. On this catalogue <c>skill:enthusiast</c> is on
    /// <b>54 of the then 76 products (71.1%)</b>, so it took slot 1 of 3 in every conjunction label and
    /// pushed <c>weight:carried</c> — which the persona notes call "the binding constraint" —
    /// out of the label entirely. Nadia's headline label read <i>"an enthusiast's standards,
    /// multi-day trips, starts before sunrise"</i> and retrieved a bike multi-tool at #1 and SD
    /// cards at #2. Marco's and Sofia's read <i>"an enthusiast's standards, home bar"</i> and
    /// produced an all-zero concept vector.
    /// </para>
    /// <para>
    /// The measured shares of every context-tag suffix in this catalogue, which is what makes
    /// the threshold auditable rather than magic: enthusiast 71.1%, packable 40.8%, beginner
    /// 28.9%, multi-day 26.3%, dawn-start 22.4%, home-bar 18.4%, living-room 14.5%, carried /
    /// day / golden-hour / travel 13.2%, and eleven more below 10%. A ceiling of "more than half
    /// the catalogue" removes exactly one suffix — the one that is on nearly three quarters of it.
    /// </para>
    /// <para>
    /// ⚠ <b>This is NOT the same number as the eval lane's latent-gold specificity cap (0.25), and
    /// the difference is deliberate.</b> That cap protects a scoring DENOMINATOR from being
    /// satisfiable by any product at all, so it is tight. This one protects a customer-facing
    /// LABEL from being led by a word that distinguishes nothing. Setting this one to 0.25 would
    /// also delete <c>multi-day</c> and <c>packable</c> — the two tags the cross-category
    /// demonstration is actually built on — so aligning the numbers would break the mechanism to
    /// make two constants match. Both are printed where they are used.
    /// </para>
    /// </remarks>
    public const double ContextTagMaximumCatalogueShare = 0.50;

    /// <summary>
    /// Builds the map. Convenience wrapper over <see cref="BuildDetailed"/> for callers that
    /// do not need the withheld-signal lists.
    /// </summary>
    /// <param name="user">The customer. <see cref="User.PersonalizationEnabled"/> gates everything.</param>
    /// <param name="history">Every order line; lines belonging to another customer are ignored.</param>
    /// <param name="productsBySku">The catalogue, keyed by <see cref="Product.Id"/>.</param>
    /// <param name="statedNeeds">What the customer said in this session, if anything.</param>
    /// <param name="asOf">The demo clock's "today". Defaults to <see cref="DateTime.UtcNow"/>'s date.</param>
    /// <param name="sensitiveCategoryNames">Category names flagged <see cref="Category.SensitiveInference"/>.</param>
    public static InterestMap Build(
        User user,
        IReadOnlyList<Purchase> history,
        IReadOnlyDictionary<string, Product> productsBySku,
        IReadOnlyList<string>? statedNeeds = null,
        DateOnly? asOf = null,
        IReadOnlySet<string>? sensitiveCategoryNames = null)
        => BuildDetailed(user, history, productsBySku, statedNeeds, asOf, sensitiveCategoryNames).Map;

    /// <summary>
    /// Builds the map and reports everything that was withheld and why.
    /// </summary>
    /// <param name="user">The customer.</param>
    /// <param name="history">
    /// Every order line. <b>Never read at all when <see cref="User.PersonalizationEnabled"/> is
    /// false</b> — data minimisation as a control-flow property, not a promise (§F.6).
    /// </param>
    /// <param name="productsBySku">The catalogue, keyed by <see cref="Product.Id"/>.</param>
    /// <param name="statedNeeds">What the customer said in this session, if anything.</param>
    /// <param name="asOf">The demo clock's "today".</param>
    /// <param name="sensitiveCategoryNames">Category names flagged <see cref="Category.SensitiveInference"/>.</param>
    public static InterestMapBuildResult BuildDetailed(
        User user,
        IReadOnlyList<Purchase> history,
        IReadOnlyDictionary<string, Product> productsBySku,
        IReadOnlyList<string>? statedNeeds = null,
        DateOnly? asOf = null,
        IReadOnlySet<string>? sensitiveCategoryNames = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(productsBySku);

        var today     = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var sensitive = sensitiveCategoryNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked   = new List<string>();

        // ── §F.6 — the opt-out path. History is not filtered, minimised or summarised: it
        //    is not read. The only signals available are the ones the customer just gave us.
        if (!user.PersonalizationEnabled)
        {
            var statedOnly = BuildStatedSignals(statedNeeds);
            var optedOutMap = statedOnly.Count == 0
                ? InterestMap.Empty(user.Id, personalizationEnabled: false)
                : new InterestMap(user.Id, statedOnly, [], [], PersonalizationEnabled: false);

            return new InterestMapBuildResult(optedOutMap, [], [], blocked, []);
        }

        var owned = history.Where(p => string.Equals(p.UserId, user.Id, StringComparison.Ordinal)).ToList();
        var unresolved = PurchaseIntentClassifier.UnresolvedProductIds(owned, productsBySku);
        var classified = PurchaseIntentClassifier.ClassifyAll(owned, productsBySku, today);

        var excludedGift = classified.Where(c => c.IsGift)
                                     .Select(c => c.PurchaseId)
                                     .OrderBy(id => id, StringComparer.Ordinal)
                                     .ToList();

        var routedToReplenishment = classified.Where(c => c.IsReplenishment)
                                              .Select(c => c.PurchaseId)
                                              .OrderBy(id => id, StringComparer.Ordinal)
                                              .ToList();

        var interestBearing = classified.Where(c => c.CountsTowardInterests).ToList();
        var nonGift         = classified.Where(c => !c.IsGift).ToList();

        var suppressedDurables = new List<string>();
        var candidates = new List<InterestSignal>();

        candidates.AddRange(BuildDepthSignals(interestBearing, today, suppressedDurables));
        candidates.AddRange(BuildConjunctionSignals(interestBearing, [.. productsBySku.Values]));
        candidates.AddRange(BuildCapabilityGapSignals(nonGift));

        // ── inbound special-category screen (§F.5 first direction, §0.5 / D-6) ──────
        var emitted = new List<InterestSignal>(candidates.Count);
        foreach (var signal in candidates)
        {
            // EVERY reason, not the first one. There is no exemption on the INBOUND screen, so
            // the verdict is the same either way — but a withheld-label report that names one of
            // three reasons understates what the label said, and this list is the only evidence
            // that the screen fired at all.
            var labelTerms = SensitiveInferenceBlocklist.AllBlockedLabelTerms(signal.Label);
            if (labelTerms.Count > 0)
            {
                blocked.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{signal.Label} (matched \"{string.Join("\", \"", labelTerms.OrderBy(t => t, StringComparer.Ordinal))}\", kind {signal.EvidenceKind})"));
                continue;
            }

            if (MentionsSensitiveCategory(signal, sensitive, out var category))
            {
                blocked.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{signal.Label} (category \"{category}\" is flagged SensitiveInference, kind {signal.EvidenceKind})"));
                continue;
            }

            emitted.Add(signal);
        }

        // Stated needs are NOT screened: the customer raised the topic, so it is a served
        // request rather than an inference. Suppression is about unsolicited inference —
        // an agent that blanket-suppresses to pass the inference case fails the stated-need
        // case, and that pairing is what makes the control a test rather than a reflex.
        emitted.AddRange(BuildStatedSignals(statedNeeds));

        var ordered = emitted
            .OrderByDescending(s => s.Strength)
            .ThenBy(s => s.Label, StringComparer.Ordinal)
            .ToList();

        var map = new InterestMap(user.Id, ordered, excludedGift, routedToReplenishment, PersonalizationEnabled: true);

        suppressedDurables.Sort(StringComparer.Ordinal);
        blocked.Sort(StringComparer.Ordinal);

        return new InterestMapBuildResult(map, classified, suppressedDurables, blocked, unresolved);
    }

    // ── (1) leaf depth — the DIRECT signals ──────────────────────────────────────────

    private static List<InterestSignal> BuildDepthSignals(
        IReadOnlyList<ClassifiedPurchase> interestBearing,
        DateOnly today,
        List<string> suppressedDurables)
    {
        var signals = new List<InterestSignal>();

        var byLeaf = interestBearing
            .GroupBy(c => c.Product.LeafCategory, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byLeaf)
        {
            var lines = group.OrderBy(c => c.Purchase.PurchasedOn)
                             .ThenBy(c => c.PurchaseId, StringComparer.Ordinal)
                             .ToList();

            // The durable-churn rule (§B.3). One unreviewed, non-consumable purchase still
            // inside its service life is not an interest in that leaf — it is a thing she
            // already owns. It still feeds the conjunction below, which is exactly how
            // Nadia's power bank contributes without becoming "interested in power banks".
            if (lines.Count == 1 &&
                !lines[0].Purchase.HasOwnReview &&
                !lines[0].Product.IsConsumable &&
                lines[0].Purchase.DaysSince(today) < DurableUpgradeHorizonDays)
            {
                suppressedDurables.Add(lines[0].PurchaseId);
                continue;
            }

            double weight   = Math.Min(DepthWeightCap, lines.Sum(l => l.InterestWeight));
            bool   reviewed = lines.Any(l => l.Purchase.HasOwnReview);
            double recency  = RecencyFactor(lines.Max(l => l.Purchase.PurchasedOn), today);

            double strength = Clamp(
                DepthSignalBase
                + DepthPerWeight   * weight
                + DepthReviewBonus * (reviewed ? 1.0 : 0.0)
                + DepthRecencyBonus * recency,
                DepthSignalBase,
                DepthSignalMaximum);

            var kind = lines.Count == 1 && reviewed
                ? InterestEvidenceKinds.ReviewAuthored
                : InterestEvidenceKinds.CategoryDepth;

            signals.Add(new InterestSignal(
                lines[^1].Product.LeafCategory,
                strength,
                lines.Select(l => l.PurchaseId).ToList(),
                kind));
        }

        return signals;
    }

    // ── (2) cross-category conjunction — the LATENT signal ───────────────────────────

    private sealed class TagBridge
    {
        public required string Suffix { get; init; }
        public required HashSet<string> PurchaseIds { get; init; }
        public required HashSet<string> RootCategories { get; init; }
    }

    private sealed class Component
    {
        public HashSet<string> Suffixes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PurchaseIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RootCategories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SuffixWeight { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The share of <paramref name="catalogueProducts"/> carrying a context tag with this suffix.
    /// </summary>
    /// <param name="suffix">A context-tag suffix, e.g. <c>enthusiast</c>.</param>
    /// <param name="catalogueProducts">Every product in the catalogue.</param>
    public static double ContextTagCatalogueShare(string suffix, IReadOnlyCollection<Product> catalogueProducts)
    {
        ArgumentNullException.ThrowIfNull(catalogueProducts);
        if (catalogueProducts.Count == 0 || string.IsNullOrEmpty(suffix)) return 0.0;

        int carriers = 0;
        foreach (var product in catalogueProducts)
        {
            foreach (var tag in product.Tags)
            {
                if (!TrySplitTag(tag, out var prefix, out var tagSuffix)) continue;
                if (!ContextTagPrefixes.Contains(prefix, StringComparer.Ordinal)) continue;
                if (!string.Equals(tagSuffix, suffix, StringComparison.Ordinal)) continue;
                carriers++;
                break;
            }
        }

        return carriers / (double)catalogueProducts.Count;
    }

    /// <summary>
    /// The context-tag suffixes this catalogue disqualifies as bridges, with their measured share.
    /// Exposed so the choice of <see cref="ContextTagMaximumCatalogueShare"/> can be PRINTED next
    /// to what it removed rather than buried in a filter.
    /// </summary>
    /// <param name="catalogueProducts">Every product in the catalogue.</param>
    public static IReadOnlyList<(string Suffix, double Share)> StopwordContextTags(
        IReadOnlyCollection<Product> catalogueProducts)
    {
        ArgumentNullException.ThrowIfNull(catalogueProducts);

        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var product in catalogueProducts)
            foreach (var tag in product.Tags)
                if (TrySplitTag(tag, out var prefix, out var suffix)
                    && ContextTagPrefixes.Contains(prefix, StringComparer.Ordinal))
                    suffixes.Add(suffix);

        var dropped = new List<(string Suffix, double Share)>();
        foreach (var suffix in suffixes)
        {
            double share = ContextTagCatalogueShare(suffix, catalogueProducts);
            if (share > ContextTagMaximumCatalogueShare) dropped.Add((suffix, share));
        }

        dropped.Sort((left, right) =>
        {
            int byShare = right.Share.CompareTo(left.Share);
            return byShare != 0 ? byShare : string.CompareOrdinal(left.Suffix, right.Suffix);
        });

        return dropped;
    }

    private static List<InterestSignal> BuildConjunctionSignals(
        IReadOnlyList<ClassifiedPurchase> interestBearing,
        IReadOnlyCollection<Product> catalogueProducts)
    {
        var signals = new List<InterestSignal>();
        if (interestBearing.Count < MinimumBridgingPurchases) return signals;

        // tag suffix -> the purchases and root categories it appears on
        var bridges = new Dictionary<string, TagBridge>(StringComparer.Ordinal);
        var shareCache = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var line in interestBearing)
        {
            foreach (var tag in line.Product.Tags)
            {
                if (!TrySplitTag(tag, out var prefix, out var suffix)) continue;
                if (!ContextTagPrefixes.Contains(prefix, StringComparer.Ordinal)) continue;

                // ── R2-SPECIFICITY, agent side (see ContextTagMaximumCatalogueShare) ──
                // A tag most of the catalogue carries cannot bridge two departments — everything
                // is in both. Filtering here rather than at label composition is deliberate: a
                // stopword must not contribute to the component's density or strength either,
                // or the label would merely hide evidence the strength still counted.
                if (!shareCache.TryGetValue(suffix, out var share))
                {
                    share = ContextTagCatalogueShare(suffix, catalogueProducts);
                    shareCache[suffix] = share;
                }

                if (share > ContextTagMaximumCatalogueShare) continue;

                if (!bridges.TryGetValue(suffix, out var bridge))
                {
                    bridge = new TagBridge
                    {
                        Suffix = suffix,
                        PurchaseIds = new HashSet<string>(StringComparer.Ordinal),
                        RootCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    };
                    bridges[suffix] = bridge;
                }

                bridge.PurchaseIds.Add(line.PurchaseId);
                bridge.RootCategories.Add(line.Product.RootCategory);
            }
        }

        var bridging = bridges.Values
            .Where(b => b.PurchaseIds.Count >= MinimumBridgingPurchases &&
                        b.RootCategories.Count >= MinimumBridgingRootCategories)
            .OrderByDescending(b => b.PurchaseIds.Count)
            .ThenBy(b => b.Suffix, StringComparer.Ordinal)
            .ToList();

        if (bridging.Count == 0) return signals;

        // Merge bridging tags that share at least one purchase — two tags describing the same
        // trip are one interest, not two.
        var components = new List<Component>();
        foreach (var bridge in bridging)
        {
            var touching = components.Where(c => c.PurchaseIds.Overlaps(bridge.PurchaseIds)).ToList();

            Component target;
            if (touching.Count == 0)
            {
                target = new Component();
                components.Add(target);
            }
            else
            {
                target = touching[0];
                for (int i = 1; i < touching.Count; i++)
                {
                    target.Suffixes.UnionWith(touching[i].Suffixes);
                    target.PurchaseIds.UnionWith(touching[i].PurchaseIds);
                    target.RootCategories.UnionWith(touching[i].RootCategories);
                    foreach (var (k, v) in touching[i].SuffixWeight) target.SuffixWeight[k] = v;
                    components.Remove(touching[i]);
                }
            }

            target.Suffixes.Add(bridge.Suffix);
            target.PurchaseIds.UnionWith(bridge.PurchaseIds);
            target.RootCategories.UnionWith(bridge.RootCategories);
            target.SuffixWeight[bridge.Suffix] = bridge.PurchaseIds.Count;
        }

        foreach (var component in components)
        {
            if (component.RootCategories.Count < MinimumBridgingRootCategories) continue;

            double coverage = interestBearing.Count == 0
                ? 0.0
                : Math.Min(1.0, component.PurchaseIds.Count / (double)interestBearing.Count);
            double span    = Math.Min(1.0, (component.RootCategories.Count - 1) / 2.0);
            double density = Math.Min(1.0, component.Suffixes.Count / 4.0);

            double strength = Clamp(
                BridgingSignalBase + 0.20 * coverage + 0.16 * span + 0.10 * density,
                BridgingSignalBase,
                BridgingSignalMaximum);

            signals.Add(new InterestSignal(
                ComposeConjunctionLabel(component),
                strength,
                component.PurchaseIds.OrderBy(id => id, StringComparer.Ordinal).ToList(),
                InterestEvidenceKinds.CoPurchaseContext));
        }

        return signals;
    }

    private static string ComposeConjunctionLabel(Component component)
    {
        var phrases = new List<string>();

        var ordered = component.Suffixes
            .OrderByDescending(s => component.SuffixWeight.TryGetValue(s, out var w) ? w : 0)
            .ThenBy(s => s, StringComparer.Ordinal);

        foreach (var suffix in ordered)
        {
            var phrase = ContextPhrases.TryGetValue(suffix, out var known)
                ? known
                : suffix.Replace('-', ' ');

            if (phrases.Contains(phrase, StringComparer.Ordinal)) continue;
            phrases.Add(phrase);
            if (phrases.Count == MaximumLabelPhrases) break;
        }

        return string.Join(", ", phrases);
    }

    // ── (3) capability gap — the thing you are MISSING ───────────────────────────────

    private static List<InterestSignal> BuildCapabilityGapSignals(IReadOnlyList<ClassifiedPurchase> nonGift)
    {
        var signals = new List<InterestSignal>();
        if (nonGift.Count == 0) return signals;

        var required = new Dictionary<string, List<ClassifiedPurchase>>(StringComparer.Ordinal);

        foreach (var line in nonGift)
        {
            foreach (var tag in line.Product.Tags)
            {
                if (!TrySplitTag(tag, out var prefix, out var suffix)) continue;
                if (!string.Equals(prefix, RequiresTagPrefix, StringComparison.Ordinal)) continue;

                if (!required.TryGetValue(suffix, out var owners))
                {
                    owners = [];
                    required[suffix] = owners;
                }

                owners.Add(line);
            }
        }

        foreach (var (companionClass, owners) in required.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (nonGift.Any(line => Satisfies(line.Product, companionClass))) continue;

            var distinctOwners = owners
                .GroupBy(o => o.Product.Id, StringComparer.Ordinal)
                .Select(g => g.OrderBy(o => o.Purchase.PurchasedOn).First())
                .OrderBy(o => o.Purchase.PurchasedOn)
                .ThenBy(o => o.PurchaseId, StringComparer.Ordinal)
                .ToList();

            if (distinctOwners.Count == 0) continue;

            double strength = Clamp(
                CapabilityGapBase + CapabilityGapPerOwner * distinctOwners.Count,
                CapabilityGapBase,
                CapabilityGapMaximum);

            signals.Add(new InterestSignal(
                ComposeGapLabel(distinctOwners, companionClass),
                strength,
                owners.Select(o => o.PurchaseId).Distinct(StringComparer.Ordinal)
                      .OrderBy(id => id, StringComparer.Ordinal).ToList(),
                InterestEvidenceKinds.CapabilityGap));
        }

        return signals;
    }

    /// <summary>
    /// True when <paramref name="product"/> IS the companion class — either because it says so
    /// with a <c>provides:</c> tag, or because its leaf category names the class. The category
    /// fallback exists so a seed that forgets one tag degrades into a missed gap rather than
    /// into a false one; recommending a grinder to someone who owns a grinder is the worse
    /// failure of the two.
    /// </summary>
    private static bool Satisfies(Product product, string companionClass)
    {
        foreach (var tag in product.Tags)
        {
            if (!TrySplitTag(tag, out var prefix, out var suffix)) continue;
            if (!string.Equals(prefix, ProvidesTagPrefix, StringComparison.Ordinal)) continue;
            if (string.Equals(suffix, companionClass, StringComparison.Ordinal)) return true;
        }

        var leaf = Product.NormalizeAttributeToken(product.LeafCategory);
        return string.Equals(leaf, companionClass, StringComparison.Ordinal)
            || string.Equals(Singularize(leaf), Singularize(companionClass), StringComparison.Ordinal);
    }

    private static string ComposeGapLabel(IReadOnlyList<ClassifiedPurchase> owners, string companionClass)
    {
        var names = owners.Select(o => o.Product.LeafCategory.ToLowerInvariant())
                          .Distinct(StringComparer.Ordinal)
                          .ToList();

        var companion = companionClass.Replace('-', ' ');

        return names.Count switch
        {
            0 => $"owns nothing that needs a {companion}",
            1 => $"owns {names[0]} but no {companion}",
            2 => $"owns {names[0]} and {names[1]} but no {companion}",
            _ => string.Create(CultureInfo.InvariantCulture,
                    $"owns {names[0]}, {names[1]} and {names.Count - 2} more but no {companion}")
        };
    }

    // ── (4) stated in session ────────────────────────────────────────────────────────

    private static List<InterestSignal> BuildStatedSignals(IReadOnlyList<string>? statedNeeds)
    {
        var signals = new List<InterestSignal>();
        if (statedNeeds is null) return signals;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var need in statedNeeds)
        {
            if (string.IsNullOrWhiteSpace(need)) continue;

            var label = need.Trim();
            if (label.Length > StatedNeedLabelMaxLength) label = label[..StatedNeedLabelMaxLength].TrimEnd();
            if (!seen.Add(label)) continue;

            // The ONE signal kind with no purchase evidence, and necessarily so: the evidence
            // is the sentence the customer just typed. EvidenceRequiredFilter knows this and
            // requires such a recommendation to cite NO purchase ids — citing history for a
            // stated need would be a fabrication, and under §F.6 there is no history to cite.
            signals.Add(new InterestSignal(label, StatedNeedStrength, [], InterestEvidenceKinds.StatedInSession));
        }

        return signals;
    }

    // ── shared helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits <c>"trip:multi-day"</c> into <c>"trip"</c> and <c>"multi-day"</c>, both
    /// normalised by <see cref="Product.NormalizeAttributeToken"/> so the tag vocabulary and
    /// the evidence vocabulary can never disagree on casing or spacing.
    /// </summary>
    /// <param name="tag">A raw tag from <see cref="Product.Tags"/>.</param>
    /// <param name="prefix">The part before the first colon.</param>
    /// <param name="suffix">The part after the first colon.</param>
    /// <returns>False for a tag with no colon, or with an empty half.</returns>
    public static bool TrySplitTag(string? tag, out string prefix, out string suffix)
    {
        prefix = string.Empty;
        suffix = string.Empty;
        if (string.IsNullOrWhiteSpace(tag)) return false;

        int colon = tag.IndexOf(':');
        if (colon <= 0 || colon >= tag.Length - 1) return false;

        prefix = Product.NormalizeAttributeToken(tag[..colon]);
        suffix = Product.NormalizeAttributeToken(tag[(colon + 1)..]);

        return prefix.Length > 0 && suffix.Length > 0;
    }

    private static bool MentionsSensitiveCategory(InterestSignal signal, IReadOnlySet<string> sensitive, out string? matched)
    {
        matched = null;
        if (sensitive.Count == 0) return false;

        foreach (var name in sensitive)
        {
            if (!signal.Label.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            matched = name;
            return true;
        }

        return false;
    }

    private static double RecencyFactor(DateOnly mostRecent, DateOnly today)
    {
        int days = Math.Max(0, today.DayNumber - mostRecent.DayNumber);
        return days switch
        {
            <= 180 => 1.00,
            <= 540 => 0.50,
            _      => 0.25
        };
    }

    private static string Singularize(string token) =>
        token.Length > 3 && token.EndsWith('s') ? token[..^1] : token;

    private static double Clamp(double value, double min, double max) =>
        Math.Round(Math.Clamp(value, min, max), 2, MidpointRounding.AwayFromZero);
}
