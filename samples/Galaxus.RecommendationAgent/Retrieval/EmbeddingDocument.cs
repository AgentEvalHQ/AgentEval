// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Security.Cryptography;
using System.Text;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The exact per-product embedding document (design §D.1) and its version stamp.
/// Composition matters more than model choice at this catalogue size, so the template is
/// pinned here, in one place, and stamped — nothing else in the project may compose
/// embedding text.
/// </summary>
/// <remarks>
/// <para><b>Five lines, each earning its place:</b></para>
/// <list type="number">
///   <item><b>Name + brand.</b> Short queries are dominated by brand and model tokens; first
///         position keeps them from being diluted by prose.</item>
///   <item><b>Category path.</b> States the taxonomy explicitly, so
///         <c>categoryPathPrefix</c> filtering and the vector neighbourhood agree.</item>
///   <item><b><c>Use:</c> — the whole cross-category trick.</b> Only use-context tags, never
///         category synonyms. This is the line on which the hiking pack and the travel tripod
///         become neighbours. Delete this line and the demo stops working, which is a useful
///         thing to be able to say out loud.</item>
///   <item><b>Six specs.</b> Supplies the constraint vocabulary ("1.1 kg", "82 mm", "10-stop")
///         that multi-constraint queries need.</item>
///   <item><b>Description, trimmed to 320 characters.</b> Long prose swamps the vector.</item>
/// </list>
/// <para>
/// <b><c>compat:</c> tags are deliberately excluded.</b> Compatibility is a hard constraint
/// enforced in code (<see cref="RetrievalQuery.HardFilter"/>); putting it in the vector would
/// only teach the retriever to make it soft.
/// </para>
/// <para>
/// <b>Determinism.</b> Lines are joined with <c>"\n"</c>, never <see cref="Environment.NewLine"/>,
/// because <see cref="HashQuery"/> keys the precomputed cache by the SHA-256 of this text: a
/// CRLF/LF difference between the machine that generated the asset and the machine that reads it
/// would silently miss every cache entry. For the same reason <see cref="Product.Specs"/> is
/// consumed in AUTHORED order (the design's <c>.Take(6)</c>) — the seed author decides which six
/// specs are the key ones, and that choice is part of the template.
/// </para>
/// </remarks>
public static class EmbeddingDocument
{
    /// <summary>
    /// Stamped into every generated embedding asset and validated at load. Bump this whenever
    /// <see cref="ForProduct"/> changes: a stale asset then FAILS LOUDLY instead of silently
    /// retrieving against vectors computed for text that no longer exists.
    /// </summary>
    public const string TemplateVersion = "v1";

    /// <summary>Maximum description characters carried into the document (§D.1).</summary>
    public const int DescriptionCharacterBudget = 320;

    /// <summary>How many specs the <c>Key specs:</c> line carries (§D.1's <c>.Take(6)</c>).</summary>
    public const int KeySpecCount = 6;

    /// <summary>The line separator used inside every document. LF, always — see the remarks.</summary>
    public const string LineSeparator = "\n";

    /// <summary>
    /// Tag prefixes that compose the <c>Use:</c> line. <c>compat:</c> is absent by design.
    /// </summary>
    public static IReadOnlyList<string> UseTagPrefixes { get; } =
    [
        "context:",
        "trip:",
        "weight:",
        "skill:",
    ];

    /// <summary>Index of the <c>Use:</c> line inside <see cref="LinesFor"/>. The load-bearing line.</summary>
    public const int UseLineIndex = 2;

    /// <summary>
    /// Renders the five-line embedding document for a product, exactly as §D.1 specifies.
    /// </summary>
    /// <param name="product">The product to describe.</param>
    /// <returns>The document text; never null, never empty.</returns>
    public static string ForProduct(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return string.Join(LineSeparator, LinesFor(product));
    }

    /// <summary>
    /// The document's individual lines, in order. Exposed so
    /// <see cref="BestMatchingLine"/> can report which one carried a hit — the
    /// <c>matchedOn</c> field of the <c>SearchProductsByMeaning</c> payload.
    /// </summary>
    /// <param name="product">The product to describe.</param>
    public static IReadOnlyList<string> LinesFor(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        return
        [
            $"{product.Name} — {product.Brand}",
            $"Category: {string.Join(" > ", product.CategoryPath)}",
            UseLine(product),
            KeySpecsLine(product),
            Trim(product.Description, DescriptionCharacterBudget),
        ];
    }

    /// <summary>
    /// Line 3 — the cross-category bridge. Use-context tags only, in authored order, comma-separated.
    /// Returns <c>"Use:"</c> with nothing after it when a product carries no use tags, which is itself
    /// informative: such a product cannot participate in a cross-category link.
    /// </summary>
    /// <param name="product">The product.</param>
    public static string UseLine(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return $"Use: {string.Join(", ", UseTags(product))}";
    }

    /// <summary>The use-context tags of a product, in authored order, with <c>compat:</c> excluded.</summary>
    /// <param name="product">The product.</param>
    public static IReadOnlyList<string> UseTags(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var tags = new List<string>(product.Tags.Count);
        foreach (var tag in product.Tags)
        {
            if (IsUseTag(tag)) tags.Add(tag);
        }
        return tags;
    }

    /// <summary>True when a tag carries a <see cref="UseTagPrefixes"/> prefix.</summary>
    /// <param name="tag">Raw tag text, e.g. <c>"trip:multi-day"</c>.</param>
    public static bool IsUseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;

