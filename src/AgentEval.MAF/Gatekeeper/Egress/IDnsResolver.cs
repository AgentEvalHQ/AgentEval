// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;

namespace AgentEval.MAF.Gatekeeper.Egress;

/// <summary>
/// A seam over DNS resolution — <see cref="GatekeeperHttpMessageHandler"/> resolves a request's host through
/// this instead of calling <see cref="System.Net.Dns"/> directly, so a test can script resolution results
/// (including a "resolves to a private IP" DNS-rebind scenario) without needing real network/DNS access. The
/// default (<see cref="SystemDnsResolver"/>) wraps the real system resolver.
/// </summary>
public interface IDnsResolver
{
    /// <summary>Resolves <paramref name="host"/> to every address it currently answers with.</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default);
}
