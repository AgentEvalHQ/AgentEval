// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Evals.Cases;

/// <summary>
/// The gold interest map for one persona: what a perfect answer would have to reach.
/// </summary>
/// <param name="Manifest">
/// Leaf categories with two or more eligible purchases — what a category counter already sees.
/// Reported as a regression channel only.
/// </param>
/// <param name="Latent">
/// Attribute tokens shared by two or more eligible purchases spanning two or more leaf categories.
/// The headline.
/// </param>
/// <param name="OwnedCategories">Leaf categories the customer has already bought from.</param>
/// <param name="ExcludedPurchaseIds">Purchase ids R3 removed, with gift lines first.</param>
public sealed record GoldInterestMap(
    IReadOnlySet<string> Manifest,
    IReadOnlySet<string> Latent,
    IReadOnlySet<string> OwnedCategories,
    IReadOnlyList<string> ExcludedPurchaseIds)
{
    /// <summary>True when R2 found nothing — the persona must then be excluded, not scored.</summary>
    public bool LatentIsEmpty => Latent.Count == 0;
}

/// <summary>
/// Derives the gold interest map from the corpus by the stated rules R3 / R1 / R2 — never by
/// hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why derived and not typed.</b> Hand-picked gold is gold picked to flatter, and its chance
/// floor cannot be computed. Deriving it mechanically makes the metric reproducible and makes the
/// random-draw floor calculable. It does <b>not</b> remove the circularity: we authored the
/// purchase histories and we authored the attribute tags the rule reads, so Eval 02 measures
/// whether the agent can recover an inference we planted. That is a capability test on a
/// constructed world, and §E of the design says so at length.
/// </para>
/// <para>
/// <b>R3 goes through the classifier, not through a flag (design §0.5 / A-3).</b> The eval design
/// reads <c>Purchase.IsGift</c>; the agent lane deliberately omits that field so gift-ness has to
/// be DERIVED from four observables — wrapped, alternate address, gift message, no review. So the
/// exclusion here runs <c>PurchaseIntentClassifier.ClassifyAll</c> and drops
/// <c>Intent == PurchaseIntent.Gift</c>. One consequence worth stating: the gold now depends on a
/// piece of the system under test. It is a deterministic, code-owned rule with no model in it and
/// it is the same rule the agent's own interest map uses — but if the classifier is wrong, the
/// gold is wrong in the same direction, and that is a real limitation rather than a nitpick.
/// </para>
/// <para>
/// <b>The known weakness of R2, named (design §0.5 / D-4).</b> Latent gold is "an attribute token
/// shared by two or more purchases spanning two or more categories", and the retrieval index
/// embeds those same <c>context:</c> / <c>trip:</c> / <c>weight:</c> tags. Gold and index derive
/// from the same field, so latent coverage may be scoring whether the system can join products on
/// a tag it was indexed by — a SELECT, not an inference. That is why Eval 02 runs the tag-join
/// baseline (Arm D) rather than only the random one: if a two-line join scores as well as the
/// agent, the headline metric is measuring the join.
/// </para>
/// </remarks>
public static class InterestMapGold
{
    /// <summary>Minimum eligible purchases in a leaf category for it to be manifest gold (R1).</summary>
    public const int ManifestMinimumPurchases = 2;

    /// <summary>Minimum eligible purchases carrying a token for it to be latent gold (R2).</summary>
    public const int LatentMinimumPurchases = 2;

    /// <summary>Minimum distinct leaf categories those purchases must span (R2).</summary>
    public const int LatentMinimumCategories = 2;

