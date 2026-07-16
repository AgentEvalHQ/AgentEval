// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;

namespace AgentEval.MAF.Gatekeeper.Egress;

/// <summary>The real, default <see cref="IDnsResolver"/> — a thin wrapper over <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    /// <summary>A shared, stateless instance — <see cref="SystemDnsResolver"/> holds no state, so one instance is safe to reuse everywhere.</summary>
    public static readonly SystemDnsResolver Instance = new();

    /// <inheritdoc/>
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}
