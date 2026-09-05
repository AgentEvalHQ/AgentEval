// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The lexical leg of the hybrid retriever (design §D.3) — an in-process, IDF-weighted token
/// overlap scorer with an exact boost for model numbers and GTINs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this leg is not optional.</b> Dense retrieval is at its weakest exactly where Galaxus
/// customers are strongest: they type model numbers. <c>α7 IV</c>, <c>A7IV</c> and
/// <c>ILCE-7M4</c> are three surface forms of one product, and a 1536-dimensional vector treats
/// the difference as noise. This leg treats it as the signal.
/// </para>
/// <para>
/// <b>What it stands in for.</b> In production this leg is Galaxus's existing Elasticsearch.
/// The architectural claim is <i>fuse with their search, do not replace it</i> — a stronger
/// claim than "we built a new search", and the reason the fusion step uses RRF, which needs no
/// score calibration between a cosine and a token count.
/// </para>
/// <para>
/// <b>Indexed fields are deliberately narrow:</b> <see cref="Product.Name"/>,
/// <see cref="Product.Brand"/>, and <see cref="Product.Specs"/> keys and values — exactly §D.3's
/// list. Use-context tags are NOT indexed here. That is not an omission: the whole demonstration
/// is that the cross-category link (hiking pack → travel tripod) lives on the <c>Use:</c> line
/// and is invisible to lexical matching. Indexing tags here would blur the one claim the demo
/// exists to make, and would make the lexical baseline look better than the thing it is a
/// baseline for.
/// </para>
/// <para>
/// <b>The ANCHOR rule — a fragment may add score, it may not create a hit.</b>
/// <see cref="ExpandToBag"/> splits a hyphenated token into its parts on BOTH sides, so that
/// <c>16-35</c> also reaches <c>16</c> and <c>35</c>. Without a guard the parts are
/// indistinguishable from real tokens, and two unrelated compounds meet on a fragment neither
/// side ever wrote as a word. MEASURED on B-8's own query — the derived interest label
/// <i>"multi-day trips, starts before sunrise, carried"</i> against the 99-SKU catalogue — the
/// entire lexical leg was four products and every one of them was a fragment collision:
/// <c>GLX-6007</c> (a bike multi-<b>tool</b>) at rank 1 with 10.58 on the single token
/// <c>multi</c>, <c>GLX-9003</c> (a pill organiser, "four per <b>day</b>") at 6.91 on
/// <c>day</c>, and two filter sets on <c>multi</c> from "multi-coating". ALL SIX of the query's
/// own tokens have <c>df = 0</c> in this index — <c>multi-day</c>, <c>trips</c>, <c>starts</c>,
/// <c>before</c>, <c>sunrise</c>, <c>carried</c> — so the only things that matched were the
/// fragments <c>multi</c> (df 3) and <c>day</c> (df 1). Rank 1 in a leg is authority under RRF
/// whatever the score, so a fragment put a Cycling SKU at the top of a photographer's tray, and
/// a Health &amp; Personal Care SKU into her candidate set. That is the false positive §8.1
/// records as B-8 and attributes to the <c>Use:</c> line, which is not where it came from — the
/// bike multi-tool's dense rank on that query was 16th of 99.
/// The same collision put a mudguard at lexical rank 1 for <i>"Mirrorless full-frame"</i>:
/// <c>mirrorless</c> has df 0 and <c>full-frame</c> df 1 (Nadia's own camera), while the
/// fragments <c>full</c> (df 4, "full-length mudguard") and <c>frame</c> (df 3, "aluminium
/// frame") have carriers in three departments.
/// </para>
/// <para>
/// So a product enters the lexical result only when it is ANCHORED: it shares at least one
/// token that NEITHER side had to split to produce, or it took a model-number or GTIN boost.
/// Fragment overlap still contributes its IDF-weighted score to an anchored product — the
/// <c>16</c> of <c>16-35</c> is not thrown away — it simply cannot, alone, admit a product.
/// This is the same defect class as <see cref="StopWords"/> (a token with no discriminating
/// meaning winning on <c>df = 1</c>) and it is fixed in the same place, at match time.
/// </para>
/// </remarks>
public sealed class LexicalIndex
{
    /// <summary>Field weight for tokens found in <see cref="Product.Name"/>.</summary>
    public const float NameFieldWeight = 3.0f;

