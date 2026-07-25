// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Shared foundation (F-D, Fable 5 §6/§13): produces the raw text plus its <b>decoded projections</b> so the
/// deterministic content gates (<see cref="ArgumentPatternGate"/>, <see cref="DomainAllowListGate"/>,
/// <see cref="TaintTrackingGate"/>) can match an encoding the downstream tool will later decode — closing the
/// evasion where a percent-, HTML-entity-, unicode-escape-, or base64-encoded payload slips past a scan of the
/// single raw surface. Layers are peeled recursively up to <see cref="DefaultMaxDepth"/> (to catch
/// double-encoding), deduplicated, and each projection is capped at <see cref="DefaultMaxLength"/> — a decode that
/// would expand past the cap is dropped, so a decode bomb cannot turn canonicalization into a DoS.
/// <para>Deterministic and allocation-bounded; safe on the hot path. Decoding is best-effort and conservative: a
/// projection is emitted only when it actually differs from its input, and base64 is decoded only for a long
/// candidate run whose bytes are valid, largely-printable UTF-8 (so random alphanumerics don't manufacture noise
/// that spuriously matches a pattern).</para>
/// </summary>
public static class ArgumentCanonicalizer
{
    /// <summary>Default recursion depth for peeling nested encodings (raw = depth 0).</summary>
    public const int DefaultMaxDepth = 2;

    /// <summary>Default per-projection length cap; a decode expanding past this is dropped (decode-bomb guard).</summary>
    public const int DefaultMaxLength = 64 * 1024;

    /// <summary>Hard cap on the number of projections returned (raw included), bounding fan-out.</summary>
    public const int DefaultMaxProjections = 24;

    private static readonly Regex UnicodeEscape = new(@"\\u([0-9A-Fa-f]{4})", RegexOptions.Compiled, GateRegexTimeouts.Standard);
    private static readonly Regex Base64Candidate = new(@"[A-Za-z0-9+/]{16,}={0,2}", RegexOptions.Compiled, GateRegexTimeouts.Standard);

    /// <summary>
    /// Returns <paramref name="raw"/> and its decoded projections. The first element is always <paramref name="raw"/>.
    /// Never throws for malformed input — a decoder that fails is simply skipped.
    /// </summary>
    public static IReadOnlyList<string> Canonicalize(
        string raw, int maxDepth = DefaultMaxDepth, int maxLength = DefaultMaxLength, int maxProjections = DefaultMaxProjections)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<(string Text, int Depth)>();

        void Add(string s, int depth)
        {
            if (s.Length > maxLength || results.Count >= maxProjections || !seen.Add(s))
            {
                return;
            }

            results.Add(s);
            if (depth < maxDepth)
            {
                frontier.Enqueue((s, depth));
            }
        }

        Add(raw, 0);
        while (frontier.Count > 0 && results.Count < maxProjections)
        {
            var (text, depth) = frontier.Dequeue();
            foreach (var decoded in DecodeOneLayer(text))
            {
                Add(decoded, depth + 1);
            }
        }

        return results;
    }

    private static IEnumerable<string> DecodeOneLayer(string text)
    {
        // Percent-decoding (URL): %2F → '/', %68%74%74%70 → 'http'. Uri.UnescapeDataString is lenient (leaves a
        // malformed % as-is) and never throws.
        if (text.IndexOf('%') >= 0)
        {
            string percent;
            try { percent = Uri.UnescapeDataString(text); }
            catch (UriFormatException) { percent = text; }
            if (!string.Equals(percent, text, StringComparison.Ordinal))
            {
                yield return percent;
            }
        }

        // HTML entities: &#x2f; / &amp; → decoded.
        if (text.IndexOf('&') >= 0)
        {
            var html = WebUtility.HtmlDecode(text);
            if (!string.Equals(html, text, StringComparison.Ordinal))
            {
                yield return html;
            }
        }

        // Literal unicode escapes in the text (/ → '/') — the shape a JSON value carries when the model
        // hides a char as a backslash-u sequence in the argument string itself.
        if (text.Contains("\\u", StringComparison.Ordinal))
        {
            string unescaped;
            try
            {
                unescaped = UnicodeEscape.Replace(text, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            }
            catch (RegexMatchTimeoutException)
            {
                unescaped = text;
            }

            if (!string.Equals(unescaped, text, StringComparison.Ordinal))
            {
                yield return unescaped;
            }
        }

        // Base64-candidate runs decoded to printable UTF-8 text.
        foreach (var decoded in DecodeBase64Candidates(text))
        {
            yield return decoded;
        }
    }

    private static IEnumerable<string> DecodeBase64Candidates(string text)
    {
        MatchCollection matches;
        try
        {
            matches = Base64Candidate.Matches(text);
        }
        catch (RegexMatchTimeoutException)
        {
            yield break;
        }

        foreach (Match match in matches)
        {
            var candidate = match.Value;
            if (candidate.Length % 4 != 0)
            {
                continue;   // real base64 is a multiple of 4 (with padding) — skip arbitrary alphanumerics
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(candidate);
            }
            catch (FormatException)
            {
                continue;
            }

            if (bytes.Length == 0)
            {
                continue;
            }

            string text8;
            try
            {
                text8 = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                continue;   // not valid UTF-8 → almost certainly not a smuggled text payload
            }

            // Require mostly-printable so decoded binary doesn't manufacture pattern-matching noise.
            if (IsMostlyPrintable(text8))
            {
                yield return text8;
            }
        }
    }

    private static bool IsMostlyPrintable(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        var printable = 0;
        foreach (var c in s)
        {
            if (!char.IsControl(c) || c is '\t' or '\n' or '\r')
            {
                printable++;
            }
        }

        return printable >= s.Length * 0.9;
    }
}
