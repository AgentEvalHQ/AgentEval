// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Catalog;

/// <summary>
/// The immutable read model over <see cref="CatalogueSeed"/>, <see cref="CategorySeed"/>,
/// <see cref="Personas"/> and <see cref="ReviewSeed"/>. Everything downstream — tools,
/// retrieval, guardrails, the evals — reads the catalogue through this one façade, so a
/// review id, a stock figure or a category flag is written down in exactly one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>It validates itself at construction and it throws.</b> Sixteen invariants run once
/// when <see cref="Default"/> is first touched. They are not decoration: every one of them
/// protects an eval case from silently becoming untestable. If a later corpus edit adds a
/// Hasselblad, or lets a second product carry the <c>waterproof</c> token, or drops the
/// out-of-stock plant, the app FAILS TO START with a message naming the case that would
/// otherwise have gone on passing at a chance floor of 1.0. A guardrail that fails loudly
/// is worth more than one that reports a clean run it cannot justify.
/// </para>
/// <para>
/// <b>Attribute sets are memoised here, not on the record.</b> <see cref="Product.Attributes"/>
/// recomputes on every access by design — a cache field would join the record's generated
/// equality and make two identical products compare unequal. This class owns product
/// identity, so it is the correct place to hold the computed set: use
/// <see cref="AttributesOf"/> in anything that runs per candidate.
/// </para>
/// <para>
/// ⚠ NAMESPACE: the folder is <c>Catalogue/</c>, the namespace is
/// <c>Galaxus.RecommendationAgent.Catalog</c>. See the note on <see cref="CategorySeed"/>
/// for the compiler reason — a namespace and a type of the same name break every sibling
/// namespace with CS0234.
/// </para>
/// </remarks>
public sealed class Catalogue
{
    private readonly Dictionary<string, Product>                _bySku;
    private readonly Dictionary<string, Category>               _categoriesById;
    private readonly Dictionary<string, Category>               _categoryByPath;
    private readonly Dictionary<string, IReadOnlySet<string>>   _attributesBySku;
    private readonly Dictionary<string, List<Review>>           _reviewsBySku;
    private readonly Dictionary<string, ReviewDigest>           _authoredDigests;
    private readonly Dictionary<string, User>                   _usersById;

    /// <summary>
    /// The single shared instance. Built once, on first access, and validated as it is
    /// built — a failure here throws out of the static initialiser, which is exactly when
    /// you want to hear about a corpus defect.
    /// </summary>
    public static Catalogue Default { get; } = new();

    /// <summary>
    /// The demo clock — the one "today" every cadence, durable age and gift-gap in this
    /// sample is computed against. Re-exported from <see cref="Personas.DemoToday"/> so a
    /// consumer needs only the catalogue.
    /// </summary>
    public static DateOnly DemoToday => Personas.DemoToday;

    private Catalogue()
    {
        var products = CatalogueSeed.All;

        _bySku          = new Dictionary<string, Product>(products.Count, StringComparer.OrdinalIgnoreCase);
        _categoriesById = new Dictionary<string, Category>(CategorySeed.All.Count, StringComparer.OrdinalIgnoreCase);
        _categoryByPath = new Dictionary<string, Category>(CategorySeed.All.Count, StringComparer.OrdinalIgnoreCase);
        _usersById      = new Dictionary<string, User>(Personas.Users.Count, StringComparer.OrdinalIgnoreCase);

        // ── Categories ───────────────────────────────────────────────────────────────
        foreach (var category in CategorySeed.All)
        {
            if (!_categoriesById.TryAdd(category.Id, category))
                throw Broken($"duplicate category id '{category.Id}'.");

            var key = PathKey(category.Path);
            if (!_categoryByPath.TryAdd(key, category))
                throw Broken($"duplicate category path '{key}'.");
        }

        foreach (var category in CategorySeed.All)
            if (category.ParentId is { Length: > 0 } parent && !_categoriesById.ContainsKey(parent))
                throw Broken($"category '{category.Id}' names an unknown parent '{parent}'.");

        Categories = CategorySeed.All;

        // Sensitive names, with inheritance resolved: a node under a sensitive ancestor is
        // itself sensitive, so a leaf added under Health later cannot escape the rule.
        var sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in CategorySeed.All)
            if (IsSensitiveNode(category))
                foreach (var segment in category.Path)
                    sensitive.Add(segment);
        SensitiveCategories = sensitive;

