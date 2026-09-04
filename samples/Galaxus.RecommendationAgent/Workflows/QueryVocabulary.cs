// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// THE §0.5 / D-3 control: a STRUCTURAL constraint on which words may reach query generation.
/// </summary>
/// <remarks>
/// <para>
/// <b>The threat, stated plainly.</b> The coverage reviewer is allowed to propose a new interest
/// from a review snippet, and that interest's <see cref="Interest.QueryTerms"/> drive the next
/// round's retrieval. Review text is written by customers and by marketplace sellers — Galaxus
/// takes roughly four thousand user-authored ratings a day. So a seller can write steering text,
/// the reviewer proposes the interest, discovery runs the injected query, the seller's SKU comes
/// back through <i>legitimate</i> retrieval, it is therefore genuinely in the candidate set, and
/// every containment check downstream stays green. The grounding story is sound and it cannot
/// catch this, because nothing was ever ungrounded.
/// </para>
/// <para>
/// <b>The control, and why it is not a prompt.</b> A model-proposed query term is accepted only
/// when every one of its tokens already appears in
/// <i>(the mapper's interest map ∪ the customer's own sentence) ∪ (the catalogue's own category
/// names and attribute/tag tokens)</i>. Terms with any token outside that set are DROPPED, and
/// each drop is recorded as a <see cref="DroppedQueryTerm"/> and printed. Prompt text telling a
/// model to ignore embedded instructions is defence in depth and is present in both prompts, but
/// it is NOT the control: a control you can talk a model out of is a request.
/// </para>
/// <para>
/// <b>Three deliberate exclusions from the vocabulary, each of them a hole if admitted.</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Product names and descriptions are NOT in it.</b> A marketplace listing's title and
///     body are seller-authored free text — admitting them would let the attacker supply the
///     vocabulary that admits their own steering terms. Only category names and the
///     attribute/tag token set are catalogue-owned enough to be a bar.
///   </item>
///   <item>
///     <b>Review bodies are NOT in it</b>, for the same reason and more directly: the review is
///     the attack channel, so it cannot also be the allow-list.
///   </item>
///   <item>
///     <b>Reviewer-inferred interests are NOT in it.</b> Only interests with
///     <see cref="InterestOrigin.Mapper"/> widen the vocabulary. Otherwise round 2's accepted
///     proposal would launder its own tokens into round 3's allow-list, and two rounds of
///     laundering is an unbounded channel wearing a bounded costume.
///   </item>
/// </list>
/// <para>
/// The customer's own in-session sentence IS admitted: they raised the topic, and a request the
/// customer typed is not an injection into their own session.
/// </para>
/// </remarks>
public sealed class QueryVocabulary
{
    private readonly HashSet<string> _tokens;
    private readonly Dictionary<string, string> _categoryPathsByKey;

    private QueryVocabulary(HashSet<string> tokens, Dictionary<string, string> categoryPathsByKey)
    {
        _tokens = tokens;
        _categoryPathsByKey = categoryPathsByKey;
    }

    /// <summary>
    /// Tokens with no steering power, admitted regardless of the catalogue.
    /// </summary>
    /// <remarks>
    /// English and German function words only. They cannot move retrieval toward a particular
    /// SKU — a query is scored on content tokens — so refusing them would only make the control
    /// reject legitimate natural-language queries and look broken, which is how a real control
    /// gets switched off. Nothing here names a product, a brand, a category or an attribute.
    /// </remarks>
    public static IReadOnlySet<string> NeutralTokens { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "and", "or", "for", "with", "without", "the", "a", "an", "in", "on", "of", "to", "at",
        "by", "from", "not", "no", "that", "this", "than", "then", "as", "is", "are", "be",
        "und", "oder", "fuer", "für", "mit", "ohne", "der", "die", "das", "den", "dem", "ein",
        "eine", "im", "am", "zum", "zur", "von", "auf", "bei", "nicht", "kein", "keine"
    };

    /// <summary>Every accepted token, ordinal. Exposed so the console can print its size.</summary>
    public IReadOnlySet<string> Tokens => _tokens;

    /// <summary>How many category paths this vocabulary can resolve a <c>next_category</c> against.</summary>
    public int CategoryCount => _categoryPathsByKey.Count;

