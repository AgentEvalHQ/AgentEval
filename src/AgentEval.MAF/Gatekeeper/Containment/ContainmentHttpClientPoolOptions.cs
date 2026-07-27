// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Construction-time settings for <see cref="ContainmentHttpClientPool"/>.</summary>
public sealed class ContainmentHttpClientPoolOptions
{
    /// <summary>Maximum simultaneous normal requests and default connections per server. Defaults to 32.</summary>
    public int NormalMaxConcurrency { get; set; } = 32;

    /// <summary>
    /// Maximum simultaneous isolated requests and default connections per server. Defaults to 2 and cannot
    /// exceed <see cref="NormalMaxConcurrency"/>.
    /// </summary>
    public int IsolatedMaxConcurrency { get; set; } = 2;

    /// <summary>Lifetime of a connection in each default socket pool. Defaults to five minutes.</summary>
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Timeout applied independently to both clients. Defaults to 100 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>Optional base address applied to both clients.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Optional factory for the normal primary handler. It is called exactly once and the pool owns the returned
    /// handler. When omitted, a dedicated <see cref="SocketsHttpHandler"/> is created.
    /// </summary>
    public Func<HttpMessageHandler>? NormalPrimaryHandlerFactory { get; set; }

    /// <summary>
    /// Optional factory for the isolated primary handler. It is called exactly once and the pool owns the
    /// returned handler. When omitted, a separate dedicated <see cref="SocketsHttpHandler"/> is created.
    /// </summary>
    public Func<HttpMessageHandler>? IsolatedPrimaryHandlerFactory { get; set; }
}
