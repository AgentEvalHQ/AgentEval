// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

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
/// <para><b>Scope / limits.</b> It gates URLs it can extract from the arguments. It does <i>not</i> detect a
/// <b>bare hostname</b> with no <c>"//"</c> (e.g. a tool that takes <c>host</c> and adds the scheme itself) or a
/// <c>data:</c> URI — validate those in the tool, or pair with an <see cref="ArgumentPatternGate"/>. It is a
/// strong egress layer over URL arguments, not a complete network firewall.</para>
/// </summary>
public sealed class DomainAllowListGate : IToolGate
{
    // Relaxed encoder: default JSON escaping would turn a URL's characters into \uXXXX and hide it from the scan.
    private static readonly JsonSerializerOptions ScanOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Capture the authority (userinfo@host:port) after an OPTIONAL scheme + "//" — so http(s), ftp, ws, gopher,
    // AND scheme-relative "//attacker.example" are all caught, not just http(s). Stops at the next delimiter
    // (including ? and #, so a query/fragment isn't folded into the host). Bounded (ReDoS-safe).
    private static readonly Regex UrlAuthority = new(
        @"(?:[a-z][a-z0-9+.\-]*:)?//([^/\s""'<>\\)\]}?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private readonly HashSet<string> _allowed;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost => GateCost.Bounded;

    /// <summary>Creates the gate from the allowed domains (bare hosts, e.g. <c>"api.example.com"</c>). Default-deny: at least one is required.</summary>
    public DomainAllowListGate(IEnumerable<string> allowedDomains, string? policyName = null)
    {
        ArgumentNullException.ThrowIfNull(allowedDomains);
        _allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in allowedDomains)
        {
            var host = NormalizeHost(d);
            if (host is not null)
            {
                _allowed.Add(host);
            }
        }

        if (_allowed.Count == 0)
        {
            throw new ArgumentException("At least one allowed domain is required (the gate is default-deny).", nameof(allowedDomains));
        }

        PolicyName = policyName ?? "DomainAllowListGate";
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
        catch (NotSupportedException)
        {
            // Cannot inspect the arguments ⇒ fail closed (cannot prove the call has no forbidden egress).
            return Blocked($"tool '{call.FunctionName}' arguments could not be serialized for URL inspection");
        }

        try
        {
            foreach (Match m in UrlAuthority.Matches(serialized))
            {
                var host = ExtractHost(m.Groups[1].Value);
                if (host is not null && !IsAllowed(host))
                {
                    return Blocked($"tool '{call.FunctionName}' targets a non-allow-listed domain '{host}'");
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return Blocked($"tool '{call.FunctionName}' arguments could not be scanned for URLs (timeout)");
        }

        return Allow();
    }

    private bool IsAllowed(string host)
    {
        if (_allowed.Contains(host))
        {
            return true;
        }

        foreach (var a in _allowed)
        {
            if (host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase))
            {
                return true;   // subdomain of an allowed host
            }
        }

        return false;
    }

    // authority = [userinfo@]host[:port] → host
    private static string? ExtractHost(string authority)
    {
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        if (authority.StartsWith('['))
        {
            // IPv6 literal [::1] (optionally :port after the ]) → the address inside the brackets.
            var close = authority.IndexOf(']');
            if (close > 1)
            {
                return authority[1..close].ToLowerInvariant();
            }
            // malformed bracket — fall through to general handling
        }
        else
        {
            var colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                authority = authority[..colon];   // strip :port
            }
        }

        authority = authority.TrimEnd('.');   // normalize a trailing dot
        return authority.Length == 0 ? null : authority.ToLowerInvariant();
    }

    private static string? NormalizeHost(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var d = domain.Trim();
        // tolerate a user passing a full URL or a scheme
        var scheme = d.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            d = d[(scheme + 3)..];
        }

        var slash = d.IndexOf('/');
        if (slash >= 0)
        {
            d = d[..slash];
        }

        return ExtractHost(d);
    }

    private ValueTask<ToolGateVerdict> Allow() => new(ToolGateVerdict.Allow(PolicyName));

    private ValueTask<ToolGateVerdict> Blocked(string reason) => new(ToolGateVerdict.Block(PolicyName, reason));
}
