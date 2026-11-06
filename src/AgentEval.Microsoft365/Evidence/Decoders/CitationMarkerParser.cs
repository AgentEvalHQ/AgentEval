// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using static AgentEval.Microsoft365.Evidence.Decoders.EvidenceJsonReader;

namespace AgentEval.Microsoft365.Evidence.Decoders;

/// <summary>
/// Parses citation markers out of the raw answer text: the Work IQ Unicode block
/// <c>U+E200 cite (U+E202 turn{X}search{Y})+ U+E201</c> (every compound reference kept with both its turn and
/// search components, UTF-16 offsets recorded) and markdown numeric anchors (<c>[n]</c>, flagged
/// <see cref="MarkdownNumericFormat"/> and reported as a heuristic issue). Parsing is a
/// <see cref="EvidenceDerivation.ParserProjection"/>; it never alters the raw text and never binds a marker to a
/// source. The scanner is hand-written (no regex, no timeout, ASCII digits only) so it is deterministic and
/// culture-invariant.
/// </summary>
internal static class CitationMarkerParser
{
    /// <summary>The private-use character that opens a citation block.</summary>
    public const char BlockStart = '';

    /// <summary>The private-use character that closes a citation block.</summary>
    public const char BlockEnd = '';

    /// <summary>The private-use character that precedes each <c>turn{X}search{Y}</c> reference inside a block.</summary>
    public const char ReferenceStart = '';

    /// <summary>The keyword that follows <see cref="BlockStart"/>.</summary>
    public const string BlockKeyword = "cite";

    /// <summary>The marker format of a Work IQ Unicode block.</summary>
    public const string UnicodeCiteFormat = "unicode-cite/v1";

    /// <summary>The marker format of a markdown numeric anchor (heuristic).</summary>
    public const string MarkdownNumericFormat = "markdown-numeric/v1";

    /// <summary>The rule id of the display projection: unresolved Unicode markers are removed, resolved ones render as <c> [n]</c>.</summary>
    public const string DisplayRuleId = "display.strip-unresolved-markers/v1";

    /// <summary>Issue: a block opened with U+E200 did not match the grammar and was left in the raw text unparsed.</summary>
    public const string MalformedBlockIssueCode = "citations.marker_block_malformed";

    /// <summary>Issue: markdown numeric anchors were parsed heuristically; they are not Work IQ markers.</summary>
    public const string MarkdownIssueCode = "citations.markdown_numeric_markers";

    private const string TurnKeyword = "turn";
    private const string SearchKeyword = "search";

    /// <summary>Parses the markers of one raw answer text.</summary>
    /// <param name="rawText">The answer text with its original markers.</param>
    /// <param name="answerSource">The source pointer of the answer text.</param>
    public static EvidenceSet<CitationMarker> Parse(string rawText, SourceRef answerSource)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentNullException.ThrowIfNull(answerSource);

        var markerSource = answerSource with { Origin = EvidenceOrigin.TextMarker };
        var found = new List<(int Start, int Length, string Format, ImmutableArray<string> Keys)>();
        var malformed = 0;

        for (var i = 0; i < rawText.Length;)
        {
            var c = rawText[i];
            if (c == BlockStart)
            {
                if (TryParseBlock(rawText, i, out var end, out var keys))
                {
                    found.Add((i, end - i, UnicodeCiteFormat, keys));
                    i = end;
                }
                else
                {
                    malformed++;
                    i++;
                }

                continue;
            }

            if (c == '[' && TryParseMarkdownAnchor(rawText, i, out var anchorEnd, out var number))
            {
                found.Add((i, anchorEnd - i, MarkdownNumericFormat, ImmutableArray.Create(number)));
                i = anchorEnd;
                continue;
            }

            i++;
        }

        var items = ImmutableArray.CreateBuilder<CitationMarker>(found.Count);
        var markdownCount = 0;
        for (var n = 0; n < found.Count; n++)
        {
            var (start, length, format, keys) = found[n];
            if (string.Equals(format, MarkdownNumericFormat, StringComparison.Ordinal))
            {
                markdownCount++;
            }

            items.Add(new CitationMarker(
                ObservationId("citation-marker", n),
                format,
                rawText.Substring(start, length),
                keys,
                start,
                length,
                One(markerSource)));
        }

        var issues = new List<EvidenceIssue>();
        if (malformed > 0)
        {
            issues.Add(Issue(
                MalformedBlockIssueCode,
                FormattableString.Invariant($"{malformed} citation block(s) opened with U+E200 did not match cite(turn{{X}}search{{Y}})+ and were left unparsed in the raw text."),
                markerSource));
        }

