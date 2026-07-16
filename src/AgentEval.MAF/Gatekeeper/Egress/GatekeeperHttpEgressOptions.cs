// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper.Egress;

/// <summary>Configuration for <see cref="GatekeeperHttpMessageHandler"/>.</summary>
public sealed class GatekeeperHttpEgressOptions
{
    /// <summary>
    /// Maximum number of redirects the handler will follow (re-validating the allow-list AND the
    /// private-network check at every hop). Defaults to 5. A redirect chain longer than this blocks the
    /// request (fail-closed — this is a limit, not best-effort).
    /// </summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>
    /// When <see langword="true"/> (the default), every DNS-resolved address is checked against
    /// <see cref="PrivateNetworkClassifier.IsPrivateOrReserved"/> and the request is blocked if any address is
    /// private/loopback/link-local/reserved — the SSRF / DNS-rebind defense. Set to <see langword="false"/>
    /// only when the allow-list itself deliberately targets an internal service and you accept that risk.
    /// </summary>
    public bool BlockPrivateNetworks { get; set; } = true;

    /// <summary>
    /// Bounded timeout for a single DNS resolution. Defaults to 2 seconds — a slow/hanging resolver must not
    /// hang the request indefinitely; a resolution that exceeds this is treated the same as resolution failure
    /// (fail-closed: cannot prove the destination safe ⇒ block).
    /// </summary>
    public TimeSpan DnsResolutionTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The DNS resolver to use. Defaults to <see cref="SystemDnsResolver.Instance"/> (the real system
    /// resolver); override for testing (script resolution results deterministically, no real network).
    /// </summary>
    public IDnsResolver DnsResolver { get; set; } = SystemDnsResolver.Instance;
}