    /// <summary>Field weight for tokens found in <see cref="Product.Brand"/>.</summary>
    public const float BrandFieldWeight = 3.0f;

    /// <summary>Field weight for tokens found in a spec VALUE ("82 mm", "10 stops").</summary>
    public const float SpecValueFieldWeight = 1.5f;

    /// <summary>Field weight for tokens found in a spec KEY ("Filter thread").</summary>
    public const float SpecKeyFieldWeight = 1.0f;

    /// <summary>Flat bonus when a model-number-shaped query token appears inside the squashed name or specs.</summary>
    public const float ModelNumberBoost = 6.0f;

    /// <summary>Flat bonus when a query token is exactly a product's GTIN. An exact GTIN is an identity, not a hint.</summary>
    public const float GtinExactBoost = 12.0f;

    /// <summary>Shortest squashed model token accepted for the substring boost — below this, noise wins.</summary>
    public const int MinimumModelTokenLength = 3;

    /// <summary>
    /// Closed-class function words that are never indexed. Not a tuning knob — a correctness fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Smoothed IDF over 76 documents REWARDS a function word that lands in a product name.</b>
    /// MEASURED before this set existed: <c>Search("it")</c> returned <c>GLX-9001</c> at
    /// <b>13.03</b> — the highest single-token score anywhere in the corpus — because "IT" appears
    /// in "Omron M7 Intelli IT" and in nothing else, so df = 1 gave it the maximum possible IDF and
    /// the 3.0 name-field weight multiplied it. The only carrier of the corpus's best-scoring token
    /// was the blood-pressure monitor.
    /// </para>
    /// <para>
    /// The consequence was not theoretical. On the design's own headline query
    /// <i>"I want to shoot waterfalls on my hikes"</i> NO content token scored at all — the whole
    /// lexical leg was <c>"to"</c> and <c>"on"</c> — and the fused top-8 put the intended answer
    /// (GLX-1003) at #4 and the blood-pressure cuff at #7. Removing these words alone restores
    /// GLX-1003 to #1. Field weights are NOT the lever here: df = 1 beats any weighting.
    /// </para>
    /// <para>
    /// Applied at INDEX time, in <see cref="AddField"/>. A query token that no document carries
    /// simply scores nothing, so the query side needs no matching list to maintain.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        // articles, conjunctions, and the copula
        "a", "an", "and", "as", "at", "be", "been", "being", "but", "by", "is", "am", "are",
        "or", "so", "than", "that", "the", "then", "there", "these", "this", "those", "was", "were",
        // prepositions and particles
        "for", "from", "in", "into", "of", "off", "on", "onto", "to", "up", "with", "without",
        // pronouns and determiners
        "he", "her", "hers", "him", "his", "i", "it", "its", "me", "mine", "my", "our", "ours",
        "she", "their", "theirs", "them", "they", "us", "we", "what", "when", "which", "who",
        "whom", "whose", "you", "your", "yours",
        // auxiliaries
        "can", "could", "did", "do", "does", "had", "has", "have", "if", "may", "might", "must",
        "shall", "should", "will", "would",
    };

    private readonly IReadOnlyList<Product> _products;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Dictionary<string, float> _inverseDocumentFrequency;

    private LexicalIndex(
        IReadOnlyList<Product> products,
        Dictionary<string, Entry> entries,
        Dictionary<string, float> inverseDocumentFrequency)
    {
        _products = products;
        _entries = entries;
        _inverseDocumentFrequency = inverseDocumentFrequency;
    }

    /// <summary>How many products are indexed.</summary>
    public int Count => _entries.Count;

    /// <summary>How many distinct tokens the index holds.</summary>
    public int VocabularySize => _inverseDocumentFrequency.Count;

    /// <summary>The products this index was built over, in catalogue order.</summary>
    public IReadOnlyList<Product> Products => _products;

    /// <summary>
    /// Builds the index. Pure and synchronous — no model, no key, no network. This is why the
    /// lexical leg is always available and degraded mode still returns something useful.
    /// </summary>
    /// <param name="products">The catalogue.</param>
    public static LexicalIndex Build(IReadOnlyList<Product> products)
    {
        ArgumentNullException.ThrowIfNull(products);

        var entries         = new Dictionary<string, Entry>(products.Count, StringComparer.Ordinal);
        var documentFreq    = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var product in products)
        {
            if (product is null) continue;

            var weights = new Dictionary<string, float>(StringComparer.Ordinal);
            var whole   = new HashSet<string>(StringComparer.Ordinal);

            AddField(weights, product.Name, NameFieldWeight);
            AddField(weights, product.Brand, BrandFieldWeight);
            AddWhole(whole, product.Name);
            AddWhole(whole, product.Brand);

            foreach (var (key, value) in product.Specs)
            {
                AddField(weights, key, SpecKeyFieldWeight);
                AddField(weights, value, SpecValueFieldWeight);
                AddWhole(whole, key);
                AddWhole(whole, value);
            }

            var haystack = new StringBuilder();
            haystack.Append(Squash(product.Name));
            haystack.Append('|');
            haystack.Append(Squash(product.Brand));
            foreach (var (key, value) in product.Specs)
            {
                haystack.Append('|');
                haystack.Append(Squash(key));
                haystack.Append('|');
                haystack.Append(Squash(value));
            }

            entries[product.Id] = new Entry(product, weights, whole, haystack.ToString(), Squash(product.Gtin));

            foreach (var token in weights.Keys)
            {
                documentFreq[token] = documentFreq.TryGetValue(token, out var n) ? n + 1 : 1;
            }
        }

        var total = Math.Max(1, entries.Count);
        var idf   = new Dictionary<string, float>(documentFreq.Count, StringComparer.Ordinal);
        foreach (var (token, df) in documentFreq)
        {
            // Smoothed IDF. It damps a token present in MANY documents ("black", "aluminium") to
            // almost nothing rather than exactly nothing.
            //
            // ⚠ It does the OPPOSITE at the other end, and the earlier comment here claimed
            // otherwise: a token in exactly one document gets the largest weight the formula can
            // produce. That is right for a model number and catastrophic for a function word that
            // happens to sit in one product's name — which is why StopWords exists and why the
            // fix is at index time rather than in these weights.
            idf[token] = (float)Math.Log(1.0 + (double)total / df);
        }

        return new LexicalIndex(products, entries, idf);
    }

    /// <summary>
    /// Scores every product that passes <paramref name="filter"/> and returns the best
    /// <paramref name="topK"/>, best first.
    /// </summary>
    /// <remarks>
    /// The filter is a PRE-filter — applied before the cut, never after — so a constrained query
    /// still gets a full <paramref name="topK"/> of eligible candidates instead of the eligible
    /// remainder of an unconstrained top-k.
    /// </remarks>
    /// <param name="query">Raw query text.</param>
    /// <param name="topK">Maximum results.</param>
    /// <param name="filter">Hard pre-filter over products.</param>
    /// <returns>Product ids with their raw scores. Scores are NOT comparable across queries — RRF only reads the rank.</returns>
    public IReadOnlyList<(string ProductId, float Score)> Search(string query, int topK, Func<Product, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (topK <= 0 || _entries.Count == 0) return [];

        var tokens = Tokenize(query);
        if (tokens.Count == 0) return [];

        var bag         = ExpandToBag(tokens);
        var modelTokens = ModelTokens(tokens);
        var wholeQuery  = new HashSet<string>(tokens, StringComparer.Ordinal);
        var results     = new List<(string ProductId, float Score)>();

        foreach (var product in _products)
        {
            if (product is null) continue;
            if (!_entries.TryGetValue(product.Id, out var entry)) continue;
            if (!filter(product)) continue;

            float score = 0f;

            // A hyphen fragment may ADD score; it may not CREATE a hit. See the ANCHOR remarks
            // on the class: an anchor is a token neither side had to be broken apart to produce.
            bool anchored = entry.WholeTokens.Overlaps(wholeQuery);

            foreach (var token in bag)
            {
                if (!entry.FieldWeightByToken.TryGetValue(token, out var fieldWeight)) continue;
                var weight = _inverseDocumentFrequency.TryGetValue(token, out var idf) ? idf : 1f;
                score += weight * fieldWeight;
            }

            foreach (var modelToken in modelTokens)
            {
                if (modelToken.Length >= MinimumModelTokenLength &&
                    entry.SquashedHaystack.Contains(modelToken, StringComparison.Ordinal))
                {
                    score += ModelNumberBoost;
                    anchored = true;
                }
            }

            foreach (var token in bag)
            {
                if (LooksLikeGtin(token) &&
                    entry.SquashedGtin.Length > 0 &&
                    string.Equals(Squash(token), entry.SquashedGtin, StringComparison.Ordinal))
                {
                    score += GtinExactBoost;
                    anchored = true;
                }
            }

            if (score > 0f && anchored) results.Add((product.Id, score));
        }

        // Deterministic order: score descending, then product id ascending. Without the tie-break
        // two runs of the same query can return different lists, and a demo that is not
        // reproducible cannot be evaluated.
        results.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.ProductId, right.ProductId);
        });

        if (results.Count > topK) results.RemoveRange(topK, results.Count - topK);
        return results;
    }

    /// <summary>
    /// The query tokens that actually matched a product — for explaining a hit in the console
    /// rather than asserting one.
    /// </summary>
    /// <param name="productId">Product to explain.</param>
    /// <param name="query">The query that retrieved it.</param>
    public IReadOnlyList<string> MatchedTokens(string productId, string query)
    {
        if (string.IsNullOrWhiteSpace(productId) || !_entries.TryGetValue(productId, out var entry)) return [];

        var matched = new List<string>();
        foreach (var token in ExpandToBag(Tokenize(query)))
        {
            if (entry.FieldWeightByToken.ContainsKey(token)) matched.Add(token);
        }

        matched.Sort(StringComparer.Ordinal);
        return matched;
    }

    // ── Text processing. Shared with ConceptEmbeddingSource so both legs agree on what a token is. ──

    /// <summary>
    /// THE tokeniser for this project. Folds the text (see <see cref="Fold"/>), then emits
    /// positional tokens of <c>[a-z0-9]</c> plus the inner characters <c>: - . +</c>.
    /// </summary>
    /// <remarks>
    /// Inner <c>-</c> and <c>.</c> are kept so <c>16-35</c>, <c>10-stop</c>, <c>wh-1000xm5</c> and
    /// <c>2.5</c> survive as single tokens — splitting them is precisely how a lexical index loses
    /// the model numbers it exists to catch. Inner <c>:</c> is kept so <c>trip:multi-day</c> stays
    /// one token; <see cref="ExpandToBag"/> adds the suffix separately.
    /// </remarks>
    /// <param name="text">Any text.</param>
    /// <returns>Tokens in order of appearance. Never null.</returns>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var folded = Fold(text);
        var tokens = new List<string>();
        var buffer = new StringBuilder(16);

        void Flush()
        {
            while (buffer.Length > 0 && IsInnerOnly(buffer[^1])) buffer.Length--;
            if (buffer.Length > 0) tokens.Add(buffer.ToString());
            buffer.Clear();
        }

        foreach (var ch in folded)
        {
            if (char.IsAsciiLetterOrDigit(ch)) { buffer.Append(ch); continue; }

            if (IsInnerOnly(ch))
            {
                if (buffer.Length > 0) buffer.Append(ch);
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// Expands a positional token stream into the SET used for overlap scoring: every token, plus
    /// the part after a <c>:</c>, plus the parts of a hyphenated token. Set semantics on purpose —
    /// a query that repeats a word must not out-score one that does not.
    /// </summary>
    /// <param name="tokens">Positional tokens from <see cref="Tokenize"/>.</param>
    public static IReadOnlySet<string> ExpandToBag(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var bag = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (token.Length == 0) continue;
            bag.Add(token);

            var colon = token.IndexOf(':');
            if (colon >= 0 && colon < token.Length - 1) bag.Add(token[(colon + 1)..]);

            if (token.Contains('-', StringComparison.Ordinal))
            {
                foreach (var part in token.Split('-', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Length > 1) bag.Add(part);
                }
            }
        }

        return bag;
    }

    /// <summary>
    /// The model-number-shaped tokens of a query, squashed for substring matching — including
    /// concatenations of adjacent short tokens, so <c>"α7 IV"</c> also produces <c>"a7iv"</c>
    /// and matches a catalogue name written the other way round.
    /// </summary>
    /// <param name="tokens">Positional tokens from <see cref="Tokenize"/>.</param>
    public static IReadOnlyList<string> ModelTokens(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var found = new List<string>();
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        void Consider(string candidate)
        {
            var squashed = Squash(candidate);
            if (squashed.Length >= MinimumModelTokenLength && seen.Add(squashed)) found.Add(squashed);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            if (LooksLikeModelNumber(tokens[i])) Consider(tokens[i]);

            if (i + 1 < tokens.Count &&
                tokens[i].Length <= 4 && tokens[i + 1].Length <= 4 &&
                (LooksLikeModelNumber(tokens[i]) || LooksLikeModelNumber(tokens[i + 1])))
            {
                Consider(tokens[i] + tokens[i + 1]);
            }
        }

        return found;
    }

    /// <summary>
    /// True when a token looks like a model number: it mixes letters and digits, or it is a
    /// hyphenated run containing a digit. <c>"a7iv"</c>, <c>"wh-1000xm5"</c>, <c>"16-35"</c>,
    /// <c>"ilce-7m4"</c> qualify; <c>"tripod"</c> and <c>"2025"</c> do not.
    /// </summary>
    /// <param name="token">A folded token.</param>
    public static bool LooksLikeModelNumber(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 2) return false;

        bool hasLetter = false, hasDigit = false, hasHyphen = false;
        foreach (var ch in token)
        {
            if (char.IsAsciiDigit(ch)) hasDigit = true;
            else if (char.IsAsciiLetter(ch)) hasLetter = true;
            else if (ch == '-') hasHyphen = true;
        }

        if (!hasDigit) return false;
        return hasLetter || hasHyphen;
    }

    /// <summary>True when a token is an 8-, 12-, 13- or 14-digit all-numeric code — a GTIN shape.</summary>
    /// <param name="token">A folded token.</param>
    public static bool LooksLikeGtin(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token.Length is not (8 or 12 or 13 or 14)) return false;

        foreach (var ch in token)
        {
            if (!char.IsAsciiDigit(ch)) return false;
        }
        return true;
    }

    /// <summary>
    /// Lower-cases, decomposes accents away, and maps the handful of non-ASCII characters that
    /// actually appear in this catalogue's brand and model names: <c>α</c> → <c>a</c> (Sony α7),
    /// <c>ø</c> → <c>o</c>, <c>ß</c> → <c>ss</c>, <c>×</c> → <c>x</c>, <c>—</c>/<c>–</c> → space.
    /// </summary>
    /// <param name="text">Any text.</param>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length + 4);

        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;

            var ch = char.ToLowerInvariant(raw);
            switch (ch)
            {
                case 'α': builder.Append('a'); break;
                case 'β': builder.Append('b'); break;
                case 'ø': builder.Append('o'); break;
                case 'ß': builder.Append("ss"); break;
                case '×': builder.Append('x'); break;
                case '—':
                case '–':
                case '‑': builder.Append(' '); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Folds then removes every non-alphanumeric character, so <c>"Sony α7 IV"</c> and
    /// <c>"sony-a7iv"</c> both become <c>"sonya7iv"</c>. This is the form the model-number
    /// substring boost matches against, and the reason three surface forms of one product agree.
    /// </summary>
    /// <param name="text">Any text.</param>
    public static string Squash(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var folded  = Fold(text);
        var builder = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (char.IsAsciiLetterOrDigit(ch)) builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool IsInnerOnly(char ch) => ch is '-' or '.' or ':' or '+';

    private static void AddField(Dictionary<string, float> weights, string? text, float fieldWeight)
    {
        foreach (var token in ExpandToBag(Tokenize(text)))
        {
            // See StopWords: an unindexed function word cannot be scored, whatever its df.
            if (StopWords.Contains(token)) continue;
            weights[token] = weights.TryGetValue(token, out var existing) ? existing + fieldWeight : fieldWeight;
        }
    }

    private static void AddWhole(HashSet<string> whole, string? text)
    {
        foreach (var token in Tokenize(text))
        {
            if (StopWords.Contains(token)) continue;
            whole.Add(token);
        }
    }

    private sealed record Entry(
        Product Product,
        Dictionary<string, float> FieldWeightByToken,
        HashSet<string> WholeTokens,
        string SquashedHaystack,
        string SquashedGtin);
}