    /// <summary>
    /// Builds the allowed vocabulary for one point in a run.
    /// </summary>
    /// <remarks>
    /// Rebuild it whenever the MAPPER-origin interest set changes. It is cheap — a few thousand
    /// short strings over a 76-product catalogue — and rebuilding is the honest thing to do,
    /// because a stale vocabulary is a vocabulary somebody widened without saying so.
    /// </remarks>
    /// <param name="catalogue">The catalogue. Supplies category names and attribute/tag tokens.</param>
    /// <param name="interests">The running interest map. Only <see cref="InterestOrigin.Mapper"/> entries contribute.</param>
    /// <param name="sessionRequest">What the customer typed this session, if anything.</param>
    public static QueryVocabulary Build(
        Catalogue catalogue,
        IEnumerable<Interest>? interests,
        string? sessionRequest = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── (a) the catalogue's own CATEGORY names, every node of the tree ───────────
        foreach (var category in catalogue.Categories)
        {
            categories[PathKey(category.Path)] = string.Join(" > ", category.Path);
            categories[category.Id] = string.Join(" > ", category.Path);
            categories[category.LeafName] = string.Join(" > ", category.Path);

            foreach (var element in category.Path) AddAll(tokens, element);
        }

        // ── (b) the catalogue's own ATTRIBUTE and TAG tokens ─────────────────────────
        //     Product.Attributes already fuses tags (whole and suffix), spec keys, spec values
        //     and key=value pairs through one normaliser, so this is exactly the token set an
        //     `attr:` citation may legitimately resolve against — nothing wider.
        foreach (var product in catalogue.All)
        {
            foreach (var attribute in catalogue.AttributesOf(product)) AddAll(tokens, attribute);
            foreach (var element in product.CategoryPath) AddAll(tokens, element);
        }

        // ── (c) the MAPPER's interest map ────────────────────────────────────────────
        if (interests is not null)
        {
            foreach (var interest in interests)
            {
                // Reviewer-inferred interests do NOT widen the vocabulary — see the type remarks.
                if (interest.Origin != InterestOrigin.Mapper) continue;

                AddAll(tokens, interest.Label);
                foreach (var term in interest.QueryTerms) AddAll(tokens, term);
                foreach (var hint in interest.CategoryHints) AddAll(tokens, hint);
                foreach (var (key, value) in interest.AttributeHints)
                {
                    AddAll(tokens, key);
                    AddAll(tokens, value);
                }
            }
        }

        // ── (d) the customer's own sentence ──────────────────────────────────────────
        AddAll(tokens, sessionRequest);

        return new QueryVocabulary(tokens, categories);
    }

    /// <summary>
    /// True when every token of <paramref name="term"/> is in the vocabulary.
    /// </summary>
    /// <param name="term">A model-proposed query phrase.</param>
    /// <param name="offendingTokens">The tokens that are not, ordered and de-duplicated.</param>
    public bool Accepts(string? term, out IReadOnlyList<string> offendingTokens)
    {
        var offending = new List<string>();
        offendingTokens = offending;

        if (string.IsNullOrWhiteSpace(term)) return false;

        var seenOffender = new HashSet<string>(StringComparer.Ordinal);
        bool sawContent = false;

        foreach (var token in Tokenize(term))
        {
            sawContent = true;
            if (_tokens.Contains(token) || NeutralTokens.Contains(token)) continue;
            if (seenOffender.Add(token)) offending.Add(token);
        }

        // A phrase that tokenises to nothing is not "clean", it is empty — and an empty query
        // retrieves the whole catalogue. Refuse it.
        if (!sawContent)
        {
            offending.Add("(no usable token)");
            return false;
        }

        return offending.Count == 0;
    }

