// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Shared exact-or-subdomain host allow-list semantics — factored out of <see cref="DomainAllowListGate"/> (a
/// behavior-preserving extraction, not a functional change) so <c>GatekeeperHttpMessageHandler</c> (#10, the
/// real HTTP-egress-enforcement handler) can reuse the IDENTICAL matching rule instead of a second copy that
/// could silently drift out of sync — the argument-level gate and the network-level handler must agree on
/// what "allowed" means, or a caller who thinks they configured one allow-list would actually be running two
/// slightly different ones.
/// </summary>
internal static class HostAllowList
{
    /// <summary>Normalizes every entry (tolerating a bare host, a full URL, or a bracketed IPv6 literal) into a case-insensitive set. Throws if the result is empty (default-deny — at least one real entry is required).</summary>
    public static HashSet<string> Build(IEnumerable<string> allowedDomains, string paramName)
    {
        ArgumentNullException.ThrowIfNull(allowedDomains);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in allowedDomains)
        {
            var host = NormalizeHost(d);
            if (host is not null)
            {
                allowed.Add(host);
            }
        }

        if (allowed.Count == 0)
        {
            throw new ArgumentException("At least one allowed domain is required (default-deny).", paramName);
        }

        return allowed;
    }

    /// <summary>True when <paramref name="host"/> is exactly an allowed entry, or a subdomain of one (<c>example.com</c> allows <c>api.example.com</c>).</summary>
    public static bool IsAllowed(string host, IReadOnlySet<string> allowed)
    {
        if (allowed.Contains(host))
        {
            return true;
        }

        foreach (var a in allowed)
        {
            if (host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tolerates a bare host, a full URL (strips scheme/path), or a bracketed IPv6 literal (<c>[::1]</c>). Returns the lowercase, dot-trimmed host, or null if empty.</summary>
    public static string? NormalizeHost(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var d = domain.Trim();
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

    /// <summary>Extracts the host from an <c>[userinfo@]host[:port]</c> authority (or a bracketed IPv6 literal).</summary>
    public static string? ExtractHost(string authority)
    {
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        if (authority.StartsWith('['))
        {
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
                authority = authority[..colon];
            }
        }

        authority = authority.TrimEnd('.');
        return authority.Length == 0 ? null : authority.ToLowerInvariant();
    }
}
