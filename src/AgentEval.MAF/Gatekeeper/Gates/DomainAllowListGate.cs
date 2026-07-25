// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Deterministic tool gate that enforces a <b>domain allow-list over the URLs in a tool call's arguments</b> — a
/// front-line defense against exfiltration (the payoff of most indirect injection: fetch/POST a secret to
/// <c>attacker.example</c>). Any URL whose host is not on the allow-list blocks the call.
/// <para>It extracts every URL authority written with <c>"//"</c> — <c>http(s)</c>, <c>ftp</c>, <c>ws</c>, and
/// <b>scheme-relative</b> <c>//attacker.example</c> — resolving the userinfo trick (<c>https://good.com@evil.com</c>
/// → real host <c>evil.com</c>) and stripping ports. Host matching is case-insensitive and an allow-list entry
/// also matches its subdomains (<c>example.com</c> allows <c>api.example.com</c>). Uses the relaxed JSON encoder
/// so a URL isn't hidden behind escaping, and is <b>fail-closed</b>: arguments that can't be serialized, or a scan
/// that times out, block the call.</para>
/// <para><b>Bare hosts &amp; <c>data:</c> URIs (Fable 5 §14).</b> Pass <paramref name="hostArguments"/> (see the
/// constructor) to also validate named arguments whose VALUE is a <b>bare hostname</b> with no <c>"//"</c> (e.g.
/// a tool that takes <c>host</c> and adds the scheme itself) against the same allow-list. And a <c>data:</c> URI —
/// which has no host and so can never be host-allow-listed — is blocked by default (un-vettable inline/exfil
/// egress). It remains a strong egress layer over arguments, not a complete network firewall.</para>
/// </summary>
public sealed class DomainAllowListGate : IToolGate
{
    // Relaxed encoder: default JSON escaping would turn a URL's characters into \uXXXX and hide it from the scan.
    private static readonly JsonSerializerOptions ScanOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Capture the authority (userinfo@host:port) after an OPTIONAL scheme + "//" — so http(s), ftp, ws, gopher,
    // AND scheme-relative "//attacker.example" are all caught, not just http(s). Stops at the next delimiter
    // (including ? and #, so a query/fragment isn't folded into the host). Bounded (ReDoS-safe).
    private static readonly Regex UrlAuthority = new(
        @"(?:[a-z][a-z0-9+.\-]*:)?//([^/\s""'<>\\)}?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, GateRegexTimeouts.Standard);

    // A data: URI (data:[<mediatype>][;base64],<data>) has no host and so can never be host-allow-listed — in an
    // egress-controlled context it is un-vettable inline/exfil content. Conservative: requires the trailing comma
    // of a real data URI, so the literal word "data:" in prose does not false-positive. Bounded (ReDoS-safe).
    private static readonly Regex DataUri = new(
        @"\bdata:(?:[a-z][a-z0-9.+\-]*/[a-z0-9.+\-]+)?(?:;[a-z0-9\-]+(?:=[a-z0-9.\-]+)?)*,",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, GateRegexTimeouts.Standard);

    private readonly HashSet<string> _allowed;
    private readonly string[] _hostArguments;
    private readonly bool _canonicalize;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost => GateCost.Bounded;

    /// <summary>
    /// Creates the gate from the allowed domains — bare hosts, e.g. <c>"api.example.com"</c> (an IPv6 literal must
    /// be bracketed, e.g. <c>"[::1]"</c>; a bare <c>"::1"</c> is mis-parsed as host:port and ignored). Default-deny:
    /// at least one is required. Pass <paramref name="hostArguments"/> to also validate the VALUE of the named
    /// arguments as a bare hostname (no <c>"//"</c>) against the same allow-list — for a tool whose parameter is a
    /// host rather than a full URL. Set <paramref name="canonicalize"/> to also scan decoded projections
    /// (percent / HTML-entity / unicode-escape / base64) of the arguments via <see cref="ArgumentCanonicalizer"/>,
    /// so an encoded URL the tool later decodes cannot evade the allow-list (Fable 5 §13).
    /// </summary>
    public DomainAllowListGate(
        IEnumerable<string> allowedDomains, string? policyName = null, IEnumerable<string>? hostArguments = null, bool canonicalize = false)
    {
        _allowed = HostAllowList.Build(allowedDomains, nameof(allowedDomains));
        PolicyName = policyName ?? "DomainAllowListGate";
        _hostArguments = hostArguments?.Where(a => !string.IsNullOrEmpty(a)).ToArray() ?? Array.Empty<string>();
        _canonicalize = canonicalize;
    }

    /// <inheritdoc/>
    public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Arguments is null || call.Arguments.Count == 0)
        {
            return Allow();   // no arguments ⇒ no URL to check
        }