    /// <summary>
    /// Filters a proposed term list, recording every refusal.
    /// </summary>
    /// <param name="terms">The model's proposed query terms.</param>
    /// <param name="proposedFor">What they were proposed for — printed in the drop line.</param>
    /// <param name="drops">The run's drop ledger; every refusal is appended.</param>
    /// <returns>The surviving terms, in input order, de-duplicated ordinally.</returns>
    public IReadOnlyList<string> Filter(
        IEnumerable<string>? terms,
        string proposedFor,
        ICollection<DroppedQueryTerm> drops)
    {
        ArgumentNullException.ThrowIfNull(drops);

        var kept = new List<string>();
        if (terms is null) return kept;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in terms)
        {
            var term = raw?.Trim() ?? string.Empty;
            if (term.Length == 0) continue;

            if (!Accepts(term, out var offending))
            {
                drops.Add(new DroppedQueryTerm(term, proposedFor, offending));
                continue;
            }

            if (seen.Add(term)) kept.Add(term);
        }

        return kept;
    }

    /// <summary>
    /// Filters one query string. Returns null when the query does not survive, which makes the
    /// gap that carried it unrunnable — and an unrunnable gap is dropped rather than searched.
    /// </summary>
    /// <param name="query">The model's <c>next_query</c>.</param>
    /// <param name="proposedFor">What it was proposed for.</param>
    /// <param name="drops">The run's drop ledger.</param>
    public string? FilterQuery(string? query, string proposedFor, ICollection<DroppedQueryTerm> drops)
    {
        ArgumentNullException.ThrowIfNull(drops);

        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        if (Accepts(text, out var offending)) return text;

        drops.Add(new DroppedQueryTerm(text, proposedFor, offending));
        return null;
    }

    /// <summary>
    /// Resolves a model-proposed category to a real catalogue path, or null.
    /// </summary>
    /// <remarks>
    /// A category is a HARD pre-filter on retrieval, so an unresolvable one must not be passed
    /// through as free text: doing so would either silently widen the search or silently empty
    /// it, and both look like the model's judgement rather than a wiring fault.
    /// </remarks>
    /// <param name="category">A category id, a leaf name, or a <c>" &gt; "</c>-joined path.</param>
    public string? ResolveCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;

        var needle = category.Trim();
        if (_categoryPathsByKey.TryGetValue(needle, out var direct)) return direct;

        var normalised = PathKey(needle.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return _categoryPathsByKey.TryGetValue(normalised, out var byPath) ? byPath : null;
    }

    /// <summary>
    /// Filters proposed attribute name/value pairs to ones the catalogue actually carries.
    /// </summary>
    /// <param name="attributes">The model's <c>next_attributes</c>.</param>
    /// <param name="proposedFor">What they were proposed for.</param>
    /// <param name="drops">The run's drop ledger.</param>
    public IReadOnlyDictionary<string, string> FilterAttributes(
        IReadOnlyDictionary<string, string>? attributes,
        string proposedFor,
        ICollection<DroppedQueryTerm> drops)
    {
        ArgumentNullException.ThrowIfNull(drops);

        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        if (attributes is null) return kept;

        foreach (var (key, value) in attributes)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;

            if (!Accepts(key, out var badKey))
            {
                drops.Add(new DroppedQueryTerm($"{key}={value}", proposedFor, badKey));
                continue;
            }

            if (!Accepts(value, out var badValue))
            {
                drops.Add(new DroppedQueryTerm($"{key}={value}", proposedFor, badValue));
                continue;
            }

            kept[key.Trim()] = value.Trim();
        }

        return kept;
    }

    /// <summary>
    /// THE tokeniser both sides of the check run through, so the vocabulary and the candidate
    /// term can never disagree on casing, hyphenation or punctuation.
    /// </summary>
    /// <remarks>
    /// Lower-invariant; every non-alphanumeric character is a separator; tokens of a single
    /// character are ignored on BOTH sides (they carry no steering meaning and admitting them
    /// would neither open nor close anything). Digits are kept and are checked like any other
    /// token — a bare article number is exactly the sort of thing an injected query would carry.
    /// </remarks>
    /// <param name="text">Any label, query, attribute key or catalogue string.</param>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (builder.Length > 1) yield return builder.ToString();
            builder.Clear();
        }

        if (builder.Length > 1) yield return builder.ToString();
    }

    private static void AddAll(HashSet<string> tokens, string? text)
    {
        foreach (var token in Tokenize(text)) tokens.Add(token);
    }

    private static string PathKey(IReadOnlyList<string> path) => string.Join(" > ", path);
}
