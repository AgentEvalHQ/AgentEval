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
        @"(?:[a-z][a-z0-9+.\-]*:)?//([^/\s""'<>\\)}?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private readonly HashSet<string> _allowed;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public GateCost Cost => GateCost.Bounded;

    /// <summary>
    /// Creates the gate from the allowed domains — bare hosts, e.g. <c>"api.example.com"</c> (an IPv6 literal must
    /// be bracketed, e.g. <c>"[::1]"</c>; a bare <c>"::1"</c> is mis-parsed as host:port and ignored). Default-deny:
    /// at least one is required.
    /// </summary>
    public DomainAllowListGate(IEnumerable<string> allowedDomains, string? policyName = null)
    {
        _allowed = HostAllowList.Build(allowedDomains, nameof(allowedDomains));
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
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            // Any serialization failure (unsupported type, reference cycle, bad object graph) ⇒ we cannot inspect
            // the arguments ⇒ fail closed (cannot prove the call has no forbidden egress).
            return Blocked($"tool '{call.FunctionName}' arguments could not be serialized for URL inspection");
        }

        try
        {
            foreach (Match m in UrlAuthority.Matches(serialized))
            {
                var host = HostAllowList.ExtractHost(m.Groups[1].Value);
                if (host is not null && !HostAllowList.IsAllowed(host, _allowed))
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

    private ValueTask<ToolGateVerdict> Allow() => new(ToolGateVerdict.Allow(PolicyName));

    private ValueTask<ToolGateVerdict> Blocked(string reason) => new(ToolGateVerdict.Block(PolicyName, reason));
}