        if (markdownCount > 0)
        {
            issues.Add(Issue(
                MarkdownIssueCode,
                FormattableString.Invariant($"{markdownCount} markdown numeric anchor(s) [n] were parsed heuristically; they are not Work IQ citation markers and bind to nothing."),
                markerSource));
        }

        var validation = malformed == 0
            ? EvidenceValidation.Valid
            : found.Count == 0 ? EvidenceValidation.Malformed : EvidenceValidation.Partial;

        return EvidenceSet<CitationMarker>.Provided(
            items.MoveToImmutable(),
            validation,
            EvidenceCoverage.Unknown,
            EvidenceScope.Response,
            One(answerSource),
            issues.ToImmutableArray());
    }

    /// <summary>
    /// Renders the display projection of <paramref name="rawText"/>: every Unicode marker is replaced by
    /// <c> [n]</c> for each ordinal <paramref name="resolveOrdinals"/> returns for it, or removed when it returns
    /// none; markdown anchors and all other text are left untouched. The raw text is never modified.
    /// </summary>
    /// <param name="rawText">The raw answer text the markers were parsed from.</param>
    /// <param name="markers">The markers of <paramref name="rawText"/> (any order).</param>
    /// <param name="resolveOrdinals">The 1-based source ordinals a marker resolved to (empty = unresolved).</param>
    public static string RenderDisplayText(string rawText, ImmutableArray<CitationMarker> markers, Func<CitationMarker, IReadOnlyList<int>> resolveOrdinals)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ArgumentNullException.ThrowIfNull(resolveOrdinals);

        var ordered = markers.IsDefaultOrEmpty
            ? Array.Empty<CitationMarker>()
            : markers.Where(m => string.Equals(m.MarkerFormat, UnicodeCiteFormat, StringComparison.Ordinal)).OrderBy(m => m.Utf16Start).ToArray();

        var builder = new StringBuilder(rawText.Length);
        var cursor = 0;
        foreach (var marker in ordered)
        {
            if (marker.Utf16Start < cursor || marker.Utf16Start + marker.Utf16Length > rawText.Length)
            {
                continue;
            }

            builder.Append(rawText, cursor, marker.Utf16Start - cursor);
            foreach (var ordinal in resolveOrdinals(marker))
            {
                builder.Append(" [").Append(ordinal.ToString(CultureInfo.InvariantCulture)).Append(']');
            }

            cursor = marker.Utf16Start + marker.Utf16Length;
        }

        builder.Append(rawText, cursor, rawText.Length - cursor);
        return builder.ToString();
    }

    private static bool TryParseBlock(string text, int start, out int end, out ImmutableArray<string> keys)
    {
        end = start;
        keys = default;
        var i = start + 1;
        if (!Matches(text, i, BlockKeyword))
        {
            return false;
        }

        i += BlockKeyword.Length;
        var builder = ImmutableArray.CreateBuilder<string>();
        while (i < text.Length && text[i] == ReferenceStart)
        {
            i++;
            var keyStart = i;
            if (!Matches(text, i, TurnKeyword))
            {
                return false;
            }

            i += TurnKeyword.Length;
            if (ReadAsciiDigits(text, ref i) == 0)
            {
                return false;
            }

            if (!Matches(text, i, SearchKeyword))
            {
                return false;
            }

            i += SearchKeyword.Length;
            if (ReadAsciiDigits(text, ref i) == 0)
            {
                return false;
            }

            builder.Add(text[keyStart..i]);
        }

        if (builder.Count == 0 || i >= text.Length || text[i] != BlockEnd)
        {
            return false;
        }

        end = i + 1;
        keys = builder.ToImmutable();
        return true;
    }

    private static bool TryParseMarkdownAnchor(string text, int start, out int end, out string number)
    {
        end = start;
        number = "";
        var i = start + 1;
        if (ReadAsciiDigits(text, ref i) == 0 || i >= text.Length || text[i] != ']')
        {
            return false;
        }

        number = text[(start + 1)..i];
        end = i + 1;
        return true;
    }

    private static bool Matches(string text, int at, string literal) =>
        at >= 0 && at + literal.Length <= text.Length && string.CompareOrdinal(text, at, literal, 0, literal.Length) == 0;

    private static int ReadAsciiDigits(string text, ref int i)
    {
        var count = 0;
        while (i < text.Length && text[i] is >= '0' and <= '9')
        {
            i++;
            count++;
        }

        return count;
    }
}