    /// <summary>
    /// R2-SPECIFICITY, expressed as a CARRIER COUNT: the largest number of catalogue products that
    /// may carry a token for it to count as a latent interest. A token more than this many products
    /// carry is a stopword, not an interest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stated as a count rather than as a share, because a share silently loosens when the
    /// catalogue grows.</b> The previous rule was "at most a quarter of the catalogue", which on a
    /// 76-SKU corpus meant 19 products and on the current 99-SKU corpus would mean 24. What the
    /// rule is trying to say has nothing to do with the size of the catalogue: it is that a token
    /// carried by a handful of products can evidence a specific interest and a token carried by two
    /// dozen cannot. Six is the number, and <see cref="LatentMaximumCatalogueShare"/> derives the
    /// share from it so <see cref="IsSpecificEnough"/> keeps its old shape.
    /// </para>
    /// <para>
    /// <b>Why six and not four or nineteen.</b> Every latent interest in this corpus is authored to
    /// the same structure — two of the customer's own purchases spanning two leaf categories, plus
    /// two or three products in leaves the customer does not own — which is four or five carriers.
    /// Six leaves exactly one slot of slack, so a later edit that adds one more carrier to a token
    /// degrades the floor slightly instead of silently deleting the token from the gold set. It is
    /// CHOSEN on that structural argument, not tuned: the floor it produces is printed on every run.
    /// </para>
    /// </remarks>
    public const int LatentMaximumCarriers = 6;

    /// <summary>
    /// R2-SPECIFICITY as the share <see cref="IsSpecificEnough"/> compares against — derived from
    /// <see cref="LatentMaximumCarriers"/> and the live catalogue size, never typed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>What this replaced, and why the old value could not simply be kept.</b> The constant was
    /// <c>0.25</c>. MEASURED under it on the five-persona corpus: Marco's and Sofia's latent gold
    /// sets were both exactly <c>{home-bar}</c> — a token 14 of 76 products carried — so their
    /// "coverage" was one Bernoulli trial each, their random-5 floors were 0.569 and 0.581 against
    /// an advisory ceiling of 0.50, and the forced choice was pinned at chance because two personas
    /// with IDENTICAL gold sets can never be strictly separated. The cap alone was never the fix:
    /// tightening it without authoring narrow tags first would have emptied both gold sets and
    /// dropped the analysis set from three personas to one. The catalogue was extended FIRST
    /// (<c>CatalogueSeed.ExtensionProducts</c> and the narrow <c>context:</c> vocabulary), and the
    /// cap tightened second — the order Docs/MEASUREMENT_STATUS.md §4 specifies.
    /// </para>
    /// <para>
    /// ⚠ This is NOT <c>InterestMapBuilder.ContextTagMaximumCatalogueShare</c> (0.50) and the two
    /// must not be aligned. That one protects a customer-facing LABEL from being led by a word that
    /// distinguishes nothing; setting it this tight would delete <c>multi-day</c> and
    /// <c>packable</c>, which are the two tags the cross-category demonstration is built on.
    /// </para>
    /// </remarks>
    public static double LatentMaximumCatalogueShare =>
        Catalogue.Default.All.Count == 0
            ? 1.0
            : LatentMaximumCarriers / (double)Catalogue.Default.All.Count;

    /// <summary>
    /// Attribute-token prefixes that are category restatements rather than use context. R2
    /// excludes them, because a token that names the category cannot evidence a CROSS-category
    /// inference — it would make the metric trivially satisfiable by staying put.
    /// </summary>
    public static IReadOnlyList<string> ExcludedTokenPrefixes { get; } =
        ["category:", "compat:", "brand:", RequiresTagPrefix, ProvidesTagPrefix];

    /// <summary>The <c>requires:</c> tag prefix consumed by the capability-gap signal builder.</summary>
    public const string RequiresTagPrefix = "requires:";

    /// <summary>The <c>provides:</c> tag prefix consumed by the capability-gap signal builder.</summary>
    public const string ProvidesTagPrefix = "provides:";