        for (int i = 0; i < UseTagPrefixes.Count; i++)
        {
            if (tag.StartsWith(UseTagPrefixes[i], StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Line 4 — the first <see cref="KeySpecCount"/> specs in authored order, <c>"Key ";" separated"</c>.</summary>
    /// <param name="product">The product.</param>
    public static string KeySpecsLine(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        var parts = new List<string>(KeySpecCount);
        foreach (var (key, value) in product.Specs)
        {
            if (parts.Count == KeySpecCount) break;
            parts.Add($"{key} {value}");
        }
        return $"Key specs: {string.Join("; ", parts)}";
    }

    /// <summary>
    /// The accessory query document behind <c>FindComplements</c> (§C.2): the anchor's identity and
    /// <c>Use:</c> line, plus the caller's extra steer. It deliberately does NOT restate the anchor's
    /// category — complements usually live in a different one, and repeating the category would pull
    /// the neighbourhood back towards more of the same product.
    /// </summary>
    /// <param name="anchor">The product being accessorised.</param>
    /// <param name="need">Optional extra need, e.g. "long-exposure water at dawn".</param>
    public static string ForAccessoryQuery(Product anchor, string? need)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        var lines = new List<string>(4)
        {
            $"Accessories, consumables and companions for a {anchor.Name} — {anchor.Brand}",
            UseLine(anchor),
        };

        var owned = string.Join(", ", anchor.CategoryPath);
        lines.Add($"Owned product category: {owned}");

        if (!string.IsNullOrWhiteSpace(need)) lines.Add($"Need: {need.Trim()}");

        return string.Join(LineSeparator, lines);
    }

    /// <summary>
    /// Normalises free text for cache keying: trim, lower-case (invariant), collapse every run of
    /// whitespace to a single space. Deliberately does NOT strip punctuation — two queries that
    /// differ by a question mark are different queries, and pretending otherwise would let one
    /// cached vector answer for text it was never computed from.
    /// </summary>
    /// <param name="text">Any query or document text.</param>
    public static string NormalizeQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        bool lastWasSpace = false;

        foreach (var ch in text.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                builder.Append(' ');
                continue;
            }

            lastWasSpace = false;
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The precomputed-query-cache key: lower-case hex SHA-256 of the UTF-8 bytes of
    /// <see cref="NormalizeQuery"/>. Content-derived, never a caller-chosen id — a stable
    /// hand-written key is exactly how a cache starts replaying vectors for text that changed.
    /// </summary>
    /// <param name="text">Query or document text.</param>
    public static string HashQuery(string? text)
    {
        var normalized = NormalizeQuery(text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Trims text to a character budget on a word boundary. No ellipsis is appended: the document
    /// is machine-read, and an added character would change the hash for no semantic gain.
    /// </summary>
    /// <param name="text">Source text.</param>
    /// <param name="maxCharacters">Hard budget.</param>
    public static string Trim(string? text, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        var collapsed = CollapseWhitespace(text);
        if (collapsed.Length <= maxCharacters) return collapsed;

        var cut = collapsed.LastIndexOf(' ', maxCharacters - 1);
        if (cut <= 0) cut = maxCharacters;

        return collapsed[..cut].TrimEnd();
    }

    /// <summary>
    /// Picks the document line that best explains why a product matched — the <c>matchedOn</c>
    /// field of §C.2's payload.
    /// </summary>
    /// <remarks>
    /// Scored by how many DISTINCT query tokens a line contains, so a long description cannot win
    /// merely by being long. Ties break toward the <c>Use:</c> line, because that is the line the
    /// cross-category claim rests on and the one worth showing an interviewer. With no overlap at
    /// all, the <c>Use:</c> line is returned when it carries tags, else the category line — never
    /// an empty string, because a blank <c>matchedOn</c> reads as a bug.
    /// </remarks>
    /// <param name="product">The matched product.</param>
    /// <param name="queryTokens">Tokens of the query, from <see cref="LexicalIndex.Tokenize"/>.</param>
    /// <param name="maxCharacters">Budget for the returned line.</param>
    public static string BestMatchingLine(Product product, IReadOnlyCollection<string> queryTokens, int maxCharacters = 120)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(queryTokens);

        var lines = LinesFor(product);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in queryTokens) wanted.Add(token);

        int bestIndex = -1;
        int bestScore = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            int score = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var token in LexicalIndex.Tokenize(lines[i]))
            {
                if (wanted.Contains(token) && seen.Add(token)) score++;
            }

            // Strictly greater keeps earlier lines on a tie; the Use: line then wins the
            // remaining ties explicitly below.
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
            else if (score == bestScore && score > 0 && i == UseLineIndex)
            {
                bestIndex = UseLineIndex;
            }
        }

        if (bestIndex < 0)
        {
            var use = lines[UseLineIndex];
            bestIndex = use.Length > "Use: ".Length ? UseLineIndex : 1;
        }

        return Trim(lines[bestIndex], maxCharacters);
    }

    /// <summary>Collapses every whitespace run (including newlines) to a single space and trims.</summary>
    /// <param name="text">Source text.</param>
    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool lastWasSpace = false;

        foreach (var ch in text.AsSpan().Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                builder.Append(' ');
                continue;
            }

            lastWasSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }
}