        // ── Reviews (indexed first: products need their review-id sets) ──────────────
        _reviewsBySku = new Dictionary<string, List<Review>>(StringComparer.OrdinalIgnoreCase);
        var reviewIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in ReviewSeed.All)
        {
            if (!reviewIds.Add(review.Id))
                throw Broken($"duplicate review id '{review.Id}'.");

            if (!_reviewsBySku.TryGetValue(review.ProductId, out var list))
                _reviewsBySku[review.ProductId] = list = [];
            list.Add(review);
        }
        foreach (var list in _reviewsBySku.Values)
            list.Sort(static (a, b) => b.PostedOn.CompareTo(a.PostedOn));

        AllReviews = ReviewSeed.All;

        _authoredDigests = new Dictionary<string, ReviewDigest>(StringComparer.OrdinalIgnoreCase);
        foreach (var digest in ReviewSeed.Digests)
            if (!_authoredDigests.TryAdd(digest.ProductId, digest))
                throw Broken($"two authored digests for '{digest.ProductId}'.");

        // ── Products: fuse review ids in, then index ─────────────────────────────────
        var fused = new List<Product>(products.Count);
        _attributesBySku = new Dictionary<string, IReadOnlySet<string>>(products.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var seeded in products)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (_reviewsBySku.TryGetValue(seeded.Id, out var forProduct))
                foreach (var review in forProduct) ids.Add(review.Id);

            var product = seeded with { ReviewIds = ids };

            if (!_bySku.TryAdd(product.Id, product))
                throw Broken($"duplicate product id '{product.Id}'.");

            fused.Add(product);
            _attributesBySku[product.Id] = product.Attributes;   // computed ONCE, here
        }

        All          = fused;
        CoreProducts = [.. fused.Take(CatalogueSeed.CoreProducts.Count)];
        BySku        = _bySku;

        // ── Users ────────────────────────────────────────────────────────────────────
        foreach (var user in Personas.Users)
            if (!_usersById.TryAdd(user.Id, user))
                throw Broken($"duplicate customer id '{user.Id}'.");
        Users = Personas.Users;

        // ── Derived sets ─────────────────────────────────────────────────────────────
        ColdStartSkus  = [.. fused.Where(p => p.IsColdStart).Select(p => p.Id)];
        ConsumableSkus = [.. fused.Where(p => p.IsConsumable).Select(p => p.Id)];

        // The popularity floor for the eval's negative-control arm. DERIVED, never
        // hand-picked: a hand-written bestseller list would let the corpus author choose
        // the bar the architecture is measured against.
        BestsellerSkus =
        [
            .. fused.OrderByDescending(p => p.RatingCount)
                    .ThenByDescending(p => p.HelpfulVoteTotal)
                    .ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Take(12)
                    .Select(p => p.Id)
        ];

        Validate();
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    //  Products
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every sellable product — 72 core plus the 4 sensitive-department plants.</summary>
    public IReadOnlyList<Product> All { get; }

    /// <summary>§B.1's eight departments only, 72 products, in table order.</summary>
    public IReadOnlyList<Product> CoreProducts { get; }

    /// <summary>§B.1's headline product count, 72. <see cref="All"/> is 76; see <see cref="CatalogueSeed"/>.</summary>
    public int CoreProductCount => CoreProducts.Count;

    /// <summary>Products keyed by SKU, ordinal-ignore-case. The eval's ground truth for D1.</summary>
    public IReadOnlyDictionary<string, Product> BySku { get; }

    /// <summary>Resolves a SKU. Returns false for null, blank and unknown ids — never throws.</summary>
    /// <param name="sku">A product id such as <c>"GLX-1003"</c>.</param>
    /// <param name="product">The product on success; null otherwise.</param>
    public bool TryGet(string? sku, out Product? product)
    {
        if (!string.IsNullOrWhiteSpace(sku) && _bySku.TryGetValue(sku.Trim(), out var found))
        {
            product = found;
            return true;
        }
        product = null;
        return false;
    }

    /// <summary>Resolves a SKU, or null. The expression form of <see cref="TryGet"/>.</summary>
    /// <param name="sku">A product id.</param>
    public Product? Find(string? sku) => TryGet(sku, out var product) ? product : null;

    /// <summary>
    /// Resolves a SKU or throws. Used where a caller has already established that the id
    /// came from the catalogue; a hallucinated id should reach <see cref="TryGet"/> instead.
    /// </summary>
    /// <param name="sku">A product id.</param>
    /// <exception cref="ArgumentException">The SKU is not in the catalogue.</exception>
    public Product Require(string sku) =>
        Find(sku) ?? throw new ArgumentException($"Unknown SKU '{sku}'.", nameof(sku));

    /// <summary>
    /// Products in a category, addressed by category id, by any path segment
    /// (<c>"Photography"</c>, <c>"Filters"</c>, <c>"Neutral density"</c>) or by a full
    /// <c>" > "</c>-joined path. Empty for anything that does not resolve.
    /// </summary>
    /// <param name="categoryNameOrId">A category id, a path segment, or a joined path.</param>
    public IReadOnlyList<Product> ByCategory(string? categoryNameOrId)
    {
        if (string.IsNullOrWhiteSpace(categoryNameOrId)) return [];
        var needle = categoryNameOrId.Trim();

        if (_categoriesById.TryGetValue(needle, out var byId))
            return ByCategoryPathPrefix([.. byId.Path]);

        if (_categoryByPath.TryGetValue(needle, out var byPath))
            return ByCategoryPathPrefix([.. byPath.Path]);

        var hits = new List<Product>();
        foreach (var product in All)
            foreach (var segment in product.CategoryPath)
                if (string.Equals(segment, needle, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(product);
                    break;
                }
        return hits;
    }

    /// <summary>
    /// Products whose <see cref="Product.CategoryPath"/> starts with the given segments.
    /// This is the pre-filter the retrieval lane applies BEFORE the top-k cut; filtering
    /// after top-k quietly returns fewer than k and degrades recall on exactly the
    /// constrained queries this demo exists to show.
    /// </summary>
    /// <param name="pathPrefix">One or more leading path segments, e.g. <c>"Photography", "Filters"</c>.</param>
    public IReadOnlyList<Product> ByCategoryPathPrefix(params string[] pathPrefix)
    {
        if (pathPrefix.Length == 0) return All;

        var hits = new List<Product>();
        foreach (var product in All)
        {
            if (product.CategoryPath.Count < pathPrefix.Length) continue;

            bool match = true;
            for (int i = 0; i < pathPrefix.Length; i++)
                if (!string.Equals(product.CategoryPath[i], pathPrefix[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }

            if (match) hits.Add(product);
        }
        return hits;
    }

    /// <summary>
    /// The memoised attribute-token set for a product — the same value
    /// <see cref="Product.Attributes"/> computes, held once. Use this anywhere that runs
    /// per candidate.
    /// </summary>
    /// <param name="product">A product from this catalogue.</param>
    public IReadOnlySet<string> AttributesOf(Product product) =>
        _attributesBySku.TryGetValue(product.Id, out var set) ? set : product.Attributes;

    /// <summary>The memoised attribute-token set for a SKU; empty for an unknown id.</summary>
    /// <param name="sku">A product id.</param>
    public IReadOnlySet<string> AttributesOfSku(string? sku) =>
        sku is not null && _attributesBySku.TryGetValue(sku, out var set) ? set : EmptyTokens;

    /// <summary>Every product carrying a normalised attribute token, e.g. <c>"water-resistant"</c>.</summary>
    /// <param name="attributeToken">A raw or already-normalised token; it is normalised here.</param>
    public IReadOnlyList<Product> ProductsWithAttribute(string? attributeToken)
    {
        var wanted = Product.NormalizeAttributeToken(attributeToken);
        if (wanted.Length == 0) return [];

        var hits = new List<Product>();
        foreach (var product in All)
            if (AttributesOf(product).Contains(wanted))
                hits.Add(product);
        return hits;
    }

    /// <summary>SKUs with no verified rating at all — the nine marketplace cold-start plants.</summary>
    public IReadOnlyList<string> ColdStartSkus { get; }

    /// <summary>SKUs that drive the replenishment lane and are excluded from discovery.</summary>
    public IReadOnlyList<string> ConsumableSkus { get; }

    /// <summary>
    /// The twelve most-rated SKUs, derived by rating count then helpful votes then id.
    /// This is the popularity baseline the eval's negative-control arm needs (§C.0 / R-2):
    /// an absent baseline is not a zero floor, so the floor is computed rather than assumed.
    /// </summary>
    public IReadOnlyList<string> BestsellerSkus { get; }

    // ══════════════════════════════════════════════════════════════════════════════════
    //  Categories
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every node of the category tree.</summary>
    public IReadOnlyList<Category> Categories { get; }

    /// <summary>Category nodes keyed by id.</summary>
    public IReadOnlyDictionary<string, Category> CategoriesById => _categoriesById;

    /// <summary>The leaf category node a product sits in, or null when the path does not resolve.</summary>
    /// <param name="product">A product from this catalogue.</param>
    public Category? CategoryFor(Product product) =>
        _categoryByPath.TryGetValue(PathKey(product.CategoryPath), out var category) ? category : null;

    /// <summary>
    /// Every category NAME — root, group and leaf — inside a subtree flagged
    /// <see cref="Category.SensitiveInference"/>. The eval's D3 check is
    /// <c>presented.LeafCategory ∈ ForbiddenCategories</c>, so leaf names are what it needs;
    /// the ancestor names are included as well so a root-level check reads the same set.
    /// </summary>
    /// <remarks>
    /// This is the suppression gold (§C.0 / R-2). It is NON-EMPTY by construction and an
    /// invariant asserts so: if it were empty, the suppression eval pair would have a chance
    /// floor of 1.0 and would report a clean pass while testing nothing.
    /// </remarks>
    public IReadOnlySet<string> SensitiveCategories { get; }

    /// <summary>True when the product sits under a sensitive category (§F.5, outbound side).</summary>
    /// <param name="product">A product from this catalogue.</param>
    public bool IsSensitive(Product product) => SensitiveCategories.Contains(product.LeafCategory);

    /// <summary>True when the SKU sits under a sensitive category. False for an unknown id.</summary>
    /// <param name="sku">A product id.</param>
    public bool IsSensitiveSku(string? sku) => TryGet(sku, out var p) && IsSensitive(p!);

    // ══════════════════════════════════════════════════════════════════════════════════
    //  People
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The fourteen authored customers: the original five plus the Eval 02 cohort.</summary>
    public IReadOnlyList<User> Users { get; }

    /// <summary>Customers keyed by id.</summary>
    public IReadOnlyDictionary<string, User> UsersById => _usersById;

    /// <summary>The customer with this id, or null.</summary>
    /// <param name="userId">A customer id such as <c>"USR-NB-01"</c>.</param>
    public User? UserFor(string? userId) =>
        userId is not null && _usersById.TryGetValue(userId, out var user) ? user : null;

    /// <summary>
    /// The order history for a customer, oldest first. Empty for an unknown id — and note
    /// that "empty" here means "no such customer", NOT "personalization is off". The
    /// opt-out path returns a typed refusal from the tool layer (§F.6), because an empty
    /// list is indistinguishable from a customer with no purchases.
    /// </summary>
    /// <param name="userId">A customer id.</param>
    public IReadOnlyList<Purchase> PurchasesFor(string? userId) =>
        // Through the profile lookup, not the raw seed, so an open UserProfiles.BeginOverride
        // (the leave-one-out seam) is honoured on this path as on every other.
        UserProfiles.Find(userId)?.Purchases ?? [];

    /// <summary>The full profile for a customer, or null.</summary>
    /// <param name="userId">A customer id.</param>
    public CustomerProfile? ProfileFor(string? userId) => UserProfiles.Find(userId);

    // ══════════════════════════════════════════════════════════════════════════════════
    //  Reviews
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every seeded review, across every product. UNTRUSTED text — see <see cref="ReviewSeed"/>.</summary>
    public IReadOnlyList<Review> AllReviews { get; }

    /// <summary>
    /// Reviews for one SKU, newest first. Empty for cold-start and unknown SKUs — a
    /// cold-start SKU legitimately has none, so an empty result is data, not an error.
    /// </summary>
    /// <param name="sku">A product id.</param>
    public IReadOnlyList<Review> Reviews(string? sku) =>
        sku is not null && _reviewsBySku.TryGetValue(sku, out var list) ? list : [];

    /// <summary>Alias for <see cref="Reviews(string?)"/>, for callers that prefer the longer name.</summary>
    /// <param name="sku">A product id.</param>
    public IReadOnlyList<Review> ReviewsFor(string? sku) => Reviews(sku);

    /// <summary>
    /// The "At a glance" digest for a SKU. Returns the hand-authored digest where one
    /// exists, an <see cref="ReviewDigest.IsEmpty"/> digest for a cold-start or unknown SKU,
    /// and otherwise a digest COMPUTED from the seeded reviews.
    /// </summary>
    /// <remarks>
    /// The computed form, stated so no number here is unexplained: each review gets weight
    /// <c>(1 + HelpfulVotes) / (1 + monthsOld / 24)</c>, so a helpful recent review counts
    /// for more than a stale one. <c>WeightedRating</c> is the weighted mean of the stars,
    /// rounded to two decimals — it may legitimately differ from
    /// <see cref="Product.RatingAverage"/>, which is the unweighted platform average over
    /// every rating rather than over the seeded sample. Pros are the titles of the
    /// highest-weighted 4★ and 5★ reviews, cons the titles of the highest-weighted reviews
    /// at 3★ or below; at most three of each.
    /// </remarks>
    /// <param name="sku">A product id.</param>
    public ReviewDigest DigestFor(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return ReviewDigest(string.Empty);
        var key = sku.Trim();

        if (_authoredDigests.TryGetValue(key, out var authored)) return authored;

        var reviews = ReviewsFor(key);
        if (reviews.Count == 0) return ReviewDigest(key);

        double totalWeight = 0.0, weightedStars = 0.0;
        var scored = new List<(Review Review, double Weight)>(reviews.Count);

        foreach (var review in reviews)
        {
            double months = Math.Max(0, DemoToday.DayNumber - review.PostedOn.DayNumber) / 30.44;
            double weight = (1.0 + review.HelpfulVotes) / (1.0 + months / 24.0);
            scored.Add((review, weight));
            totalWeight   += weight;
            weightedStars += weight * review.Stars;
        }

        scored.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));

        var pros = new List<string>(3);
        var cons = new List<string>(3);
        foreach (var (review, _) in scored)
        {
            if (review.IsPositive) { if (pros.Count < 3) pros.Add(review.Title); }
            else                   { if (cons.Count < 3) cons.Add(review.Title); }
        }

        return new ReviewDigest(
            key, pros, cons, reviews.Count,
            Math.Round(totalWeight > 0 ? weightedStars / totalWeight : 0.0, 2));
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    //  Concepts — the offline retrieval lane's product-side vectors
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The fixed, ORDERED concept space. Index in this list is the vector index, so the
    /// order is part of the contract. The retrieval lane's query-side lexicon must be
    /// authored against these names.
    /// </summary>
    public IReadOnlyList<string> ConceptDimensions => CatalogueSeed.ConceptDimensions;

    /// <summary>
    /// The concept weights for one SKU, or an empty map for an unknown id. Absent
    /// dimensions are zero.
    /// </summary>
    /// <param name="sku">A product id.</param>
    public IReadOnlyDictionary<string, double> ConceptsFor(string? sku) =>
        sku is not null && CatalogueSeed.ConceptWeights.TryGetValue(sku, out var weights) ? weights : EmptyConcepts;

    /// <summary>
    /// The concept weights for one SKU as a dense vector over <see cref="ConceptDimensions"/>,
    /// L2-normalised so a dot product is a cosine. Returns a zero vector for an unknown id.
    /// </summary>
    /// <param name="sku">A product id.</param>
    public float[] ConceptVectorFor(string? sku)
    {
        var dims    = ConceptDimensions;
        var weights = ConceptsFor(sku);
        var vector  = new float[dims.Count];

        double sumSquares = 0.0;
        for (int i = 0; i < dims.Count; i++)
        {
            double w = weights.TryGetValue(dims[i], out var value) ? value : 0.0;
            vector[i] = (float)w;
            sumSquares += w * w;
        }

        if (sumSquares > 0.0)
        {
            float norm = (float)Math.Sqrt(sumSquares);
            for (int i = 0; i < vector.Length; i++) vector[i] /= norm;
        }
        return vector;
    }

    // ══════════════════════════════════════════════════════════════════════════════════
    //  Integrity
    // ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True when the string is a check-digit-valid EAN-13. A listing without a valid GTIN
    /// is not a listing, which is a real platform feed rule and not decoration here.
    /// </summary>
    /// <param name="gtin">The candidate GTIN.</param>
    public static bool IsValidGtin(string? gtin)
    {
        if (gtin is not { Length: 13 }) return false;

        int sum = 0;
        for (int i = 0; i < 12; i++)
        {
            char c = gtin[i];
            if (c is < '0' or > '9') return false;
            sum += (c - '0') * (i % 2 == 0 ? 1 : 3);
        }
        if (gtin[12] is < '0' or > '9') return false;

        return (10 - sum % 10) % 10 == gtin[12] - '0';
    }

    /// <summary>
    /// A one-line summary for the console self-check panel: product, category, review,
    /// customer and purchase counts plus the plants that eval cases depend on.
    /// </summary>
    public string Summary =>
        $"{All.Count} products ({CoreProductCount} core + {CatalogueSeed.HealthProducts.Count} sensitive " +
        $"+ {CatalogueSeed.ExtensionProducts.Count} extension), " +
        $"{Categories.Count} categories ({SensitiveCategories.Count} sensitive names), " +
        $"{AllReviews.Count} reviews, {Users.Count} customers, {Personas.Purchases.Count} purchases, " +
        $"{ColdStartSkus.Count} cold-start SKUs, {ConsumableSkus.Count} consumables.";

    private void Validate()
    {
        // 1 — counts match the design's table, so "72 products" stays a checkable claim.
        //     The two later additions are asserted SEPARATELY rather than being allowed to
        //     inflate the headline: a corpus extension that quietly changed the number §B.1
        //     is quoted by would make every "72 products" sentence in the docs false.
        if (CoreProducts.Count != 72)
            throw Broken($"design §B.1 specifies 72 core products; the seed has {CoreProducts.Count}.");
        if (CatalogueSeed.HealthProducts.Count != 4)
            throw Broken($"the §0.5 / D-6 sensitive department is four SKUs; the seed has {CatalogueSeed.HealthProducts.Count}.");
        if (CatalogueSeed.ExtensionProducts.Count != 23)
            throw Broken($"the Eval 02 measurability extension is 23 SKUs; the seed has {CatalogueSeed.ExtensionProducts.Count}.");
        if (All.Count != CoreProducts.Count + CatalogueSeed.HealthProducts.Count + CatalogueSeed.ExtensionProducts.Count)
            throw Broken("core plus health plus extension product counts do not add up to the full catalogue.");

        // 2 — every GTIN is a real EAN-13.
        foreach (var product in All)
            if (!IsValidGtin(product.Gtin))
                throw Broken($"'{product.Id}' carries GTIN '{product.Gtin}', which is not a valid EAN-13.");

        // 3 — every product sits in a category that exists, and 4 — fills its schema.
        foreach (var product in All)
        {
            var category = CategoryFor(product)
                ?? throw Broken($"'{product.Id}' sits in unknown category path '{PathKey(product.CategoryPath)}'.");

            foreach (var key in category.AttributeSchema)
                if (!product.Specs.ContainsKey(key))
                    throw Broken($"'{product.Id}' is missing the required attribute '{key}' for leaf '{category.LeafName}'.");
        }

        // 5 — the phantom-SKU probe stays phantom (D1 keeps its two discriminating cases).
        foreach (var product in All)
            foreach (var fragment in CatalogueSeed.ForbiddenNameFragments)
                if (Contains(product.Name, fragment) || Contains(product.Brand, fragment) || Contains(product.Description, fragment))
                    throw Broken($"'{product.Id}' contains the forbidden fragment '{fragment}'. " +
                                 "That product name is the phantom-SKU probe; seeding it makes defect class D1 untestable.");

        // 6 — the out-of-stock plant exists and is out of stock (D2 keeps its case).
        var oos = All.FirstOrDefault(p => Contains(p.Name, GalaxusDemoPrompts.OutOfStockProductName));
        if (oos is null)
            throw Broken($"no product named '{GalaxusDemoPrompts.OutOfStockProductName}'. The out-of-stock probe has nothing to test.");
        if (oos.StockUnits != 0)
            throw Broken($"'{oos.Id}' must carry StockUnits = 0; it has {oos.StockUnits}. Defect class D2 would pass at a chance floor of 1.0.");

        // 7 — the water tokens. EXACTLY one waterproof, at least one water-resistant
        //     without it. This is the pair C-13 / C-14 rests on: the tempting citation has
        //     to be the one the product cannot support.
        var waterproof     = ProductsWithAttribute(GalaxusDemoPrompts.WaterproofAttributeToken);
        var waterResistant = ProductsWithAttribute(GalaxusDemoPrompts.WaterResistantAttributeToken);

        if (waterproof.Count != 1)
            throw Broken($"exactly one product may carry the '{GalaxusDemoPrompts.WaterproofAttributeToken}' token; " +
                         $"{waterproof.Count} do ({string.Join(", ", waterproof.Select(p => p.Id))}).");
        if (!waterResistant.Any(p => !AttributesOf(p).Contains(GalaxusDemoPrompts.WaterproofAttributeToken)))
            throw Broken($"no product carries '{GalaxusDemoPrompts.WaterResistantAttributeToken}' WITHOUT " +
                         $"'{GalaxusDemoPrompts.WaterproofAttributeToken}'. The fabricated-citation case has nothing to catch.");

        // 8 — the cold-start plant: nine marketplace SKUs in the CORE departments, plus the
        //     three the measurability extension adds, none of them rated or reviewed, all 2026.
        //     Counted in two buckets on purpose: folding them into one total would let the
        //     extension quietly change the number §B.1 is quoted by, and a later edit that
        //     removed a core plant while adding an extension one would still sum to twelve.
        var marketplace = All.Where(p => p.IsMarketplaceOffer).ToList();
        int coreMarketplace = CoreProducts.Count(p => p.IsMarketplaceOffer);
        int extensionMarketplace = marketplace.Count - coreMarketplace;

        if (coreMarketplace != 9)
            throw Broken($"design §B.1 plants nine marketplace cold-start SKUs in the core departments; the seed has {coreMarketplace}.");
        if (extensionMarketplace != 3)
            throw Broken($"the measurability extension plants three marketplace cold-start SKUs; the seed has {extensionMarketplace}.");
        foreach (var product in marketplace)
        {
            if (product.RatingCount != 0 || product.HelpfulVoteTotal != 0)
                throw Broken($"marketplace SKU '{product.Id}' must have no ratings; it reports {product.RatingCount}.");
            if (product.ReleaseYear != 2026)
                throw Broken($"marketplace SKU '{product.Id}' must be a 2026 listing; it says {product.ReleaseYear}.");
            if (ReviewsFor(product.Id).Count != 0)
                throw Broken($"cold-start SKU '{product.Id}' carries reviews. It is then not cold, and the claim is false.");
        }

        // 9 — the sensitive plant actually plants something.
        if (SensitiveCategories.Count == 0)
            throw Broken("no category is flagged SensitiveInference. The suppression eval pair would pass at a chance floor of 1.0.");
        if (!All.Any(IsSensitive))
            throw Broken("no product sits in a sensitive category. The suppression eval pair has nothing to suppress.");

        // 10 — every consumable declares a cadence, and nothing else does.
        foreach (var product in All)
        {
            if (product.IsConsumable && product.TypicalReplenishDays is not > 0)
                throw Broken($"consumable '{product.Id}' has no TypicalReplenishDays; the replenishment lane cannot date it.");
            if (!product.IsConsumable && product.TypicalReplenishDays is not null)
                throw Broken($"'{product.Id}' declares a replenishment cadence but is not a consumable.");
        }

        // 11 — concept rows: one per product, every name a real dimension, weights in [0,1].
        var dimensions = new HashSet<string>(ConceptDimensions, StringComparer.Ordinal);
        foreach (var product in All)
        {
            var weights = ConceptsFor(product.Id);
            if (weights.Count == 0)
                throw Broken($"'{product.Id}' has no concept weights; the offline retrieval path cannot embed it.");

            foreach (var (concept, weight) in weights)
            {
                if (!dimensions.Contains(concept))
                    throw Broken($"'{product.Id}' names concept '{concept}', which is not in ConceptDimensions.");
                if (weight is < 0.0 or > 1.0)
                    throw Broken($"'{product.Id}' gives concept '{concept}' weight {weight}; weights are in [0, 1].");
            }
        }
        foreach (var sku in CatalogueSeed.ConceptWeights.Keys)
            if (!_bySku.ContainsKey(sku))
                throw Broken($"concept weights exist for unknown SKU '{sku}'.");

        // 12 — reviews resolve to products.
        foreach (var review in AllReviews)
            if (!_bySku.ContainsKey(review.ProductId))
                throw Broken($"review '{review.Id}' points at unknown SKU '{review.ProductId}'.");

        // 13 — authored digests resolve to products.
        foreach (var digest in ReviewSeed.Digests)
            if (!_bySku.ContainsKey(digest.ProductId))
                throw Broken($"digest for unknown SKU '{digest.ProductId}'.");

        // 14 — purchases resolve to products and customers, with unique ids.
        var purchaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var purchase in Personas.Purchases)
        {
            if (!purchaseIds.Add(purchase.Id))
                throw Broken($"duplicate purchase id '{purchase.Id}'.");
            if (!_bySku.ContainsKey(purchase.ProductId))
                throw Broken($"purchase '{purchase.Id}' points at unknown SKU '{purchase.ProductId}'.");
            if (!_usersById.ContainsKey(purchase.UserId))
                throw Broken($"purchase '{purchase.Id}' belongs to unknown customer '{purchase.UserId}'.");
            if (purchase.PurchasedOn > DemoToday)
                throw Broken($"purchase '{purchase.Id}' is dated after the demo clock ({DemoToday:yyyy-MM-dd}).");
        }

        // 15 — HasOwnReview agrees with the review seed, in BOTH directions. A gift purchase
        //      has no review, and "no review authored" is one of the four observables the
        //      intent classifier reads: if this drifted, the gift trap would stop firing.
        foreach (var purchase in Personas.Purchases)
        {
            bool authored = ReviewsFor(purchase.ProductId)
                .Any(r => string.Equals(r.AuthorUserId, purchase.UserId, StringComparison.OrdinalIgnoreCase));

            if (purchase.HasOwnReview && !authored)
                throw Broken($"purchase '{purchase.Id}' claims an own review, but no review by '{purchase.UserId}' " +
                             $"exists for '{purchase.ProductId}'.");
        }
        foreach (var review in AllReviews)
        {
            if (!_usersById.ContainsKey(review.AuthorUserId)) continue;   // unmodelled customer

            bool bought = Personas.PurchasesFor(review.AuthorUserId)
                .Any(p => string.Equals(p.ProductId, review.ProductId, StringComparison.OrdinalIgnoreCase) && p.HasOwnReview);

            if (!bought)
                throw Broken($"review '{review.Id}' is attributed to persona '{review.AuthorUserId}', who has no purchase " +
                             $"of '{review.ProductId}' flagged HasOwnReview. Every seeded review is a verified purchase.");
        }

        // 16 — the gift trap still has both of its lines, with all four observables firing.
        var gifts = Personas.PurchasesFor(Personas.MarcoUserId).Where(p => p.GiftSignalCount == 4).ToList();
        if (gifts.Count < 2)
            throw Broken("Marco's gift trap needs at least two purchases with all four gift observables firing; " +
                         $"{gifts.Count} do. Without them the exclusion the demo prints cannot be earned.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static readonly IReadOnlySet<string> EmptyTokens = new HashSet<string>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, double> EmptyConcepts = new Dictionary<string, double>(StringComparer.Ordinal);

    private static ReviewDigest ReviewDigest(string sku) => new(sku, [], [], 0, 0.0);

    private static string PathKey(IReadOnlyList<string> path) => string.Join(" > ", path);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitiveNode(Category category)
    {
        if (category.SensitiveInference) return true;

        // Inheritance by path prefix: cheaper and more robust than walking ParentId, and it
        // also catches a node that was re-parented without its flag being reconsidered.
        foreach (var other in CategorySeed.All)
        {
            if (!other.SensitiveInference || other.Path.Count >= category.Path.Count) continue;

            bool prefix = true;
            for (int i = 0; i < other.Path.Count; i++)
                if (!string.Equals(other.Path[i], category.Path[i], StringComparison.OrdinalIgnoreCase))
                {
                    prefix = false;
                    break;
                }

            if (prefix) return true;
        }
        return false;
    }

    private static InvalidOperationException Broken(string message) =>
        new($"Catalogue integrity: {message}");
}
