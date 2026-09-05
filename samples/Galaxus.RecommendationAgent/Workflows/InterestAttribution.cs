// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// Decides whether a retrieved product is <b>attributable</b> to the interest it was credited to —
/// that is, whether the product carries anything the interest actually names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists: the coverage gate was keying on the wrong signal.</b> A candidate used to
/// count toward covering an interest when the retriever's fused score cleared
/// <see cref="DiscoveryState.MinCandidateScore"/> — a floor on a ranking statistic, which the
/// constant's own comment already called UNMEASURED. A ranking score says a product was the best of
/// what the index returned for that query. It does not say the product has anything to do with the
/// interest, and for a query carrying no information it cannot: something is always top of the list.
/// </para>
/// <para>
/// <b>MEASURED, 2026-09-05 (MEASUREMENT_STATUS §22, HybridRetriever.DefaultDenseScoreFloor).</b>
/// Deriving the dense floor per space moved the real-vectors floor DOWN, from a transported 0.280
/// to 0.223. On Luca Ferrari (<c>USR-LF-04</c>) — one purchase, zero independent signals, and the
/// contentless utterance <i>"Hi — what do you recommend for me?"</i> — that turned 0 candidates and
/// <c>GAPS_UNRESOLVABLE</c> into 2 candidates, a second discovery round and five recommendations,
/// two of them espresso accessories credited to an <i>"Over-ear wireless"</i> interest.
/// </para>
/// <para>
/// ⚠ <b>The threshold is not the fix and must not be moved.</b> It was derived on a named held-out
/// split; re-tuning it so one persona comes out right would be fitting a calibrated number to a
/// result, and it would leave the same gate keying on the same wrong quantity for every other
/// persona. What changed is the QUESTION the gate asks: not "did the retriever score this well?"
/// but "does this product carry anything the interest names?".
/// </para>
/// <para>
/// <b>Every input is corpus fact or the interest's own declaration.</b> The product side is the
/// catalogue's title, category path and attribute tokens; the interest side is the map's
/// <see cref="Interest.AttributeHints"/>, <see cref="Interest.CategoryHints"/>,
/// <see cref="Interest.QueryTerms"/> and <see cref="Interest.Label"/>. Nothing here reads a
/// retrieval score, and neither side is supplied by the retriever whose output is being screened.
/// </para>
/// <para>
/// <b>It is deliberately lenient in the accepting direction.</b> Any one of the three signals is
/// enough, and the word test matches on substrings so that "espresso" reaches "espresso machine
/// descaler". The failure this guards against is a product with NO connection at all being counted
/// as coverage; a narrow test would start dropping true matches, which fails in the direction that
/// makes the loop look worse than it is.
/// </para>
/// </remarks>
public static class InterestAttribution
{
    /// <summary>Shortest word that can carry attribution. Below this, words match everything.</summary>
    public const int MinimumWordLength = 4;