    /// <summary>
    /// The attribute tokens of one product that are eligible to become latent gold, and the same
    /// set a presented product is credited with SERVING. One function, so the two sides of the
    /// metric cannot disagree about what a latent interest is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Latent gold is the USE-CONTEXT vocabulary and nothing else</b> — the four prefixes in
    /// <see cref="EmbeddingDocument.UseTagPrefixes"/> (<c>context:</c>, <c>trip:</c>,
    /// <c>weight:</c>, <c>skill:</c>), each reduced to ONE canonical spelling: the suffix.
    /// </para>
    /// <para>
    /// ⚠ <b>What this replaced, and why it was wrong.</b> The previous version started from
    /// <see cref="Product.Attributes"/>, which emits five sources per product — the whole tag, the
    /// tag's suffix, each spec key, each spec value and each <c>key=value</c> pair. Three
    /// consequences, all MEASURED on this corpus:
    /// </para>
    /// <list type="number">
    ///   <item><description><b>One authored fact counted twice.</b> <c>context:home-bar</c> entered
    ///   gold as <c>context:home-bar</c> AND as <c>home-bar</c>. Marco's ENTIRE latent gold was
    ///   that one tag, spelled two ways, and his "coverage 1.000 (2/2)" was a single Bernoulli
    ///   trial printed as a fraction with denominator 2.</description></item>
    ///   <item><description><b>Spec KEYS were scored as interests.</b> <c>capacity</c>,
    ///   <c>max-output</c> and <c>pack-size</c> are the names of specification fields, not things
    ///   a customer is interested in.</description></item>
    ///   <item><description><b>A bare boolean was scored as an interest.</b> <c>consumable:true</c>
    ///   put the literal <c>true</c> into Sofia's latent gold.</description></item>
    /// </list>
    /// <para>
    /// The old code defended against the whole/suffix duplication for EXCLUDED prefixes only — its
    /// own remarks said so — which left every non-excluded colon tag doubled. Restricting to the
    /// use-tag vocabulary fixes all three at once, and it restricts the metric to the vocabulary
    /// the design actually claims the inference runs on: the <c>Use:</c> line of the embedding
    /// document is composed from exactly these prefixes.
    /// </para>
    /// <para>
    /// <see cref="ExcludedTokenPrefixes"/> remains asserted below rather than deleted: it is the
    /// statement of WHY <c>compat:</c>, <c>category:</c>, <c>requires:</c> and <c>provides:</c>
    /// may never become gold, and a future edit to <see cref="EmbeddingDocument.UseTagPrefixes"/>
    /// that added one of them would then fail loudly instead of quietly grading the capability-gap
    /// mechanism against a bar authored for the capability-gap mechanism.
    /// </para>
    /// </remarks>
    /// <param name="product">The catalogue record.</param>
    /// <exception cref="InvalidOperationException">A use-tag prefix is also an excluded prefix.</exception>
    public static IReadOnlySet<string> EligibleTokens(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var tokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tag in product.Tags)
        {
            int colon = tag.IndexOf(':');
            if (colon <= 0 || colon >= tag.Length - 1) continue;

            var prefix = tag[..(colon + 1)];
            if (!EmbeddingDocument.UseTagPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase)) continue;