        string serialized;
        try
        {
            serialized = JsonSerializer.Serialize(call.Arguments, ScanOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            // Any serialization failure (unsupported type, reference cycle, bad object graph) ⇒ we cannot inspect
            // the arguments ⇒ fail closed (cannot prove the call has no forbidden egress).
            return Blocked($"tool '{call.FunctionName}' arguments could not be serialized for URL inspection");
        }

        // Default: scan the raw serialized surface (the first projection). With canonicalize: also scan decoded
        // projections so an encoded URL the tool later decodes cannot evade the allow-list.
        var surfaces = _canonicalize
            ? ArgumentCanonicalizer.Canonicalize(serialized)
            : (IReadOnlyList<string>)new[] { serialized };
        try
        {
            foreach (var surface in surfaces)
            {
                if (DataUri.IsMatch(surface))
                {
                    return Blocked($"tool '{call.FunctionName}' targets a data: URI, which has no host and cannot be domain-allow-listed");
                }

                foreach (Match m in UrlAuthority.Matches(surface))
                {
                    var host = HostAllowList.ExtractHost(m.Groups[1].Value);
                    if (host is not null && !HostAllowList.IsAllowed(host, _allowed))
                    {
                        return Blocked($"tool '{call.FunctionName}' targets a non-allow-listed domain '{host}'");
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Blocked($"tool '{call.FunctionName}' arguments could not be scanned for URLs (timeout)");
        }

        // Bare-hostname arguments (opt-in): the "//" scan above misses a host written without a scheme, so
        // validate the VALUE of each caller-declared host argument directly against the same allow-list.
        foreach (var argName in _hostArguments)
        {
            if (!call.Arguments.TryGetValue(argName, out var value) || value is null)
            {
                continue;
            }

            var host = HostFromArgumentValue(value.ToString());
            if (host is not null && !HostAllowList.IsAllowed(host, _allowed))
            {
                return Blocked($"tool '{call.FunctionName}' argument '{argName}' targets a non-allow-listed host '{host}'");
            }
        }

        return Allow();
    }

    // Extract a host from a host-argument value that may be a bare host, host:port, or a full URL. Strips an
    // optional scheme "//", then path/query/fragment, delegating the userinfo@host:port / IPv6 parse to
    // HostAllowList.ExtractHost so that handling stays in one place.
    private static string? HostFromArgumentValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var authority = raw.Trim();
        var slashSlash = authority.IndexOf("//", StringComparison.Ordinal);
        if (slashSlash >= 0)
        {
            authority = authority[(slashSlash + 2)..];
        }

        var cut = authority.IndexOfAny(new[] { '/', '?', '#', '\\' });
        if (cut >= 0)
        {
            authority = authority[..cut];
        }

        return HostAllowList.ExtractHost(authority);
    }

    private ValueTask<ToolGateVerdict> Allow() => new(ToolGateVerdict.Allow(PolicyName));

    private ValueTask<ToolGateVerdict> Blocked(string reason) => new(ToolGateVerdict.Block(PolicyName, reason));
}