    /// <summary>
    /// Words that are long enough to pass <see cref="MinimumWordLength"/> and still carry no
    /// subject matter. Kept short on purpose: a long list is a place to hide a tuning decision.
    /// </summary>
    public static IReadOnlySet<string> Stopwords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "with", "from", "that", "this", "your", "have", "been", "were", "what", "when", "where",
        "them", "they", "into", "only", "very", "just", "like", "more", "most", "some", "also",
        "than", "then", "each", "both", "best", "good", "great", "quality", "product",
        "products", "recommend", "recommendation", "recommendations", "customer", "buy", "shop",
    };

    /// <summary>
    /// True when <paramref name="product"/> carries something <paramref name="interest"/> names.
    /// </summary>
    /// <param name="catalogue">The catalogue façade, for the product's resolved attribute tokens.</param>
    /// <param name="interest">The interest the candidate was credited to.</param>
    /// <param name="product">The retrieved product.</param>
    /// <param name="why">The signal that matched, or the reason nothing did. Never empty.</param>
    public static bool IsAttributable(Catalogue catalogue, Interest interest, Product product, out string why)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(interest);
        ArgumentNullException.ThrowIfNull(product);

        var tokens = catalogue.AttributesOf(product);

        // ── 1. An attribute hint the product actually carries. The strongest signal, and the
        //       same matching rule the hard filter in CatalogueDiscoverySearch already uses. ──
        foreach (var (key, value) in interest.AttributeHints)
        {
            string k = Product.NormalizeAttributeToken(key);
            string v = Product.NormalizeAttributeToken(value);
            if (k.Length == 0 || v.Length == 0) continue;

            if (tokens.Contains($"{k}={v}") || tokens.Contains(v))
            {
                why = $"attribute {k}={v}";
                return true;
            }
        }

        // ── 2. A category hint that meets the product's own path. ──
        foreach (string hint in interest.CategoryHints)
        {
            string h = Fold(hint);
            if (h.Length == 0) continue;

            foreach (string segment in product.CategoryPath)
            {
                string s = Fold(segment);
                if (s.Length == 0) continue;
                if (s.Contains(h, StringComparison.Ordinal) || h.Contains(s, StringComparison.Ordinal))
                {
                    why = $"category '{segment}' meets hint '{hint}'";
                    return true;
                }
            }
        }

        // ── 3. A content word from the interest's own terms, in the product's own text. ──
        var haystack = Fold(string.Join(
            ' ',
            [product.Name, product.Brand, .. product.CategoryPath, .. tokens]))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (string word in Vocabulary(interest))
        {
            foreach (string candidate in haystack)
            {
                if (!WordsMeet(word, candidate)) continue;
                why = $"'{word}' meets '{candidate}' in the product's title, category or attributes";
                return true;
            }
        }

        why = "nothing the interest names appears on this product";
        return false;
    }

    /// <summary>
    /// The content words an interest can be recognised by — from its query terms and its label.
    /// </summary>
    /// <remarks>
    /// An interest that produces an EMPTY vocabulary cannot be attributed to anything, and the
    /// caller records that rather than falling back to counting candidates. Falling back would be
    /// the flattering direction: an interest nobody can describe would become an interest
    /// everything covers.
    /// </remarks>
    /// <param name="interest">The interest.</param>
    public static IReadOnlyList<string> Vocabulary(Interest interest)
    {
        ArgumentNullException.ThrowIfNull(interest);

        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // ⚠ OUR OWN LABEL PREFIX IS NOT THE CUSTOMER'S WORDS. A session-request interest is
        //   labelled "stated this session: <what the customer typed>", and leaving that prefix in
        //   put "stated" and "session" into the attribution vocabulary — so a product whose text
        //   happened to contain "session" would have been counted as covering the request. That is
        //   the harness supplying an input to its own gate. MEASURED: Luca's contentless
        //   "Hi — what do you recommend for me?" produced a vocabulary of exactly [stated, session]
        //   and NOTHING of the customer's.
        foreach (string phrase in interest.QueryTerms.Append(
                     interest.Label.StartsWith(DiscoveryInterestMapping.SessionRequestLabelPrefix, StringComparison.Ordinal)
                         ? interest.Label[DiscoveryInterestMapping.SessionRequestLabelPrefix.Length..]
                         : interest.Label))
        {
            foreach (string raw in Fold(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string word = raw.Trim();
                if (word.Length < MinimumWordLength) continue;
                if (Stopwords.Contains(word)) continue;
                if (seen.Add(word)) words.Add(word);
            }
        }

        return words;
    }

    /// <summary>
    /// True when two folded words are the same word: whole tokens, one a PREFIX of the other,
    /// both at least <see cref="MinimumWordLength"/> long.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Whole tokens, not substrings, and the prefix relation runs BOTH ways.</b> A bare
    /// substring test lets "over" (from "Over-ear wireless") match "cover", which is how a lenient
    /// screen quietly stops screening. A one-directional word-start test was worse in the other
    /// direction and it was MEASURED: the interest "Headlamps" scored 0 attributable candidates out
    /// of 6 on USR-NB-01, because every product is called a "Headlamp" and the plural is one
    /// character longer than the singular. Both-ways prefix matching costs "five"/"fiver" and buys
    /// every plural, every "grinder"/"grinders", every "machine"/"machines".
    /// </para>
    /// <para>
    /// It is not a stemmer. A stemmer is a language-specific asset this corpus does not have — it
    /// runs in German, French and Italian as well as English — and a wrong stem fails silently in
    /// the accepting direction.
    /// </para>
    /// </remarks>
    /// <param name="a">A folded word.</param>
    /// <param name="b">A folded word.</param>
    public static bool WordsMeet(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length < MinimumWordLength || b.Length < MinimumWordLength) return false;
        return a.Length <= b.Length
            ? b.StartsWith(a, StringComparison.Ordinal)
            : a.StartsWith(b, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lower-cases and turns every non-alphanumeric character into a space, so
    /// <c>"context:latte-art"</c> and <c>"latte art"</c> meet.
    /// </summary>
    /// <param name="text">Any text.</param>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text.ToLower(CultureInfo.InvariantCulture))
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
