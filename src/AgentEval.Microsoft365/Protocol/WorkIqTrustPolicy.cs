// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using A2A;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// Decides where a bearer token may go and which card interface is used: the card URI and every selected
/// interface URL must be <c>https</c> on a host in <see cref="TrustedHosts"/>; the selected interface must declare
/// binding <see cref="RequiredBinding"/> and protocol version <see cref="RequiredProtocolVersion"/> (never element
/// zero regardless of binding). An untrusted host is refused before any token use with
/// <see cref="WorkIqFailureCategory.UntrustedHost"/>.
/// </summary>
internal sealed class WorkIqTrustPolicy
{
    /// <summary>The only protocol version this provider speaks.</summary>
    public const string RequiredProtocolVersion = "1.0";

    /// <summary>The only binding this provider uses (Work IQ's documented binding; equals <see cref="ProtocolBindingNames.JsonRpc"/>).</summary>
    public const string RequiredBinding = "JSONRPC";

    /// <summary>Creates the policy from the configured trusted hosts.</summary>
    /// <param name="trustedHosts">Host names a token may be sent to (compared case-insensitively).</param>
    public WorkIqTrustPolicy(IReadOnlyList<string> trustedHosts)
    {
        ArgumentNullException.ThrowIfNull(trustedHosts);
        if (trustedHosts.Count == 0)
        {
            throw new ArgumentException("At least one trusted host is required.", nameof(trustedHosts));
        }

        TrustedHosts = trustedHosts;
    }

    /// <summary>The configured trusted hosts.</summary>
    public IReadOnlyList<string> TrustedHosts { get; }

    /// <summary>Whether a token may be sent to <paramref name="uri"/> (https + trusted host).</summary>
    /// <param name="uri">The destination.</param>
    public bool IsTrusted(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsTrustedHost(uri.Host);
    }

    /// <summary>Whether <paramref name="host"/> is one of <see cref="TrustedHosts"/> (case-insensitive, trimmed).</summary>
    /// <param name="host">The host name.</param>
    public bool IsTrustedHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        foreach (var trusted in TrustedHosts)
        {
            if (!string.IsNullOrWhiteSpace(trusted) && string.Equals(trusted.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Selects the interface of <paramref name="card"/> to use: binding <see cref="RequiredBinding"/>, protocol
    /// version <see cref="RequiredProtocolVersion"/>, trusted host. Throws <see cref="WorkIqProtocolException"/>
    /// (<see cref="WorkIqFailureCategory.ProtocolVersionMismatch"/> or <see cref="WorkIqFailureCategory.UntrustedHost"/>)
    /// when none qualifies. A card that advertises only 0.3 interfaces is a version mismatch, never a downgrade;
    /// a 1.0 JSON-RPC interface on a host outside the policy is refused before any request reaches it.
    /// </summary>
    /// <param name="card">The discovered card.</param>
    /// <param name="cardUri">Where the card was fetched from (for the failure's correlation).</param>
    public AgentInterface SelectInterface(AgentCard card, Uri cardUri)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(cardUri);

        var advertised = card.SupportedInterfaces ?? [];
        var compatible = new List<(AgentInterface Interface, Uri? Url)>();
        foreach (var candidate in advertised)
        {
            if (candidate is null || !IsRequiredBinding(candidate.ProtocolBinding) || !IsRequiredVersion(candidate.ProtocolVersion))
            {
                continue;
            }

            Uri.TryCreate(candidate.Url, UriKind.Absolute, out var url);
            compatible.Add((candidate, url));
        }

        if (compatible.Count == 0)
        {
            throw new WorkIqProtocolException(new WorkIqFailure(
                WorkIqFailureCategory.ProtocolVersionMismatch,
                $"the agent card at {cardUri.Host}{cardUri.AbsolutePath} advertises no {RequiredBinding} interface with protocol version " +
                $"{RequiredProtocolVersion} (advertised: {Describe(advertised)}); this provider speaks 1.0 only and never downgrades",
                OutcomeCertainty.Known,
                WorkIqOperation.Discover));
        }

        foreach (var (candidate, url) in compatible)
        {
            if (url is not null && IsTrusted(url))
            {
                return candidate;
            }
        }

        var hosts = string.Join(", ", compatible.Select(c => c.Url is null ? "(unparsable url)" : $"{c.Url.Scheme}://{c.Url.Host}"));
        throw new WorkIqProtocolException(new WorkIqFailure(
            WorkIqFailureCategory.UntrustedHost,
            $"every {RequiredBinding} {RequiredProtocolVersion} interface of the agent card at {cardUri.Host}{cardUri.AbsolutePath} points outside " +
            $"the trust policy (interface hosts: {hosts}; trusted: {string.Join(", ", TrustedHosts)}); no token was sent there",
            OutcomeCertainty.Known,
            WorkIqOperation.Discover));
    }

    /// <summary>Whether <paramref name="binding"/> is the required JSON-RPC binding (case-insensitive, trimmed).</summary>
    /// <param name="binding">The advertised binding.</param>
    public static bool IsRequiredBinding(string? binding) =>
        !string.IsNullOrWhiteSpace(binding) && string.Equals(binding.Trim(), RequiredBinding, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="version"/> is <c>1.0</c> or a <c>1.0.x</c> patch of it (never <c>0.3</c>, never <c>1.1</c>).</summary>
    /// <param name="version">The advertised protocol version.</param>
    public static bool IsRequiredVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var trimmed = version.Trim();
        return string.Equals(trimmed, RequiredProtocolVersion, StringComparison.Ordinal)
            || trimmed.StartsWith(RequiredProtocolVersion + ".", StringComparison.Ordinal);
    }

    private static string Describe(IEnumerable<AgentInterface?> interfaces)
    {
        var parts = interfaces
            .Where(i => i is not null)
            .Select(i => $"{i!.ProtocolBinding ?? "(no binding)"}/{i.ProtocolVersion ?? "(no version)"}@{HostOf(i.Url)}")
            .ToArray();
        return parts.Length == 0 ? "none" : string.Join(", ", parts);
    }

    private static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(unparsable url)";
}
