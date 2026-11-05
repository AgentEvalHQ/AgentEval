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

    /// <summary>The only binding this provider uses (Work IQ's documented binding).</summary>
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
    public bool IsTrusted(Uri uri) =>
        throw new NotImplementedException("TODO(M10)");

    /// <summary>
    /// Selects the interface of <paramref name="card"/> to use: binding <see cref="RequiredBinding"/>, protocol
    /// version <see cref="RequiredProtocolVersion"/>, trusted host. Throws <see cref="WorkIqProtocolException"/>
    /// (<see cref="WorkIqFailureCategory.ProtocolVersionMismatch"/> or <see cref="WorkIqFailureCategory.UntrustedHost"/>)
    /// when none qualifies.
    /// </summary>
    /// <param name="card">The discovered card.</param>
    /// <param name="cardUri">Where the card was fetched from (for the failure's correlation).</param>
    public AgentInterface SelectInterface(AgentCard card, Uri cardUri) =>
        throw new NotImplementedException("TODO(M10)");
}