            if (ExcludedTokenPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"'{prefix}' is both a use-tag prefix and an excluded prefix. Latent gold would then be derived " +
                    "from the same tag a mechanism under test reads, which is the bar and the artifact moving together.");
            }

            // The SUFFIX is the canonical spelling. One authored fact, one token.
            var suffix = Product.NormalizeAttributeToken(tag[(colon + 1)..]);
            if (suffix.Length == 0) continue;
            if (IsExcludedToken(suffix)) continue;
            if (IsBooleanLiteral(suffix)) continue;

            tokens.Add(suffix);
        }

        return tokens;
    }

    /// <summary>
    /// True when a token is a bare boolean rather than an interest. <c>consumable:true</c> put
    /// the literal <c>true</c> into Sofia's scored gold; nobody is interested in <i>true</i>.
    /// </summary>
    /// <param name="token">A normalised attribute token.</param>
    public static bool IsBooleanLiteral(string token) =>
        string.Equals(token, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives the gold map for one customer.
    /// </summary>
    /// <param name="userId">A customer id.</param>
    /// <param name="asOf">Clock for the intent classifier. Defaults to the demo clock.</param>
    /// <exception cref="ArgumentException">The customer id is not authored.</exception>
    public static GoldInterestMap Derive(string userId, DateOnly? asOf = null)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Require(userId);
        var today = asOf ?? Catalogue.DemoToday;

        // ── R3 — exclusions, applied FIRST. Gift-ness is derived, never read off a flag. ──
        var classified = PurchaseIntentClassifier.ClassifyAll(profile.Purchases, catalogue.BySku, today);

        var excluded = new List<string>();
        var eligible = new List<Product>();

        foreach (var line in classified)
        {
            if (line.Intent == PurchaseIntent.Gift)
            {
                excluded.Add($"{line.PurchaseId} (gift)");
                continue;
            }

            if (catalogue.IsSensitive(line.Product))
            {
                excluded.Add($"{line.PurchaseId} (sensitive category)");
                continue;
            }

            eligible.Add(line.Product);
        }

        var owned = eligible
            .Select(p => p.LeafCategory)
            .ToHashSet(StringComparer.Ordinal);

        // ── R1 — manifest: two or more eligible purchases in one leaf category. ───────────
        var manifest = eligible
            .GroupBy(p => p.LeafCategory, StringComparer.Ordinal)
            .Where(g => g.Count() >= ManifestMinimumPurchases)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        // ── R2 — latent: a token shared by two or more purchases spanning two or more leaves.
        //
        // Distinct-by-product first: a customer who bought the SAME sku five times (Sofia's
        // cartridges) must not have that one product's tokens counted five times, or a single
        // repeat purchase would manufacture latent gold out of nothing.
        var distinctProducts = eligible
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var latent = distinctProducts
            .SelectMany(p => EligibleTokens(p).Select(t => (Token: t, p.LeafCategory)))
            .Where(x => !IsExcludedToken(x.Token))
            .GroupBy(x => x.Token, StringComparer.Ordinal)
            .Where(g => g.Count() >= LatentMinimumPurchases
                     && g.Select(x => x.LeafCategory).Distinct(StringComparer.Ordinal).Count() >= LatentMinimumCategories
                     && IsSpecificEnough(g.Key))
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        return new GoldInterestMap(manifest, latent, owned, excluded);
    }

    /// <summary>True when a token is a category or compatibility restatement rather than use context.</summary>
    /// <param name="token">A normalised attribute token.</param>
    public static bool IsExcludedToken(string token) =>
        ExcludedTokenPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// True when the token is distinctive enough to be an interest rather than a stopword — carried
    /// by at most <see cref="LatentMaximumCatalogueShare"/> of the catalogue.
    /// </summary>
    /// <param name="token">A normalised attribute token.</param>
    public static bool IsSpecificEnough(string token) => CatalogueShareOf(token) <= LatentMaximumCatalogueShare;

    /// <summary>
    /// The share of the catalogue carrying a token. Printed alongside every gold set so the
    /// specificity rule is auditable instead of being a magic number in a filter.
    /// </summary>
    /// <remarks>
    /// Counted over <see cref="EligibleTokens"/>, the same vocabulary gold is drawn from and the
    /// same one a presented product is credited with serving. Counting over
    /// <see cref="Product.Attributes"/> instead would let a spec value that happens to spell a
    /// use-tag suffix inflate the share of a token it cannot actually serve.
    /// </remarks>
    /// <param name="token">A normalised attribute token.</param>
    public static double CatalogueShareOf(string token)
    {
        var catalogue = Catalogue.Default;
        if (catalogue.All.Count == 0) return 0.0;
        return catalogue.All.Count(p => EligibleTokens(p).Contains(token)) / (double)catalogue.All.Count;
    }
}
